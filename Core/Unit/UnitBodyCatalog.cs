using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Data-only anatomy profiles. Body-local +X is forward; +/-Y are the sides.
/// The colonist torso is centered on the unit marker, with the head forward
/// on the centerline and limbs placed symmetrically on both sides.
/// </summary>
internal static class UnitBodyCatalog
{
    private static readonly BodyPartDefinition[] Colonist =
    {
        // The torso origin is the unit marker center. +X is forward, +/-Y are the sides.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.13f, 0.095f, 0.065f),
            BodyPrimitive.Circle,
            role: BodyPartRole.Torso),

        new BodyPartDefinition(
            new Vector3(0.155f, 0f, 0f),
            new Vector3(0.055f, 0.055f, 0.045f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head),

        // Arms sit at equal and opposite offsets from the torso center.
        new BodyPartDefinition(
            new Vector3(0f, 0.105f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: MathF.PI * 0.5f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.025f, 0.025f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 2,
            role: BodyPartRole.Hand),

        new BodyPartDefinition(
            new Vector3(0f, -0.105f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: -MathF.PI * 0.5f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.UpperArm),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.025f, 0.025f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 4,
            role: BodyPartRole.Hand),

        // Legs extend slightly backward but sit symmetrically on either side.
        new BodyPartDefinition(
            new Vector3(-0.055f, 0.06f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: MathF.PI,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.03f, 0.022f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Foot),

        new BodyPartDefinition(
            new Vector3(-0.055f, -0.06f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: -MathF.PI,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.03f, 0.022f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 8,
            role: BodyPartRole.Foot)
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
    private static readonly BodyPartDefinition[] ScavenRat = CreateScavenRat();

    public static ReadOnlySpan<BodyPartDefinition> Get(UnitBodyType bodyType) =>
        bodyType switch
        {
            UnitBodyType.Colonist => Colonist,
            UnitBodyType.SmallCreature => SmallCreature,
            UnitBodyType.SegmentedCreature => SegmentedCreature,
            UnitBodyType.ScavenRat => ScavenRat,
            _ => throw new ArgumentOutOfRangeException(
                nameof(bodyType),
                bodyType,
                "Неизвестный тип тела.")
        };

    private static BodyPartDefinition[] CreateScavenRat()
    {
        // Body-local +X is forward. The rat is built entirely from outline-ready
        // primitives; the tail is a parent-following chain, not one rigid shape.
        return new BodyPartDefinition[]
        {
            new(Vector3.Zero, new Vector3(0.47f, 0.21f, 0.06f),
                BodyPrimitive.Circle, role: BodyPartRole.RatBody),

            new(new Vector3(-0.16f, 0f, 0f), new Vector3(0.23f, 0.18f, 0.05f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),

            new(new Vector3(0.38f, 0f, 0f), new Vector3(0.19f, 0.135f, 0.05f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.Head),

            // A long triangular wedge gives the muzzle its rat-like point.
            new(new Vector3(0.16f, 0f, 0f), new Vector3(0.20f, 0.073f, 0.025f),
                BodyPrimitive.Triangle, parentIndex: 2, role: BodyPartRole.Muzzle),

            new(new Vector3(0.335f, 0f, 0f), new Vector3(0.022f, 0.020f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Muzzle),

            new(new Vector3(-0.025f, 0.122f, 0f), new Vector3(0.075f, 0.066f, 0.025f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Ear),

            new(new Vector3(-0.025f, -0.122f, 0f), new Vector3(0.075f, 0.066f, 0.025f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Ear),

            new(new Vector3(0.055f, 0.103f, 0f), new Vector3(0.022f, 0.022f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Eye),

            new(new Vector3(0.055f, -0.103f, 0f), new Vector3(0.022f, 0.022f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Eye),

            new(new Vector3(0.058f, 0.103f, 0f), new Vector3(0.008f, 0.008f, 0.01f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Eye),

            new(new Vector3(0.058f, -0.103f, 0f), new Vector3(0.008f, 0.008f, 0.01f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Eye),

            // Whiskers fan out from both cheeks.
            new(new Vector3(0.17f, 0.055f, 0f), new Vector3(0.16f, 0.008f, 0.01f),
                BodyPrimitive.Line, parentIndex: 2, localRotation: 1.30f, role: BodyPartRole.Whisker),
            new(new Vector3(0.18f, 0.058f, 0f), new Vector3(0.17f, 0.008f, 0.01f),
                BodyPrimitive.Line, parentIndex: 2, localRotation: MathF.PI * 0.5f, role: BodyPartRole.Whisker),
            new(new Vector3(0.17f, 0.055f, 0f), new Vector3(0.16f, 0.008f, 0.01f),
                BodyPrimitive.Line, parentIndex: 2, localRotation: 1.85f, role: BodyPartRole.Whisker),

            new(new Vector3(0.17f, -0.055f, 0f), new Vector3(0.16f, 0.008f, 0.01f),
                BodyPrimitive.Line, parentIndex: 2, localRotation: -1.30f, role: BodyPartRole.Whisker),
            new(new Vector3(0.18f, -0.058f, 0f), new Vector3(0.17f, 0.008f, 0.01f),
                BodyPrimitive.Line, parentIndex: 2, localRotation: -MathF.PI * 0.5f, role: BodyPartRole.Whisker),
            new(new Vector3(0.17f, -0.055f, 0f), new Vector3(0.16f, 0.008f, 0.01f),
                BodyPrimitive.Line, parentIndex: 2, localRotation: -1.85f, role: BodyPartRole.Whisker),

            // Four legs swing from the body and carry a small outlined paw.
            new(new Vector3(0.15f, 0.145f, 0f), new Vector3(0.11f, 0.012f, 0.015f),
                BodyPrimitive.Line, parentIndex: 0, localRotation: 1.28f,
                motion: BodyPartMotion.WalkCycle, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start),
            new(new Vector3(0.15f, -0.145f, 0f), new Vector3(0.11f, 0.012f, 0.015f),
                BodyPrimitive.Line, parentIndex: 0, localRotation: -1.28f,
                motion: BodyPartMotion.WalkCycleOpposite, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start),
            new(new Vector3(-0.22f, 0.145f, 0f), new Vector3(0.105f, 0.012f, 0.015f),
                BodyPrimitive.Line, parentIndex: 0, localRotation: 2.78f,
                motion: BodyPartMotion.WalkCycleOpposite, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start),
            new(new Vector3(-0.22f, -0.145f, 0f), new Vector3(0.105f, 0.012f, 0.015f),
                BodyPrimitive.Line, parentIndex: 0, localRotation: -2.78f,
                motion: BodyPartMotion.WalkCycle, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start),

            new(new Vector3(0.11f, 0f, 0f), new Vector3(0.038f, 0.023f, 0.015f),
                BodyPrimitive.Circle, parentIndex: 17, role: BodyPartRole.Foot),
            new(new Vector3(0.11f, 0f, 0f), new Vector3(0.038f, 0.023f, 0.015f),
                BodyPrimitive.Circle, parentIndex: 18, role: BodyPartRole.Foot),
            new(new Vector3(0.105f, 0f, 0f), new Vector3(0.038f, 0.023f, 0.015f),
                BodyPrimitive.Circle, parentIndex: 19, role: BodyPartRole.Foot),
            new(new Vector3(0.105f, 0f, 0f), new Vector3(0.038f, 0.023f, 0.015f),
                BodyPrimitive.Circle, parentIndex: 20, role: BodyPartRole.Foot),

            // The tail base is attached to the rump. Each following segment
            // tracks its parent with smoothing, so it bends after turns.
            new(new Vector3(-0.43f, 0f, 0f), new Vector3(0.17f, 0.065f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 0, localRotation: MathF.PI, role: BodyPartRole.Tail),
            new(new Vector3(0.16f, 0f, 0f), new Vector3(0.15f, 0.055f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 25, motion: BodyPartMotion.FollowParent,
                followDistance: 0.16f, role: BodyPartRole.Tail),
            new(new Vector3(-0.14f, 0f, 0f), new Vector3(0.13f, 0.046f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 26, motion: BodyPartMotion.FollowParent,
                followDistance: 0.14f, role: BodyPartRole.Tail),
            new(new Vector3(-0.12f, 0f, 0f), new Vector3(0.11f, 0.037f, 0.02f),
                BodyPrimitive.Circle, parentIndex: 27, motion: BodyPartMotion.FollowParent,
                followDistance: 0.12f, role: BodyPartRole.Tail),
            new(new Vector3(-0.10f, 0f, 0f), new Vector3(0.085f, 0.027f, 0.015f),
                BodyPrimitive.Circle, parentIndex: 28, motion: BodyPartMotion.FollowParent,
                followDistance: 0.10f, role: BodyPartRole.Tail),
            new(new Vector3(-0.075f, 0f, 0f), new Vector3(0.05f, 0.018f, 0.01f),
                BodyPrimitive.Circle, parentIndex: 29, motion: BodyPartMotion.FollowParent,
                followDistance: 0.075f, role: BodyPartRole.Tail)
        };
    }

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
