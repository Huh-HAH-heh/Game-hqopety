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