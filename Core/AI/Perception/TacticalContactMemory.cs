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
/// One unit's current knowledge of a possible hostile or threat source.
/// It deliberately stores estimates, not authoritative world state.
/// </summary>
public sealed class TacticalContact
{
    public long ContactId { get; internal set; }
    public TacticalContactKind Kind { get; internal set; }
    public UnitId? SubjectUnit { get; internal set; }
    public UnitId? ReporterUnit { get; internal set; }
    public Vector3 EstimatedPosition { get; internal set; }
    public float PositionErrorRadius { get; internal set; }
    public float Confidence { get; internal set; }
    public float LastEventAtSeconds { get; internal set; }
    public float LastReceivedAtSeconds { get; internal set; }
    public float LastVisualAtSeconds { get; internal set; }
    public float ExpiresAtSeconds { get; internal set; }
    public TacticalStimulusType LastStimulusType { get; internal set; }
    public bool IsInvestigated { get; internal set; }
    internal bool VisualLossMarked { get; set; }

    internal TacticalContact(long contactId, TacticalStimulus stimulus, float receivedAtSeconds, float lifetimeSeconds)
    {
        ContactId = contactId;
        Kind = ResolveKind(stimulus);
        SubjectUnit = stimulus.SubjectUnit;
        ReporterUnit = stimulus.ReporterUnit;
        EstimatedPosition = stimulus.Position;
        PositionErrorRadius = stimulus.PositionErrorRadius;
        Confidence = stimulus.Confidence;
        LastEventAtSeconds = stimulus.OccurredAtSeconds;
        LastReceivedAtSeconds = receivedAtSeconds;
        LastVisualAtSeconds = stimulus.Type == TacticalStimulusType.VisualContact
            ? stimulus.OccurredAtSeconds
            : -1f;
        ExpiresAtSeconds = stimulus.OccurredAtSeconds + lifetimeSeconds;
        LastStimulusType = stimulus.Type;
    }

    /// <summary>
    /// Confidence discounted by the age of the underlying evidence.
    /// This is a read-only estimate; it does not mutate stored confidence.
    /// </summary>
    public float GetEffectiveConfidence(float nowSeconds, float halfLifeSeconds)
    {
        if (!float.IsFinite(nowSeconds) || nowSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));

        float age = MathF.Max(0f, nowSeconds - LastEventAtSeconds);
        return Confidence * MathF.Exp(-0.69314718f * age / halfLifeSeconds);
    }

    internal static TacticalContactKind ResolveKind(TacticalStimulus stimulus)
    {
        if (stimulus.Type == TacticalStimulusType.VisualContact)
            return TacticalContactKind.VisualConfirmed;
        if (stimulus.Type == TacticalStimulusType.AllyContactReport)
            return TacticalContactKind.Reported;
        return stimulus.SubjectUnit.HasValue
            ? TacticalContactKind.LastKnown
            : TacticalContactKind.Suspected;
    }
}

/// <summary>
/// Contact memory for one observer. Sensors feed observations into this store;
/// tactical decisions can query it later. It is not connected to the live AI yet.
/// </summary>
public sealed class TacticalContactMemory
{
    public const float VisualConfirmationGraceSeconds = 0.30f;

    private const float UnknownContactMergeRadiusFloor = 1.5f;

    private readonly List<TacticalContact> _contacts = new();
    private float _currentTimeSeconds;
    private long _nextContactId = 1;

    public IReadOnlyList<TacticalContact> Contacts => _contacts;
    public float CurrentTimeSeconds => _currentTimeSeconds;

