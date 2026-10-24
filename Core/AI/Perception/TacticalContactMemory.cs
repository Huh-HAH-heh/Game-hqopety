using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

public enum TacticalContactKind : byte
{
    Suspected = 0,
    Reported = 1,
    LastKnown = 2,
    VisualConfirmed = 3
}

/// <summary>
/// One observer's fused knowledge about a possible source. Its position and confidence
/// are derived from evidence; this object is never the authoritative unit state.
/// </summary>
public sealed class TacticalContact
{
    public long ContactId { get; internal set; }
    public TacticalContactKind Kind { get; internal set; }
    public UnitId? SubjectUnit { get; internal set; }
    public UnitId? ReporterUnit { get; internal set; }
    public Vector3 EstimatedPosition { get; internal set; }
    public float PositionErrorRadius { get; internal set; }
    public float Confidence => GetCombinedConfidence(LastReceivedAtSeconds, 1f, applyAgeDecay: false);
    public IReadOnlyList<PerceptionObservation> Observations => _observations;
    public IReadOnlyList<PerceptionObservation> Evidence => _observations;
    public float LastEventAtSeconds { get; internal set; }
    public float LastReceivedAtSeconds { get; internal set; }
    public float LastVisualAtSeconds { get; internal set; }
    public float ExpiresAtSeconds { get; internal set; }
    public TacticalStimulusType LastStimulusType { get; internal set; }
    public bool IsInvestigated { get; internal set; }
    internal bool VisualLossMarked { get; set; }

    private const int MaxEvidencePerSense = 4;
    private readonly List<PerceptionObservation> _observations = new();

    internal TacticalContact(long contactId, PerceptionObservation observation)
    {
        ContactId = contactId;
        Kind = ResolveKind(observation);
        SubjectUnit = observation.IdentifiedSubject;
        ReporterUnit = observation.ReporterUnit;
        EstimatedPosition = observation.EstimatedPosition;
        PositionErrorRadius = observation.PositionErrorRadius;
        LastEventAtSeconds = observation.OccurredAtSeconds;
        LastReceivedAtSeconds = observation.ReceivedAtSeconds;
        LastVisualAtSeconds = IsFreshVisual(observation)
            ? observation.OccurredAtSeconds
            : -1f;
        ExpiresAtSeconds = observation.ExpiresAtSeconds;
        LastStimulusType = observation.StimulusType;
        RecordObservation(observation);
    }

    /// <summary>
    /// Keeps a small recent sample per sense. Repeated signals cannot inflate confidence
    /// without bound; different senses remain available to corroborate one anonymous track.
    /// </summary>
    internal void RecordObservation(PerceptionObservation observation)
    {
        for (int i = _observations.Count - 1; i >= 0; i--)
        {
            if (_observations[i].ExpiresAtSeconds <= observation.ReceivedAtSeconds)
                _observations.RemoveAt(i);
        }

        int senseCount = 0;
        int oldestIndex = -1;
        float oldestTime = float.PositiveInfinity;
        for (int i = 0; i < _observations.Count; i++)
        {
            PerceptionObservation existing = _observations[i];
            if (existing.Sense != observation.Sense)
                continue;

            senseCount++;
            if (existing.OccurredAtSeconds < oldestTime)
            {
                oldestTime = existing.OccurredAtSeconds;
                oldestIndex = i;
            }
        }

        if (senseCount >= MaxEvidencePerSense && oldestIndex >= 0)
            _observations.RemoveAt(oldestIndex);

        _observations.Add(observation);
    }

    private float GetCombinedConfidence(
        float nowSeconds,
        float halfLifeSeconds,
        bool applyAgeDecay)
    {
        if (!float.IsFinite(nowSeconds) || nowSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if (applyAgeDecay &&
            (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f))
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));

        Span<PerceptionSense> senses = _observations.Count <= 64
            ? stackalloc PerceptionSense[_observations.Count]
            : new PerceptionSense[_observations.Count];
        Span<float> strongestBySense = _observations.Count <= 64
            ? stackalloc float[_observations.Count]
            : new float[_observations.Count];
        int senseCount = 0;

