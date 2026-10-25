using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using Core.AI.Perception;
using Core.Items;
using Core.Map;
using Core.Unit;
using RimClone.Experiments.AiEvolution;

internal enum AiStealthTrainingTask
{
    RemainUndetected,
    BreakContact
}

internal readonly record struct AiStealthEpisodeJob(int Index, int CandidateIndex, int MatchIndex, int Seed);

internal readonly record struct AiStealthEpisodeResult(
    double Fitness, bool Success, bool Detected, bool Triggered, bool ContactBroken,
    bool GoalReached, bool Died, double Progress, double HealthRatio,
    double FirstDetectionSeconds, double BreakContactSeconds, int RouteFailures);

internal readonly record struct AiStealthCandidateResult(
    float[] Genes, double Fitness, double SuccessRate, double DetectionRate,
    double ContactBreakRate, double GoalRate, double MeanProgress, double MeanHealth,
    double MeanDetectionSeconds, double MeanBreakSeconds);

/// <summary>
/// Evolves two individual stealth objectives using isolated real game simulations.
/// Hidden world state is read only by the evaluator for scoring; pawn decisions use
/// the same uncertain perception-contact memory that normal gameplay uses.
/// </summary>
internal static class AiSpecializedEvolutionTrainer
{
    private const float Dt = 0.05f;
    private const int GeneCount = 7;
    private static volatile bool _stopRequested;
    private static readonly object OutputGate = new();
    private static int _completedEpisodes;
    private static int _workers = 1;
    private static string _phase = "TRAIN";

