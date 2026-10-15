using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>Shared default pose math used by the editor and live simulation.</summary>
public static class BodyPartPoseMath
{
    public static BodyPartDefinition ForPosture(
        BodyPartDefinition source,
        UnitPosture posture)
    {
        Vector3 position = source.LocalPosition;
        float rotation = source.LocalRotation;
        ApplyDefaultPose(posture, source.Role, source.Pivot, ref position, ref rotation);

        return new BodyPartDefinition(
            position,
            source.Scale,
            source.Primitive,
            parentIndex: source.ParentIndex,
            localRotation: rotation,
            motion: source.Motion,
            followDistance: source.FollowDistance,
            anchor: source.Anchor,
            role: source.Role,
            pivot: source.Pivot,
            localRotationX: source.LocalRotationX,
            localRotationY: source.LocalRotationY);
    }

    public static void ApplyDefaultPose(
        UnitPosture posture,
        BodyPartRole role,
        BodyPartPivot pivot,
        ref Vector3 localPosition,
        ref Vector3 localEuler)
    {
        float rotation = localEuler.Z;
        ApplyDefaultPose(posture, role, pivot, ref localPosition, ref rotation);
        localEuler.Z = rotation;
    }

    public static void ApplyDefaultPose(
        UnitPosture posture,
        BodyPartRole role,
        BodyPartPivot pivot,
        ref Vector3 localPosition,
        ref float localRotation)
    {
        if (posture == UnitPosture.Standing)
            return;

        // Child joint offsets stay fixed to preserve the parent-child chain.
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

        // Prone is an overhead silhouette, not a sideways character rotation.
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

    public static BodyPartDefinition WithTransform(
        BodyPartDefinition source,
        Vector3 localPosition,
        Vector3 localEuler) =>
        new(
            localPosition,
            source.Scale,
            source.Primitive,
            parentIndex: source.ParentIndex,
            localRotation: localEuler.Z,
            motion: source.Motion,
            followDistance: source.FollowDistance,
            anchor: source.Anchor,
            role: source.Role,
            pivot: source.Pivot,
            localRotationX: localEuler.X,
            localRotationY: localEuler.Y);

    public static Vector3 NormalizeEulerDelta(Vector3 delta) =>
        new(
            WrapAngle(delta.X),
            WrapAngle(delta.Y),
            WrapAngle(delta.Z));

    public static float WrapAngle(float angle) =>
        MathF.IEEERemainder(angle, MathF.Tau);
}
