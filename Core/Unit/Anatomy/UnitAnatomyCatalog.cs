using System;
using System.Numerics;
using Core.Items;

namespace Core.Unit;

/// <summary>
/// A complete creature definition. Stats, body rig, damageable tissues, organs,
/// and functional markers are authored together in the anatomy profile.
/// </summary>
public sealed class UnitArchetypeProfile
{
    private readonly UnitStartingEquipment[] _startingEquipment;

    public UnitDefinition Definition { get; }
    public UnitAnatomyProfile Anatomy { get; }
    public ReadOnlySpan<BodyPartDefinition> BodyParts => Anatomy.BodyParts;
    public ReadOnlySpan<UnitStartingEquipment> StartingEquipment => _startingEquipment;

    public UnitArchetypeProfile(
        UnitDefinition definition,
        UnitAnatomyProfile anatomy,
        UnitStartingEquipment[]? startingEquipment = null)
    {
        Definition = definition;
        Anatomy = anatomy ?? throw new ArgumentNullException(nameof(anatomy));
        if (!anatomy.HasSharedShapes)
            throw new ArgumentException("An archetype requires a shared render/hit shape for every node.", nameof(anatomy));

        _startingEquipment = startingEquipment == null
            ? Array.Empty<UnitStartingEquipment>()
            : (UnitStartingEquipment[])startingEquipment.Clone();
    }
}

public readonly struct UnitStartingEquipment
{
    public ArmorConfig Item { get; }
    public float Durability { get; }

    public UnitStartingEquipment(ArmorConfig item, float durability)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        if (!float.IsFinite(durability) || durability <= 0f)
            throw new ArgumentOutOfRangeException(nameof(durability));
        Durability = durability;
    }
}

/// <summary>
/// The single authoring catalog for unit archetypes. Legacy catalogs delegate here.
/// </summary>
public static class UnitAnatomyCatalog
{
    private static readonly UnitArchetypeProfile Colonist = new(
        new UnitDefinition(UnitType.Colonist, UnitBodyType.Colonist, 2.8f, 0.35f, 0.60f, 1.75f, 20f, 110f),
        new UnitAnatomyProfile(
            // These are the only three physical colonist nodes. Individual limbs and
            // organs stay as health metadata attached to the appropriate layer.
            Layer("body.lower",
                Part(V(-0.055f, 0f, 0.30f), V(0.105f, 0.085f, 0.30f),
                    BodyPrimitive.Circle, role: BodyPartRole.Thigh),
                0.34f,
                new[]
                {
                    Tissue(UnitHealthPartId.LeftLeg, 8f, 35f, "leg.left", parentId: "torso.main"),
                    Tissue(UnitHealthPartId.LeftFoot, 2f, 14f, "foot.left", parentId: "leg.left"),
                    Tissue(UnitHealthPartId.RightLeg, 8f, 35f, "leg.right", parentId: "torso.main"),
                    Tissue(UnitHealthPartId.RightFoot, 2f, 14f, "foot.right", parentId: "leg.right")
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)),

            Layer("body.middle",
                Part(V(0f, 0f, 0.875f), V(0.13f, 0.105f, 0.34f),
                    BodyPrimitive.Circle, role: BodyPartRole.Torso),
                0.385f,
                new[]
                {
                    Tissue(UnitHealthPartId.Torso, 14f, 60f, "torso.main"),
                    Tissue(UnitHealthPartId.LeftArm, 2f, 30f, "arm.left", parentId: "torso.main"),
                    Tissue(UnitHealthPartId.LeftHand, 0.5f, 12f, "hand.left", parentId: "arm.left"),
                    Tissue(UnitHealthPartId.RightArm, 2f, 30f, "arm.right", parentId: "torso.main"),
                    Tissue(UnitHealthPartId.RightHand, 0.5f, 12f, "hand.right", parentId: "arm.right"),
                    Tissue(UnitHealthPartId.Heart, 0.3f, 15f, "heart.main", UnitHealthPartKind.Organ, "torso.main"),
                    Tissue(UnitHealthPartId.LeftLung, 0.25f, 12f, "lung.left", UnitHealthPartKind.Organ, "torso.main"),
                    Tissue(UnitHealthPartId.RightLung, 0.25f, 12f, "lung.right", UnitHealthPartKind.Organ, "torso.main"),
                    Tissue(UnitHealthPartId.Stomach, 0.18f, 12f, "stomach.main", UnitHealthPartKind.Organ, "torso.main"),
                    Tissue(UnitHealthPartId.Liver, 0.2f, 15f, "liver.main", UnitHealthPartKind.Organ, "torso.main"),
                    Tissue(UnitHealthPartId.LeftKidney, 0.12f, 10f, "kidney.left", UnitHealthPartKind.Organ, "torso.main"),
                    Tissue(UnitHealthPartId.RightKidney, 0.12f, 10f, "kidney.right", UnitHealthPartKind.Organ, "torso.main")
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)),

            Layer("body.upper",
                Part(V(0.155f, 0f, 1.47f), V(0.055f, 0.055f, 0.22f),
                    BodyPrimitive.Circle, anchor: BodyPartAnchor.UnitHead, role: BodyPartRole.Head),
                0.2275f,
                new[]
                {
                    Tissue(UnitHealthPartId.Head, 19f, 40f, "head.main", parentId: "torso.main"),
                    Tissue(UnitHealthPartId.Brain, 1f, 10f, "brain.main", UnitHealthPartKind.Organ, "head.main")
                },
                new UnitAnatomyMarker("eye", UnitAnatomyMarkerRole.Eye | UnitAnatomyMarkerRole.Vision, V(0f, 0f, 0.015f)),
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("muzzle", UnitAnatomyMarkerRole.Muzzle, V(0.035f, 0f, 0.005f)))),
        new[]
        {
            new UnitStartingEquipment(ApparelCatalog.WorkShirt, 55f),
            new UnitStartingEquipment(ApparelCatalog.WorkTrousers, 60f),
            new UnitStartingEquipment(ApparelCatalog.CombatVest, 120f),
            new UnitStartingEquipment(ApparelCatalog.CombatHelmet, 90f),
            new UnitStartingEquipment(ApparelCatalog.PoweredExoskeleton, 160f)
        });

