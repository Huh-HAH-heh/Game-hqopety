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

    public void Update(
        UnitStore units,
        WorldMap worldMap,
        float deltaTime,
        UnitHealthStore? health = null,
        MeleeCombatSystem? meleeCombat = null,
        UnitAiStore? ai = null,
        UnitInventoryStore? inventory = null)
    {
        if (deltaTime <= 0f || units.ActiveCount == 0)
            return;

        _navigation.BeginUpdate(units, worldMap);
        _traceElapsedSeconds += deltaTime;
        bool traceThisUpdate = _traceUnitIndex >= 0 && _traceElapsedSeconds >= 1f;
        if (traceThisUpdate)
            _traceElapsedSeconds = 0f;

        ReadOnlySpan<int> active = units.ActiveIndices;
        Vector3[] positions = units.Position;
        Vector3[] velocities = units.Velocity;
        Vector3[] targets = units.Target;
        float[] speeds = units.MoveSpeed;
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

            // Keep the original movement order, but do not move during a landed
            // melee hit's short stagger window. Navigation can resume afterwards.
            if (meleeCombat?.IsMovementLocked(unit) == true)
            {
                velocities[unit] = Vector3.Zero;
                TraceUnit(traceUnit, "melee-stagger-lock", units, ai, inventory, hasTarget,
                    targets, unit, false, default);
                continue;
            }

            // A melee pawn stops when it reaches striking distance even if its
            // cached navigation waypoint still points past the opponent.
            if (ai != null && inventory != null && meleeCombat != null &&
                MeleeCombatSystem.IsMeleeOnly(inventory, unit) &&
                ai.HasTarget[unit] &&
                units.TryGetIndex(ai.Target[unit], out int combatTarget) &&
                combatTarget != unit &&
                (health == null || health.OverallHitPoints[combatTarget] > 0f) &&
                FactionRules.ShouldAttack(units.FactionTag[unit], units.FactionTag[combatTarget]))
            {
                Vector3 combatDelta = positions[combatTarget] - positions[unit];
                float attackDistance = MeleeCombatSystem.GetAttackDistance(
                    unit, combatTarget, units, inventory);
                float stopDistance = attackDistance * 0.975f;

                // Stop at contact distance even if navigation still has a waypoint
                // behind the target. The AI can still assign a different attack slot
                // next think cycle, but movement never pushes into the opponent.
                if (combatDelta.X * combatDelta.X + combatDelta.Y * combatDelta.Y <=
                    stopDistance * stopDistance)
                {
                    hasTarget[unit] = false;
                    velocities[unit] = Vector3.Zero;
                    TraceUnit(traceUnit, "melee-contact-range", units, ai, inventory, hasTarget,
                        targets, unit, false, default);
                    _navigation.ClearRoute(unit);
                    continue;
                }
            }

            if (!hasTarget[unit])
            {
                velocities[unit] = Vector3.Zero;
                TraceUnit(traceUnit, "no-movement-target", units, ai, inventory, hasTarget,
                    targets, unit, false, default);
                _navigation.ClearRoute(unit);
                continue;
            }

            Vector3 position = positions[unit];
            Vector3 target = targets[unit];

            float halfWidth = MathF.Max(0.01f, widths[unit] * 0.5f);
            float halfLength = MathF.Max(0.01f, lengths[unit] * 0.5f);

            float minX = MathF.Min(halfWidth, worldMap.MaxTileX);
            float maxX = MathF.Max(minX, worldMap.MaxTileX - halfWidth);
            float minY = MathF.Min(halfLength, worldMap.MaxTileY);
            float maxY = MathF.Max(minY, worldMap.MaxTileY - halfLength);

            target.X = Math.Clamp(target.X, minX, maxX);
            target.Y = Math.Clamp(target.Y, minY, maxY);
            targets[unit] = target;

            Vector2 targetDelta = new(
                target.X - position.X,
                target.Y - position.Y);

            float targetDistanceSquared = targetDelta.LengthSquared();

            if (targetDistanceSquared <= 0.0025f)
            {
                position.X = target.X;
                position.Y = target.Y;
                position.Z = SampleSurfaceZ(worldMap, target.X, target.Y);
                positions[unit] = position;
                velocities[unit] = Vector3.Zero;
                hasTarget[unit] = false;
                TraceUnit(traceUnit, "movement-destination-reached", units, ai, inventory,
                    hasTarget, targets, unit, false, default);
                _navigation.ClearRoute(unit);
                continue;
            }

            if (!_navigation.TryGetWaypoint(
                    units,
                    unit,
                    worldMap,
                    position,
                    target,
                    out Vector2 waypoint))
            {
                // Keep the order while a route is queued or temporarily unavailable.
                velocities[unit] = Vector3.Zero;
                TraceUnit(traceUnit, "no-waypoint", units, ai, inventory, hasTarget,
                    targets, unit, false, default);
                continue;
            }

            Vector2 delta = waypoint - new Vector2(position.X, position.Y);
            float distanceSquared = delta.LengthSquared();

            if (distanceSquared <= 0.0025f)
            {
                velocities[unit] = Vector3.Zero;
                TraceUnit(traceUnit, "waypoint-near", units, ai, inventory, hasTarget,
                    targets, unit, true, waypoint);
                continue;
            }

            // Crawling uses a distinct slow locomotion speed, not a standing
            // walk cycle pasted over a prone silhouette.
            float moveSpeed = MathF.Max(0f, speeds[unit]);
            if (postures[unit] == UnitPosture.Lying)
                moveSpeed *= 0.45f;

            float step = moveSpeed * deltaTime;

            if (step <= 0f)
            {
                velocities[unit] = Vector3.Zero;
                TraceUnit(traceUnit, "zero-move-speed", units, ai, inventory, hasTarget,
                    targets, unit, true, waypoint);
                continue;
            }

            if (distanceSquared <= step * step)
            {
                position.X = waypoint.X;
                position.Y = waypoint.Y;
                velocities[unit] = Vector3.Zero;
            }
            else
            {
                float inverseDistance = 1f / MathF.Sqrt(distanceSquared);
                Vector2 direction = delta * inverseDistance;

                position.X += direction.X * step;
                position.Y += direction.Y * step;
                velocities[unit] = new Vector3(
                    direction.X * moveSpeed,
                    direction.Y * moveSpeed,
                    0f);
            }

            position.X = Math.Clamp(position.X, minX, maxX);
            position.Y = Math.Clamp(position.Y, minY, maxY);
            position.Z = SampleSurfaceZ(worldMap, position.X, position.Y);
            positions[unit] = position;
            TraceUnit(traceUnit, "move-step", units, ai, inventory, hasTarget,
                targets, unit, true, waypoint);
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
