using System;
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
/// Value handle into the shared perception contact pool. The slot generation prevents
/// a cached handle from observing a different contact after that slot is recycled.
/// </summary>
public readonly struct TacticalContact : IEquatable<TacticalContact>
{
    private readonly PerceptionContactStorage? _owner;
    internal readonly int Slot;
    internal readonly int Generation;

    internal TacticalContact(PerceptionContactStorage owner, int slot, int generation)
    {
        _owner = owner;
        Slot = slot;
        Generation = generation;
    }

    public bool IsValid =>
        _owner != null && _owner.IsValidHandle(Slot, Generation);

    public long ContactId =>
        _owner?.GetContactId(Slot, Generation) ?? 0L;

    public TacticalContactKind Kind =>
        _owner?.GetKind(Slot, Generation) ?? TacticalContactKind.Suspected;

    public UnitId? SubjectUnit =>
        _owner?.GetSubjectUnit(Slot, Generation);

    public UnitId? ReporterUnit =>
        _owner?.GetReporterUnit(Slot, Generation);

    public Vector3 EstimatedPosition =>
        _owner?.GetEstimatedPosition(Slot, Generation) ?? Vector3.Zero;

    public float PositionErrorRadius =>
        _owner?.GetPositionErrorRadius(Slot, Generation) ?? 0f;

    public float Confidence =>
        _owner?.GetConfidence(Slot, Generation) ?? 0f;

    public PerceptionEvidenceCollection Observations =>
        new(_owner, Slot, Generation);

    public PerceptionEvidenceCollection Evidence => Observations;

    public float LastEventAtSeconds =>
        _owner?.GetLastEventAtSeconds(Slot, Generation) ?? 0f;

    public float LastReceivedAtSeconds =>
        _owner?.GetLastReceivedAtSeconds(Slot, Generation) ?? 0f;

    public float LastVisualAtSeconds =>
        _owner?.GetLastVisualAtSeconds(Slot, Generation) ?? -1f;

    public float ExpiresAtSeconds =>
        _owner?.GetExpiresAtSeconds(Slot, Generation) ?? 0f;

    public TacticalStimulusType LastStimulusType =>
        _owner?.GetLastStimulusType(Slot, Generation) ?? TacticalStimulusType.Unknown;

    public bool IsInvestigated =>
        _owner?.GetIsInvestigated(Slot, Generation) ?? false;

    public PerceptionDetectionLevel GetDetectionLevel(float nowSeconds) =>
        _owner?.GetDetectionLevel(Slot, Generation, nowSeconds) ??
        PerceptionDetectionLevel.Unnoticed;

    public PerceptionIdentificationLevel GetIdentificationLevel(float nowSeconds) =>
        _owner?.GetIdentificationLevel(Slot, Generation, nowSeconds) ??
        PerceptionIdentificationLevel.Unknown;

    public float GetEffectiveConfidence(float nowSeconds, float halfLifeSeconds) =>
        _owner?.GetEffectiveConfidence(Slot, Generation, nowSeconds, halfLifeSeconds) ?? 0f;

    public PerceptionSignalFlags GetActiveSignals(float nowSeconds) =>
        _owner?.GetActiveSignals(Slot, Generation, nowSeconds) ??
        PerceptionSignalFlags.None;

    public float GetEffectiveIdentificationConfidence(float nowSeconds, float halfLifeSeconds) =>
        _owner?.GetEffectiveIdentificationConfidence(
            Slot, Generation, nowSeconds, halfLifeSeconds) ?? 0f;

    public bool Equals(TacticalContact other) =>
        ReferenceEquals(_owner, other._owner) &&
        Slot == other.Slot &&
        Generation == other.Generation;

    public override bool Equals(object? obj) =>
        obj is TacticalContact other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_owner, Slot, Generation);

    public static bool operator ==(TacticalContact left, TacticalContact right) =>
        left.Equals(right);

    public static bool operator !=(TacticalContact left, TacticalContact right) =>
        !left.Equals(right);

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

    internal static bool IsFreshVisual(PerceptionObservation observation) =>
        observation.Sense == PerceptionSense.Visual &&
        observation.DetectionLevel == PerceptionDetectionLevel.Detected &&
        observation.IdentifiedSubject.HasValue;
}

