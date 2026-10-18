using System;
using System.Collections.Generic;
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

        _animationTime = (_animationTime + Math.Clamp(deltaTime, 0f, 0.1f)) % 1000f;
        float followAlpha = Math.Clamp(deltaTime * 14f, 0f, 1f);
        ReadOnlySpan<int> active = units.ActiveIndices;

        Vector3[] unitPositions = units.Position;
        Vector3[] velocities = units.Velocity;
        Vector3[] bodyNormals = units.BodyNormal;
        Vector3[] headNormals = units.HeadNormal;
        UnitPosture[] postures = units.Posture;
        UnitType[] unitTypes = units.Type;
        int[] bodyStarts = units.BodyStart;
        short[] bodyCounts = units.BodyCount;

        Vector3[] localPositions = bodies.LocalPosition;
        Vector3[] worldPositions = bodies.WorldPosition;
        float[] tailAngles = bodies.TailAngle;
        bool[] tailInitialized = bodies.TailInitialized;
        float[] localRotationsX = bodies.LocalRotationX;
        float[] localRotationsY = bodies.LocalRotationY;
        float[] localRotations = bodies.LocalRotation;
        float[] worldRotations = bodies.WorldRotation;
        Quaternion[] worldOrientations = bodies.WorldOrientation;
        Vector3[] worldScales = bodies.WorldScale;
        float[] followDistances = bodies.FollowDistance;
        int[] parents = bodies.Parent;
        BodyPartMotion[] motions = bodies.Motion;
        BodyPartAnchor[] anchors = bodies.Anchor;
        BodyPartRole[] roles = bodies.Role;
        string?[] partIds = bodies.PartId;
        BodyPartPivot[] pivots = bodies.Pivot;
        Vector3[] scales = bodies.Scale;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            int start = bodyStarts[unit];
            int count = bodyCounts[unit];

            if (count <= 0)
                continue;

            // Movement wins over a stationary/combat pose in the same frame.
            Vector3 velocity = velocities[unit];
            if (velocity.X * velocity.X + velocity.Y * velocity.Y > 0.04f &&
                postures[unit] != UnitPosture.Lying)
                postures[unit] = UnitPosture.Standing;

            UpdateBody(
                unit,
                start,
                count,
                unitPositions,
                bodyNormals,
                headNormals,
                velocities,
                unitTypes[unit],
                postures[unit],
                worldPositions,
                tailAngles,
                tailInitialized,
                worldRotations,
                worldOrientations,
                worldScales,
                localPositions,
                localRotationsX,
                localRotationsY,
                localRotations,
                parents,
                motions,
                anchors,
                roles,
                partIds,
                pivots,
                scales,
                followDistances,
                followAlpha,
                _animationTime,
                Math.Clamp(deltaTime, 0f, 0.05f));
        }
    }

    /// <summary>
    /// Rebuilds the authored base rig at each unit's current position without applying
    /// posture poses or articulated motion. Anatomy stays rigid in local space while
    /// the complete model follows the gameplay unit.
    /// </summary>
    public void UpdateStatic(UnitStore units, UnitBodyStore bodies)
    {
        if (units.ActiveCount == 0)
            return;

        ReadOnlySpan<int> active = units.ActiveIndices;
        Vector3[] unitPositions = units.Position;
        Vector3[] velocities = units.Velocity;
        Vector3[] bodyNormals = units.BodyNormal;
        Vector3[] headNormals = units.HeadNormal;
        int[] bodyStarts = units.BodyStart;
        short[] bodyCounts = units.BodyCount;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            int start = bodyStarts[unit];
            int count = bodyCounts[unit];

            if (start < 0 || count <= 0 || start + count > bodies.Count)
                continue;

            // Keep the body facing its travel direction, but never deform it for
            // walking, crouching, lying down, tail motion, or authored posture poses.
            Vector3 velocity = velocities[unit];
            if (velocity.X * velocity.X + velocity.Y * velocity.Y > 0.04f)
                bodyNormals[unit] = NormalizeHorizontal(velocity);

            Vector3 bodyNormal = NormalizeHorizontal(bodyNormals[unit]);
            Vector3 headNormal = NormalizeHorizontal(headNormals[unit]);

            bodies.SetInitialWorldPosition(
                new BodyHandle(start, count),
                unitPositions[unit],
                MathF.Atan2(bodyNormal.Y, bodyNormal.X),
                MathF.Atan2(headNormal.Y, headNormal.X));
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
        UnitType unitType,
        UnitPosture posture,
        Vector3[] worldPositions,
        float[] tailAngles,
        bool[] tailInitialized,
        float[] worldRotations,
        Quaternion[] worldOrientations,
        Vector3[] worldScales,
        Vector3[] localPositions,
        float[] localRotationsX,
        float[] localRotationsY,
        float[] localRotations,
        int[] parents,
        BodyPartMotion[] motions,
        BodyPartAnchor[] anchors,
        BodyPartRole[] roles,
        string?[] partIds,
        BodyPartPivot[] pivots,
        Vector3[] scales,
        float[] followDistances,
        float followAlpha,
        float animationTime,
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
        Quaternion bodyOrientation = BodyPartTransformMath.TopDownRotation(bodyRotation);
        Quaternion headOrientation = BodyPartTransformMath.TopDownRotation(headRotation);
        int end = start + count;
        IReadOnlyDictionary<string, UnitBodyPoseTransform>? savedPose =
            UnitBodyPoseSettingsStore.GetTransforms(unitType, posture);

        // Definitions are parent-first, so every parent's transform has
        // already been calculated when its children are visited.
        for (int part = start; part < end; part++)
        {
            int parent = parents[part];
            Vector3 localPosition = localPositions[part];
            Vector3 localEuler = new(
                localRotationsX[part],
                localRotationsY[part],
                localRotations[part]);
            UnitBodyPoseTransform savedTransform = default;
            bool hasSavedPose = savedPose != null &&
                !string.IsNullOrWhiteSpace(partIds[part]) &&
                savedPose.TryGetValue(partIds[part]!, out savedTransform);
            bool hasCustomPoseOffset = false;
            if (hasSavedPose)
            {
                if (savedTransform.IsOffset)
                {
                    localPosition += savedTransform.LocalPosition;
                    localEuler += savedTransform.LocalEulerRotation;
                    hasCustomPoseOffset =
                        savedTransform.LocalPosition.LengthSquared() > 0.0000001f ||
                        savedTransform.LocalEulerRotation.LengthSquared() > 0.0000001f;
                }
                else
                {
                    localPosition = savedTransform.LocalPosition;
                    localEuler = savedTransform.LocalEulerRotation;
                    hasCustomPoseOffset =
                        Vector3.DistanceSquared(localPosition, localPositions[part]) > 0.0000001f ||
                        Vector3.DistanceSquared(localEuler, new Vector3(
                            localRotationsX[part], localRotationsY[part], localRotations[part])) > 0.0000001f;
                }
            }
            if (posture == UnitPosture.Lying)
            {
                // Prone lowers every part centre (including head and organs),
                // compresses its vertical extent and lengthens the XY outline.
                float standingHeight = localPosition.Z;
                localPosition.X += (standingHeight - 0.62f) * 0.28f;
                localPosition.Y *= 1.12f;
                localPosition.Z = 0.025f + standingHeight * 0.18f;
            }

            // WalkCycle motions were present in the rig, but never evaluated.
            // Keep the animation layered on top of saved pose offsets; the opposing
            // variants make left/right limbs swing in alternating phases.
            if (speed > 0.05f)
            {
                float gaitAmount = Math.Clamp(speed / 2.8f, 0f, 1f);
                // Prone locomotion is a crawl: keep the prone silhouette and use
                // a shorter alternating limb cycle rather than snapping upright.
                if (posture == UnitPosture.Lying)
                    gaitAmount *= 0.42f;
                float gait = MathF.Sin(animationTime * (5.5f + speed * 0.75f)) * gaitAmount;
                switch (motions[part])
                {
                    case BodyPartMotion.WalkCycle:
                        localEuler.Z += gait * 0.48f;
                        break;
                    case BodyPartMotion.WalkCycleOpposite:
                        localEuler.Z -= gait * 0.48f;
                        break;
                    case BodyPartMotion.WalkCycleProfile:
                        localPosition.Y += gait * 0.025f;
                        localEuler.Z += gait * 0.34f;
                        break;
                    case BodyPartMotion.WalkCycleProfileOpposite:
                        localPosition.Y -= gait * 0.025f;
                        localEuler.Z -= gait * 0.34f;
                        break;
                }
            }

            Vector3 origin = unitPositions[unit];
            Quaternion positionOrientation = bodyOrientation;
            Quaternion partBaseOrientation = bodyOrientation;
            float partBaseRotation = bodyRotation;

            if (anchors[part] == BodyPartAnchor.UnitHead)
            {
                // Keep the head center anchored ahead of the torso even when
                // its facing direction changes independently.
                partBaseOrientation = headOrientation;
                partBaseRotation = headRotation;
            }
            else if (anchors[part] == BodyPartAnchor.Parent &&
                     parent >= start &&
                     (motions[part] is BodyPartMotion.SpinalChain or BodyPartMotion.PhysicalTail) &&
                     !hasCustomPoseOffset &&
                     posture != UnitPosture.Lying)
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
                worldOrientations[part] = BodyPartTransformMath.TopDownRotation(worldRotations[part]);
                worldScales[part] = BodyPartTransformMath.Project(
                    scales[part], worldOrientations[part], 0, 1).Scale;
                continue;
            }
            else if (anchors[part] == BodyPartAnchor.Parent &&
                     motions[part] == BodyPartMotion.FollowParent &&
                     parent >= start &&
                     !hasCustomPoseOffset &&
                     posture != UnitPosture.Lying)
            {
                UpdateFollower(
                    part,
                    parent,
                    worldPositions,
                    worldRotations,
                    followDistances,
                    followAlpha);
                worldOrientations[part] = BodyPartTransformMath.TopDownRotation(worldRotations[part]);
                worldScales[part] = BodyPartTransformMath.Project(
                    scales[part], worldOrientations[part], 0, 1).Scale;
                continue;
            }
            else if (anchors[part] == BodyPartAnchor.Parent &&
                     parent >= start)
            {
                origin = worldPositions[parent];
                positionOrientation = worldOrientations[parent];
                partBaseOrientation = worldOrientations[parent];
                partBaseRotation = worldRotations[parent];
            }

            Quaternion worldOrientation = BodyPartTransformMath.Compose(partBaseOrientation, localEuler);
            float worldRotation = BodyPartTransformMath.Project(
                scales[part], worldOrientation, 0, 1).Rotation;

            // Preserve unwrapped angular accumulation for a truly planar chain.
            // Quaternion projection returns an equivalent angle in [-PI, PI], which
            // is fine for rendering but causes discontinuities in chain/follower math.
            if (MathF.Abs(localEuler.X) < 0.0001f &&
                MathF.Abs(localEuler.Y) < 0.0001f &&
                MathF.Abs(partBaseOrientation.X) < 0.0001f &&
                MathF.Abs(partBaseOrientation.Y) < 0.0001f)
            {
                worldRotation = partBaseRotation + localEuler.Z;
            }

            Vector3 joint =
                origin + BodyPartTransformMath.TransformPosition(localPosition, positionOrientation);

            // Rotating a limb around its center makes the shoulder/hip drift
            // away from the torso. Start-pivot parts are instead positioned
            // from their proximal joint, then offset to their visual center.
            if (pivots[part] == BodyPartPivot.Start)
            {
                joint += BodyPartTransformMath.TransformPosition(
                    new Vector3(MathF.Abs(scales[part].X), 0f, 0f),
                    worldOrientation);
            }

            worldPositions[part] = joint;
            worldOrientations[part] = worldOrientation;
            Vector3 projectedScale = BodyPartTransformMath.Project(
                scales[part], worldOrientation, 0, 1).Scale;
            if (posture == UnitPosture.Lying)
                projectedScale.Z *= 0.55f;
            worldScales[part] = projectedScale;
            worldRotations[part] = worldRotation;
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
