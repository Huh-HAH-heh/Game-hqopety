using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Core.Map;
using Core.Unit;

namespace RimClone.Render;

public sealed record AiEvolutionStatus(
    bool HasStarted,
    bool IsRunning,
    int Generation,
    int CandidateIndex,
    int PopulationSize,
    int RoundIndex,
    int RoundCount,
    ushort CandidateFaction,
    float EpisodeElapsedSeconds,
    float EpisodeDurationSeconds,
    int BlueAlive,
    int BlueTotal,
    float BlueHealthRatio,
    int RedAlive,
    int RedTotal,
    float RedHealthRatio,
    double CurrentRoundFitness,
    double CandidateMeanFitness,
    double BestFitness,
    string GeneSummary,
    string Message,
    string BestProfilePath);

/// <summary>
/// Visible, online evolution for the existing mass-combat scene. Each candidate fights
/// the current baseline twice, once on each side, then selection/crossover/mutation
/// produces the next generation. The ordinary simulation and renderer remain active.
/// </summary>
public sealed class LiveAiEvolutionTrainer
{
    private const int GeneCount = 40;
    private const int PopulationSize = 4;
    private const int RoundsPerCandidate = 2;
    private const float EpisodeDurationSeconds = 30f;

    private static readonly float[] Min =
    [
        .65f,.55f,.65f,.70f,.35f,.50f,.50f,.50f,.50f,.50f,
        .45f,.03f,.04f,.12f,.12f,.35f,12f,.08f,.15f,.60f,
        .20f,.60f,.25f,.01f,.80f,.10f,4f,4f,5f,4f,
        5f,.75f,.80f,.04f,.04f,.50f,.30f,.02f,.003f,.20f
    ];

    private static readonly float[] Max =
    [
        1.35f,1.45f,1.45f,1.40f,1.25f,1.50f,1.50f,1.50f,1.50f,1.50f,
        .85f,.15f,.18f,.34f,.34f,.85f,40f,.40f,.50f,.95f,
        .60f,.95f,3f,.08f,1f,.45f,14f,14f,20f,24f,
        30f,.98f,.999f,.30f,.35f,.95f,.80f,.16f,.04f,1.50f
    ];

    private static readonly float[] DefaultGenes =
    [
        1f,1f,1f,1f,1f,1f,1f,1f,1f,1f,
        .60f,.08f,.10f,.23f,.24f,.66f,24f,.22f,.35f,.84f,
        .35f,.84f,1f,.03f,.94f,.20f,8f,4f,6f,6f,
        8f,.90f,.98f,.12f,.16f,.80f,.60f,.08f,.012f,.90f
    ];

    private readonly Random _random = new(
        unchecked(Environment.TickCount * 397 ^ DateTime.UtcNow.Millisecond));

    private readonly List<float[]> _population = new(PopulationSize);
    private readonly List<CandidateResult> _scores = new(PopulationSize);
    private readonly double[] _roundFitness = new double[RoundsPerCandidate];

    private AiBalanceProfile _baseProfile = AiBalanceProfile.Default;
    private AiBalanceProfile _bestProfile = AiBalanceProfile.Default;
    private float[] _bestGenes = (float[])DefaultGenes.Clone();
    private double _bestFitness;
    private int _generation;
    private int _candidateIndex;
    private int _roundIndex;
    private float _episodeElapsedSeconds;
    private float _statusElapsedSeconds;
    private double _roundFitnessSum;
    private double _latestRoundFitness = double.NaN;
    private bool _running;
    private bool _hasStarted;
    private string _message = "Нажми F5, чтобы начать эволюционное обучение.";
    private string _outputDirectory = string.Empty;
    private string _bestProfilePath = string.Empty;
    private string _historyPath = string.Empty;
    private AiEvolutionStatus? _status;

    public AiEvolutionStatus? Status => Volatile.Read(ref _status);

    public void Toggle(UnitSimulation simulation, LongRangeCombatTestScene scene, WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(worldMap);

        if (_running)
        {
            StopAndApplyBest(simulation, scene, worldMap);
            return;
        }

        Start(simulation, scene, worldMap);
    }

