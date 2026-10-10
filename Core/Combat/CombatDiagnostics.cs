using System;
using System.Diagnostics;

namespace Core.Combat;

public static class CombatDiagnostics
{
    private const int MaxDetailedEventsPerSecond = 28;

    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _eventsInWindow;
    private static int _suppressedEvents;

    public static bool Enabled { get; set; }

    public static void WriteLine(string message)
    {
        if (!Enabled)
            return;

        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_windowStart, now).TotalSeconds >= 1d)
        {
            if (_suppressedEvents > 0)
            {
                Console.WriteLine(
                    $"[COMBAT] suppressed {_suppressedEvents:N0} extra detail events in the previous second");
            }

            _windowStart = now;
            _eventsInWindow = 0;
            _suppressedEvents = 0;
        }

        if (_eventsInWindow >= MaxDetailedEventsPerSecond)
        {
            _suppressedEvents++;
            return;
        }

        _eventsInWindow++;
        Console.WriteLine(message);
    }
}
