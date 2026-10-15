using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Core.Unit;

/// <summary>
/// Loads and persists user-authored unit archetypes. Definitions keep stats, named
/// body regions, damageable tissues, functional markers and internal organ volumes
/// together so the simulation uses the exact model written by the editor.
/// </summary>
public static class UnitArchetypeDefinitionStore
{
    private const int CurrentVersion = 1;
    private const ushort FirstCustomTypeId = 1000;
    private const ushort LastCustomTypeId = ushort.MaxValue;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static bool _loaded;
    private static readonly Dictionary<ushort, UnitArchetypeDocument> Entries = new();
    private static readonly Dictionary<ushort, UnitArchetypeProfile> Profiles = new();

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Defs", "Units", "custom-unit-types.json");

    public static string LastError { get; private set; } = string.Empty;

    public static bool TryGet(UnitType type, out UnitArchetypeProfile profile)
    {
        EnsureLoaded();
        lock (Sync)
            return Profiles.TryGetValue((ushort)type, out profile!);
    }

    public static string GetDisplayName(UnitType type)
    {
        EnsureLoaded();
        lock (Sync)
        {
            return Entries.TryGetValue((ushort)type, out UnitArchetypeDocument? entry)
                ? entry.DisplayName
                : type.ToString();
        }
    }

    public static ushort GetNextTypeId()
    {
        EnsureLoaded();
        lock (Sync)
        {
            for (int id = FirstCustomTypeId; id <= LastCustomTypeId; id++)
            {
                if (!Entries.ContainsKey((ushort)id))
                    return (ushort)id;
            }
        }

        throw new InvalidOperationException("Исчерпан диапазон пользовательских типов юнитов.");
    }

    public static bool CreateFromRig(
        string displayName,
        UnitDefinition template,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out UnitType type,
        out string error)
    {
        type = default;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            error = "Укажи название нового типа юнита.";
            return false;
        }

        if (!TryValidateRig(regionIds, parts, out error))
            return false;

