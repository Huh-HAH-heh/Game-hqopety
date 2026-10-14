using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Unit;

namespace RimClone.Render;

/// <summary>
/// Common contract for inspectable anatomy. The UI consumes this base type and
/// renders each specialized entry through its virtual category/geometry data.
/// </summary>
public abstract class AnatomyCardEntry
{
    public string RegionId { get; }
    public string Id { get; }
    public string Name { get; }
    public string ParentId { get; }
    public UnitHealthPartId Part { get; }
    public float HitPoints { get; }
    public float MaxHitPoints { get; }
    public float BleedRate { get; }
    public bool Present { get; }

    public float HealthRatio =>
        MaxHitPoints > 0f ? Math.Clamp(HitPoints / MaxHitPoints, 0f, 1f) : 0f;

    public abstract string Category { get; }
    public abstract string GeometryDescription { get; }
    public virtual string FunctionDescription => Category;

    protected AnatomyCardEntry(
        string regionId,
        string id,
        string name,
        string parentId,
        UnitHealthPartId part,
        float hitPoints,
        float maxHitPoints,
        float bleedRate,
        bool present)
    {
        RegionId = regionId ?? string.Empty;
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        ParentId = parentId ?? string.Empty;
        Part = part;
        HitPoints = hitPoints;
        MaxHitPoints = maxHitPoints;
        BleedRate = bleedRate;
        Present = present;
    }
}

public sealed class BodyPartCardEntry : AnatomyCardEntry
{
    public BodyPartDefinition Shape { get; }

    public override string Category => "Часть тела";
    public override string FunctionDescription => "Наружная ткань / часть тела";

    public override string GeometryDescription =>
        $"Геометрия слоя {RegionId}: {Shape.Primitive}; размер: {FormatVector(Shape.Scale)}; " +
        $"локальная позиция: {FormatVector(Shape.LocalPosition)}; " +
        $"поворот: {Shape.LocalRotation * 180f / MathF.PI:0.0}°";

    public BodyPartCardEntry(
        string regionId,
        string id,
        string name,
        string parentId,
        UnitHealthPartId part,
        float hitPoints,
        float maxHitPoints,
        float bleedRate,
        bool present,
        BodyPartDefinition shape)
        : base(regionId, id, name, parentId, part, hitPoints, maxHitPoints, bleedRate, present)
    {
        Shape = shape;
    }

    private static string FormatVector(Vector3 value) =>
        $"({value.X:0.000}, {value.Y:0.000}, {value.Z:0.000})";
}

public sealed class OrganCardEntry : AnatomyCardEntry
{
    private readonly UnitAnatomyVolume[] _volumes;

    public ReadOnlySpan<UnitAnatomyVolume> Volumes => _volumes;
    public override string Category => "Орган";
    public override string FunctionDescription => "Внутренний орган";

    public override string GeometryDescription
    {
        get
        {
            if (_volumes.Length == 0)
                return "Внутренний объём не задан; здоровье ведётся отдельной тканью";

            UnitAnatomyVolume volume = _volumes[0];
            string summary =
                $"Объёмов: {_volumes.Length}; примитив: {volume.Primitive}; " +
                $"центр: {FormatVector(volume.LocalCenter)}; " +
                $"полуразмер: {FormatVector(volume.HalfExtents)}";
            if (_volumes.Length > 1)
                summary += $"; первый объём: {volume.Id}";
            return summary;
        }
    }

    public OrganCardEntry(
        string regionId,
        string id,
        string name,
        string parentId,
        UnitHealthPartId part,
        float hitPoints,
        float maxHitPoints,
        float bleedRate,
        bool present,
        UnitAnatomyVolume[] volumes)
        : base(regionId, id, name, parentId, part, hitPoints, maxHitPoints, bleedRate, present)
    {
        _volumes = volumes == null ? Array.Empty<UnitAnatomyVolume>() :
            (UnitAnatomyVolume[])volumes.Clone();
    }

    private static string FormatVector(Vector3 value) =>
        $"({value.X:0.000}, {value.Y:0.000}, {value.Z:0.000})";
}

