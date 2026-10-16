using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Core.Items;

namespace Core.Unit;

/// <summary>
/// Persists the editable parts of a unit anatomy that do not belong to the render rig:
/// tissue health/ballistics, functional markers, hit thickness and internal organs.
/// </summary>
public static class UnitAnatomySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string FilePath =>
        Path.Combine(
            Path.GetDirectoryName(UnitBodyRigSettingsStore.FilePath) ?? Environment.CurrentDirectory,
            "colonist-anatomy-settings.json");

    public static bool Save(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        ReadOnlySpan<float> hitHalfHeights,
        HitRegionPartBinding[][] tissues,
        UnitAnatomyMarker[][] markers,
        ReadOnlySpan<UnitAnatomyVolume> volumes,
        ReadOnlySpan<UnitStartingEquipment> startingEquipment,
        out string error)
    {
        error = string.Empty;
        try
        {
            UnitAnatomyProfile current = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
            _ = current.WithAuthoringData(
                regionIds, bodyParts, hitHalfHeights, tissues, markers, volumes);

            var document = new SettingsDocument { Version = 1 };
            for (int i = 0; i < regionIds.Length; i++)
            {
                var region = new RegionDocument
                {
                    Id = regionIds[i],
                    HitHalfHeight = hitHalfHeights[i]
                };

                for (int j = 0; j < tissues[i].Length; j++)
                    region.Tissues.Add(TissueDocument.From(tissues[i][j]));
                for (int j = 0; j < markers[i].Length; j++)
                    region.Markers.Add(MarkerDocument.From(markers[i][j]));

                document.Regions.Add(region);
            }

            for (int i = 0; i < volumes.Length; i++)
                document.Volumes.Add(VolumeDocument.From(volumes[i]));
            document.Equipment = new List<EquipmentDocument>(startingEquipment.Length);
            for (int i = 0; i < startingEquipment.Length; i++)
                document.Equipment.Add(EquipmentDocument.From(startingEquipment[i]));

            string path = FilePath;
            string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Loads editor-authored metadata only when region IDs still match the saved rig.</summary>
    public static bool TryLoad(
        UnitAnatomyProfile fallback,
        out UnitAnatomyProfile profile) =>
        TryLoad(fallback, out profile, out _, out _);

    public static bool TryLoad(
        UnitAnatomyProfile fallback,
        out UnitAnatomyProfile profile,
        out UnitStartingEquipment[] startingEquipment,
        out bool hasEquipment)
    {
        profile = fallback;
        startingEquipment = Array.Empty<UnitStartingEquipment>();
        hasEquipment = false;
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
                return false;

            SettingsDocument? document =
                JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(path), JsonOptions);
            if (document == null || document.Version != 1)
                return false;

            ReadOnlySpan<UnitAnatomyNode> nodes = fallback.Nodes;
            ReadOnlySpan<BodyPartDefinition> parts = fallback.BodyParts;
            if (document.Regions.Count != nodes.Length)
                return false;

            var ids = new string[nodes.Length];
            var heights = new float[nodes.Length];
            var tissues = new HitRegionPartBinding[nodes.Length][];
            var markers = new UnitAnatomyMarker[nodes.Length][];

            for (int i = 0; i < nodes.Length; i++)
            {
                RegionDocument saved = document.Regions[i];
                if (!string.Equals(saved.Id, nodes[i].Id, StringComparison.Ordinal))
                    return false;

                ids[i] = saved.Id;
                heights[i] = saved.HitHalfHeight;
                tissues[i] = new HitRegionPartBinding[saved.Tissues.Count];
                markers[i] = new UnitAnatomyMarker[saved.Markers.Count];

                for (int j = 0; j < tissues[i].Length; j++)
                    tissues[i][j] = saved.Tissues[j].ToBinding();
                for (int j = 0; j < markers[i].Length; j++)
                    markers[i][j] = saved.Markers[j].ToMarker();
            }

            var volumes = new UnitAnatomyVolume[document.Volumes.Count];
            for (int i = 0; i < volumes.Length; i++)
                volumes[i] = document.Volumes[i].ToVolume();

            profile = fallback.WithAuthoringData(
                ids, parts, heights, tissues, markers, volumes);
            if (document.Equipment != null)
            {
                startingEquipment = new UnitStartingEquipment[document.Equipment.Count];
                for (int i = 0; i < startingEquipment.Length; i++)
                    startingEquipment[i] = document.Equipment[i].ToEquipment();
                hasEquipment = true;
            }
            return true;
        }
        catch (Exception)
        {
            // Keep the built-in profile when the saved file is malformed or references stale IDs.
            profile = fallback;
            startingEquipment = Array.Empty<UnitStartingEquipment>();
            hasEquipment = false;
            return false;
        }
    }

    private sealed class SettingsDocument
    {
        public SettingsDocument() { }

        public int Version { get; set; } = 1;
        public List<RegionDocument> Regions { get; set; } = new();
        public List<VolumeDocument> Volumes { get; set; } = new();
        public List<EquipmentDocument>? Equipment { get; set; }
    }

    private sealed class EquipmentDocument
    {
        public EquipmentDocument() { }

        public string ItemId { get; set; } = string.Empty;
        public float Durability { get; set; } = 1f;
        public ArmorDocument? Armor { get; set; }

        public static EquipmentDocument From(UnitStartingEquipment equipment) => new()
        {
            ItemId = GetApparelId(equipment.Item),
            Durability = equipment.Durability,
            Armor = ArmorDocument.From(equipment.Item)
        };

        public UnitStartingEquipment ToEquipment() =>
            new(
                string.Equals(ItemId, "custom", StringComparison.Ordinal)
                    ? Armor?.ToArmor() ?? throw new InvalidDataException("Для пользовательской брони отсутствуют параметры.")
                    : GetApparel(ItemId),
                Durability);
    }

    private sealed class ArmorDocument
    {
        public ArmorDocument() { }

        public string Name { get; set; } = "Custom armor";
        public float Weight { get; set; }
        public byte WorldColorR { get; set; } = 155;
        public byte WorldColorG { get; set; } = 160;
        public byte WorldColorB { get; set; } = 165;
        public int ProtectedZone { get; set; } = -1;
        public float DamageAbsorption { get; set; }
        public float ArmorDamageCoefficient { get; set; } = 0.25f;
        public float BleedProtectionChance { get; set; }
        public float EnergyLoss { get; set; }
        public float PenetrationResistance { get; set; }
        public float SharpRating { get; set; }
        public float BluntRating { get; set; }
        public float HeatRating { get; set; }
        public bool SoftArmor { get; set; }
        public float HardArmorDamageFactor { get; set; } = 0.5f;
        public float ArmorRating { get; set; }
        public ushort Coverage { get; set; }
        public byte Layer { get; set; }
        public float WorldRenderPadding { get; set; } = 0.075f;
        public float MovementSpeedMultiplier { get; set; } = 1f;

        public static ArmorDocument From(ArmorConfig item) => new()
        {
            Name = item.Name,
            Weight = item.Weight,
            WorldColorR = item.WorldColorR,
            WorldColorG = item.WorldColorG,
            WorldColorB = item.WorldColorB,
            ProtectedZone = item.ProtectedZone,
            DamageAbsorption = item.DamageAbsorption,
            ArmorDamageCoefficient = item.ArmorDamageCoefficient,
            BleedProtectionChance = item.BleedProtectionChance,
            EnergyLoss = item.EnergyLoss,
            PenetrationResistance = item.PenetrationResistance,
            SharpRating = item.SharpRating,
            BluntRating = item.BluntRating,
            HeatRating = item.HeatRating,
            SoftArmor = item.SoftArmor,
            HardArmorDamageFactor = item.HardArmorDamageFactor,
            ArmorRating = item.ArmorRating,
            Coverage = (ushort)item.Coverage,
            Layer = (byte)item.Layer,
            WorldRenderPadding = item.WorldRenderPadding,
            MovementSpeedMultiplier = item.MovementSpeedMultiplier
        };

        public ArmorConfig ToArmor() => new()
        {
            Name = Name,
            Weight = Weight,
            WorldColorR = WorldColorR,
            WorldColorG = WorldColorG,
            WorldColorB = WorldColorB,
            ProtectedZone = ProtectedZone,
            DamageAbsorption = DamageAbsorption,
            ArmorDamageCoefficient = ArmorDamageCoefficient,
            BleedProtectionChance = BleedProtectionChance,
            EnergyLoss = EnergyLoss,
            PenetrationResistance = PenetrationResistance,
            SharpRating = SharpRating,
            BluntRating = BluntRating,
            HeatRating = HeatRating,
            SoftArmor = SoftArmor,
            HardArmorDamageFactor = HardArmorDamageFactor,
            ArmorRating = ArmorRating,
            Coverage = (ArmorCoverage)Coverage,
            Layer = ParseEnum((ArmorLayer)Layer, ArmorLayer.Clothing),
            WorldRenderPadding = WorldRenderPadding,
            MovementSpeedMultiplier = MovementSpeedMultiplier
        };
    }

    private static string GetApparelId(ArmorConfig item)
    {
        if (ReferenceEquals(item, ApparelCatalog.WorkShirt)) return nameof(ApparelCatalog.WorkShirt);
        if (ReferenceEquals(item, ApparelCatalog.WorkTrousers)) return nameof(ApparelCatalog.WorkTrousers);
        if (ReferenceEquals(item, ApparelCatalog.CombatVest)) return nameof(ApparelCatalog.CombatVest);
        if (ReferenceEquals(item, ApparelCatalog.CombatHelmet)) return nameof(ApparelCatalog.CombatHelmet);
        if (ReferenceEquals(item, ApparelCatalog.PoweredExoskeleton)) return nameof(ApparelCatalog.PoweredExoskeleton);
        return "custom";
    }

    private static ArmorConfig GetApparel(string id) => id switch
    {
        nameof(ApparelCatalog.WorkShirt) => ApparelCatalog.WorkShirt,
        nameof(ApparelCatalog.WorkTrousers) => ApparelCatalog.WorkTrousers,
        nameof(ApparelCatalog.CombatVest) => ApparelCatalog.CombatVest,
        nameof(ApparelCatalog.CombatHelmet) => ApparelCatalog.CombatHelmet,
        nameof(ApparelCatalog.PoweredExoskeleton) => ApparelCatalog.PoweredExoskeleton,
        _ => throw new InvalidDataException($"Неизвестный ID стартового снаряжения: {id}.")
    };

    private sealed class RegionDocument
    {
        public RegionDocument() { }

        public string Id { get; set; } = string.Empty;
        public float HitHalfHeight { get; set; } = 0.1f;
        public List<TissueDocument> Tissues { get; set; } = new();
        public List<MarkerDocument> Markers { get; set; } = new();
    }

    private sealed class TissueDocument
    {
        public TissueDocument() { }

        public string Id { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public string Part { get; set; } = nameof(UnitHealthPartId.WholeBody);
        public string ParentPart { get; set; } = nameof(UnitHealthPartId.None);
        public string Kind { get; set; } = nameof(UnitHealthPartKind.BodyPart);
        public float Weight { get; set; } = 1f;
        public float MaxHitPoints { get; set; } = 30f;
        public int ArmorZone { get; set; } = -1;
        public float ProjectileEnergyLoss { get; set; } = -1f;
        public float ProjectilePenetrationLoss { get; set; } = -1f;

        public static TissueDocument From(HitRegionPartBinding tissue) => new()
        {
            Id = tissue.Id,
            ParentId = tissue.ParentId,
            Part = tissue.Part.ToString(),
            ParentPart = tissue.ParentPart.ToString(),
            Kind = tissue.Kind.ToString(),
            Weight = tissue.Weight,
            MaxHitPoints = tissue.MaxHitPoints,
            ArmorZone = tissue.ArmorZone,
            ProjectileEnergyLoss = tissue.ProjectileEnergyLoss,
            ProjectilePenetrationLoss = tissue.ProjectilePenetrationLoss
        };

        public HitRegionPartBinding ToBinding() => new(
            ParseEnum(Part, UnitHealthPartId.WholeBody),
            Weight,
            ParseEnum(Kind, UnitHealthPartKind.BodyPart),
            MaxHitPoints,
            ParseEnum(ParentPart, UnitHealthPartId.None),
            Id,
            ParentId,
            ArmorZone,
            ProjectileEnergyLoss,
            ProjectilePenetrationLoss);
    }

    private sealed class MarkerDocument
    {
        public MarkerDocument() { }

        public string Id { get; set; } = "target";
        public ushort Roles { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public static MarkerDocument From(UnitAnatomyMarker marker) => new()
        {
            Id = marker.Id,
            Roles = (ushort)marker.Roles,
            X = marker.LocalOffset.X,
            Y = marker.LocalOffset.Y,
            Z = marker.LocalOffset.Z
        };

        public UnitAnatomyMarker ToMarker() =>
            new(Id, (UnitAnatomyMarkerRole)Roles, new Vector3(X, Y, Z));
    }

    private sealed class VolumeDocument
    {
        public VolumeDocument() { }

        public string Id { get; set; } = string.Empty;
        public string ParentRegionId { get; set; } = string.Empty;
        public string EntryBindingId { get; set; } = string.Empty;
        public string TargetBindingId { get; set; } = string.Empty;
        public string Primitive { get; set; } = nameof(BodyPrimitive.Circle);
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float CenterZ { get; set; }
        public float HalfX { get; set; } = 0.01f;
        public float HalfY { get; set; } = 0.01f;
        public float HalfZ { get; set; } = 0.01f;
        public float Rotation { get; set; }
        public float EnergyLoss { get; set; } = 18f;
        public float PenetrationLoss { get; set; } = 1f;
        public float DamageMultiplier { get; set; } = 0.75f;
        public float BleedMultiplier { get; set; } = 1f;

        public static VolumeDocument From(UnitAnatomyVolume volume) => new()
        {
            Id = volume.Id,
            ParentRegionId = volume.ParentRegionId,
            EntryBindingId = volume.EntryBindingId,
            TargetBindingId = volume.TargetBindingId,
            Primitive = volume.Primitive.ToString(),
            CenterX = volume.LocalCenter.X,
            CenterY = volume.LocalCenter.Y,
            CenterZ = volume.LocalCenter.Z,
            HalfX = volume.HalfExtents.X,
            HalfY = volume.HalfExtents.Y,
            HalfZ = volume.HalfExtents.Z,
            Rotation = volume.LocalRotation,
            EnergyLoss = volume.EnergyLoss,
            PenetrationLoss = volume.PenetrationLoss,
            DamageMultiplier = volume.DamageMultiplier,
            BleedMultiplier = volume.BleedMultiplier
        };

        public UnitAnatomyVolume ToVolume() => new(
            Id,
            ParentRegionId,
            EntryBindingId,
            TargetBindingId,
            new Vector3(CenterX, CenterY, CenterZ),
            new Vector3(HalfX, HalfY, HalfZ),
            ParseEnum(Primitive, BodyPrimitive.Circle),
            Rotation,
            EnergyLoss,
            PenetrationLoss,
            DamageMultiplier,
            BleedMultiplier);
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;
}
