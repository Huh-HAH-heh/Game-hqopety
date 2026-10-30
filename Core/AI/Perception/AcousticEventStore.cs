using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// Owns pending acoustic events and their spatial/temporal batches.
/// Source identities stay inside this acoustic layer; listeners receive only
/// independently estimated anonymous observations.
/// </summary>
internal sealed class AcousticEventStore
{
    internal const float GunshotBatchWindowSeconds = 0.05f;
    internal const float FootstepBatchWindowSeconds = 0.15f;
    internal const float GunshotMergeCellSize = 24f;
    internal const float FootstepMergeCellSize = 16f;

    private const int BufferTrimThreshold = 4096;
    private const int RetainedCapacity = 128;
    private const float TrimQuietSeconds = 2f;

    private readonly List<AcousticEvent> _pendingEvents = new();
    private readonly Dictionary<AcousticBatchKey, AcousticBatch> _pendingBatches = new(128);
    private readonly List<AcousticBatchKey> _batchKeysToFlush = new(128);
    private long _nextEventId = 1;
    private float _lastEventAtSeconds = float.NegativeInfinity;

    public int PendingEventCount => _pendingEvents.Count;
    public int PendingBatchCount => _pendingBatches.Count;
    public IReadOnlyList<AcousticEvent> PendingEvents => _pendingEvents;

    public long NextEventId() => _nextEventId++;

    public bool WillBatch(in AcousticEvent sound) =>
        sound.SourceFaction != 0 &&
        sound.Type is TacticalStimulusType.Gunshot or TacticalStimulusType.Footstep;

    public void Queue(in AcousticEvent sound)
    {
        _lastEventAtSeconds = sound.OccurredAtSeconds;
        if (!WillBatch(sound))
        {
            _pendingEvents.Add(sound);
            return;
        }

        bool gunshot = sound.Type == TacticalStimulusType.Gunshot;
        float cellSize = gunshot ? GunshotMergeCellSize : FootstepMergeCellSize;
        float batchWindow = gunshot ? GunshotBatchWindowSeconds : FootstepBatchWindowSeconds;
        long timeBucket = (long)MathF.Floor(sound.OccurredAtSeconds / batchWindow);
        var key = new AcousticBatchKey(
            (int)MathF.Floor(sound.Position.X / cellSize),
            (int)MathF.Floor(sound.Position.Y / cellSize),
            sound.SourceFaction,
            sound.Type,
            timeBucket);

        if (_pendingBatches.TryGetValue(key, out AcousticBatch batch))
        {
            AcousticEvent existing = batch.Sound;
            Vector3 positionSum = batch.PositionSum + sound.Position;
            int count = batch.Count + 1;
            AcousticEvent merged = new(
                existing.EventId,
                existing.SourceUnitId,
                existing.SourceFaction,
                positionSum / count,
                existing.Type,
                MathF.Max(existing.OccurredAtSeconds, sound.OccurredAtSeconds),
                MathF.Max(existing.Intensity, sound.Intensity),
                MathF.Max(existing.Range, sound.Range),
                count);
            _pendingBatches[key] = new AcousticBatch(merged, positionSum, count);
        }
        else
        {
            _pendingBatches.Add(key, new AcousticBatch(sound, sound.Position, 1));
        }
    }

    /// <summary>Moves matured batches into the dispatch list and returns how many were added.</summary>
    public int FlushMaturedBatches(float nowSeconds)
    {
        if (_pendingBatches.Count == 0)
            return 0;

        _batchKeysToFlush.Clear();
        foreach (KeyValuePair<AcousticBatchKey, AcousticBatch> entry in _pendingBatches)
        {
            float batchWindow = entry.Key.Type == TacticalStimulusType.Gunshot
                ? GunshotBatchWindowSeconds
                : FootstepBatchWindowSeconds;
            double bucketEnd = (entry.Key.TimeBucket + 1d) * batchWindow;
            if (bucketEnd <= nowSeconds + 0.0001d)
                _batchKeysToFlush.Add(entry.Key);
        }

        int initialCount = _pendingEvents.Count;
        for (int i = 0; i < _batchKeysToFlush.Count; i++)
        {
            AcousticBatchKey key = _batchKeysToFlush[i];
            if (!_pendingBatches.TryGetValue(key, out AcousticBatch batch))
                continue;

            _pendingEvents.Add(batch.Sound);
            _pendingBatches.Remove(key);
        }

        return _pendingEvents.Count - initialCount;
    }

    public void ClearDispatchedEvents() => _pendingEvents.Clear();

    public void TrimBuffersIfQuiet(float nowSeconds)
    {
        if (_pendingEvents.Count != 0 ||
            _pendingBatches.Count != 0 ||
            nowSeconds - _lastEventAtSeconds < TrimQuietSeconds)
            return;

        if (_pendingEvents.Capacity > BufferTrimThreshold)
            _pendingEvents.Capacity = RetainedCapacity;

        if (_pendingBatches.EnsureCapacity(0) > BufferTrimThreshold)
        {
            _pendingBatches.TrimExcess();
            _pendingBatches.EnsureCapacity(RetainedCapacity);
        }

        if (_batchKeysToFlush.Capacity > BufferTrimThreshold)
            _batchKeysToFlush.Capacity = RetainedCapacity;
    }

    private readonly record struct AcousticBatchKey(
        int X, int Y, ushort Faction, TacticalStimulusType Type, long TimeBucket);

    private readonly record struct AcousticBatch(
        AcousticEvent Sound, Vector3 PositionSum, int Count);
}

/// <summary>
/// One shared acoustic event. SourceUnitId is authoritative only inside the
/// acoustic system and is never copied into an anonymous listener contact.
/// </summary>
internal readonly record struct AcousticEvent(
    long EventId,
    UnitId SourceUnitId,
    ushort SourceFaction,
    Vector3 Position,
    TacticalStimulusType Type,
    float OccurredAtSeconds,
    float Intensity,
    float Range,
    int EventCount = 1);
