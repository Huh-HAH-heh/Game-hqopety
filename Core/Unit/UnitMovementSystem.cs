using System;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

public sealed class UnitMovementSystem
{
    private readonly UnitNavigationSystem _navigation = new UnitNavigationSystem();
    private readonly UnitSpatialGrid _collisionGrid = new UnitSpatialGrid();
    private int[] _collisionVisitStamp = Array.Empty<int>();
    private int _collisionStamp;

    public UnitNavigationSystem Navigation => _navigation;

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

                if (combatDelta.X * combatDelta.X + combatDelta.Y * combatDelta.Y <=
                    stopDistance * stopDistance)
                {
                    hasTarget[unit] = false;
                    velocities[unit] = Vector3.Zero;
                    _navigation.ClearRoute(unit);
                    continue;
                }
            }

            if (!hasTarget[unit])
            {
                velocities[unit] = Vector3.Zero;
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
                continue;
            }

            Vector2 delta = waypoint - new Vector2(position.X, position.Y);
            float distanceSquared = delta.LengthSquared();

            if (distanceSquared <= 0.0025f)
            {
                velocities[unit] = Vector3.Zero;
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
        }

        ResolveUnitOverlaps(units, worldMap, health);
    }

    private void ResolveUnitOverlaps(
        UnitStore units,
        WorldMap worldMap,
        UnitHealthStore? health)
    {
        _collisionGrid.Ensure(worldMap, units.Capacity);
        _collisionGrid.Build(units, health);

        if (_collisionVisitStamp.Length < units.Capacity)
            Array.Resize(ref _collisionVisitStamp, units.Capacity);

        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < active.Length; i++)
            {
                int unit = active[i];
                if (health != null && health.OverallHitPoints[unit] <= 0f)
                    continue;

                Vector3 position = units.Position[unit];
                int cellX = (int)MathF.Floor(position.X);
                int cellY = (int)MathF.Floor(position.Y);
                int stamp = NextCollisionStamp();

                for (int y = cellY - 1; y <= cellY + 1; y++)
                {
                    for (int x = cellX - 1; x <= cellX + 1; x++)
                    {
                        int node = _collisionGrid.GetCellHead(x, y);
                        while (node >= 0)
                        {
                            int other = _collisionGrid.GetNodeUnit(node);
                            node = _collisionGrid.GetNextNode(node);

                            if (other <= unit ||
                                other >= units.Capacity ||
                                _collisionVisitStamp[other] == stamp ||
                                (health != null && health.OverallHitPoints[other] <= 0f))
                            {
                                continue;
                            }

                            _collisionVisitStamp[other] = stamp;
                            Vector3 otherPosition = units.Position[other];
                            float dx = otherPosition.X - position.X;
                            float dy = otherPosition.Y - position.Y;
                            float distanceSquared = dx * dx + dy * dy;
                            float minimumDistance = MathF.Max(
                                0.04f,
                                (units.Radius[unit] + units.Radius[other]) * 0.96f);

                            if (distanceSquared >= minimumDistance * minimumDistance)
                                continue;

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
                        }
                    }
                }
            }
        }
    }

    private int NextCollisionStamp()
    {
        if (_collisionStamp >= int.MaxValue - 1)
        {
            Array.Clear(_collisionVisitStamp);
            _collisionStamp = 0;
        }

        return ++_collisionStamp;
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
