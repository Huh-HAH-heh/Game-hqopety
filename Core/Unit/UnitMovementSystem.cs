using System;
using Core.Map;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitMovementSystem
{
    private readonly UnitNavigationSystem _navigation = new UnitNavigationSystem();

    public UnitNavigationSystem Navigation => _navigation;

    public void Update(
        UnitStore units,
        WorldMap worldMap,
        float deltaTime,
        UnitHealthStore? health = null)
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

            float distance = MathF.Sqrt(distanceSquared);
            float step = MathF.Max(0f, speeds[unit]) * deltaTime;

            if (step <= 0f)
            {
                velocities[unit] = Vector3.Zero;
                continue;
            }

            Vector2 direction = delta / distance;

            if (distance <= step)
            {
                position.X = waypoint.X;
                position.Y = waypoint.Y;
                velocities[unit] = Vector3.Zero;
            }
            else
            {
                position.X += direction.X * step;
                position.Y += direction.Y * step;
                velocities[unit] = new Vector3(
                    direction.X * speeds[unit],
                    direction.Y * speeds[unit],
                    0f);
            }

            position.X = Math.Clamp(position.X, minX, maxX);
            position.Y = Math.Clamp(position.Y, minY, maxY);
            position.Z = SampleSurfaceZ(worldMap, position.X, position.Y);
            positions[unit] = position;
        }
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
