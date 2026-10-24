using System.Numerics;
using Core.AI;
using Core.AI.Behaviac;
using Core.AI.Perception;
using Core.Items;
using Core.Map;
using Core.Unit;

namespace RimClone.AiLab;

/// <summary>
/// Runs one isolated episode on the real UnitSimulation update loop. No renderer,
/// global random state, shared UnitSimulation, or shared WorldMap is used by worker jobs.
/// </summary>
public sealed class BattleEpisodeRunner
{
    private const float Dt = 0.05f;
    private const ushort GroundMaterial = 1;

    public EpisodeMetrics Run(
        LabConfiguration configuration,
        LabExperimentDefinition definition,
        TacticalGenome genome,
        LabScenarioKind scenario,
        int seed,
        bool candidateStartedLeft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(genome);

        var world = new WorldMap(regionsX: 4, regionsY: 4, layerCount: 32);
        BuildGround(world);
        BuildScenario(world, scenario, seed);

        var simulation = new UnitSimulation(
            unitCapacity: configuration.TeamSize * 2 + 8,
            bodyCapacity: 2048);

        AiBalanceProfile candidateProfile = genome.ApplyTo(configuration.BaselineProfile);
        simulation.AI.BalanceProfile = configuration.BaselineProfile;
        simulation.AI.SetBalanceProfileForFaction(
            1, candidateStartedLeft ? candidateProfile : configuration.BaselineProfile);
        simulation.AI.SetBalanceProfileForFaction(
            2, candidateStartedLeft ? configuration.BaselineProfile : candidateProfile);

        List<UnitId> left = SpawnTeam(
            simulation, world, isLeft: true, faction: 1, configuration, scenario, seed,
            isCandidate: candidateStartedLeft);
        List<UnitId> right = SpawnTeam(
            simulation, world, isLeft: false, faction: 2, configuration, scenario, seed,
            isCandidate: !candidateStartedLeft);

        List<UnitId> candidate = candidateStartedLeft ? left : right;
        List<UnitId> enemy = candidateStartedLeft ? right : left;

        simulation.AI.SynchronizeSquadMemberships(
        [
            new SquadMembershipSnapshot(1, left),
            new SquadMembershipSnapshot(2, right)
        ]);
        simulation.AI.Enabled = true;

        var firstDetectedAt = new float[candidate.Count];
        var lastVisibleAt = new float[candidate.Count];
        var everDetected = new bool[candidate.Count];
        var breakRecorded = new bool[candidate.Count];
        Array.Fill(firstDetectedAt, float.NaN);

        double elapsed = 0d;
        double visibleUnitSeconds = 0d;
        int detectedCount = 0;
        int brokenContactCount = 0;
        double breakLatencyTotal = 0d;
        double candidateMaximumHp = CaptureMaximumHealth(simulation, candidate);
        double enemyMaximumHp = CaptureMaximumHealth(simulation, enemy);
        TeamSnapshot own = MeasureTeam(simulation, candidate, candidateMaximumHp);
        TeamSnapshot other = MeasureTeam(simulation, enemy, enemyMaximumHp);

        for (int tick = 0; tick < configuration.SimulationTicks; tick++)
        {
            if ((tick & 15) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            simulation.Update(world, Dt);
            elapsed += Dt;

            for (int i = 0; i < candidate.Count; i++)
            {
                bool visible = IsVisuallyConfirmedByEnemy(
                    simulation, candidate[i], enemy, (float)elapsed);

                if (visible)
                {
                    if (!everDetected[i])
                    {
                        everDetected[i] = true;
                        firstDetectedAt[i] = (float)elapsed;
                        detectedCount++;
                    }

                    lastVisibleAt[i] = (float)elapsed;
                    visibleUnitSeconds += Dt;
                }
                else if (everDetected[i] && !breakRecorded[i] &&
                         elapsed - lastVisibleAt[i] >= TacticalContactMemory.VisualConfirmationGraceSeconds + Dt)
                {
                    breakRecorded[i] = true;
                    brokenContactCount++;
                    breakLatencyTotal += Math.Max(0d, elapsed - firstDetectedAt[i]);
                }
            }

            own = MeasureTeam(simulation, candidate, candidateMaximumHp);
            other = MeasureTeam(simulation, enemy, enemyMaximumHp);
            if (own.Alive == 0 || other.Alive == 0)
                break;
        }

        double exposureRatio = Math.Clamp(
            visibleUnitSeconds / Math.Max(Dt, elapsed * candidate.Count), 0d, 1d);
        double detectionRate = detectedCount / (double)Math.Max(1, candidate.Count);
        double breakRate = brokenContactCount / (double)Math.Max(1, detectedCount);
        double meanBreakSeconds = brokenContactCount == 0
            ? elapsed
            : breakLatencyTotal / brokenContactCount;

        int outcome = own.Alive == other.Alive
            ? (own.HealthRatio > other.HealthRatio + 0.03d ? 1 :
               own.HealthRatio + 0.03d < other.HealthRatio ? -1 : 0)
            : own.Alive > other.Alive ? 1 : -1;

        double outcomeScore = outcome > 0 ? 1d : outcome < 0 ? 0d : 0.5d;
        double combatFitness =
            45d * outcomeScore +
            20d * own.HealthRatio +
            20d * (1d - other.HealthRatio) +
            15d * own.SurvivorRate;

        double fitness = definition.Kind switch
        {
            LabExperimentKind.RemainUndetected =>
                65d * (1d - exposureRatio) +
                20d * own.SurvivorRate +
                15d * own.HealthRatio,

            LabExperimentKind.BreakContact =>
                40d * breakRate +
                25d * (1d - exposureRatio) +
                20d * own.SurvivorRate +
                10d * own.HealthRatio +
                5d * (1d - other.HealthRatio),

            LabExperimentKind.WeaponSpecialization =>
                0.85d * combatFitness +
                15d * Math.Clamp(other.Deaths / (double)Math.Max(1, configuration.TeamSize), 0d, 1d),

            LabExperimentKind.SquadDoctrine =>
                0.90d * combatFitness +
                10d * own.ActionDiversity,

            _ => combatFitness
        };

        double activity = Math.Clamp(
            simulation.TotalShotsFired / Math.Max(1d, elapsed * configuration.TeamSize * 0.30d),
            0d, 1d);

        return new EpisodeMetrics(
            scenario,
            seed,
            candidateStartedLeft,
            fitness,
            outcome,
            own.SurvivorRate,
            own.HealthRatio,
            other.HealthRatio,
            detectionRate,
            exposureRatio,
            breakRate,
            meanBreakSeconds,
            activity,
            own.ActionDiversity,
            own.Deaths,
            other.Deaths,
            simulation.TotalShotsFired,
            simulation.TotalRoundsConsumed,
            elapsed);
    }

    private static bool IsVisuallyConfirmedByEnemy(
        UnitSimulation simulation,
        UnitId target,
        IReadOnlyList<UnitId> enemies,
        float now)
    {
        for (int i = 0; i < enemies.Count; i++)
        {
            if (!simulation.Units.TryGetIndex(enemies[i], out int observerIndex) ||
                !simulation.PerceptionMemory.TryGetMemory(observerIndex, out TacticalContactMemory memory) ||
                !memory.TryGetContact(target, out TacticalContact contact))
            {
                continue;
            }

            if (contact.Kind == TacticalContactKind.VisualConfirmed &&
                now - contact.LastVisualAtSeconds < TacticalContactMemory.VisualConfirmationGraceSeconds)
            {
                return true;
            }
        }

        return false;
    }

    private static double CaptureMaximumHealth(UnitSimulation simulation, IReadOnlyList<UnitId> roster)
    {
        double total = 0d;
        foreach (UnitId id in roster)
            total += Math.Max(0f, simulation.Health.OverallMaxHitPoints[id.Index]);
        return total;
    }

    private static TeamSnapshot MeasureTeam(
        UnitSimulation simulation, IReadOnlyList<UnitId> roster, double startingMaximumHp)
    {
        int alive = 0;
        double currentHp = 0d;
        var intents = new HashSet<byte>();

        foreach (UnitId id in roster)
        {
            int index = id.Index;
            bool active = simulation.Units.TryGetIndex(id, out _);
            float current = active
                ? Math.Clamp(simulation.Health.OverallHitPoints[index], 0f,
                    MathF.Max(0f, simulation.Health.OverallMaxHitPoints[index]))
                : 0f;

            if (active && current > 0f)
            {
                alive++;
                intents.Add((byte)simulation.AI.Store.UtilityIntent[index]);
            }

            currentHp += current;
        }

        int distinctLimit = Math.Max(1, Math.Min(4, roster.Count));
        double diversity = Math.Clamp((intents.Count - 1d) / Math.Max(1, distinctLimit - 1), 0d, 1d);
        return new TeamSnapshot(
            roster.Count,
            alive,
            startingMaximumHp > 0d ? Math.Clamp(currentHp / startingMaximumHp, 0d, 1d) : 0d,
            alive / (double)Math.Max(1, roster.Count),
            roster.Count - alive,
            diversity);
    }

    private static List<UnitId> SpawnTeam(
        UnitSimulation simulation,
        WorldMap world,
        bool isLeft,
        ushort faction,
        LabConfiguration configuration,
        LabScenarioKind scenario,
        int seed,
        bool isCandidate)
    {
        var roster = new List<UnitId>(configuration.TeamSize);
        var positionRandom = new Random(unchecked(seed ^ (isLeft ? 0x45D9F3B : 0x119DE1F3)));
        var equipmentRandom = new Random(unchecked(seed ^ (int)faction * 7919));
        int meleeIndex = configuration.Experiment == LabExperimentKind.WeaponSpecialization
            ? -1
            : equipmentRandom.Next(configuration.TeamSize);
        int width = world.TileWidth;
        int height = world.TileHeight;
        int x = scenario switch
        {
            LabScenarioKind.CloseStart => isLeft ? 47 : 81,
            LabScenarioKind.Barrier => isLeft ? 23 : width - 24,
            _ => isLeft ? 14 : width - 15
        };

        for (int i = 0; i < configuration.TeamSize; i++)
        {
            int margin = 12;
            int usable = Math.Max(1, height - margin * 2 - 1);
            int y = margin + i * usable / Math.Max(1, configuration.TeamSize - 1);
            y = Math.Clamp(y + positionRandom.Next(-1, 2), 2, height - 3);
            int unitX = Math.Clamp(x + positionRandom.Next(-1, 2), 2, width - 3);
            float surface = world.GetSurfaceHeight(unitX, y);

            UnitId id = simulation.Spawn(
                UnitType.Colonist,
                new Vector3(unitX, y, surface),
                bodyAngle: isLeft ? 0f : MathF.PI,
                factionTag: faction);
            roster.Add(id);

            if (i == meleeIndex)
            {
                int slot = simulation.AddInventoryItem(id, MeleeWeaponCatalog.CombatKnife);
                if (slot < 0 || !simulation.EquipWeapon(id, slot, UnitWeaponSlot.Melee))
                    throw new InvalidOperationException("Could not equip the training melee unit.");
                continue;
            }

            WeaponConfig weapon = SelectWeapon(configuration, equipmentRandom, isCandidate);
            int weaponSlot = simulation.AddInventoryItem(id, weapon);
            if (weaponSlot < 0 || !simulation.EquipWeapon(id, weaponSlot, UnitWeaponSlot.Primary))
                throw new InvalidOperationException($"Could not equip training weapon '{weapon.Name}'.");
        }

        return roster;
    }

    private static WeaponConfig SelectWeapon(
        LabConfiguration configuration,
        Random random,
        bool isCandidate)
    {
        if (isCandidate && configuration.Experiment == LabExperimentKind.WeaponSpecialization)
        {
            return configuration.Weapon switch
            {
                LabWeaponPreset.Pistol => WeaponCatalog.Pistol,
                _ => WeaponCatalog.AssaultRifle
            };
        }

        return random.Next(3) == 0 ? WeaponCatalog.Pistol : WeaponCatalog.AssaultRifle;
    }

    private static void BuildGround(WorldMap world)
    {
        for (int y = 0; y < world.TileHeight; y++)
        for (int x = 0; x < world.TileWidth; x++)
            world.SetSolidHeight(x, y, 1, GroundMaterial);
    }

    private static void BuildScenario(WorldMap world, LabScenarioKind scenario, int seed)
    {
        var random = new Random(unchecked(seed ^ 0x6D2B79F5));

        if (scenario == LabScenarioKind.Barrier)
        {
            int wallX = world.TileWidth / 2;
            int gapA = world.TileHeight / 3 + random.Next(-3, 4);
            int gapB = world.TileHeight * 2 / 3 + random.Next(-3, 4);

            for (int y = 7; y < world.TileHeight - 7; y++)
            {
                if (Math.Abs(y - gapA) <= 4 || Math.Abs(y - gapB) <= 4)
                    continue;
                world.SetSolidHeight(wallX, y, 20, GroundMaterial);
                world.SetNavigationBlocked(wallX, y, true);
            }
        }
        else if (scenario == LabScenarioKind.BrokenGround)
        {
            AddObstacle(world, 39, 20, 45, 47, 12);
            AddObstacle(world, 78, 77, 84, 106, 12);
            AddObstacle(world, 56, 50, 60, 74, 8);
            // Open corridors are intentionally left between obstacles.
        }
    }

    private static void AddObstacle(
        WorldMap world, int x0, int y0, int x1, int y1, ushort height)
    {
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            world.SetSolidHeight(x, y, height, GroundMaterial);
            world.SetNavigationBlocked(x, y, true);
        }
    }

    private readonly record struct TeamSnapshot(
        int Total,
        int Alive,
        double HealthRatio,
        double SurvivorRate,
        int Deaths,
        double ActionDiversity);
}
