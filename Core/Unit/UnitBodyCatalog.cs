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
        static Vector3 Circle(float radius) => new(radius, radius, radius);

        // Side-profile Skaven: X is forward, Z is height. Every visible
        // primitive is a circle, including the muzzle, limbs and tapered tail.
        // The raised spine slopes down into the forward-thrust head.
        return new BodyPartDefinition[]
        {
            new(new Vector3(0f, 0f, 0.82f), Circle(0.34f),
                BodyPrimitive.Circle, role: BodyPartRole.RatBody),
            new(new Vector3(-0.18f, 0f, 0.02f), Circle(0.28f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),
            new(new Vector3(-0.10f, 0f, 0.23f), Circle(0.29f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),
            new(new Vector3(0.09f, 0f, 0.21f), Circle(0.28f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),
            new(new Vector3(0.22f, 0f, 0.09f), Circle(0.27f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),
            new(new Vector3(0.16f, 0f, -0.14f), Circle(0.22f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),
            new(new Vector3(-0.16f, 0f, -0.20f), Circle(0.22f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.RatBody),
            new(new Vector3(0.32f, 0f, 0.02f), Circle(0.18f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.Neck),

            // The skull sits forward and lower than the back hump.
            new(new Vector3(0.53f, 0f, 0.82f), Circle(0.235f),
                BodyPrimitive.Circle, anchor: BodyPartAnchor.UnitHead, role: BodyPartRole.Head),
            new(new Vector3(0.08f, 0f, -0.10f), Circle(0.14f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Head),
            new(new Vector3(0.21f, 0f, -0.07f), Circle(0.13f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Muzzle),
            new(new Vector3(0.115f, 0f, 0f), Circle(0.05f),
                BodyPrimitive.Circle, parentIndex: 10, role: BodyPartRole.Muzzle),
            new(new Vector3(0.17f, 0f, -0.15f), Circle(0.10f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Muzzle),

            // Rounded ears and simple ring-like eyes keep the primitive style.
            new(new Vector3(-0.12f, 0f, 0.20f), Circle(0.125f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Ear),
            new(new Vector3(0.015f, 0f, 0f), Circle(0.065f),
                BodyPrimitive.Circle, parentIndex: 13, role: BodyPartRole.Ear),
            new(new Vector3(-0.01f, 0f, 0.24f), Circle(0.105f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Ear),
            new(new Vector3(0.01f, 0f, 0f), Circle(0.052f),
                BodyPrimitive.Circle, parentIndex: 15, role: BodyPartRole.Ear),
            new(new Vector3(0.12f, 0f, 0.07f), Circle(0.045f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Eye),
            new(new Vector3(0.015f, 0f, 0f), Circle(0.020f),
                BodyPrimitive.Circle, parentIndex: 17, role: BodyPartRole.Eye),
            new(new Vector3(-0.02f, 0f, 0.08f), Circle(0.015f),
                BodyPrimitive.Circle, parentIndex: 10, role: BodyPartRole.Whisker),
            new(new Vector3(0.05f, 0f, 0.09f), Circle(0.013f),
                BodyPrimitive.Circle, parentIndex: 10, role: BodyPartRole.Whisker),
            new(new Vector3(0.10f, 0f, 0.04f), Circle(0.012f),
                BodyPrimitive.Circle, parentIndex: 10, role: BodyPartRole.Whisker),

            // Two tucked arms made from overlapping circles, with a restrained swing.
            new(new Vector3(0.25f, 0f, -0.03f), Circle(0.15f),
                BodyPrimitive.Circle, parentIndex: 0,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.UpperArm),
            new(new Vector3(0.12f, 0f, -0.10f), Circle(0.115f),
                BodyPrimitive.Circle, parentIndex: 22,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.Elbow),
            new(new Vector3(0.13f, 0f, -0.045f), Circle(0.08f),
                BodyPrimitive.Circle, parentIndex: 23,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.Hand),
            new(new Vector3(0.21f, 0f, 0.04f), Circle(0.13f),
                BodyPrimitive.Circle, parentIndex: 0,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.UpperArm),
            new(new Vector3(0.11f, 0f, -0.10f), Circle(0.10f),
                BodyPrimitive.Circle, parentIndex: 25,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.Elbow),
            new(new Vector3(0.12f, 0f, -0.035f), Circle(0.075f),
                BodyPrimitive.Circle, parentIndex: 26,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.Hand),
            new(new Vector3(0.05f, 0f, -0.025f), Circle(0.03f),
                BodyPrimitive.Circle, parentIndex: 27, role: BodyPartRole.Hand),

            // Only two hind legs. Feet lift alternately while the body remains hunched.
            new(new Vector3(-0.12f, 0f, -0.31f), Circle(0.19f),
                BodyPrimitive.Circle, parentIndex: 0,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.Thigh),
            new(new Vector3(0.12f, 0f, -0.15f), Circle(0.135f),
                BodyPrimitive.Circle, parentIndex: 29,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.Knee),
            new(new Vector3(-0.02f, 0f, -0.13f), Circle(0.105f),
                BodyPrimitive.Circle, parentIndex: 30,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.Shin),
            new(new Vector3(0.08f, 0f, -0.095f), Circle(0.085f),
                BodyPrimitive.Circle, parentIndex: 31,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.Foot),
            new(new Vector3(0.075f, 0f, 0f), Circle(0.055f),
                BodyPrimitive.Circle, parentIndex: 32, role: BodyPartRole.Foot),
            new(new Vector3(-0.26f, 0f, -0.30f), Circle(0.17f),
                BodyPrimitive.Circle, parentIndex: 0,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.Thigh),
            new(new Vector3(-0.02f, 0f, -0.14f), Circle(0.12f),
                BodyPrimitive.Circle, parentIndex: 34,
                motion: BodyPartMotion.WalkCycleProfile, role: BodyPartRole.Knee),
            new(new Vector3(0.04f, 0f, -0.13f), Circle(0.09f),
                BodyPrimitive.Circle, parentIndex: 35,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.Shin),
            new(new Vector3(0.08f, 0f, -0.09f), Circle(0.075f),
                BodyPrimitive.Circle, parentIndex: 36,
                motion: BodyPartMotion.WalkCycleProfileOpposite, role: BodyPartRole.Foot),
            new(new Vector3(0.065f, 0f, 0f), Circle(0.05f),
                BodyPrimitive.Circle, parentIndex: 37, role: BodyPartRole.Foot),

            // A long taper made only from circles; its slight downward curve follows the rump.
            new(new Vector3(-0.40f, 0f, -0.10f), Circle(0.13f),
                BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.Tail),
            new(new Vector3(-0.13f, 0f, -0.015f), Circle(0.115f),
                BodyPrimitive.Circle, parentIndex: 39, role: BodyPartRole.Tail),
            new(new Vector3(-0.13f, 0f, -0.012f), Circle(0.11f),
                BodyPrimitive.Circle, parentIndex: 40, role: BodyPartRole.Tail),
            new(new Vector3(-0.13f, 0f, -0.010f), Circle(0.10f),
                BodyPrimitive.Circle, parentIndex: 41, role: BodyPartRole.Tail),
            new(new Vector3(-0.12f, 0f, -0.010f), Circle(0.095f),
                BodyPrimitive.Circle, parentIndex: 42, role: BodyPartRole.Tail),
            new(new Vector3(-0.12f, 0f, -0.010f), Circle(0.09f),
                BodyPrimitive.Circle, parentIndex: 43, role: BodyPartRole.Tail),
            new(new Vector3(-0.12f, 0f, -0.008f), Circle(0.085f),
                BodyPrimitive.Circle, parentIndex: 44, role: BodyPartRole.Tail),
            new(new Vector3(-0.115f, 0f, -0.008f), Circle(0.08f),
                BodyPrimitive.Circle, parentIndex: 45, role: BodyPartRole.Tail),
            new(new Vector3(-0.11f, 0f, -0.006f), Circle(0.075f),
                BodyPrimitive.Circle, parentIndex: 46, role: BodyPartRole.Tail),
            new(new Vector3(-0.11f, 0f, -0.005f), Circle(0.07f),
                BodyPrimitive.Circle, parentIndex: 47, role: BodyPartRole.Tail),
            new(new Vector3(-0.105f, 0f, -0.005f), Circle(0.065f),
                BodyPrimitive.Circle, parentIndex: 48, role: BodyPartRole.Tail),
            new(new Vector3(-0.10f, 0f, -0.005f), Circle(0.06f),
                BodyPrimitive.Circle, parentIndex: 49, role: BodyPartRole.Tail),
            new(new Vector3(-0.095f, 0f, -0.003f), Circle(0.055f),
                BodyPrimitive.Circle, parentIndex: 50, role: BodyPartRole.Tail),
            new(new Vector3(-0.09f, 0f, -0.003f), Circle(0.05f),
                BodyPrimitive.Circle, parentIndex: 51, role: BodyPartRole.Tail),
            new(new Vector3(-0.08f, 0f, -0.002f), Circle(0.045f),
                BodyPrimitive.Circle, parentIndex: 52, role: BodyPartRole.Tail),
            new(new Vector3(-0.075f, 0f, -0.002f), Circle(0.04f),
                BodyPrimitive.Circle, parentIndex: 53, role: BodyPartRole.Tail),
            new(new Vector3(-0.065f, 0f, 0f), Circle(0.032f),
                BodyPrimitive.Circle, parentIndex: 54, role: BodyPartRole.Tail)
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
