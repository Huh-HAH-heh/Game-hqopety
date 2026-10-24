using System;
using System.Diagnostics;

namespace Core.AI.Perception;

public interface IPerceptionSense
{
    string Id { get; }
    PerceptionSense Channel { get; }
    bool Enabled { get; set; }
    double LastUpdateMilliseconds { get; }
    void Update(in PerceptionSenseContext context);
    void InitializeUnit(int unitIndex);
    void ClearUnit(int unitIndex);
}

public abstract class PerceptionSenseBase : IPerceptionSense
{
    public abstract string Id { get; }
    public abstract PerceptionSense Channel { get; }
    public virtual bool Enabled { get; set; } = true;
    public double LastUpdateMilliseconds { get; private set; }

    public void Update(in PerceptionSenseContext context)
    {
        if (!Enabled)
        {
            LastUpdateMilliseconds = 0d;
            return;
        }

        long started = Stopwatch.GetTimestamp();
        try { UpdateCore(in context); }
        finally { LastUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    protected abstract void UpdateCore(in PerceptionSenseContext context);
    public virtual void InitializeUnit(int unitIndex) { }
    public virtual void ClearUnit(int unitIndex) { }
}