        EnsureLoaded();
        lock (Sync)
        {
            ushort reservedId = 0;
            try
            {
                ushort numericId = GetNextTypeId();
                reservedId = numericId;
                var entry = new UnitArchetypeDocument
                {
                    TypeId = numericId,
                    Id = $"custom-unit-{numericId}",
                    DisplayName = displayName.Trim(),
                    MoveSpeed = template.MoveSpeed,
                    Width = template.Width,
                    Length = template.Length,
                    Height = template.Height,
                    ViewRange = template.ViewRange,
                    FieldOfView = template.FieldOfView,
                    BodyType = UnitBodyType.Custom,
                    Regions = CreateRegions(regionIds, parts, previous: null),
                    Volumes = new List<VolumeDocument>()
                };

                UnitArchetypeProfile profile = BuildProfile(entry);
                Persist(entry);
                Entries.Add(numericId, entry);
                Profiles.Add(numericId, profile);
                type = (UnitType)numericId;
                return true;
            }
            catch (Exception exception) when (IsExpectedIoOrDataException(exception))
            {
                if (reservedId >= FirstCustomTypeId)
                {
                    Entries.Remove(reservedId);
                    Profiles.Remove(reservedId);
                }
                error = exception.Message;
                return false;
            }
        }
    }

    public static bool SaveRig(
        UnitType type,
        string displayName,
        float moveSpeed,
        float width,
        float length,
        float height,
        float viewRange,
        float fieldOfView,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string error)
    {
        error = string.Empty;
        ushort id = (ushort)type;
        if (id < FirstCustomTypeId)
        {
            error = "Нельзя сохранить пользовательский тип поверх встроенного.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            error = "Название типа не может быть пустым.";
            return false;
        }
        if (!IsValidStat(moveSpeed, 0.05f, 100f) ||
            !IsValidStat(width, 0.01f, 100f) ||
            !IsValidStat(length, 0.01f, 100f) ||
            !IsValidStat(height, 0.01f, 100f) ||
            !IsValidStat(viewRange, 0.1f, 10000f) ||
            !IsValidStat(fieldOfView, 1f, 360f))
        {
            error = "Одна или несколько характеристик вне допустимого диапазона.";
            return false;
        }
        if (!TryValidateRig(regionIds, parts, out error))
            return false;

        EnsureLoaded();
        lock (Sync)
        {
            try
            {
                if (!Entries.TryGetValue(id, out UnitArchetypeDocument? entry))
                {
                    error = $"Тип {(int)id} не найден. Создай его через редактор.";
                    return false;
                }

                UnitArchetypeDocument previousEntry = entry;
                UnitArchetypeProfile previousProfile = Profiles[id];
                List<RegionDocument> regions = CreateRegions(regionIds, parts, entry);
                var knownRegionIds = new HashSet<string>(regions.Select(r => r.Id), StringComparer.Ordinal);
                var knownBindingIds = new HashSet<string>(
                    regions.SelectMany(r => r.Tissues).Select(t => t.Id), StringComparer.Ordinal);
                var volumes = (entry.Volumes ?? new List<VolumeDocument>())
                    .Where(v =>
                        knownRegionIds.Contains(v.ParentRegionId) &&
                        knownBindingIds.Contains(v.EntryBindingId) &&
                        knownBindingIds.Contains(v.TargetBindingId))
                    .ToList();

                var updated = new UnitArchetypeDocument
                {
                    TypeId = id,
                    Id = entry.Id,
                    DisplayName = displayName.Trim(),
                    MoveSpeed = moveSpeed,
                    Width = width,
                    Length = length,
                    Height = height,
                    ViewRange = viewRange,
                    FieldOfView = fieldOfView,
                    BodyType = UnitBodyType.Custom,
                    Regions = regions,
                    Volumes = volumes
                };

                UnitArchetypeProfile profile = BuildProfile(updated);
                Persist(updated);
                Entries[id] = updated;
                Profiles[id] = profile;
                return true;
            }
            catch (Exception exception) when (IsExpectedIoOrDataException(exception))
            {
                error = exception.Message;
                return false;
            }
        }
    }

    public static bool AddOrgan(UnitType type, string regionId, out string organId, out string error)
    {
        organId = string.Empty;
        error = string.Empty;
        ushort id = (ushort)type;
        if (id < FirstCustomTypeId)
        {
            error = "Органы можно добавлять только в пользовательский тип.";
            return false;
        }

        EnsureLoaded();
        lock (Sync)
        {
            try
            {
                if (!Entries.TryGetValue(id, out UnitArchetypeDocument? storedEntry))
                {
                    error = "Сначала создай и сохрани пользовательский тип.";
                    return false;
                }

                UnitArchetypeDocument entry = CloneDocument(storedEntry);
                UnitArchetypeProfile previousProfile = Profiles[id];
                RegionDocument? region = entry.Regions.FirstOrDefault(
                    r => string.Equals(r.Id, regionId, StringComparison.Ordinal));
                if (region == null)
                {
                    error = $"Регион '{regionId}' не найден.";
                    return false;
                }

                int organNumber = 1;
                string candidate;
                do
                {
                    candidate = $"organ.{region.Id}.{organNumber++:00}";
                }
                while (entry.Regions.Any(r => r.Tissues.Any(t =>
                    string.Equals(t.Id, candidate + ".tissue", StringComparison.Ordinal))) ||
                    entry.Volumes.Any(v => string.Equals(v.Id, candidate, StringComparison.Ordinal)));

                string tissueId = candidate + ".tissue";
                TissueDocument entryTissue = region.Tissues[0];
                region.Tissues.Add(new TissueDocument
                {
                    Id = tissueId,
                    Name = candidate,
                    ParentId = entryTissue.Id,
                    Part = UnitHealthPartId.None,
                    Weight = 0.15f,
                    Kind = UnitHealthPartKind.Organ,
                    MaxHitPoints = 25f,
                    ParentPart = UnitHealthPartId.None,
                    ArmorZone = -1,
                    ProjectileEnergyLoss = 8f,
                    ProjectilePenetrationLoss = 0.8f
                });

                RegionDocument root = entry.Regions.FirstOrDefault(r => r.Id == region.Id) ?? region;
                float halfX = MathF.Max(0.005f, MathF.Abs(root.ScaleX) * 0.30f);
                float halfY = MathF.Max(0.005f, MathF.Abs(root.ScaleY) * 0.30f);
                float halfZ = MathF.Max(0.005f, MathF.Abs(root.ScaleZ) * 0.30f);
                entry.Volumes.Add(new VolumeDocument
                {
                    Id = candidate,
                    ParentRegionId = region.Id,
                    EntryBindingId = entryTissue.Id,
                    TargetBindingId = tissueId,
                    CenterX = 0f,
                    CenterY = 0f,
                    CenterZ = 0f,
                    HalfX = halfX,
                    HalfY = halfY,
                    HalfZ = halfZ,
                    Primitive = BodyPrimitive.Circle,
                    LocalRotation = 0f,
                    EnergyLoss = 8f,
                    PenetrationLoss = 0.8f,
                    DamageMultiplier = 0.7f,
                    BleedMultiplier = 0.9f
                });

                UnitArchetypeProfile profile = BuildProfile(entry);
                Persist(entry);
                Entries[id] = entry;
                Profiles[id] = profile;
                organId = candidate;
                return true;
            }
            catch (Exception exception) when (IsExpectedIoOrDataException(exception))
            {
                error = exception.Message;
                return false;
            }
        }
    }

    private static UnitArchetypeDocument CloneDocument(UnitArchetypeDocument source) =>
        JsonSerializer.Deserialize<UnitArchetypeDocument>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("Не удалось клонировать описание пользовательского типа.");

    private static List<RegionDocument> CreateRegions(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        UnitArchetypeDocument? previous)
    {
        var previousById = previous?.Regions.ToDictionary(r => r.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, RegionDocument>(StringComparer.Ordinal);
        var regions = new List<RegionDocument>(parts.Length);

        for (int i = 0; i < parts.Length; i++)
        {
            BodyPartDefinition part = parts[i];
            string parentId = part.ParentIndex >= 0 ? regionIds[part.ParentIndex] : string.Empty;
            if (previousById.TryGetValue(regionIds[i], out RegionDocument? old))
            {
                regions.Add(new RegionDocument
                {
                    Id = regionIds[i],
                    ParentId = parentId,
                    PositionX = part.LocalPosition.X,
                    PositionY = part.LocalPosition.Y,
                    PositionZ = part.LocalPosition.Z,
                    ScaleX = part.Scale.X,
                    ScaleY = part.Scale.Y,
                    ScaleZ = part.Scale.Z,
                    RotationX = part.LocalRotationX,
                    RotationY = part.LocalRotationY,
                    RotationZ = part.LocalRotation,
                    Primitive = part.Primitive,
                    Motion = part.Motion,
                    FollowDistance = part.FollowDistance,
                    Anchor = part.Anchor,
                    Role = part.Role,
                    Pivot = part.Pivot,
                    HitHalfHeight = MathF.Max(0.001f, MathF.Abs(part.Scale.Z)),
                    Tissues = old.Tissues,
                    Markers = old.Markers
                });
                continue;
            }

            string tissueId = $"{regionIds[i]}.tissue";
            var tissues = new List<TissueDocument>
            {
                new()
                {
                    Id = tissueId,
                    Name = regionIds[i],
                    ParentId = part.ParentIndex >= 0
                        ? GetPrimaryTissueId(regions[part.ParentIndex])
                        : string.Empty,
                    Part = i == 0 ? UnitHealthPartId.WholeBody : UnitHealthPartId.None,
                    Weight = 1f,
                    Kind = UnitHealthPartKind.BodyPart,
                    MaxHitPoints = i == 0 ? 100f : 45f,
                    ParentPart = UnitHealthPartId.None,
                    ArmorZone = -1,
                    ProjectileEnergyLoss = -1f,
                    ProjectilePenetrationLoss = -1f
                }
            };

            var markers = new List<MarkerDocument>();
            if (i == 0 || part.Role == BodyPartRole.Torso)
            {
                markers.Add(new MarkerDocument
                {
                    Id = "target",
                    Roles = UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision
                });
                markers.Add(new MarkerDocument
                {
                    Id = "locomotion",
                    Roles = UnitAnatomyMarkerRole.Locomotion
                });
            }
            if (part.Role == BodyPartRole.Head)
            {
                markers.Add(new MarkerDocument
                {
                    Id = "eye",
                    Roles = UnitAnatomyMarkerRole.Eye | UnitAnatomyMarkerRole.Vision,
                    OffsetZ = MathF.Max(0f, part.Scale.Z * 0.25f)
                });
                markers.Add(new MarkerDocument
                {
                    Id = "target",
                    Roles = UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision
                });
            }

            regions.Add(new RegionDocument
            {
                Id = regionIds[i],
                ParentId = parentId,
                PositionX = part.LocalPosition.X,
                PositionY = part.LocalPosition.Y,
                PositionZ = part.LocalPosition.Z,
                ScaleX = part.Scale.X,
                ScaleY = part.Scale.Y,
                ScaleZ = part.Scale.Z,
                RotationX = part.LocalRotationX,
                RotationY = part.LocalRotationY,
                RotationZ = part.LocalRotation,
                Primitive = part.Primitive,
                Motion = part.Motion,
                FollowDistance = part.FollowDistance,
                Anchor = part.Anchor,
                Role = part.Role,
                Pivot = part.Pivot,
                HitHalfHeight = MathF.Max(0.001f, MathF.Abs(part.Scale.Z)),
                Tissues = tissues,
                Markers = markers
            });
        }

        return regions;
    }

    private static string GetPrimaryTissueId(RegionDocument region) =>
        region.Tissues.Count > 0 ? region.Tissues[0].Id : $"{region.Id}.tissue";

    private static UnitArchetypeProfile BuildProfile(UnitArchetypeDocument entry)
    {
        if (entry.TypeId < FirstCustomTypeId || string.IsNullOrWhiteSpace(entry.DisplayName) ||
            !IsValidStat(entry.MoveSpeed, 0.05f, 100f) ||
            !IsValidStat(entry.Width, 0.01f, 100f) ||
            !IsValidStat(entry.Length, 0.01f, 100f) ||
            !IsValidStat(entry.Height, 0.01f, 100f) ||
            !IsValidStat(entry.ViewRange, 0.1f, 10000f) ||
            !IsValidStat(entry.FieldOfView, 1f, 360f) ||
            entry.Regions == null || entry.Regions.Count is < 1 or > 128)
            throw new InvalidDataException($"Тип '{entry.Id}' имеет неверный ID, характеристики или число регионов.");

        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entry.Regions.Count; i++)
        {
            RegionDocument region = entry.Regions[i];
            if (string.IsNullOrWhiteSpace(region.Id) || !indexById.TryAdd(region.Id, i))
                throw new InvalidDataException($"У типа '{entry.Id}' пустой или повторяющийся ID региона.");
            if (!IsValidStat(region.ScaleX, 0.001f, 100f) ||
                !IsValidStat(region.ScaleY, 0.001f, 100f) ||
                !IsValidStat(region.ScaleZ, 0.001f, 100f) ||
                !float.IsFinite(region.PositionX) || !float.IsFinite(region.PositionY) ||
                !float.IsFinite(region.PositionZ) || !float.IsFinite(region.RotationX) ||
                !float.IsFinite(region.RotationY) || !float.IsFinite(region.RotationZ) ||
                !IsValidStat(region.HitHalfHeight, 0.001f, 100f) ||
                region.Tissues == null || region.Tissues.Count == 0 ||
                region.Markers == null)
                throw new InvalidDataException($"Регион '{region.Id}' содержит некорректную геометрию или список тканей.");
            foreach (TissueDocument tissue in region.Tissues)
            {
                if (string.IsNullOrWhiteSpace(tissue.Id) || !bindingIds.Add(tissue.Id) ||
                    !IsValidStat(tissue.Weight, 0.001f, 1000f) ||
                    !IsValidStat(tissue.MaxHitPoints, 0.01f, 1000000f) ||
                    !float.IsFinite(tissue.ProjectileEnergyLoss) || tissue.ProjectileEnergyLoss < -1f ||
                    !float.IsFinite(tissue.ProjectilePenetrationLoss) || tissue.ProjectilePenetrationLoss < -1f)
                    throw new InvalidDataException($"Ткань в регионе '{region.Id}' содержит некорректные данные или ID.");
            }
            foreach (MarkerDocument marker in region.Markers)
            {
                if (string.IsNullOrWhiteSpace(marker.Id) ||
                    !float.IsFinite(marker.OffsetX) || !float.IsFinite(marker.OffsetY) ||
                    !float.IsFinite(marker.OffsetZ))
                    throw new InvalidDataException($"Маркер в регионе '{region.Id}' содержит некорректные данные.");
            }
            if (i == 0 && !string.IsNullOrWhiteSpace(region.ParentId))
                throw new InvalidDataException("Первый регион должен быть корневым.");
            if (i > 0 && !string.IsNullOrWhiteSpace(region.ParentId) &&
                (!indexById.TryGetValue(region.ParentId, out int parentIndex) || parentIndex >= i))
                throw new InvalidDataException($"Родитель '{region.ParentId}' должен быть объявлен перед дочерним регионом.");
        }

        var nodes = new UnitAnatomyNode[entry.Regions.Count];
        for (int i = 0; i < entry.Regions.Count; i++)
        {
            RegionDocument region = entry.Regions[i];
            int parentIndex = string.IsNullOrWhiteSpace(region.ParentId) ? -1 : indexById[region.ParentId];
            var shape = new BodyPartDefinition(
                new Vector3(region.PositionX, region.PositionY, region.PositionZ),
                new Vector3(region.ScaleX, region.ScaleY, region.ScaleZ),
                region.Primitive,
                parentIndex,
                region.RotationZ,
                region.Motion,
                region.FollowDistance,
                region.Anchor,
                region.Role,
                region.Pivot,
                region.RotationX,
                region.RotationY);

            HitRegionPartBinding[] tissues = region.Tissues.Select(t => new HitRegionPartBinding(
                t.Part, t.Weight, t.Kind, t.MaxHitPoints, t.ParentPart,
                t.Id, t.ParentId, t.ArmorZone, t.ProjectileEnergyLoss, t.ProjectilePenetrationLoss)).ToArray();
            if (tissues.Length == 0)
                throw new InvalidDataException($"Регион '{region.Id}' должен иметь хотя бы одну ткань.");

            UnitAnatomyMarker[] markers = region.Markers.Select(m => new UnitAnatomyMarker(
                m.Id, m.Roles, new Vector3(m.OffsetX, m.OffsetY, m.OffsetZ))).ToArray();
            nodes[i] = UnitAnatomyNode.Part(
                region.Id, region.ParentId, shape, region.HitHalfHeight, tissues, markers);
        }

        foreach (VolumeDocument volume in entry.Volumes ?? new List<VolumeDocument>())
        {
            if (string.IsNullOrWhiteSpace(volume.Id) ||
                !indexById.ContainsKey(volume.ParentRegionId) ||
                !bindingIds.Contains(volume.EntryBindingId) ||
                !bindingIds.Contains(volume.TargetBindingId))
                throw new InvalidDataException($"Внутренний объём '{volume.Id}' ссылается на отсутствующий регион или ткань.");
        }

        UnitAnatomyVolume[] volumes = (entry.Volumes ?? new List<VolumeDocument>())
            .Select(v => new UnitAnatomyVolume(
                v.Id, v.ParentRegionId, v.EntryBindingId, v.TargetBindingId,
                new Vector3(v.CenterX, v.CenterY, v.CenterZ),
                new Vector3(v.HalfX, v.HalfY, v.HalfZ),
                v.Primitive, v.LocalRotation, v.EnergyLoss, v.PenetrationLoss,
                v.DamageMultiplier, v.BleedMultiplier)).ToArray();

        var anatomy = new UnitAnatomyProfile(volumes, nodes);
        var definition = new UnitDefinition(
            (UnitType)entry.TypeId,
            UnitBodyType.Custom,
            entry.MoveSpeed,
            entry.Width,
            entry.Length,
            entry.Height,
            entry.ViewRange,
            entry.FieldOfView);
        return new UnitArchetypeProfile(definition, anatomy);
    }

    private static bool TryValidateRig(
        ReadOnlySpan<string> ids,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string error)
    {
        error = string.Empty;
        if (ids.Length == 0 || ids.Length != parts.Length || ids.Length > 128)
        {
            error = "Для типа нужно от 1 до 128 регионов и одна форма на каждый регион.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++)
        {
            BodyPartDefinition part = parts[i];
            if (string.IsNullOrWhiteSpace(ids[i]) || !seen.Add(ids[i]))
            {
                error = $"Пустой или повторяющийся ID региона: '{ids[i]}'.";
                return false;
            }
            if (part.ParentIndex < -1 || part.ParentIndex >= i)
            {
                error = $"Родитель региона '{ids[i]}' должен находиться выше него в списке.";
                return false;
            }
            if (!IsFinite(part.LocalPosition) || !IsFinite(part.Scale) ||
                part.Scale.X <= 0f || part.Scale.Y <= 0f || part.Scale.Z <= 0f ||
                !float.IsFinite(part.LocalRotationX) ||
                !float.IsFinite(part.LocalRotationY) ||
                !float.IsFinite(part.LocalRotation) ||
                !float.IsFinite(part.FollowDistance))
            {
                error = $"У региона '{ids[i]}' нечисловая позиция, форма или ориентация.";
                return false;
            }
        }

        return true;
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (Sync)
        {
            if (_loaded)
                return;

            _loaded = true;
            if (!File.Exists(FilePath))
                return;

            try
            {
                DefinitionDocument? document = JsonSerializer.Deserialize<DefinitionDocument>(
                    File.ReadAllText(FilePath), JsonOptions);
                if (document == null || document.Version != CurrentVersion || document.Types == null)
                    throw new InvalidDataException("Файл типов юнитов имеет неподдерживаемый формат.");

                foreach (UnitArchetypeDocument entry in document.Types)
                {
                    if (entry.TypeId < FirstCustomTypeId ||
                        Entries.ContainsKey(entry.TypeId))
                    {
                        LastError = $"Пропущен некорректный или дублирующий ID типа: {entry.TypeId}.";
                        continue;
                    }

                    try
                    {
                        UnitArchetypeProfile profile = BuildProfile(entry);
                        Entries.Add(entry.TypeId, entry);
                        Profiles.Add(entry.TypeId, profile);
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or InvalidDataException or KeyNotFoundException or IndexOutOfRangeException)
                    {
                        LastError = $"Тип '{entry.DisplayName}' пропущен: {exception.Message}";
                    }
                }
            }
            catch (Exception exception) when (IsExpectedIoOrDataException(exception))
            {
                LastError = exception.Message;
            }
        }
    }

    private static void Persist(UnitArchetypeDocument replacement)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        List<UnitArchetypeDocument> values = Entries.Values
            .Where(entry => entry.TypeId != replacement.TypeId)
            .Append(replacement)
            .OrderBy(entry => entry.TypeId)
            .ToList();
        var document = new DefinitionDocument
        {
            Version = CurrentVersion,
            Types = values
        };
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
        LastError = string.Empty;
    }

    private static bool IsValidStat(float value, float min, float max) =>
        float.IsFinite(value) && value >= min && value <= max;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsExpectedIoOrDataException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            ArgumentException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class DefinitionDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public List<UnitArchetypeDocument> Types { get; set; } = new();
    }

    private sealed class UnitArchetypeDocument
    {
        public ushort TypeId { get; set; }
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public UnitBodyType BodyType { get; set; } = UnitBodyType.Custom;
        public float MoveSpeed { get; set; } = 2.8f;
        public float Width { get; set; } = 0.35f;
        public float Length { get; set; } = 0.6f;
        public float Height { get; set; } = 1.75f;
        public float ViewRange { get; set; } = 20f;
        public float FieldOfView { get; set; } = 110f;
        public List<RegionDocument> Regions { get; set; } = new();
        public List<VolumeDocument> Volumes { get; set; } = new();
    }

    private sealed class RegionDocument
    {
        public string Id { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float ScaleX { get; set; } = 0.1f;
        public float ScaleY { get; set; } = 0.1f;
        public float ScaleZ { get; set; } = 0.1f;
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public BodyPrimitive Primitive { get; set; } = BodyPrimitive.Circle;
        public BodyPartMotion Motion { get; set; } = BodyPartMotion.StaticRelative;
        public float FollowDistance { get; set; }
        public BodyPartAnchor Anchor { get; set; } = BodyPartAnchor.Parent;
        public BodyPartRole Role { get; set; } = BodyPartRole.Generic;
        public BodyPartPivot Pivot { get; set; } = BodyPartPivot.Center;
        public float HitHalfHeight { get; set; } = 0.1f;
        public List<TissueDocument> Tissues { get; set; } = new();
        public List<MarkerDocument> Markers { get; set; } = new();
    }

    private sealed class TissueDocument
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public UnitHealthPartId Part { get; set; } = UnitHealthPartId.WholeBody;
        public float Weight { get; set; } = 1f;
        public UnitHealthPartKind Kind { get; set; } = UnitHealthPartKind.BodyPart;
        public float MaxHitPoints { get; set; } = 100f;
        public UnitHealthPartId ParentPart { get; set; } = UnitHealthPartId.None;
        public int ArmorZone { get; set; } = -1;
        public float ProjectileEnergyLoss { get; set; } = -1f;
        public float ProjectilePenetrationLoss { get; set; } = -1f;
    }

    private sealed class MarkerDocument
    {
        public string Id { get; set; } = string.Empty;
        public UnitAnatomyMarkerRole Roles { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
    }

    private sealed class VolumeDocument
    {
        public string Id { get; set; } = string.Empty;
        public string ParentRegionId { get; set; } = string.Empty;
        public string EntryBindingId { get; set; } = string.Empty;
        public string TargetBindingId { get; set; } = string.Empty;
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float CenterZ { get; set; }
        public float HalfX { get; set; } = 0.025f;
        public float HalfY { get; set; } = 0.025f;
        public float HalfZ { get; set; } = 0.025f;
        public BodyPrimitive Primitive { get; set; } = BodyPrimitive.Circle;
        public float LocalRotation { get; set; }
        public float EnergyLoss { get; set; } = 18f;
        public float PenetrationLoss { get; set; } = 1f;
        public float DamageMultiplier { get; set; } = 0.75f;
        public float BleedMultiplier { get; set; } = 1f;
    }
}
