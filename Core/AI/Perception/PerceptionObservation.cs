using System;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>Physical or informational channel that supplied an observation.</summary>
public enum PerceptionSense : byte
{
    Unknown = 0,
    Visual = 1,
    Auditory = 2,
    Olfactory = 3,
    Tactile = 4,
    Vibration = 5,
    Thermal = 6,
    ChemicalTrace = 7,
    Communication = 8
}

/// <summary>How strongly a signal supports the presence of something.</summary>
public enum PerceptionDetectionLevel : byte
{
    Unnoticed = 0,
    Suspected = 1,
    Detected = 2
}

/// <summary>How much is known about the identity of the source, independently of detection.</summary>
public enum PerceptionIdentificationLevel : byte
{
    Unknown = 0,
    Possible = 1,
    Identified = 2
}

/// <summary>
/// A sensor's report about the world. Position is an estimate, not an authoritative
/// world coordinate. Detection and identity are separate so a sound or a fleeting
/// shape can be detected without revealing which unit produced it.
/// </summary>
public readonly record struct PerceptionObservation
{
    public PerceptionSense Sense { get; }
    public TacticalStimulusType StimulusType { get; }
    public Vector3 EstimatedPosition { get; }
    public float PositionErrorRadius { get; }
    public float Intensity { get; }
    public float DetectionConfidence { get; }
    public float IdentificationConfidence { get; }
    public PerceptionDetectionLevel DetectionLevel { get; }
    public PerceptionIdentificationLevel IdentificationLevel { get; }
    public float OccurredAtSeconds { get; }
    public float ReceivedAtSeconds { get; }
    public float ExpiresAtSeconds { get; }
    public UnitId? IdentifiedSubject { get; }
    public UnitId? ReporterUnit { get; }

    // Compatibility names for code migrating from the first perception-memory pass.
    public float Confidence => DetectionConfidence;
    public UnitId? SubjectUnit => IdentifiedSubject;

    public PerceptionObservation(
        PerceptionSense sense,
        TacticalStimulusType stimulusType,
        Vector3 estimatedPosition,
        float positionErrorRadius,
        float intensity,
        float detectionConfidence,
        float identificationConfidence,
        PerceptionDetectionLevel detectionLevel,
        PerceptionIdentificationLevel identificationLevel,
        float occurredAtSeconds,
        float receivedAtSeconds,
        float expiresAtSeconds,
        UnitId? identifiedSubject = null,
        UnitId? reporterUnit = null)
    {
        if (!Enum.IsDefined(sense) || sense == PerceptionSense.Unknown)
            throw new ArgumentOutOfRangeException(nameof(sense), "An observation must name its sensory channel.");
        if (!Enum.IsDefined(stimulusType) || stimulusType == TacticalStimulusType.Unknown)
            throw new ArgumentOutOfRangeException(nameof(stimulusType));
        if (!IsFinite(estimatedPosition))
            throw new ArgumentOutOfRangeException(nameof(estimatedPosition));
        if (!float.IsFinite(positionErrorRadius) || positionErrorRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(positionErrorRadius));
        if (!float.IsFinite(intensity))
            throw new ArgumentOutOfRangeException(nameof(intensity));
        if (!float.IsFinite(detectionConfidence))
            throw new ArgumentOutOfRangeException(nameof(detectionConfidence));
        if (!float.IsFinite(identificationConfidence))
            throw new ArgumentOutOfRangeException(nameof(identificationConfidence));
        if (!Enum.IsDefined(detectionLevel) || detectionLevel == PerceptionDetectionLevel.Unnoticed)
            throw new ArgumentOutOfRangeException(nameof(detectionLevel));
        if (!Enum.IsDefined(identificationLevel))
            throw new ArgumentOutOfRangeException(nameof(identificationLevel));
        if (!float.IsFinite(occurredAtSeconds) || occurredAtSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(occurredAtSeconds));
        if (!float.IsFinite(receivedAtSeconds) || receivedAtSeconds < occurredAtSeconds)
            throw new ArgumentOutOfRangeException(nameof(receivedAtSeconds));
        if (!float.IsFinite(expiresAtSeconds) || expiresAtSeconds <= occurredAtSeconds)
            throw new ArgumentOutOfRangeException(nameof(expiresAtSeconds));

        UnitId? validSubject = ValidOrNull(identifiedSubject);
        if (identificationLevel == PerceptionIdentificationLevel.Identified && !validSubject.HasValue)
            throw new ArgumentException("An identified source must have a valid unit identity.", nameof(identifiedSubject));

        Sense = sense;
        StimulusType = stimulusType;
        EstimatedPosition = estimatedPosition;
        PositionErrorRadius = positionErrorRadius;
        Intensity = Math.Clamp(intensity, 0f, 1f);
        DetectionConfidence = Math.Clamp(detectionConfidence, 0f, 1f);
        IdentifiedSubject = validSubject;
        IdentificationConfidence = validSubject.HasValue
            ? Math.Clamp(identificationConfidence, 0f, 1f)
            : 0f;
        DetectionLevel = detectionLevel;
        IdentificationLevel = validSubject.HasValue
            ? PerceptionIdentificationLevel.Identified
            : identificationLevel;
        OccurredAtSeconds = occurredAtSeconds;
        ReceivedAtSeconds = receivedAtSeconds;
        ExpiresAtSeconds = expiresAtSeconds;
        ReporterUnit = ValidOrNull(reporterUnit);
    }

    /// <summary>Converts a prepared event into the uncertain report received by one observer.</summary>
    public static PerceptionObservation FromStimulus(
        TacticalStimulus stimulus,
        float receivedAtSeconds,
        float lifetimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(stimulus);
        if (!float.IsFinite(receivedAtSeconds) ||
            receivedAtSeconds < stimulus.OccurredAtSeconds)
            throw new ArgumentOutOfRangeException(nameof(receivedAtSeconds));
        if (!float.IsFinite(lifetimeSeconds) || lifetimeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));

        float expiresAt = stimulus.OccurredAtSeconds + lifetimeSeconds;
        if (!float.IsFinite(expiresAt))
            throw new ArgumentOutOfRangeException(nameof(lifetimeSeconds));

        PerceptionIdentificationLevel identityLevel = stimulus.SubjectUnit.HasValue
            ? PerceptionIdentificationLevel.Identified
            : PerceptionIdentificationLevel.Unknown;
        float identityConfidence = stimulus.SubjectUnit.HasValue ? stimulus.Confidence : 0f;

        return new PerceptionObservation(
            stimulus.Sense,
            stimulus.Type,
            stimulus.Position,
            stimulus.PositionErrorRadius,
            stimulus.Intensity,
            stimulus.Confidence,
            identityConfidence,
            stimulus.Confidence >= 0.45f
                ? PerceptionDetectionLevel.Detected
                : PerceptionDetectionLevel.Suspected,
            identityLevel,
            stimulus.OccurredAtSeconds,
            receivedAtSeconds,
            expiresAt,
            stimulus.SubjectUnit,
            stimulus.ReporterUnit);
    }

    public float GetEffectiveDetectionConfidence(float nowSeconds, float halfLifeSeconds)
    {
        if (!float.IsFinite(nowSeconds) || nowSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));
        if (nowSeconds >= ExpiresAtSeconds)
            return 0f;

        float age = MathF.Max(0f, nowSeconds - OccurredAtSeconds);
        return DetectionConfidence * MathF.Exp(-0.69314718f * age / halfLifeSeconds);
    }

    private static UnitId? ValidOrNull(UnitId? id) =>
        id.HasValue && id.Value.IsValid ? id : null;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Common write contract for all senses. Sensors publish observations; the memory
/// store handles fusion and lifetime, while each sensor keeps its own detection rules.
/// </summary>
public interface IPerceptionObservationSink
{
    TacticalContact? RecordObservation(
        int observerIndex,
        PerceptionObservation observation,
        long? correlatedContactId = null);
}
