using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Core.Unit;

/// <summary>
/// Loads and stores custom unit archetypes beside the game executable.
/// Custom UnitType IDs are persisted numeric values, so they remain stable across launches.
/// </summary>
public static class CustomUnitTypeCatalog
{
    private static readonly Dictionary<UnitType, UnitArchetypeProfile> Profiles = new();
    private static readonly Dictionary<UnitType, string> Names = new();
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static bool _loaded;

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Units");

    public static bool SaveType(
        string requestedPath,
        string id,
        string displayName,
        float moveSpeed,
        float width,
        float length,
        float height,
        float viewRange,
        float fieldOfView,
        UnitBodyType bodyType,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        ReadOnlySpan<UnitAnatomyVolume> organs,
        out UnitType type,
        out string savedPath,
        out string error)
    {
        type = default;
        savedPath = string.Empty;
        error = string.Empty;

        string safeId = SanitizeId(id);
        if (safeId.Length == 0)
        {
            error = "ID типа должен содержать латинские буквы, цифры, точку, дефис или подчёркивание.";
            return false;
        }

        if (!float.IsFinite(moveSpeed) || moveSpeed <= 0f ||
            !float.IsFinite(width) || width <= 0f ||
            !float.IsFinite(length) || length <= 0f ||
            !float.IsFinite(height) || height <= 0f ||
            !float.IsFinite(viewRange) || viewRange <= 0f ||
            !float.IsFinite(fieldOfView) || fieldOfView <= 0f || fieldOfView > 360f)
        {
            error = "Характеристики юнита содержат некорректные значения.";
            return false;
        }

        UnitType assignedType;
        string targetPath;
        try
        {
            lock (Sync)
            {
                EnsureLoaded();
                targetPath = Path.Combine(DirectoryPath, safeId + ".json");
                if (File.Exists(targetPath))
                {
                    TypeDocument? current = JsonSerializer.Deserialize<TypeDocument>(
                        File.ReadAllText(targetPath), JsonOptions);
                    assignedType = current is { TypeValue: >= 1000 }
                        ? (UnitType)current.TypeValue
                        : FindFreeTypeValue();
                }
                else
                {
                    assignedType = FindFreeTypeValue();
                }
            }

            UnitAnatomyProfile baseAnatomy = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
            UnitAnatomyProfile anatomy = baseAnatomy.WithRig(regionIds, bodyParts, organs);
            var definition = new UnitDefinition(
                assignedType,
                bodyType,
                moveSpeed,
                width,
                length,
                height,
                viewRange,
                fieldOfView);
            var profile = new UnitArchetypeProfile(definition, anatomy);

            var document = new TypeDocument
            {
                Version = 1,
                Id = safeId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? safeId : displayName.Trim(),
                TypeValue = (ushort)assignedType,
                MoveSpeed = moveSpeed,
                Width = width,
                Length = length,
                Height = height,
                ViewRange = viewRange,
                FieldOfView = fieldOfView,
                BodyType = bodyType.ToString(),
                Parts = new List<UnitBodyRigSettingsStore.BodyRigPartSettings>(bodyParts.Length),
                Organs = new List<UnitBodyRigSettingsStore.OrganSettings>(organs.Length)
            };

            for (int i = 0; i < regionIds.Length; i++)
                document.Parts.Add(UnitBodyRigSettingsStore.BodyRigPartSettings.From(regionIds[i], bodyParts[i], i));
            for (int i = 0; i < organs.Length; i++)
                document.Organs.Add(UnitBodyRigSettingsStore.OrganSettings.From(organs[i]));

            Directory.CreateDirectory(DirectoryPath);
            string temporaryPath = targetPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, targetPath, overwrite: true);

            lock (Sync)
            {
                Profiles[assignedType] = profile;
                Names[assignedType] = document.DisplayName;
                _loaded = true;
            }
            UnitAnatomyCatalog.RegisterCustomType(profile);
            type = assignedType;
            savedPath = targetPath;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryReadTypeFile(string path, out TypeDocument document, out string error)
    {
        document = new TypeDocument();
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Файл типа юнита не найден.", path);

            TypeDocument? loaded = JsonSerializer.Deserialize<TypeDocument>(
                File.ReadAllText(path), JsonOptions);
            if (loaded == null || loaded.Version != 1 || loaded.TypeValue < 1000 ||
                loaded.Parts == null || loaded.Parts.Count == 0 ||
                loaded.Parts.Count > 128)
                throw new InvalidDataException("Это не файл пользовательского типа юнита.");

            document = loaded;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static bool TryGetProfile(UnitType type, out UnitArchetypeProfile profile)
    {
        lock (Sync)
        {
            EnsureLoaded();
            return Profiles.TryGetValue(type, out profile!);
        }
    }

    public static string GetDisplayName(UnitType type)
    {
        lock (Sync)
        {
            EnsureLoaded();
            return Names.TryGetValue(type, out string? name) ? name : type.ToString();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        Profiles.Clear();
        Names.Clear();
        if (!Directory.Exists(DirectoryPath))
            return;

        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                TypeDocument? document = JsonSerializer.Deserialize<TypeDocument>(
                    File.ReadAllText(path), JsonOptions);
                if (document == null || document.Version != 1 ||
                    document.TypeValue < 1000 ||
                    document.Parts == null || document.Parts.Count == 0 ||
                    document.Parts.Count > 128)
                    continue;

                UnitType type = (UnitType)document.TypeValue;
                var ids = new string[document.Parts.Count];
                var bodyParts = new BodyPartDefinition[document.Parts.Count];
                bool invalid = false;
                for (int i = 0; i < document.Parts.Count; i++)
                {
                    UnitBodyRigSettingsStore.BodyRigPartSettings part = document.Parts[i];
                    ids[i] = part.Id;
                    bodyParts[i] = part.ToDefinition(i);
                    if (string.IsNullOrWhiteSpace(ids[i]) ||
                        bodyParts[i].ParentIndex < -1 || bodyParts[i].ParentIndex >= i)
                    {
                        invalid = true;
                        break;
                    }
                }
                if (invalid)
                    continue;

                UnitAnatomyVolume[] organs = document.Organs?
                    .Select(item => item.ToVolume())
                    .ToArray() ?? Array.Empty<UnitAnatomyVolume>();

                UnitAnatomyProfile anatomy =
                    UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy.WithRig(ids, bodyParts, organs);
                var definition = new UnitDefinition(
                    type,
                    ParseBodyType(document.BodyType),
                    document.MoveSpeed,
                    document.Width,
                    document.Length,
                    document.Height,
                    document.ViewRange,
                    document.FieldOfView);
                Profiles[type] = new UnitArchetypeProfile(definition, anatomy);
                Names[type] = string.IsNullOrWhiteSpace(document.DisplayName)
                    ? document.Id
                    : document.DisplayName;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Custom unit] Skipped {path}: {exception.Message}");
            }
        }
    }

