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
        var parts = new System.Collections.Generic.List<BodyPartDefinition>(14);

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

        // A compact rat silhouette: one skull, a pointed muzzle, two body
        // sections and a thin chain of small tail beads. No limbs or ornaments.
        Add(0.30f, 0f, 0.095f, -1, BodyPartRole.Head,
            anchor: BodyPartAnchor.UnitHead);                         // 0 skull
        Add(0.105f, 0f, 0.045f, 0, BodyPartRole.Muzzle);              // 1 snout
        Add(0.090f, 0f, 0.014f, 1, BodyPartRole.Muzzle);              // 2 nose tip

        Add(-0.175f, 0f, 0.125f, 0, BodyPartRole.RatBody,
            BodyPartMotion.SpinalChain, 0.175f);                      // 3 shoulders
        Add(-0.180f, 0f, 0.092f, 3, BodyPartRole.RatBody,
            BodyPartMotion.SpinalChain, 0.180f);                      // 4 rump

        Add(-0.100f, 0f, 0.035f, 4, BodyPartRole.Tail,
            BodyPartMotion.PhysicalTail, 0.100f);                     // 5 tail root

        float[] tailRadii = { 0.030f, 0.026f, 0.022f, 0.018f, 0.014f, 0.011f, 0.008f, 0.005f };
        float[] tailDistances = { 0.052f, 0.048f, 0.044f, 0.040f, 0.035f, 0.030f, 0.024f, 0.018f };

        for (int i = 0; i < tailRadii.Length; i++)
        {
            Add(-tailDistances[i], 0f, tailRadii[i], parts.Count - 1,
                BodyPartRole.Tail, BodyPartMotion.PhysicalTail, tailDistances[i]);
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