    public void Update(
        UnitSimulation simulation,
        LongRangeCombatTestScene scene,
        WorldMap worldMap,
        float deltaTime)
    {
        if (!_running || !float.IsFinite(deltaTime) || deltaTime <= 0f)
            return;

        _episodeElapsedSeconds += deltaTime;
        _statusElapsedSeconds += deltaTime;

        if (scene.AliveBlue <= 0 || scene.AliveRed <= 0 ||
            _episodeElapsedSeconds >= EpisodeDurationSeconds)
        {
            CompleteRound(simulation, scene, worldMap);
            return;
        }

        if (_statusElapsedSeconds >= 0.25f)
        {
            _statusElapsedSeconds = 0f;
            PublishStatus(simulation);
        }
    }

    private void Start(UnitSimulation simulation, LongRangeCombatTestScene scene, WorldMap worldMap)
    {
        _baseProfile = simulation.AI.BalanceProfile ?? AiBalanceProfile.Default;
        _bestProfile = _baseProfile;
        _bestGenes = GenesFrom(_baseProfile);
        _bestFitness = 0d;
        _generation = 1;
        _candidateIndex = 0;
        _roundIndex = 0;
        _roundFitnessSum = 0d;
        _latestRoundFitness = double.NaN;
        _episodeElapsedSeconds = 0f;
        _statusElapsedSeconds = 0f;
        _scores.Clear();
        _population.Clear();
        _population.Add(GenesFrom(_baseProfile));

        float[] seedGenes = GenesFrom(_baseProfile);
        while (_population.Count < PopulationSize)
            _population.Add(Mutate(seedGenes, _random, .75d, .22f));

        ConfigureOutputPaths();
        _hasStarted = true;
        _running = true;
        _message = "Идёт бой кандидата против базового профиля.";

        try
        {
            Directory.CreateDirectory(_outputDirectory);
            File.WriteAllText(
                _bestProfilePath,
                JsonSerializer.Serialize(_bestProfile, new JsonSerializerOptions { WriteIndented = true }));
            if (!File.Exists(_historyPath))
            {
                File.WriteAllText(
                    _historyPath,
                    "generation,candidate,fitness,best_fitness,blue_survivors,red_survivors,blue_health,red_health,engage,flank,cover,retreat,prone,suppressive_fire\n");
            }
        }
        catch (Exception exception)
        {
            _message = $"Обучение идёт, но файл результата недоступен: {exception.Message}";
            Console.WriteLine($"[EVOLVE LIVE] Could not initialize output files: {exception.Message}");
        }

        Console.WriteLine(
            $"[EVOLVE LIVE] Started. Population={PopulationSize}; episode={EpisodeDurationSeconds:0}s per side; " +
            $"checkpoint={_bestProfilePath}");
        StartEpisode(simulation, scene, worldMap);
    }

    private void StopAndApplyBest(
        UnitSimulation simulation,
        LongRangeCombatTestScene scene,
        WorldMap worldMap)
    {
        _running = false;
        bool applyBest = _bestFitness > 0d;
        AiBalanceProfile selected = applyBest ? _bestProfile : _baseProfile;
        simulation.AI.BalanceProfile = selected;
        simulation.AI.SetBalanceProfileForFaction(1, null);
        simulation.AI.SetBalanceProfileForFaction(2, null);
        scene.Start(simulation, worldMap);

        _message = applyBest
            ? "Обучение остановлено: лучший найденный профиль применён и сохранён."
            : "Обучение остановлено: улучшение над базой пока не подтверждено, оставлена база.";
        Console.WriteLine($"[EVOLVE LIVE] {_message}");
        PublishStatus(simulation);
    }

    private void StartEpisode(
        UnitSimulation simulation,
        LongRangeCombatTestScene scene,
        WorldMap worldMap)
    {
        float[] genes = _population[_candidateIndex];
        AiBalanceProfile candidate = Profile(genes, _baseProfile);
        ushort candidateFaction = _roundIndex == 0 ? (ushort)1 : (ushort)2;

        simulation.AI.BalanceProfile = _baseProfile;
        simulation.AI.SetBalanceProfileForFaction(1, candidateFaction == 1 ? candidate : _baseProfile);
        simulation.AI.SetBalanceProfileForFaction(2, candidateFaction == 2 ? candidate : _baseProfile);

        _episodeElapsedSeconds = 0f;
        _statusElapsedSeconds = 0f;
        _latestRoundFitness = double.NaN;
        scene.Start(simulation, worldMap);
        _message = candidateFaction == 1
            ? "Синие используют геном кандидата; красные — базовый профиль."
            : "Красные используют геном кандидата; синие — базовый профиль.";
        PublishStatus(simulation);
    }

