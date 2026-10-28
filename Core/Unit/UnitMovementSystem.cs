using System;
using System.Numerics;
using Core.AI;
using Core.Map;

namespace Core.Unit;

public sealed class UnitMovementSystem
{
    private readonly UnitNavigationSystem _navigation = new UnitNavigationSystem();
    private int _collisionWidth;
    private int _collisionHeight;
    private int[] _collisionCellHeads = Array.Empty<int>();
    private int[] _collisionTouchedCells = Array.Empty<int>();
    private int[] _collisionNextUnit = Array.Empty<int>();
    private int _collisionTouchedCount;
    private float _collisionMaxRadius;
    private readonly int _traceUnitIndex = ReadTraceUnitIndex();
    private float _traceElapsedSeconds;

    public UnitNavigationSystem Navigation => _navigation;

    private static int ReadTraceUnitIndex()
    {
        string? value = Environment.GetEnvironmentVariable("RIMCLONE_AI_TRACE_UNIT");
        return int.TryParse(value, out int unitIndex) && unitIndex >= 0
            ? unitIndex
            : -1;
    }

    private const float ReferenceMassKg = 75f;
    private const float BaseMovementAcceleration = 6.5f;
    private const float BaseBrakingAcceleration = 8.5f;
    private const float BaseTurningAcceleration = 6f;
    private const float ForceResponseTime = 0.32f;
    private const float GroundRollingDeceleration = 0.18f;
    private const float CollisionRestitution = 0.08f;
    private const float CollisionFriction = 0.45f;
    private const float ArrivalRadius = 0.14f;
    private const float ArrivalSpeed = 0.12f;
    private const float MinimumMovingSpeed = 0.035f;
    private const float MaximumMovementDelta = 0.1f;
    private const float FixedMovementStep = 1f / 60f;

