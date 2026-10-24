using System.Globalization;
using System.Text.Json;
using Core.Unit;

namespace RimClone.AiLab;

public sealed class LabTrainer
{
    private const int EliteCount = 2;
    private const double FreshLineChance = 0.08d;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<HoldoutReport> RunAsync(
        LabConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        LabExperimentDefinition definition = LabExperimentDefinition.Get(configuration.Experiment);
        string output = Path.GetFullPath(configuration.OutputPath);
        string directory = Path.GetDirectoryName(output) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        string historyPath = Path.ChangeExtension(output, ".history.csv");
        string archivePath = Path.ChangeExtension(output, ".archive.json");
        string validationPath = Path.ChangeExtension(output, ".validation.json");
        File.WriteAllText(historyPath,
            "generation,candidate,fitness,win_rate,survivor_rate,own_health,enemy_health,detection_rate,visual_exposure,break_contact_rate,mean_break_seconds,activity,action_diversity,genes\n");

        var random = new Random(configuration.Seed);
        TacticalGenome baselineGenome = TacticalGenome.FromProfile(configuration.BaselineProfile);
        var population = new List<TacticalGenome>(configuration.PopulationSize) { baselineGenome };
        while (population.Count < configuration.PopulationSize)
            population.Add(baselineGenome.Mutate(definition, random, 0.75d, 0.22f));

        var archive = new QualityDiversityArchive();
        TacticalGenome bestGenome = baselineGenome.Copy();
        double bestFitness = double.NegativeInfinity;
        var runner = new BattleEpisodeRunner();

        Console.WriteLine($"[AI-LAB] Experiment={definition.DisplayName}; population={configuration.PopulationSize}; " +
            $"generations={configuration.Generations}; workers={configuration.WorkerCount}; " +
            $"episodes/candidate={definition.Scenarios.Length * configuration.RepeatsPerScenario * 2}; seed={configuration.Seed}");
        Console.WriteLine($"[AI-LAB] Objective: {definition.Goal}");
        Console.WriteLine("[AI-LAB] Each worker owns its UnitSimulation and WorldMap. Rendering is not involved.");

        for (int generation = 1; generation <= configuration.Generations; generation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluated = new CandidateEvaluation?[population.Count];
            int completed = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = configuration.WorkerCount,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, population.Count),
                parallelOptions,
                async (index, token) =>
                {
                    CandidateEvaluation result = await Task.Run(
                        () => EvaluateCandidate(
                            runner, configuration, definition, population[index],
                            generation, index + 1, configuration.Seed, token),
                        token);
                    evaluated[index] = result;
                    int done = Interlocked.Increment(ref completed);
                    lock (Console.Out)
                    {
                        Console.WriteLine(
                            $"[AI-LAB] G{generation}/{configuration.Generations} " +
                            $"candidate {index + 1}/{population.Count} " +
                            $"fitness={result.Fitness:F2}; wins={result.WinRate:P0}; " +
                            $"survival={result.SurvivorRate:P0}; detection={result.DetectionRate:P0}; " +
                            $"complete={done}/{population.Count}");
                    }
                });

            CandidateEvaluation[] results = evaluated
                .Select(x => x ?? throw new InvalidOperationException("A worker returned no result."))
                .OrderByDescending(x => x.Fitness)
                .ToArray();

            foreach (CandidateEvaluation result in results)
            {
                archive.Add(result);
                AppendHistory(historyPath, result);
            }

            CandidateEvaluation generationBest = results[0];
            double mean = results.Average(x => x.Fitness);
            double worst = results[^1].Fitness;
            if (generationBest.Fitness > bestFitness)
            {
                bestFitness = generationBest.Fitness;
                bestGenome = generationBest.Genome.Copy();
                AiBalanceProfile bestProfile = bestGenome.ApplyTo(configuration.BaselineProfile);
                File.WriteAllText(output, JsonSerializer.Serialize(bestProfile, JsonOptions));
                Console.WriteLine($"[AI-LAB] New best checkpoint: {bestFitness:F2}; {output}");
            }

