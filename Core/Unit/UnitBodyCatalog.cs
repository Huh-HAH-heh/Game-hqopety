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
        var parts = new System.Collections.Generic.List<BodyPartDefinition>(59);

        void Add(
            float x,
            float y,
            float radius,
            int parent = -1,
            BodyPartRole role = BodyPartRole.RatBody,
            BodyPartMotion motion = BodyPartMotion.StaticRelative,
            float followDistance = 0f,
            BodyPartAnchor anchor = BodyPartAnchor.Parent)
        {
            parts.Add(new BodyPartDefinition(
                new Vector3(x, y, 0f),
                new Vector3(radius, radius, radius),
                BodyPrimitive.Circle,
                parentIndex: parent,
                motion: motion,
                followDistance: followDistance,
                anchor: anchor,
                role: role));
        }

        // Top-down silhouette. +X is forward; +/-Y are the two sides.
        // Z is deliberately flat so the renderer can never turn this into a side view.
        Add(0f, 0f, 0.25f);                                        // 0 torso
        Add(-0.16f, 0f, 0.24f, 0, BodyPartRole.RatBody);           // 1 rump
        Add(-0.13f, 0.105f, 0.22f, 0, BodyPartRole.RatBody);       // 2 back left
        Add(-0.13f, -0.105f, 0.22f, 0, BodyPartRole.RatBody);      // 3 back right
        Add(0.16f, 0f, 0.22f, 0, BodyPartRole.RatBody);            // 4 shoulders
        Add(0.09f, 0.11f, 0.16f, 0, BodyPartRole.RatBody);         // 5 left chest
        Add(0.09f, -0.11f, 0.16f, 0, BodyPartRole.RatBody);        // 6 right chest
        Add(0.28f, 0f, 0.14f, 0, BodyPartRole.Neck);               // 7 neck

        Add(0.39f, 0f, 0.20f, -1, BodyPartRole.Head,
            anchor: BodyPartAnchor.UnitHead);                     // 8 head
        Add(0.015f, 0.085f, 0.105f, 8, BodyPartRole.Muzzle);       // 9 cheek
        Add(0.015f, -0.085f, 0.105f, 8, BodyPartRole.Muzzle);      // 10 cheek
        Add(0.16f, 0f, 0.12f, 8, BodyPartRole.Muzzle);             // 11 snout
        Add(0.10f, 0f, 0.045f, 11, BodyPartRole.Muzzle);           // 12 nose
        Add(0.07f, 0.035f, 0.065f, 11, BodyPartRole.Muzzle);       // 13 jaw left
        Add(0.07f, -0.035f, 0.065f, 11, BodyPartRole.Muzzle);      // 14 jaw right

        Add(-0.105f, 0.14f, 0.095f, 8, BodyPartRole.Ear);          // 15 ear
        Add(0.015f, 0.005f, 0.045f, 15, BodyPartRole.Ear);         // 16 inner ear
        Add(-0.105f, -0.14f, 0.095f, 8, BodyPartRole.Ear);         // 17 ear
        Add(0.015f, -0.005f, 0.045f, 17, BodyPartRole.Ear);        // 18 inner ear
        Add(0.06f, 0.095f, 0.038f, 8, BodyPartRole.Eye);           // 19 eye
        Add(0.018f, 0.002f, 0.018f, 19, BodyPartRole.Eye);         // 20 pupil
        Add(0.06f, -0.095f, 0.038f, 8, BodyPartRole.Eye);          // 21 eye
        Add(0.018f, -0.002f, 0.018f, 21, BodyPartRole.Eye);        // 22 pupil
        Add(0.16f, 0.07f, 0.014f, 8, BodyPartRole.Whisker);        // 23 whisker root
        Add(0.17f, 0.11f, 0.012f, 8, BodyPartRole.Whisker);        // 24 whisker
        Add(0.16f, -0.07f, 0.014f, 8, BodyPartRole.Whisker);       // 25 whisker root
        Add(0.17f, -0.11f, 0.012f, 8, BodyPartRole.Whisker);       // 26 whisker

        // Front paws tucked in close to the chest, moving opposite each other.
        Add(0.07f, 0.18f, 0.12f, 0, BodyPartRole.UpperArm,
            BodyPartMotion.WalkCycleProfile);                      // 27 shoulder
        Add(0.09f, -0.045f, 0.09f, 27, BodyPartRole.Forearm,
            BodyPartMotion.WalkCycleProfile);                      // 28 forearm
        Add(0.075f, -0.02f, 0.06f, 28, BodyPartRole.Hand,
            BodyPartMotion.WalkCycleProfile);                      // 29 paw
        Add(0.07f, -0.18f, 0.12f, 0, BodyPartRole.UpperArm,
            BodyPartMotion.WalkCycleProfileOpposite);               // 30 shoulder
        Add(0.09f, 0.045f, 0.09f, 30, BodyPartRole.Forearm,
            BodyPartMotion.WalkCycleProfileOpposite);               // 31 forearm
        Add(0.075f, 0.02f, 0.06f, 31, BodyPartRole.Hand,
            BodyPartMotion.WalkCycleProfileOpposite);               // 32 paw

        // Exactly two rear legs with a small alternating stride.
        Add(-0.13f, 0.14f, 0.14f, 0, BodyPartRole.Thigh,
            BodyPartMotion.WalkCycleProfile);                       // 33 hip/thigh
        Add(-0.105f, 0.045f, 0.11f, 33, BodyPartRole.Knee,
            BodyPartMotion.WalkCycleProfileOpposite);               // 34 knee
        Add(-0.10f, 0.025f, 0.09f, 34, BodyPartRole.Shin,
            BodyPartMotion.WalkCycleProfile);                       // 35 lower leg
        Add(-0.045f, 0.02f, 0.075f, 35, BodyPartRole.Foot);         // 36 paw
        Add(-0.13f, -0.14f, 0.14f, 0, BodyPartRole.Thigh,
            BodyPartMotion.WalkCycleProfileOpposite);               // 37 hip/thigh
        Add(-0.105f, -0.045f, 0.11f, 37, BodyPartRole.Knee,
            BodyPartMotion.WalkCycleProfile);                       // 38 knee
        Add(-0.10f, -0.025f, 0.09f, 38, BodyPartRole.Shin,
            BodyPartMotion.WalkCycleProfileOpposite);               // 39 lower leg
        Add(-0.045f, -0.02f, 0.075f, 39, BodyPartRole.Foot);        // 40 paw

        // Verlet-like tail chain: each circle keeps its own inertia and a fixed
        // link length, so it swings wide after turns instead of teleporting
        // into a smoothed straight line behind the body.
        Add(-0.20f, 0f, 0.115f, 1, BodyPartRole.Tail,
            BodyPartMotion.PhysicalTail, 0.20f);                    // 41 tail root
        float[] tailRadii =
        {
            0.105f, 0.098f, 0.092f, 0.086f, 0.080f, 0.074f,
            0.068f, 0.062f, 0.056f, 0.050f, 0.044f, 0.039f,
            0.034f, 0.030f, 0.026f, 0.022f, 0.018f
        };

        for (int i = 0; i < tailRadii.Length; i++)
        {
            float curve = (i % 4) switch
            {
                0 => 0.012f,
                1 => 0.004f,
                2 => -0.009f,
                _ => -0.003f
            };

            Add(-0.115f, curve, tailRadii[i], parts.Count - 1,
                BodyPartRole.Tail, BodyPartMotion.PhysicalTail, 0.115f);
        }

        return parts.ToArray();
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
