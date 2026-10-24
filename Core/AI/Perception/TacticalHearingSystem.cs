using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;
using Core.Unit;

namespace Core.AI.Perception;

public readonly record struct TacticalSoundDebugEvent(
    long Sequence, UnitId SourceUnit, Vector3 Position, TacticalStimulusType Type,
    float OccurredAtSeconds, float Intensity, float AudibleRange);

/// <summary>
/// Converts loud events and locomotion into uncertain per-observer contacts.
/// The observer never receives the emitter ID from an audio-only contact.
/// </summary>
public sealed class TacticalHearingSystem : PerceptionSenseBase
{
    public override string Id => "auditory";
    public override PerceptionSense Channel => PerceptionSense.Auditory;
    private const float GunshotRange = 180f;
    private const float GunshotMemorySeconds = 8f;
    private const float GunshotMemoryPublishIntervalSeconds = 0.15f;
    private const float GunshotMergeCellSize = 8f;
    private const float FootstepMergeCellSize = 4f;
    private const float FootstepMemorySeconds = 3f;
    private const float ConfidenceHalfLifeSeconds = 2.5f;
    private const float MinimumContactConfidence = 0.12f;
    private const float DebugEventLifetimeSeconds = 2.25f;
    private const int MaxRecentLoudDebugEvents = 96;
    private const int MaxRecentFootstepDebugEvents = 48;
    private const int ListenerGridCellSize = 32;
    private const int PulseBufferTrimThreshold = 4096;
    private const int PulseBufferRetainedCapacity = 128;
    private const float PulseBufferTrimQuietSeconds = 2f;

    private readonly List<SoundPulse> _pendingPulses = new();
    private readonly Dictionary<AcousticCellKey, AcousticBatch> _pendingAcousticBatches = new(128);
    private SoundPulse[] _bestGunshotPulseByObserver = Array.Empty<SoundPulse>();
    private Vector3[] _bestGunshotEstimateByObserver = Array.Empty<Vector3>();
    private float[] _bestGunshotConfidenceByObserver = Array.Empty<float>();
    private float[] _bestGunshotErrorByObserver = Array.Empty<float>();
    private bool[] _hasBestGunshotByObserver = Array.Empty<bool>();
    private float[] _lastGunshotMemoryPublishedAt = Array.Empty<float>();
    private bool[] _hasPublishedGunshotMemory = Array.Empty<bool>();
    private int _pendingGunshotEvents;
    private int _pendingGunshotBatches;
    private int _pendingGunshotsMerged;
    private readonly Queue<TacticalSoundDebugEvent> _recentLoudDebugEvents = new();
    private readonly Queue<TacticalSoundDebugEvent> _recentFootstepDebugEvents = new();
    private readonly PerceptionMemoryStore _memoryStore;
    private float[] _footstepCooldown;
    private int _listenerGridWidth;
    private int _listenerGridHeight;
    private int[] _listenerCellCounts = Array.Empty<int>();
    private int[] _listenerCellStarts = Array.Empty<int>();
    private int[] _listenerCellWriteCursors = Array.Empty<int>();
    private int[] _listenerUnits = Array.Empty<int>();
    private long _nextPulseId = 1;
    private float _lastPulseAtSeconds = float.NegativeInfinity;

    public float CurrentTimeSeconds => _memoryStore.CurrentTimeSeconds;
    public int LastQueuedPulseCount { get; private set; }
    public int LastListenerChecks { get; private set; }
    public int LastListenerCellsVisited { get; private set; }
    public int LastContactsRecorded { get; private set; }
    public int LastGunshotEventsQueued { get; private set; }
    public int LastGunshotBatchesQueued { get; private set; }
    public int LastGunshotsMerged { get; private set; }

