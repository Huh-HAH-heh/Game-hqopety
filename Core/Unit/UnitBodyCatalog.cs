using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Data-only anatomy profiles for built-in species. The renderer and
/// transform system never branch on a species name.
/// Body local +X is the forward direction; +Y/-Y are the two sides.
/// </summary>
internal static class UnitBodyCatalog
{
    private static readonly BodyPartDefinition[] Colonist =
    {
        // Torso: compact capsule, not a broad rectangular "T-pose" hub.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.15f, 0.078f, 0.15f),
            BodyPrimitive.Box,
            role: BodyPartRole.Torso),

        // Head is controlled by the aim/head direction, independently
        // of the body, and sits in front of the torso.
        new BodyPartDefinition(
            new Vector3(0.235f, 0f, 0f),
            new Vector3(0.068f, 0.064f, 0.064f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Left/top arm: upper arm angles forward and inward; the forearm
        // bends back toward the center, so the pawn does not form a star.
        new BodyPartDefinition(
            new Vector3(0f, 0.064f, 0f),
            new Vector3(0.075f, 0.024f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -0.70f,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.065f, 0f, 0f),
            new Vector3(0.064f, 0.021f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 2,
            localRotation: 0.40f,
            role: BodyPartRole.Forearm),

        new BodyPartDefinition(
            new Vector3(0.058f, 0f, 0f),
            new Vector3(0.026f, 0.026f, 0.026f),
            BodyPrimitive.Circle,
            parentIndex: 3,
            role: BodyPartRole.Hand),

        // Right/bottom arm mirrors the left, maintaining a bent, compact
        // ready pose instead of extending both arms perpendicular to body.
        new BodyPartDefinition(
            new Vector3(0f, -0.064f, 0f),
            new Vector3(0.075f, 0.024f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 0.70f,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.065f, 0f, 0f),
            new Vector3(0.064f, 0.021f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 5,
            localRotation: -0.40f,
            role: BodyPartRole.Forearm),

        new BodyPartDefinition(
            new Vector3(0.058f, 0f, 0f),
            new Vector3(0.026f, 0.026f, 0.026f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Hand),

        // Left/top leg: two segments and a small foot. The natural pose
        // keeps the legs behind the torso with a little outward angle.
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
            new Vector3(0.064f, 0.023f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 8,
            role: BodyPartRole.Shin),

        new BodyPartDefinition(
            new Vector3(0.054f, 0f, 0f),
            new Vector3(0.040f, 0.023f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 9,
            role: BodyPartRole.Foot),

        // Right/bottom leg; phase is opposite to make movement readable.
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
            new Vector3(0.064f, 0.023f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 11,
            role: BodyPartRole.Shin),

        new BodyPartDefinition(
            new Vector3(0.054f, 0f, 0f),
            new Vector3(0.040f, 0.023f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 12,
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
