using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace Core.Unit;

public readonly struct UnitBodyPoseTransform
{
    public Vector3 LocalPosition { get; }
    public Vector3 LocalEulerRotation { get; }

    public UnitBodyPoseTransform(Vector3 localPosition, Vector3 localEulerRotation)
    {
        LocalPosition = localPosition;
        LocalEulerRotation = localEulerRotation;
    }
}

/// <summary>
/// Saved per-node joint transforms for each unit archetype and posture.
/// Shape, scale, topology and hit geometry stay in the base anatomy definition.
/// </summary>
public static class UnitBodyPoseSettingsStore
{
    private const int PoseCount = 4;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static bool _loaded;
    private static Dictionary<ushort, Dictionary<string, UnitBodyPoseTransform>[]> _poses = new();

    public static string FilePath
    {
        get
        {
            string root = FindProjectRoot() ?? AppContext.BaseDirectory;
            return Path.Combine(root, "Data", "Units", "body-poses.json");
        }
    }

    public static IReadOnlyDictionary<string, UnitBodyPoseTransform>? GetTransforms(
        UnitType unitType,
        UnitPosture posture)
    {
        EnsureLoaded();
        int index = (int)posture;
        if ((uint)index >= (uint)PoseCount)
            return null;

        lock (Sync)
        {
            if (!_poses.TryGetValue((ushort)unitType, out Dictionary<string, UnitBodyPoseTransform>[]? poses))
                return null;

            Dictionary<string, UnitBodyPoseTransform> pose = poses[index];
            return pose.Count == 0 ? null : pose;
        }
    }

    public static bool TryGetTransform(
        UnitType unitType,
        UnitPosture posture,
        string? partId,
        out UnitBodyPoseTransform transform)
    {
        IReadOnlyDictionary<string, UnitBodyPoseTransform>? pose = GetTransforms(unitType, posture);
        if (pose != null &&
            !string.IsNullOrWhiteSpace(partId) &&
            pose.TryGetValue(partId, out transform))
        {
            return true;
        }

        transform = default;
        return false;
    }

    public static bool Save(
        UnitType unitType,
        UnitPosture posture,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> parts,
        out string fullPath,
        out string error)
    {
        fullPath = FilePath;
        error = string.Empty;
        int poseIndex = (int)posture;
        if ((uint)poseIndex >= PoseCount)
        {
            error = "Неизвестный тип позы.";
            return false;
        }

        if (regionIds.Length == 0 || regionIds.Length != parts.Length || regionIds.Length > 256)
        {
            error = "Поза должна содержать от 1 до 256 частей тела.";
            return false;
        }

        var nextPose = new Dictionary<string, UnitBodyPoseTransform>(StringComparer.Ordinal);
        for (int i = 0; i < regionIds.Length; i++)
        {
            string id = regionIds[i];
            BodyPartDefinition part = parts[i];
            Vector3 position = part.LocalPosition;
            Vector3 euler = part.LocalEulerRotation;
            if (string.IsNullOrWhiteSpace(id) ||
                !IsFinite(position) ||
                !IsFinite(euler) ||
                !nextPose.TryAdd(id, new UnitBodyPoseTransform(position, euler)))
            {
                error = $"Пустой/повторяющийся ID или некорректный трансформ части №{i + 1}.";
                return false;
            }
        }

        EnsureLoaded();
        lock (Sync)
        {
            var updated = new Dictionary<ushort, Dictionary<string, UnitBodyPoseTransform>[]>(_poses);
            ushort typeKey = (ushort)unitType;
            Dictionary<string, UnitBodyPoseTransform>[] typePoses =
                updated.TryGetValue(typeKey, out Dictionary<string, UnitBodyPoseTransform>[]? existing)
                    ? (Dictionary<string, UnitBodyPoseTransform>[])existing.Clone()
                    : CreateEmptyPoses();
            typePoses[poseIndex] = nextPose;
            updated[typeKey] = typePoses;

            if (!WriteFile(updated, fullPath, out error))
                return false;

            _poses = updated;
            _loaded = true;
        }

        return true;
    }

