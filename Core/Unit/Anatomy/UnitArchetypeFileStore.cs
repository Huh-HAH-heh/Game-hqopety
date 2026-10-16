using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Core.Items;

namespace Core.Unit;

/// <summary>
/// One complete unit archetype plus all editor-authored data. The same object feeds
/// the editor, runtime catalog and unit spawner, so there is only one source of truth.
/// </summary>
public sealed class UnitArchetypeAsset
{
    private readonly string[] _regionIds;
    private readonly BodyPartDefinition[] _bodyParts;
    private readonly float[] _hitHalfHeights;
    private readonly HitRegionPartBinding[][] _tissues;
    private readonly UnitAnatomyMarker[][] _markers;
    private readonly UnitAnatomyVolume[] _organs;
    private readonly UnitStartingEquipment[] _startingEquipment;

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string FilePath { get; }
    public ushort RuntimeTypeId { get; }
    public UnitDefinition Definition { get; }
    public UnitAnatomyProfile Anatomy { get; }
    public UnitArchetypeProfile Profile { get; }
    public string[] RegionIds => (string[])_regionIds.Clone();
    public BodyPartDefinition[] BodyParts => (BodyPartDefinition[])_bodyParts.Clone();
    public float[] HitHalfHeights => (float[])_hitHalfHeights.Clone();
    public HitRegionPartBinding[][] Tissues => CloneRows(_tissues);
    public UnitAnatomyMarker[][] Markers => CloneRows(_markers);
    public UnitAnatomyVolume[] Organs => (UnitAnatomyVolume[])_organs.Clone();
    public UnitStartingEquipment[] StartingEquipment => (UnitStartingEquipment[])_startingEquipment.Clone();

    internal UnitArchetypeAsset(
        string id,
        string displayName,
        string description,
        string filePath,
        ushort runtimeTypeId,
        UnitDefinition definition,
        string[] regionIds,
        BodyPartDefinition[] bodyParts,
        float[] hitHalfHeights,
        HitRegionPartBinding[][] tissues,
        UnitAnatomyMarker[][] markers,
        UnitAnatomyVolume[] organs,
        UnitStartingEquipment[] startingEquipment)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        FilePath = filePath;
        RuntimeTypeId = runtimeTypeId;
        Definition = definition;
        _regionIds = (string[])regionIds.Clone();
        _bodyParts = (BodyPartDefinition[])bodyParts.Clone();
        _hitHalfHeights = (float[])hitHalfHeights.Clone();
        _tissues = CloneRows(tissues);
        _markers = CloneRows(markers);
        _organs = (UnitAnatomyVolume[])organs.Clone();
        _startingEquipment = (UnitStartingEquipment[])startingEquipment.Clone();

        var nodes = new UnitAnatomyNode[_regionIds.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            string parentId = _bodyParts[i].ParentIndex >= 0
                ? _regionIds[_bodyParts[i].ParentIndex]
                : string.Empty;
            nodes[i] = UnitAnatomyNode.Part(
                _regionIds[i], parentId, _bodyParts[i],
                _hitHalfHeights[i], _tissues[i], _markers[i]);
        }

        Anatomy = new UnitAnatomyProfile(_organs, nodes);
        Profile = new UnitArchetypeProfile(
            Definition, Anatomy, _startingEquipment);
    }

    private static T[][] CloneRows<T>(T[][] source)
    {
        var result = new T[source.Length][];
        for (int i = 0; i < source.Length; i++)
            result[i] = (T[])source[i].Clone();
        return result;
    }
}

/// <summary>
/// Human-editable, one-file-per-unit project assets. Only top-level *.unit.json files
/// in Data/Units are loaded by the game; Templates/ can therefore safely contain starters.
/// </summary>
public static class UnitArchetypeFileStore
{
    public const int SchemaVersion = 1;
    public const ushort FirstCustomRuntimeTypeId = 1024;
    public const ushort LastCustomRuntimeTypeId = ushort.MaxValue - 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ProjectDirectory =>
        Path.GetDirectoryName(UnitBodyRigSettingsStore.FilePath) ?? AppContext.BaseDirectory;

