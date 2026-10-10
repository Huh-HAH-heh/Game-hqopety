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

        float followAlpha = Math.Clamp(deltaTime * 14f, 0f, 1f);
        ReadOnlySpan<int> active = units.ActiveIndices;

        Vector3[] unitPositions = units.Position;
        Vector3[] bodyNormals = units.BodyNormal;
        Vector3[] headNormals = units.HeadNormal;
        int[] bodyStarts = units.BodyStart;
        short[] bodyCounts = units.BodyCount;

        Vector3[] localPositions = bodies.LocalPosition;
        Vector3[] worldPositions = bodies.WorldPosition;
        float[] localRotations = bodies.LocalRotation;
        float[] worldRotations = bodies.WorldRotation;
        float[] followDistances = bodies.FollowDistance;
        int[] parents = bodies.Parent;
        BodyPartMotion[] motions = bodies.Motion;
        BodyPartAnchor[] anchors = bodies.Anchor;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            int start = bodyStarts[unit];
            int count = bodyCounts[unit];

            if (count <= 0)
                continue;

            UpdateBody(
                unit,
                start,
                count,
                unitPositions,
                bodyNormals,
                headNormals,
                worldPositions,
                worldRotations,
                localPositions,
                localRotations,
                parents,
                motions,
                anchors,
                followDistances,
                followAlpha);
        }
    }

    private static void UpdateBody(
        int unit,
        int start,
        int count,
        Vector3[] unitPositions,
        Vector3[] bodyNormals,
        Vector3[] headNormals,
        Vector3[] worldPositions,
        float[] worldRotations,
        Vector3[] localPositions,
        float[] localRotations,
        int[] parents,
        BodyPartMotion[] motions,
        BodyPartAnchor[] anchors,
        float[] followDistances,
        float followAlpha)
    {
        Vector3 bodyNormal = NormalizeHorizontal(bodyNormals[unit]);
        Vector3 headNormal = NormalizeHorizontal(headNormals[unit]);
        float bodyRotation = MathF.Atan2(bodyNormal.Y, bodyNormal.X);
        float headRotation = MathF.Atan2(headNormal.Y, headNormal.X);
        int end = start + count;

        // Definitions are parent-first, so every parent's transform has
        // already been calculated when its children are visited.
        for (int part = start; part < end; part++)
        {
            int parent = parents[part];
            Vector3 origin = unitPositions[unit];
            float rotation = bodyRotation;

            if (anchors[part] == BodyPartAnchor.UnitHead)
            {
                rotation = headRotation;
            }
            else if (anchors[part] == BodyPartAnchor.Parent &&
                     motions[part] == BodyPartMotion.FollowParent &&
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
            else if (anchors[part] == BodyPartAnchor.Parent &&
                     parent >= start)
            {
                origin = worldPositions[parent];
                rotation = worldRotations[parent];
            }

            worldPositions[part] =
                origin + Rotate2D(localPositions[part], rotation);
            worldRotations[part] = rotation + localRotations[part];
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
        Vector3 current = positions[part];
        Vector3 parentPosition = positions[parent];
        Vector3 delta = current - parentPosition;
        delta.Z = 0f;

        float lengthSquared = delta.LengthSquared();
        Vector3 direction;

        if (lengthSquared < 0.0001f)
        {
            float angle = rotations[parent];
            direction = new Vector3(
                -MathF.Cos(angle),
                -MathF.Sin(angle),
                0f);
        }
        else
        {
            direction = delta / MathF.Sqrt(lengthSquared);
        }

        Vector3 desired = parentPosition + direction * distances[part];
        positions[part] = Vector3.Lerp(current, desired, alpha);

        Vector3 toParent = parentPosition - positions[part];
        rotations[part] = MathF.Atan2(toParent.Y, toParent.X);
    }

    private static Vector3 NormalizeHorizontal(Vector3 value)
    {
        value.Z = 0f;
        float lengthSquared = value.LengthSquared();

        if (lengthSquared < 0.000001f)
            return new Vector3(1f, 0f, 0f);

        return value / MathF.Sqrt(lengthSquared);
    }

    private static Vector3 Rotate2D(Vector3 value, float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        return new Vector3(
            value.X * cos - value.Y * sin,
            value.X * sin + value.Y * cos,
            value.Z);
    }
}
