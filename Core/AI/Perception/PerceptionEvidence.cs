using System;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// Modality that supplied evidence. New sensory channels can be added without
/// changing contact memory or the way contacts expose their evidence.
/// </summary>
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

/// <summary>
/// A single piece of sensory evidence. It is a bounded, uncertain report about
/// the world, never an authoritative read of a unit's current position.
/// </summary>
public readonly record struct PerceptionEvidence(
    PerceptionSense Sense,
    TacticalStimulusType StimulusType,
    Vector3 EstimatedPosition,
    float PositionErrorRadius,
    float Intensity,
    float Confidence,
    float OccurredAtSeconds,
    float ReceivedAtSeconds,
    float ExpiresAtSeconds,
    UnitId? SubjectUnit,
    UnitId? ReporterUnit)
{
    /// <summary>Returns this evidence's confidence after age-based decay.</summary>
    public float GetEffectiveConfidence(float nowSeconds, float halfLifeSeconds)
    {
        if (!float.IsFinite(nowSeconds) || nowSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));
        if (nowSeconds >= ExpiresAtSeconds)
            return 0f;

        float age = MathF.Max(0f, nowSeconds - OccurredAtSeconds);
        return Confidence * MathF.Exp(-0.69314718f * age / halfLifeSeconds);
    }
}