    public static string TemplatesDirectory =>
        Path.Combine(ProjectDirectory, "Templates");

    public static ushort GetNextRuntimeTypeId()
    {
        var used = new HashSet<ushort>
        {
            (ushort)UnitType.Colonist,
            (ushort)UnitType.Greenbob,
            (ushort)UnitType.SegmentedMonster
        };

        foreach (string path in EnumerateProjectFiles())
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("runtimeTypeId", out JsonElement id) &&
                    id.TryGetUInt16(out ushort value))
                    used.Add(value);
            }
            catch (Exception)
            {
                // An incomplete file is ignored here; TryLoadFile reports its exact error.
            }
        }

        for (int value = FirstCustomRuntimeTypeId; value <= LastCustomRuntimeTypeId; value++)
        {
            if (!used.Contains((ushort)value))
                return (ushort)value;
        }

        throw new InvalidOperationException("Исчерпан диапазон ID пользовательских архетипов юнитов.");
    }

    public static string GetProjectFilePath(string id) =>
        Path.Combine(ProjectDirectory, NormalizeId(id) + ".unit.json");

    public static UnitArchetypeAsset CreateBlank(
        string id,
        string displayName,
        ushort runtimeTypeId)
    {
        id = NormalizeId(id);
        ValidateRuntimeTypeId(runtimeTypeId);
        var regionIds = new[] { "body.core" };
        var bodyParts = new[]
        {
            new BodyPartDefinition(
                new Vector3(0f, 0f, 0.82f),
                new Vector3(0.18f, 0.12f, 0.16f),
                BodyPrimitive.Circle,
                parentIndex: -1,
                role: BodyPartRole.Torso,
                pivot: BodyPartPivot.Center,
                displayName: "Тело")
        };
        var tissues = new[]
        {
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody,
                    weight: 1f,
                    kind: UnitHealthPartKind.BodyPart,
                    maxHitPoints: 100f,
                    id: "body.core.tissue")
            }
        };
        var markers = new[]
        {
            new[]
            {
                new UnitAnatomyMarker(
                    "target",
                    UnitAnatomyMarkerRole.Target |
                    UnitAnatomyMarkerRole.Aim |
                    UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)
            }
        };
        var definition = new UnitDefinition(
            (UnitType)runtimeTypeId, UnitBodyType.Colonist,
            moveSpeed: 2.8f, width: 0.36f, length: 0.50f, height: 1.0f,
            viewRange: 20f, fieldOfView: 110f);

        return new UnitArchetypeAsset(
            id, displayName, string.Empty, string.Empty, runtimeTypeId, definition,
            regionIds, bodyParts, new[] { 0.16f }, tissues, markers,
            Array.Empty<UnitAnatomyVolume>(), Array.Empty<UnitStartingEquipment>());
    }

    public static bool SaveProjectFile(
        string path,
        string id,
        string displayName,
        string description,
        ushort runtimeTypeId,
        UnitDefinition definition,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        ReadOnlySpan<float> hitHalfHeights,
        HitRegionPartBinding[][] tissues,
        UnitAnatomyMarker[][] markers,
        ReadOnlySpan<UnitAnatomyVolume> organs,
        ReadOnlySpan<UnitStartingEquipment> startingEquipment,
        out UnitArchetypeAsset? asset,
        out string error)
    {
        asset = null;
        error = string.Empty;
        try
        {
            string fullPath = Path.GetFullPath(path);
            string projectDirectory = Path.GetFullPath(ProjectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Файл юнита должен находиться в папке проекта: {ProjectDirectory}");
            if (!fullPath.EndsWith(".unit.json", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(ProjectDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл архетипа должен иметь расширение .unit.json и находиться прямо в Data/Units.");
            if (string.IsNullOrWhiteSpace(displayName))
                throw new InvalidDataException("Укажи отображаемое имя юнита.");
            ValidateRuntimeTypeId(runtimeTypeId);

            id = NormalizeId(id);
            if (definition.Type != (UnitType)runtimeTypeId)
                definition = new UnitDefinition(
                    (UnitType)runtimeTypeId, definition.BodyType,
                    definition.MoveSpeed, definition.Width, definition.Length,
                    definition.Height, definition.ViewRange, definition.FieldOfView);

            string expectedFileName = id + ".unit.json";
            if (!string.Equals(Path.GetFileName(fullPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Имя файла должно совпадать с ID: {expectedFileName}");

            UnitArchetypeAsset candidate = BuildAsset(
                id, displayName.Trim(), description ?? string.Empty, fullPath,
                runtimeTypeId, definition, regionIds.ToArray(), bodyParts.ToArray(),
                hitHalfHeights.ToArray(), tissues, markers, organs.ToArray(),
                startingEquipment.ToArray());

            EnsureRuntimeTypeAvailable(fullPath, runtimeTypeId);
            EnsureIdAvailable(fullPath, id);
            UnitAssetDocument document = CreateDocument(candidate);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new IOException("Не удалось определить папку сохранения.");
            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);

            if (!TryLoadFile(fullPath, out asset, out error))
                return false;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryLoadFile(
        string path,
        out UnitArchetypeAsset? asset,
        out string error)
    {
        asset = null;
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Файл юнита не найден.", path);

            string fullPath = Path.GetFullPath(path);
            UnitAssetDocument? document =
                JsonSerializer.Deserialize<UnitAssetDocument>(File.ReadAllText(fullPath), JsonOptions);
            if (document == null || document.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"Неподдерживаемая версия unit-файла. Требуется версия {SchemaVersion}.");
            if (document.Regions == null || document.Organs == null || document.StartingEquipment == null)
                throw new InvalidDataException("Файл должен содержать regions, organs и startingEquipment.");
            if (string.IsNullOrWhiteSpace(document.DisplayName))
                throw new InvalidDataException("В unit-файле отсутствует displayName.");

            ValidateRuntimeTypeId(document.RuntimeTypeId);
            string id = NormalizeId(document.Id);
            UnitType unitType = (UnitType)document.RuntimeTypeId;
            UnitBodyType bodyType = ParseEnum(document.Stats?.BodyType, UnitBodyType.Colonist);
            UnitAssetStats stats = document.Stats
                ?? throw new InvalidDataException("В unit-файле отсутствует блок stats.");
            var definition = new UnitDefinition(
                unitType, bodyType, stats.MoveSpeed, stats.Width, stats.Length,
                stats.Height, stats.ViewRange, stats.FieldOfView);

            int count = document.Regions.Count;
            if (count == 0 || count > 256)
                throw new InvalidDataException("Количество regions должно быть от 1 до 256.");

            var ids = new string[count];
            var parts = new BodyPartDefinition[count];
            var heights = new float[count];
            var tissues = new HitRegionPartBinding[count][];
            var markers = new UnitAnatomyMarker[count][];
            var knownRegionIds = new HashSet<string>(StringComparer.Ordinal);
            var knownTissueIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                RegionDocument region = document.Regions[i]
                    ?? throw new InvalidDataException($"Region #{i + 1} равен null.");
                if (string.IsNullOrWhiteSpace(region.Id) || !knownRegionIds.Add(region.Id))
                    throw new InvalidDataException($"Пустой или повторяющийся ID региона: '{region.Id}'.");
                if (region.Part == null || !string.Equals(region.Part.Id, region.Id, StringComparison.Ordinal))
                    throw new InvalidDataException($"regions[{i}].part.id должен совпадать с region.id.");

                ids[i] = region.Id;
                parts[i] = region.Part.ToDefinition(i);
                if (parts[i].ParentIndex < -1 || parts[i].ParentIndex >= i)
                    throw new InvalidDataException($"Родитель '{region.Id}' должен стоять выше дочерней части.");
                heights[i] = region.HitHalfHeight;
                if (!float.IsFinite(heights[i]) || heights[i] <= 0f)
                    throw new InvalidDataException($"Некорректная hitHalfHeight у региона '{region.Id}'.");
                if (region.Tissues == null || region.Tissues.Count == 0)
                    throw new InvalidDataException($"У региона '{region.Id}' должна быть хотя бы одна ткань.");

                tissues[i] = new HitRegionPartBinding[region.Tissues.Count];
                for (int j = 0; j < tissues[i].Length; j++)
                {
                    TissueDocument tissue = region.Tissues[j]
                        ?? throw new InvalidDataException($"Ткань #{j + 1} у '{region.Id}' равна null.");
                    if (string.IsNullOrWhiteSpace(tissue.Id) || !knownTissueIds.Add(tissue.Id))
                        throw new InvalidDataException($"Пустой или повторяющийся ID ткани: '{tissue.Id}'.");
                    tissues[i][j] = tissue.ToBinding();
                }

                markers[i] = (region.Markers ?? new List<MarkerDocument>())
                    .Select(marker => marker.ToMarker())
                    .ToArray();
                var knownMarkers = new HashSet<string>(StringComparer.Ordinal);
                foreach (UnitAnatomyMarker marker in markers[i])
                    if (!knownMarkers.Add(marker.Id))
                        throw new InvalidDataException($"Повторяющийся marker ID '{marker.Id}' в регионе '{region.Id}'.");
            }

            var organs = new UnitAnatomyVolume[document.Organs.Count];
            var knownOrganIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < organs.Length; i++)
            {
                organs[i] = document.Organs[i].ToVolume();
                if (!knownOrganIds.Add(organs[i].Id))
                    throw new InvalidDataException($"Повторяющийся ID органа '{organs[i].Id}'.");
                int parentIndex = Array.IndexOf(ids, organs[i].ParentRegionId);
                if (parentIndex < 0)
                    throw new InvalidDataException($"Орган '{organs[i].Id}' ссылается на неизвестный регион '{organs[i].ParentRegionId}'.");
                if (!tissues[parentIndex].Any(tissue => tissue.Id == organs[i].EntryBindingId))
                    throw new InvalidDataException($"У органа '{organs[i].Id}' входная ткань должна принадлежать его региону.");
                if (!knownTissueIds.Contains(organs[i].TargetBindingId))
                    throw new InvalidDataException($"У органа '{organs[i].Id}' неизвестная ткань-цель '{organs[i].TargetBindingId}'.");
            }

            var equipment = new UnitStartingEquipment[document.StartingEquipment.Count];
            for (int i = 0; i < equipment.Length; i++)
                equipment[i] = document.StartingEquipment[i].ToEquipment();

            asset = BuildAsset(
                id, document.DisplayName.Trim(), document.Description ?? string.Empty,
                fullPath, document.RuntimeTypeId, definition, ids, parts, heights,
                tissues, markers, organs, equipment);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            asset = null;
            return false;
        }
    }

    public static IReadOnlyList<string> EnumerateProjectFiles()
    {
        try
        {
            if (!Directory.Exists(ProjectDirectory))
                return Array.Empty<string>();
            return Directory.GetFiles(ProjectDirectory, "*.unit.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<UnitArchetypeAsset> LoadProjectFiles(Action<string>? reportError = null)
    {
        var assets = new List<UnitArchetypeAsset>();
        var usedTypes = new HashSet<ushort>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateProjectFiles())
        {
            if (!TryLoadFile(path, out UnitArchetypeAsset? asset, out string error) || asset == null)
            {
                reportError?.Invoke($"[Unit asset] {Path.GetFileName(path)}: {error}");
                continue;
            }

            if (!usedTypes.Add(asset.RuntimeTypeId))
            {
                reportError?.Invoke($"[Unit asset] Дублируется runtimeTypeId={asset.RuntimeTypeId}: {path}");
                continue;
            }
            if (!usedIds.Add(asset.Id))
            {
                reportError?.Invoke($"[Unit asset] Дублируется id='{asset.Id}': {path}");
                continue;
            }

            assets.Add(asset);
        }

        return assets;
    }

    public static bool IsProjectFile(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.EndsWith(".unit.json", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(ProjectDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static UnitArchetypeAsset BuildAsset(
        string id,
        string displayName,
        string description,
        string filePath,
        ushort runtimeTypeId,
        UnitDefinition definition,
        string[] regionIds,
        BodyPartDefinition[] bodyParts,
        float[] hitHalfHeights,
        HitRegionPartBinding[][] tissues,
        UnitAnatomyMarker[][] markers,
        UnitAnatomyVolume[] organs,
        UnitStartingEquipment[] equipment)
    {
        ValidateRuntimeTypeId(runtimeTypeId);
        if (definition.Type != (UnitType)runtimeTypeId)
            throw new InvalidDataException("definition.type must equal runtimeTypeId.");
        if (!Enum.IsDefined(definition.BodyType))
            throw new InvalidDataException("Неизвестный bodyType.");
        if (!Positive(definition.MoveSpeed) || !Positive(definition.Width) ||
            !Positive(definition.Length) || !Positive(definition.Height) ||
            !Positive(definition.ViewRange) || !float.IsFinite(definition.FieldOfView) ||
            definition.FieldOfView < 1f || definition.FieldOfView > 360f)
            throw new InvalidDataException("Одна или несколько характеристик юнита выходят за допустимые пределы.");
        if (regionIds.Length == 0 || regionIds.Length > 256 ||
            regionIds.Length != bodyParts.Length || regionIds.Length != hitHalfHeights.Length ||
            regionIds.Length != tissues.Length || regionIds.Length != markers.Length)
            throw new InvalidDataException("regions, parts, hitHalfHeights, tissues и markers должны иметь одинаковый размер 1–256.");

        var knownRegions = new HashSet<string>(StringComparer.Ordinal);
        var knownTissues = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < regionIds.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(regionIds[i]) || !knownRegions.Add(regionIds[i]))
                throw new InvalidDataException($"Пустой или повторяющийся ID региона '{regionIds[i]}'.");
            if (bodyParts[i].ParentIndex < -1 || bodyParts[i].ParentIndex >= i)
                throw new InvalidDataException($"Родитель региона '{regionIds[i]}' должен находиться выше него в списке.");
            if (!float.IsFinite(hitHalfHeights[i]) || hitHalfHeights[i] <= 0f)
                throw new InvalidDataException($"Некорректная hitHalfHeight у региона '{regionIds[i]}'.");
            if (tissues[i] == null || tissues[i].Length == 0 || markers[i] == null)
                throw new InvalidDataException($"У региона '{regionIds[i]}' должна быть хотя бы одна ткань и массив маркеров.");

            foreach (HitRegionPartBinding tissue in tissues[i])
                if (string.IsNullOrWhiteSpace(tissue.Id) || !knownTissues.Add(tissue.Id))
                    throw new InvalidDataException($"Пустой или повторяющийся ID ткани '{tissue.Id}'.");

            var markerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnitAnatomyMarker marker in markers[i])
                if (string.IsNullOrWhiteSpace(marker.Id) || !markerIds.Add(marker.Id))
                    throw new InvalidDataException($"Пустой или повторяющийся ID маркера в регионе '{regionIds[i]}'.");
        }

        var knownOrgans = new HashSet<string>(StringComparer.Ordinal);
        foreach (UnitAnatomyVolume organ in organs)
        {
            int parentIndex = Array.IndexOf(regionIds, organ.ParentRegionId);
            if (!knownOrgans.Add(organ.Id) || parentIndex < 0 ||
                !tissues[parentIndex].Any(tissue => tissue.Id == organ.EntryBindingId) ||
                !knownTissues.Contains(organ.TargetBindingId))
                throw new InvalidDataException($"Орган '{organ.Id}' содержит некорректную область или ссылку на ткань.");
        }

        foreach (UnitStartingEquipment item in equipment)
            if (item.Item == null || !Positive(item.Durability))
                throw new InvalidDataException("Некорректная стартовая экипировка.");

        // Building the asset constructs every immutable anatomy type and validates
        // the values using the exact runtime constructors the game will use.
        return new UnitArchetypeAsset(
            id, displayName, description, filePath, runtimeTypeId, definition,
            regionIds, bodyParts, hitHalfHeights, tissues, markers, organs, equipment);
    }

    private static UnitAssetDocument CreateDocument(UnitArchetypeAsset asset)
    {
        var document = new UnitAssetDocument
        {
            SchemaVersion = SchemaVersion,
            Id = asset.Id,
            DisplayName = asset.DisplayName,
            Description = asset.Description,
            RuntimeTypeId = asset.RuntimeTypeId,
            Stats = new UnitAssetStats
            {
                BodyType = asset.Definition.BodyType.ToString(),
                MoveSpeed = asset.Definition.MoveSpeed,
                Width = asset.Definition.Width,
                Length = asset.Definition.Length,
                Height = asset.Definition.Height,
                ViewRange = asset.Definition.ViewRange,
                FieldOfView = asset.Definition.FieldOfView
            }
        };

        string[] ids = asset.RegionIds;
        BodyPartDefinition[] parts = asset.BodyParts;
        float[] heights = asset.HitHalfHeights;
        HitRegionPartBinding[][] tissues = asset.Tissues;
        UnitAnatomyMarker[][] markers = asset.Markers;
        for (int i = 0; i < ids.Length; i++)
        {
            var region = new RegionDocument
            {
                Id = ids[i],
                HitHalfHeight = heights[i],
                Part = UnitBodyRigSettingsStore.BodyRigPartSettings.From(ids[i], parts[i], i)
            };
            region.Tissues = tissues[i].Select(TissueDocument.From).ToList();
            region.Markers = markers[i].Select(MarkerDocument.From).ToList();
            document.Regions.Add(region);
        }

        document.Organs = asset.Organs.Select(VolumeDocument.From).ToList();
        document.StartingEquipment = asset.StartingEquipment.Select(EquipmentDocument.From).ToList();
        return document;
    }

    private static void EnsureRuntimeTypeAvailable(string path, ushort runtimeTypeId)
    {
        foreach (string otherPath in EnumerateProjectFiles())
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(otherPath), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(otherPath));
                if (json.RootElement.TryGetProperty("runtimeTypeId", out JsonElement element) &&
                    element.TryGetUInt16(out ushort existing) && existing == runtimeTypeId)
                    throw new InvalidDataException($"runtimeTypeId={runtimeTypeId} уже занят файлом {Path.GetFileName(otherPath)}.");
            }
            catch (JsonException)
            {
                // Keep editing valid assets even if a different file in the folder is malformed.
            }
        }
    }

    private static void EnsureIdAvailable(string path, string id)
    {
        foreach (string otherPath in EnumerateProjectFiles())
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(otherPath), StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(otherPath));
                if (json.RootElement.TryGetProperty("id", out JsonElement element) &&
                    element.ValueKind == JsonValueKind.String &&
                    string.Equals(element.GetString(), id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"ID '{id}' уже занят файлом {Path.GetFileName(otherPath)}.");
            }
            catch (JsonException)
            {
                // Keep editing valid assets even if a different file in the folder is malformed.
            }
        }
    }

    private static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("ID юнита не может быть пустым.");
        id = id.Trim();
        if (id.Length > 64 || id.Length < 2 ||
            !char.IsAsciiLetterOrDigit(id[0]) ||
            id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new InvalidDataException("ID: 2–64 символа; разрешены латинские буквы, цифры, '-', '_' и '.'.");
        return id.ToLowerInvariant();
    }

    private static void ValidateRuntimeTypeId(ushort id)
    {
        if (id < FirstCustomRuntimeTypeId || id > LastCustomRuntimeTypeId)
            throw new InvalidDataException(
                $"runtimeTypeId должен находиться в диапазоне {FirstCustomRuntimeTypeId}–{LastCustomRuntimeTypeId}.");
    }

    private static bool Positive(float value) => float.IsFinite(value) && value > 0f;

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;

    private sealed class UnitAssetDocument
    {
        public UnitAssetDocument() { }

        public int SchemaVersion { get; set; } = UnitArchetypeFileStore.SchemaVersion;
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public ushort RuntimeTypeId { get; set; } = FirstCustomRuntimeTypeId;
        public UnitAssetStats? Stats { get; set; } = new();
        public List<RegionDocument> Regions { get; set; } = new();
        public List<VolumeDocument> Organs { get; set; } = new();
        public List<EquipmentDocument> StartingEquipment { get; set; } = new();
    }

    private sealed class UnitAssetStats
    {
        public UnitAssetStats() { }

        public string BodyType { get; set; } = nameof(UnitBodyType.Colonist);
        public float MoveSpeed { get; set; } = 2.8f;
        public float Width { get; set; } = 0.36f;
        public float Length { get; set; } = 0.5f;
        public float Height { get; set; } = 1f;
        public float ViewRange { get; set; } = 20f;
        public float FieldOfView { get; set; } = 110f;
    }

    private sealed class RegionDocument
    {
        public RegionDocument() { }

        public string Id { get; set; } = string.Empty;
        public float HitHalfHeight { get; set; } = 0.1f;
        public UnitBodyRigSettingsStore.BodyRigPartSettings? Part { get; set; }
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
            Id = tissue.Id, ParentId = tissue.ParentId, Part = tissue.Part.ToString(),
            ParentPart = tissue.ParentPart.ToString(), Kind = tissue.Kind.ToString(),
            Weight = tissue.Weight, MaxHitPoints = tissue.MaxHitPoints,
            ArmorZone = tissue.ArmorZone, ProjectileEnergyLoss = tissue.ProjectileEnergyLoss,
            ProjectilePenetrationLoss = tissue.ProjectilePenetrationLoss
        };

        public HitRegionPartBinding ToBinding() => new(
            ParseEnum(Part, UnitHealthPartId.WholeBody), Weight,
            ParseEnum(Kind, UnitHealthPartKind.BodyPart), MaxHitPoints,
            ParseEnum(ParentPart, UnitHealthPartId.None), Id, ParentId,
            ArmorZone, ProjectileEnergyLoss, ProjectilePenetrationLoss);
    }

    private sealed class MarkerDocument
    {
        public MarkerDocument() { }

        public string Id { get; set; } = "target";
        public string Roles { get; set; } = nameof(UnitAnatomyMarkerRole.Target);
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public static MarkerDocument From(UnitAnatomyMarker marker) => new()
        {
            Id = marker.Id, Roles = marker.Roles.ToString(),
            X = marker.LocalOffset.X, Y = marker.LocalOffset.Y, Z = marker.LocalOffset.Z
        };

        public UnitAnatomyMarker ToMarker() =>
            new(Id, ParseEnum(Roles, UnitAnatomyMarkerRole.None), new Vector3(X, Y, Z));
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
            Id = volume.Id, ParentRegionId = volume.ParentRegionId,
            EntryBindingId = volume.EntryBindingId, TargetBindingId = volume.TargetBindingId,
            Primitive = volume.Primitive.ToString(),
            CenterX = volume.LocalCenter.X, CenterY = volume.LocalCenter.Y, CenterZ = volume.LocalCenter.Z,
            HalfX = volume.HalfExtents.X, HalfY = volume.HalfExtents.Y, HalfZ = volume.HalfExtents.Z,
            Rotation = volume.LocalRotation, EnergyLoss = volume.EnergyLoss,
            PenetrationLoss = volume.PenetrationLoss, DamageMultiplier = volume.DamageMultiplier,
            BleedMultiplier = volume.BleedMultiplier
        };

        public UnitAnatomyVolume ToVolume() => new(
            Id, ParentRegionId, EntryBindingId, TargetBindingId,
            new Vector3(CenterX, CenterY, CenterZ), new Vector3(HalfX, HalfY, HalfZ),
            ParseEnum(Primitive, BodyPrimitive.Circle), Rotation,
            EnergyLoss, PenetrationLoss, DamageMultiplier, BleedMultiplier);
    }

    private sealed class EquipmentDocument
    {
        public EquipmentDocument() { }

        public string ItemId { get; set; } = "custom";
        public float Durability { get; set; } = 100f;
        public ArmorDocument? Armor { get; set; }

        public static EquipmentDocument From(UnitStartingEquipment equipment) => new()
        {
            ItemId = GetApparelId(equipment.Item),
            Durability = equipment.Durability,
            Armor = ArmorDocument.From(equipment.Item)
        };

        public UnitStartingEquipment ToEquipment()
        {
            ArmorConfig armor = Armor?.ToArmor() ?? GetApparel(ItemId);
            if (Armor != null && Armor.Matches(GetApparelOrNull(ItemId)))
                armor = GetApparel(ItemId);
            return new UnitStartingEquipment(armor, Durability);
        }
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
            Name = item.Name, Weight = item.Weight,
            WorldColorR = item.WorldColorR, WorldColorG = item.WorldColorG, WorldColorB = item.WorldColorB,
            ProtectedZone = item.ProtectedZone, DamageAbsorption = item.DamageAbsorption,
            ArmorDamageCoefficient = item.ArmorDamageCoefficient, BleedProtectionChance = item.BleedProtectionChance,
            EnergyLoss = item.EnergyLoss, PenetrationResistance = item.PenetrationResistance,
            SharpRating = item.SharpRating, BluntRating = item.BluntRating, HeatRating = item.HeatRating,
            SoftArmor = item.SoftArmor, HardArmorDamageFactor = item.HardArmorDamageFactor,
            ArmorRating = item.ArmorRating, Coverage = (ushort)item.Coverage,
            Layer = (byte)item.Layer, WorldRenderPadding = item.WorldRenderPadding,
            MovementSpeedMultiplier = item.MovementSpeedMultiplier
        };

        public bool Matches(ArmorConfig? item)
        {
            if (item == null) return false;
            return Name == item.Name && Weight == item.Weight &&
                WorldColorR == item.WorldColorR && WorldColorG == item.WorldColorG && WorldColorB == item.WorldColorB &&
                ProtectedZone == item.ProtectedZone && DamageAbsorption == item.DamageAbsorption &&
                ArmorDamageCoefficient == item.ArmorDamageCoefficient && BleedProtectionChance == item.BleedProtectionChance &&
                EnergyLoss == item.EnergyLoss && PenetrationResistance == item.PenetrationResistance &&
                SharpRating == item.SharpRating && BluntRating == item.BluntRating && HeatRating == item.HeatRating &&
                SoftArmor == item.SoftArmor && HardArmorDamageFactor == item.HardArmorDamageFactor &&
                ArmorRating == item.ArmorRating && Coverage == (ushort)item.Coverage &&
                Layer == (byte)item.Layer && WorldRenderPadding == item.WorldRenderPadding &&
                MovementSpeedMultiplier == item.MovementSpeedMultiplier;
        }

        public ArmorConfig ToArmor() => new()
        {
            Name = Name, Weight = Weight,
            WorldColorR = WorldColorR, WorldColorG = WorldColorG, WorldColorB = WorldColorB,
            ProtectedZone = ProtectedZone, DamageAbsorption = DamageAbsorption,
            ArmorDamageCoefficient = ArmorDamageCoefficient, BleedProtectionChance = BleedProtectionChance,
            EnergyLoss = EnergyLoss, PenetrationResistance = PenetrationResistance,
            SharpRating = SharpRating, BluntRating = BluntRating, HeatRating = HeatRating,
            SoftArmor = SoftArmor, HardArmorDamageFactor = HardArmorDamageFactor,
            ArmorRating = ArmorRating, Coverage = (ArmorCoverage)Coverage,
            Layer = Enum.IsDefined(typeof(ArmorLayer), (ArmorLayer)Layer) ? (ArmorLayer)Layer : ArmorLayer.Clothing,
            WorldRenderPadding = WorldRenderPadding, MovementSpeedMultiplier = MovementSpeedMultiplier
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

    private static ArmorConfig? GetApparelOrNull(string id) => id switch
    {
        nameof(ApparelCatalog.WorkShirt) => ApparelCatalog.WorkShirt,
        nameof(ApparelCatalog.WorkTrousers) => ApparelCatalog.WorkTrousers,
        nameof(ApparelCatalog.CombatVest) => ApparelCatalog.CombatVest,
        nameof(ApparelCatalog.CombatHelmet) => ApparelCatalog.CombatHelmet,
        nameof(ApparelCatalog.PoweredExoskeleton) => ApparelCatalog.PoweredExoskeleton,
        _ => null
    };

    private static ArmorConfig GetApparel(string id) =>
        GetApparelOrNull(id) ?? throw new InvalidDataException($"Неизвестный ID экипировки '{id}'.");

    private static T[][] CloneRows<T>(T[][] source)
    {
        var result = new T[source.Length][];
        for (int i = 0; i < source.Length; i++)
            result[i] = (T[])source[i].Clone();
        return result;
    }
}
