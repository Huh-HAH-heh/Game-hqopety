using System;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// Types of world events that may inform a unit's perception.
/// Publishing a stimulus does not imply that every unit receives it.
/// </summary>
public enum TacticalStimulusType : byte
{
    Unknown = 0,
    VisualContact = 1,
    Gunshot = 2,
    Footstep = 3,
    Explosion = 4,
    NearMiss = 5,
    CoverImpact = 6,
    UnitWounded = 7,
    UnitKilled = 8,
    AllyContactReport = 9
}

/// <summary>
/// Immutable observation/event payload shared by sensors and contact memory.
/// Position is an estimate; PositionErrorRadius communicates its spatial uncertainty.
/// OccurredAtSeconds is simulation time, not wall-clock time.
/// </summary>
public sealed class TacticalStimulus
{
    public TacticalStimulusType Type { get; }
    public Vector3 Position { get; }
    public float OccurredAtSeconds { get; }
    public float Intensity { get; }
    public float PositionErrorRadius { get; }
    public float Confidence { get; }
    public UnitId? SubjectUnit { get; }
    public UnitId? ReporterUnit { get; }

    public TacticalStimulus(
        TacticalStimulusType type,
        Vector3 position,
        float occurredAtSeconds,
        float intensity = 1f,
        float positionErrorRadius = 0f,
        float confidence = 1f,
        UnitId? subjectUnit = null,
        UnitId? reporterUnit = null)
    {
        if (!Enum.IsDefined(type) || type == TacticalStimulusType.Unknown)
            throw new ArgumentOutOfRangeException(nameof(type), "A concrete stimulus type is required.");
        if (!IsFinite(position))
            throw new ArgumentOutOfRangeException(nameof(position), "Stimulus position must be finite.");
        if (!float.IsFinite(occurredAtSeconds) || occurredAtSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(occurredAtSeconds));
        if (!float.IsFinite(intensity))
            throw new ArgumentOutOfRangeException(nameof(intensity));
        if (!float.IsFinite(positionErrorRadius) || positionErrorRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(positionErrorRadius));
        if (!float.IsFinite(confidence))
            throw new ArgumentOutOfRangeException(nameof(confidence));
        if (type == TacticalStimulusType.VisualContact &&
            (!subjectUnit.HasValue || !subjectUnit.Value.IsValid))
        {
            throw new ArgumentException("A visual contact must identify the observed unit.", nameof(subjectUnit));
        }

        Type = type;
        Position = position;
        OccurredAtSeconds = occurredAtSeconds;
        Intensity = Math.Clamp(intensity, 0f, 1f);
        PositionErrorRadius = positionErrorRadius;
        Confidence = Math.Clamp(confidence, 0f, 1f);
        SubjectUnit = ValidOrNull(subjectUnit);
        ReporterUnit = ValidOrNull(reporterUnit);
    }

    private static UnitId? ValidOrNull(UnitId? id) =>
        id.HasValue && id.Value.IsValid ? id : null;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
