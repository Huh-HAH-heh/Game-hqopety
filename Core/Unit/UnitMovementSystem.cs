using System;
using Core.Map;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitMovementSystem
{
    public void Update(
        UnitStore units,
        WorldMap worldMap,
        float deltaTime)
    {
        if (deltaTime <= 0f ||
            units.ActiveCount == 0)
        {
            return;
        }

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        Vector3[] positions =
            units.Position;

        Vector3[] velocities =
            units.Velocity;

        Vector3[] targets =
            units.Target;

        float[] speeds =
            units.MoveSpeed;

        float[] widths =
            units.Width;

        float[] lengths =
            units.Length;

        bool[] hasTarget =
            units.HasTarget;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            Vector3 position =
                positions[unit];

            if (!hasTarget[unit])
            {
                velocities[unit] =
                    Vector3.Zero;

                continue;
            }

            Vector3 target =
                targets[unit];

            float halfWidth =
                MathF.Max(
                    0.01f,
                    widths[unit] * 0.5f);

            float halfLength =
                MathF.Max(
                    0.01f,
                    lengths[unit] * 0.5f);

            float minX =
                MathF.Min(
                    halfWidth,
                    worldMap.MaxTileX);

            float maxX =
                MathF.Max(
                    minX,
                    worldMap.MaxTileX -
                    halfWidth);

            float minY =
                MathF.Min(
                    halfLength,
                    worldMap.MaxTileY);

            float maxY =
                MathF.Max(
                    minY,
                    worldMap.MaxTileY -
                    halfLength);

            target.X =
                Math.Clamp(
                    target.X,
                    minX,
                    maxX);

            target.Y =
                Math.Clamp(
                    target.Y,
                    minY,
                    maxY);

            Vector3 delta =
                target -
                position;

            delta.Z = 0f;

            float distanceSquared =
                delta.LengthSquared();

            if (distanceSquared <=
                0.0025f)
            {
                position.X = target.X;
                position.Y = target.Y;
                position.Z =
                    SampleSurfaceZ(
                        worldMap,
                        target.X,
                        target.Y);

                positions[unit] =
                    position;

                velocities[unit] =
                    Vector3.Zero;

                hasTarget[unit] = false;
                continue;
            }

            float distance =
                MathF.Sqrt(
                    distanceSquared);

            Vector3 direction =
                delta / distance;

            float step =
                speeds[unit] *
                deltaTime;

            if (distance <= step)
            {
                position.X =
                    target.X;

                position.Y =
                    target.Y;

                velocities[unit] =
                    Vector3.Zero;

                hasTarget[unit] = false;
            }
            else
            {
                Vector3 velocity =
                    direction *
                    speeds[unit];

                position +=
                    velocity *
                    deltaTime;

                velocities[unit] =
                    velocity;
            }

            position.X =
                Math.Clamp(
                    position.X,
                    minX,
                    maxX);

            position.Y =
                Math.Clamp(
                    position.Y,
                    minY,
                    maxY);

            position.Z =
                SampleSurfaceZ(
                    worldMap,
                    position.X,
                    position.Y);

            positions[unit] =
                position;
        }
    }

    private static float SampleSurfaceZ(
        WorldMap worldMap,
        float x,
        float y)
    {
        int x0 =
            (int)MathF.Floor(x);

        int y0 =
            (int)MathF.Floor(y);

        int x1 =
            Math.Min(
                x0 + 1,
                worldMap.MaxTileX);

        int y1 =
            Math.Min(
                y0 + 1,
                worldMap.MaxTileY);

        x0 = Math.Clamp(
            x0,
            0,
            worldMap.MaxTileX);

        y0 = Math.Clamp(
            y0,
            0,
            worldMap.MaxTileY);

        float tx =
            Math.Clamp(
                x - x0,
                0f,
                1f);

        float ty =
            Math.Clamp(
                y - y0,
                0f,
                1f);

        float z00 =
            worldMap.GetSurfaceHeightUnits(
                x0,
                y0) *
            0.1f;

        float z10 =
            worldMap.GetSurfaceHeightUnits(
                x1,
                y0) *
            0.1f;

        float z01 =
            worldMap.GetSurfaceHeightUnits(
                x0,
                y1) *
            0.1f;

        float z11 =
            worldMap.GetSurfaceHeightUnits(
                x1,
                y1) *
            0.1f;

        float top =
            z00 +
            (z10 - z00) *
            tx;

        float bottom =
            z01 +
            (z11 - z01) *
            tx;

        return
            top +
            (bottom - top) *
            ty;
    }
}
