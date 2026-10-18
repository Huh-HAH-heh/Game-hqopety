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

            if (Array.Exists(args, arg => string.Equals(arg, "--ai=legacy", System.StringComparison.OrdinalIgnoreCase)))
                System.Environment.SetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI", "0");
            else if (Array.Exists(args, arg => string.Equals(arg, "--ai=behaviac", System.StringComparison.OrdinalIgnoreCase)))
                System.Environment.SetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI", "1");

            bool behaviacEnabled = !string.Equals(
                System.Environment.GetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI"),
                "0",
                System.StringComparison.Ordinal);
            System.Console.WriteLine(
                $"[AI] Policy={(behaviacEnabled ? "BEHAVIAC XML runtime" : "LEGACY C# path")}; " +
                "override with --ai=behaviac or --ai=legacy.");

            GameBootstrap.Run();
        }
    }
}