using System;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitBodySystem
{
    public void Update(
        UnitStore units,
        UnitBodyStore bodies,
        float deltaTime)
    {
        if (units.ActiveCount == 0)
            return;

        float followAlpha =
            Math.Clamp(
                deltaTime * 14f,
                0f,
                1f);

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        Vector3[] unitPositions =
            units.Position;

        float[] unitRotations =
            units.Rotation;

        int[] bodyStarts =
            units.BodyStart;

        short[] bodyCounts =
            units.BodyCount;

        Vector3[] localPositions =
            bodies.LocalPosition;

        Vector3[] worldPositions =
            bodies.WorldPosition;

        Vector3[] scales =
            bodies.Scale;

        float[] localRotations =
            bodies.LocalRotation;

        float[] worldRotations =
            bodies.WorldRotation;

        float[] followDistances =
            bodies.FollowDistance;

        int[] parents =
            bodies.Parent;

        BodyPartMotion[] motions =
            bodies.Motion;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit = active[i];

            int start =
                bodyStarts[unit];

            int count =
                bodyCounts[unit];

            int end =
                start + count;

            if (count <= 0)
                continue;

            for (int part = start;
                 part < end;
                 part++)
            {
                int parent =
                    parents[part];

                if (motions[part] ==
                    BodyPartMotion.FollowParent &&
                    parent >= start)
                {
                    UpdateFollower(
                        part,
                        parent,
                        worldPositions,
                        worldRotations,
                        followDistances,
                        followAlpha);

                    continue;
                }

                Vector3 origin =
                    unitPositions[unit];

                float rotation =
                    unitRotations[unit];

                if (parent >= start)
                {
                    origin =
                        worldPositions[parent];

                    rotation =
                        worldRotations[parent];
                }

                worldPositions[part] =
                    origin +
                    Rotate2D(
                        localPositions[part],
                        rotation);

                worldRotations[part] =
                    rotation +
                    localRotations[part];
            }
        }
    }

    private static void UpdateFollower(
        int part,
        int parent,
        Vector3[] positions,
        float[] rotations,
        float[] distances,
        float alpha)
    {
        Vector3 current =
            positions[part];

        Vector3 parentPosition =
            positions[parent];

        Vector3 delta =
            current -
            parentPosition;

        delta.Z = 0f;

        float lengthSquared =
            delta.LengthSquared();

        Vector3 direction;

        if (lengthSquared < 0.0001f)
        {
            float angle =
                rotations[parent];

            direction = new Vector3(
                -MathF.Cos(angle),
                -MathF.Sin(angle),
                0f);
        }
        else
        {
            direction =
                delta /
                MathF.Sqrt(
                    lengthSquared);
        }

        Vector3 desired =
            parentPosition +
            direction * distances[part];

        positions[part] =
            Vector3.Lerp(
                current,
                desired,
                alpha);

        Vector3 toParent =
            parentPosition -
            positions[part];

        rotations[part] =
            MathF.Atan2(
                toParent.Y,
                toParent.X);
    }

    private static Vector3 Rotate2D(
        Vector3 value,
        float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        return new Vector3(
            value.X * cos - value.Y * sin,
            value.X * sin + value.Y * cos,
            value.Z);
    }
}
