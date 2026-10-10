using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Data-only anatomy profiles. Body local +X is forward; +Y/-Y are
/// the two sides. Colonists use a compact overhead pose: arms fan sideways,
/// legs trail behind the torso, and the head stays ahead of the chest.
/// </summary>
internal static class UnitBodyCatalog
{
    private static readonly BodyPartDefinition[] Colonist =
    {
        // Head is a separate look direction, but stays at the front of the torso.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.105f, 0.052f, 0.12f),
            BodyPrimitive.Box,
            role: BodyPartRole.Torso),

        // Neck starts at the front edge of the torso and extends toward the head.
        new BodyPartDefinition(
            new Vector3(0.075f, 0f, 0f),
            new Vector3(0.035f, 0.020f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            role: BodyPartRole.Neck,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.195f, 0f, 0f),
            new Vector3(0.055f, 0.052f, 0.052f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Arms are modeled from shoulder to elbow. Rotation happens around
        // each shoulder so the torso/limb joint cannot slide during animation.
        new BodyPartDefinition(
            new Vector3(0f, 0.038f, 0f),
            new Vector3(0.045f, 0.018f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 1.20f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.UpperArm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.045f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 3,
            role: BodyPartRole.Elbow),

        // The forearm starts at the elbow and bends forward/inward.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.044f, 0.017f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 4,
            localRotation: -1.75f,
            role: BodyPartRole.Forearm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.044f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 5,
            role: BodyPartRole.Hand),

        new BodyPartDefinition(
            new Vector3(0f, -0.038f, 0f),
            new Vector3(0.045f, 0.018f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -1.20f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.UpperArm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.045f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 7,
            role: BodyPartRole.Elbow),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.044f, 0.017f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 8,
            localRotation: 1.75f,
            role: BodyPartRole.Forearm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.044f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 9,
            role: BodyPartRole.Hand),

        // Legs start at the hips and extend backward to the knees.
        new BodyPartDefinition(
            new Vector3(-0.045f, 0.028f, 0f),
            new Vector3(0.052f, 0.021f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.12f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.052f, 0f, 0f),
            new Vector3(0.020f, 0.020f, 0.020f),
            BodyPrimitive.Circle,
            parentIndex: 11,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.046f, 0.018f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 12,
            localRotation: -0.20f,
            role: BodyPartRole.Shin,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.046f, 0f, 0f),
            new Vector3(0.030f, 0.020f, 0.030f),
            BodyPrimitive.Box,
            parentIndex: 13,
            localRotation: 0.12f,
            role: BodyPartRole.Foot,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(-0.045f, -0.028f, 0f),
            new Vector3(0.052f, 0.021f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.12f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.052f, 0f, 0f),
            new Vector3(0.020f, 0.020f, 0.020f),
            BodyPrimitive.Circle,
            parentIndex: 15,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.046f, 0.018f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 16,
            localRotation: 0.20f,
            role: BodyPartRole.Shin,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.046f, 0f, 0f),
            new Vector3(0.030f, 0.020f, 0.030f),
            BodyPrimitive.Box,
            parentIndex: 17,
            localRotation: -0.12f,
            role: BodyPartRole.Foot,
            pivot: BodyPartPivot.Start)
    };

    private static readonly BodyPartDefinition[] SmallCreature =
    {
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.18f, 0.105f, 0.15f),
            BodyPrimitive.Circle,
            role: BodyPartRole.Generic),

        new BodyPartDefinition(
            new Vector3(0.18f, 0f, 0f),
            new Vector3(0.058f, 0.055f, 0.055f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Four legs are positioned from their actual hips; paws sit at their ends.
        new BodyPartDefinition(
            new Vector3(0.07f, 0.060f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 0.65f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.052f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 2,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(0.07f, -0.060f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -0.65f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.052f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 4,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.10f, 0.060f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.65f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.052f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.10f, -0.060f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.65f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.052f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 8,
            role: BodyPartRole.Foot)
    };

    private static readonly BodyPartDefinition[] SegmentedCreature = CreateSegmented();

    public static ReadOnlySpan<BodyPartDefinition> Get(UnitBodyType bodyType) =>
        bodyType switch
        {
            UnitBodyType.Colonist => Colonist,
            UnitBodyType.SmallCreature => SmallCreature,
            UnitBodyType.SegmentedCreature => SegmentedCreature,
            _ => throw new ArgumentOutOfRangeException(
                nameof(bodyType),
                bodyType,
                "Неизвестный тип тела.")
        };

    private static BodyPartDefinition[] CreateSegmented()
    {
        var parts = new BodyPartDefinition[10];

        parts[0] = new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.52f, 0.52f, 0.45f),
            BodyPrimitive.Circle,
            role: BodyPartRole.Segment);

        for (int i = 1; i < parts.Length; i++)
        {
            float radius = 0.46f - (i - 1) * 0.025f;

            parts[i] = new BodyPartDefinition(
                new Vector3(-0.62f, 0f, 0f),
                new Vector3(radius, radius, radius),
                BodyPrimitive.Circle,
                parentIndex: i - 1,
                motion: BodyPartMotion.FollowParent,
                followDistance: 0.62f,
                role: BodyPartRole.Segment);
        }

        return parts;
    }
}