    private void CompleteRound(
        UnitSimulation simulation,
        LongRangeCombatTestScene scene,
        WorldMap worldMap)
    {
        ushort candidateFaction = _roundIndex == 0 ? (ushort)1 : (ushort)2;
        SideMetrics blue = MeasureFaction(simulation, 1);
        SideMetrics red = MeasureFaction(simulation, 2);
        SideMetrics candidate = candidateFaction == 1 ? blue : red;
        SideMetrics opponent = candidateFaction == 1 ? red : blue;

        double fitness = Fitness(candidate, opponent);
        _roundFitness[_roundIndex] = fitness;
        _roundFitnessSum += fitness;
        _latestRoundFitness = fitness;
        _message = $"Матч завершён: fitness {fitness:+0.0;-0.0;0.0}.";

        Console.WriteLine(
            $"[EVOLVE LIVE] gen={_generation} candidate={_candidateIndex + 1}/{PopulationSize} " +
            $"round={_roundIndex + 1}/{RoundsPerCandidate} fitness={fitness:+0.0;-0.0;0.0} " +
            $"blue={blue.Alive}/{blue.Total} red={red.Alive}/{red.Total}");

        if (_roundIndex + 1 < RoundsPerCandidate)
        {
            _roundIndex++;
            StartEpisode(simulation, scene, worldMap);
            return;
        }

        double averageFitness = _roundFitnessSum / RoundsPerCandidate;
        float[] genes = _population[_candidateIndex];
        AiBalanceProfile profile = Profile(genes, _baseProfile);
        _scores.Add(new CandidateResult((float[])genes.Clone(), averageFitness));

        if (averageFitness > _bestFitness)
        {
            _bestFitness = averageFitness;
            _bestGenes = (float[])genes.Clone();
            _bestProfile = profile;
            SaveBestProfile();
        }

        AppendHistory(blue, red, averageFitness, profile);
        _candidateIndex++;

        if (_candidateIndex >= _population.Count)
        {
            NextGeneration();
        }
        else
        {
            _roundIndex = 0;
            _roundFitnessSum = 0d;
            _latestRoundFitness = double.NaN;
        }

        StartEpisode(simulation, scene, worldMap);
    }

