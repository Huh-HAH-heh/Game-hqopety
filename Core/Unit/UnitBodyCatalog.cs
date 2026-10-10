using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Built-in anatomy layouts. Every unit type uses the same body-part
/// storage, transform, culling and render path; adding a type means adding
/// a definition here, not adding another renderer branch.
/// </summary>
internal static class UnitBodyCatalog
{
    private static readonly BodyPartDefinition[] Colonist =
    {
        // 0: torso, facing along the unit's body normal.
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.15f, 0.09f, 0.15f),
            BodyPrimitive.Box),

        // 1: head.
        new BodyPartDefinition(
            new Vector3(0.22f, 0f, 0f),
            new Vector3(0.085f, 0.085f, 0.085f),
            BodyPrimitive.Circle,
            parentIndex: 0),

        // 2-4: left upper arm, forearm, hand.
        new BodyPartDefinition(
            new Vector3(0f, 0.085f, 0f),
            new Vector3(0.075f, 0.027f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: MathF.PI * 0.5f),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.065f, 0.023f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 2),

        new BodyPartDefinition(
            new Vector3(0.07f, 0f, 0f),
            new Vector3(0.035f, 0.035f, 0.035f),
            BodyPrimitive.Circle,
            parentIndex: 3),

        // 5-7: right upper arm, forearm, hand.
        new BodyPartDefinition(
            new Vector3(0f, -0.085f, 0f),
            new Vector3(0.075f, 0.027f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0,
            localRotation: -MathF.PI * 0.5f),

        new BodyPartDefinition(
            new Vector3(0.085f, 0f, 0f),
            new Vector3(0.065f, 0.023f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 5),

        new BodyPartDefinition(
            new Vector3(0.07f, 0f, 0f),
            new Vector3(0.035f, 0.035f, 0.035f),
            BodyPrimitive.Circle,
            parentIndex: 6),

        // 8-10: left thigh, shin, foot. Legs extend behind the torso.
        new BodyPartDefinition(
            new Vector3(-0.17f, 0.045f, 0f),
            new Vector3(0.085f, 0.028f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0),

        new BodyPartDefinition(
            new Vector3(-0.13f, 0f, 0f),
            new Vector3(0.075f, 0.024f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 8),

        new BodyPartDefinition(
            new Vector3(-0.075f, 0f, 0f),
            new Vector3(0.045f, 0.025f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 9,
            localRotation: MathF.PI * 0.5f),

        // 11-13: right thigh, shin, foot.
        new BodyPartDefinition(
            new Vector3(-0.17f, -0.045f, 0f),
            new Vector3(0.085f, 0.028f, 0.05f),
            BodyPrimitive.Box,
            parentIndex: 0),

        new BodyPartDefinition(
            new Vector3(-0.13f, 0f, 0f),
            new Vector3(0.075f, 0.024f, 0.045f),
            BodyPrimitive.Box,
            parentIndex: 11),

        new BodyPartDefinition(
            new Vector3(-0.075f, 0f, 0f),
            new Vector3(0.045f, 0.025f, 0.035f),
            BodyPrimitive.Box,
            parentIndex: 12,
            localRotation: MathF.PI * 0.5f)
    };

    private static readonly BodyPartDefinition[] SmallCreature =
    {
        new BodyPartDefinition(
            Vector3.Zero,
            new Vector3(0.24f, 0.24f, 0.24f),
            BodyPrimitive.Circle)
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
            BodyPrimitive.Circle);

        for (int i = 1; i < parts.Length; i++)
        {
            float radius = 0.46f - (i - 1) * 0.025f;

            parts[i] = new BodyPartDefinition(
                new Vector3(-0.62f, 0f, 0f),
                new Vector3(radius, radius, radius),
                BodyPrimitive.Circle,
                parentIndex: i - 1,
                motion: BodyPartMotion.FollowParent,
                followDistance: 0.62f);
        }

        return parts;
    }
}
