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

        Vector3[] bodyNormals =
            units.BodyNormal;

        Vector3[] headNormals =
            units.HeadNormal;

        float[] widths =
            units.Width;

        float[] lengths =
            units.Length;

        float[] heights =
            units.Height;

        UnitPosture[] postures =
            units.Posture;

        UnitType[] types =
            units.Type;

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
            int unit =
                active[i];

            int start =
                bodyStarts[unit];

            int count =
                bodyCounts[unit];

            if (count <= 0)
                continue;

            if (types[unit] ==
                UnitType.Colonist &&
                count >= 2)
            {
                UpdateColonist(
                    unit,
                    start,
                    unitPositions,
                    bodyNormals,
                    headNormals,
                    widths,
                    lengths,
                    heights,
                    postures,
                    worldPositions,
                    scales,
                    worldRotations);
            }
            else
            {
                UpdateGenericBody(
                    unit,
                    start,
                    count,
                    unitPositions,
                    bodyNormals,
                    worldPositions,
                    worldRotations,
                    localPositions,
                    localRotations,
                    parents,
                    motions,
                    followDistances,
                    followAlpha);
            }
        }
    }

    private static void UpdateColonist(
        int unit,
        int start,
        Vector3[] unitPositions,
        Vector3[] bodyNormals,
        Vector3[] headNormals,
        float[] widths,
        float[] lengths,
        float[] heights,
        UnitPosture[] postures,
        Vector3[] worldPositions,
        Vector3[] scales,
        float[] worldRotations)
    {
        Vector3 position =
            unitPositions[unit];

        Vector3 bodyNormal =
            NormalizeHorizontal(
                bodyNormals[unit]);

        Vector3 headNormal =
            NormalizeHorizontal(
                headNormals[unit]);

        float width =
            MathF.Max(
                0.05f,
                widths[unit]);

        float length =
            MathF.Max(
                0.05f,
                lengths[unit]);

        float height =
            MathF.Max(
                0.05f,
                heights[unit]);

        bool lying =
            postures[unit] ==
            UnitPosture.Lying;

        float bodyLength =
            lying
                ? length
                : length * 0.60f;

        float headRadius =
            MathF.Min(
                width,
                length) *
            0.32f;

        float headDistance =
            bodyLength * 0.5f +
            headRadius * 1.15f;

        scales[start] =
            new Vector3(
                width * 0.5f,
                bodyLength * 0.5f,
                height * 0.5f);

        worldPositions[start] =
            position +
            new Vector3(
                0f,
                0f,
                lying
                    ? height * 0.5f
                    : height * 0.5f);

        worldRotations[start] =
            MathF.Atan2(
                bodyNormal.Y,
                bodyNormal.X);

        int head =
            start + 1;

        scales[head] =
            new Vector3(
                headRadius,
                headRadius,
                headRadius);

        worldPositions[head] =
            position +
            headNormal * headDistance +
            new Vector3(
                0f,
                0f,
                lying
                    ? height * 0.55f
                    : height);

        worldRotations[head] =
            MathF.Atan2(
                headNormal.Y,
                headNormal.X);
    }

    private static void UpdateGenericBody(
        int unit,
        int start,
        int count,
        Vector3[] unitPositions,
        Vector3[] bodyNormals,
        Vector3[] worldPositions,
        float[] worldRotations,
        Vector3[] localPositions,
        float[] localRotations,
        int[] parents,
        BodyPartMotion[] motions,
        float[] followDistances,
        float followAlpha)
    {
        Vector3 bodyNormal =
            NormalizeHorizontal(
                bodyNormals[unit]);

        float bodyRotation =
            MathF.Atan2(
                bodyNormal.Y,
                bodyNormal.X);

        for (int part = start;
             part < start + count;
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
                bodyRotation;

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

            direction =
                new Vector3(
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
            direction *
            distances[part];

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

    private static Vector3 NormalizeHorizontal(
        Vector3 value)
    {
        value.Z = 0f;

        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared < 0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }

    private static Vector3 Rotate2D(
        Vector3 value,
        float angle)
    {
        float cos =
            MathF.Cos(angle);

        float sin =
            MathF.Sin(angle);

        return new Vector3(
            value.X * cos -
            value.Y * sin,
            value.X * sin +
            value.Y * cos,
            value.Z);
    }
}
