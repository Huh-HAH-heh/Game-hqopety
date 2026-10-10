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
        // Overhead human: head forward, arms out to the sides, legs trailing.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.105f, 0.052f, 0.12f),
            BodyPrimitive.Box,
            role: BodyPartRole.Torso),

        new BodyPartDefinition(
            new Vector3(0.105f, 0f, 0f),
            new Vector3(0.030f, 0.020f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            role: BodyPartRole.Neck),

        new BodyPartDefinition(
            new Vector3(0.195f, 0f, 0f),
            new Vector3(0.055f, 0.052f, 0.052f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Near-side arm: shoulder -> outward elbow -> forward-curving forearm -> hand.
        new BodyPartDefinition(
            new Vector3(0.005f, 0.046f, 0f),
            new Vector3(0.045f, 0.018f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 1.20f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.042f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 3,
            role: BodyPartRole.Elbow),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.044f, 0.017f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 4,
            localRotation: -0.58f,
            role: BodyPartRole.Forearm),

        new BodyPartDefinition(
            new Vector3(0.040f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 5,
            role: BodyPartRole.Hand),

        // Far-side arm mirrors the near side.
        new BodyPartDefinition(
            new Vector3(0.005f, -0.046f, 0f),
            new Vector3(0.045f, 0.018f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -1.20f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.042f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 7,
            role: BodyPartRole.Elbow),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.044f, 0.017f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 8,
            localRotation: 0.58f,
            role: BodyPartRole.Forearm),

        new BodyPartDefinition(
            new Vector3(0.040f, 0f, 0f),
            new Vector3(0.019f, 0.019f, 0.019f),
            BodyPrimitive.Circle,
            parentIndex: 9,
            role: BodyPartRole.Hand),

        // Near-side leg: thigh, knee, shin and foot, all behind the torso.
        new BodyPartDefinition(
            new Vector3(-0.062f, 0.030f, 0f),
            new Vector3(0.052f, 0.021f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.12f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.045f, 0f, 0f),
            new Vector3(0.020f, 0.020f, 0.020f),
            BodyPrimitive.Circle,
            parentIndex: 11,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.048f, 0.018f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 12,
            role: BodyPartRole.Shin),

        new BodyPartDefinition(
            new Vector3(0.043f, 0f, 0f),
            new Vector3(0.030f, 0.020f, 0.030f),
            BodyPrimitive.Box,
            parentIndex: 13,
            role: BodyPartRole.Foot),

        // Far-side leg mirrors the near side.
        new BodyPartDefinition(
            new Vector3(-0.062f, -0.030f, 0f),
            new Vector3(0.052f, 0.021f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.12f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.045f, 0f, 0f),
            new Vector3(0.020f, 0.020f, 0.020f),
            BodyPrimitive.Circle,
            parentIndex: 15,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.048f, 0.018f, 0.038f),
            BodyPrimitive.Box,
            parentIndex: 16,
            role: BodyPartRole.Shin),

        new BodyPartDefinition(
            new Vector3(0.043f, 0f, 0f),
            new Vector3(0.030f, 0.020f, 0.030f),
            BodyPrimitive.Box,
            parentIndex: 17,
            role: BodyPartRole.Foot)
    };

    private static readonly BodyPartDefinition[] SmallCreature =
    {
        // Body and forward head.
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

        // Four short legs with alternating phases; paws are separate parts.
        new BodyPartDefinition(
            new Vector3(0.065f, 0.075f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 0.80f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.047f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 2,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(0.065f, -0.075f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -0.80f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.047f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 4,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.075f, 0.075f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.80f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.047f, 0f, 0f),
            new Vector3(0.026f, 0.020f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.075f, -0.075f, 0f),
            new Vector3(0.052f, 0.019f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.80f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.047f, 0f, 0f),
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