internal static class AnatomyCardEntryFactory
{
    public static AnatomyCardEntry[] Capture(
        UnitAnatomyProfile profile,
        UnitHealthStore health,
        int unitIndex)
    {
        List<AnatomyCardEntry> result = new(32);
        ReadOnlySpan<UnitAnatomyVolume> allVolumes = profile.Volumes;

        for (int regionIndex = 0; regionIndex < profile.NodeCount; regionIndex++)
        {
            UnitAnatomyNode node = profile.GetNode(regionIndex);
            ReadOnlySpan<HitRegionPartBinding> tissues = node.Tissues;

            for (int bindingIndex = 0; bindingIndex < tissues.Length; bindingIndex++)
            {
                HitRegionPartBinding binding = tissues[bindingIndex];
                        health.TryFindRegionPart(unitIndex, regionIndex, bindingIndex, out int healthIndex);
                if (healthIndex < 0)
                    continue;

                bool present = health.Present[healthIndex];
                float hp = present ? health.HitPoints[healthIndex] : 0f;
                float maxHp = health.MaxHitPoints[healthIndex];
                float bleed = health.BleedRate[healthIndex];
                string name = DisplayName(binding.Id, binding.Part, binding.Kind);
                string parent = string.IsNullOrWhiteSpace(binding.ParentId)
                    ? node.ParentId
                    : binding.ParentId;

                if (binding.Kind == UnitHealthPartKind.Organ)
                {
                    List<UnitAnatomyVolume> volumes = new(2);
                    for (int volumeIndex = 0; volumeIndex < allVolumes.Length; volumeIndex++)
                    {
                        UnitAnatomyVolume volume = allVolumes[volumeIndex];
                        if (string.Equals(volume.TargetBindingId, binding.Id, StringComparison.Ordinal))
                            volumes.Add(volume);
                    }

                    result.Add(new OrganCardEntry(
                        node.Id, binding.Id, name, parent, binding.Part,
                        hp, maxHp, bleed, present, volumes.ToArray()));
                }
                else
                {
                    result.Add(new BodyPartCardEntry(
                        node.Id, binding.Id, name, parent, binding.Part,
                        hp, maxHp, bleed, present, node.Shape));
                }
            }
        }

        return result.ToArray();
    }

    private static string DisplayName(string id, UnitHealthPartId part, UnitHealthPartKind kind)
    {
        return id switch
        {
            "body.main" => "Тело",
            "torso.main" => "Средняя часть туловища",
            "torso.lower" => "Нижняя часть туловища",
            "torso.upper" => "Верхняя часть туловища",
            "neck.main" => "Шея",
            "head.main" => "Голова",
            "heart.main" => "Сердце",
            "lung.left" => "Левое лёгкое",
            "lung.right" => "Правое лёгкое",
            "stomach.main" => "Желудок",
            "liver.main" => "Печень",
            "kidney.left" => "Левая почка",
            "kidney.right" => "Правая почка",
            "brain.main" => "Мозг",
            "arm.left" => "Левая верхняя рука",
            "arm.right" => "Правая верхняя рука",
            "hand.left" => "Левое предплечье / кисть",
            "hand.right" => "Правое предплечье / кисть",
            "leg.left" => "Левое бедро",
            "leg.right" => "Правое бедро",
            "foot.left" => "Левая голень / стопа",
            "foot.right" => "Правая голень / стопа",
            _ => part switch
            {
                UnitHealthPartId.WholeBody => HumanizeId(id),
                UnitHealthPartId.Head => "Голова",
                UnitHealthPartId.Brain => "Мозг",
                UnitHealthPartId.Torso => "Торс",
                UnitHealthPartId.Heart => "Сердце",
                UnitHealthPartId.LeftLung => "Левое лёгкое",
                UnitHealthPartId.RightLung => "Правое лёгкое",
                UnitHealthPartId.Stomach => "Желудок",
                UnitHealthPartId.Liver => "Печень",
                UnitHealthPartId.LeftKidney => "Левая почка",
                UnitHealthPartId.RightKidney => "Правая почка",
                UnitHealthPartId.LeftArm => "Левая рука",
                UnitHealthPartId.LeftHand => "Левая кисть",
                UnitHealthPartId.RightArm => "Правая рука",
                UnitHealthPartId.RightHand => "Правая кисть",
                UnitHealthPartId.LeftLeg => "Левая нога",
                UnitHealthPartId.LeftFoot => "Левая стопа",
                UnitHealthPartId.RightLeg => "Правая нога",
                UnitHealthPartId.RightFoot => "Правая стопа",
                _ => HumanizeId(id)
            }
        };
    }

    private static string HumanizeId(string id)
    {
        string value = (id ?? string.Empty).Replace('.', ' ').Replace('_', ' ').Trim();
        return value.Length == 0 ? "Анатомическая часть" : char.ToUpperInvariant(value[0]) + value.Substring(1);
    }
}
