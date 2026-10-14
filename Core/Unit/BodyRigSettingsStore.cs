using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Core.Unit;

/// <summary>
/// Persistent transform overrides for the editable colonist rig.
/// The canonical body definition remains in UnitAnatomyCatalog; this file stores
/// only user-edited local transforms and primitive type, keyed by stable anatomy IDs.
/// </summary>
public static class UnitBodyRigSettingsStore
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, BodyPartDefinition> Defaults = new(StringComparer.Ordinal);
    private static Dictionary<string, BodyRigPartSettings> Saved = new(StringComparer.Ordinal);
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

    /// <summary>Called while the built-in catalog is initialized, before any edited shape is applied.</summary>
    public static BodyPartDefinition ApplySavedShape(string regionId, BodyPartDefinition defaults)
    {
        lock (Sync)
        {
            if (!Defaults.ContainsKey(regionId))
                Defaults.Add(regionId, defaults);

            EnsureLoaded();
            return Saved.TryGetValue(regionId, out BodyRigPartSettings? settings)
                ? settings.Apply(defaults)
                : defaults;
        }
    }

    public static BodyPartDefinition[] GetDefaultBodyParts(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> currentParts)
    {
        if (regionIds.Length != currentParts.Length)
            throw new ArgumentException("Region IDs and body-part definitions must have the same length.");

        var result = new BodyPartDefinition[currentParts.Length];
        lock (Sync)
        {
            EnsureLoaded();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Defaults.TryGetValue(regionIds[i], out BodyPartDefinition value)
                    ? value
                    : currentParts[i];
            }
        }

        return result;
    }

    public static bool Save(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string fullPath,
        out string error)
    {
        fullPath = FilePath;
        error = string.Empty;

        if (regionIds.Length != parts.Length)
        {
            error = "Количество регионов и форм не совпадает.";
            return false;
        }

        var document = new RigSettingsDocument();
        var nextSaved = new Dictionary<string, BodyRigPartSettings>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Length; i++)
        {
            BodyRigPartSettings settings = BodyRigPartSettings.From(regionIds[i], parts[i]);
            document.Parts.Add(settings);
            nextSaved[regionIds[i]] = settings;
        }

        try
        {
            lock (Sync)
            {
                EnsureLoaded();
                string? directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new IOException("Не удалось определить папку сохранения.");

                Directory.CreateDirectory(directory);
                string temporaryPath = fullPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporaryPath, fullPath, overwrite: true);
                Saved = nextSaved;
            }

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
        Saved = new Dictionary<string, BodyRigPartSettings>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath))
                return;

            string json = File.ReadAllText(FilePath);
            RigSettingsDocument? document = JsonSerializer.Deserialize<RigSettingsDocument>(json, JsonOptions);
            if (document?.Parts == null || document.Version != 1)
                return;

            foreach (BodyRigPartSettings part in document.Parts)
            {
                if (!string.IsNullOrWhiteSpace(part.Id))
                    Saved[part.Id] = part;
            }
        }
        catch (Exception exception)
        {
            // Invalid config must never prevent the game from starting.
            Console.Error.WriteLine($"[Body rig] Не удалось прочитать {FilePath}: {exception.Message}");
        }
    }

    public sealed class RigSettingsDocument
    {
        public int Version { get; set; } = 1;
        public List<BodyRigPartSettings> Parts { get; set; } = new();

        public RigSettingsDocument() { }
    }

    public sealed class BodyRigPartSettings
    {
        public string Id { get; set; } = string.Empty;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float ScaleX { get; set; }
        public float ScaleY { get; set; }
        public float ScaleZ { get; set; }
        public float Rotation { get; set; }
        public string Primitive { get; set; } = nameof(BodyPrimitive.Circle);

        public BodyRigPartSettings() { }

        public static BodyRigPartSettings From(string id, BodyPartDefinition part) => new()
        {
            Id = id,
            X = part.LocalPosition.X,
            Y = part.LocalPosition.Y,
            Z = part.LocalPosition.Z,
            ScaleX = part.Scale.X,
            ScaleY = part.Scale.Y,
            ScaleZ = part.Scale.Z,
            Rotation = part.LocalRotation,
            Primitive = part.Primitive.ToString()
        };

        public BodyPartDefinition Apply(BodyPartDefinition baseline)
        {
            if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z) ||
                !float.IsFinite(ScaleX) || !float.IsFinite(ScaleY) || !float.IsFinite(ScaleZ) ||
                !float.IsFinite(Rotation) ||
                ScaleX <= 0f || ScaleY <= 0f || ScaleZ <= 0f)
            {
                return baseline;
            }

            BodyPrimitive primitive = Enum.TryParse(Primitive, ignoreCase: true, out BodyPrimitive parsed)
                ? parsed
                : baseline.Primitive;

            return new BodyPartDefinition(
                new Vector3(
                    Math.Clamp(X, -3f, 3f),
                    Math.Clamp(Y, -3f, 3f),
                    Math.Clamp(Z, -3f, 3f)),
                new Vector3(
                    Math.Clamp(ScaleX, 0.005f, 2f),
                    Math.Clamp(ScaleY, 0.005f, 2f),
                    Math.Clamp(ScaleZ, 0.005f, 2f)),
                primitive,
                parentIndex: baseline.ParentIndex,
                localRotation: Math.Clamp(Rotation, -20f * MathF.PI, 20f * MathF.PI),
                motion: baseline.Motion,
                followDistance: baseline.FollowDistance,
                anchor: baseline.Anchor,
                role: baseline.Role,
                pivot: baseline.Pivot);
        }
    }
}