/// <summary>
/// Value facade for one observer's shared-memory slice. No object or collection is
/// allocated when the simulation asks for a memory view.
/// </summary>
public readonly struct TacticalContactMemory
{
    public const float VisualConfirmationGraceSeconds = 1.00f;

    private readonly PerceptionContactStorage? _owner;
    private readonly int _observerIndex;
    private readonly int _generation;

    internal TacticalContactMemory(
        PerceptionContactStorage owner,
        int observerIndex,
        int generation)
    {
        _owner = owner;
        _observerIndex = observerIndex;
        _generation = generation;
    }

    public bool IsValid =>
        _owner != null && _owner.IsObserverHandleValid(_observerIndex, _generation);

    public float CurrentTimeSeconds =>
        _owner?.CurrentTimeSeconds ?? 0f;

    public int ContactCount =>
        IsValid ? _owner!.GetContactCount(_observerIndex) : 0;

    internal int EvidenceInUse => _owner?.EvidenceInUse ?? 0;

    public TacticalContactCollection Contacts =>
        new(_owner, _observerIndex, _generation);

    public TacticalContact? Record(
        TacticalStimulus stimulus,
        float nowSeconds,
        float memoryLifetimeSeconds)
    {
        if (!IsValid)
            return null;
        return _owner!.Record(
            _observerIndex,
            PerceptionObservation.FromStimulus(stimulus, nowSeconds, memoryLifetimeSeconds));
    }

    public TacticalContact? Record(
        PerceptionObservation observation,
        long? correlatedContactId = null)
    {
        if (!IsValid)
            return null;
        return _owner!.Record(_observerIndex, observation, correlatedContactId);
    }

    public TacticalContact? ObserveVisual(
        UnitId target,
        Vector3 position,
        float nowSeconds,
        float memoryLifetimeSeconds = 3f,
        float positionErrorRadius = 0.25f)
    {
        if (!IsValid)
            return null;
        return _owner!.ObserveVisual(
            _observerIndex, target, position, nowSeconds,
            memoryLifetimeSeconds, positionErrorRadius);
    }

    public bool MarkVisualLost(
        UnitId target,
        float nowSeconds,
        float memoryLifetimeSeconds = 3f) =>
        IsValid && _owner!.MarkVisualLost(
            _observerIndex, target, nowSeconds, memoryLifetimeSeconds);

    public void AdvanceTime(float nowSeconds)
    {
        if (IsValid)
            _owner!.AdvanceTime(nowSeconds);
    }

    public bool TryGetContact(UnitId target, out TacticalContact contact)
    {
        if (IsValid)
            return _owner!.TryGetContact(_observerIndex, target, out contact);

        contact = default;
        return false;
    }

    public bool MarkInvestigated(long contactId) =>
        IsValid && _owner!.MarkInvestigated(_observerIndex, contactId);

    public bool Forget(UnitId target) =>
        IsValid && _owner!.Forget(_observerIndex, target);

    public void Clear()
    {
        if (IsValid)
            _owner!.ClearObserver(_observerIndex, invalidateHandle: false);
    }
}

/// <summary>
/// Non-allocating indexed view and enumerator over one observer's linked contact slice.
/// </summary>
public readonly struct TacticalContactCollection
{
    private readonly PerceptionContactStorage? _owner;
    private readonly int _observerIndex;
    private readonly int _generation;

    internal TacticalContactCollection(
        PerceptionContactStorage? owner,
        int observerIndex,
        int generation)
    {
        _owner = owner;
        _observerIndex = observerIndex;
        _generation = generation;
    }

    public int Count =>
        _owner != null && _owner.IsObserverHandleValid(_observerIndex, _generation)
            ? _owner.GetContactCount(_observerIndex)
            : 0;

    public TacticalContact this[int index] =>
        _owner == null ||
        !_owner.IsObserverHandleValid(_observerIndex, _generation)
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : _owner.GetContactAt(_observerIndex, index);

    public Enumerator GetEnumerator() =>
        new(_owner, _observerIndex, _generation);

    public struct Enumerator
    {
        private readonly PerceptionContactStorage? _owner;
        private readonly int _observerIndex;
        private readonly int _generation;
        private int _currentSlot;

        internal Enumerator(
            PerceptionContactStorage? owner,
            int observerIndex,
            int generation)
        {
            _owner = owner;
            _observerIndex = observerIndex;
            _generation = generation;
            _currentSlot = -1;
        }

        public TacticalContact Current =>
            _owner == null
                ? default
                : _owner.GetContactHandle(_currentSlot);

        public bool MoveNext()
        {
            if (_owner == null ||
                !_owner.IsObserverHandleValid(_observerIndex, _generation))
            {
                return false;
            }

            _currentSlot = _currentSlot < 0
                ? _owner.GetFirstContactSlot(_observerIndex)
                : _owner.GetNextContactSlot(_currentSlot);
            return _currentSlot >= 0;
        }
    }
}

/// <summary>
/// Non-allocating indexed view and enumerator over a contact's evidence nodes.
/// </summary>
public readonly struct PerceptionEvidenceCollection
{
    private readonly PerceptionContactStorage? _owner;
    private readonly int _slot;
    private readonly int _generation;

    internal PerceptionEvidenceCollection(
        PerceptionContactStorage? owner,
        int slot,
        int generation)
    {
        _owner = owner;
        _slot = slot;
        _generation = generation;
    }

    public int Count =>
        _owner?.GetEvidenceCount(_slot, _generation) ?? 0;

    public PerceptionObservation this[int index] =>
        _owner == null
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : _owner.GetEvidenceAt(_slot, _generation, index);

    public Enumerator GetEnumerator() =>
        new(_owner, _slot, _generation);

    public struct Enumerator
    {
        private readonly PerceptionContactStorage? _owner;
        private readonly int _slot;
        private readonly int _generation;
        private int _currentNode;

        internal Enumerator(
            PerceptionContactStorage? owner,
            int slot,
            int generation)
        {
            _owner = owner;
            _slot = slot;
            _generation = generation;
            _currentNode = -1;
        }

        public PerceptionObservation Current =>
            _owner == null ? default : _owner.GetEvidenceValue(_currentNode);

        public bool MoveNext()
        {
            if (_owner == null ||
                !_owner.IsValidHandle(_slot, _generation))
            {
                return false;
            }

            _currentNode = _currentNode < 0
                ? _owner.GetFirstEvidenceNode(_slot)
                : _owner.GetNextEvidenceNode(_currentNode);
            return _currentNode >= 0;
        }
    }
}
