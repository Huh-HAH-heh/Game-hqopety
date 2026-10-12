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
        var parts = new System.Collections.Generic.List<BodyPartDefinition>(31);

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

        // Clean top-down Skaven silhouette: only the circles needed to read
        // the rump, shoulders, head, tucked arms, two hind legs and tail.
        Add(0f, 0f, 0.25f);                                          // 0 torso
        Add(-0.17f, 0f, 0.22f, 0, BodyPartRole.RatBody);             // 1 rump
        Add(0.13f, 0f, 0.20f, 0, BodyPartRole.RatBody);              // 2 shoulders
        Add(0.39f, 0f, 0.19f, -1, BodyPartRole.Head,
            anchor: BodyPartAnchor.UnitHead);                        // 3 head
        Add(0.15f, 0f, 0.105f, 3, BodyPartRole.Muzzle);              // 4 snout
        Add(0.09f, 0f, 0.042f, 4, BodyPartRole.Muzzle);              // 5 nose
        Add(-0.10f, 0.13f, 0.075f, 3, BodyPartRole.Ear);             // 6 ear
        Add(-0.10f, -0.13f, 0.075f, 3, BodyPartRole.Ear);            // 7 ear
        Add(0.055f, 0.083f, 0.026f, 3, BodyPartRole.Eye);            // 8 eye
        Add(0.055f, -0.083f, 0.026f, 3, BodyPartRole.Eye);           // 9 eye

        // Front arms are tucked into the silhouette, not split into many joints.
        Add(0.06f, 0.16f, 0.105f, 0, BodyPartRole.UpperArm,
            BodyPartMotion.WalkCycleProfile);                        // 10 arm
        Add(0.10f, -0.035f, 0.067f, 10, BodyPartRole.Hand,
            BodyPartMotion.WalkCycleProfile);                         // 11 paw
        Add(0.06f, -0.16f, 0.105f, 0, BodyPartRole.UpperArm,
            BodyPartMotion.WalkCycleProfileOpposite);                 // 12 arm
        Add(0.10f, 0.035f, 0.067f, 12, BodyPartRole.Hand,
            BodyPartMotion.WalkCycleProfileOpposite);                 // 13 paw

        // Two hind legs: one large joint and one foot on each side.
        Add(-0.13f, 0.13f, 0.13f, 0, BodyPartRole.Thigh,
            BodyPartMotion.WalkCycleProfile);                         // 14 thigh
        Add(-0.12f, 0.045f, 0.078f, 14, BodyPartRole.Foot,
            BodyPartMotion.WalkCycleProfileOpposite);                 // 15 foot
        Add(-0.13f, -0.13f, 0.13f, 0, BodyPartRole.Thigh,
            BodyPartMotion.WalkCycleProfileOpposite);                 // 16 thigh
        Add(-0.12f, -0.045f, 0.078f, 16, BodyPartRole.Foot,
            BodyPartMotion.WalkCycleProfile);                         // 17 foot

        // A shorter, cleaner chain with progressive taper. Each link still
        // has inertia, length constraints and its own phase of the tail wave.
        Add(-0.20f, 0f, 0.105f, 1, BodyPartRole.Tail,
            BodyPartMotion.PhysicalTail, 0.20f);                      // 18 tail root
        float[] tailRadii =
        {
            0.092f, 0.082f, 0.072f, 0.062f, 0.052f, 0.043f,
            0.034f, 0.026f, 0.019f, 0.014f, 0.010f, 0.008f
        };

        for (int i = 0; i < tailRadii.Length; i++)
        {
            Add(-0.11f, 0f, tailRadii[i], parts.Count - 1,
                BodyPartRole.Tail, BodyPartMotion.PhysicalTail, 0.11f);
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
