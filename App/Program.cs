using RimClone.App;

namespace RimClone
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                System.Environment.ExitCode =
                    Core.Unit.CombatSelfTest.Run() ? 0 : 1;
                return;
            }

            GameBootstrap.Run();
        }
    }
}