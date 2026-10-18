using RimClone.App;

namespace RimClone
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                // Simulation regression tests keep the legacy policy stable;
                // interactive runs use Behaviac by default on this experiment branch.
                System.Environment.SetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI", "0");
                System.Environment.ExitCode =
                    Core.Unit.CombatSelfTest.Run() ? 0 : 1;
                return;
            }

            GameBootstrap.Run();
        }
    }
}