using System;
using System.Diagnostics;

namespace Core.AI.Perception;

/// <summary>
/// A plug-in perception channel. Implementations own detection rules and publish
/// observer-specific observations through PerceptionSenseContext.
/// </summary>
public interface IPerceptionSense
{
    /// <summary>Stable, unique registration key (for example: "visual" or "thermal").</summary>
    string Id { get; }
    /// <summary>The information channel this module produces.</summary>
    PerceptionSense Channel { get; }
    bool Enabled { get; set; }
    /// <summary>Time spent in the most recent update, in milliseconds.</summary>
    double LastUpdateMilliseconds { get; }
    void Update(in PerceptionSenseContext context);
    void InitializeUnit(int unitIndex);
    void ClearUnit(int unitIndex);
}

/// <summary>
/// Default implementation for sensors. The registry can work with any
/// IPerceptionSense, while most new sensors only need to implement UpdateCore.
/// </summary>
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
