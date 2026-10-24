using System;

namespace Core.AI.Perception;

/// <summary>
/// Shared per-observer memory for every perception channel.
/// Sensors publish observations here; no sensor or AI system owns a parallel contact list.
/// </summary>
public sealed class PerceptionMemoryStore : IPerceptionObservationSink
{
    private TacticalContactMemory?[] _memories;

    public float CurrentTimeSeconds { get; private set; }
    public int Capacity => _memories.Length;

    public PerceptionMemoryStore(int initialUnitCapacity = 1024)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialUnitCapacity));
        _memories = new TacticalContactMemory?[initialUnitCapacity];
    }

    public void AdvanceTime(float nowSeconds)
    {
        ValidateTime(nowSeconds);
        if (nowSeconds < CurrentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds),
                "Perception memory time cannot move backwards.");
        if (nowSeconds == CurrentTimeSeconds)
            return;

        CurrentTimeSeconds = nowSeconds;
        for (int i = 0; i < _memories.Length; i++)
            _memories[i]?.AdvanceTime(nowSeconds);
    }

    public bool TryGetMemory(int observerIndex, out TacticalContactMemory memory)
    {
        if ((uint)observerIndex < (uint)_memories.Length &&
            _memories[observerIndex] is TacticalContactMemory found)
        {
            memory = found;
            return true;
        }

        memory = null!;
        return false;
    }

    public TacticalContactMemory GetOrCreateMemory(int observerIndex)
    {
        if (observerIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(observerIndex));
        EnsureCapacity(observerIndex + 1);
        return _memories[observerIndex] ??= new TacticalContactMemory();
    }

    /// <summary>
    /// Compatibility bridge for existing event producers. The common store keeps
    /// only PerceptionObservation objects, irrespective of the producer type.
    /// </summary>
    public TacticalContact? Record(
        int observerIndex,
        TacticalStimulus stimulus,
        float nowSeconds,
        float memoryLifetimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(stimulus);
        ValidateTime(nowSeconds);
        if (nowSeconds < CurrentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds),
                "Perception memory time cannot move backwards.");

        return RecordObservation(
            observerIndex,
            PerceptionObservation.FromStimulus(stimulus, nowSeconds, memoryLifetimeSeconds));
    }

    public TacticalContact? RecordObservation(
        int observerIndex,
        PerceptionObservation observation,
        long? correlatedContactId = null)
    {
        if (observerIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(observerIndex));

        float receivedAtSeconds = observation.ReceivedAtSeconds;
        ValidateTime(receivedAtSeconds);
        if (receivedAtSeconds < CurrentTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(observation),
                "Observation receipt time cannot be older than the shared perception clock.");
        if (receivedAtSeconds > CurrentTimeSeconds)
            AdvanceTime(receivedAtSeconds);

        return GetOrCreateMemory(observerIndex).Record(observation, correlatedContactId);
    }

    public void InitializeUnit(int unitIndex)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        EnsureCapacity(unitIndex + 1);
        _memories[unitIndex]?.Clear();
        _memories[unitIndex] = null;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_memories.Length)
            return;

        _memories[unitIndex]?.Clear();
        _memories[unitIndex] = null;
    }

    public void EnsureCapacity(int required)
    {
        if (required <= 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required <= _memories.Length)
            return;

        int capacity = Math.Max(required, _memories.Length * 2);
        Array.Resize(ref _memories, capacity);
    }

    private static void ValidateTime(float nowSeconds)
    {
        if (!float.IsFinite(nowSeconds) || nowSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
    }
}
