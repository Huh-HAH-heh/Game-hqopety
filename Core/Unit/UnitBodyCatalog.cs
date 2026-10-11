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
        // Strict overhead silhouette: short trunk, head forward, limbs tucked under it.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.095f, 0.047f, 0.12f),
            BodyPrimitive.Box,
            role: BodyPartRole.Torso),

        new BodyPartDefinition(
            new Vector3(0.060f, 0f, 0f),
            new Vector3(0.028f, 0.013f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            role: BodyPartRole.Neck,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.155f, 0f, 0f),
            new Vector3(0.043f, 0.041f, 0.045f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Upper arms angle out only briefly, so the torso masks their shoulder ends.
        new BodyPartDefinition(
            new Vector3(0f, 0.026f, 0f),
            new Vector3(0.025f, 0.011f, 0.032f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 1.40f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.UpperArm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.025f, 0f, 0f),
            new Vector3(0.011f, 0.011f, 0.011f),
            BodyPrimitive.Circle,
            parentIndex: 3,
            role: BodyPartRole.Elbow),

        // Forearms fold back towards the body instead of forming an outstretched V.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.022f, 0.009f, 0.026f),
            BodyPrimitive.Box,
            parentIndex: 4,
            localRotation: -2.00f,
            role: BodyPartRole.Forearm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.022f, 0f, 0f),
            new Vector3(0.012f, 0.012f, 0.012f),
            BodyPrimitive.Circle,
            parentIndex: 5,
            role: BodyPartRole.Hand),

        new BodyPartDefinition(
            new Vector3(0f, -0.026f, 0f),
            new Vector3(0.025f, 0.011f, 0.032f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -1.40f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.UpperArm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.025f, 0f, 0f),
            new Vector3(0.011f, 0.011f, 0.011f),
            BodyPrimitive.Circle,
            parentIndex: 7,
            role: BodyPartRole.Elbow),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.022f, 0.009f, 0.026f),
            BodyPrimitive.Box,
            parentIndex: 8,
            localRotation: 2.00f,
            role: BodyPartRole.Forearm,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.022f, 0f, 0f),
            new Vector3(0.012f, 0.012f, 0.012f),
            BodyPrimitive.Circle,
            parentIndex: 9,
            role: BodyPartRole.Hand),

        // Short thighs remain underneath the trunk; only shin and foot tips emerge at the rear.
        new BodyPartDefinition(
            new Vector3(-0.045f, 0.020f, 0f),
            new Vector3(0.020f, 0.013f, 0.030f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.12f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.020f, 0f, 0f),
            new Vector3(0.013f, 0.013f, 0.013f),
            BodyPrimitive.Circle,
            parentIndex: 11,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.012f, 0.009f, 0.024f),
            BodyPrimitive.Box,
            parentIndex: 12,
            localRotation: 0.26f,
            role: BodyPartRole.Shin,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.012f, 0f, 0f),
            new Vector3(0.010f, 0.009f, 0.018f),
            BodyPrimitive.Box,
            parentIndex: 13,
            role: BodyPartRole.Foot,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(-0.045f, -0.020f, 0f),
            new Vector3(0.020f, 0.013f, 0.030f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.12f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.020f, 0f, 0f),
            new Vector3(0.013f, 0.013f, 0.013f),
            BodyPrimitive.Circle,
            parentIndex: 15,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.012f, 0.009f, 0.024f),
            BodyPrimitive.Box,
            parentIndex: 16,
            localRotation: -0.26f,
            role: BodyPartRole.Shin,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.012f, 0f, 0f),
            new Vector3(0.010f, 0.009f, 0.018f),
            BodyPrimitive.Box,
            parentIndex: 17,
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
            new Vector3(0.052f, 0.050f, 0.052f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Legs are kept under the broad body; only small paws peek out from the silhouette.
        new BodyPartDefinition(
            new Vector3(0.11f, 0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 0.60f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 2,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(0.11f, -0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -0.60f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 4,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.14f, 0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.60f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.14f, -0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.60f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
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