    /// <summary>
    /// Records an observation/event at the time it reaches this observer.
    /// Old events are not allowed to revive already-stale knowledge.
    /// Lifetime is measured from event occurrence, not receipt.
    /// </summary>
    public TacticalContact? Record(
        TacticalStimulus stimulus,
        float nowSeconds,
        float memoryLifetimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(stimulus);
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds), "Contact memory time cannot move backwards.");
        if (stimulus.OccurredAtSeconds > nowSeconds)
            throw new ArgumentOutOfRangeException(nameof(stimulus), "A stimulus cannot occur after it is received.");
        if (!float.IsFinite(memoryLifetimeSeconds) || memoryLifetimeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(memoryLifetimeSeconds));

        AdvanceTime(nowSeconds);

        float expiresAt = stimulus.OccurredAtSeconds + memoryLifetimeSeconds;
        if (!float.IsFinite(expiresAt) || expiresAt <= nowSeconds)
            return null;

        TacticalContact? contact = FindBySubject(stimulus.SubjectUnit);
        if (contact == null)
            contact = FindNearbyUnknown(stimulus);

        if (contact == null)
        {
            contact = new TacticalContact(
                _nextContactId++,
                stimulus,
                nowSeconds,
                memoryLifetimeSeconds);
            _contacts.Add(contact);
            return contact;
        }

        UpdateContact(contact, stimulus, nowSeconds, memoryLifetimeSeconds);
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

    /// <summary>
    /// Converts a visible target into a last-known contact without pretending
    /// the unit is still visible. Confidence continues to age from its evidence.
    /// </summary>
    public bool MarkVisualLost(
        UnitId target,
        float nowSeconds,
        float memoryLifetimeSeconds = 3f)
    {
        if (!target.IsValid)
            return false;
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds), "Contact memory time cannot move backwards.");
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

    /// <summary>
    /// Advances simulated time, downgrades stale visual certainty, and removes
    /// expired knowledge. It never changes authoritative unit/world state.
    /// </summary>
    public void AdvanceTime(float nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds), "Contact memory time cannot move backwards.");

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
        for (int i = 0; i < _contacts.Count; i++)
        {
            if (_contacts[i].ContactId != contactId)
                continue;
            _contacts[i].IsInvestigated = true;
            return true;
        }
        return false;
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

    private TacticalContact? FindNearbyUnknown(TacticalStimulus stimulus)
    {
        TacticalContact? nearest = null;
        float nearestDistanceSquared = float.MaxValue;

        for (int i = 0; i < _contacts.Count; i++)
        {
            TacticalContact contact = _contacts[i];
            if (contact.SubjectUnit.HasValue ||
                contact.Kind == TacticalContactKind.VisualConfirmed ||
                (stimulus.Type != TacticalStimulusType.AllyContactReport &&
                 contact.LastStimulusType != stimulus.Type))
            {
                continue;
            }

            float dx = contact.EstimatedPosition.X - stimulus.Position.X;
            float dy = contact.EstimatedPosition.Y - stimulus.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            float mergeRadius = MathF.Max(
                UnknownContactMergeRadiusFloor,
                contact.PositionErrorRadius + stimulus.PositionErrorRadius);

            if (distanceSquared > mergeRadius * mergeRadius ||
                distanceSquared >= nearestDistanceSquared)
            {
                continue;
            }

            nearest = contact;
            nearestDistanceSquared = distanceSquared;
        }

        return nearest;
    }

    private static void UpdateContact(
        TacticalContact contact,
        TacticalStimulus stimulus,
        float nowSeconds,
        float memoryLifetimeSeconds)
    {
        bool newerEvidence = stimulus.OccurredAtSeconds > contact.LastEventAtSeconds;
        TacticalContactKind incomingKind = TacticalContact.ResolveKind(stimulus);
        bool recentlyVisual = contact.Kind == TacticalContactKind.VisualConfirmed &&
            nowSeconds - contact.LastVisualAtSeconds < VisualConfirmationGraceSeconds;

        if (stimulus.Type == TacticalStimulusType.VisualContact)
            contact.Kind = TacticalContactKind.VisualConfirmed;
        else if (!recentlyVisual)
            contact.Kind = incomingKind;

        // Prefer the more precise estimate. Confidence is not allowed to inflate
        // from a stream of repeated low-confidence events.
        if (stimulus.PositionErrorRadius <= contact.PositionErrorRadius ||
            stimulus.Confidence > contact.Confidence)
        {
            contact.EstimatedPosition = stimulus.Position;
            contact.PositionErrorRadius = stimulus.PositionErrorRadius;
        }

        contact.Confidence = MathF.Max(contact.Confidence, stimulus.Confidence);
        contact.LastEventAtSeconds = MathF.Max(contact.LastEventAtSeconds, stimulus.OccurredAtSeconds);
        contact.LastReceivedAtSeconds = nowSeconds;
        contact.ExpiresAtSeconds = MathF.Max(
            contact.ExpiresAtSeconds,
            stimulus.OccurredAtSeconds + memoryLifetimeSeconds);
        contact.LastStimulusType = stimulus.Type;
        if (newerEvidence)
            contact.IsInvestigated = false;

        if (stimulus.Type == TacticalStimulusType.VisualContact)
        {
            contact.LastVisualAtSeconds = stimulus.OccurredAtSeconds;
            contact.VisualLossMarked = false;
        }

        if (stimulus.ReporterUnit.HasValue)
            contact.ReporterUnit = stimulus.ReporterUnit;

        if (stimulus.SubjectUnit.HasValue)
            contact.SubjectUnit = stimulus.SubjectUnit;
    }

    private static void ValidateTime(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
