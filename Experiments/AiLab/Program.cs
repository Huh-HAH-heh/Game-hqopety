using System.Text.Json;
using Core.Unit;

namespace RimClone.AiLab;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            Console.WriteLine("[AI-LAB] Stop requested; cancelling at the next episode/tick checkpoint.");
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (args.Any(x => string.Equals(x, "--help", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(x, "-h", StringComparison.OrdinalIgnoreCase)))
            {
                PrintHelp();
                return 0;
            }

            LabConfiguration configuration = Parse(args);
            configuration.Validate();
            var trainer = new LabTrainer();
            HoldoutReport report = await trainer.RunAsync(configuration, cancellation.Token);
            return report.ImprovesFitness ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[AI-LAB] Cancelled. Completed history and checkpoints remain on disk.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[AI-LAB] {exception.Message}");
            Console.Error.WriteLine("Use --help to see supported options.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static LabConfiguration Parse(string[] args)
    {
        string objective = Text(args, "--objective", "combat").ToLowerInvariant();
        LabExperimentKind kind = objective switch
        {
            "combat" => LabExperimentKind.CombatBalance,
            "hide" or "remain-undetected" or "stealth" => LabExperimentKind.RemainUndetected,
            "break-contact" or "hide-after-seen" => LabExperimentKind.BreakContact,
            "weapon" or "weapon-specialization" => LabExperimentKind.WeaponSpecialization,
            "squad" or "doctrine" => LabExperimentKind.SquadDoctrine,
            _ => throw new ArgumentException($"Unknown objective '{objective}'.")
        };

        string weaponText = Text(args, "--weapon", "rifle").ToLowerInvariant();
        LabWeaponPreset weapon = weaponText switch
        {
            "rifle" or "assault-rifle" => LabWeaponPreset.AssaultRifle,
            "pistol" => LabWeaponPreset.Pistol,
            _ => throw new ArgumentException($"Unknown weapon '{weaponText}'. Use rifle or pistol.")
        };

        AiBalanceProfile baseline = AiBalanceProfile.Default;
        string? profilePath = OptionalText(args, "--seed-profile");
        if (profilePath != null)
        {
            string path = Path.GetFullPath(profilePath);
            baseline = JsonSerializer.Deserialize<AiBalanceProfile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Could not load AiBalanceProfile from '{path}'.");
        }

        return new LabConfiguration
        {
            Experiment = kind,
            BaselineProfile = baseline,
            Weapon = weapon,
            PopulationSize = Int(args, "--population", 16, 4, 128),
            Generations = Int(args, "--generations", 10, 1, 10000),
            RepeatsPerScenario = Int(args, "--repeats", 1, 1, 100),
            TeamSize = Int(args, "--team-size", 4, 2, 16),
            SimulationTicks = Int(args, "--ticks", 600, 100, 100000),
            WorkerCount = Int(args, "--workers", Math.Clamp(Environment.ProcessorCount - 2, 1, 8), 1, 64),
            Seed = Int(args, "--seed", 20261025, int.MinValue, int.MaxValue),
            OutputPath = Text(args, "--output", $"Experiments/AiLab/output/{kind.ToString().ToLowerInvariant()}-best.json")
        };
    }

    private static int Int(string[] args, string key, int fallback, int min, int max)
    {
        string? raw = OptionalText(args, key);
        if (raw == null)
            return fallback;
        if (!int.TryParse(raw, out int value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(key, $"Expected {min}..{max}.");
        return value;
    }

    private static string Text(string[] args, string key, string fallback) =>
        OptionalText(args, key) ?? fallback;

    private static string? OptionalText(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            return args[i + 1];
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
RimClone AI Lab
  dotnet run --project Experiments/AiLab/AiLab.csproj -- [options]

  --objective combat|hide|break-contact|weapon|squad
  --weapon rifle|pistol           Candidate weapon for weapon specialization
  --population 16                 Candidates per generation (4..128)
  --generations 10                 Number of generations
  --repeats 1                     Paired side-swapped repeats per scenario
  --team-size 4                   Units per faction (2..16)
  --ticks 600                     Maximum 0.05s simulation updates per episode
  --workers N                     Parallel isolated simulations; default is CPU count minus two, capped at 8
  --seed N                        Reproducible training seed
  --seed-profile path.json        Continue tuning an existing AiBalanceProfile
  --output path.json              Best directly loadable AiBalanceProfile

Objectives:
  combat         Broad tactical-balance optimization.
  hide           Minimize visual exposure under enemy perception.
  break-contact  Break visual contact after being detected.
  weapon         Tune tactics for an assault rifle or pistol.
  squad          Tune a shared profile for a coordinated squad.

Training and holdout use different seed sets. The holdout report is written
beside the profile; results are never auto-applied to the running game.
""");
    }
}
