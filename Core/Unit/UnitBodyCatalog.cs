using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Data-only anatomy profiles. Body local +X is forward; +Y/-Y are
/// the two sides. Every species uses the same transform and render path.
/// </summary>
internal static class UnitBodyCatalog
{
    private static readonly BodyPartDefinition[] Colonist =
    {
        // 0: torso.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.12f, 0.064f, 0.15f),
            BodyPrimitive.Box,
            role: BodyPartRole.Torso),

        // 1: short neck connects the chest to the independently aimed head.
        new BodyPartDefinition(
            new Vector3(0.135f, 0f, 0f),
            new Vector3(0.040f, 0.026f, 0.04f),
            BodyPrimitive.Box,
            parentIndex: 0,
            role: BodyPartRole.Neck),

        // 2: head. Its offset follows the head direction, not body heading.
        new BodyPartDefinition(
            new Vector3(0.235f, 0f, 0f),
            new Vector3(0.068f, 0.064f, 0.064f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // 3-6: left/top arm with a visible elbow bend and separate hand.
        new BodyPartDefinition(
            new Vector3(0f, 0.064f, 0f),
            new Vector3(0.075f, 0.024f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -0.35f,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.065f, 0f, 0f),
            new Vector3(0.024f, 0.024f, 0.024f),
            BodyPrimitive.Circle,
            parentIndex: 3,
            role: BodyPartRole.Elbow),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.064f, 0.021f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 4,
            localRotation: 0.70f,
            role: BodyPartRole.Forearm),

        new BodyPartDefinition(
            new Vector3(0.058f, 0f, 0f),
            new Vector3(0.026f, 0.026f, 0.026f),
            BodyPrimitive.Circle,
            parentIndex: 5,
            role: BodyPartRole.Hand),

        // 7-10: right/bottom arm mirrors the left.
        new BodyPartDefinition(
            new Vector3(0f, -0.064f, 0f),
            new Vector3(0.075f, 0.024f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 0.35f,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.065f, 0f, 0f),
            new Vector3(0.024f, 0.024f, 0.024f),
            BodyPrimitive.Circle,
            parentIndex: 7,
            role: BodyPartRole.Elbow),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.064f, 0.021f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 8,
            localRotation: -0.70f,
            role: BodyPartRole.Forearm),

        new BodyPartDefinition(
            new Vector3(0.058f, 0f, 0f),
            new Vector3(0.026f, 0.026f, 0.026f),
            BodyPrimitive.Circle,
            parentIndex: 9,
            role: BodyPartRole.Hand),

        // 11-14: left/top leg: thigh, knee, shin and trailing foot.
        new BodyPartDefinition(
            new Vector3(-0.095f, 0.035f, 0f),
            new Vector3(0.070f, 0.027f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.18f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.065f, 0f, 0f),
            new Vector3(0.025f, 0.025f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 11,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.064f, 0.023f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 12,
            role: BodyPartRole.Shin),

        new BodyPartDefinition(
            new Vector3(0.054f, 0f, 0f),
            new Vector3(0.040f, 0.023f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 13,
            role: BodyPartRole.Foot),

        // 15-18: right/bottom leg, driven with the opposite gait phase.
        new BodyPartDefinition(
            new Vector3(-0.095f, -0.035f, 0f),
            new Vector3(0.070f, 0.027f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.18f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.065f, 0f, 0f),
            new Vector3(0.025f, 0.025f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 15,
            role: BodyPartRole.Knee),

        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.064f, 0.023f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 16,
            role: BodyPartRole.Shin),

        new BodyPartDefinition(
            new Vector3(0.054f, 0f, 0f),
            new Vector3(0.040f, 0.023f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 17,
            role: BodyPartRole.Foot)
    };

    private static readonly BodyPartDefinition[] SmallCreature =
    {
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.24f, 0.24f, 0.24f),
            BodyPrimitive.Circle,
            role: BodyPartRole.Generic)
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
