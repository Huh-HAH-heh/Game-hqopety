using Core.Unit;

namespace RimClone.AiLab;

public enum LabExperimentKind : byte
{
    CombatBalance,
    RemainUndetected,
    BreakContact,
    WeaponSpecialization,
    SquadDoctrine
}

public enum LabScenarioKind : byte
{
    OpenField,
    Barrier,
    CloseStart,
    BrokenGround
}

public sealed record LabExperimentDefinition(
    LabExperimentKind Kind,
    string Id,
    string DisplayName,
    string Goal,
    LabScenarioKind[] Scenarios,
    int[] ActiveGenes)
{
    public static LabExperimentDefinition Get(LabExperimentKind kind) => kind switch
    {
        LabExperimentKind.CombatBalance => new(
            kind, "combat", "Combat balance",
            "Win balanced matches without sacrificing survivability or combat effectiveness.",
            [LabScenarioKind.OpenField, LabScenarioKind.Barrier, LabScenarioKind.CloseStart, LabScenarioKind.BrokenGround],
            [0, 1, 2, 3, 4, 5, 6]),
        LabExperimentKind.RemainUndetected => new(
            kind, "remain-undetected", "Remain undetected",
            "Minimize real visual detections by enemy perception while keeping the candidate team alive.",
            [LabScenarioKind.Barrier, LabScenarioKind.BrokenGround, LabScenarioKind.OpenField],
            [0, 2, 3, 4]),
        LabExperimentKind.BreakContact => new(
            kind, "break-contact", "Break visual contact",
            "After an enemy has visually detected a unit, break visual contact and survive.",
            [LabScenarioKind.Barrier, LabScenarioKind.CloseStart, LabScenarioKind.BrokenGround],
            [0, 2, 3, 4]),
        LabExperimentKind.WeaponSpecialization => new(
            kind, "weapon-specialization", "Weapon specialization",
            "Find a useful tactical profile for a selected weapon while measuring casualties and ammunition use.",
            [LabScenarioKind.OpenField, LabScenarioKind.Barrier, LabScenarioKind.CloseStart, LabScenarioKind.BrokenGround],
            [0, 1, 2, 3, 5]),
        LabExperimentKind.SquadDoctrine => new(
            kind, "squad-doctrine", "Squad-level doctrine",
            "Evaluate one shared candidate doctrine across a coordinated squad, including delayed squad contact sharing.",
            [LabScenarioKind.OpenField, LabScenarioKind.Barrier, LabScenarioKind.CloseStart, LabScenarioKind.BrokenGround],
            [0, 1, 2, 3, 5, 6]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public enum LabWeaponPreset : byte
{
    StandardMix,
    AssaultRifle,
    Pistol
}

public sealed record LabConfiguration
{
    public LabExperimentKind Experiment { get; init; } = LabExperimentKind.CombatBalance;
    public AiBalanceProfile BaselineProfile { get; init; } = AiBalanceProfile.Default;
    public LabWeaponPreset Weapon { get; init; } = LabWeaponPreset.AssaultRifle;
    public int PopulationSize { get; init; } = 16;
    public int Generations { get; init; } = 10;
    public int RepeatsPerScenario { get; init; } = 1;
    public int TeamSize { get; init; } = 4;
    public int SimulationTicks { get; init; } = 600;
    public int WorkerCount { get; init; } = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
    public int Seed { get; init; } = 20261025;
    public string OutputPath { get; init; } = "Experiments/AiLab/output/ai-lab-best.json";

    public void Validate()
    {
        if (PopulationSize < 4 || PopulationSize > 128)
            throw new ArgumentOutOfRangeException(nameof(PopulationSize), "Population must be 4..128.");
        if (Generations < 1 || Generations > 10000)
            throw new ArgumentOutOfRangeException(nameof(Generations), "Generations must be 1..10000.");
        if (RepeatsPerScenario < 1 || RepeatsPerScenario > 100)
            throw new ArgumentOutOfRangeException(nameof(RepeatsPerScenario), "Repeats per scenario must be 1..100.");
        if (TeamSize < 2 || TeamSize > 16)
            throw new ArgumentOutOfRangeException(nameof(TeamSize), "Team size must be 2..16.");
        if (SimulationTicks < 100 || SimulationTicks > 100000)
            throw new ArgumentOutOfRangeException(nameof(SimulationTicks), "Simulation ticks must be 100..100000.");
        if (WorkerCount < 1 || WorkerCount > 64)
            throw new ArgumentOutOfRangeException(nameof(WorkerCount), "Workers must be 1..64.");
        if (!Enum.IsDefined(Experiment))
            throw new ArgumentOutOfRangeException(nameof(Experiment));
        if (!Enum.IsDefined(Weapon))
            throw new ArgumentOutOfRangeException(nameof(Weapon));
        if (string.IsNullOrWhiteSpace(OutputPath))
            throw new ArgumentException("Output path cannot be empty.", nameof(OutputPath));
    }
}

public sealed record EpisodeMetrics(
    LabScenarioKind Scenario,
    int Seed,
    bool CandidateStartedLeft,
    double Fitness,
    int Outcome,
    double CandidateSurvivorRate,
    double CandidateHealthRatio,
    double EnemyHealthRatio,
    double DetectionRate,
    double VisualExposureRatio,
    double BreakContactRate,
    double MeanBreakContactSeconds,
    double Activity,
    double ActionDiversity,
    int CandidateDeaths,
    int EnemyDeaths,
    long ShotsFired,
    long RoundsConsumed,
    double SimulatedSeconds);

public sealed record CandidateEvaluation(
    int Generation,
    int CandidateIndex,
    TacticalGenome Genome,
    double Fitness,
    double WinRate,
    double SurvivorRate,
    double OwnHealthRatio,
    double EnemyHealthRatio,
    double DetectionRate,
    double VisualExposureRatio,
    double BreakContactRate,
    double MeanBreakContactSeconds,
    double Activity,
    double ActionDiversity,
    EpisodeMetrics[] Episodes);

public sealed record GenerationSummary(
    int Generation,
    double BestFitness,
    double MeanFitness,
    double WorstFitness,
    double WinRate,
    double SurvivorRate,
    int CandidateCount);

public sealed record HoldoutReport(
    int TrainingSeed,
    int HoldoutSeed,
    LabExperimentKind Experiment,
    CandidateEvaluation Baseline,
    CandidateEvaluation Trained,
    double FitnessDelta,
    double WinRateDelta,
    double SurvivorRateDelta,
    bool ImprovesFitness,
    bool ImprovesWinRate);
