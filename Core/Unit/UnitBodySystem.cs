using System;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitBodySystem
{
    private const int PostureCount = 4;
    private const float PostureTransitionSpeed = 2.8f;
    private float _animationTime;
    private float[] _postureWeights = new float[1024 * PostureCount];
    private bool[] _postureInitialized = new bool[1024];
    private uint[] _postureGeneration = new uint[1024];

    public void Update(
        UnitStore units,
        UnitBodyStore bodies,
        float deltaTime)
    {
        if (units.ActiveCount == 0)
            return;

        _animationTime += Math.Clamp(deltaTime, 0f, 0.1f);
        EnsurePostureCapacity(units.Type.Length);
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
        float[] tailAngles = bodies.TailAngle;
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
        Vector3[] crouchingOffsets = bodies.CrouchingOffset;
        Vector3[] sittingOffsets = bodies.SittingOffset;
        Vector3[] lyingOffsets = bodies.LyingOffset;
        float[] crouchingRotationOffsets = bodies.CrouchingRotationOffset;
        float[] sittingRotationOffsets = bodies.SittingRotationOffset;
        float[] lyingRotationOffsets = bodies.LyingRotationOffset;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            int start = bodyStarts[unit];
            int count = bodyCounts[unit];

            if (count <= 0)
                continue;

            UpdatePostureWeights(unit, postures[unit], units.Generation[unit], deltaTime);
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
                tailAngles,
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
                crouchingOffsets,
                sittingOffsets,
                lyingOffsets,
                crouchingRotationOffsets,
                sittingRotationOffsets,
                lyingRotationOffsets,
                followDistances,
                followAlpha,
                Math.Clamp(deltaTime, 0f, 0.05f));
        }
    }

    private void UpdateBody(
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
        float[] tailAngles,
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
        Vector3[] crouchingOffsets,
        Vector3[] sittingOffsets,
        Vector3[] lyingOffsets,
        float[] crouchingRotationOffsets,
        float[] sittingRotationOffsets,
        float[] lyingRotationOffsets,
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
            ReadOnlySpan<float> poseWeights = _postureWeights.AsSpan(unit * PostureCount, PostureCount);
            ApplyPosture(
                poseWeights,
                roles[part], pivots[part],
                ref localPosition, ref localRotation);
            localPosition += crouchingOffsets[part] * poseWeights[(int)UnitPosture.Crouching] +
                             sittingOffsets[part] * poseWeights[(int)UnitPosture.Sitting] +
                             lyingOffsets[part] * poseWeights[(int)UnitPosture.Lying];
            localRotation += crouchingRotationOffsets[part] * poseWeights[(int)UnitPosture.Crouching] +
                             sittingRotationOffsets[part] * poseWeights[(int)UnitPosture.Sitting] +
                             lyingRotationOffsets[part] * poseWeights[(int)UnitPosture.Lying];

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
                     motions[part] is BodyPartMotion.SpinalChain or BodyPartMotion.PhysicalTail)
            {
                UpdateChainLink(
                    part,
                    parent,
                    worldPositions,
                    tailAngles,
                    tailInitialized,
                    worldRotations,
                    roles,
                    followDistances,
                    bodyRotation,
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

    private void EnsurePostureCapacity(int requiredUnits)
    {
        if (requiredUnits <= _postureInitialized.Length)
            return;

        int capacity = _postureInitialized.Length;
        while (capacity < requiredUnits)
            capacity *= 2;

        Array.Resize(ref _postureGeneration, capacity);
        Array.Resize(ref _postureInitialized, capacity);
        Array.Resize(ref _postureWeights, capacity * PostureCount);
    }

    private void UpdatePostureWeights(int unit, UnitPosture target, uint generation, float deltaTime)
    {
        int offset = unit * PostureCount;
        int targetIndex = Math.Clamp((int)target, 0, PostureCount - 1);

        if (!_postureInitialized[unit] || _postureGeneration[unit] != generation)
        {
            Array.Clear(_postureWeights, offset, PostureCount);
            _postureWeights[offset + targetIndex] = 1f;
            _postureGeneration[unit] = generation;
            _postureInitialized[unit] = true;
            return;
        }
        float alpha = 1f - MathF.Exp(-PostureTransitionSpeed * Math.Clamp(deltaTime, 0f, 0.1f));
        float sum = 0f;
        for (int pose = 0; pose < PostureCount; pose++)
        {
            int index = offset + pose;
            float desired = pose == targetIndex ? 1f : 0f;
            _postureWeights[index] += (desired - _postureWeights[index]) * alpha;
            _postureWeights[index] = Math.Clamp(_postureWeights[index], 0f, 1f);
            sum += _postureWeights[index];
        }

        if (sum <= 0.0001f)
        {
            Array.Clear(_postureWeights, offset, PostureCount);
            _postureWeights[offset + targetIndex] = 1f;
            return;
        }

        for (int pose = 0; pose < PostureCount; pose++)
            _postureWeights[offset + pose] /= sum;
    }

    private static void ApplyPosture(
        ReadOnlySpan<float> postureWeights,
        BodyPartRole role,
        BodyPartPivot pivot,
        ref Vector3 localPosition,
        ref float localRotation)
    {
        Vector3 basePosition = localPosition;
        float baseRotation = localRotation;
        Vector3 positionDelta = Vector3.Zero;
        float rotationDelta = 0f;

        for (int poseIndex = 1; poseIndex < PostureCount; poseIndex++)
        {
            float weight = postureWeights[poseIndex];
            if (weight <= 0.0001f)
                continue;

            Vector3 posePosition = basePosition;
            float poseRotation = baseRotation;
            ApplyPostureShape((UnitPosture)poseIndex, role, ref posePosition, ref poseRotation);
            positionDelta += (posePosition - basePosition) * weight;
            rotationDelta += (poseRotation - baseRotation) * weight;
        }

        // Blend relative to the base pose; parent/child joint pivots remain linked.
        localPosition = basePosition + positionDelta;
        localRotation = baseRotation + rotationDelta;
    }

    private static void ApplyPostureShape(
        UnitPosture posture,
        BodyPartRole role,
        ref Vector3 localPosition,
        ref float localRotation)
    {
        if (posture == UnitPosture.Standing)
            return;

        // Only relative transforms change; parent and child joints stay linked.
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

        if (posture == UnitPosture.Sitting)
        {
            switch (role)
            {
                case BodyPartRole.UpperArm:
                    localPosition.Y *= 0.72f;
                    localPosition.Z *= 0.92f;
                    localRotation *= 0.72f;
                    break;
                case BodyPartRole.Forearm:
                    localRotation = MathF.CopySign(2.45f, localRotation);
                    break;
                case BodyPartRole.Thigh:
                    localPosition.X *= 0.48f;
                    localPosition.Y *= 1.22f;
                    localRotation = MathF.CopySign(1.05f, localRotation);
                    break;
                case BodyPartRole.Shin:
                    localRotation = MathF.CopySign(0.95f, localRotation);
                    break;
            }
            return;
        }

        // Prone pose keeps the colonist's top-down orientation.
        switch (role)
        {
            case BodyPartRole.UpperArm:
                localPosition.Y *= 1.12f;
                localRotation = MathF.CopySign(MathF.Max(MathF.Abs(localRotation), 1.55f), localRotation);
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

    private static void UpdateChainLink(
        int part,
        int parent,
        Vector3[] positions,
        float[] tailAngles,
        bool[] initialized,
        float[] rotations,
        BodyPartRole[] roles,
        float[] distances,
        float bodyRotation,
        float deltaTime)
    {
        Vector3 parentPosition = positions[parent];
        Vector3 current = positions[part];
        float angle = tailAngles[part];

        if (!initialized[part])
        {
            Vector3 direction = current - parentPosition;
            direction.Z = 0f;
            angle = direction.LengthSquared() > 0.000001f
                ? MathF.Atan2(direction.Y, direction.X)
                : bodyRotation + MathF.PI;
            initialized[part] = true;
        }

        // Only the bend angle is persistent. Every body section follows the
        // previous section, so the whole unit curves through turns without momentum.
        float targetAngle = roles[parent] == BodyPartRole.Head
            ? bodyRotation + MathF.PI
            : tailAngles[parent];

        float response = roles[part] == BodyPartRole.Tail ? 8f : 12f;
        float blend = 1f - MathF.Exp(-response * Math.Clamp(deltaTime, 0f, 0.05f));
        angle = WrapAngle(angle + WrapAngle(targetAngle - angle) * blend);

        // Keep fixed link lengths and limit the bend between neighboring links.
        // This prevents sharp kinks without integrating velocity or stretching the chain.
        if (roles[parent] != BodyPartRole.Head)
        {
            float maxBend = roles[part] == BodyPartRole.Tail ? 0.55f : 0.42f;
            float bend = Math.Clamp(WrapAngle(angle - targetAngle), -maxBend, maxBend);
            angle = WrapAngle(targetAngle + bend);
        }

        float length = MathF.Max(0.015f, distances[part]);
        Vector3 directionToChild = new(MathF.Cos(angle), MathF.Sin(angle), 0f);
        current = parentPosition + directionToChild * length;
        current.Z = parentPosition.Z;

        tailAngles[part] = angle;
        positions[part] = current;
        rotations[part] = angle;
    }

    private static float WrapAngle(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.Tau;
        while (angle < -MathF.PI)
            angle += MathF.Tau;
        return angle;
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
