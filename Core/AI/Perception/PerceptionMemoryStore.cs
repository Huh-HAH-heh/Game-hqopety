using System;

namespace Core.AI.Perception;

/// <summary>
/// Owns the shared perception clock and one data-oriented contact/evidence pool.
/// Per-observer memories are value views, not separately allocated objects.
/// </summary>
public sealed class PerceptionMemoryStore : IPerceptionObservationSink
{
    private bool[] _hasMemory;
    private readonly PerceptionContactStorage _contacts;

    public float CurrentTimeSeconds => _contacts.CurrentTimeSeconds;
    public int Capacity => _hasMemory.Length;

    public PerceptionMemoryStore(int initialUnitCapacity = 1024)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialUnitCapacity));

        _hasMemory = new bool[initialUnitCapacity];
        _contacts = new PerceptionContactStorage(initialUnitCapacity);
    }

    public void AdvanceTime(float nowSeconds) =>
        _contacts.AdvanceTime(nowSeconds);

    public bool TryGetMemory(int observerIndex, out TacticalContactMemory memory)
    {
        if ((uint)observerIndex < (uint)_hasMemory.Length &&
            _hasMemory[observerIndex])
        {
            memory = CreateView(observerIndex);
            return true;
        }

        memory = default;
        return false;
    }

    public TacticalContactMemory GetOrCreateMemory(int observerIndex)
    {
        if (observerIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(observerIndex));

        EnsureCapacity(observerIndex + 1);
        _hasMemory[observerIndex] = true;
        return CreateView(observerIndex);
    }

    /// <summary>
    /// Compatibility bridge for event producers. All observations enter the same
    /// observer-scoped contact pool regardless of their source sensor.
    /// </summary>
    public TacticalContact? Record(
        int observerIndex,
        TacticalStimulus stimulus,
        float nowSeconds,
        float memoryLifetimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(stimulus);
        if (observerIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(observerIndex));

        EnsureCapacity(observerIndex + 1);
        PerceptionObservation observation = PerceptionObservation.FromStimulus(
            stimulus, nowSeconds, memoryLifetimeSeconds);
        TacticalContact? contact = _contacts.Record(observerIndex, observation);
        _hasMemory[observerIndex] = true;
        return contact;
    }

    public TacticalContact? RecordObservation(
        int observerIndex,
        PerceptionObservation observation,
        long? correlatedContactId = null)
    {
        if (observerIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(observerIndex));

        EnsureCapacity(observerIndex + 1);
        TacticalContact? contact = _contacts.Record(observerIndex, observation, correlatedContactId);
        _hasMemory[observerIndex] = true;
        return contact;
    }

    public void InitializeUnit(int unitIndex)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));

        EnsureCapacity(unitIndex + 1);
        _contacts.ClearObserver(unitIndex);
        _hasMemory[unitIndex] = false;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_hasMemory.Length)
            return;

        _contacts.ClearObserver(unitIndex);
        _hasMemory[unitIndex] = false;
    }

    public void EnsureCapacity(int required)
    {
        if (required <= 0)
            throw new ArgumentOutOfRangeException(nameof(required));
        if (required <= _hasMemory.Length)
            return;

        int capacity = Math.Max(required, _hasMemory.Length * 2);
        Array.Resize(ref _hasMemory, capacity);
        _contacts.EnsureObserverCapacity(capacity);
    }

    private TacticalContactMemory CreateView(int observerIndex) =>
        new(
            _contacts,
            observerIndex,
            _contacts.GetObserverGeneration(observerIndex));
}