    public void Update(
        UnitStore units,
        WorldMap worldMap,
        float deltaTime,
        UnitHealthStore? health = null,
        MeleeCombatSystem? meleeCombat = null,
        UnitAiStore? ai = null,
        UnitInventoryStore? inventory = null)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f || units.ActiveCount == 0)
            return;

        _navigation.BeginUpdate(units, worldMap);
        _traceElapsedSeconds += deltaTime;
        bool traceThisUpdate = _traceUnitIndex >= 0 && _traceElapsedSeconds >= 1f;
        if (traceThisUpdate)
            _traceElapsedSeconds = 0f;

        float movementDelta = MathF.Min(deltaTime, MaximumMovementDelta);
        int substepCount = Math.Clamp(
            (int)MathF.Ceiling(movementDelta / FixedMovementStep), 1, 6);
        float substepDelta = movementDelta / substepCount;

        ReadOnlySpan<int> active = units.ActiveIndices;
        Vector3[] positions = units.Position;
        Vector3[] velocities = units.Velocity;
        Vector3[] targets = units.Target;
        float[] speeds = units.MoveSpeed;
        float[] masses = units.BodyMassKg;
        float[] widths = units.Width;
        float[] lengths = units.Length;
        UnitPosture[] postures = units.Posture;
        bool[] hasTarget = units.HasTarget;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            bool traceUnit = traceThisUpdate && unit == _traceUnitIndex;

            if (health != null && health.OverallHitPoints[unit] <= 0f)
            {
                hasTarget[unit] = false;
                velocities[unit] = Vector3.Zero;
                _navigation.ClearRoute(unit);
                continue;
            }

            // Preserve the existing short combat stagger lock.
            if (meleeCombat?.IsMovementLocked(unit) == true)
            {
                velocities[unit] = Vector3.Zero;
                TraceUnit(traceUnit, "melee-stagger-lock", units, ai, inventory, hasTarget,
                    targets, unit, false, default);
                continue;
            }

            Vector3 position = positions[unit];
            Vector3 target = targets[unit];
            bool steeringActive = hasTarget[unit];
            string? stopReason = null;

            float halfWidth = MathF.Max(0.01f, widths[unit] * 0.5f);
            float halfLength = MathF.Max(0.01f, lengths[unit] * 0.5f);
            float minX = MathF.Min(halfWidth, worldMap.MaxTileX);
            float maxX = MathF.Max(minX, worldMap.MaxTileX - halfWidth);
            float minY = MathF.Min(halfLength, worldMap.MaxTileY);
            float maxY = MathF.Max(minY, worldMap.MaxTileY - halfLength);

            if (steeringActive)
            {
                target.X = Math.Clamp(target.X, minX, maxX);
                target.Y = Math.Clamp(target.Y, minY, maxY);
                targets[unit] = target;

                // A melee pawn starts braking at striking distance. The target
                // command is released, but residual movement is not erased.
                if (ai != null && inventory != null && meleeCombat != null &&
                    MeleeCombatSystem.IsMeleeOnly(inventory, unit) &&
                    ai.HasTarget[unit] &&
                    units.TryGetIndex(ai.Target[unit], out int combatTarget) &&
                    combatTarget != unit &&
                    (health == null || health.OverallHitPoints[combatTarget] > 0f) &&
                    FactionRules.ShouldAttack(units.FactionTag[unit], units.FactionTag[combatTarget]))
                {
                    Vector3 combatDelta = positions[combatTarget] - position;
                    float attackDistance = MeleeCombatSystem.GetAttackDistance(
                        unit, combatTarget, units, inventory);
                    float stopDistance = attackDistance * 0.975f;
                    if (combatDelta.X * combatDelta.X + combatDelta.Y * combatDelta.Y <=
                        stopDistance * stopDistance)
                    {
                        hasTarget[unit] = false;
                        steeringActive = false;
                        stopReason = "melee-contact-range";
                        _navigation.ClearRoute(unit);
                    }
                }
            }

            float moveSpeed = MathF.Max(0f, speeds[unit]);
            if (postures[unit] == UnitPosture.Lying)
                moveSpeed *= 0.45f;

            float mass = MathF.Max(1f, masses[unit]);
            // Motor/traction force grows roughly with muscle cross-section (mass^(2/3)),
            // not linearly with body mass. Heavier bodies therefore accelerate more slowly.
            float massAccelerationScale = MathF.Pow(
                ReferenceMassKg / mass, 1f / 3f);
            float maxAcceleration = BaseMovementAcceleration * massAccelerationScale;
            float maxBraking = BaseBrakingAcceleration * massAccelerationScale;
            float maxTurningAcceleration = BaseTurningAcceleration * massAccelerationScale;
            float maxDriveForce = mass * maxAcceleration;
            float maxBrakeForce = mass * maxBraking;
            float maxLateralForce = mass * maxTurningAcceleration;

            Vector2 steeringPoint = new(position.X, position.Y);
            float distanceToGoal = float.PositiveInfinity;
            float distanceToNextTurn = float.PositiveInfinity;
            float turnSeverity = 0f;
            bool hasWaypoint = false;

            // The route follower is evaluated inside the integration loop so
            // each substep steers from the body's updated position and velocity.
            if (!steeringActive)
                _navigation.ClearRoute(unit);

            Vector2 velocity = new(velocities[unit].X, velocities[unit].Y);

            for (int substep = 0; substep < substepCount; substep++)
            {
                if (steeringActive && moveSpeed > 0f)
                {
                    float speedBeforeSteering = velocity.Length();
                    hasWaypoint = _navigation.TryGetLookAheadWaypoint(
                        units,
                        unit,
                        worldMap,
                        position,
                        target,
                        speedBeforeSteering,
                        out steeringPoint,
                        out distanceToGoal,
                        out distanceToNextTurn,
                        out turnSeverity);
                }
                else
                {
                    hasWaypoint = false;
                }

                Vector2 desiredVelocity = Vector2.Zero;
                if (steeringActive && hasWaypoint && moveSpeed > 0f)
                {
                    Vector2 toSteeringPoint = steeringPoint - new Vector2(position.X, position.Y);
                    float steeringDistanceSquared = toSteeringPoint.LengthSquared();
                    if (steeringDistanceSquared > 0.0025f)
                    {
                        float steeringDistance = MathF.Sqrt(steeringDistanceSquared);
                        float desiredSpeed = moveSpeed;

                        // Final-goal braking is based on remaining route length,
                        // not distance to the temporary look-ahead point.
                        float safeGoalDistance = MathF.Max(0f, distanceToGoal - ArrivalRadius);
                        desiredSpeed = MathF.Min(
                            desiredSpeed,
                            MathF.Sqrt(2f * maxBraking * safeGoalDistance));

                        // A corner is not a waypoint to stop at, but a speed
                        // constraint the body must satisfy before reaching it.
                        // Reserve turning distance from the available braking distance:
                        // v^2 = v_corner^2 + 2 * a_brake * d.
                        if (float.IsFinite(distanceToNextTurn))
                        {
                            float speedAlongRoute = velocity.Length();
                            float cornerSpeed = moveSpeed * (1f - 0.6f * turnSeverity);
                            float turnBuffer = MathF.Max(
                                0.5f,
                                speedAlongRoute * speedAlongRoute /
                                    MathF.Max(1f, maxTurningAcceleration) * turnSeverity);
                            float brakeDistance = MathF.Max(
                                0f, distanceToNextTurn - turnBuffer);
                            float turnSpeedLimit = MathF.Sqrt(
                                cornerSpeed * cornerSpeed + 2f * maxBraking * brakeDistance);
                            desiredSpeed = MathF.Min(desiredSpeed, turnSpeedLimit);
                        }

                        desiredVelocity = toSteeringPoint * (desiredSpeed / steeringDistance);
                    }
                }

                // The route controller requests a force; it never assigns velocity.
                // F = m * deltaVelocity / responseTime, then available traction limits it.
                Vector2 velocityDelta = desiredVelocity - velocity;
                float currentSpeed = velocity.Length();
                float desiredSpeedNow = desiredVelocity.Length();
                Vector2 forward = currentSpeed > 0.05f
                    ? velocity / currentSpeed
                    : (desiredSpeedNow > 0.05f
                        ? desiredVelocity / desiredSpeedNow
                        : Vector2.UnitX);

                Vector2 requestedForce = velocityDelta * (mass / ForceResponseTime);
                float requestedLongitudinalForce = Vector2.Dot(requestedForce, forward);
                float longitudinalForce = Math.Clamp(
                    requestedLongitudinalForce, -maxBrakeForce, maxDriveForce);
                Vector2 lateralForce = requestedForce -
                    forward * requestedLongitudinalForce;
                float lateralForceLength = lateralForce.Length();
                if (lateralForceLength > maxLateralForce && lateralForceLength > 0.0001f)
                    lateralForce *= maxLateralForce / lateralForceLength;

                Vector2 netForce = forward * longitudinalForce + lateralForce;
                Vector2 previousVelocity = velocity;
                Vector2 acceleration = netForce / mass;
                velocity += acceleration * substepDelta;

                // Rolling resistance is an external ground force. Clamp its effect
                // so a discrete step cannot reverse a nearly stopped body.
                float postForceSpeed = velocity.Length();
                if (postForceSpeed > 0.0001f &&
                    Vector2.Dot(velocity, previousVelocity) > 0f)
                {
                    float dragDelta = MathF.Min(
                        GroundRollingDeceleration * substepDelta, postForceSpeed);
                    velocity -= velocity / postForceSpeed * dragDelta;
                }

                if (!steeringActive && velocity.LengthSquared() <=
                    MinimumMovingSpeed * MinimumMovingSpeed)
                {
                    velocity = Vector2.Zero;
                }

                float rawNextX = position.X + velocity.X * substepDelta;
                float rawNextY = position.Y + velocity.Y * substepDelta;
                float nextX = Math.Clamp(rawNextX, minX, maxX);
                float nextY = Math.Clamp(rawNextY, minY, maxY);
                bool hitXBoundary = nextX != rawNextX;
                bool hitYBoundary = nextY != rawNextY;

                Vector2 from = new(position.X, position.Y);
                Vector2 proposed = new(nextX, nextY);
                if ((proposed - from).LengthSquared() > 0.0000001f)
                {
                    if (_navigation.CanMoveDirectly(units, unit, worldMap, from, proposed))
                    {
                        position.X = nextX;
                        position.Y = nextY;
                    }
                    else
                    {
                        // If inertia would cross blocked terrain, slide along any
                        // axis that remains traversable instead of clipping through it.
                        if (MathF.Abs(nextX - position.X) > 0.000001f)
                        {
                            Vector2 xOnly = new(nextX, position.Y);
                            if (_navigation.CanMoveDirectly(units, unit, worldMap, from, xOnly))
                                position.X = nextX;
                            else
                                velocity.X = 0f;
                        }

                        if (MathF.Abs(nextY - position.Y) > 0.000001f)
                        {
                            Vector2 yStart = new(position.X, position.Y);
                            Vector2 yOnly = new(position.X, nextY);
                            if (_navigation.CanMoveDirectly(units, unit, worldMap, yStart, yOnly))
                                position.Y = nextY;
                            else
                                velocity.Y = 0f;
                        }
                    }
                }

                // The map boundary acts as a solid constraint rather than
                // allowing persistent velocity to drive the unit out of bounds.
                if (hitXBoundary)
                    velocity.X = 0f;
                if (hitYBoundary)
                    velocity.Y = 0f;

                // Ground height changes only when the body actually travels across
                // the surface. A stationary unit keeps its authored Z so perception and
                // projectile geometry do not move merely because movement was updated.
                if (MathF.Abs(position.X - from.X) > 0.000001f ||
                    MathF.Abs(position.Y - from.Y) > 0.000001f)
                {
                    position.Z = SampleSurfaceZ(worldMap, position.X, position.Y);
                }
            }

            velocities[unit] = new Vector3(velocity.X, velocity.Y, 0f);
            positions[unit] = position;

            if (hasTarget[unit])
            {
                Vector2 toGoal = new(targets[unit].X - position.X, targets[unit].Y - position.Y);
                if (toGoal.LengthSquared() <= ArrivalRadius * ArrivalRadius &&
                    velocity.LengthSquared() <= ArrivalSpeed * ArrivalSpeed)
                {
                    hasTarget[unit] = false;
                    _navigation.ClearRoute(unit);
                    stopReason = "movement-destination-reached";
                    steeringActive = false;
                }
            }

            string stage = stopReason ??
                (hasTarget[unit]
                    ? (hasWaypoint ? "move-steering" : "waiting-for-route-braking")
                    : (velocity.LengthSquared() > MinimumMovingSpeed * MinimumMovingSpeed
                        ? "braking-without-target"
                        : "no-movement-target"));
            TraceUnit(traceUnit, stage, units, ai, inventory, hasTarget,
                targets, unit, hasWaypoint, steeringPoint);
        }

        ResolveUnitOverlaps(units, worldMap, health);
    }

    private void TraceUnit(
        bool enabled,
        string stage,
        UnitStore units,
        UnitAiStore? ai,
        UnitInventoryStore? inventory,
        bool[] hasMovementTarget,
        Vector3[] movementTargets,
        int unit,
        bool hasWaypoint,
        Vector2 waypoint)
    {
        if (!enabled)
            return;

        Vector3 position = units.Position[unit];
        Vector3 velocity = units.Velocity[unit];
        int trackedTarget = -1;
        bool hasTrackedTarget = ai != null &&
            ai.HasTarget[unit] &&
            units.TryGetIndex(ai.Target[unit], out trackedTarget);

        float targetDistance = float.NaN;
        float attackReach = float.NaN;
        string targetId = "none";
        if (hasTrackedTarget)
        {
            Vector3 targetPosition = units.Position[trackedTarget];
            float dx = targetPosition.X - position.X;
            float dy = targetPosition.Y - position.Y;
            targetDistance = MathF.Sqrt(dx * dx + dy * dy);
            targetId = ai!.Target[unit].ToString();
            if (inventory != null &&
                MeleeCombatSystem.IsMeleeOnly(inventory, unit))
            {
                attackReach = MeleeCombatSystem.GetAttackDistance(
                    unit, trackedTarget, units, inventory);
            }
        }

        bool hasGoal = ai != null && ai.HasGoal[unit];
        Vector3 goal = hasGoal ? ai!.Goal[unit] : Vector3.Zero;
        bool isMeleeOnly = inventory != null && MeleeCombatSystem.IsMeleeOnly(inventory, unit);
        Vector3 movementTarget = hasMovementTarget[unit] ? movementTargets[unit] : Vector3.Zero;
        float movementDistance = hasMovementTarget[unit]
            ? Vector2.Distance(
                new Vector2(position.X, position.Y),
                new Vector2(movementTarget.X, movementTarget.Y))
            : float.NaN;

        Console.WriteLine(
            $"[AI-NAV] unit={units.GetId(unit)} stage={stage} " +
            $"state={(ai == null ? "n/a" : ai.State[unit].ToString())} " +
            $"intent={(ai == null ? "n/a" : ai.LastIntent[unit].ToString())} " +
            $"posture={units.Posture[unit]} meleeOnly={isMeleeOnly} " +
            $"pos=({position.X:0.00},{position.Y:0.00},{position.Z:0.00}) " +
            $"velocity=({velocity.X:0.00},{velocity.Y:0.00}) " +
            $"aiTarget={targetId} aiDist={targetDistance:0.0} meleeReach={attackReach:0.0} " +
            $"moveTarget={hasMovementTarget[unit]}:{(hasMovementTarget[unit] ? movementTarget.ToString() : "-")} " +
            $"moveDist={movementDistance:0.0} goal={hasGoal}:{(hasGoal ? goal.ToString() : "-")} " +
            $"nav={_navigation.GetLastStatus(unit)} " +
            $"waypoint={(hasWaypoint ? waypoint.ToString() : "-")}");
    }

    private void ResolveUnitOverlaps(
        UnitStore units,
        WorldMap worldMap,
        UnitHealthStore? health)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int pass = 0; pass < 3; pass++)
        {
            BuildCollisionGrid(units, worldMap, health);
            if (_collisionTouchedCount == 0)
                return;

            for (int i = 0; i < active.Length; i++)
            {
                int unit = active[i];
                if (health != null && health.OverallHitPoints[unit] <= 0f)
                    continue;

                Vector3 position = units.Position[unit];
                int cellX = Math.Clamp((int)MathF.Floor(position.X), 0, _collisionWidth - 1);
                int cellY = Math.Clamp((int)MathF.Floor(position.Y), 0, _collisionHeight - 1);
                int radius = Math.Clamp(
                    (int)MathF.Ceiling(units.Radius[unit] + _collisionMaxRadius) + 1,
                    1, 6);

                for (int y = Math.Max(0, cellY - radius);
                     y <= Math.Min(_collisionHeight - 1, cellY + radius);
                     y++)
                {
                    for (int x = Math.Max(0, cellX - radius);
                         x <= Math.Min(_collisionWidth - 1, cellX + radius);
                         x++)
                    {
                        int other = _collisionCellHeads[x + y * _collisionWidth];
                        while (other >= 0)
                        {
                            int next = _collisionNextUnit[other];
                            if (other <= unit ||
                                (health != null && health.OverallHitPoints[other] <= 0f))
                            {
                                other = next;
                                continue;
                            }

                            bool hostile = FactionRules.ShouldAttack(
                                units.FactionTag[unit],
                                units.FactionTag[other]);
                            // Moving allies use a softer contact response so crowds can
                            // compress and flow, but never pass through one another.
                            bool movingAllies = !hostile &&
                                (units.HasTarget[unit] || units.HasTarget[other]);

                            Vector3 otherPosition = units.Position[other];
                            // Positions are surface bases. Bodies on vertically separated
                            // floors should not collide merely because their XY footprints overlap.
                            if (otherPosition.Z >= position.Z + units.Height[unit] ||
                                position.Z >= otherPosition.Z + units.Height[other])
                            {
                                other = next;
                                continue;
                            }

                            float dx = otherPosition.X - position.X;
                            float dy = otherPosition.Y - position.Y;
                            float distanceSquared = dx * dx + dy * dy;
                            float minimumDistance = MathF.Max(
                                0.04f,
                                (units.Radius[unit] + units.Radius[other]) *
                                    (movingAllies ? 0.96f : 1.08f));

                            if (distanceSquared >= minimumDistance * minimumDistance)
                            {
                                other = next;
                                continue;
                            }

                            float distance;
                            if (distanceSquared < 0.000001f)
                            {
                                // Stable direction for coincident centers; avoids NaN and jitter.
                                float angle = ((unit * 37 + other * 17) % 360) *
                                    (MathF.PI / 180f);
                                dx = MathF.Cos(angle);
                                dy = MathF.Sin(angle);
                                distance = 0f;
                            }
                            else
                            {
                                distance = MathF.Sqrt(distanceSquared);
                                dx /= distance;
                                dy /= distance;
                            }

                            Vector2 normal = new(dx, dy);
                            float massA = MathF.Max(1f, units.BodyMassKg[unit]);
                            float massB = MathF.Max(1f, units.BodyMassKg[other]);
                            float inverseMassA = 1f / massA;
                            float inverseMassB = 1f / massB;
                            float inverseMassSum = inverseMassA + inverseMassB;

                            // Correct actual overlap, weighted by inverse mass. Keep the
                            // correction on traversable ground so contacts do not push pawns
                            // through walls; if one side is blocked, try moving the other.
                            float correctionDistance =
                                MathF.Max(0f, minimumDistance - distance - 0.005f) *
                                (movingAllies ? 0.55f : 0.8f);
                            Vector3 a = units.Position[unit];
                            Vector3 b = units.Position[other];
                            Vector2 oldA = new(a.X, a.Y);
                            Vector2 oldB = new(b.X, b.Y);
                            Vector2 moveA = -normal *
                                (correctionDistance * inverseMassA / inverseMassSum);
                            Vector2 moveB = normal *
                                (correctionDistance * inverseMassB / inverseMassSum);
                            a.X += moveA.X;
                            a.Y += moveA.Y;
                            b.X += moveB.X;
                            b.Y += moveB.Y;
                            ClampUnitPosition(ref a, units, unit, worldMap);
                            ClampUnitPosition(ref b, units, other, worldMap);

                            bool canMoveA = _navigation.CanMoveDirectly(
                                units, unit, worldMap, oldA, new Vector2(a.X, a.Y));
                            bool canMoveB = _navigation.CanMoveDirectly(
                                units, other, worldMap, oldB, new Vector2(b.X, b.Y));
                            if (canMoveA && canMoveB)
                            {
                                units.Position[unit] = a;
                                units.Position[other] = b;
                                position = a;
                            }
                            else if (canMoveA)
                            {
                                a = units.Position[unit];
                                a.X -= normal.X * correctionDistance;
                                a.Y -= normal.Y * correctionDistance;
                                ClampUnitPosition(ref a, units, unit, worldMap);
                                if (_navigation.CanMoveDirectly(
                                    units, unit, worldMap, oldA, new Vector2(a.X, a.Y)))
                                {
                                    units.Position[unit] = a;
                                    position = a;
                                }
                            }
                            else if (canMoveB)
                            {
                                b = units.Position[other];
                                b.X += normal.X * correctionDistance;
                                b.Y += normal.Y * correctionDistance;
                                ClampUnitPosition(ref b, units, other, worldMap);
                                if (_navigation.CanMoveDirectly(
                                    units, other, worldMap, oldB, new Vector2(b.X, b.Y)))
                                    units.Position[other] = b;
                            }

                            // Impulse-based contact: only approaching bodies receive a
                            // normal impulse. Mass converts the impulse into velocity change.
                            Vector3 storedVelocityA = units.Velocity[unit];
                            Vector3 storedVelocityB = units.Velocity[other];
                            Vector2 velocityA = new(storedVelocityA.X, storedVelocityA.Y);
                            Vector2 velocityB = new(storedVelocityB.X, storedVelocityB.Y);
                            Vector2 relativeVelocity = velocityB - velocityA;
                            float normalVelocity = Vector2.Dot(relativeVelocity, normal);
                            if (normalVelocity < 0f)
                            {
                                float normalImpulseMagnitude =
                                    -(1f + CollisionRestitution) * normalVelocity /
                                    inverseMassSum;
                                if (movingAllies)
                                    normalImpulseMagnitude *= 0.4f;
                                Vector2 normalImpulse = normal * normalImpulseMagnitude;
                                velocityA -= normalImpulse * inverseMassA;
                                velocityB += normalImpulse * inverseMassB;

                                // Coulomb-like tangential friction is capped by the normal impulse.
                                relativeVelocity = velocityB - velocityA;
                                Vector2 tangent = relativeVelocity -
                                    normal * Vector2.Dot(relativeVelocity, normal);
                                float tangentLength = tangent.Length();
                                if (tangentLength > 0.0001f)
                                {
                                    tangent /= tangentLength;
                                    float tangentImpulseMagnitude =
                                        -Vector2.Dot(relativeVelocity, tangent) / inverseMassSum;
                                    float frictionLimit =
                                        CollisionFriction * normalImpulseMagnitude *
                                        (movingAllies ? 0.6f : 1f);
                                    tangentImpulseMagnitude = Math.Clamp(
                                        tangentImpulseMagnitude, -frictionLimit, frictionLimit);
                                    Vector2 tangentImpulse = tangent * tangentImpulseMagnitude;
                                    velocityA -= tangentImpulse * inverseMassA;
                                    velocityB += tangentImpulse * inverseMassB;
                                }

                                units.Velocity[unit] =
                                    new Vector3(velocityA.X, velocityA.Y, 0f);
                                units.Velocity[other] =
                                    new Vector3(velocityB.X, velocityB.Y, 0f);
                            }

                            other = next;
                        }
                    }
                }
            }
        }
    }

    private void BuildCollisionGrid(
        UnitStore units,
        WorldMap worldMap,
        UnitHealthStore? health)
    {
        int width = worldMap.TileWidth;
        int height = worldMap.TileHeight;
        if (_collisionWidth != width || _collisionHeight != height)
        {
            _collisionWidth = width;
            _collisionHeight = height;
            _collisionCellHeads = new int[width * height];
            Array.Fill(_collisionCellHeads, -1);
            _collisionTouchedCells = new int[width * height];
            _collisionTouchedCount = 0;
        }

        if (_collisionNextUnit.Length < units.Capacity)
            Array.Resize(ref _collisionNextUnit, units.Capacity);

        for (int i = 0; i < _collisionTouchedCount; i++)
            _collisionCellHeads[_collisionTouchedCells[i]] = -1;
        _collisionTouchedCount = 0;
        _collisionMaxRadius = 0f;

        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (health != null && health.OverallHitPoints[unit] <= 0f)
                continue;

            _collisionMaxRadius = MathF.Max(_collisionMaxRadius, units.Radius[unit]);
            int x = Math.Clamp((int)MathF.Floor(units.Position[unit].X), 0, width - 1);
            int y = Math.Clamp((int)MathF.Floor(units.Position[unit].Y), 0, height - 1);
            int cell = x + y * width;
            if (_collisionCellHeads[cell] < 0)
                _collisionTouchedCells[_collisionTouchedCount++] = cell;

            _collisionNextUnit[unit] = _collisionCellHeads[cell];
            _collisionCellHeads[cell] = unit;
        }
    }

    private static void ClampUnitPosition(
        ref Vector3 position,
        UnitStore units,
        int unit,
        WorldMap worldMap)
    {
        float halfWidth = MathF.Max(0.01f, units.Width[unit] * 0.5f);
        float halfLength = MathF.Max(0.01f, units.Length[unit] * 0.5f);
        float minX = MathF.Min(halfWidth, worldMap.MaxTileX);
        float maxX = MathF.Max(minX, worldMap.MaxTileX - halfWidth);
        float minY = MathF.Min(halfLength, worldMap.MaxTileY);
        float maxY = MathF.Max(minY, worldMap.MaxTileY - halfLength);

        position.X = Math.Clamp(position.X, minX, maxX);
        position.Y = Math.Clamp(position.Y, minY, maxY);
        position.Z = SampleSurfaceZ(worldMap, position.X, position.Y);
    }

    private static float SampleSurfaceZ(WorldMap worldMap, float x, float y)
    {
        int x0 = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
        int x1 = Math.Min(x0 + 1, worldMap.MaxTileX);
        int y1 = Math.Min(y0 + 1, worldMap.MaxTileY);

        float tx = Math.Clamp(x - x0, 0f, 1f);
        float ty = Math.Clamp(y - y0, 0f, 1f);

        float z00 = worldMap.GetSurfaceHeight(x0, y0);
        float z10 = worldMap.GetSurfaceHeight(x1, y0);
        float z01 = worldMap.GetSurfaceHeight(x0, y1);
        float z11 = worldMap.GetSurfaceHeight(x1, y1);

        float top = z00 + (z10 - z00) * tx;
        float bottom = z01 + (z11 - z01) * tx;
        return top + (bottom - top) * ty;
    }
}