    private static UnitBodyType ParseBodyType(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out UnitBodyType bodyType) &&
        Enum.IsDefined(bodyType)
            ? bodyType
            : UnitBodyType.Colonist;

    private static UnitType FindFreeTypeValue()
    {
        var used = new HashSet<ushort>(Profiles.Keys.Select(type => (ushort)type));
        if (Directory.Exists(DirectoryPath))
        {
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                try
                {
                    TypeDocument? document = JsonSerializer.Deserialize<TypeDocument>(
                        File.ReadAllText(path), JsonOptions);
                    if (document != null && document.TypeValue >= 1000)
                        used.Add(document.TypeValue);
                }
                catch { }
            }
        }

        for (int candidate = 1000; candidate < ushort.MaxValue; candidate++)
        {
            if (!used.Contains((ushort)candidate))
                return (UnitType)candidate;
        }

        throw new InvalidOperationException("Исчерпаны числовые ID пользовательских типов юнитов.");
    }

    private static string SanitizeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = new char[Math.Min(80, value.Length)];
        int write = 0;
        foreach (char ch in value.Trim().ToLowerInvariant())
        {
            if (write == result.Length)
                break;
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')
                result[write++] = ch;
        }

        return new string(result, 0, write).Trim('.', '-', '_');
    }

    public sealed class TypeDocument
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public ushort TypeValue { get; set; }
        public float MoveSpeed { get; set; } = 2.8f;
        public float Width { get; set; } = 0.35f;
        public float Length { get; set; } = 0.6f;
        public float Height { get; set; } = 1.75f;
        public float ViewRange { get; set; } = 20f;
        public float FieldOfView { get; set; } = 110f;
        public string BodyType { get; set; } = nameof(UnitBodyType.Colonist);
        public List<UnitBodyRigSettingsStore.BodyRigPartSettings> Parts { get; set; } = new();
        public List<UnitBodyRigSettingsStore.OrganSettings>? Organs { get; set; }
    }
}
