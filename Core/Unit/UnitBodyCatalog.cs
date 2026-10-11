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
        // Body-local +X is forward. Parts remain connected and have distinct anatomical positions.
        new BodyPartDefinition(Vector3.Zero, new Vector3(0.22f, 0.15f, 0.12f), BodyPrimitive.Circle, role: BodyPartRole.Torso),
        new BodyPartDefinition(new Vector3(0.205f, 0f, 0f), new Vector3(0.045f, 0.045f, 0.045f), BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.Neck),
        new BodyPartDefinition(new Vector3(0.32f, 0f, 0f), new Vector3(0.09f, 0.09f, 0.09f), BodyPrimitive.Circle, parentIndex: 1, anchor: BodyPartAnchor.UnitHead, role: BodyPartRole.Head),

        // Shoulder -> upper arm -> elbow -> forearm -> hand.
        new BodyPartDefinition(new Vector3(0.055f, 0.13f, 0f), new Vector3(0.11f, 0.045f, 0.05f), BodyPrimitive.Circle, parentIndex: 0, localRotation: 1.15f, motion: BodyPartMotion.WalkCycleOpposite, role: BodyPartRole.UpperArm, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.11f, 0f, 0f), new Vector3(0.035f, 0.035f, 0.035f), BodyPrimitive.Circle, parentIndex: 3, role: BodyPartRole.Elbow),
        new BodyPartDefinition(Vector3.Zero, new Vector3(0.14f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 4, localRotation: -2.23f, role: BodyPartRole.Forearm, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.14f, 0f, 0f), new Vector3(0.04f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 5, role: BodyPartRole.Hand),

        new BodyPartDefinition(new Vector3(0.055f, -0.13f, 0f), new Vector3(0.11f, 0.045f, 0.05f), BodyPrimitive.Circle, parentIndex: 0, localRotation: -1.15f, motion: BodyPartMotion.WalkCycle, role: BodyPartRole.UpperArm, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.11f, 0f, 0f), new Vector3(0.035f, 0.035f, 0.035f), BodyPrimitive.Circle, parentIndex: 7, role: BodyPartRole.Elbow),
        new BodyPartDefinition(Vector3.Zero, new Vector3(0.14f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 8, localRotation: 2.23f, role: BodyPartRole.Forearm, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.14f, 0f, 0f), new Vector3(0.04f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 9, role: BodyPartRole.Hand),

        // Hips -> thighs -> knees -> shins -> feet. Legs trail behind the torso.
        new BodyPartDefinition(new Vector3(-0.12f, 0.065f, 0f), new Vector3(0.10f, 0.06f, 0.055f), BodyPrimitive.Circle, parentIndex: 0, localRotation: MathF.PI - 0.58f, motion: BodyPartMotion.WalkCycle, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.10f, 0f, 0f), new Vector3(0.035f, 0.035f, 0.035f), BodyPrimitive.Circle, parentIndex: 11, role: BodyPartRole.Knee),
        new BodyPartDefinition(Vector3.Zero, new Vector3(0.12f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 12, localRotation: 0.63f, role: BodyPartRole.Shin, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.12f, 0f, 0f), new Vector3(0.06f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 13, localRotation: 0.10f, role: BodyPartRole.Foot, pivot: BodyPartPivot.Start),

        new BodyPartDefinition(new Vector3(-0.12f, -0.065f, 0f), new Vector3(0.10f, 0.06f, 0.055f), BodyPrimitive.Circle, parentIndex: 0, localRotation: -MathF.PI + 0.58f, motion: BodyPartMotion.WalkCycleOpposite, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.10f, 0f, 0f), new Vector3(0.035f, 0.035f, 0.035f), BodyPrimitive.Circle, parentIndex: 15, role: BodyPartRole.Knee),
        new BodyPartDefinition(Vector3.Zero, new Vector3(0.12f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 16, localRotation: -0.63f, role: BodyPartRole.Shin, pivot: BodyPartPivot.Start),
        new BodyPartDefinition(new Vector3(0.12f, 0f, 0f), new Vector3(0.06f, 0.04f, 0.04f), BodyPrimitive.Circle, parentIndex: 17, localRotation: -0.10f, role: BodyPartRole.Foot, pivot: BodyPartPivot.Start)
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