    private static readonly float[] Min = { 0.25f, 0.25f, 0f, 0f, 6f, 2f, 0.08f };
    private static readonly float[] Max = { 6f, 12f, 8f, 1.5f, 80f, 18f, 0.95f };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static int Run(string[] args, string taskName)
    {
        if (!TryParseTask(taskName, out AiStealthTrainingTask task))
        {
            Console.Error.WriteLine(
                $"Unknown specialized task '{taskName}'. Use remain-undetected or break-contact.");
            return 2;
        }

        _stopRequested = false;
        _completedEpisodes = 0;
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            _stopRequested = true;
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            int populationSize = Arg(args, "--population", 16, 4, 128);
            int generations = Arg(args, "--generations", 12, 1, 10000);
            int matches = Arg(args, "--matches", 6, 2, 1000);
            int ticks = Arg(args, "--ticks", 2400, 200, 100000);
            int holdoutMatches = Arg(args, "--holdout-matches", 16, 8, 1000);
            int workers = Arg(args, "--workers", Math.Clamp(Environment.ProcessorCount - 1, 1, 6), 1, 32);
            int seed = Arg(args, "--seed", 20261025, int.MinValue, int.MaxValue);
            _workers = workers;

            string taskId = task == AiStealthTrainingTask.RemainUndetected
                ? "remain-undetected" : "break-contact";
            string outputPath = Path.GetFullPath(TextArg(
                args, "--output", $"Experiments/AiEvolution/output/ai-stealth-{taskId}.json"));
            string outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("Output file must have a directory.");
            Directory.CreateDirectory(outputDirectory);
            string historyPath = Path.ChangeExtension(outputPath, ".history.csv");
            string validationPath = Path.ChangeExtension(outputPath, ".validation.json");

            float[] defaultGenes = GenesFrom(AiStealthProfile.Default);
            ValidateGenome(defaultGenes);
            var random = new Random(seed);
            var population = new List<float[]>(populationSize) { (float[])defaultGenes.Clone() };
            while (population.Count < populationSize)
                population.Add(Mutate(defaultGenes, random, 0.78d, 0.22f));

            double globalBest = double.NegativeInfinity;
            float[] globalBestGenes = (float[])defaultGenes.Clone();
            using (var history = new StreamWriter(historyPath, false))
            {
                history.WriteLine(
                    "generation,candidate,fitness,success_rate,detection_rate,contact_break_rate," +
                    "goal_rate,mean_progress,mean_health,mean_detection_seconds,mean_break_seconds," +
                    "progress_weight,exposure_weight,proximity_weight,travel_cost_weight," +
                    "estimated_threat_range,look_ahead_distance,crawl_risk_threshold");
            }

            int totalEpisodes = checked(populationSize * matches);
            Console.WriteLine(
                $"[STEALTH-EVOLVE] task={taskId}; population={populationSize}; generations={generations}; " +
                $"matches/candidate={matches}; ticks/match={ticks}; workers={workers}; seed={seed}");
            Console.WriteLine(
                "[STEALTH-EVOLVE] Ctrl+C requests a graceful stop. Candidates in each generation share scenario seeds; " +
                "holdout validation uses separate seeds.");

            for (int generation = 1; generation <= generations; generation++)
            {
                ThrowIfStopped();
                _phase = $"TRAIN G{generation}/{generations}";
                var jobs = new AiStealthEpisodeJob[totalEpisodes];
                int jobIndex = 0;
                for (int candidateIndex = 0; candidateIndex < population.Count; candidateIndex++)
                for (int matchIndex = 0; matchIndex < matches; matchIndex++)
                {
                    int matchSeed = unchecked(seed + generation * 1_000_003 + matchIndex * 7_919);
                    jobs[jobIndex] = new AiStealthEpisodeJob(jobIndex, candidateIndex, matchIndex, matchSeed);
                    jobIndex++;
                }

                Interlocked.Exchange(ref _completedEpisodes, 0);
                AiStealthEpisodeResult[] episodes = AiExperimentBatchRunner.Execute(
                    jobs,
                    workers,
                    (job, _) =>
                    {
                        ThrowIfStopped();
                        AiStealthEpisodeResult episode = RunEpisode(
                            task, Profile(population[job.CandidateIndex]), job.Seed, ticks);
                        int done = Interlocked.Increment(ref _completedEpisodes);
                        if (done % Math.Max(1, workers * 2) == 0 || done == totalEpisodes)
                        {
                            lock (OutputGate)
                                Console.WriteLine(
                                    $"[STEALTH-EVOLVE] {_phase} episodes {done}/{totalEpisodes}; " +
                                    $"latest fitness={episode.Fitness:F1}; success={episode.Success}");
                        }
                        return episode;
                    });

                var results = new List<AiStealthCandidateResult>(populationSize);
                using var history = new StreamWriter(historyPath, true);
                for (int candidateIndex = 0; candidateIndex < population.Count; candidateIndex++)
                {
                    ThrowIfStopped();
                    var candidateEpisodes = new List<AiStealthEpisodeResult>(matches);
                    for (int i = 0; i < jobs.Length; i++)
                        if (jobs[i].CandidateIndex == candidateIndex)
                            candidateEpisodes.Add(episodes[i]);

                    AiStealthCandidateResult metrics = Aggregate(
                        (float[])population[candidateIndex].Clone(), candidateEpisodes);
                    results.Add(metrics);

                    if (metrics.Fitness > globalBest)
                    {
                        globalBest = metrics.Fitness;
                        globalBestGenes = (float[])metrics.Genes.Clone();
                        SaveProfile(outputPath, Profile(globalBestGenes));
                        Console.WriteLine(
                            $"[STEALTH-EVOLVE] New best G{generation} #{candidateIndex + 1}: " +
                            $"fitness={globalBest:F2}; success={metrics.SuccessRate:P1}; " +
                            $"detection={metrics.DetectionRate:P1}; saved={outputPath}");
                    }

                    history.WriteLine(string.Join(",",
                        generation.ToString(CultureInfo.InvariantCulture),
                        (candidateIndex + 1).ToString(CultureInfo.InvariantCulture),
                        F(metrics.Fitness), F(metrics.SuccessRate), F(metrics.DetectionRate),
                        F(metrics.ContactBreakRate), F(metrics.GoalRate), F(metrics.MeanProgress),
                        F(metrics.MeanHealth), F(metrics.MeanDetectionSeconds), F(metrics.MeanBreakSeconds),
                        F(metrics.Genes[0]), F(metrics.Genes[1]), F(metrics.Genes[2]), F(metrics.Genes[3]),
                        F(metrics.Genes[4]), F(metrics.Genes[5]), F(metrics.Genes[6])));
                }

                results.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));
                AiStealthCandidateResult champion = results[0];
                double meanFitness = results.Average(x => x.Fitness);
                Console.WriteLine(
                    $"[STEALTH-EVOLVE] GENERATION {generation}: best={champion.Fitness:F2}; " +
                    $"mean={meanFitness:F2}; success={champion.SuccessRate:P1}; " +
                    $"detected={champion.DetectionRate:P1}; contact-break={champion.ContactBreakRate:P1}; " +
                    $"best-ever={globalBest:F2}");
                if (generation < generations)
                    population = NextGeneration(results, populationSize, random, defaultGenes);
            }

            _phase = "HOLDOUT";
            int validationSeed = unchecked(seed ^ 0x5F3759DF);
            Console.WriteLine(
                $"[STEALTH-EVOLVE] Holdout: trained vs default on {holdoutMatches} paired new scenarios.");
            Interlocked.Exchange(ref _completedEpisodes, 0);
            AiStealthCandidateResult trainedHoldout = EvaluateProfile(
                task, Profile(globalBestGenes), holdoutMatches, ticks, validationSeed, workers);
            Interlocked.Exchange(ref _completedEpisodes, 0);
            AiStealthCandidateResult baselineHoldout = EvaluateProfile(
                task, AiStealthProfile.Default, holdoutMatches, ticks, validationSeed, workers);

            var validation = new
            {
                Task = taskId,
                TrainingSeed = seed,
                HoldoutSeed = validationSeed,
                HoldoutMatches = holdoutMatches,
                Trained = trainedHoldout,
                Baseline = baselineHoldout,
                Delta = new
                {
                    Fitness = trainedHoldout.Fitness - baselineHoldout.Fitness,
                    SuccessRate = trainedHoldout.SuccessRate - baselineHoldout.SuccessRate,
                    DetectionRate = baselineHoldout.DetectionRate - trainedHoldout.DetectionRate,
                    ContactBreakRate = trainedHoldout.ContactBreakRate - baselineHoldout.ContactBreakRate,
                    GoalRate = trainedHoldout.GoalRate - baselineHoldout.GoalRate
                },
                ImprovesFitness = trainedHoldout.Fitness > baselineHoldout.Fitness,
                ImprovesSuccessRate = trainedHoldout.SuccessRate > baselineHoldout.SuccessRate
            };
            File.WriteAllText(validationPath, JsonSerializer.Serialize(validation, JsonOptions));

            Console.WriteLine(
                $"[STEALTH-EVOLVE] HOLDOUT baseline: fitness={baselineHoldout.Fitness:F2}; " +
                $"success={baselineHoldout.SuccessRate:P1}; detected={baselineHoldout.DetectionRate:P1}; " +
                $"goal={baselineHoldout.GoalRate:P1}");
            Console.WriteLine(
                $"[STEALTH-EVOLVE] HOLDOUT trained:  fitness={trainedHoldout.Fitness:F2}; " +
                $"success={trainedHoldout.SuccessRate:P1}; detected={trainedHoldout.DetectionRate:P1}; " +
                $"goal={trainedHoldout.GoalRate:P1}");
            Console.WriteLine(
                $"[STEALTH-EVOLVE] HOLDOUT delta: fitness={trainedHoldout.Fitness - baselineHoldout.Fitness:+0.00;-0.00;0.00}; " +
                $"success={trainedHoldout.SuccessRate - baselineHoldout.SuccessRate:+0.0%;-0.0%;0.0%}; " +
                $"report={validationPath}");
            Console.WriteLine($"[STEALTH-EVOLVE] Profile: {outputPath}");
            Console.WriteLine($"[STEALTH-EVOLVE] History: {historyPath}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("[STEALTH-EVOLVE] Stop requested; best checkpoint has been preserved.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[STEALTH-EVOLVE] Failed: {exception}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static AiStealthCandidateResult EvaluateProfile(
        AiStealthTrainingTask task, AiStealthProfile profile, int matches,
        int ticks, int seed, int workers)
    {
        var jobs = new AiStealthEpisodeJob[matches];
        for (int i = 0; i < matches; i++)
            jobs[i] = new AiStealthEpisodeJob(i, 0, i, unchecked(seed + i * 7_919));

        AiStealthEpisodeResult[] episodes = AiExperimentBatchRunner.Execute(
            jobs, workers, (job, _) =>
            {
                ThrowIfStopped();
                AiStealthEpisodeResult episode = RunEpisode(task, profile, job.Seed, ticks);
                int done = Interlocked.Increment(ref _completedEpisodes);
                if (done % Math.Max(1, workers * 2) == 0 || done == matches)
                {
                    lock (OutputGate)
                        Console.WriteLine(
                            $"[STEALTH-EVOLVE] HOLDOUT {_phase}: {done}/{matches} episodes; " +
                            $"latest fitness={episode.Fitness:F1}");
                }
                return episode;
            });

        return Aggregate(GenesFrom(profile), episodes.ToList());
    }

    private static AiStealthEpisodeResult RunEpisode(
        AiStealthTrainingTask task, AiStealthProfile profile, int seed, int maxTicks)
    {
        ThrowIfStopped();
        var random = new Random(seed);
        var world = new WorldMap(regionsX: 4, regionsY: 2, layerCount: 32);
        BuildFlatGround(world);

        var sim = new UnitSimulation(unitCapacity: 16, bodyCapacity: 256)
        {
            // Keep every candidate in its own real simulation instance.
        };
        sim.AI.Enabled = true;
        sim.VisionEnabled = true;

        Vector3 candidateStart;
        Vector3 destination;
        List<(float X, float Y, float Angle)> guardLayout;
        if (task == AiStealthTrainingTask.RemainUndetected)
        {
            candidateStart = Surface(world, 5.5f, 54.5f);
            destination = Surface(world, 248.5f, 54.5f);
            guardLayout = BuildConcealmentCourse(world, random);
        }
        else
        {
            float gapY = 91f + random.Next(-4, 5);
            BuildFullHeightBarrier(world, 82, gapY, 6);
            candidateStart = Surface(world, 109.5f, 53.5f);
            destination = Surface(world, 17.5f, 53.5f);
            float guardX = 124f + random.Next(-2, 3);
            float guardY = 52f + random.Next(-3, 4);
            guardLayout = new List<(float X, float Y, float Angle)>
            {
                (guardX, guardY, MathF.PI),
                (guardX + 6f, guardY + 4f, MathF.PI)
            };
        }

        UnitId candidateId = sim.Spawn(
            UnitType.Colonist, candidateStart, bodyAngle: MathF.PI, factionTag: 1);
        var guardIds = new UnitId[guardLayout.Count];
        for (int i = 0; i < guardLayout.Count; i++)
        {
            (float x, float y, float angle) = guardLayout[i];
            Vector3 guardPosition = Surface(world, x, y);
            UnitId guardId = sim.Spawn(
                UnitType.Colonist, guardPosition, bodyAngle: angle, factionTag: 2);
            guardIds[i] = guardId;
            int guardIndex = guardId.Index;

            sim.Units.ViewRange[guardIndex] =
                task == AiStealthTrainingTask.BreakContact ? 78f : 40f;
            sim.Units.FieldOfView[guardIndex] =
                task == AiStealthTrainingTask.BreakContact ? 360f : 100f;
            sim.Units.HeadNormal[guardIndex] =
                new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            sim.Units.BodyNormal[guardIndex] = sim.Units.HeadNormal[guardIndex];

            int weaponSlot = sim.AddInventoryItem(guardId, WeaponCatalog.Pistol);
            if (weaponSlot < 0 ||
                !sim.EquipWeapon(guardId, weaponSlot, UnitWeaponSlot.Primary))
                throw new InvalidOperationException("Could not equip scenario guard with a pistol.");

            sim.AI.Store.SetPlayerOrder(guardIndex, UnitAiOrderType.Stop, guardPosition);
        }

        sim.AI.SetStealthProfileForFaction(1, profile);
        SeedContactReports(sim, candidateId, guardIds, maxTicks, random);
        if (task == AiStealthTrainingTask.RemainUndetected)
            sim.AI.Store.SetPlayerOrder(
                candidateId.Index, UnitAiOrderType.StealthMove, destination);
        else
            sim.AI.Store.SetPlayerOrder(
                candidateId.Index, UnitAiOrderType.Stop, candidateStart);

        float startDistance = Distance2D(candidateStart, destination);
        float firstDetection = float.NaN;
        float lastDetection = float.NaN;
        float hiddenSince = -1f;
        bool hasEverBeenDetected = false;
        bool triggered = task == AiStealthTrainingTask.RemainUndetected;
        bool contactBroken = false;
        bool goalReached = false;
        bool died = false;
        float healthRatio = 1f;
        int routeFailuresAtStart = sim.Navigation.RoutesFailed;
        int routeFailures = 0;
        float elapsed = 0f;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            if (tick % 10 == 0)
                ThrowIfStopped();

            sim.Update(world, Dt);
            elapsed = sim.PerceptionMemory.CurrentTimeSeconds;
            ObserveGuardDetection(
                sim, candidateId, guardIds,
                ref hasEverBeenDetected, ref firstDetection, ref lastDetection);

            bool currentlyVisible = hasEverBeenDetected &&
                float.IsFinite(lastDetection) && elapsed - lastDetection <= 0.45f;

            if (task == AiStealthTrainingTask.BreakContact &&
                hasEverBeenDetected && !triggered)
            {
                triggered = true;
                hiddenSince = -1f;
                sim.AI.Store.SetPlayerOrder(
                    candidateId.Index, UnitAiOrderType.StealthMove, destination);
            }

            if (currentlyVisible)
                hiddenSince = -1f;
            else if (triggered && hasEverBeenDetected && hiddenSince < 0f)
                hiddenSince = elapsed;

            contactBroken = triggered && hasEverBeenDetected &&
                hiddenSince >= 0f && elapsed - hiddenSince >= 1.75f;

            if (sim.Units.TryGetIndex(candidateId, out int candidateIndex))
            {
                float maxHp = sim.Health.OverallMaxHitPoints[candidateIndex];
                healthRatio = maxHp > 0f
                    ? Math.Clamp(sim.Health.OverallHitPoints[candidateIndex] / maxHp, 0f, 1f)
                    : 0f;
                died = sim.Health.OverallHitPoints[candidateIndex] <= 0f;
            }
            else
            {
                healthRatio = 0f;
                died = true;
            }

            routeFailures = sim.Navigation.RoutesFailed - routeFailuresAtStart;
            if (!died && sim.Units.TryGetIndex(candidateId, out candidateIndex))
                goalReached = Distance2D(sim.Units.Position[candidateIndex], destination) <= 2.1f;

            if (task == AiStealthTrainingTask.RemainUndetected)
            {
                if (died || goalReached)
                    break;
            }
            else if (died || (triggered && goalReached && contactBroken))
            {
                break;
            }
        }

        Vector3 finalPosition = sim.Units.TryGetIndex(candidateId, out int finalIndex)
            ? sim.Units.Position[finalIndex]
            : candidateStart;
        double progress = Math.Clamp(
            1d - Distance2D(finalPosition, destination) / Math.Max(1f, startDistance), 0d, 1d);
        bool detected = hasEverBeenDetected;
        bool success = task == AiStealthTrainingTask.RemainUndetected
            ? goalReached && !detected && !died
            : triggered && contactBroken && goalReached && !died;

        double fitness;
        if (task == AiStealthTrainingTask.RemainUndetected)
        {
            fitness =
                25d * progress + 10d * healthRatio + 35d * (goalReached ? 1d : 0d) +
                30d * (!detected ? 1d : 0d) - 70d * (detected ? 1d : 0d) -
                20d * (died ? 1d : 0d) - Math.Min(10d, routeFailures * 0.1d);
        }
        else
        {
            fitness =
                25d * progress + 15d * healthRatio + 20d * (goalReached ? 1d : 0d) +
                35d * (contactBroken ? 1d : 0d) + 25d * (success ? 1d : 0d) -
                30d * (died ? 1d : 0d) - 45d * (!contactBroken ? 1d : 0d) -
                30d * (!triggered ? 1d : 0d) - Math.Min(10d, routeFailures * 0.1d);

            if (contactBroken && float.IsFinite(firstDetection) && elapsed > firstDetection)
                fitness += Math.Max(0d, 8d - Math.Min(8d, elapsed - firstDetection)) * 0.5d;
        }

        return new AiStealthEpisodeResult(
            fitness, success, detected, triggered, contactBroken, goalReached, died,
            progress, healthRatio,
            float.IsFinite(firstDetection) ? firstDetection : -1d,
            contactBroken && float.IsFinite(lastDetection) ? Math.Max(0d, elapsed - lastDetection) : -1d,
            routeFailures);
    }

    private static void ObserveGuardDetection(
        UnitSimulation sim, UnitId candidateId, IReadOnlyList<UnitId> guards,
        ref bool everDetected, ref float firstDetection, ref float lastDetection)
    {
        for (int i = 0; i < guards.Count; i++)
        {
            UnitId guardId = guards[i];
            if (!sim.Units.TryGetIndex(guardId, out int guardIndex) ||
                !sim.PerceptionMemory.TryGetMemory(guardIndex, out TacticalContactMemory memory) ||
                !memory.TryGetContact(candidateId, out TacticalContact contact) ||
                contact.LastVisualAtSeconds < 0f)
                continue;

            float visualTime = contact.LastVisualAtSeconds;
            if (!float.IsFinite(firstDetection) || visualTime < firstDetection)
                firstDetection = visualTime;
            if (!float.IsFinite(lastDetection) || visualTime > lastDetection)
                lastDetection = visualTime;
            everDetected = true;
        }
    }

    private static void SeedContactReports(
        UnitSimulation sim, UnitId observerId, IReadOnlyList<UnitId> targetIds,
        int maxTicks, Random random)
    {
        TacticalContactMemory memory =
            sim.PerceptionMemory.GetOrCreateMemory(observerId.Index);
        float expiry = maxTicks * Dt + 60f;
        for (int i = 0; i < targetIds.Count; i++)
        {
            UnitId targetId = targetIds[i];
            if (!sim.Units.TryGetIndex(targetId, out int targetIndex))
                continue;

            Vector3 actual = sim.Units.Position[targetIndex];
            Vector3 estimate = actual + new Vector3(
                random.Next(-4, 5), random.Next(-4, 5), 0f);
            memory.Record(new PerceptionObservation(
                PerceptionSense.Communication,
                TacticalStimulusType.AllyContactReport,
                estimate,
                positionErrorRadius: 4.5f,
                intensity: 0.85f,
                detectionConfidence: 0.78f,
                identificationConfidence: 0f,
                detectionLevel: PerceptionDetectionLevel.Detected,
                identificationLevel: PerceptionIdentificationLevel.Unknown,
                occurredAtSeconds: 0f,
                receivedAtSeconds: 0f,
                expiresAtSeconds: expiry,
                identifiedSubject: null,
                reporterUnit: null,
                signals: PerceptionSignalFlags.Investigate |
                    PerceptionSignalFlags.PotentialThreat |
                    PerceptionSignalFlags.Avoid));
        }
    }

    private static List<(float X, float Y, float Angle)> BuildConcealmentCourse(
        WorldMap world, Random random)
    {
        int[] wallXs = { 28, 56, 84, 112, 140, 168, 196 };
        var guards = new List<(float X, float Y, float Angle)>(4);
        for (int i = 0; i < wallXs.Length; i++)
        {
            float gapCenter = (i % 2 == 0 ? 42f : 68f) + random.Next(-3, 4);
            BuildFullHeightBarrier(world, wallXs[i], gapCenter, 6);
            if (i % 2 == 0)
            {
                float angle = (i % 4 == 0 ? MathF.PI * 0.5f : -MathF.PI * 0.5f);
                guards.Add((wallXs[i] + 7f, gapCenter + random.Next(-1, 2), angle));
            }
        }
        return guards;
    }

    private static void BuildFullHeightBarrier(
        WorldMap world, int x, float gapCenter, int halfGap)
    {
        int gapY = (int)MathF.Round(gapCenter);
        for (int y = 1; y < world.TileHeight - 1; y++)
        {
            if (Math.Abs(y - gapY) <= halfGap)
                continue;
            world.SetSolidHeight(x, y, (ushort)Math.Min(22, world.LayerCount), (ushort)1);
            world.SetNavigationBlocked(x, y, true);
        }
    }

    private static void BuildFlatGround(WorldMap world)
    {
        for (int y = 0; y < world.TileHeight; y++)
        for (int x = 0; x < world.TileWidth; x++)
            world.SetSolidHeight(x, y, 1, (ushort)1);
    }

    private static Vector3 Surface(WorldMap world, float x, float y)
    {
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, world.MaxTileX);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, world.MaxTileY);
        return new Vector3(x, y, world.GetSurfaceHeight(tileX, tileY));
    }

    private static AiStealthCandidateResult Aggregate(
        float[] genes, IReadOnlyList<AiStealthEpisodeResult> episodes)
    {
        if (episodes.Count == 0)
            throw new InvalidOperationException("Cannot aggregate an empty candidate batch.");

        double count = episodes.Count;
        double detectionTime = 0d;
        int detectionCount = 0;
        double breakTime = 0d;
        int breakCount = 0;
        foreach (AiStealthEpisodeResult episode in episodes)
        {
            if (episode.FirstDetectionSeconds >= 0d)
            {
                detectionTime += episode.FirstDetectionSeconds;
                detectionCount++;
            }
            if (episode.BreakContactSeconds >= 0d)
            {
                breakTime += episode.BreakContactSeconds;
                breakCount++;
            }
        }

        return new AiStealthCandidateResult(
            genes,
            episodes.Average(x => x.Fitness),
            episodes.Count(x => x.Success) / count,
            episodes.Count(x => x.Detected) / count,
            episodes.Count(x => x.ContactBroken) / count,
            episodes.Count(x => x.GoalReached) / count,
            episodes.Average(x => x.Progress),
            episodes.Average(x => x.HealthRatio),
            detectionCount == 0 ? -1d : detectionTime / detectionCount,
            breakCount == 0 ? -1d : breakTime / breakCount);
    }

    private static List<float[]> NextGeneration(
        IReadOnlyList<AiStealthCandidateResult> ranked,
        int populationSize,
        Random random,
        float[] baseline)
    {
        var next = new List<float[]>(populationSize) { (float[])ranked[0].Genes.Clone() };
        if (populationSize > 1 && ranked.Count > 1)
            next.Add((float[])ranked[1].Genes.Clone());

        int parentPool = Math.Clamp(populationSize / 2, 2, ranked.Count);
        while (next.Count < populationSize)
        {
            if (random.NextDouble() < 0.05d)
            {
                next.Add(Mutate(baseline, random, 0.95d, 0.42f));
                continue;
            }

            float[] parentA = ranked[random.Next(parentPool)].Genes;
            float[] parentB = ranked[random.Next(parentPool)].Genes;
            var child = new float[GeneCount];
            for (int gene = 0; gene < GeneCount; gene++)
                child[gene] = random.Next(2) == 0 ? parentA[gene] : parentB[gene];
            next.Add(Mutate(child, random, 0.22d, 0.15f));
        }
        return next;
    }

    private static float[] Mutate(float[] parent, Random random, double chance, float strength)
    {
        float[] genes = (float[])parent.Clone();
        for (int i = 0; i < genes.Length; i++)
            if (random.NextDouble() < chance)
                genes[i] = Math.Clamp(
                    genes[i] + ((float)random.NextDouble() * 2f - 1f) * (Max[i] - Min[i]) * strength,
                    Min[i], Max[i]);
        return genes;
    }

    private static AiStealthProfile Profile(float[] genes) => new AiStealthProfile
    {
        ProgressWeight = genes[0],
        ExposureWeight = genes[1],
        ProximityWeight = genes[2],
        TravelCostWeight = genes[3],
        EstimatedThreatRange = genes[4],
        LookAheadDistance = genes[5],
        CrawlRiskThreshold = genes[6]
    }.Sanitized();

    private static float[] GenesFrom(AiStealthProfile profile) =>
    [
        profile.ProgressWeight, profile.ExposureWeight, profile.ProximityWeight,
        profile.TravelCostWeight, profile.EstimatedThreatRange, profile.LookAheadDistance,
        profile.CrawlRiskThreshold
    ];

    private static void ValidateGenome(float[] genes)
    {
        if (genes.Length != GeneCount || Min.Length != GeneCount || Max.Length != GeneCount)
            throw new InvalidOperationException("Stealth genome length mismatch.");
        for (int i = 0; i < genes.Length; i++)
            if (!float.IsFinite(genes[i]) || genes[i] < Min[i] || genes[i] > Max[i])
                throw new InvalidOperationException($"Invalid stealth gene {i}: {genes[i]}.");
    }

    private static void SaveProfile(string path, AiStealthProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    private static bool TryParseTask(string value, out AiStealthTrainingTask task)
    {
        if (string.Equals(value, "remain-undetected", StringComparison.OrdinalIgnoreCase))
        {
            task = AiStealthTrainingTask.RemainUndetected;
            return true;
        }
        if (string.Equals(value, "break-contact", StringComparison.OrdinalIgnoreCase))
        {
            task = AiStealthTrainingTask.BreakContact;
            return true;
        }
        task = default;
        return false;
    }

    private static int Arg(string[] args, string name, int fallback, int min, int max)
    {
        string? value = TextOpt(args, name);
        if (value == null)
            return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < min || parsed > max)
            throw new ArgumentOutOfRangeException(name, $"Expected {min}..{max}.");
        return parsed;
    }

    private static string TextArg(string[] args, string name, string fallback) =>
        TextOpt(args, name) ?? fallback;

    private static string? TextOpt(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value after {name}.");
            return args[i + 1];
        }
        return null;
    }

    private static string F(double value) =>
        value.ToString("F4", CultureInfo.InvariantCulture);

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static void ThrowIfStopped()
    {
        if (_stopRequested)
            throw new OperationCanceledException("Specialized stealth training stop requested.");
    }
}
