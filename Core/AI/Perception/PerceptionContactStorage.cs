using System;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// Shared data-oriented storage for all observers. Contact state is SoA; active contacts
/// are linked by observer using integer indices; evidence records come from one reusable
/// node pool. No observer, contact, or evidence owns a List or a per-item heap object.
/// </summary>
internal sealed class PerceptionContactStorage
{
    private const float UnknownContactMergeRadiusFloor = 1.5f;
    private const float CrossSenseMergeWindowSeconds = 1.25f;
    private const float ContactMaintenanceIntervalSeconds = 0.5f;
    private const int MaxEvidencePerSense = 4;
    private const int SensoryChannelCount = 8;
    private const int InitialContactCapacity = 32;
    private const int InitialEvidenceCapacity = 64;

    private int[] _firstContactByObserver;
    private int[] _lastContactByObserver;
    private int[] _contactCountByObserver;
    private int[] _observerGeneration;

    private int[] _activeSlots = new int[InitialContactCapacity];
    private int[] _activeIndex = new int[InitialContactCapacity];
    private int _activeCount;
    private int _slotCount;
    private int _freeContactHead = -1;

    private bool[] _active = new bool[InitialContactCapacity];
    private int[] _generation = new int[InitialContactCapacity];
    private int[] _freeContactNext = new int[InitialContactCapacity];
    private int[] _observerIndex = new int[InitialContactCapacity];
    private int[] _previousForObserver = new int[InitialContactCapacity];
    private int[] _nextForObserver = new int[InitialContactCapacity];
    private long[] _contactId = new long[InitialContactCapacity];
    private TacticalContactKind[] _kind = new TacticalContactKind[InitialContactCapacity];
    private UnitId?[] _subjectUnit = new UnitId?[InitialContactCapacity];
    private UnitId?[] _reporterUnit = new UnitId?[InitialContactCapacity];
    private Vector3[] _estimatedPosition = new Vector3[InitialContactCapacity];
    private float[] _positionErrorRadius = new float[InitialContactCapacity];
    private float[] _lastEventAt = new float[InitialContactCapacity];
    private float[] _lastReceivedAt = new float[InitialContactCapacity];
    private float[] _lastVisualAt = new float[InitialContactCapacity];
    private float[] _expiresAt = new float[InitialContactCapacity];
    private TacticalStimulusType[] _lastStimulusType =
        new TacticalStimulusType[InitialContactCapacity];
    private bool[] _isInvestigated = new bool[InitialContactCapacity];
    private bool[] _visualLossMarked = new bool[InitialContactCapacity];
    private int[] _evidenceHead = new int[InitialContactCapacity];
    private int[] _evidenceTail = new int[InitialContactCapacity];
    private byte[] _evidenceCount = new byte[InitialContactCapacity];

    private PerceptionObservation[] _evidenceValues =
        new PerceptionObservation[InitialEvidenceCapacity];
    private int[] _evidenceNext = new int[InitialEvidenceCapacity];
    private int _evidenceSlotCount;
    private int _freeEvidenceHead = -1;
    private int _evidenceInUse;

    private float _currentTimeSeconds;
    private float _lastMaintenanceTimeSeconds = float.NegativeInfinity;
    private long _nextContactId = 1;

    public float CurrentTimeSeconds => _currentTimeSeconds;
    public int EvidenceInUse => _evidenceInUse;

    public PerceptionContactStorage(int observerCapacity)
    {
        if (observerCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(observerCapacity));

        _firstContactByObserver = new int[observerCapacity];
        _lastContactByObserver = new int[observerCapacity];
        _contactCountByObserver = new int[observerCapacity];
        _observerGeneration = new int[observerCapacity];
        Array.Fill(_firstContactByObserver, -1);
        Array.Fill(_lastContactByObserver, -1);
        Array.Fill(_observerGeneration, 1);

        Array.Fill(_activeIndex, -1);
        Array.Fill(_previousForObserver, -1);
        Array.Fill(_nextForObserver, -1);
        Array.Fill(_evidenceHead, -1);
        Array.Fill(_evidenceTail, -1);
        Array.Fill(_lastVisualAt, -1f);
    }

    public int GetObserverGeneration(int observerIndex)
    {
        EnsureObserverIndex(observerIndex);
        return _observerGeneration[observerIndex];
    }

    public bool IsObserverHandleValid(int observerIndex, int generation) =>
        (uint)observerIndex < (uint)_observerGeneration.Length &&
        _observerGeneration[observerIndex] == generation;

