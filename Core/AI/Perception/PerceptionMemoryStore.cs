using System;

namespace Core.AI.Perception;

/// <summary>
/// Shared per-observer memory for every perception channel.
/// Sensors submit uncertain evidence here; they do not own separate knowledge stores.
/// </summary>
public sealed class PerceptionMemoryStore
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
        if (nowSeconds > CurrentTimeSeconds)
            AdvanceTime(nowSeconds);

        return GetOrCreateMemory(observerIndex)
            .Record(stimulus, nowSeconds, memoryLifetimeSeconds);
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