            var summary = new GenerationSummary(
                generation, generationBest.Fitness, mean, worst,
                results.Average(x => x.WinRate),
                results.Average(x => x.SurvivorRate),
                results.Length);
            Console.WriteLine(
                $"[AI-LAB] ===== G{generation}: best={summary.BestFitness:F2}; " +
                $"mean={summary.MeanFitness:F2}; worst={summary.WorstFitness:F2}; " +
                $"archive={archive.Count}/25 =====");

            File.WriteAllText(archivePath, JsonSerializer.Serialize(archive.GetEntries(), JsonOptions));
            File.WriteAllText(output + ".run.json", JsonSerializer.Serialize(new
            {
                Experiment = definition,
                Configuration = new
                {
                    configuration.PopulationSize,
                    configuration.Generations,
                    configuration.RepeatsPerScenario,
                    configuration.TeamSize,
                    configuration.SimulationTicks,
                    configuration.WorkerCount,
                    configuration.Seed,
                    Objective = definition.Goal
                },
                LastGeneration = summary,
                BestFitness = bestFitness,
                BestGenome = bestGenome,
                ArchiveEntries = archive.Count,
                Checkpoint = output
            }, JsonOptions));

            if (generation < configuration.Generations)
                population = NextGeneration(
                    results, archive.GetEntries(), baselineGenome, definition,
                    configuration.PopulationSize, random);
        }

        // New root seed for a held-out comparison. Baseline and trained profiles use
        // paired scenarios; the report does not automatically apply the new profile.
        int holdoutSeed = unchecked(configuration.Seed ^ (int)0x5F3759DF);
        CandidateEvaluation baseline = EvaluateCandidate(
            runner, configuration, definition, baselineGenome,
            configuration.Generations + 1, 0, holdoutSeed, cancellationToken);
        CandidateEvaluation trained = EvaluateCandidate(
            runner, configuration, definition, bestGenome,
            configuration.Generations + 1, 1, holdoutSeed, cancellationToken);

        var report = new HoldoutReport(
            configuration.Seed,
            holdoutSeed,
            configuration.Experiment,
            baseline,
            trained,
            trained.Fitness - baseline.Fitness,
            trained.WinRate - baseline.WinRate,
            trained.SurvivorRate - baseline.SurvivorRate,
            trained.Fitness > baseline.Fitness,
            trained.WinRate > baseline.WinRate);
        File.WriteAllText(validationPath, JsonSerializer.Serialize(report, JsonOptions));

        Console.WriteLine(
            $"[AI-LAB] HOLDOUT baseline={baseline.Fitness:F2}, trained={trained.Fitness:F2}, " +
            $"delta={report.FitnessDelta:+0.00;-0.00;0.00}; win-rate delta={report.WinRateDelta:+0.0%;-0.0%;0.0%}");
        Console.WriteLine($"[AI-LAB] Holdout report: {validationPath}");
        Console.WriteLine("[AI-LAB] Training fitness alone is not proof of improvement; inspect the holdout report.");

        return report;
    }

    private static CandidateEvaluation EvaluateCandidate(
        BattleEpisodeRunner runner,
        LabConfiguration configuration,
        LabExperimentDefinition definition,
        TacticalGenome genome,
        int generation,
        int candidateIndex,
        int seedRoot,
        CancellationToken cancellationToken)
    {
        var episodes = new List<EpisodeMetrics>(
            definition.Scenarios.Length * configuration.RepeatsPerScenario * 2);

        foreach (LabScenarioKind scenario in definition.Scenarios)
        for (int repeat = 0; repeat < configuration.RepeatsPerScenario; repeat++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int seed = StableSeed(seedRoot, scenario, repeat);

            // Paired side-swapped episodes use the same scenario seed.
            episodes.Add(runner.Run(configuration, definition, genome, scenario, seed, candidateStartedLeft: true, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            episodes.Add(runner.Run(configuration, definition, genome, scenario, seed, candidateStartedLeft: false, cancellationToken));
        }

        double count = Math.Max(1, episodes.Count);
        return new CandidateEvaluation(
            generation,
            candidateIndex,
            genome.Copy(),
            episodes.Average(x => x.Fitness),
            episodes.Count(x => x.Outcome > 0) / count,
            episodes.Average(x => x.CandidateSurvivorRate),
            episodes.Average(x => x.CandidateHealthRatio),
            episodes.Average(x => x.EnemyHealthRatio),
            episodes.Average(x => x.DetectionRate),
            episodes.Average(x => x.VisualExposureRatio),
            episodes.Average(x => x.BreakContactRate),
            episodes.Average(x => x.MeanBreakContactSeconds),
            episodes.Average(x => x.Activity),
            episodes.Average(x => x.ActionDiversity),
            episodes.ToArray());
    }

    private static int StableSeed(int root, LabScenarioKind scenario, int repeat) =>
        unchecked(root + ((int)scenario + 1) * 7919 + (repeat + 1) * 104729);

    private static List<TacticalGenome> NextGeneration(
        CandidateEvaluation[] ranked,
        ArchiveEntry[] archive,
        TacticalGenome baseline,
        LabExperimentDefinition definition,
        int populationSize,
        Random random)
    {
        var next = new List<TacticalGenome>(populationSize);
        int elites = Math.Min(EliteCount, Math.Min(ranked.Length, populationSize));
        for (int i = 0; i < elites; i++)
            next.Add(ranked[i].Genome.Copy());

        // Inject a limited number of archived behaviorally different profiles. The archive
        // supplements fitness selection and preserves promising alternatives.
        int archiveBudget = Math.Min(Math.Max(1, populationSize / 5), Math.Max(0, populationSize - next.Count));
        foreach (ArchiveEntry entry in archive.OrderByDescending(x => x.Fitness))
        {
            if (archiveBudget <= 0 || next.Count >= populationSize)
                break;
            if (next.Any(x => SameGenome(x, entry.Genome)))
                continue;
            next.Add(entry.Genome.Copy());
            archiveBudget--;
        }

        int parentPool = Math.Clamp(ranked.Length / 2, 2, ranked.Length);
        while (next.Count < populationSize)
        {
            if (random.NextDouble() < FreshLineChance)
            {
                next.Add(baseline.Mutate(definition, random, 0.90d, 0.45f));
                continue;
            }

            TacticalGenome a = ranked[random.Next(parentPool)].Genome;
            TacticalGenome b = ranked[random.Next(parentPool)].Genome;
            TacticalGenome child = a.Crossover(b, definition, random)
                .Mutate(definition, random, 0.18d, 0.14f);
            next.Add(child);
        }

        return next;
    }

    private static bool SameGenome(TacticalGenome a, TacticalGenome b)
    {
        for (int i = 0; i < TacticalGenome.GeneCount; i++)
            if (MathF.Abs(a.Values[i] - b.Values[i]) > 0.0001f)
                return false;
        return true;
    }

    private static void AppendHistory(string path, CandidateEvaluation result)
    {
        string row = string.Join(",",
            result.Generation.ToString(CultureInfo.InvariantCulture),
            result.CandidateIndex.ToString(CultureInfo.InvariantCulture),
            result.Fitness.ToString("F4", CultureInfo.InvariantCulture),
            result.WinRate.ToString("F4", CultureInfo.InvariantCulture),
            result.SurvivorRate.ToString("F4", CultureInfo.InvariantCulture),
            result.OwnHealthRatio.ToString("F4", CultureInfo.InvariantCulture),
            result.EnemyHealthRatio.ToString("F4", CultureInfo.InvariantCulture),
            result.DetectionRate.ToString("F4", CultureInfo.InvariantCulture),
            result.VisualExposureRatio.ToString("F4", CultureInfo.InvariantCulture),
            result.BreakContactRate.ToString("F4", CultureInfo.InvariantCulture),
            result.MeanBreakContactSeconds.ToString("F4", CultureInfo.InvariantCulture),
            result.Activity.ToString("F4", CultureInfo.InvariantCulture),
            result.ActionDiversity.ToString("F4", CultureInfo.InvariantCulture),
            "\"" + result.Genome.ToCompactString().Replace("\"", "\"\"") + "\"") + Environment.NewLine;
        File.AppendAllText(path, row);
    }
}