    private void NextGeneration()
    {
        _scores.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));
        var next = new List<float[]>(PopulationSize)
        {
            (float[])_scores[0].Genes.Clone()
        };
        if (PopulationSize > 1 && _scores.Count > 1)
            next.Add((float[])_scores[1].Genes.Clone());

        int parentPool = Math.Clamp(PopulationSize / 2, 2, _scores.Count);
        while (next.Count < PopulationSize)
        {
            if (_random.NextDouble() < .06d)
            {
                next.Add(Mutate(GenesFrom(_baseProfile), _random, .9d, .45f));
                continue;
            }

            float[] a = _scores[_random.Next(parentPool)].Genes;
            float[] b = _scores[_random.Next(parentPool)].Genes;
            var child = new float[GeneCount];
            for (int gene = 0; gene < GeneCount; gene++)
                child[gene] = _random.Next(2) == 0 ? a[gene] : b[gene];
            next.Add(Mutate(child, _random, .18d, .14f));
        }

        _population.Clear();
        _population.AddRange(next);
        _scores.Clear();
        _generation++;
        _candidateIndex = 0;
        _roundIndex = 0;
        _roundFitnessSum = 0d;
        _latestRoundFitness = double.NaN;
        _message = $"Новое поколение {_generation}; лучшая fitness={_bestFitness:+0.0;-0.0;0.0}.";
        Console.WriteLine($"[EVOLVE LIVE] ===== GENERATION {_generation}; best={_bestFitness:+0.0;-0.0;0.0} =====");
    }

    private void SaveBestProfile()
    {
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            File.WriteAllText(
                _bestProfilePath,
                JsonSerializer.Serialize(_bestProfile, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(
                $"[EVOLVE LIVE] New best fitness={_bestFitness:+0.0;-0.0;0.0}; saved {_bestProfilePath}");
        }
        catch (Exception exception)
        {
            _message = $"Не удалось сохранить лучший профиль: {exception.Message}";
            Console.WriteLine($"[EVOLVE LIVE] Checkpoint save failed: {exception.Message}");
        }
    }

    private void AppendHistory(
        SideMetrics blue,
        SideMetrics red,
        double fitness,
        AiBalanceProfile profile)
    {
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            string row = string.Create(CultureInfo.InvariantCulture,
                $"{_generation},{_candidateIndex + 1},{fitness:F4},{_bestFitness:F4}," +
                $"{blue.Alive}/{blue.Total},{red.Alive}/{red.Total},{blue.HealthRatio:F4},{red.HealthRatio:F4}," +
                $"{profile.EngageScale:F4},{profile.FlankScale:F4},{profile.CoverScale:F4}," +
                $"{profile.RetreatScale:F4},{profile.ProneScale:F4},{profile.SuppressiveFireScale:F4}{Environment.NewLine}");
            File.AppendAllText(_historyPath, row);
        }
        catch (Exception exception)
        {
            _message = $"Не удалось записать историю: {exception.Message}";
            Console.WriteLine($"[EVOLVE LIVE] History write failed: {exception.Message}");
        }
    }

    private void PublishStatus(UnitSimulation simulation)
    {
        SideMetrics blue = MeasureFaction(simulation, 1);
        SideMetrics red = MeasureFaction(simulation, 2);
        float[] genes = _candidateIndex >= 0 && _candidateIndex < _population.Count
            ? _population[_candidateIndex]
            : GenesFrom(_bestProfile);
        AiBalanceProfile profile = Profile(genes, _baseProfile);
        ushort candidateFaction = _roundIndex == 0 ? (ushort)1 : (ushort)2;

        var status = new AiEvolutionStatus(
            _hasStarted,
            _running,
            _generation,
            Math.Clamp(_candidateIndex + 1, 1, PopulationSize),
            PopulationSize,
            _roundIndex + 1,
            RoundsPerCandidate,
            candidateFaction,
            _episodeElapsedSeconds,
            EpisodeDurationSeconds,
            blue.Alive,
            blue.Total,
            blue.HealthRatio,
            red.Alive,
            red.Total,
            red.HealthRatio,
            _latestRoundFitness,
            _roundFitnessSum / Math.Max(1, _roundIndex),
            _bestFitness,
            GeneSummary(profile),
            _message,
            _bestProfilePath);

        Volatile.Write(ref _status, status);
    }

    private void ConfigureOutputPaths()
    {
        string root = FindRepositoryRoot();
        _outputDirectory = Path.Combine(root, "Experiments", "AiEvolution", "output");
        _bestProfilePath = Path.Combine(_outputDirectory, "ai-balance-live-best.json");
        _historyPath = Path.Combine(_outputDirectory, "ai-balance-live-history.csv");
    }

    private static string FindRepositoryRoot()
    {
        string[] startPaths = [Directory.GetCurrentDirectory(), AppContext.BaseDirectory];
        foreach (string path in startPaths)
        {
            DirectoryInfo? current = new(path);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "Experiments", "AiEvolution")))
                    return current.FullName;
                current = current.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static SideMetrics MeasureFaction(UnitSimulation simulation, ushort faction)
    {
        int total = 0;
        int alive = 0;
        double healthRatioSum = 0d;
        ReadOnlySpan<int> active = simulation.Units.ActiveIndices;
        float[] hp = simulation.Health.OverallHitPoints;
        float[] maxHp = simulation.Health.OverallMaxHitPoints;
        ushort[] factions = simulation.Units.FactionTag;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (factions[unit] != faction || maxHp[unit] <= 0f)
                continue;

            total++;
            float ratio = Math.Clamp(hp[unit] / maxHp[unit], 0f, 1f);
            healthRatioSum += ratio;
            if (hp[unit] > 0f)
                alive++;
        }

        return new SideMetrics(total, alive, total > 0 ? healthRatioSum / total : 0d);
    }

    private static double Fitness(SideMetrics candidate, SideMetrics opponent)
    {
        double candidateSurvivors = candidate.Alive / (double)Math.Max(1, candidate.Total);
        double opponentSurvivors = opponent.Alive / (double)Math.Max(1, opponent.Total);
        double score = (candidateSurvivors - opponentSurvivors) * 100d +
                       (candidate.HealthRatio - opponent.HealthRatio) * 60d;

        if (candidateSurvivors > opponentSurvivors + .02d)
            score += 10d;
        else if (opponentSurvivors > candidateSurvivors + .02d)
            score -= 10d;
        return score;
    }

    private static float[] Mutate(float[] parent, Random random, double chance, float strength)
    {
        float[] genes = (float[])parent.Clone();
        for (int i = 0; i < genes.Length; i++)
        {
            if (random.NextDouble() < chance)
            {
                float span = Max[i] - Min[i];
                genes[i] = Math.Clamp(
                    genes[i] + ((float)random.NextDouble() * 2f - 1f) * span * strength,
                    Min[i],
                    Max[i]);
            }
        }

        return genes;
    }

    private static string GeneSummary(AiBalanceProfile profile) =>
        string.Create(CultureInfo.InvariantCulture,
            $"engage {profile.EngageScale:F2} · flank {profile.FlankScale:F2} · " +
            $"cover {profile.CoverScale:F2} · retreat {profile.RetreatScale:F2} · " +
            $"prone {profile.ProneScale:F2} · suppress {profile.SuppressiveFireScale:F2}");

    private static float[] GenesFrom(AiBalanceProfile p) =>
    [
        p.EngageScale,p.FlankScale,p.CoverScale,p.RetreatScale,p.ProneScale,
        p.SuppressiveFireScale,p.LastKnownScale,p.InvestigationScale,p.SquadContactScale,p.PatrolScale,
        p.ProneAdoptionMinimumUtility,p.FlankUtilityAdvantage,p.RegularSwitchMargin,p.CommittedSwitchMargin,
        p.MinimumInvestigationUtility,p.TacticalCoverPressureWeight,p.TacticalFlankDistanceStart,p.TacticalFlankDistanceWeight,
        p.InvestigationDistancePenalty,p.VisibleCombatMediumScore,p.CommitDurationBaseSeconds,
        p.RangedEngagementRangeFraction,p.RangedRangeToleranceMinimum,p.RangedRangeToleranceFraction,
        p.MeleeEngagementRangeFraction,p.MeleeMinimumEngagementDistance,p.CoverSearchRadius,
        p.FlankNearBackoffDistance,p.FlankNearLateralDistance,p.FlankFarBackoffDistance,p.FlankFarLateralDistance,
        p.FlankNearRangeFraction,p.FlankFarRangeFraction,p.CoverThreatSeparationWeight,p.CoverTravelCostWeight,
        p.FlankBackoffProportion,p.FlankLateralProportion,p.FlankRangeErrorWeight,p.FlankTravelCostWeight,
        p.FlankLineOfSightBonus
    ];

    private static AiBalanceProfile Profile(float[] g, AiBalanceProfile basis) => basis with
    {
        EngageScale=g[0], FlankScale=g[1], CoverScale=g[2], RetreatScale=g[3], ProneScale=g[4],
        SuppressiveFireScale=g[5], LastKnownScale=g[6], InvestigationScale=g[7], SquadContactScale=g[8],
        PatrolScale=g[9], ProneAdoptionMinimumUtility=g[10], FlankUtilityAdvantage=g[11],
        RegularSwitchMargin=g[12], CommittedSwitchMargin=g[13], MinimumInvestigationUtility=g[14],
        TacticalCoverPressureWeight=g[15], TacticalFlankDistanceStart=g[16], TacticalFlankDistanceWeight=g[17],
        InvestigationDistancePenalty=g[18], VisibleCombatMediumScore=g[19], CommitDurationBaseSeconds=g[20],
        RangedEngagementRangeFraction=g[21], RangedRangeToleranceMinimum=g[22], RangedRangeToleranceFraction=g[23],
        MeleeEngagementRangeFraction=g[24], MeleeMinimumEngagementDistance=g[25], CoverSearchRadius=g[26],
        FlankNearBackoffDistance=g[27], FlankNearLateralDistance=g[28], FlankFarBackoffDistance=g[29],
        FlankFarLateralDistance=g[30], FlankNearRangeFraction=g[31], FlankFarRangeFraction=g[32],
        CoverThreatSeparationWeight=g[33], CoverTravelCostWeight=g[34], FlankBackoffProportion=g[35],
        FlankLateralProportion=g[36], FlankRangeErrorWeight=g[37], FlankTravelCostWeight=g[38],
        FlankLineOfSightBonus=g[39]
    };

    private sealed record CandidateResult(float[] Genes, double Fitness);
    private readonly record struct SideMetrics(int Total, int Alive, double HealthRatio);
}
