using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Core.Unit;

/// <summary>
/// Persistent colonist rig definition. The JSON file contains stable region IDs,
/// local transforms, parent dependencies and rendering metadata.
/// </summary>
public static class UnitBodyRigSettingsStore
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, BodyPartDefinition> Defaults = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BodyRigPartSettings> Saved = new(StringComparer.Ordinal);
    private static readonly List<BodyRigPartSettings> SavedInFileOrder = new();
    private static bool _loaded;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string FilePath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = AppContext.BaseDirectory;

            return Path.Combine(root, "RimClone", "colonist-body-rig.json");
        }
    }

    public static BodyPartDefinition ApplySavedShape(string regionId, BodyPartDefinition defaults)
    {
        lock (Sync)
        {
            int index = Defaults.Count;
            if (!Defaults.ContainsKey(regionId))
                Defaults.Add(regionId, defaults);
            else
                index = Array.IndexOf(Defaults.Keys.ToArray(), regionId);

            EnsureLoaded();
            return Saved.TryGetValue(regionId, out BodyRigPartSettings? settings)
                ? settings.Apply(defaults, index)
                : defaults;
        }
    }

    public static BodyPartDefinition[] GetDefaultBodyParts(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> currentParts)
    {
        if (regionIds.Length != currentParts.Length)
            throw new ArgumentException("Количество регионов и форм не совпадает.");

        var result = new BodyPartDefinition[currentParts.Length];
        lock (Sync)
        {
            EnsureLoaded();
            for (int i = 0; i < result.Length; i++)
                result[i] = Defaults.TryGetValue(regionIds[i], out BodyPartDefinition value)
                    ? value
                    : currentParts[i];
        }

        return result;
    }

    /// <summary>Appends saved custom regions after the canonical catalog regions.</summary>
    public static void MergeSavedRig(
        ReadOnlySpan<string> baseIds,
        ReadOnlySpan<BodyPartDefinition> baseParts,
        out string[] regionIds,
        out BodyPartDefinition[] parts)
    {
        if (baseIds.Length != baseParts.Length)
            throw new ArgumentException("Количество ID и форм должно совпадать.");

        lock (Sync)
        {
            EnsureLoaded();
            var ids = new List<string>(baseIds.Length + SavedInFileOrder.Count);
            var definitions = new List<BodyPartDefinition>(baseParts.Length + SavedInFileOrder.Count);
            var known = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < baseIds.Length; i++)
            {
                ids.Add(baseIds[i]);
                definitions.Add(baseParts[i]);
                known.Add(baseIds[i]);
            }

            foreach (BodyRigPartSettings setting in SavedInFileOrder)
            {
                if (string.IsNullOrWhiteSpace(setting.Id) || !known.Add(setting.Id))
                    continue;

                int index = ids.Count;
                BodyPartDefinition part = setting.ToDefinition(index);
                if (part.ParentIndex < -1 || part.ParentIndex >= index)
                    part = setting.ToDefinition(index, fallbackParent: 0);

                ids.Add(setting.Id);
                definitions.Add(part);
            }

            regionIds = ids.ToArray();
            parts = definitions.ToArray();
        }
    }

    public static bool Save(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string fullPath,
        out string error)
    {
        fullPath = FilePath;
        if (!SaveFile(fullPath, regionIds, parts, out error))
            return false;

        lock (Sync)
        {
            Saved.Clear();
            SavedInFileOrder.Clear();
            for (int i = 0; i < regionIds.Length; i++)
            {
                BodyRigPartSettings item = BodyRigPartSettings.From(regionIds[i], parts[i], i);
                Saved[item.Id] = item;
                SavedInFileOrder.Add(item);
            }

            _loaded = true;
        }

        return true;
    }

    public static bool ExportFile(
        string path,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string error) =>
        SaveFile(path, regionIds, parts, out error);

    public static bool LoadFile(
        string path,
        out string[] regionIds,
        out BodyPartDefinition[] parts,
        out string error)
    {
        regionIds = Array.Empty<string>();
        parts = Array.Empty<BodyPartDefinition>();
        error = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Файл рига не найден.", path);

            string json = File.ReadAllText(path);
            RigSettingsDocument? document = JsonSerializer.Deserialize<RigSettingsDocument>(json, JsonOptions);
            if (document?.Parts == null || document.Version is < 1 or > 2)
                throw new InvalidDataException("Неподдерживаемый формат файла рига.");

            if (document.Parts.Count == 0 || document.Parts.Count > 256)
                throw new InvalidDataException("Риг должен содержать от 1 до 256 регионов.");

            var ids = new string[document.Parts.Count];
            var definitions = new BodyPartDefinition[document.Parts.Count];
            var known = new HashSet<string>(StringComparer.Ordinal);

            lock (Sync)
                EnsureLoaded();

            for (int i = 0; i < document.Parts.Count; i++)
            {
                BodyRigPartSettings setting = document.Parts[i];
                if (string.IsNullOrWhiteSpace(setting.Id) || !known.Add(setting.Id))
                    throw new InvalidDataException($"Пустой или повторяющийся ID региона в строке {i + 1}.");

                ids[i] = setting.Id;
                BodyPartDefinition fallback;
                lock (Sync)
                    fallback = Defaults.TryGetValue(setting.Id, out BodyPartDefinition knownDefault)
                        ? knownDefault
                        : CreateFallback(i);

                definitions[i] = setting.Apply(fallback, i);
                if (definitions[i].ParentIndex < -1 || definitions[i].ParentIndex >= i)
                    throw new InvalidDataException(
                        $"Родитель региона '{setting.Id}' должен находиться выше него в списке.");
            }

            regionIds = ids;
            parts = definitions;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool SaveFile(
        string path,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string error)
    {
        error = string.Empty;
        if (regionIds.Length != parts.Length || regionIds.Length == 0 || regionIds.Length > 256)
        {
            error = "Риг должен содержать от 1 до 256 регионов.";
            return false;
        }

        var document = new RigSettingsDocument { Version = 2 };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(regionIds[i]) || !seen.Add(regionIds[i]))
            {
                error = $"Пустой или повторяющийся ID региона: {regionIds[i]}";
                return false;
            }

            if (parts[i].ParentIndex < -1 || parts[i].ParentIndex >= i)
            {
                error = $"Родитель региона '{regionIds[i]}' должен предшествовать ему в списке.";
                return false;
            }

            document.Parts.Add(BodyRigPartSettings.From(regionIds[i], parts[i], i));
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new IOException("Не удалось определить папку сохранения.");

            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        Saved.Clear();
        SavedInFileOrder.Clear();

        try
        {
            if (!File.Exists(FilePath))
                return;

            string json = File.ReadAllText(FilePath);
            RigSettingsDocument? document = JsonSerializer.Deserialize<RigSettingsDocument>(json, JsonOptions);
            if (document?.Parts == null || document.Version is < 1 or > 2)
                return;

            foreach (BodyRigPartSettings part in document.Parts)
            {
                if (string.IsNullOrWhiteSpace(part.Id) || Saved.ContainsKey(part.Id))
                    continue;

                Saved[part.Id] = part;
                SavedInFileOrder.Add(part);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Body rig] Не удалось прочитать {FilePath}: {exception.Message}");
        }
    }

    private static BodyPartDefinition CreateFallback(int index) =>
        new(
            new Vector3(0f, 0f, index == 0 ? 0f : -0.06f),
            new Vector3(0.035f, 0.022f, 0.06f),
            BodyPrimitive.Circle,
            parentIndex: index == 0 ? -1 : 0,
            role: BodyPartRole.Generic,
            pivot: BodyPartPivot.Center);

    public sealed class RigSettingsDocument
    {
        public int Version { get; set; } = 2;
        public List<BodyRigPartSettings> Parts { get; set; } = new();
        public RigSettingsDocument() { }
    }

    public sealed class BodyRigPartSettings
    {
        public string Id { get; set; } = string.Empty;
        public int ParentIndex { get; set; } = -2;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float ScaleX { get; set; } = 0.035f;
        public float ScaleY { get; set; } = 0.022f;
        public float ScaleZ { get; set; } = 0.06f;
        public float Rotation { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float FollowDistance { get; set; } = float.NaN;
        public string Primitive { get; set; } = nameof(BodyPrimitive.Circle);
        public string Motion { get; set; } = string.Empty;
        public string Anchor { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Pivot { get; set; } = string.Empty;

        public BodyRigPartSettings() { }

        public static BodyRigPartSettings From(string id, BodyPartDefinition part, int order) => new()
        {
            Id = id,
            ParentIndex = part.ParentIndex,
            X = part.LocalPosition.X,
            Y = part.LocalPosition.Y,
            Z = part.LocalPosition.Z,
            ScaleX = part.Scale.X,
            ScaleY = part.Scale.Y,
            ScaleZ = part.Scale.Z,
            Rotation = part.LocalRotation,
            RotationX = part.LocalRotationX,
            RotationY = part.LocalRotationY,
            FollowDistance = part.FollowDistance,
            Primitive = part.Primitive.ToString(),
            Motion = part.Motion.ToString(),
            Anchor = part.Anchor.ToString(),
            Role = part.Role.ToString(),
            Pivot = part.Pivot.ToString()
        };

        public BodyPartDefinition Apply(BodyPartDefinition baseline, int index)
        {
            if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z) ||
                !float.IsFinite(ScaleX) || !float.IsFinite(ScaleY) || !float.IsFinite(ScaleZ) ||
                !float.IsFinite(Rotation) || !float.IsFinite(RotationX) || !float.IsFinite(RotationY) ||
                ScaleX <= 0f || ScaleY <= 0f || ScaleZ <= 0f)
                return baseline;

            int parent = ParentIndex >= -1 && ParentIndex < index
                ? ParentIndex
                : baseline.ParentIndex;
            BodyPrimitive primitive = Parse(Primitive, baseline.Primitive);
            BodyPartMotion motion = Parse(Motion, baseline.Motion);
            BodyPartAnchor anchor = Parse(Anchor, baseline.Anchor);
            if (parent >= 0 && anchor == BodyPartAnchor.UnitHead)
                anchor = BodyPartAnchor.Parent;
            BodyPartRole role = Parse(Role, baseline.Role);
            BodyPartPivot pivot = Parse(Pivot, baseline.Pivot);

            return new BodyPartDefinition(
                new Vector3(Math.Clamp(X, -3f, 3f), Math.Clamp(Y, -3f, 3f), Math.Clamp(Z, -3f, 3f)),
                new Vector3(Math.Clamp(ScaleX, 0.005f, 2f), Math.Clamp(ScaleY, 0.005f, 2f), Math.Clamp(ScaleZ, 0.005f, 2f)),
                primitive,
                parentIndex: parent,
                localRotation: Math.Clamp(Rotation, -20f * MathF.PI, 20f * MathF.PI),
                motion: motion,
                followDistance: float.IsFinite(FollowDistance)
                    ? Math.Clamp(FollowDistance, 0f, 3f)
                    : baseline.FollowDistance,
                anchor: anchor,
                role: role,
                pivot: pivot,
                localRotationX: Math.Clamp(RotationX, -20f * MathF.PI, 20f * MathF.PI),
                localRotationY: Math.Clamp(RotationY, -20f * MathF.PI, 20f * MathF.PI));
        }

        public BodyPartDefinition ToDefinition(int index, int fallbackParent = 0)
        {
            int parent = ParentIndex >= -1 && ParentIndex < index
                ? ParentIndex
                : (index == 0 ? -1 : Math.Clamp(fallbackParent, -1, index - 1));
            BodyPartDefinition baseline = CreateFallback(index);
            return Apply(baseline, index) is BodyPartDefinition part
                ? new BodyPartDefinition(
                    part.LocalPosition,
                    part.Scale,
                    part.Primitive,
                    parentIndex: parent,
                    localRotation: part.LocalRotation,
                    motion: part.Motion,
                    followDistance: part.FollowDistance,
                    anchor: part.Anchor,
                    role: part.Role,
                    pivot: part.Pivot,
                    localRotationX: part.LocalRotationX,
                    localRotationY: part.LocalRotationY)
                : baseline;
        }

        private static T Parse<T>(string value, T fallback) where T : struct, Enum =>
            Enum.TryParse(value, ignoreCase: true, out T parsed) ? parsed : fallback;
    }
}