    public TacticalHearingSystem(
        int initialUnitCapacity = 1024,
        PerceptionMemoryStore? memoryStore = null)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialUnitCapacity));

        _memoryStore = memoryStore ?? new PerceptionMemoryStore(initialUnitCapacity);
        _memoryStore.EnsureCapacity(initialUnitCapacity);
        _footstepCooldown = new float[initialUnitCapacity];
    }

    public void EmitGunshot(UnitId shooter, Vector3 position, ushort factionTag) =>
        EmitDanger(shooter, factionTag, TacticalStimulusType.Gunshot, position, 1f, GunshotRange);

    public void EmitDanger(
        UnitId sourceUnit,
        ushort sourceFaction,
        TacticalStimulusType type,
        Vector3 position,
        float intensity,
        float audibleRange)
    {
        if (!IsFinite(position) ||
            !float.IsFinite(intensity) ||
            !float.IsFinite(audibleRange) ||
            intensity <= 0f ||
            audibleRange <= 0f ||
            type is not (TacticalStimulusType.Gunshot or TacticalStimulusType.NearMiss or
                TacticalStimulusType.CoverImpact or TacticalStimulusType.Explosion))
            return;

        QueuePulse(new SoundPulse(
            _nextPulseId++, sourceUnit, sourceFaction, position, type,
            CurrentTimeSeconds, Math.Clamp(intensity, 0f, 1f), audibleRange));
    }

    /// <summary>
    /// Entry point for future blast producers. The current weapon model does not
    /// yet implement explosive ammunition or radius-based blast damage.
    /// </summary>
    public void EmitExplosion(
        Vector3 position,
        ushort sourceFaction = 0,
        float intensity = 1f,
        float audibleRange = 80f) =>
        EmitDanger(default, sourceFaction, TacticalStimulusType.Explosion,
            position, intensity, audibleRange);

    public TacticalSoundDebugEvent[] GetRecentDebugEvents(float nowSeconds)
    {
        if (!float.IsFinite(nowSeconds) || nowSeconds < 0f)
            return Array.Empty<TacticalSoundDebugEvent>();

        PruneDebugEvents(_recentLoudDebugEvents, nowSeconds);
        PruneDebugEvents(_recentFootstepDebugEvents, nowSeconds);
        TacticalSoundDebugEvent[] events = new TacticalSoundDebugEvent[
            _recentLoudDebugEvents.Count + _recentFootstepDebugEvents.Count];
        _recentLoudDebugEvents.CopyTo(events, 0);
        _recentFootstepDebugEvents.CopyTo(events, _recentLoudDebugEvents.Count);
        return events;
    }

    public void Update(
        UnitStore units,
        UnitHealthStore health,
        UnitSuppressionStore response,
        float nowSeconds,
        float deltaTime,
        WorldMap? worldMap = null,
        VisionSystem? vision = null) =>
        UpdateInternal(units, health, response, nowSeconds, deltaTime, worldMap, vision,
            advanceMemoryTime: true);

    protected override void UpdateCore(in PerceptionSenseContext context) =>
        UpdateInternal(context.Units, context.Health, context.Suppression,
            context.SimulationTimeSeconds, context.DeltaTime, context.WorldMap, context.Vision,
            advanceMemoryTime: false);

    private void UpdateInternal(
        UnitStore units,
        UnitHealthStore health,
        UnitSuppressionStore response,
        float nowSeconds,
        float deltaTime,
        WorldMap? worldMap,
        VisionSystem? vision,
        bool advanceMemoryTime)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(health);
        if (!float.IsFinite(nowSeconds) || nowSeconds < CurrentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        EnsureCapacity(units.Capacity);
        if (advanceMemoryTime)
            _memoryStore.AdvanceTime(nowSeconds);
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            return;
        CollectFootsteps(units, health, deltaTime, nowSeconds);
        LastQueuedPulseCount = _pendingPulses.Count;
        LastListenerChecks = 0;
        LastContactsRecorded = 0;
        LastGunshotEventsQueued = _pendingGunshotEvents;
        LastGunshotBatchesQueued = _pendingGunshotBatches;
        LastGunshotsMerged = _pendingGunshotsMerged;

        DeliverPulses(units, health, response, nowSeconds, worldMap, vision);
        _pendingPulses.Clear();
        _pendingAcousticBatches.Clear();
        _pendingGunshotEvents = 0;
        _pendingGunshotBatches = 0;
        _pendingGunshotsMerged = 0;
        TrimTransientBuffersIfQuiet(nowSeconds);
    }

    public void InitializeUnit(int unitIndex)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        EnsureCapacity(unitIndex + 1);
        _footstepCooldown[unitIndex] = 0f;
        _hasBestGunshotByObserver[unitIndex] = false;
        _lastGunshotMemoryPublishedAt[unitIndex] = 0f;
        _hasPublishedGunshotMemory[unitIndex] = false;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex < (uint)_footstepCooldown.Length)
            _footstepCooldown[unitIndex] = 0f;

        if ((uint)unitIndex < (uint)_hasBestGunshotByObserver.Length)
        {
            _hasBestGunshotByObserver[unitIndex] = false;
            _lastGunshotMemoryPublishedAt[unitIndex] = 0f;
            _hasPublishedGunshotMemory[unitIndex] = false;
        }
    }

    public bool HasInvestigableContact(int observerIndex) =>
        TryGetBestInvestigableContact(observerIndex, out _);

    public bool TryGetBestInvestigableContact(int observerIndex, out TacticalContact contact)
    {
        contact = null!;
        if (!_memoryStore.TryGetMemory(observerIndex, out TacticalContactMemory memory))
            return false;
        float bestScore = 0f;
        IReadOnlyList<TacticalContact> contacts = memory.Contacts;
        for (int i = 0; i < contacts.Count; i++)
        {
            TacticalContact candidate = contacts[i];
            if (candidate.IsInvestigated ||
                candidate.ExpiresAtSeconds <= CurrentTimeSeconds ||
                candidate.LastStimulusType is not (TacticalStimulusType.Gunshot or TacticalStimulusType.Footstep))
                continue;
            float confidence = candidate.GetEffectiveConfidence(CurrentTimeSeconds, ConfidenceHalfLifeSeconds);
            float minimum = candidate.LastStimulusType == TacticalStimulusType.Footstep ? 0.18f : 0.16f;
            if (confidence < minimum)
                continue;
            float score = confidence +
                (candidate.LastStimulusType == TacticalStimulusType.Gunshot ? 0.12f : 0f);
            if (score <= bestScore)
                continue;
            bestScore = score;
            contact = candidate;
        }
        return bestScore > 0f;
    }

    public bool HasImmediateDangerContact(int observerIndex) =>
        TryGetBestImmediateDangerContact(observerIndex, out _);

    public bool TryGetBestImmediateDangerContact(int observerIndex, out TacticalContact contact)
    {
        contact = null!;
        if (!_memoryStore.TryGetMemory(observerIndex, out TacticalContactMemory memory))
            return false;

        float bestScore = 0f;
        IReadOnlyList<TacticalContact> contacts = memory.Contacts;
        for (int i = 0; i < contacts.Count; i++)
        {
            TacticalContact candidate = contacts[i];
            if (candidate.IsInvestigated ||
                candidate.ExpiresAtSeconds <= CurrentTimeSeconds ||
                candidate.LastStimulusType is not (TacticalStimulusType.NearMiss or
                    TacticalStimulusType.CoverImpact or TacticalStimulusType.Explosion))
                continue;

            float confidence = candidate.GetEffectiveConfidence(CurrentTimeSeconds, ConfidenceHalfLifeSeconds);
            if (confidence < 0.14f || confidence <= bestScore)
                continue;
            bestScore = confidence;
            contact = candidate;
        }
        return bestScore > 0f;
    }

    public bool MarkInvestigated(int observerIndex, long contactId)
    {
        if (!_memoryStore.TryGetMemory(observerIndex, out TacticalContactMemory memory))
            return false;
        return memory.MarkInvestigated(contactId);
    }

    private void CollectFootsteps(UnitStore units, UnitHealthStore health, float deltaTime, float nowSeconds)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            _footstepCooldown[unit] = MathF.Max(0f, _footstepCooldown[unit] - deltaTime);
            if (health.OverallHitPoints[unit] <= 0f)
            {
                _footstepCooldown[unit] = 0f;
                continue;
            }
            Vector3 velocity = units.Velocity[unit];
            if (velocity.X * velocity.X + velocity.Y * velocity.Y < 0.36f ||
                _footstepCooldown[unit] > 0f)
                continue;
            UnitPosture posture = units.Posture[unit];
            float intensity, range, interval;
            switch (posture)
            {
                case UnitPosture.Lying: intensity = 0.22f; range = 9f; interval = 0.90f; break;
                case UnitPosture.Crouching: intensity = 0.36f; range = 14f; interval = 0.70f; break;
                case UnitPosture.Sitting: intensity = 0.28f; range = 11f; interval = 0.80f; break;
                default: intensity = 0.55f; range = 20f; interval = 0.50f; break;
            }
            QueuePulse(new SoundPulse(
                _nextPulseId++, units.GetId(unit), units.FactionTag[unit],
                units.Position[unit], TacticalStimulusType.Footstep, nowSeconds, intensity, range));
            _footstepCooldown[unit] = interval;
        }
    }

    private void DeliverPulses(
        UnitStore units,
        UnitHealthStore health,
        UnitSuppressionStore response,
        float nowSeconds,
        WorldMap? worldMap,
        VisionSystem? vision)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        Array.Clear(_hasBestGunshotByObserver);
        LastListenerCellsVisited = 0;

        bool useListenerGrid = worldMap != null && _pendingPulses.Count > 0;
        if (useListenerGrid)
            BuildListenerGrid(units, health, worldMap! , active);

        for (int pulseIndex = 0; pulseIndex < _pendingPulses.Count; pulseIndex++)
        {
            SoundPulse pulse = _pendingPulses[pulseIndex];
            float lifetime = pulse.Type switch
            {
                TacticalStimulusType.Footstep => FootstepMemorySeconds,
                TacticalStimulusType.Gunshot => GunshotMemorySeconds,
                TacticalStimulusType.NearMiss => 5f,
                TacticalStimulusType.CoverImpact => 6f,
                TacticalStimulusType.Explosion => 10f,
                _ => 4f
            };
            if (nowSeconds - pulse.OccurredAtSeconds >= lifetime)
                continue;

            if (!useListenerGrid)
            {
                for (int i = 0; i < active.Length; i++)
                {
                    int observer = active[i];
                    LastListenerChecks++;
                    DeliverPulseToObserver(
                        pulse, lifetime, observer, units, health, response,
                        nowSeconds, worldMap, vision);
                }

                continue;
            }

            float range = pulse.Range;
            int minCellX = ClampListenerCell(pulse.Position.X - range, _listenerGridWidth);
            int maxCellX = ClampListenerCell(pulse.Position.X + range, _listenerGridWidth);
            int minCellY = ClampListenerCell(pulse.Position.Y - range, _listenerGridHeight);
            int maxCellY = ClampListenerCell(pulse.Position.Y + range, _listenerGridHeight);

            for (int cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                int rowOffset = cellY * _listenerGridWidth;
                for (int cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    int cell = rowOffset + cellX;
                    LastListenerCellsVisited++;
                    int start = _listenerCellStarts[cell];
                    int count = _listenerCellCounts[cell];
                    int end = start + count;
                    for (int listener = start; listener < end; listener++)
                    {
                        int observer = _listenerUnits[listener];
                        LastListenerChecks++;
                        DeliverPulseToObserver(
                            pulse, lifetime, observer, units, health, response,
                            nowSeconds, worldMap, vision);
                    }
                }
            }
        }

        // Persist gunfire as a sensory update, not once per bullet received by
        // every listener. Fast bursts still raise alertness for all heard clusters.
        for (int i = 0; i < active.Length; i++)
        {
            int observer = active[i];
            if (!_hasBestGunshotByObserver[observer])
                continue;

            float lastPublished = _lastGunshotMemoryPublishedAt[observer];
            if (_hasPublishedGunshotMemory[observer] &&
                nowSeconds - lastPublished < GunshotMemoryPublishIntervalSeconds)
                continue;

            SoundPulse pulse = _bestGunshotPulseByObserver[observer];
            Vector3 estimate = _bestGunshotEstimateByObserver[observer];
            if (_memoryStore.Record(
                    observer,
                    new TacticalStimulus(
                        pulse.Type, estimate, pulse.OccurredAtSeconds,
                        pulse.Intensity, _bestGunshotErrorByObserver[observer],
                        _bestGunshotConfidenceByObserver[observer]),
                    nowSeconds,
                    GunshotMemorySeconds) != null)
            {
                LastContactsRecorded++;
            }

            _lastGunshotMemoryPublishedAt[observer] = nowSeconds;
            _hasPublishedGunshotMemory[observer] = true;
        }
    }

    private void DeliverPulseToObserver(
        SoundPulse pulse,
        float lifetime,
        int observer,
        UnitStore units,
        UnitHealthStore health,
        UnitSuppressionStore response,
        float nowSeconds,
        WorldMap? worldMap,
        VisionSystem? vision)
    {
        if (health.OverallHitPoints[observer] <= 0f ||
            units.GetId(observer).Equals(pulse.SourceUnit))
        {
            return;
        }

        ushort observerFaction = units.FactionTag[observer];
        if (pulse.SourceFaction != 0 && pulse.SourceFaction == observerFaction)
            return;

        Vector3 observerPosition = units.Position[observer];
        float dx = pulse.Position.X - observerPosition.X;
        float dy = pulse.Position.Y - observerPosition.Y;
        float distanceSquared = dx * dx + dy * dy;
        if (distanceSquared > pulse.Range * pulse.Range)
            return;

        float distance = MathF.Sqrt(distanceSquared);
        float attenuation = Math.Clamp(1f - distance / pulse.Range, 0f, 1f);
        float confidence = pulse.Intensity * (0.25f + 0.75f * attenuation);
        if (confidence < MinimumContactConfidence)
            return;

        float error = pulse.Type switch
        {
            TacticalStimulusType.Footstep =>
                MathF.Max(1.5f + distance * 0.20f, FootstepMergeCellSize * 0.75f),
            TacticalStimulusType.Gunshot =>
                MathF.Max(4f + distance * 0.22f, GunshotMergeCellSize * 0.75f),
            _ => 4f + distance * 0.22f
        };
        Vector3 estimate = EstimatePosition(pulse, observer, error, attenuation);

        float alertFactor = pulse.Type switch
        {
            TacticalStimulusType.Gunshot => 0.42f,
            TacticalStimulusType.Footstep => 0.24f,
            TacticalStimulusType.NearMiss => 0.55f,
            TacticalStimulusType.CoverImpact => 0.36f,
            TacticalStimulusType.Explosion => 0.70f,
            _ => 0.18f
        };
        response.AddAlertness(observer, alertFactor * confidence, estimate);

        if (pulse.Type == TacticalStimulusType.Gunshot)
        {
            // Hearing still affects alertness for every cluster, but we keep
            // only the strongest uncertain gunshot contact per observer/tick.
            if (!_hasBestGunshotByObserver[observer] ||
                confidence >= _bestGunshotConfidenceByObserver[observer])
            {
                _hasBestGunshotByObserver[observer] = true;
                _bestGunshotPulseByObserver[observer] = pulse;
                _bestGunshotEstimateByObserver[observer] = estimate;
                _bestGunshotConfidenceByObserver[observer] = confidence;
                _bestGunshotErrorByObserver[observer] = error;
            }

            return;
        }

        if (_memoryStore.Record(
                observer,
                new TacticalStimulus(
                    pulse.Type, estimate, pulse.OccurredAtSeconds,
                    pulse.Intensity, error, confidence),
                nowSeconds,
                lifetime) != null)
        {
            LastContactsRecorded++;
        }

        ApplyImmediateDangerResponse(
            response, units, vision, worldMap, observer, pulse.Type,
            pulse.Position, pulse.Range, confidence, estimate);
    }

    private void BuildListenerGrid(
        UnitStore units,
        UnitHealthStore health,
        WorldMap worldMap,
        ReadOnlySpan<int> active)
    {
        int width = Math.Max(1, (worldMap.TileWidth + ListenerGridCellSize - 1) / ListenerGridCellSize);
        int height = Math.Max(1, (worldMap.TileHeight + ListenerGridCellSize - 1) / ListenerGridCellSize);
        if (width != _listenerGridWidth || height != _listenerGridHeight)
        {
            int cellCount = checked(width * height);
            _listenerCellCounts = new int[cellCount];
            _listenerCellStarts = new int[cellCount];
            _listenerCellWriteCursors = new int[cellCount];
            _listenerGridWidth = width;
            _listenerGridHeight = height;
        }
        else
        {
            Array.Clear(_listenerCellCounts, 0, _listenerCellCounts.Length);
        }

        int listenerCount = 0;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (health.OverallHitPoints[unit] <= 0f)
                continue;

            _listenerCellCounts[GetListenerCellIndex(units.Position[unit])]++;
            listenerCount++;
        }

        int offset = 0;
        for (int cell = 0; cell < _listenerCellCounts.Length; cell++)
        {
            _listenerCellStarts[cell] = offset;
            _listenerCellWriteCursors[cell] = offset;
            offset += _listenerCellCounts[cell];
        }

        if (_listenerUnits.Length < listenerCount)
            Array.Resize(ref _listenerUnits, Math.Max(listenerCount, Math.Max(64, _listenerUnits.Length * 2)));

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (health.OverallHitPoints[unit] <= 0f)
                continue;

            int cell = GetListenerCellIndex(units.Position[unit]);
            _listenerUnits[_listenerCellWriteCursors[cell]++] = unit;
        }
    }

    private int GetListenerCellIndex(Vector3 position)
    {
        int x = ClampListenerCell(position.X, _listenerGridWidth);
        int y = ClampListenerCell(position.Y, _listenerGridHeight);
        return y * _listenerGridWidth + x;
    }

    private static int ClampListenerCell(float worldPosition, int cellCount) =>
        Math.Clamp((int)MathF.Floor(worldPosition / ListenerGridCellSize), 0, cellCount - 1);

    private static void ApplyImmediateDangerResponse(
        UnitSuppressionStore response,
        UnitStore units,
        VisionSystem? vision,
        WorldMap? worldMap,
        int observer,
        TacticalStimulusType type,
        Vector3 sourcePosition,
        float eventRange,
        float confidence,
        Vector3 estimatedSource)
    {
        float dangerRadius;
        float minimumConfidence;
        float duration;

        switch (type)
        {
            case TacticalStimulusType.NearMiss:
                dangerRadius = 5f;
                minimumConfidence = 0.20f;
                duration = 0.25f + 0.25f * confidence;
                break;
            case TacticalStimulusType.CoverImpact:
                dangerRadius = 8f;
                minimumConfidence = 0.24f;
                duration = 0.16f + 0.30f * confidence;
                break;
            case TacticalStimulusType.Explosion:
                dangerRadius = Math.Clamp(eventRange * 0.30f, 10f, 24f);
                minimumConfidence = 0.18f;
                duration = 0.45f + 0.85f * confidence;
                break;
            default:
                return;
        }

        // Sound can travel vertically, but danger radius is physical 3D proximity.
        float dangerDistance = Vector3.Distance(units.Position[observer], sourcePosition);
        if (!float.IsFinite(dangerDistance) ||
            dangerDistance > dangerRadius ||
            confidence < minimumConfidence)
            return;

        bool hasDirectExposure = true;
        if ((type is TacticalStimulusType.CoverImpact or TacticalStimulusType.Explosion) &&
            vision != null && worldMap != null)
        {
            Vector3 eyePosition = vision.GetEyePosition(units, observer);
            Vector3 exposurePoint = sourcePosition;

            // Terrain impacts are reported at the material entry boundary.
            // Check the observer-side point just before that boundary, not a
            // point that is already inside the wall that stopped the projectile.
            if (type == TacticalStimulusType.CoverImpact)
            {
                Vector3 towardObserver = eyePosition - sourcePosition;
                float distanceSquared = towardObserver.LengthSquared();
                if (distanceSquared > 0.000001f)
                {
                    float backoff = MathF.Min(0.05f, MathF.Sqrt(distanceSquared) * 0.5f);
                    exposurePoint += towardObserver * (backoff / MathF.Sqrt(distanceSquared));
                }
            }

            hasDirectExposure = vision.HasLineOfSight(
                worldMap, eyePosition, exposurePoint, out _);
        }

        // A wall that stops a round protects units behind it from the impact.
        // The report still raises alertness, but does not force a cover reaction.
        if (type == TacticalStimulusType.CoverImpact && !hasDirectExposure)
            return;

        // A blast can still be heard behind terrain, but its immediate pressure is lower.
        if (type == TacticalStimulusType.Explosion && !hasDirectExposure)
            duration *= 0.25f;

        // Suppression resistance reduces flinch-driven reactions to close rounds
        // and nearby impacts; it does not make a true blast physically harmless.
        if (type is TacticalStimulusType.NearMiss or TacticalStimulusType.CoverImpact)
            duration *= 1f - response.GetSuppressionResistance(observer) * 0.45f;

        // Existing cover status further reduces urgency once a protected position is reached.
        if ((uint)observer < (uint)response.InCover.Length && response.InCover[observer])
            duration *= 0.60f;

        if (duration < 0.16f)
            return;

        response.MarkImmediateDanger(observer, duration, estimatedSource);
    }

    private static Vector3 EstimatePosition(SoundPulse pulse, int observer, float error, float attenuation)
    {
        ulong seed = unchecked(
            (ulong)pulse.Sequence * 6364136223846793005UL +
            (ulong)(observer + 1) * 1442695040888963407UL +
            (ulong)(pulse.SourceUnit.Index + 1) * 22695477UL);
        float angle = (seed % 628319UL) / 100000f;
        float offset = error * (0.15f + (1f - attenuation) * 0.45f);
        return new Vector3(
            pulse.Position.X + MathF.Cos(angle) * offset,
            pulse.Position.Y + MathF.Sin(angle) * offset,
            pulse.Position.Z);
    }

    private void QueuePulse(SoundPulse pulse)
    {
        _lastPulseAtSeconds = pulse.OccurredAtSeconds;
        bool appendPulse = true;
        bool gunshot = pulse.Type == TacticalStimulusType.Gunshot;
        bool batchable = gunshot || pulse.Type == TacticalStimulusType.Footstep;
        if (gunshot)
            _pendingGunshotEvents++;

        if (batchable && pulse.SourceFaction != 0)
        {
            float cellSize = gunshot ? GunshotMergeCellSize : FootstepMergeCellSize;
            var key = new AcousticCellKey(
                (int)MathF.Floor(pulse.Position.X / cellSize),
                (int)MathF.Floor(pulse.Position.Y / cellSize),
                pulse.SourceFaction,
                pulse.Type);

            if (_pendingAcousticBatches.TryGetValue(key, out AcousticBatch batch))
            {
                SoundPulse existing = _pendingPulses[batch.PulseIndex];
                Vector3 positionSum = batch.PositionSum + pulse.Position;
                int count = batch.Count + 1;
                _pendingPulses[batch.PulseIndex] = new SoundPulse(
                    existing.Sequence,
                    existing.SourceUnit,
                    existing.SourceFaction,
                    positionSum / count,
                    existing.Type,
                    MathF.Max(existing.OccurredAtSeconds, pulse.OccurredAtSeconds),
                    MathF.Max(existing.Intensity, pulse.Intensity),
                    MathF.Max(existing.Range, pulse.Range));
                _pendingAcousticBatches[key] = new AcousticBatch(batch.PulseIndex, positionSum, count);
                if (gunshot)
                    _pendingGunshotsMerged++;
                appendPulse = false;
            }
            else
            {
                _pendingAcousticBatches.Add(
                    key,
                    new AcousticBatch(_pendingPulses.Count, pulse.Position, 1));
                if (gunshot)
                    _pendingGunshotBatches++;
            }
        }

        if (!appendPulse)
            return;

        if (gunshot && pulse.SourceFaction == 0)
            _pendingGunshotBatches++;

        _pendingPulses.Add(pulse);
        Queue<TacticalSoundDebugEvent> history = pulse.Type == TacticalStimulusType.Footstep
            ? _recentFootstepDebugEvents
            : _recentLoudDebugEvents;
        int limit = pulse.Type == TacticalStimulusType.Footstep
            ? MaxRecentFootstepDebugEvents
            : MaxRecentLoudDebugEvents;
        while (history.Count >= limit)
            history.Dequeue();
        history.Enqueue(new TacticalSoundDebugEvent(
            pulse.Sequence, pulse.SourceUnit, pulse.Position, pulse.Type,
            pulse.OccurredAtSeconds, pulse.Intensity, pulse.Range));
    }

    private static void PruneDebugEvents(
        Queue<TacticalSoundDebugEvent> events,
        float nowSeconds)
    {
        while (events.Count > 0 &&
               nowSeconds - events.Peek().OccurredAtSeconds > DebugEventLifetimeSeconds)
            events.Dequeue();
    }

    private void TrimTransientBuffersIfQuiet(float nowSeconds)
    {
        if (_pendingPulses.Count != 0 ||
            _pendingAcousticBatches.Count != 0 ||
            nowSeconds - _lastPulseAtSeconds < PulseBufferTrimQuietSeconds)
        {
            return;
        }

        // Keep normal small buffers warm, but release the high-water allocation after
        // an unusually large battle burst has been quiet for a while. Events are never
        // dropped to satisfy a cap; this only affects empty collection capacity.
        if (_pendingPulses.Capacity > PulseBufferTrimThreshold)
            _pendingPulses.Capacity = PulseBufferRetainedCapacity;

        if (_pendingAcousticBatches.EnsureCapacity(0) > PulseBufferTrimThreshold)
        {
            _pendingAcousticBatches.TrimExcess();
            _pendingAcousticBatches.EnsureCapacity(PulseBufferRetainedCapacity);
        }
    }

    private void EnsureCapacity(int required)
    {
        _memoryStore.EnsureCapacity(required);
        if (_footstepCooldown.Length < required)
            Array.Resize(ref _footstepCooldown, Math.Max(required, _footstepCooldown.Length * 2));
        if (_bestGunshotPulseByObserver.Length < required)
        {
            int capacity = Math.Max(required, _bestGunshotPulseByObserver.Length * 2);
            Array.Resize(ref _bestGunshotPulseByObserver, capacity);
            Array.Resize(ref _bestGunshotEstimateByObserver, capacity);
            Array.Resize(ref _bestGunshotConfidenceByObserver, capacity);
            Array.Resize(ref _bestGunshotErrorByObserver, capacity);
            Array.Resize(ref _hasBestGunshotByObserver, capacity);
            Array.Resize(ref _lastGunshotMemoryPublishedAt, capacity);
            Array.Resize(ref _hasPublishedGunshotMemory, capacity);
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct AcousticCellKey(
        int X, int Y, ushort Faction, TacticalStimulusType Type);
    private readonly record struct AcousticBatch(int PulseIndex, Vector3 PositionSum, int Count);

    private readonly record struct SoundPulse(
        long Sequence, UnitId SourceUnit, ushort SourceFaction, Vector3 Position,
        TacticalStimulusType Type, float OccurredAtSeconds, float Intensity, float Range);
}
