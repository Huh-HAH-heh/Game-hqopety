using RimClone.App;

namespace RimClone
{
    class Program
    {
        static void Main(string[] args)
        {
            if (Array.Exists(args, arg => string.Equals(arg, "--self-test", System.StringComparison.Ordinal)))
            {
                // Simulation regression tests keep the legacy policy stable.
                System.Environment.SetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI", "0");
                System.Environment.ExitCode =
                    Core.Unit.CombatSelfTest.Run() ? 0 : 1;
                return;
            }

            const string navDebugPrefix = "--ai-nav-debug=";
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith(navDebugPrefix, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                string value = args[i].Substring(navDebugPrefix.Length);
                if (int.TryParse(value, out int traceUnitIndex) && traceUnitIndex >= 0)
                {
                    System.Environment.SetEnvironmentVariable(
                        "RIMCLONE_AI_TRACE_UNIT",
                        traceUnitIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    System.Console.WriteLine(
                        $"[AI-NAV] Tracing unit index {traceUnitIndex} once per second.");
                }
                else
                {
                    System.Console.WriteLine(
                        $"[AI-NAV] Invalid unit index in '{args[i]}'. Use --ai-nav-debug=805.");
                }
            }

            bool useLegacyAi = Array.Exists(
                args,
                arg => string.Equals(arg, "--ai=legacy", System.StringComparison.OrdinalIgnoreCase));

            // Ignore stale PowerShell/IDE environment values during ordinary game launches.
            // Behaviac is the default; legacy AI is available only as an explicit comparison.
            System.Environment.SetEnvironmentVariable(
                "RIMCLONE_USE_BEHAVIAC_AI",
                useLegacyAi ? "0" : "1");

            System.Console.WriteLine(
                $"[AI] Policy={(useLegacyAi ? "LEGACY C# path" : "BEHAVIAC XML runtime")}; " +
                $"Override: --ai={(useLegacyAi ? "behaviac" : "legacy")} for comparison.");

            GameBootstrap.Run();
        }
    }
}