    public void EnsureObserverCapacity(int required)
    {
        if (required <= 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required <= _observerGeneration.Length)
            return;

        int oldCapacity = _observerGeneration.Length;
        int capacity = Math.Max(required, oldCapacity * 2);
        Array.Resize(ref _firstContactByObserver, capacity);
        Array.Resize(ref _lastContactByObserver, capacity);
        Array.Resize(ref _contactCountByObserver, capacity);
        Array.Resize(ref _observerGeneration, capacity);

        for (int i = oldCapacity; i < capacity; i++)
        {
            _firstContactByObserver[i] = -1;
            _lastContactByObserver[i] = -1;
            _observerGeneration[i] = 1;
        }
    }

    public TacticalContact? Record(
        int observerIndex,
        PerceptionObservation observation,
        long? correlatedContactId = null)
    {
        EnsureObserverIndex(observerIndex);
        float nowSeconds = observation.ReceivedAtSeconds;
        ValidateTime(nowSeconds, nameof(observation));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(observation),
                "Perception memory time cannot move backwards.");
        if (observation.OccurredAtSeconds > nowSeconds)
            throw new ArgumentOutOfRangeException(nameof(observation),
                "An observation cannot occur after it is received.");

        AdvanceTime(nowSeconds);
        if (observation.ExpiresAtSeconds <= nowSeconds)
            return null;

        TacticalContact? correlated = correlatedContactId.HasValue
            ? FindByContactId(observerIndex, correlatedContactId.Value)
            : null;
        if (correlatedContactId.HasValue && !correlated.HasValue)
            return null;

        if (correlated.HasValue &&
            correlated.Value.SubjectUnit is UnitId correlatedSubject &&
            observation.IdentifiedSubject is UnitId correlatedIncomingSubject &&
            !correlatedSubject.Equals(correlatedIncomingSubject))
        {
            return null;
        }

        TacticalContact? contact = observation.IdentifiedSubject.HasValue
            ? FindBySubject(observerIndex, observation.IdentifiedSubject)
            : null;

        if (contact.HasValue &&
            correlated.HasValue &&
            !contact.Value.Equals(correlated.Value))
        {
            MergeInto(contact.Value, correlated.Value);
            RemoveContact(correlated.Value);
        }
        else if (!contact.HasValue)
        {
            contact = correlated;
        }

        if (contact.HasValue &&
            contact.Value.SubjectUnit is UnitId existingSubject &&
            observation.IdentifiedSubject is UnitId incomingSubject &&
            !existingSubject.Equals(incomingSubject))
        {
            return null;
        }

        if (!contact.HasValue && !observation.IdentifiedSubject.HasValue)
            contact = FindNearbyUnknown(observerIndex, observation);

        if (!contact.HasValue)
            return CreateContact(observerIndex, observation);

