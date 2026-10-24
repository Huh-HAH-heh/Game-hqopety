using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Core.AI.Perception;

public sealed class PerceptionSystem
{
    private readonly PerceptionMemoryStore _memory;
    private readonly List<IPerceptionSense> _senses = new();
    private readonly Dictionary<string, IPerceptionSense> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    public int SenseCount => _senses.Count;
    public int EnabledSenseCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _senses.Count; i++)
                if (_senses[i].Enabled) count++;
            return count;
        }
    }

    public double LastUpdateMilliseconds { get; private set; }

    public PerceptionSystem(PerceptionMemoryStore memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        _memory = memory;
    }

    public void Register(IPerceptionSense sense)
    {
        ArgumentNullException.ThrowIfNull(sense);
        if (string.IsNullOrWhiteSpace(sense.Id))
            throw new ArgumentException("A sense must provide a stable non-empty ID.", nameof(sense));
        if (!Enum.IsDefined(sense.Channel) || sense.Channel == PerceptionSense.Unknown)
            throw new ArgumentException("A sense must provide a concrete perception channel.", nameof(sense));
        if (!_byId.TryAdd(sense.Id, sense))
            throw new InvalidOperationException($"A perception sense with ID '{sense.Id}' is already registered.");
        _senses.Add(sense);
    }

    public bool TryGetSense(string id, out IPerceptionSense sense)
    {
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out IPerceptionSense? found))
        {
            sense = found;
            return true;
        }
        sense = null!;
        return false;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        if (!TryGetSense(id, out IPerceptionSense sense))
            return false;
        sense.Enabled = enabled;
        return true;
    }

    public void Update(in PerceptionSenseContext context)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!ReferenceEquals(context.ObservationSink, _memory))
                throw new InvalidOperationException("All senses must publish into this registry's shared memory store.");
            _memory.AdvanceTime(context.SimulationTimeSeconds);
            for (int i = 0; i < _senses.Count; i++)
                _senses[i].Update(in context);
        }
        finally
        {
            LastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    public void InitializeUnit(int unitIndex)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        _memory.InitializeUnit(unitIndex);
        for (int i = 0; i < _senses.Count; i++)
            _senses[i].InitializeUnit(unitIndex);
    }

    public void ClearUnit(int unitIndex)
    {
        if (unitIndex < 0)
            return;
        for (int i = 0; i < _senses.Count; i++)
            _senses[i].ClearUnit(unitIndex);
        _memory.ClearUnit(unitIndex);
    }
}
