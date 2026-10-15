using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Core.Unit;

public readonly record struct UnitPoseAdjustment(
    Vector3 PositionOffset,
    Vector3 RotationOffset);

/// <summary>
/// Persistent per-unit-type pose adjustments relative to the shared procedural pose.
/// Stable region IDs keep saved poses resilient to reordering parts in the editor.
/// </summary>
public static class UnitPoseProfileStore
{
    private const int CurrentVersion = 1;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static bool _loaded;
    private static Dictionary<PoseKey, UnitPoseAdjustment> _adjustments = new();
    private static List<PosePartSettings> _entries = new();

    public static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Defs", "Units", "unit-poses.json");

    public static string LastLoadError { get; private set; } = string.Empty;

    public static bool TryGetAdjustment(
        string unitTypeId,
        UnitPosture posture,
        string regionId,
        out UnitPoseAdjustment adjustment)
    {
        adjustment = default;
        if (string.IsNullOrWhiteSpace(unitTypeId) || string.IsNullOrWhiteSpace(regionId))
            return false;

        EnsureLoaded();
        Dictionary<PoseKey, UnitPoseAdjustment> snapshot = System.Threading.Volatile.Read(ref _adjustments);
        return snapshot.TryGetValue(new PoseKey(unitTypeId, posture, regionId), out adjustment);
    }

    public static BodyPartDefinition ApplyAdjustment(
        BodyPartDefinition proceduralPose,
        UnitPoseAdjustment adjustment) =>
        BodyPartPoseMath.WithTransform(
            proceduralPose,
            proceduralPose.LocalPosition + adjustment.PositionOffset,
            proceduralPose.LocalEulerRotation + adjustment.RotationOffset);

    public static bool SaveOffsets(
        string unitTypeId,
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> baseParts,
        BodyPartDefinition[][] posedParts,
        out string fullPath,
        out string error)
    {
        fullPath = FilePath;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(unitTypeId))
        {
            error = "Не задан идентификатор типа юнита.";
            return false;
        }
        if (regionIds.Length == 0 || regionIds.Length != baseParts.Length ||
            posedParts == null || posedParts.Length <= (int)UnitPosture.Crouching)
        {
            error = "Состав поз не совпадает с базовой геометрией.";
            return false;
        }

        foreach (UnitPosture posture in Enum.GetValues<UnitPosture>())
        {
            if (posedParts[(int)posture] == null ||
                posedParts[(int)posture].Length != baseParts.Length)
            {
                error = $"В позе «{posture}» отсутствуют части тела.";
                return false;
            }
        }

        lock (Sync)
        {
            try
            {
                PoseDocument document = ReadDocument(FilePath);
                document.Version = CurrentVersion;
                document.Entries ??= new List<PosePartSettings>();
                document.Entries.RemoveAll(entry =>
                    string.Equals(entry.UnitTypeId, unitTypeId, StringComparison.Ordinal));

                foreach (UnitPosture posture in Enum.GetValues<UnitPosture>())
                {
                    BodyPartDefinition[] targetParts = posedParts[(int)posture];
                    for (int i = 0; i < regionIds.Length; i++)
                    {
                        string id = regionIds[i];
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            error = $"У части №{i + 1} отсутствует стабильный ID.";
                            return false;
                        }

                        BodyPartDefinition baseline = BodyPartPoseMath.ForPosture(baseParts[i], posture);
                        BodyPartDefinition target = targetParts[i];
                        Vector3 positionOffset = target.LocalPosition - baseline.LocalPosition;
                        Vector3 rotationOffset = BodyPartPoseMath.NormalizeEulerDelta(
                            target.LocalEulerRotation - baseline.LocalEulerRotation);

                        if (positionOffset.LengthSquared() < 1e-10f &&
                            rotationOffset.LengthSquared() < 1e-10f)
                            continue;

                        document.Entries.Add(new PosePartSettings
                        {
                            UnitTypeId = unitTypeId,
                            Posture = posture.ToString(),
                            RegionId = id,
                            OffsetX = positionOffset.X,
                            OffsetY = positionOffset.Y,
                            OffsetZ = positionOffset.Z,
                            RotationOffsetX = rotationOffset.X,
                            RotationOffsetY = rotationOffset.Y,
                            RotationOffsetZ = rotationOffset.Z
                        });
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                string temporaryPath = fullPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporaryPath, fullPath, overwrite: true);
                Publish(document);
                LastLoadError = string.Empty;
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                error = exception.Message;
                return false;
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        lock (Sync)
        {
            if (_loaded)
                return;

            try
            {
                Publish(ReadDocument(FilePath));
                LastLoadError = string.Empty;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _entries = new List<PosePartSettings>();
                _adjustments = new Dictionary<PoseKey, UnitPoseAdjustment>();
                LastLoadError = exception.Message;
            }

            _loaded = true;
        }
    }

    private static PoseDocument ReadDocument(string path)
    {
        if (!File.Exists(path))
            return new PoseDocument { Version = CurrentVersion, Entries = new List<PosePartSettings>() };

        PoseDocument? document = JsonSerializer.Deserialize<PoseDocument>(
            File.ReadAllText(path), JsonOptions);
        if (document == null || document.Version < 1 || document.Entries == null)
            throw new InvalidDataException("Файл поз имеет неподдерживаемый формат.");
        return document;
    }

    private static void Publish(PoseDocument document)
    {
        var cache = new Dictionary<PoseKey, UnitPoseAdjustment>();
        foreach (PosePartSettings entry in document.Entries ?? new List<PosePartSettings>())
        {
            if (string.IsNullOrWhiteSpace(entry.UnitTypeId) ||
                string.IsNullOrWhiteSpace(entry.RegionId) ||
                !Enum.TryParse(entry.Posture, ignoreCase: true, out UnitPosture posture) ||
                !Enum.IsDefined(posture))
                continue;

            Vector3 position = new(entry.OffsetX, entry.OffsetY, entry.OffsetZ);
            Vector3 rotation = new(
                entry.RotationOffsetX,
                entry.RotationOffsetY,
                entry.RotationOffsetZ);
            if (!IsFinite(position) || !IsFinite(rotation))
                continue;

            cache[new PoseKey(entry.UnitTypeId, posture, entry.RegionId)] =
                new UnitPoseAdjustment(position, rotation);
        }

        _entries = new List<PosePartSettings>(document.Entries ?? new List<PosePartSettings>());
        System.Threading.Volatile.Write(ref _adjustments, cache);
        _loaded = true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct PoseKey(
        string UnitTypeId,
        UnitPosture Posture,
        string RegionId);

    private sealed class PoseDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public List<PosePartSettings>? Entries { get; set; } = new();
    }

    private sealed class PosePartSettings
    {
        public string UnitTypeId { get; set; } = string.Empty;
        public string Posture { get; set; } = nameof(UnitPosture.Standing);
        public string RegionId { get; set; } = string.Empty;
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public float RotationOffsetX { get; set; }
        public float RotationOffsetY { get; set; }
        public float RotationOffsetZ { get; set; }
    }
}