        UpdateContact(contact.Value, observation);
        return contact;
    }

    public TacticalContact? ObserveVisual(
        int observerIndex,
        UnitId target,
        Vector3 position,
        float nowSeconds,
        float memoryLifetimeSeconds,
        float positionErrorRadius)
    {
        if (!target.IsValid)
            throw new ArgumentException(
                "A visual observation must identify a valid unit.", nameof(target));

        var stimulus = new TacticalStimulus(
            TacticalStimulusType.VisualContact,
            position,
            nowSeconds,
            intensity: 1f,
            positionErrorRadius: positionErrorRadius,
            confidence: 1f,
            subjectUnit: target);

        return Record(observerIndex,
            PerceptionObservation.FromStimulus(stimulus, nowSeconds, memoryLifetimeSeconds));
    }

    public bool MarkVisualLost(
        int observerIndex,
        UnitId target,
        float nowSeconds,
        float memoryLifetimeSeconds)
    {
        EnsureObserverIndex(observerIndex);
        if (!target.IsValid)
            return false;
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds),
                "Perception memory time cannot move backwards.");
        if (!float.IsFinite(memoryLifetimeSeconds) || memoryLifetimeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(memoryLifetimeSeconds));

        AdvanceTime(nowSeconds);
        TacticalContact? contact = FindBySubject(observerIndex, target);
        if (!contact.HasValue ||
            contact.Value.LastVisualAtSeconds < 0f ||
            _visualLossMarked[contact.Value.Slot])
        {
            return false;
        }

        int slot = contact.Value.Slot;
        _kind[slot] = TacticalContactKind.LastKnown;
        _expiresAt[slot] = nowSeconds + memoryLifetimeSeconds;
        _lastReceivedAt[slot] = nowSeconds;
        _visualLossMarked[slot] = true;
        return true;
    }

    public void AdvanceTime(float nowSeconds)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (nowSeconds < _currentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds),
                "Perception memory time cannot move backwards.");
        if (nowSeconds == _currentTimeSeconds)
            return;

        _currentTimeSeconds = nowSeconds;
        if (nowSeconds - _lastMaintenanceTimeSeconds < ContactMaintenanceIntervalSeconds)
            return;

        _lastMaintenanceTimeSeconds = nowSeconds;
        for (int i = _activeCount - 1; i >= 0; i--)
        {
            int slot = _activeSlots[i];
            if (_expiresAt[slot] <= nowSeconds)
                RemoveContact(slot);
        }
    }

    public bool TryGetContact(
        int observerIndex,
        UnitId target,
        out TacticalContact contact)
    {
        TacticalContact? found = FindBySubject(observerIndex, target);
        if (found.HasValue)
        {
            contact = found.Value;
            return true;
        }

        contact = default;
        return false;
    }

    public bool MarkInvestigated(int observerIndex, long contactId)
    {
        int slot = FindSlotByContactId(observerIndex, contactId);
        if (slot < 0)
            return false;
        _isInvestigated[slot] = true;
        return true;
    }

    public bool Forget(int observerIndex, UnitId target)
    {
        int slot = _firstContactByObserver[observerIndex];
        while (slot >= 0)
        {
            int next = _nextForObserver[slot];
            if (_subjectUnit[slot] is UnitId subject && subject.Equals(target))
            {
                RemoveContact(slot);
                return true;
            }
            slot = next;
        }

        return false;
    }

    public void ClearObserver(int observerIndex, bool invalidateHandle = true)
    {
        EnsureObserverIndex(observerIndex);
        int slot = _firstContactByObserver[observerIndex];
        while (slot >= 0)
        {
            int next = _nextForObserver[slot];
            RemoveContact(slot);
            slot = next;
        }

        if (!invalidateHandle)
            return;

        _observerGeneration[observerIndex] =
            _observerGeneration[observerIndex] == int.MaxValue
                ? 1
                : _observerGeneration[observerIndex] + 1;
    }

    public int GetContactCount(int observerIndex) =>
        (uint)observerIndex < (uint)_contactCountByObserver.Length
            ? _contactCountByObserver[observerIndex]
            : 0;

    public TacticalContact GetContactAt(int observerIndex, int index)
    {
        if ((uint)observerIndex >= (uint)_firstContactByObserver.Length ||
            (uint)index >= (uint)_contactCountByObserver[observerIndex])
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int slot = _firstContactByObserver[observerIndex];
        for (int i = 0; i < index; i++)
            slot = _nextForObserver[slot];
        return new TacticalContact(this, slot, _generation[slot]);
    }

    public int GetFirstContactSlot(int observerIndex) =>
        (uint)observerIndex < (uint)_firstContactByObserver.Length
            ? _firstContactByObserver[observerIndex]
            : -1;

    public int GetNextContactSlot(int slot) =>
        IsActiveSlot(slot) ? _nextForObserver[slot] : -1;

    public TacticalContact GetContactHandle(int slot) =>
        IsActiveSlot(slot)
            ? new TacticalContact(this, slot, _generation[slot])
            : default;

    public bool IsValidHandle(int slot, int generation) =>
        IsActiveSlot(slot) && _generation[slot] == generation;

    public long GetContactId(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _contactId[slot] : 0L;

    public TacticalContactKind GetKind(int slot, int generation)
    {
        if (!IsValidHandle(slot, generation))
            return TacticalContactKind.Suspected;

        return _kind[slot] == TacticalContactKind.VisualConfirmed &&
               _currentTimeSeconds - _lastVisualAt[slot] >=
                   TacticalContactMemory.VisualConfirmationGraceSeconds
            ? TacticalContactKind.LastKnown
            : _kind[slot];
    }

    public UnitId? GetSubjectUnit(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _subjectUnit[slot] : null;

    public UnitId? GetReporterUnit(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _reporterUnit[slot] : null;

    public Vector3 GetEstimatedPosition(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _estimatedPosition[slot] : Vector3.Zero;

    public float GetPositionErrorRadius(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _positionErrorRadius[slot] : 0f;

    public float GetLastEventAtSeconds(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _lastEventAt[slot] : 0f;

    public float GetLastReceivedAtSeconds(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _lastReceivedAt[slot] : 0f;

    public float GetLastVisualAtSeconds(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _lastVisualAt[slot] : -1f;

    public float GetExpiresAtSeconds(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _expiresAt[slot] : 0f;

    public TacticalStimulusType GetLastStimulusType(int slot, int generation) =>
        IsValidHandle(slot, generation)
            ? _lastStimulusType[slot]
            : TacticalStimulusType.Unknown;

    public bool GetIsInvestigated(int slot, int generation) =>
        IsValidHandle(slot, generation) && _isInvestigated[slot];

    public int GetEvidenceCount(int slot, int generation) =>
        IsValidHandle(slot, generation) ? _evidenceCount[slot] : 0;

    public PerceptionObservation GetEvidenceAt(
        int slot,
        int generation,
        int index)
    {
        if (!IsValidHandle(slot, generation) ||
            (uint)index >= _evidenceCount[slot])
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        int node = _evidenceHead[slot];
        for (int i = 0; i < index; i++)
            node = _evidenceNext[node];
        return _evidenceValues[node];
    }

    public int GetFirstEvidenceNode(int slot) =>
        IsActiveSlot(slot) ? _evidenceHead[slot] : -1;

    public int GetNextEvidenceNode(int node) =>
        (uint)node < (uint)_evidenceSlotCount ? _evidenceNext[node] : -1;

    public PerceptionObservation GetEvidenceValue(int node) =>
        (uint)node < (uint)_evidenceSlotCount ? _evidenceValues[node] : default;

    public float GetConfidence(int slot, int generation)
    {
        if (!IsValidHandle(slot, generation))
            return 0f;
        return GetCombinedConfidence(slot, _lastReceivedAt[slot], 1f, applyAgeDecay: false);
    }

    public PerceptionDetectionLevel GetDetectionLevel(
        int slot,
        int generation,
        float nowSeconds)
    {
        if (!IsValidHandle(slot, generation))
            return PerceptionDetectionLevel.Unnoticed;
        ValidateTime(nowSeconds, nameof(nowSeconds));

        PerceptionDetectionLevel level = PerceptionDetectionLevel.Unnoticed;
        for (int node = _evidenceHead[slot]; node >= 0; node = _evidenceNext[node])
        {
            PerceptionObservation observation = _evidenceValues[node];
            if (observation.ExpiresAtSeconds > nowSeconds &&
                observation.DetectionLevel > level)
            {
                level = observation.DetectionLevel;
            }
        }

        return level;
    }

    public PerceptionIdentificationLevel GetIdentificationLevel(
        int slot,
        int generation,
        float nowSeconds)
    {
        if (!IsValidHandle(slot, generation))
            return PerceptionIdentificationLevel.Unknown;
        ValidateTime(nowSeconds, nameof(nowSeconds));

        if (_subjectUnit[slot].HasValue && _expiresAt[slot] > nowSeconds)
            return PerceptionIdentificationLevel.Identified;

        PerceptionIdentificationLevel level = PerceptionIdentificationLevel.Unknown;
        for (int node = _evidenceHead[slot]; node >= 0; node = _evidenceNext[node])
        {
            PerceptionObservation observation = _evidenceValues[node];
            if (observation.ExpiresAtSeconds > nowSeconds &&
                observation.IdentificationLevel > level)
            {
                level = observation.IdentificationLevel;
            }
        }

        return level;
    }

    public float GetEffectiveConfidence(
        int slot,
        int generation,
        float nowSeconds,
        float halfLifeSeconds)
    {
        if (!IsValidHandle(slot, generation))
            return 0f;
        return GetCombinedConfidence(slot, nowSeconds, halfLifeSeconds, applyAgeDecay: true);
    }

    public PerceptionSignalFlags GetActiveSignals(
        int slot,
        int generation,
        float nowSeconds)
    {
        if (!IsValidHandle(slot, generation))
            return PerceptionSignalFlags.None;
        ValidateTime(nowSeconds, nameof(nowSeconds));

        PerceptionSignalFlags signals = PerceptionSignalFlags.None;
        for (int node = _evidenceHead[slot]; node >= 0; node = _evidenceNext[node])
        {
            PerceptionObservation observation = _evidenceValues[node];
            if (observation.ExpiresAtSeconds > nowSeconds)
                signals |= observation.Signals;
        }

        return signals;
    }

    public float GetEffectiveIdentificationConfidence(
        int slot,
        int generation,
        float nowSeconds,
        float halfLifeSeconds)
    {
        if (!IsValidHandle(slot, generation))
            return 0f;
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));

        Span<PerceptionSense> senses = stackalloc PerceptionSense[SensoryChannelCount];
        Span<float> strongestBySense = stackalloc float[SensoryChannelCount];
        int senseCount = 0;

        for (int node = _evidenceHead[slot]; node >= 0; node = _evidenceNext[node])
        {
            PerceptionObservation observation = _evidenceValues[node];
            if (observation.ExpiresAtSeconds <= nowSeconds ||
                observation.IdentificationConfidence <= 0f)
            {
                continue;
            }

            float age = MathF.Max(0f, nowSeconds - observation.OccurredAtSeconds);
            float confidence = observation.IdentificationConfidence *
                MathF.Exp(-0.69314718f * age / halfLifeSeconds);
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

        float remainingUncertainty = 1f;
        for (int i = 0; i < senseCount; i++)
            remainingUncertainty *= 1f - Math.Clamp(strongestBySense[i], 0f, 1f);

        return Math.Clamp(1f - remainingUncertainty, 0f, 1f);
    }

    private TacticalContact CreateContact(
        int observerIndex,
        PerceptionObservation observation)
    {
        int slot = AllocateContactSlot();
        _active[slot] = true;
        _observerIndex[slot] = observerIndex;
        _contactId[slot] = _nextContactId++;
        _kind[slot] = TacticalContact.ResolveKind(observation);
        _subjectUnit[slot] = observation.IdentifiedSubject;
        _reporterUnit[slot] = observation.ReporterUnit;
        _estimatedPosition[slot] = observation.EstimatedPosition;
        _positionErrorRadius[slot] = observation.PositionErrorRadius;
        _lastEventAt[slot] = observation.OccurredAtSeconds;
        _lastReceivedAt[slot] = observation.ReceivedAtSeconds;
        _lastVisualAt[slot] = TacticalContact.IsFreshVisual(observation)
            ? observation.OccurredAtSeconds
            : -1f;
        _expiresAt[slot] = observation.ExpiresAtSeconds;
        _lastStimulusType[slot] = observation.StimulusType;
        _isInvestigated[slot] = false;
        _visualLossMarked[slot] = false;
        _evidenceHead[slot] = -1;
        _evidenceTail[slot] = -1;
        _evidenceCount[slot] = 0;

        int tail = _lastContactByObserver[observerIndex];
        _previousForObserver[slot] = tail;
        _nextForObserver[slot] = -1;
        if (tail < 0)
            _firstContactByObserver[observerIndex] = slot;
        else
            _nextForObserver[tail] = slot;
        _lastContactByObserver[observerIndex] = slot;
        _contactCountByObserver[observerIndex]++;

        _activeIndex[slot] = _activeCount;
        _activeSlots[_activeCount++] = slot;
        AddEvidence(slot, observation);
        return new TacticalContact(this, slot, _generation[slot]);
    }

    private int AllocateContactSlot()
    {
        int slot;
        if (_freeContactHead >= 0)
        {
            slot = _freeContactHead;
            _freeContactHead = _freeContactNext[slot];
        }
        else
        {
            slot = _slotCount++;
            EnsureContactCapacity(slot + 1);
        }

        if (_generation[slot] == 0)
            _generation[slot] = 1;
        _freeContactNext[slot] = -1;
        return slot;
    }

    private void RemoveContact(TacticalContact contact)
    {
        if (!IsValidHandle(contact.Slot, contact.Generation))
            return;
        RemoveContact(contact.Slot);
    }

    private void RemoveContact(int slot)
    {
        if (!IsActiveSlot(slot))
            return;

        int observer = _observerIndex[slot];
        int previous = _previousForObserver[slot];
        int next = _nextForObserver[slot];

        if (previous < 0)
            _firstContactByObserver[observer] = next;
        else
            _nextForObserver[previous] = next;

        if (next < 0)
            _lastContactByObserver[observer] = previous;
        else
            _previousForObserver[next] = previous;

        _contactCountByObserver[observer]--;
        ReleaseContactEvidence(slot);

        int activeIndex = _activeIndex[slot];
        int lastActiveIndex = --_activeCount;
        int movedSlot = _activeSlots[lastActiveIndex];
        _activeSlots[activeIndex] = movedSlot;
        _activeIndex[movedSlot] = activeIndex;
        _activeSlots[lastActiveIndex] = 0;
        _activeIndex[slot] = -1;

        _active[slot] = false;
        _observerIndex[slot] = -1;
        _previousForObserver[slot] = -1;
        _nextForObserver[slot] = -1;
        _contactId[slot] = 0L;
        _subjectUnit[slot] = null;
        _reporterUnit[slot] = null;
        _estimatedPosition[slot] = Vector3.Zero;
        _positionErrorRadius[slot] = 0f;
        _lastEventAt[slot] = 0f;
        _lastReceivedAt[slot] = 0f;
        _lastVisualAt[slot] = -1f;
        _expiresAt[slot] = 0f;
        _lastStimulusType[slot] = TacticalStimulusType.Unknown;
        _isInvestigated[slot] = false;
        _visualLossMarked[slot] = false;
        _kind[slot] = TacticalContactKind.Suspected;

        _generation[slot] =
            _generation[slot] == int.MaxValue
                ? 1
                : _generation[slot] + 1;
        _freeContactNext[slot] = _freeContactHead;
        _freeContactHead = slot;
    }

    private TacticalContact? FindByContactId(int observerIndex, long contactId)
    {
        int slot = FindSlotByContactId(observerIndex, contactId);
        return slot >= 0
            ? new TacticalContact(this, slot, _generation[slot])
            : null;
    }

    private int FindSlotByContactId(int observerIndex, long contactId)
    {
        int slot = _firstContactByObserver[observerIndex];
        while (slot >= 0)
        {
            if (_contactId[slot] == contactId &&
                _expiresAt[slot] > _currentTimeSeconds)
            {
                return slot;
            }

            slot = _nextForObserver[slot];
        }

        return -1;
    }

    private TacticalContact? FindBySubject(int observerIndex, UnitId? subject)
    {
        if (!subject.HasValue || !subject.Value.IsValid)
            return null;

        int slot = _firstContactByObserver[observerIndex];
        while (slot >= 0)
        {
            if (_expiresAt[slot] > _currentTimeSeconds &&
                _subjectUnit[slot] is UnitId candidate &&
                candidate.Equals(subject.Value))
            {
                return new TacticalContact(this, slot, _generation[slot]);
            }

            slot = _nextForObserver[slot];
        }

        return null;
    }

    private TacticalContact? FindNearbyUnknown(
        int observerIndex,
        PerceptionObservation observation)
    {
        int nearestSlot = -1;
        float nearestDistanceSquared = float.MaxValue;
        int slot = _firstContactByObserver[observerIndex];

        while (slot >= 0)
        {
            if (_expiresAt[slot] > observation.ReceivedAtSeconds &&
                !_subjectUnit[slot].HasValue &&
                MathF.Abs(_lastEventAt[slot] - observation.OccurredAtSeconds) <=
                    CrossSenseMergeWindowSeconds)
            {
                bool hasSameSense = false;
                float latestSameSenseTime = float.NegativeInfinity;
                TacticalStimulusType latestSameSenseType = TacticalStimulusType.Unknown;

                for (int node = _evidenceHead[slot]; node >= 0; node = _evidenceNext[node])
                {
                    PerceptionObservation prior = _evidenceValues[node];
                    if (prior.Sense != observation.Sense)
                        continue;

                    hasSameSense = true;
                    if (prior.OccurredAtSeconds >= latestSameSenseTime)
                    {
                        latestSameSenseTime = prior.OccurredAtSeconds;
                        latestSameSenseType = prior.StimulusType;
                    }
                }

                if (!hasSameSense || latestSameSenseType == observation.StimulusType)
                {
                    Vector3 delta = _estimatedPosition[slot] - observation.EstimatedPosition;
                    float distanceSquared = delta.LengthSquared();
                    float mergeRadius = MathF.Max(
                        UnknownContactMergeRadiusFloor,
                        _positionErrorRadius[slot] + observation.PositionErrorRadius);

                    if (distanceSquared <= mergeRadius * mergeRadius &&
                        distanceSquared < nearestDistanceSquared)
                    {
                        nearestSlot = slot;
                        nearestDistanceSquared = distanceSquared;
                    }
                }
            }

            slot = _nextForObserver[slot];
        }

        return nearestSlot >= 0
            ? new TacticalContact(this, nearestSlot, _generation[nearestSlot])
            : null;
    }

    private void UpdateContact(TacticalContact contact, PerceptionObservation observation)
    {
        int slot = contact.Slot;
        bool newerEvidence = observation.OccurredAtSeconds > _lastEventAt[slot];
        TacticalContactKind incomingKind = TacticalContact.ResolveKind(observation);
        bool recentlyVisual =
            GetKind(slot, _generation[slot]) == TacticalContactKind.VisualConfirmed &&
            observation.ReceivedAtSeconds - _lastVisualAt[slot] <
                TacticalContactMemory.VisualConfirmationGraceSeconds;
        bool freshVisual = TacticalContact.IsFreshVisual(observation);

        if (freshVisual)
            _kind[slot] = TacticalContactKind.VisualConfirmed;
        else if (!recentlyVisual)
            _kind[slot] = incomingKind;

        float currentConfidence = GetConfidence(slot, _generation[slot]);
        if (observation.PositionErrorRadius <= _positionErrorRadius[slot] ||
            observation.DetectionConfidence > currentConfidence)
        {
            _estimatedPosition[slot] = observation.EstimatedPosition;
            _positionErrorRadius[slot] = observation.PositionErrorRadius;
        }

        _lastEventAt[slot] = MathF.Max(
            _lastEventAt[slot], observation.OccurredAtSeconds);
        _lastReceivedAt[slot] = observation.ReceivedAtSeconds;
        _expiresAt[slot] = MathF.Max(
            _expiresAt[slot], observation.ExpiresAtSeconds);
        _lastStimulusType[slot] = observation.StimulusType;
        AddEvidence(slot, observation);
        if (newerEvidence)
            _isInvestigated[slot] = false;

        if (freshVisual)
        {
            _lastVisualAt[slot] = observation.OccurredAtSeconds;
            _visualLossMarked[slot] = false;
        }

        if (observation.ReporterUnit.HasValue)
            _reporterUnit[slot] = observation.ReporterUnit;
        if (observation.IdentifiedSubject.HasValue)
            _subjectUnit[slot] = observation.IdentifiedSubject;
    }

    private void MergeInto(TacticalContact destination, TacticalContact source)
    {
        if (!destination.IsValid || !source.IsValid || destination == source)
            return;

        int destinationSlot = destination.Slot;
        int sourceSlot = source.Slot;
        for (int node = _evidenceHead[sourceSlot]; node >= 0; node = _evidenceNext[node])
            AddEvidence(destinationSlot, _evidenceValues[node]);

        if (_lastEventAt[sourceSlot] > _lastEventAt[destinationSlot])
        {
            _estimatedPosition[destinationSlot] = _estimatedPosition[sourceSlot];
            _positionErrorRadius[destinationSlot] = _positionErrorRadius[sourceSlot];
            _lastEventAt[destinationSlot] = _lastEventAt[sourceSlot];
            _lastStimulusType[destinationSlot] = _lastStimulusType[sourceSlot];
            if (_lastVisualAt[sourceSlot] > _lastVisualAt[destinationSlot])
            {
                _lastVisualAt[destinationSlot] = _lastVisualAt[sourceSlot];
                _kind[destinationSlot] = _kind[sourceSlot];
                _visualLossMarked[destinationSlot] = _visualLossMarked[sourceSlot];
            }
        }

        _lastReceivedAt[destinationSlot] = MathF.Max(
            _lastReceivedAt[destinationSlot], _lastReceivedAt[sourceSlot]);
        _expiresAt[destinationSlot] = MathF.Max(
            _expiresAt[destinationSlot], _expiresAt[sourceSlot]);
        _reporterUnit[destinationSlot] ??= _reporterUnit[sourceSlot];
        _subjectUnit[destinationSlot] ??= _subjectUnit[sourceSlot];
        _isInvestigated[destinationSlot] &= _isInvestigated[sourceSlot];
    }

    private void AddEvidence(int contactSlot, PerceptionObservation observation)
    {
        RemoveExpiredEvidence(contactSlot, observation.ReceivedAtSeconds);

        int senseCount = 0;
        int oldestNode = -1;
        int oldestPrevious = -1;
        int previous = -1;
        float oldestTime = float.PositiveInfinity;

        for (int node = _evidenceHead[contactSlot]; node >= 0; node = _evidenceNext[node])
        {
            PerceptionObservation existing = _evidenceValues[node];
            if (existing.Sense == observation.Sense)
            {
                senseCount++;
                if (existing.OccurredAtSeconds < oldestTime)
                {
                    oldestTime = existing.OccurredAtSeconds;
                    oldestNode = node;
                    oldestPrevious = previous;
                }
            }

            previous = node;
        }

        if (senseCount >= MaxEvidencePerSense && oldestNode >= 0)
            RemoveEvidenceNode(contactSlot, oldestNode, oldestPrevious);

        int newNode = AllocateEvidenceNode();
        _evidenceValues[newNode] = observation;
        _evidenceNext[newNode] = -1;

        int tail = _evidenceTail[contactSlot];
        if (tail < 0)
            _evidenceHead[contactSlot] = newNode;
        else
            _evidenceNext[tail] = newNode;

        _evidenceTail[contactSlot] = newNode;
        _evidenceCount[contactSlot]++;
    }

    private void RemoveExpiredEvidence(int contactSlot, float receivedAtSeconds)
    {
        int previous = -1;
        int node = _evidenceHead[contactSlot];
        while (node >= 0)
        {
            int next = _evidenceNext[node];
            if (_evidenceValues[node].ExpiresAtSeconds <= receivedAtSeconds)
                RemoveEvidenceNode(contactSlot, node, previous);
            else
                previous = node;
            node = next;
        }
    }

    private void RemoveEvidenceNode(int contactSlot, int node, int previous)
    {
        int next = _evidenceNext[node];
        if (previous < 0)
            _evidenceHead[contactSlot] = next;
        else
            _evidenceNext[previous] = next;

        if (_evidenceTail[contactSlot] == node)
            _evidenceTail[contactSlot] = previous;

        if (_evidenceCount[contactSlot] > 0)
            _evidenceCount[contactSlot]--;

        ReleaseEvidenceNode(node);
    }

    private void ReleaseContactEvidence(int contactSlot)
    {
        int node = _evidenceHead[contactSlot];
        while (node >= 0)
        {
            int next = _evidenceNext[node];
            ReleaseEvidenceNode(node);
            node = next;
        }

        _evidenceHead[contactSlot] = -1;
        _evidenceTail[contactSlot] = -1;
        _evidenceCount[contactSlot] = 0;
    }

    private int AllocateEvidenceNode()
    {
        int node;
        if (_freeEvidenceHead >= 0)
        {
            node = _freeEvidenceHead;
            _freeEvidenceHead = _evidenceNext[node];
        }
        else
        {
            if (_evidenceSlotCount >= _evidenceValues.Length)
                EnsureEvidenceCapacity(_evidenceSlotCount + 1);
            node = _evidenceSlotCount++;
        }

        _evidenceInUse++;
        _evidenceNext[node] = -1;
        return node;
    }

    private void ReleaseEvidenceNode(int node)
    {
        _evidenceValues[node] = default;
        _evidenceNext[node] = _freeEvidenceHead;
        _freeEvidenceHead = node;
        _evidenceInUse--;
    }

    private float GetCombinedConfidence(
        int contactSlot,
        float nowSeconds,
        float halfLifeSeconds,
        bool applyAgeDecay)
    {
        ValidateTime(nowSeconds, nameof(nowSeconds));
        if (applyAgeDecay &&
            (!float.IsFinite(halfLifeSeconds) || halfLifeSeconds <= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(halfLifeSeconds));
        }

        Span<PerceptionSense> senses = stackalloc PerceptionSense[SensoryChannelCount];
        Span<float> strongestBySense = stackalloc float[SensoryChannelCount];
        int senseCount = 0;

        for (int node = _evidenceHead[contactSlot]; node >= 0; node = _evidenceNext[node])
        {
            PerceptionObservation observation = _evidenceValues[node];
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

        float remainingUncertainty = 1f;
        for (int i = 0; i < senseCount; i++)
            remainingUncertainty *= 1f - Math.Clamp(strongestBySense[i], 0f, 1f);

        return Math.Clamp(1f - remainingUncertainty, 0f, 1f);
    }

    private void EnsureObserverIndex(int observerIndex)
    {
        if (observerIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(observerIndex));
        if (observerIndex >= _observerGeneration.Length)
            EnsureObserverCapacity(observerIndex + 1);
    }

    private void EnsureContactCapacity(int required)
    {
        if (required <= _active.Length)
            return;

        int oldCapacity = _active.Length;
        int capacity = Math.Max(required, oldCapacity * 2);
        Array.Resize(ref _activeSlots, capacity);
        Array.Resize(ref _activeIndex, capacity);
        Array.Resize(ref _active, capacity);
        Array.Resize(ref _generation, capacity);
        Array.Resize(ref _freeContactNext, capacity);
        Array.Resize(ref _observerIndex, capacity);
        Array.Resize(ref _previousForObserver, capacity);
        Array.Resize(ref _nextForObserver, capacity);
        Array.Resize(ref _contactId, capacity);
        Array.Resize(ref _kind, capacity);
        Array.Resize(ref _subjectUnit, capacity);
        Array.Resize(ref _reporterUnit, capacity);
        Array.Resize(ref _estimatedPosition, capacity);
        Array.Resize(ref _positionErrorRadius, capacity);
        Array.Resize(ref _lastEventAt, capacity);
        Array.Resize(ref _lastReceivedAt, capacity);
        Array.Resize(ref _lastVisualAt, capacity);
        Array.Resize(ref _expiresAt, capacity);
        Array.Resize(ref _lastStimulusType, capacity);
        Array.Resize(ref _isInvestigated, capacity);
        Array.Resize(ref _visualLossMarked, capacity);
        Array.Resize(ref _evidenceHead, capacity);
        Array.Resize(ref _evidenceTail, capacity);
        Array.Resize(ref _evidenceCount, capacity);

        for (int i = oldCapacity; i < capacity; i++)
        {
            _activeIndex[i] = -1;
            _observerIndex[i] = -1;
            _previousForObserver[i] = -1;
            _nextForObserver[i] = -1;
            _evidenceHead[i] = -1;
            _evidenceTail[i] = -1;
            _lastVisualAt[i] = -1f;
        }
    }

    private void EnsureEvidenceCapacity(int required)
    {
        if (required <= _evidenceValues.Length)
            return;

        int capacity = Math.Max(required, _evidenceValues.Length * 2);
        Array.Resize(ref _evidenceValues, capacity);
        Array.Resize(ref _evidenceNext, capacity);
    }

    private bool IsActiveSlot(int slot) =>
        (uint)slot < (uint)_slotCount && _active[slot];

    private static void ValidateTime(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
