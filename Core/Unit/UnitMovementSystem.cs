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

    private const float BaseMovementAcceleration = 6.5f;
    private const float BaseBrakingAcceleration = 8.5f;
    private const float BaseTurningAcceleration = 6f;
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
            float massScale = Math.Clamp(MathF.Sqrt(75f / mass), 0.7f, 1.25f);
            float maxAcceleration = BaseMovementAcceleration * massScale;
            float maxBraking = BaseBrakingAcceleration * massScale;
            float maxTurningAcceleration = BaseTurningAcceleration * massScale;

            Vector2 steeringPoint = new(position.X, position.Y);
            float distanceToGoal = float.PositiveInfinity;
            float distanceToNextTurn = float.PositiveInfinity;
            float turnSeverity = 0f;
            bool hasWaypoint = false;

            if (steeringActive)
            {
                Vector3 currentVelocity = velocities[unit];
                float currentSpeed = MathF.Sqrt(
                    currentVelocity.X * currentVelocity.X +
                    currentVelocity.Y * currentVelocity.Y);

                hasWaypoint = _navigation.TryGetLookAheadWaypoint(
                    units,
                    unit,
                    worldMap,
                    position,
                    target,
                    currentSpeed,
                    out steeringPoint,
                    out distanceToGoal,
                    out distanceToNextTurn,
                    out turnSeverity);
            }
            else
            {
                _navigation.ClearRoute(unit);
            }

            Vector2 velocity = new(velocities[unit].X, velocities[unit].Y);

            for (int substep = 0; substep < substepCount; substep++)
            {
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

                        // Reduce speed as a corner approaches. Straight segments
                        // retain their normal speed; sharp turns request a lower
                        // speed before the steering direction begins to swing.
                        if (float.IsFinite(distanceToNextTurn))
                        {
                            float turnRange = MathF.Max(2.5f, moveSpeed * 1.8f);
                            float turnInfluence = Math.Clamp(
                                (turnRange - distanceToNextTurn) / turnRange, 0f, 1f);
                            float turnSpeedRatio = 1f - 0.55f * turnSeverity * turnInfluence;
                            desiredSpeed = MathF.Min(desiredSpeed, moveSpeed * turnSpeedRatio);
                        }

                        desiredVelocity = toSteeringPoint * (desiredSpeed / steeringDistance);
                    }
                }

                Vector2 velocityDelta = desiredVelocity - velocity;
                float deltaLengthSquared = velocityDelta.LengthSquared();
                float currentSpeed = velocity.Length();
                float desiredSpeedNow = desiredVelocity.Length();

                if (deltaLengthSquared > 0.000001f)
                {
                    float directionDot = 1f;
                    if (currentSpeed > 0.001f && desiredSpeedNow > 0.001f)
                    {
                        directionDot = Vector2.Dot(
                            velocity / currentSpeed,
                            desiredVelocity / desiredSpeedNow);
                    }

                    float allowedAcceleration;
                    if (currentSpeed > desiredSpeedNow + 0.05f)
                        allowedAcceleration = maxBraking;
                    else if (directionDot < 0.92f)
                        allowedAcceleration = maxTurningAcceleration;
                    else
                        allowedAcceleration = maxAcceleration;

                    float maxVelocityChange = allowedAcceleration * substepDelta;
                    float deltaLength = MathF.Sqrt(deltaLengthSquared);
                    if (deltaLength <= maxVelocityChange)
                        velocity = desiredVelocity;
                    else
                        velocity += velocityDelta * (maxVelocityChange / deltaLength);
                }
                else if (!steeringActive && currentSpeed <= MinimumMovingSpeed)
                {
                    velocity = Vector2.Zero;
                }

                position.X += velocity.X * substepDelta;
                position.Y += velocity.Y * substepDelta;

                float unclampedX = position.X;
                float unclampedY = position.Y;
                position.X = Math.Clamp(position.X, minX, maxX);
                position.Y = Math.Clamp(position.Y, minY, maxY);

                // The map boundary acts as a solid constraint rather than
                // allowing persistent velocity to drive the unit out of bounds.
                if (position.X != unclampedX)
                    velocity.X = 0f;
                if (position.Y != unclampedY)
                    velocity.Y = 0f;

                // Stage 1 is still ground-only. Vertical free-flight is added
                // after the inertial ground controller has been validated.
                position.Z = SampleSurfaceZ(worldMap, position.X, position.Y);
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
                            // RimWorld-style pawn passage: allies can cross paths while
                            // moving, but settled allies are separated if they overlap.
                            if (!hostile && (units.HasTarget[unit] || units.HasTarget[other]))
                            {
                                other = next;
                                continue;
                            }

                            Vector3 otherPosition = units.Position[other];
                            float dx = otherPosition.X - position.X;
                            float dy = otherPosition.Y - position.Y;
                            float distanceSquared = dx * dx + dy * dy;
                            float minimumDistance = MathF.Max(
                                0.04f,
                                (units.Radius[unit] + units.Radius[other]) * 1.08f);

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

                            float push = (minimumDistance - distance) * 0.51f;
                            Vector3 a = units.Position[unit];
                            Vector3 b = units.Position[other];
                            a.X -= dx * push;
                            a.Y -= dy * push;
                            b.X += dx * push;
                            b.Y += dy * push;

                            ClampUnitPosition(ref a, units, unit, worldMap);
                            ClampUnitPosition(ref b, units, other, worldMap);
                            units.Position[unit] = a;
                            units.Position[other] = b;
                            units.Velocity[unit] = Vector3.Zero;
                            units.Velocity[other] = Vector3.Zero;
                            position = a;
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
