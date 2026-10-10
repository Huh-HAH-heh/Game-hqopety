using System;
using System.Diagnostics;

namespace Core.Combat;

public static class CombatDiagnostics
{
    private const int MaxShotEventsPerSecond = 5;
    private const int MaxHitEventsPerSecond = 18;
    private const int MaxBlockedEventsPerSecond = 10;
    private const int MaxOtherEventsPerSecond = 4;

    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _shotsLogged;
    private static int _hitsLogged;
    private static int _blockedLogged;
    private static int _otherLogged;
    private static int _shotsSuppressed;
    private static int _hitsSuppressed;
    private static int _blockedSuppressed;
    private static int _otherSuppressed;

    public static bool Enabled { get; set; }

    public static void WriteLine(string message)
    {
        if (!Enabled)
            return;

        ResetWindowIfNeeded();

        if (message.StartsWith("[SHOT]", StringComparison.Ordinal))
        {
            WriteLimited(message, MaxShotEventsPerSecond,
                ref _shotsLogged, ref _shotsSuppressed);
        }
        else if (message.StartsWith("[HIT]", StringComparison.Ordinal))
        {
            WriteLimited(message, MaxHitEventsPerSecond,
                ref _hitsLogged, ref _hitsSuppressed);
        }
        else if (message.StartsWith("[BLOCKED]", StringComparison.Ordinal))
        {
            WriteLimited(message, MaxBlockedEventsPerSecond,
                ref _blockedLogged, ref _blockedSuppressed);
        }
        else
        {
            WriteLimited(message, MaxOtherEventsPerSecond,
                ref _otherLogged, ref _otherSuppressed);
        }
    }

    private static void ResetWindowIfNeeded()
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_windowStart, now).TotalSeconds < 1d)
            return;

        if (_shotsSuppressed + _hitsSuppressed +
            _blockedSuppressed + _otherSuppressed > 0)
        {
            Console.WriteLine(
                $"[COMBAT] suppressed previous second: " +
                $"SHOT={_shotsSuppressed:N0} " +
                $"HIT={_hitsSuppressed:N0} " +
                $"BLOCKED={_blockedSuppressed:N0} " +
                $"OTHER={_otherSuppressed:N0}");
        }

        _windowStart = now;
        _shotsLogged = 0;
        _hitsLogged = 0;
        _blockedLogged = 0;
        _otherLogged = 0;
        _shotsSuppressed = 0;
        _hitsSuppressed = 0;
        _blockedSuppressed = 0;
        _otherSuppressed = 0;
    }

    private static void WriteLimited(
        string message,
        int limit,
        ref int logged,
        ref int suppressed)
    {
        if (logged >= limit)
        {
            suppressed++;
            return;
        }

        logged++;
        Console.WriteLine(message);
    }
}