    private static readonly UnitArchetypeProfile Greenbob = new(
        new UnitDefinition(UnitType.Greenbob, UnitBodyType.SmallCreature, 3.2f, 0.40f, 0.40f, 0.40f, 20f, 110f),
        new UnitAnatomyProfile(
            Node("body", "", Part(V(0f, 0f, 0.20f), V(0.18f, 0.105f, 0.15f),
                BodyPrimitive.Circle, role: BodyPartRole.Torso), 0.15f,
                UnitHealthPartId.WholeBody, 100f, "body.main", UnitHealthPartKind.BodyPart, "",
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)),
            Node("head", "body", Part(V(0.18f, 0f, 0.34f), V(0.052f, 0.050f, 0.052f),
                BodyPrimitive.Circle, parentIndex: 0, anchor: BodyPartAnchor.UnitHead, role: BodyPartRole.Head), 0.052f,
                UnitHealthPartId.WholeBody, 100f, "head.tissue", UnitHealthPartKind.BodyPart, "body.main",
                new UnitAnatomyMarker("eye", UnitAnatomyMarkerRole.Eye | UnitAnatomyMarkerRole.Vision, V(0f, 0f, 0.015f)),
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision)),
            Node("leg.front.left", "body", Part(V(0.11f, 0.055f, -0.12f), V(0.030f, 0.012f, 0.028f),
                BodyPrimitive.Box, parentIndex: 0, localRotation: 0.60f,
                motion: BodyPartMotion.WalkCycle, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start), 0.028f,
                UnitHealthPartId.WholeBody, 100f, "leg.front.left.tissue", tissueParentId: "body.main"),
            Node("foot.front.left", "leg.front.left", Part(V(0.030f, 0f, 0f), V(0.014f, 0.014f, 0.014f),
                BodyPrimitive.Circle, parentIndex: 2, role: BodyPartRole.Foot), 0.014f,
                UnitHealthPartId.WholeBody, 100f, "foot.front.left.tissue", tissueParentId: "leg.front.left.tissue"),
            Node("leg.front.right", "body", Part(V(0.11f, -0.055f, -0.12f), V(0.030f, 0.012f, 0.028f),
                BodyPrimitive.Box, parentIndex: 0, localRotation: -0.60f,
                motion: BodyPartMotion.WalkCycleOpposite, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start), 0.028f,
                UnitHealthPartId.WholeBody, 100f, "leg.front.right.tissue", tissueParentId: "body.main"),
            Node("foot.front.right", "leg.front.right", Part(V(0.030f, 0f, 0f), V(0.014f, 0.014f, 0.014f),
                BodyPrimitive.Circle, parentIndex: 4, role: BodyPartRole.Foot), 0.014f,
                UnitHealthPartId.WholeBody, 100f, "foot.front.right.tissue", tissueParentId: "leg.front.right.tissue"),
            Node("leg.rear.left", "body", Part(V(-0.14f, 0.055f, -0.12f), V(0.030f, 0.012f, 0.028f),
                BodyPrimitive.Box, parentIndex: 0, localRotation: MathF.PI - 0.60f,
                motion: BodyPartMotion.WalkCycleOpposite, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start), 0.028f,
                UnitHealthPartId.WholeBody, 100f, "leg.rear.left.tissue", tissueParentId: "body.main"),
            Node("foot.rear.left", "leg.rear.left", Part(V(0.030f, 0f, 0f), V(0.014f, 0.014f, 0.014f),
                BodyPrimitive.Circle, parentIndex: 6, role: BodyPartRole.Foot), 0.014f,
                UnitHealthPartId.WholeBody, 100f, "foot.rear.left.tissue", tissueParentId: "leg.rear.left.tissue"),
            Node("leg.rear.right", "body", Part(V(-0.14f, -0.055f, -0.12f), V(0.030f, 0.012f, 0.028f),
                BodyPrimitive.Box, parentIndex: 0, localRotation: -MathF.PI + 0.60f,
                motion: BodyPartMotion.WalkCycle, role: BodyPartRole.Thigh, pivot: BodyPartPivot.Start), 0.028f,
                UnitHealthPartId.WholeBody, 100f, "leg.rear.right.tissue", tissueParentId: "body.main"),
            Node("foot.rear.right", "leg.rear.right", Part(V(0.030f, 0f, 0f), V(0.014f, 0.014f, 0.014f),
                BodyPrimitive.Circle, parentIndex: 8, role: BodyPartRole.Foot), 0.014f,
                UnitHealthPartId.WholeBody, 100f, "foot.rear.right.tissue", tissueParentId: "leg.rear.right.tissue")));

    private static readonly UnitArchetypeProfile SegmentedMonster = CreateSegmentedMonster();

    public static UnitArchetypeProfile Get(UnitType type) =>
        type switch
        {
            UnitType.Colonist => Colonist,
            UnitType.Greenbob => Greenbob,
            UnitType.SegmentedMonster => SegmentedMonster,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown unit archetype.")
        };

    public static UnitArchetypeProfile Get(UnitBodyType bodyType) =>
        bodyType switch
        {
            UnitBodyType.Colonist => Colonist,
            UnitBodyType.SmallCreature => Greenbob,
            UnitBodyType.SegmentedCreature => SegmentedMonster,
            _ => throw new ArgumentOutOfRangeException(nameof(bodyType), bodyType, "Unknown unit body type.")
        };

    private static UnitArchetypeProfile CreateSegmentedMonster()
    {
        var nodes = new UnitAnatomyNode[10];
        for (int i = 0; i < nodes.Length; i++)
        {
            string id = $"segment.{i}";
            string tissueId = $"{id}.tissue";
            string parentId = i == 0 ? string.Empty : $"segment.{i - 1}";
            string parentTissueId = i == 0 ? string.Empty : $"segment.{i - 1}.tissue";
            float radius = i == 0 ? 0.52f : 0.46f - (i - 1) * 0.025f;

            var shape = new BodyPartDefinition(
                new Vector3(i == 0 ? 0f : -0.62f, 0f, 0f),
                new Vector3(radius, radius, radius),
                BodyPrimitive.Circle,
                parentIndex: i == 0 ? -1 : i - 1,
                motion: i == 0 ? BodyPartMotion.StaticRelative : BodyPartMotion.FollowParent,
                followDistance: i == 0 ? 0f : 0.62f,
                role: BodyPartRole.Segment);

            nodes[i] = Node(
                id, parentId, shape, 0.225f,
                UnitHealthPartId.WholeBody, 100f, tissueId,
                UnitHealthPartKind.BodyPart, parentTissueId,
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion));
        }

        return new UnitArchetypeProfile(
            new UnitDefinition(UnitType.SegmentedMonster, UnitBodyType.SegmentedCreature, 1.4f, 1.0f, 6.0f, 0.45f, 20f, 110f),
            new UnitAnatomyProfile(nodes));
    }

    private static BodyPartDefinition Part(
        Vector3 localPosition,
        Vector3 scale,
        BodyPrimitive primitive,
        int parentIndex = -1,
        float localRotation = 0f,
        BodyPartMotion motion = BodyPartMotion.StaticRelative,
        float followDistance = 0f,
        BodyPartAnchor anchor = BodyPartAnchor.Parent,
        BodyPartRole role = BodyPartRole.Generic,
        BodyPartPivot pivot = BodyPartPivot.Center) =>
        new(localPosition, scale, primitive, parentIndex, localRotation, motion, followDistance, anchor, role, pivot);

    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private static UnitAnatomyNode Layer(
        string id,
        BodyPartDefinition shape,
        float hitHalfHeight,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers) =>
        UnitAnatomyNode.Part(id, string.Empty, shape, hitHalfHeight, tissues, markers);

    private static HitRegionPartBinding Tissue(
        UnitHealthPartId part,
        float weight,
        float maxHitPoints,
        string id,
        UnitHealthPartKind kind = UnitHealthPartKind.BodyPart,
        string parentId = "") =>
        new(
            part,
            weight,
            kind,
            maxHitPoints,
            id: id,
            parentId: string.IsNullOrWhiteSpace(parentId) ? null : parentId);

    private static UnitAnatomyNode Node(
        string id,
        string parentId,
        BodyPartDefinition shape,
        float hitHalfHeight,
        UnitHealthPartId part,
        float maxHitPoints,
        string tissueId,
        UnitHealthPartKind kind = UnitHealthPartKind.BodyPart,
        string tissueParentId = "",
        params UnitAnatomyMarker[] markers)
    {
        var tissue = new HitRegionPartBinding(
            part,
            weight: 1f,
            kind: kind,
            maxHitPoints: maxHitPoints,
            id: tissueId,
            parentId: string.IsNullOrWhiteSpace(tissueParentId) ? null : tissueParentId);

        return UnitAnatomyNode.Part(id, parentId, shape, hitHalfHeight, new[] { tissue }, markers);
    }
}
