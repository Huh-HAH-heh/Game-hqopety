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
        Vector3[] previousWorldPositions = bodies.PreviousWorldPosition;
        bool[] tailInitialized = bodies.TailInitialized;
        float[] localRotations = bodies.LocalRotation;
        float[] worldRotations = bodies.WorldRotation;
        float[] followDistances = bodies.FollowDistance;
        int[] parents = bodies.Parent;
        BodyPartMotion[] motions = bodies.Motion;
        BodyPartAnchor[] anchors = bodies.Anchor;
        BodyPartRole[] roles = bodies.Role;
        BodyPartPivot[] pivots = bodies.Pivot;
        Vector3[] scales = bodies.Scale;

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
                previousWorldPositions,
                tailInitialized,
                worldRotations,
                localPositions,
                localRotations,
                parents,
                motions,
                anchors,
                roles,
                pivots,
                scales,
                followDistances,
                followAlpha,
                Math.Clamp(deltaTime, 0f, 0.05f));
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
        Vector3[] previousWorldPositions,
        bool[] tailInitialized,
        float[] worldRotations,
        Vector3[] localPositions,
        float[] localRotations,
        int[] parents,
        BodyPartMotion[] motions,
        BodyPartAnchor[] anchors,
        BodyPartRole[] roles,
        BodyPartPivot[] pivots,
        Vector3[] scales,
        float[] followDistances,
        float followAlpha,
        float deltaTime)
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
            ApplyPosture(posture, roles[part], pivots[part],
                ref localPosition, ref localRotation);

            Vector3 origin = unitPositions[unit];
            float positionRotation = bodyRotation;
            float partBaseRotation = bodyRotation;

            if (anchors[part] == BodyPartAnchor.UnitHead)
            {
                // Keep the head center anchored ahead of the torso even when
                // its facing direction changes independently.
                partBaseRotation = headRotation;
            }
            else if (anchors[part] == BodyPartAnchor.Parent &&
                     parent >= start &&
                     motions[part] == BodyPartMotion.PhysicalTail)
            {
                UpdatePhysicalTail(
                    part,
                    parent,
                    worldPositions,
                    previousWorldPositions,
                    tailInitialized,
                    worldRotations,
                    followDistances,
                    deltaTime);
                continue;
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
                positionRotation = worldRotations[parent];
                partBaseRotation = worldRotations[parent];
            }

            float gaitRotation = motions[part] switch
            {
                BodyPartMotion.WalkCycle =>
                    MathF.Sin(gaitPhase) * 0.32f * gait,

                BodyPartMotion.WalkCycleOpposite =>
                    -MathF.Sin(gaitPhase) * 0.32f * gait,

                _ => 0f
            };

            float worldRotation =
                partBaseRotation + localRotation + gaitRotation;

            Vector3 joint =
                origin + Rotate2D(localPosition, positionRotation);

            float profilePhase = motions[part] switch
            {
                BodyPartMotion.WalkCycleProfile => MathF.Sin(gaitPhase),
                BodyPartMotion.WalkCycleProfileOpposite => -MathF.Sin(gaitPhase),
                _ => 0f
            };

            if (profilePhase != 0f)
            {
                joint += Rotate2D(
                    new Vector3(
                        profilePhase * 0.17f * gait,
                        profilePhase * 0.025f * gait,
                        0f),
                    positionRotation);
            }

            // Rotating a limb around its center makes the shoulder/hip drift
            // away from the torso. Start-pivot parts are instead positioned
            // from their proximal joint, then offset to their visual center.
            if (pivots[part] == BodyPartPivot.Start)
            {
                joint += Rotate2D(
                    new Vector3(MathF.Abs(scales[part].X), 0f, 0f),
                    worldRotation);
            }

            worldPositions[part] = joint;
            worldRotations[part] = worldRotation;
        }
    }

    private static void ApplyPosture(
        UnitPosture posture,
        BodyPartRole role,
        BodyPartPivot pivot,
        ref Vector3 localPosition,
        ref float localRotation)
    {
        if (posture == UnitPosture.Standing)
            return;

        // Joint offsets remain untouched: children must stay on the exact
        // endpoint of their parent. Only root attachment points and angles
        // are adjusted for the pose.
        if (posture == UnitPosture.Crouching)
        {
            switch (role)
            {
                case BodyPartRole.UpperArm:
                    localPosition.Y *= 0.82f;
                    localRotation *= 0.84f;
                    break;

                case BodyPartRole.Forearm:
                    localRotation = MathF.CopySign(2.20f, localRotation);
                    break;

                case BodyPartRole.Thigh:
                    localPosition.X *= 1.05f;
                    localPosition.Y *= 0.82f;
                    localRotation -= MathF.Sign(localRotation) * 0.12f;
                    break;

                case BodyPartRole.Shin:
                    localRotation = MathF.CopySign(0.45f, localRotation);
                    break;
            }

            return;
        }

        // Prone is still an overhead silhouette: arms open out and the legs
        // extend back. It never rotates the pawn into a side-on presentation.
        switch (role)
        {
            case BodyPartRole.UpperArm:
                localPosition.Y *= 1.12f;
                localRotation = MathF.CopySign(
                    MathF.Max(MathF.Abs(localRotation), 1.55f),
                    localRotation);
                break;

            case BodyPartRole.Forearm:
                localRotation = MathF.CopySign(1.80f, localRotation);
                break;

            case BodyPartRole.Thigh:
                localPosition.X *= 1.08f;
                localPosition.Y *= 1.18f;
                localRotation = MathF.CopySign(MathF.PI - 0.12f, localRotation);
                break;

            case BodyPartRole.Shin:
                localRotation *= 0.5f;
                break;
        }
    }

    private static void UpdatePhysicalTail(
        int part,
        int parent,
        Vector3[] positions,
        Vector3[] previousPositions,
        bool[] initialized,
        float[] rotations,
        float[] distances,
        float deltaTime)
    {
        Vector3 parentPosition = positions[parent];
        Vector3 current = positions[part];

        if (!initialized[part])
        {
            // Use the rig's authored curve on first spawn/reset.
            previousPositions[part] = current;
            initialized[part] = true;
            current.Z = parentPosition.Z;
            positions[part] = current;
            rotations[part] = MathF.Atan2(
                parentPosition.Y - current.Y,
                parentPosition.X - current.X);
            return;
        }

        Vector3 previous = previousPositions[part];
        previousPositions[part] = current;

        float frameScale = Math.Clamp(deltaTime * 60f, 0f, 3f);
        float damping = MathF.Pow(0.90f, frameScale);
        Vector3 predicted = current + (current - previous) * damping * frameScale;
        predicted.Z = parentPosition.Z;

        Vector3 delta = predicted - parentPosition;
        delta.Z = 0f;
        float lengthSquared = delta.LengthSquared();
        Vector3 direction;

        if (lengthSquared < 0.000001f)
        {
            float angle = rotations[parent];
            direction = new Vector3(-MathF.Cos(angle), -MathF.Sin(angle), 0f);
        }
        else
        {
            direction = delta / MathF.Sqrt(lengthSquared);
        }

        // Distance constraint acts like an inextensible tail link; inertia
        // determines its angle, while the parent anchors it to the body.
        float length = MathF.Max(0.015f, distances[part]);
        current = parentPosition + direction * length;
        current.Z = parentPosition.Z;
        positions[part] = current;
        rotations[part] = MathF.Atan2(
            parentPosition.Y - current.Y,
            parentPosition.X - current.X);
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
