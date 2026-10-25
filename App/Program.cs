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

            const string profilePrefix = "--ai-balance-profile=";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(profilePrefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    string profilePath = args[i].Substring(profilePrefix.Length);
                    System.Environment.SetEnvironmentVariable("RIMCLONE_AI_BALANCE_PROFILE", profilePath);
                    System.Console.WriteLine($"[AI] Balance profile requested: {profilePath}");
                }
                else if (string.Equals(args[i], "--ai-balance-profile", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        System.Console.WriteLine("[AI] Missing path after --ai-balance-profile.");
                        continue;
                    }

                    string profilePath = args[++i];
                    System.Environment.SetEnvironmentVariable("RIMCLONE_AI_BALANCE_PROFILE", profilePath);
                    System.Console.WriteLine($"[AI] Balance profile requested: {profilePath}");
                }
            }

            const string stealthProfilePrefix = "--ai-stealth-profile=";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith(stealthProfilePrefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    string profilePath = args[i].Substring(stealthProfilePrefix.Length);
                    System.Environment.SetEnvironmentVariable("RIMCLONE_AI_STEALTH_PROFILE", profilePath);
                    System.Console.WriteLine($"[AI-STEALTH] Profile requested: {profilePath}");
                }
                else if (string.Equals(args[i], "--ai-stealth-profile", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        System.Console.WriteLine("[AI-STEALTH] Missing path after --ai-stealth-profile.");
                        continue;
                    }

                    string profilePath = args[++i];
                    System.Environment.SetEnvironmentVariable("RIMCLONE_AI_STEALTH_PROFILE", profilePath);
                    System.Console.WriteLine($"[AI-STEALTH] Profile requested: {profilePath}");
                }
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

            System.Environment.SetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI", "0");
            System.Console.WriteLine(
                "[AI] Policy=Utility AI with shared perception, tactical movement, ranged/melee combat and " +
                "contact memory. Behaviac decision runtime remains disabled.");

            GameBootstrap.Run();
        }
    }
}