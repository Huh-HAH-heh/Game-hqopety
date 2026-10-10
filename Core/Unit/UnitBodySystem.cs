using System;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitBodySystem
{
    private float _animationTime;

    public void Update(
        UnitStore units,
        UnitBodyStore bodies,
        float deltaTime)
    {
        if (units.ActiveCount == 0)
            return;

        _animationTime += Math.Clamp(deltaTime, 0f, 0.1f);
        float followAlpha = Math.Clamp(deltaTime * 14f, 0f, 1f);
        ReadOnlySpan<int> active = units.ActiveIndices;

        Vector3[] unitPositions = units.Position;
        Vector3[] velocities = units.Velocity;
        float[] moveSpeeds = units.MoveSpeed;
        Vector3[] bodyNormals = units.BodyNormal;
        Vector3[] headNormals = units.HeadNormal;
        UnitPosture[] postures = units.Posture;
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
        BodyPartRole[] roles = bodies.Role;

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
                velocities,
                moveSpeeds,
                postures[unit],
                _animationTime,
                worldPositions,
                worldRotations,
                localPositions,
                localRotations,
                parents,
                motions,
                anchors,
                roles,
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
        Vector3[] velocities,
        float[] moveSpeeds,
        UnitPosture posture,
        float animationTime,
        Vector3[] worldPositions,
        float[] worldRotations,
        Vector3[] localPositions,
        float[] localRotations,
        int[] parents,
        BodyPartMotion[] motions,
        BodyPartAnchor[] anchors,
        BodyPartRole[] roles,
        float[] followDistances,
        float followAlpha)
    {
        Vector3 bodyNormal = NormalizeHorizontal(bodyNormals[unit]);
        Vector3 headNormal = NormalizeHorizontal(headNormals[unit]);
        Vector3 velocity = velocities[unit];
        float speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);

        // During movement the torso faces its travel direction. The head keeps
        // its independent aim direction, so a pawn can move and look elsewhere.
        if (speed > 0.05f)
        {
            bodyNormal = NormalizeHorizontal(velocity);
            bodyNormals[unit] = bodyNormal;
        }

        float bodyRotation = MathF.Atan2(bodyNormal.Y, bodyNormal.X);
        float headRotation = MathF.Atan2(headNormal.Y, headNormal.X);
        float gait = Math.Clamp(
            speed / MathF.Max(0.5f, moveSpeeds[unit] * 0.20f),
            0f,
            1f);
        float gaitPhase = animationTime * 11f + unit * 1.317f;
        int end = start + count;

        // Definitions are parent-first, so every parent's transform has
        // already been calculated when its children are visited.
        for (int part = start; part < end; part++)
        {
            int parent = parents[part];
            Vector3 localPosition = localPositions[part];
            float localRotation = localRotations[part];
            ApplyPosture(posture, roles[part], ref localPosition, ref localRotation);

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

            float gaitRotation = motions[part] switch
            {
                BodyPartMotion.WalkCycle =>
                    MathF.Sin(gaitPhase) * 0.32f * gait,

                BodyPartMotion.WalkCycleOpposite =>
                    -MathF.Sin(gaitPhase) * 0.32f * gait,

                _ => 0f
            };

            // The hip/shoulder anchor stays attached to its parent.
            // Gait rotates the limb itself; child segments inherit that pose.
            worldPositions[part] =
                origin + Rotate2D(localPosition, rotation);
            worldRotations[part] =
                rotation + localRotation + gaitRotation;
        }
    }

    private static void ApplyPosture(
        UnitPosture posture,
        BodyPartRole role,
        ref Vector3 localPosition,
        ref float localRotation)
    {
        if (posture == UnitPosture.Standing)
            return;

        if (posture == UnitPosture.Crouching)
        {
            switch (role)
            {
                case BodyPartRole.UpperArm:
                    localPosition.Y *= 0.78f;
                    localRotation *= 0.76f;
                    break;

                case BodyPartRole.Forearm:
                    localRotation *= 1.15f;
                    break;

                case BodyPartRole.Elbow:
                case BodyPartRole.Hand:
                    localPosition.X *= 0.88f;
                    break;

                case BodyPartRole.Thigh:
                    localPosition.X *= 0.92f;
                    localPosition.Y *= 0.78f;
                    if (MathF.Abs(localRotation) > 2.5f)
                        localRotation -= MathF.Sign(localRotation) * 0.30f;
                    break;

                case BodyPartRole.Knee:
                case BodyPartRole.Shin:
                case BodyPartRole.Foot:
                    localPosition.X *= 0.85f;
                    break;
            }

            return;
        }

        // Prone pawns have a wider, flatter limb silhouette. Their head and
        // torso still face the same world direction; this is not a 90-degree
        // side-view rotation.
        switch (role)
        {
            case BodyPartRole.UpperArm:
                localPosition.Y *= 1.18f;
                localRotation = MathF.CopySign(
                    MathF.Max(MathF.Abs(localRotation), 1.34f),
                    localRotation);
                break;

            case BodyPartRole.Forearm:
                localRotation *= 0.45f;
                break;

            case BodyPartRole.Elbow:
            case BodyPartRole.Hand:
                localPosition.X *= 1.05f;
                break;

            case BodyPartRole.Thigh:
                localPosition.Y *= 1.18f;
                if (MathF.Abs(localRotation) > 2.5f)
                    localRotation = MathF.CopySign(MathF.PI, localRotation);
                break;

            case BodyPartRole.Knee:
            case BodyPartRole.Shin:
            case BodyPartRole.Foot:
                localPosition.X *= 1.05f;
                break;
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
