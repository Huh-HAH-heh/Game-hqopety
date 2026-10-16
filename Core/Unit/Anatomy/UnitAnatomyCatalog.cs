using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
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
    private const float DefaultProjectileEnergyLoss = 160f;
    private const float DefaultProjectilePenetrationLoss = 8f;

    private static UnitArchetypeProfile Colonist = new(
        new UnitDefinition(UnitType.Colonist, UnitBodyType.Colonist, 2.8f, 0.35f, 0.60f, 1.75f, 20f, 110f),
        new UnitAnatomyProfile(
            new UnitAnatomyVolume[]
            {
                new("organ.heart", "body.middle", "torso.main", "heart.main",
                    V(0.012f, 0.008f, 0.005f), V(0.020f, 0.016f, 0.012f),
                    energyLoss: 26f, penetrationLoss: 2f, damageMultiplier: 0.85f, bleedMultiplier: 1.5f),
                new("organ.lung.left", "body.middle", "torso.main", "lung.left",
                    V(0.005f, 0.030f, 0.005f), V(0.026f, 0.018f, 0.012f),
                    energyLoss: 18f, penetrationLoss: 1.5f, damageMultiplier: 0.65f, bleedMultiplier: 1.2f),
                new("organ.lung.right", "body.middle", "torso.main", "lung.right",
                    V(0.005f, -0.030f, 0.005f), V(0.026f, 0.018f, 0.012f),
                    energyLoss: 18f, penetrationLoss: 1.5f, damageMultiplier: 0.65f, bleedMultiplier: 1.2f),
                new("organ.stomach", "body.middle", "torso.main", "stomach.main",
                    V(-0.035f, 0.008f, -0.025f), V(0.022f, 0.018f, 0.012f),
                    energyLoss: 14f, penetrationLoss: 1f, damageMultiplier: 0.65f, bleedMultiplier: 0.8f),
                new("organ.liver", "body.middle", "torso.main", "liver.main",
                    V(-0.012f, -0.020f, -0.015f), V(0.023f, 0.018f, 0.012f),
                    energyLoss: 22f, penetrationLoss: 1.5f, damageMultiplier: 0.8f, bleedMultiplier: 1.4f),
                new("organ.kidney.left", "body.middle", "torso.main", "kidney.left",
                    V(-0.045f, 0.032f, -0.035f), V(0.012f, 0.010f, 0.010f),
                    energyLoss: 12f, penetrationLoss: 1f, damageMultiplier: 0.55f, bleedMultiplier: 0.8f),
                new("organ.kidney.right", "body.middle", "torso.main", "kidney.right",
                    V(-0.045f, -0.032f, -0.035f), V(0.012f, 0.010f, 0.010f),
                    energyLoss: 12f, penetrationLoss: 1f, damageMultiplier: 0.55f, bleedMultiplier: 0.8f),
                new("organ.brain", "body.upper", "head.main", "brain.main",
                    V(0f, 0f, 0.015f), V(0.018f, 0.018f, 0.014f),
                    energyLoss: 20f, penetrationLoss: 1.5f, damageMultiplier: 1.5f, bleedMultiplier: 1.8f)
            },
            // Colonist exterior: three trunk segments, two segments per limb,
            // a neck, and a head. Existing torso/head/organs bindings remain stable.
            Layer("body.middle",
                Part(V(0f, 0f, 0.875f), V(0.112f, 0.092f, 0.22f),
                    BodyPrimitive.Circle, role: BodyPartRole.Torso),
                0.24f,
                new[]
                {
                    Tissue(UnitHealthPartId.Torso, 14f, 60f, "torso.main", projectileEnergyLoss: 190f, projectilePenetrationLoss: 12f),
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

            ChildLayer("body.lower", "body.middle",
                Part(V(-0.015f, 0f, -0.395f), V(0.095f, 0.078f, 0.20f),
                    BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.Torso),
                0.22f,
                new[]
                {
                    Tissue(UnitHealthPartId.Torso, 1f, 42f, "torso.lower",
                        parentId: "torso.main",
                        projectileEnergyLoss: 190f,
                        projectilePenetrationLoss: 12f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)),

            ChildLayer("body.torso.upper", "body.middle",
                Part(V(0.015f, 0f, 0.225f), V(0.102f, 0.082f, 0.17f),
                    BodyPrimitive.Circle, parentIndex: 0, role: BodyPartRole.Torso),
                0.19f,
                new[]
                {
                    Tissue(UnitHealthPartId.Torso, 1f, 38f, "torso.upper",
                        parentId: "torso.main",
                        projectileEnergyLoss: 190f,
                        projectilePenetrationLoss: 12f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision)),

            ChildLayer("body.arm.left.upper", "body.torso.upper",
                Part(V(0f, 0.072f, -0.10f), V(0.045f, 0.022f, 0.105f),
                    BodyPrimitive.Circle, parentIndex: 2,
                    localRotation: MathF.PI / 2f,
                    motion: BodyPartMotion.WalkCycle,
                    role: BodyPartRole.UpperArm,
                    pivot: BodyPartPivot.Start),
                0.14f,
                new[]
                {
                    Tissue(UnitHealthPartId.LeftArm, 2f, 30f, "arm.left",
                        parentId: "torso.main",
                        projectileEnergyLoss: 90f,
                        projectilePenetrationLoss: 6f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.arm.left.lower", "body.arm.left.upper",
                Part(V(0.045f, 0f, -0.205f), V(0.040f, 0.020f, 0.10f),
                    BodyPrimitive.Circle, parentIndex: 3,
                    role: BodyPartRole.Forearm,
                    pivot: BodyPartPivot.Start),
                0.115f,
                new[]
                {
                    Tissue(UnitHealthPartId.LeftHand, 0.5f, 12f, "hand.left",
                        parentId: "arm.left",
                        projectileEnergyLoss: 55f,
                        projectilePenetrationLoss: 4f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.arm.right.upper", "body.torso.upper",
                Part(V(0f, -0.072f, -0.10f), V(0.045f, 0.022f, 0.105f),
                    BodyPrimitive.Circle, parentIndex: 2,
                    localRotation: -MathF.PI / 2f,
                    motion: BodyPartMotion.WalkCycleOpposite,
                    role: BodyPartRole.UpperArm,
                    pivot: BodyPartPivot.Start),
                0.14f,
                new[]
                {
                    Tissue(UnitHealthPartId.RightArm, 2f, 30f, "arm.right",
                        parentId: "torso.main",
                        projectileEnergyLoss: 90f,
                        projectilePenetrationLoss: 6f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.arm.right.lower", "body.arm.right.upper",
                Part(V(0.045f, 0f, -0.205f), V(0.040f, 0.020f, 0.10f),
                    BodyPrimitive.Circle, parentIndex: 5,
                    role: BodyPartRole.Forearm,
                    pivot: BodyPartPivot.Start),
                0.115f,
                new[]
                {
                    Tissue(UnitHealthPartId.RightHand, 0.5f, 12f, "hand.right",
                        parentId: "arm.right",
                        projectileEnergyLoss: 55f,
                        projectilePenetrationLoss: 4f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.leg.left.upper", "body.lower",
                Part(V(-0.005f, 0.035f, -0.08f), V(0.050f, 0.025f, 0.13f),
                    BodyPrimitive.Circle, parentIndex: 1,
                    localRotation: MathF.PI - 0.22f,
                    motion: BodyPartMotion.WalkCycle,
                    role: BodyPartRole.Thigh,
                    pivot: BodyPartPivot.Start),
                0.15f,
                new[]
                {
                    Tissue(UnitHealthPartId.LeftLeg, 8f, 35f, "leg.left",
                        parentId: "torso.main",
                        projectileEnergyLoss: 105f,
                        projectilePenetrationLoss: 7f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.leg.left.lower", "body.leg.left.upper",
                Part(V(0.050f, 0f, -0.255f), V(0.040f, 0.020f, 0.12f),
                    BodyPrimitive.Circle, parentIndex: 7,
                    role: BodyPartRole.Shin,
                    pivot: BodyPartPivot.Start),
                0.12f,
                new[]
                {
                    Tissue(UnitHealthPartId.LeftFoot, 2f, 14f, "foot.left",
                        parentId: "leg.left",
                        projectileEnergyLoss: 50f,
                        projectilePenetrationLoss: 4f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.leg.right.upper", "body.lower",
                Part(V(-0.005f, -0.035f, -0.08f), V(0.050f, 0.025f, 0.13f),
                    BodyPrimitive.Circle, parentIndex: 1,
                    localRotation: -MathF.PI + 0.22f,
                    motion: BodyPartMotion.WalkCycleOpposite,
                    role: BodyPartRole.Thigh,
                    pivot: BodyPartPivot.Start),
                0.15f,
                new[]
                {
                    Tissue(UnitHealthPartId.RightLeg, 8f, 35f, "leg.right",
                        parentId: "torso.main",
                        projectileEnergyLoss: 105f,
                        projectilePenetrationLoss: 7f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.leg.right.lower", "body.leg.right.upper",
                Part(V(0.050f, 0f, -0.255f), V(0.040f, 0.020f, 0.12f),
                    BodyPrimitive.Circle, parentIndex: 9,
                    role: BodyPartRole.Shin,
                    pivot: BodyPartPivot.Start),
                0.12f,
                new[]
                {
                    Tissue(UnitHealthPartId.RightFoot, 2f, 14f, "foot.right",
                        parentId: "leg.right",
                        projectileEnergyLoss: 50f,
                        projectilePenetrationLoss: 4f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            ChildLayer("body.neck", "body.torso.upper",
                Part(V(0.075f, 0f, 0.205f), V(0.038f, 0.035f, 0.10f),
                    BodyPrimitive.Circle, parentIndex: 2,
                    role: BodyPartRole.Neck),
                0.105f,
                new[]
                {
                    Tissue(UnitHealthPartId.Neck, 1f, 18f, "neck.main",
                        parentId: "torso.upper",
                        projectileEnergyLoss: 100f,
                        projectilePenetrationLoss: 6f)
                },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim)),

            Layer("body.upper",
                Part(V(0.155f, 0f, 1.47f), V(0.055f, 0.055f, 0.14f),
                    BodyPrimitive.Circle, anchor: BodyPartAnchor.UnitHead, role: BodyPartRole.Head),
                0.145f,
                new[]
                {
                    Tissue(UnitHealthPartId.Head, 19f, 40f, "head.main", parentId: "torso.main", projectileEnergyLoss: 170f, projectilePenetrationLoss: 10f),
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

    private static bool _colonistRigLoaded;

    /// <summary>Apply an editable rig while preserving anatomy metadata and organ volumes.</summary>
    public static void ApplyColonistAuthoringData(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        ReadOnlySpan<float> hitHalfHeights,
        HitRegionPartBinding[][] tissues,
        UnitAnatomyMarker[][] markers,
        ReadOnlySpan<UnitAnatomyVolume> volumes)
    {
        EnsureSavedColonistRigLoaded();
        UnitArchetypeProfile current = Colonist;
        UnitAnatomyProfile nextAnatomy = current.Anatomy.WithAuthoringData(
            regionIds, bodyParts, hitHalfHeights, tissues, markers, volumes);
        Colonist = new UnitArchetypeProfile(
            current.Definition,
            nextAnatomy,
            current.StartingEquipment.ToArray());
        _colonistRigLoaded = true;
    }

    /// <summary>Apply a complete rig including appended regions and parent dependencies.</summary>
    public static void ApplyColonistRig(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts)
    {
        UnitArchetypeProfile current = Colonist;
        UnitAnatomyProfile nextAnatomy = current.Anatomy.WithRig(regionIds, bodyParts);
        Colonist = new UnitArchetypeProfile(
            current.Definition,
            nextAnatomy,
            current.StartingEquipment.ToArray());
        _colonistRigLoaded = true;
    }

    public static void ApplyColonistBodyParts(ReadOnlySpan<BodyPartDefinition> bodyParts)
    {
        UnitAnatomyProfile anatomy = Get(UnitType.Colonist).Anatomy;
        var ids = new string[anatomy.NodeCount];
        ReadOnlySpan<UnitAnatomyNode> nodes = anatomy.Nodes;
        for (int i = 0; i < ids.Length; i++)
            ids[i] = nodes[i].Id;
        ApplyColonistRig(ids, bodyParts);
    }

    private static string ColonistSettingsPath =>
        Path.Combine(
            Path.GetDirectoryName(UnitBodyRigSettingsStore.FilePath) ?? Environment.CurrentDirectory,
            "colonist-unit-settings.json");

    /// <summary>Applies and persists the editable movement, size and vision settings.</summary>
    public static bool SaveColonistDefinition(UnitDefinition definition, out string error)
    {
        if (definition.Type != UnitType.Colonist ||
            !IsValidPositive(definition.MoveSpeed) ||
            !IsValidPositive(definition.Width) ||
            !IsValidPositive(definition.Length) ||
            !IsValidPositive(definition.Height) ||
            !IsValidPositive(definition.ViewRange) ||
            !float.IsFinite(definition.FieldOfView) ||
            definition.FieldOfView < 1f ||
            definition.FieldOfView > 360f)
        {
            error = "Параметры юнита выходят за допустимые пределы.";
            return false;
        }

        var settings = new ColonistSettingsFile
        {
            BodyType = definition.BodyType.ToString(),
            MoveSpeed = definition.MoveSpeed,
            Width = definition.Width,
            Length = definition.Length,
            Height = definition.Height,
            ViewRange = definition.ViewRange,
            FieldOfView = definition.FieldOfView
        };

        try
        {
            string directory = Path.GetDirectoryName(ColonistSettingsPath) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                ColonistSettingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        ApplyColonistDefinition(definition);
        error = string.Empty;
        return true;
    }

    public static void ApplyColonistDefinition(UnitDefinition definition)
    {
        if (definition.Type != UnitType.Colonist)
            throw new ArgumentException("Only the colonist definition can be edited in this constructor.", nameof(definition));

        EnsureSavedColonistRigLoaded();
        UnitArchetypeProfile current = Colonist;
        var updated = new UnitDefinition(
            UnitType.Colonist,
            definition.BodyType,
            definition.MoveSpeed,
            definition.Width,
            definition.Length,
            definition.Height,
            definition.ViewRange,
            definition.FieldOfView);
        Colonist = new UnitArchetypeProfile(
            updated,
            current.Anatomy,
            current.StartingEquipment.ToArray());
    }

    private static bool IsValidPositive(float value) =>
        float.IsFinite(value) && value > 0f;

    private sealed class ColonistSettingsFile
    {
        public string BodyType { get; set; } = nameof(UnitBodyType.Colonist);
        public float MoveSpeed { get; set; } = 2.8f;
        public float Width { get; set; } = 0.35f;
        public float Length { get; set; } = 0.60f;
        public float Height { get; set; } = 1.75f;
        public float ViewRange { get; set; } = 20f;
        public float FieldOfView { get; set; } = 110f;
    }

    private static void EnsureSavedColonistRigLoaded()
    {
        if (_colonistRigLoaded)
            return;

        _colonistRigLoaded = true;
        UnitArchetypeProfile current = Colonist;
        ReadOnlySpan<UnitAnatomyNode> nodes = current.Anatomy.Nodes;
        var baseIds = new string[nodes.Length];
        for (int i = 0; i < baseIds.Length; i++)
            baseIds[i] = nodes[i].Id;

        UnitBodyRigSettingsStore.MergeSavedRig(
            baseIds,
            current.BodyParts,
            out string[] ids,
            out BodyPartDefinition[] parts);
        if (ids.Length != nodes.Length)
        {
            UnitAnatomyProfile next = current.Anatomy.WithRig(ids, parts);
            Colonist = new UnitArchetypeProfile(
                current.Definition,
                next,
                current.StartingEquipment.ToArray());
        }
        else if (parts.Length == current.BodyParts.Length)
        {
            UnitAnatomyProfile next = current.Anatomy.WithRig(ids, parts);
            Colonist = new UnitArchetypeProfile(
                current.Definition,
                next,
                current.StartingEquipment.ToArray());
        }

        try
        {
            if (File.Exists(ColonistSettingsPath))
            {
                ColonistSettingsFile? settings =
                    JsonSerializer.Deserialize<ColonistSettingsFile>(File.ReadAllText(ColonistSettingsPath));
                if (settings != null &&
                    IsValidPositive(settings.MoveSpeed) &&
                    IsValidPositive(settings.Width) &&
                    IsValidPositive(settings.Length) &&
                    IsValidPositive(settings.Height) &&
                    IsValidPositive(settings.ViewRange) &&
                    float.IsFinite(settings.FieldOfView) &&
                    settings.FieldOfView >= 1f &&
                    settings.FieldOfView <= 360f)
                {
                    UnitArchetypeProfile saved = Colonist;
                    UnitBodyType bodyType =
                        Enum.TryParse(settings.BodyType, ignoreCase: true, out UnitBodyType savedBodyType) &&
                        Enum.IsDefined(savedBodyType)
                            ? savedBodyType
                            : saved.Definition.BodyType;
                    var definition = new UnitDefinition(
                        UnitType.Colonist,
                        bodyType,
                        settings.MoveSpeed,
                        settings.Width,
                        settings.Length,
                        settings.Height,
                        settings.ViewRange,
                        settings.FieldOfView);
                    Colonist = new UnitArchetypeProfile(
                        definition,
                        saved.Anatomy,
                        saved.StartingEquipment.ToArray());
                }
            }
        }
        catch (Exception)
        {
            // Ignore an invalid settings file and continue with the built-in archetype.
        }

        UnitArchetypeProfile currentProfile = Colonist;
        if (UnitAnatomySettingsStore.TryLoad(currentProfile.Anatomy, out UnitAnatomyProfile savedAnatomy))
        {
            Colonist = new UnitArchetypeProfile(
                currentProfile.Definition,
                savedAnatomy,
                currentProfile.StartingEquipment.ToArray());
        }
    }

    public static UnitArchetypeProfile Get(UnitType type)
    {
        if (type == UnitType.Colonist)
            EnsureSavedColonistRigLoaded();

        return type switch
        {
            UnitType.Colonist => Colonist,
            UnitType.Greenbob => Greenbob,
            UnitType.SegmentedMonster => SegmentedMonster,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown unit archetype.")
        };
    }

    public static UnitArchetypeProfile Get(UnitBodyType bodyType)
    {
        if (bodyType == UnitBodyType.Colonist)
            EnsureSavedColonistRigLoaded();

        return bodyType switch
        {
            UnitBodyType.Colonist => Colonist,
            UnitBodyType.SmallCreature => Greenbob,
            UnitBodyType.SegmentedCreature => SegmentedMonster,
            _ => throw new ArgumentOutOfRangeException(nameof(bodyType), bodyType, "Unknown unit body type.")
        };
    }

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
        params UnitAnatomyMarker[] markers)
    {
        shape = UnitBodyRigSettingsStore.ApplySavedShape(id, shape);
        return UnitAnatomyNode.Part(id, string.Empty, shape, hitHalfHeight, tissues, markers);
    }

    private static UnitAnatomyNode ChildLayer(
        string id,
        string parentId,
        BodyPartDefinition shape,
        float hitHalfHeight,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers)
    {
        shape = UnitBodyRigSettingsStore.ApplySavedShape(id, shape);
        return UnitAnatomyNode.Part(id, parentId, shape, hitHalfHeight, tissues, markers);
    }

    private static HitRegionPartBinding Tissue(
        UnitHealthPartId part,
        float weight,
        float maxHitPoints,
        string id,
        UnitHealthPartKind kind = UnitHealthPartKind.BodyPart,
        string parentId = "",
        float projectileEnergyLoss = -1f,
        float projectilePenetrationLoss = -1f) =>
        new(
            part,
            weight,
            kind,
            maxHitPoints,
            id: id,
            parentId: string.IsNullOrWhiteSpace(parentId) ? null : parentId,
            projectileEnergyLoss: projectileEnergyLoss,
            projectilePenetrationLoss: projectilePenetrationLoss);

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
            parentId: string.IsNullOrWhiteSpace(tissueParentId) ? null : tissueParentId,
            projectileEnergyLoss: DefaultProjectileEnergyLoss,
            projectilePenetrationLoss: DefaultProjectilePenetrationLoss);

        return UnitAnatomyNode.Part(id, parentId, shape, hitHalfHeight, new[] { tissue }, markers);
    }
}
