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
            role: BodyPartRole.Torso,
            damagePart: UnitHealthPartId.Torso),

        new BodyPartDefinition(
            new Vector3(0.155f, 0f, 0f),
            new Vector3(0.055f, 0.055f, 0.045f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head,
            damagePart: UnitHealthPartId.Head),

        // Arms sit at equal and opposite offsets from the torso center.
        new BodyPartDefinition(
            new Vector3(0f, 0.105f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: MathF.PI * 0.5f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.UpperArm,
            damagePart: UnitHealthPartId.RightArm),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.025f, 0.025f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 2,
            role: BodyPartRole.Hand,
            damagePart: UnitHealthPartId.RightHand),

        new BodyPartDefinition(
            new Vector3(0f, -0.105f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: -MathF.PI * 0.5f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.UpperArm,
            damagePart: UnitHealthPartId.LeftArm),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.025f, 0.025f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 4,
            role: BodyPartRole.Hand,
            damagePart: UnitHealthPartId.LeftHand),

        // Legs extend slightly backward but sit symmetrically on either side.
        new BodyPartDefinition(
            new Vector3(-0.055f, 0.06f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: MathF.PI,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            damagePart: UnitHealthPartId.RightLeg),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.03f, 0.022f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Foot,
            damagePart: UnitHealthPartId.RightFoot),

        new BodyPartDefinition(
            new Vector3(-0.055f, -0.06f, 0f),
            new Vector3(0.085f, 0.03f, 0.032f),
            BodyPrimitive.Circle,
            parentIndex: 0,
            localRotation: -MathF.PI,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            damagePart: UnitHealthPartId.LeftLeg),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.03f, 0.022f, 0.025f),
            BodyPrimitive.Circle,
            parentIndex: 8,
            role: BodyPartRole.Foot,
            damagePart: UnitHealthPartId.LeftFoot)
    };

    private static readonly BodyPartDefinition[] SmallCreature =
    {
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.18f, 0.105f, 0.15f),
            BodyPrimitive.Circle,
            role: BodyPartRole.Generic,
            damagePart: UnitHealthPartId.WholeBody),

        new BodyPartDefinition(
            new Vector3(0.18f, 0f, 0f),
            new Vector3(0.052f, 0.050f, 0.052f),
            BodyPrimitive.Circle,
            anchor: BodyPartAnchor.UnitHead,
            role: BodyPartRole.Head,
            damagePart: UnitHealthPartId.WholeBody),

        // Legs are kept under the broad body; only small paws peek out from the silhouette.
        new BodyPartDefinition(
            new Vector3(0.11f, 0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: 0.60f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            damagePart: UnitHealthPartId.WholeBody,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 2,
            role: BodyPartRole.Foot,
            damagePart: UnitHealthPartId.WholeBody),

        new BodyPartDefinition(
            new Vector3(0.11f, -0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -0.60f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            damagePart: UnitHealthPartId.WholeBody,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 4,
            role: BodyPartRole.Foot,
            damagePart: UnitHealthPartId.WholeBody),

        new BodyPartDefinition(
            new Vector3(-0.14f, 0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI - 0.60f,
            motion: BodyPartMotion.WalkCycleOpposite,
            role: BodyPartRole.Thigh,
            damagePart: UnitHealthPartId.WholeBody,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 6,
            role: BodyPartRole.Foot,
            damagePart: UnitHealthPartId.WholeBody),

        new BodyPartDefinition(
            new Vector3(-0.14f, -0.055f, 0f),
            new Vector3(0.030f, 0.012f, 0.028f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI + 0.60f,
            motion: BodyPartMotion.WalkCycle,
            role: BodyPartRole.Thigh,
            damagePart: UnitHealthPartId.WholeBody,
            pivot: BodyPartPivot.Start),

        new BodyPartDefinition(
            new Vector3(0.030f, 0f, 0f),
            new Vector3(0.014f, 0.014f, 0.014f),
            BodyPrimitive.Circle,
            parentIndex: 8,
            role: BodyPartRole.Foot,
            damagePart: UnitHealthPartId.WholeBody)
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
            role: BodyPartRole.Segment,
            damagePart: UnitHealthPartId.WholeBody);

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
                role: BodyPartRole.Segment,
                damagePart: UnitHealthPartId.WholeBody);
        }

        return parts;
    }
}