    public static bool Reset(
        UnitType unitType,
        UnitPosture posture,
        out string fullPath,
        out string error)
    {
        fullPath = FilePath;
        error = string.Empty;
        int poseIndex = (int)posture;
        if ((uint)poseIndex >= PoseCount)
        {
            error = "Неизвестный тип позы.";
            return false;
        }

        EnsureLoaded();
        lock (Sync)
        {
            var updated = new Dictionary<ushort, Dictionary<string, UnitBodyPoseTransform>[]>(_poses);
            ushort typeKey = (ushort)unitType;
            Dictionary<string, UnitBodyPoseTransform>[] typePoses =
                updated.TryGetValue(typeKey, out Dictionary<string, UnitBodyPoseTransform>[]? existing)
                    ? (Dictionary<string, UnitBodyPoseTransform>[])existing.Clone()
                    : CreateEmptyPoses();
            typePoses[poseIndex] = new Dictionary<string, UnitBodyPoseTransform>(StringComparer.Ordinal);
            updated[typeKey] = typePoses;

            if (!WriteFile(updated, fullPath, out error))
                return false;

            _poses = updated;
            _loaded = true;
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

            var loaded = new Dictionary<ushort, Dictionary<string, UnitBodyPoseTransform>[]>();
            string path = FilePath;
            if (File.Exists(path))
            {
                try
                {
                    PoseFileDocument? document =
                        JsonSerializer.Deserialize<PoseFileDocument>(File.ReadAllText(path), JsonOptions);
                    if (document == null || document.Version is < 1 or > 2 || document.Poses == null)
                        throw new InvalidDataException("Неподдерживаемый формат файла поз.");

                    foreach (PoseDocument pose in document.Poses)
                    {
                        if (pose.UnitType < 0 ||
                            pose.UnitType > ushort.MaxValue ||
                            (uint)pose.Posture >= (uint)PoseCount ||
                            pose.Parts == null)
                            continue;

                        ushort typeKey = (ushort)pose.UnitType;
                        if (!loaded.TryGetValue(typeKey, out Dictionary<string, UnitBodyPoseTransform>[]? typePoses))
                        {
                            typePoses = CreateEmptyPoses();
                            loaded[typeKey] = typePoses;
                        }

                        Dictionary<string, UnitBodyPoseTransform> parts = typePoses[pose.Posture];
                        foreach (PosePartDocument part in pose.Parts)
                        {
                            if (string.IsNullOrWhiteSpace(part.Id))
                                continue;

                            Vector3 position = new(part.PositionX, part.PositionY, part.PositionZ);
                            Vector3 euler = new(part.RotationX, part.RotationY, part.RotationZ);
                            if (!IsFinite(position) || !IsFinite(euler))
                                continue;

                            parts[part.Id] = new UnitBodyPoseTransform(position, euler);
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Body poses] Cannot load '{path}': {exception.Message}");
                    loaded.Clear();
                }
            }

            _poses = loaded;
            _loaded = true;
        }
    }

    private static bool WriteFile(
        Dictionary<ushort, Dictionary<string, UnitBodyPoseTransform>[]> poses,
        string path,
        out string error)
    {
        error = string.Empty;
        try
        {
            var document = new PoseFileDocument { Version = 2 };
            foreach (KeyValuePair<ushort, Dictionary<string, UnitBodyPoseTransform>[]> typeEntry in poses)
            {
                for (int posture = 0; posture < typeEntry.Value.Length; posture++)
                {
                    Dictionary<string, UnitBodyPoseTransform> pose = typeEntry.Value[posture];
                    if (pose.Count == 0)
                        continue;

                    var record = new PoseDocument
                    {
                        UnitType = typeEntry.Key,
                        Posture = posture
                    };
                    foreach (KeyValuePair<string, UnitBodyPoseTransform> pair in pose)
                    {
                        UnitBodyPoseTransform transform = pair.Value;
                        record.Parts.Add(new PosePartDocument
                        {
                            Id = pair.Key,
                            PositionX = transform.LocalPosition.X,
                            PositionY = transform.LocalPosition.Y,
                            PositionZ = transform.LocalPosition.Z,
                            RotationX = transform.LocalEulerRotation.X,
                            RotationY = transform.LocalEulerRotation.Y,
                            RotationZ = transform.LocalEulerRotation.Z
                        });
                    }

                    document.Poses.Add(record);
                }
            }

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

    private static Dictionary<string, UnitBodyPoseTransform>[] CreateEmptyPoses()
    {
        var result = new Dictionary<string, UnitBodyPoseTransform>[PoseCount];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Dictionary<string, UnitBodyPoseTransform>(StringComparer.Ordinal);
        return result;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static string? FindProjectRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current != null;
             current = current.Parent)
        {
            if (string.Equals(current.Name, "publish", StringComparison.OrdinalIgnoreCase))
                return null;

            if (File.Exists(Path.Combine(current.FullName, "RimClone.sln")))
                return current.FullName;
        }

        return null;
    }

    private sealed class PoseFileDocument
    {
        public int Version { get; set; } = 2;
        public List<PoseDocument> Poses { get; set; } = new();
    }

    private sealed class PoseDocument
    {
        public int UnitType { get; set; }
        public int Posture { get; set; }
        public List<PosePartDocument> Parts { get; set; } = new();
    }

    private sealed class PosePartDocument
    {
        public string Id { get; set; } = string.Empty;
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
    }
}