        for (int i = 0; i < _observations.Count; i++)
        {
            PerceptionObservation observation = _observations[i];
            if (observation.ExpiresAtSeconds <= nowSeconds)
                continue;

            float confidence = observation.DetectionConfidence;
            if (applyAgeDecay)
            {
                float age = MathF.Max(0f, nowSeconds - observation.OccurredAtSeconds);
                confidence *= MathF.Exp(-0.69314718f * age / halfLifeSeconds);
            }

            int senseSlot = 0;
            while (senseSlot < senseCount && senses[senseSlot] != observation.Sense)
                senseSlot++;

            if (senseSlot == senseCount)
            {
                senses[senseCount] = observation.Sense;
                strongestBySense[senseCount] = confidence;
                senseCount++;
            }
            else if (confidence > strongestBySense[senseSlot])
            {
                strongestBySense[senseSlot] = confidence;
            }
        }

        // Each channel contributes at most its strongest live observation.
        // Independent channels corroborate through confidence union.
        float remainingUncertainty = 1f;
        for (int i = 0; i < senseCount; i++)
            remainingUncertainty *= 1f - Math.Clamp(strongestBySense[i], 0f, 1f);

        return Math.Clamp(1f - remainingUncertainty, 0f, 1f);
    }

    public PerceptionDetectionLevel GetDetectionLevel(float nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        PerceptionDetectionLevel level = PerceptionDetectionLevel.Unnoticed;
        for (int i = 0; i < _observations.Count; i++)
        {
            PerceptionObservation observation = _observations[i];
            if (observation.ExpiresAtSeconds > nowSeconds && observation.DetectionLevel > level)
                level = observation.DetectionLevel;
        }
        return level;
    }

    public PerceptionIdentificationLevel GetIdentificationLevel(float nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (SubjectUnit.HasValue && ExpiresAtSeconds > nowSeconds)
            return PerceptionIdentificationLevel.Identified;

        PerceptionIdentificationLevel level = PerceptionIdentificationLevel.Unknown;
        for (int i = 0; i < _observations.Count; i++)
        {
            PerceptionObservation observation = _observations[i];
            if (observation.ExpiresAtSeconds > nowSeconds && observation.IdentificationLevel > level)
                level = observation.IdentificationLevel;
        }
        return level;
    }

    public float GetEffectiveConfidence(float nowSeconds, float halfLifeSeconds) =>
        GetCombinedConfidence(nowSeconds, halfLifeSeconds, applyAgeDecay: true);

    /// <summary>Combines active semantic flags from all unexpired evidence for this contact.</summary>
    public PerceptionSignalFlags GetActiveSignals(float nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        PerceptionSignalFlags signals = PerceptionSignalFlags.None;
        for (int i = 0; i < _observations.Count; i++)
        {
            PerceptionObservation observation = _observations[i];
            if (observation.ExpiresAtSeconds > nowSeconds)
                signals |= observation.Signals;
        }
        return signals;
    }

    /// <summary>Identity evidence decays independently from confidence that a signal was detected.</summary>
    public float GetEffectiveIdentificationConfidence(float nowSeconds, float halfLifeSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));

        Span<PerceptionSense> senses = _observations.Count <= 64
            ? stackalloc PerceptionSense[_observations.Count]
            : new PerceptionSense[_observations.Count];
        Span<float> strongestBySense = _observations.Count <= 64
            ? stackalloc float[_observations.Count]
            : new float[_observations.Count];
        int senseCount = 0;

        for (int i = 0; i < _observations.Count; i++)
        {
            PerceptionObservation observation = _observations[i];
            if (observation.ExpiresAtSeconds <= nowSeconds ||
                observation.IdentificationConfidence <= 0f)
                continue;

            float age = MathF.Max(0f, nowSeconds - observation.OccurredAtSeconds);
            float confidence = observation.IdentificationConfidence *
                MathF.Exp(-0.69314718f * age / halfLifeSeconds);
            int slot = 0;
            while (slot < senseCount && senses[slot] != observation.Sense)
                slot++;

            if (slot == senseCount)
            {
                senses[senseCount] = observation.Sense;
                strongestBySense[senseCount] = confidence;
                senseCount++;
            }
            else if (confidence > strongestBySense[slot])
            {
                strongestBySense[slot] = confidence;
            }
        }

        float remainingUncertainty = 1f;
        for (int i = 0; i < senseCount; i++)
            remainingUncertainty *= 1f - Math.Clamp(strongestBySense[i], 0f, 1f);
        return Math.Clamp(1f - remainingUncertainty, 0f, 1f);
    }

    internal static TacticalContactKind ResolveKind(PerceptionObservation observation)
    {
        if (IsFreshVisual(observation))
            return TacticalContactKind.VisualConfirmed;
        if (observation.StimulusType == TacticalStimulusType.AllyContactReport)
            return TacticalContactKind.Reported;
        return observation.IdentifiedSubject.HasValue
            ? TacticalContactKind.LastKnown
            : TacticalContactKind.Suspected;
    }

    private static bool IsFreshVisual(PerceptionObservation observation) =>
        observation.Sense == PerceptionSense.Visual &&
        observation.DetectionLevel == PerceptionDetectionLevel.Detected &&
        observation.IdentifiedSubject.HasValue;

    private static void ValidateTime(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// Per-observer fusion memory shared by every sensory channel.
/// </summary>
public sealed class TacticalContactMemory
{
    public const float VisualConfirmationGraceSeconds = 1.00f;

    private const float UnknownContactMergeRadiusFloor = 1.5f;
    private const float CrossSenseMergeWindowSeconds = 1.25f;

    private readonly List<TacticalContact> _contacts = new();
    private float _currentTimeSeconds;
    private long _nextContactId = 1;

    public IReadOnlyList<TacticalContact> Contacts => _contacts;
    public float CurrentTimeSeconds => _currentTimeSeconds;

    /// <summary>Compatibility bridge for event producers; the memory stores observations.</summary>
    public TacticalContact? Record(
        TacticalStimulus stimulus,
        float nowSeconds,
        float memoryLifetimeSeconds) =>
        Record(PerceptionObservation.FromStimulus(stimulus, nowSeconds, memoryLifetimeSeconds));

    /// <summary>
    /// Records one sensor result. A correlated contact ID is an explicit track association,
    /// allowing a later sensor to identify a previously anonymous contact without guessing
    /// identity from position alone.
    /// </summary>
    public TacticalContact? Record(
        PerceptionObservation observation,
        long? correlatedContactId = null)
    {
        float nowSeconds = observation.ReceivedAtSeconds;
        ValidateTime(nowSeconds, nameof(observation));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(observation),
                "Contact memory time cannot move backwards.");
        if (observation.OccurredAtSeconds > nowSeconds)
            throw new ArgumentOutOfRangeException(nameof(observation),
                "An observation cannot occur after it is received.");

        AdvanceTime(nowSeconds);
        if (observation.ExpiresAtSeconds <= nowSeconds)
            return null;

        TacticalContact? correlated = correlatedContactId.HasValue
            ? FindByContactId(correlatedContactId.Value)
            : null;
        if (correlatedContactId.HasValue && correlated == null)
            return null;
        if (correlated?.SubjectUnit is UnitId correlatedSubject &&
            observation.IdentifiedSubject is UnitId correlatedIncomingSubject &&
            !correlatedSubject.Equals(correlatedIncomingSubject))
            return null;

        TacticalContact? contact = observation.IdentifiedSubject.HasValue
            ? FindBySubject(observation.IdentifiedSubject)
            : null;

        if (contact != null && correlated != null && contact != correlated)
        {
            MergeInto(contact, correlated);
            _contacts.Remove(correlated);
        }
        else
        {
            contact ??= correlated;
        }

        if (contact?.SubjectUnit is UnitId existingSubject &&
            observation.IdentifiedSubject is UnitId incomingSubject &&
            !existingSubject.Equals(incomingSubject))
        {
            return null;
        }

        if (contact == null && !observation.IdentifiedSubject.HasValue)
            contact = FindNearbyUnknown(observation);

        if (contact == null)
        {
            contact = new TacticalContact(_nextContactId++, observation);
            _contacts.Add(contact);
            return contact;
        }

        UpdateContact(contact, observation);
        return contact;
    }

    public TacticalContact? ObserveVisual(
        UnitId target,
        Vector3 position,
        float nowSeconds,
        float memoryLifetimeSeconds = 3f,
        float positionErrorRadius = 0.25f)
    {
        if (!target.IsValid)
            throw new ArgumentException("A visual observation must identify a valid unit.", nameof(target));

        var stimulus = new TacticalStimulus(
            TacticalStimulusType.VisualContact,
            position,
            nowSeconds,
            intensity: 1f,
            positionErrorRadius: positionErrorRadius,
            confidence: 1f,
            subjectUnit: target);

        return Record(stimulus, nowSeconds, memoryLifetimeSeconds);
    }

    /// <summary>Downgrades an identified visual contact without inventing a new position.</summary>
    public bool MarkVisualLost(
        UnitId target,
        float nowSeconds,
        float memoryLifetimeSeconds = 3f)
    {
        if (!target.IsValid)
            return false;
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds),
                "Contact memory time cannot move backwards.");
        if (!float.IsFinite(memoryLifetimeSeconds) || memoryLifetimeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(memoryLifetimeSeconds));

        AdvanceTime(nowSeconds);
        TacticalContact? contact = FindBySubject(target);
        if (contact == null ||
            contact.LastVisualAtSeconds < 0f ||
            contact.VisualLossMarked)
        {
            return false;
        }

        contact.Kind = TacticalContactKind.LastKnown;
        contact.ExpiresAtSeconds = nowSeconds + memoryLifetimeSeconds;
        contact.LastReceivedAtSeconds = nowSeconds;
        contact.VisualLossMarked = true;
        return true;
    }

    public void AdvanceTime(float nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds),
                "Contact memory time cannot move backwards.");

        _currentTimeSeconds = nowSeconds;
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            TacticalContact contact = _contacts[i];
            if (contact.ExpiresAtSeconds <= nowSeconds)
            {
                _contacts.RemoveAt(i);
                continue;
            }

            if (contact.Kind == TacticalContactKind.VisualConfirmed &&
                nowSeconds - contact.LastVisualAtSeconds >= VisualConfirmationGraceSeconds)
            {
                contact.Kind = TacticalContactKind.LastKnown;
            }
        }
    }

    public bool TryGetContact(UnitId target, out TacticalContact contact)
    {
        TacticalContact? found = FindBySubject(target);
        if (found != null)
        {
            contact = found;
            return true;
        }

        contact = null!;
        return false;
    }

    public bool MarkInvestigated(long contactId)
    {
        TacticalContact? contact = FindByContactId(contactId);
        if (contact == null)
            return false;
        contact.IsInvestigated = true;
        return true;
    }

    public bool Forget(UnitId target)
    {
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            if (_contacts[i].SubjectUnit is UnitId subject && subject.Equals(target))
            {
                _contacts.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        _contacts.Clear();
        _currentTimeSeconds = 0f;
        _nextContactId = 1;
    }

    private TacticalContact? FindByContactId(long contactId)
    {
        for (int i = 0; i < _contacts.Count; i++)
        {
            if (_contacts[i].ContactId == contactId)
                return _contacts[i];
        }
        return null;
    }

    private TacticalContact? FindBySubject(UnitId? subject)
    {
        if (!subject.HasValue || !subject.Value.IsValid)
            return null;

        for (int i = 0; i < _contacts.Count; i++)
        {
            if (_contacts[i].SubjectUnit is UnitId candidate &&
                candidate.Equals(subject.Value))
            {
                return _contacts[i];
            }
        }

        return null;
    }

    private TacticalContact? FindNearbyUnknown(PerceptionObservation observation)
    {
        TacticalContact? nearest = null;
        float nearestDistanceSquared = float.MaxValue;

        for (int i = 0; i < _contacts.Count; i++)
        {
            TacticalContact contact = _contacts[i];
            if (contact.SubjectUnit.HasValue ||
                MathF.Abs(contact.LastEventAtSeconds - observation.OccurredAtSeconds) >
                    CrossSenseMergeWindowSeconds)
            {
                continue;
            }

            bool hasSameSense = false;
            float latestSameSenseTime = float.NegativeInfinity;
            TacticalStimulusType latestSameSenseType = TacticalStimulusType.Unknown;
            for (int j = 0; j < contact.Observations.Count; j++)
            {
                PerceptionObservation prior = contact.Observations[j];
                if (prior.Sense != observation.Sense)
                    continue;
                hasSameSense = true;
                if (prior.OccurredAtSeconds >= latestSameSenseTime)
                {
                    latestSameSenseTime = prior.OccurredAtSeconds;
                    latestSameSenseType = prior.StimulusType;
                }
            }

            // Same-sense signals are grouped only when they describe the same event type.
            // Different senses may corroborate an anonymous contact in the same time/space window.
            if (hasSameSense && latestSameSenseType != observation.StimulusType)
                continue;

            Vector3 delta = contact.EstimatedPosition - observation.EstimatedPosition;
            float distanceSquared = delta.LengthSquared();
            float mergeRadius = MathF.Max(
                UnknownContactMergeRadiusFloor,
                contact.PositionErrorRadius + observation.PositionErrorRadius);

            if (distanceSquared > mergeRadius * mergeRadius ||
                distanceSquared >= nearestDistanceSquared)
                continue;

            nearest = contact;
            nearestDistanceSquared = distanceSquared;
        }

        return nearest;
    }

    private static void UpdateContact(TacticalContact contact, PerceptionObservation observation)
    {
        bool newerEvidence = observation.OccurredAtSeconds > contact.LastEventAtSeconds;
        TacticalContactKind incomingKind = TacticalContact.ResolveKind(observation);
        bool recentlyVisual = contact.Kind == TacticalContactKind.VisualConfirmed &&
            observation.ReceivedAtSeconds - contact.LastVisualAtSeconds < VisualConfirmationGraceSeconds;
        bool freshVisual = observation.Sense == PerceptionSense.Visual &&
            observation.DetectionLevel == PerceptionDetectionLevel.Detected &&
            observation.IdentifiedSubject.HasValue;

        if (freshVisual)
            contact.Kind = TacticalContactKind.VisualConfirmed;
        else if (!recentlyVisual)
            contact.Kind = incomingKind;

        if (observation.PositionErrorRadius <= contact.PositionErrorRadius ||
            observation.DetectionConfidence > contact.Confidence)
        {
            contact.EstimatedPosition = observation.EstimatedPosition;
            contact.PositionErrorRadius = observation.PositionErrorRadius;
        }

        contact.LastEventAtSeconds = MathF.Max(
            contact.LastEventAtSeconds, observation.OccurredAtSeconds);
        contact.LastReceivedAtSeconds = observation.ReceivedAtSeconds;
        contact.ExpiresAtSeconds = MathF.Max(
            contact.ExpiresAtSeconds, observation.ExpiresAtSeconds);
        contact.LastStimulusType = observation.StimulusType;
        contact.RecordObservation(observation);
        if (newerEvidence)
            contact.IsInvestigated = false;

        if (freshVisual)
        {
            contact.LastVisualAtSeconds = observation.OccurredAtSeconds;
            contact.VisualLossMarked = false;
        }

        if (observation.ReporterUnit.HasValue)
            contact.ReporterUnit = observation.ReporterUnit;
        if (observation.IdentifiedSubject.HasValue)
            contact.SubjectUnit = observation.IdentifiedSubject;
    }

    private static void MergeInto(TacticalContact destination, TacticalContact source)
    {
        for (int i = 0; i < source.Observations.Count; i++)
            destination.RecordObservation(source.Observations[i]);

        if (source.LastEventAtSeconds > destination.LastEventAtSeconds)
        {
            destination.EstimatedPosition = source.EstimatedPosition;
            destination.PositionErrorRadius = source.PositionErrorRadius;
            destination.LastEventAtSeconds = source.LastEventAtSeconds;
            destination.LastStimulusType = source.LastStimulusType;
            if (source.LastVisualAtSeconds > destination.LastVisualAtSeconds)
            {
                destination.LastVisualAtSeconds = source.LastVisualAtSeconds;
                destination.Kind = source.Kind;
                destination.VisualLossMarked = source.VisualLossMarked;
            }
        }

        destination.LastReceivedAtSeconds = MathF.Max(
            destination.LastReceivedAtSeconds, source.LastReceivedAtSeconds);
        destination.ExpiresAtSeconds = MathF.Max(
            destination.ExpiresAtSeconds, source.ExpiresAtSeconds);
        destination.ReporterUnit ??= source.ReporterUnit;
        destination.SubjectUnit ??= source.SubjectUnit;
        destination.IsInvestigated &= source.IsInvestigated;
    }

    private static void ValidateTime(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
