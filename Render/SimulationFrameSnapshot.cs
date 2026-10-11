using System;
using System.Numerics;
using System.Collections.Generic;
using Core.Items;
using Core.Unit;

namespace RimClone.Render;

public readonly record struct RenderWeaponVisual(
    WeaponConfig? Config,
    Vector3 AimPoint,
    bool AimIndicatorActive);

public readonly record struct RenderUnitState(
    int Index,
    uint Generation,
    Vector3 Position,
    Vector3 BodyNormal,
    Vector3 HeadNormal,
    float Width,
    float Length,
    float HitPoints,
    float HealthRatio,
    ushort FactionTag,
    UnitAiState AiState,
    bool HasTarget,
    Vector3 TargetPosition,
    bool HasGoal,
    Vector3 Goal,
    float Suppression,
    UnitSuppressionState SuppressionState,
    bool HasApparel,
    bool HasPoweredArmor,
    RenderWeaponVisual PrimaryWeapon,
    RenderWeaponVisual SecondaryWeapon);

public readonly record struct RenderAimRing(Vector3 AimPoint, float Progress);

public readonly record struct RenderProjectileTrace(
    Vector3 Start,
    Vector3 End,
    ushort Faction,
    bool IsLaser,
    float Lifetime);

public readonly record struct RenderProjectileImpact(
    Vector3 Position,
    float Lifetime,
    ProjectileImpactKind Kind);

public readonly record struct RenderHitVolume(
    UnitHealthPartId Part,
    Vector3 Center,
    Vector3 Forward,
    Vector3 Right,
    Vector3 Radii,
    Vector3 InspectionCenter,
    float InspectionRotation);

/// <summary>
/// Immutable data published by the simulation thread and consumed by the renderer.
/// No mutable simulation-store arrays escape this object.
/// </summary>
public sealed class SimulationFrameSnapshot
{
    public RenderUnitState[] Units { get; init; } = Array.Empty<RenderUnitState>();
    public RenderProjectileTrace[] Traces { get; init; } = Array.Empty<RenderProjectileTrace>();
    public RenderAimRing[] AimRings { get; init; } = Array.Empty<RenderAimRing>();
    public RenderProjectileImpact[] Impacts { get; init; } = Array.Empty<RenderProjectileImpact>();
    public RenderHitVolume[] HitVolumes { get; init; } = Array.Empty<RenderHitVolume>();
    public int HitVolumeUnitIndex { get; init; } = -1;

    public int UnitCount => Units.Length;
    public int ActiveProjectiles { get; init; }
    public int ProjectileCapacity { get; init; }
    public long TotalShotsFired { get; init; }
    public long TotalRoundsConsumed { get; init; }
    public long TotalProjectilesSpawned { get; init; }
    public long TotalHits { get; init; }
    public string LastHitPart { get; init; } = "None";

    public bool AiEnabled { get; init; }
    public bool VisionEnabled { get; init; }
    public int BlueAlive { get; init; }
    public int BlueDead { get; init; }
    public int RedAlive { get; init; }
    public int RedDead { get; init; }
    public int AttackCount { get; init; }
    public int SearchCount { get; init; }
    public int CoverCount { get; init; }
    public int IdleCount { get; init; }
    public int DeadCount { get; init; }
    public int SuppressedCount { get; init; }
    public int PanickedCount { get; init; }

    public double SimulationMs { get; init; }
    public double VisionMs { get; init; }
    public long VisionCandidatePairs { get; init; }
    public long VisionTargetEvaluations { get; init; }
    public long VisionLineOfSightChecks { get; init; }
    public long VisionCandidatesScanned { get; init; }
    public long VisionMemoryEntriesScanned { get; init; }
    public double AiMs { get; init; }
    public double MovementMs { get; init; }
    public double WeaponsMs { get; init; }
    public double BallisticsMs { get; init; }
    public double ProjectileGridMs { get; init; }
    public int ProjectilesVisited { get; init; }
    public int TerrainCellsTraced { get; init; }
    public int ProjectileUnitCandidates { get; init; }
    public double HealthMs { get; init; }
    public int RoutesBuilt { get; init; }
    public int RoutesFailed { get; init; }
    public int NavigationSearches { get; init; }
    public long NavigationCellsExpanded { get; init; }
    public double NavigationSearchMs { get; init; }
    public double NavigationGridMs { get; init; }
    public int RoutesReused { get; init; }
    public long RouteTrafficCells { get; init; }

    private static RenderWeaponVisual CaptureWeaponVisual(
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        int unitIndex,
        UnitWeaponSlot slot)
    {
        short itemSlot = inventory.GetWeaponEquipment(unitIndex, slot);
        WeaponConfig? config = itemSlot >= 0
            ? inventory.GetItem(unitIndex, itemSlot) as WeaponConfig
            : null;

        int stateIndex = UnitWeaponStore.GetIndex(unitIndex, slot);
        return new RenderWeaponVisual(
            config,
            weapons.AimPoint[stateIndex],
            weapons.AimIndicatorActive[stateIndex]);
    }

    public static SimulationFrameSnapshot Capture(
        UnitSimulation simulation,
        int hitVolumeUnitIndex = -1)
    {
        UnitStore units = simulation.Units;
        ReadOnlySpan<int> active = units.ActiveIndices;
        RenderUnitState[] unitStates = new RenderUnitState[active.Length];

        int blueAlive = 0;
        int blueDead = 0;
        int redAlive = 0;
        int redDead = 0;
        int attack = 0;
        int search = 0;
        int cover = 0;
        int idle = 0;
        int dead = 0;
        int suppressed = 0;
        int panicked = 0;

        float[] hitPoints = simulation.Health.OverallHitPoints;
        float[] maxHitPoints = simulation.Health.OverallMaxHitPoints;
        ushort[] factions = units.FactionTag;
        Vector3[] positions = units.Position;
        float[] widths = units.Width;
        float[] lengths = units.Length;
        Vector3[] bodyNormals = units.BodyNormal;
        Vector3[] headNormals = units.HeadNormal;
        UnitAiStore aiStore = simulation.AI.Store;
        UnitAiState[] aiStates = aiStore.State;
        UnitInventoryStore inventoryStore = simulation.Inventory;
        UnitSuppressionStore suppressionStore = simulation.Suppression;
        UnitWeaponStore weaponStore = simulation.Weapons;

        for (int i = 0; i < active.Length; i++)
        {
            int index = active[i];
            float hp = hitPoints[index];
            float healthRatio = maxHitPoints[index] > 0f
                ? hp / maxHitPoints[index]
                : 0f;
            ushort faction = factions[index];
            UnitAiState aiState = aiStates[index];

            bool hasApparel = false;
            bool hasPoweredArmor = false;
            for (int zone = 0; zone < UnitInventoryStore.BodyZoneCount; zone++)
            {
                for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
                {
                    short itemSlot = inventoryStore.GetBodyEquipment(
                        index,
                        zone,
                        (ArmorLayer)layer);

                    if (itemSlot < 0 ||
                        inventoryStore.GetItem(index, itemSlot) is not ArmorConfig armor)
                    {
                        continue;
                    }

                    hasApparel = true;
                    hasPoweredArmor |= armor.MovementSpeedMultiplier > 1f;
                }
            }

            RenderWeaponVisual primaryWeapon = CaptureWeaponVisual(
                inventoryStore, weaponStore, index, UnitWeaponSlot.Primary);
            RenderWeaponVisual secondaryWeapon = CaptureWeaponVisual(
                inventoryStore, weaponStore, index, UnitWeaponSlot.Secondary);

            bool hasTarget = false;
            Vector3 targetPosition = Vector3.Zero;
            if (aiStore.HasTarget[index] &&
                units.TryGetIndex(aiStore.Target[index], out int targetIndex))
            {
                hasTarget = true;
                targetPosition = positions[targetIndex];
            }

            bool hasGoal = aiStore.HasGoal[index];
            Vector3 goal = aiStore.Goal[index];
            float suppression = suppressionStore.Value[index];
            UnitSuppressionState suppressionState = suppressionStore.GetState(index);

            unitStates[i] = new RenderUnitState(
                index,
                units.GetId(index).Generation,
                positions[index],
                bodyNormals[index],
                headNormals[index],
                widths[index],
                lengths[index],
                hp,
                healthRatio,
                faction,
                aiState,
                hasTarget,
                targetPosition,
                hasGoal,
                goal,
                suppression,
                suppressionState,
                hasApparel,
                hasPoweredArmor,
                primaryWeapon,
                secondaryWeapon);

            bool alive = hp > 0f;
            if (faction == 1)
            {
                if (alive) blueAlive++;
                else blueDead++;
            }
            else if (faction == 2)
            {
                if (alive) redAlive++;
                else redDead++;
            }

            switch (aiState)
            {
                case UnitAiState.Attack: attack++; break;
                case UnitAiState.Search: search++; break;
                case UnitAiState.SeekCover: cover++; break;
                case UnitAiState.Dead: dead++; break;
                default: idle++; break;
            }

            switch (simulation.Suppression.GetState(index))
            {
                case UnitSuppressionState.Suppressed: suppressed++; break;
                case UnitSuppressionState.Panicked: panicked++; break;
            }
        }

        RenderHitVolume[] hitVolumes = Array.Empty<RenderHitVolume>();
        int capturedHitVolumeUnitIndex = -1;
        Span<HitVolume> hitVolumeBuffer =
            stackalloc HitVolume[UnitHitSystem.MaxHitVolumeCount];

        if (hitVolumeUnitIndex >= 0)
        {
            for (int i = 0; i < active.Length; i++)
            {
                int unitIndex = active[i];
                if (unitIndex != hitVolumeUnitIndex)
                    continue;

                int volumeCount = UnitHitSystem.GetHitVolumes(
                    units,
                    unitIndex,
                    hitVolumeBuffer,
                    simulation.Bodies);

                if (volumeCount > 0)
                {
                    hitVolumes = new RenderHitVolume[volumeCount];
                    for (int part = 0; part < volumeCount; part++)
                    {
                        ref readonly HitVolume volume = ref hitVolumeBuffer[part];
                        hitVolumes[part] = new RenderHitVolume(
                            volume.Part,
                            volume.Center,
                            volume.Forward,
                            volume.Right,
                            volume.Radii,
                            volume.InspectionCenter,
                            volume.InspectionRotation);
                    }

                    capturedHitVolumeUnitIndex = unitIndex;
                }

                break;
            }
        }

        List<RenderAimRing> aimRings = new(Math.Min(active.Length, 128));
        bool[] targetDrawn = new bool[units.Capacity];
        UnitInventoryStore inventory = simulation.Inventory;
        UnitWeaponStore weaponStates = simulation.Weapons;

        for (int i = 0; i < active.Length; i++)
        {
            int shooterIndex = active[i];
            for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
            {
                UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
                int stateIndex = UnitWeaponStore.GetIndex(shooterIndex, weaponSlot);

                if (!weaponStates.AimIndicatorActive[stateIndex] ||
                    weaponStates.AimTimer[stateIndex] <= 0f)
                {
                    continue;
                }

                UnitId targetId = weaponStates.AimTarget[stateIndex];
                if (!units.TryGetIndex(targetId, out int targetIndex) ||
                    targetDrawn[targetIndex] ||
                    hitPoints[targetIndex] <= 0f)
                {
                    continue;
                }

                short inventorySlot = inventory.GetWeaponEquipment(shooterIndex, weaponSlot);
                if (inventorySlot < 0 ||
                    inventory.GetItem(shooterIndex, inventorySlot) is not RangedWeaponConfig weapon)
                {
                    continue;
                }

                Vector3 aimPoint = weaponStates.AimPoint[stateIndex];
                if (aimPoint == Vector3.Zero)
                    continue;

                Vector3 shooterPosition = positions[shooterIndex];
                float dx = aimPoint.X - shooterPosition.X;
                float dy = aimPoint.Y - shooterPosition.Y;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                float duration = UnitWeaponSystem.GetAimDuration(
                    weapon,
                    weaponStates.CurrentAimMode[stateIndex],
                    distance);
                if (duration <= 0f)
                    continue;

                float progress = Math.Clamp(
                    weaponStates.AimTimer[stateIndex] / duration,
                    0f,
                    1f);

                aimRings.Add(new RenderAimRing(aimPoint, progress));
                targetDrawn[targetIndex] = true;
                break;
            }
        }

        ProjectileStore projectiles = simulation.Projectiles;
        Vector3[] traceStarts = projectiles.TraceStarts;
        Vector3[] traceEnds = projectiles.TraceEnds;
        ushort[] traceFactions = projectiles.TraceFactions;
        bool[] traceLasers = projectiles.TraceLasers;
        float[] traceLifetimes = projectiles.TraceLifetimes;

        int traceCount = 0;
        for (int i = 0; i < projectiles.TraceCapacityCount; i++)
        {
            if (traceLifetimes[i] > 0f)
                traceCount++;
        }

        RenderProjectileTrace[] traces = new RenderProjectileTrace[traceCount];
        int traceWrite = 0;
        for (int i = 0; i < projectiles.TraceCapacityCount; i++)
        {
            float lifetime = traceLifetimes[i];
            if (lifetime <= 0f)
                continue;

            traces[traceWrite++] = new RenderProjectileTrace(
                traceStarts[i],
                traceEnds[i],
                traceFactions[i],
                traceLasers[i],
                lifetime);
        }

        Vector3[] impactPositions = projectiles.ImpactPositions;
        float[] impactLifetimes = projectiles.ImpactLifetimes;
        ProjectileImpactKind[] impactKinds = projectiles.ImpactKinds;

        int impactCount = 0;
        for (int i = 0; i < projectiles.ImpactCapacityCount; i++)
        {
            if (impactLifetimes[i] > 0f)
                impactCount++;
        }

        RenderProjectileImpact[] impacts = new RenderProjectileImpact[impactCount];
        int impactWrite = 0;
        for (int i = 0; i < projectiles.ImpactCapacityCount; i++)
        {
            float lifetime = impactLifetimes[i];
            if (lifetime <= 0f)
                continue;

            impacts[impactWrite++] = new RenderProjectileImpact(
                impactPositions[i],
                lifetime,
                impactKinds[i]);
        }

        VisionSystem vision = simulation.Vision;
        UnitNavigationSystem navigation = simulation.Navigation;

        return new SimulationFrameSnapshot
        {
            Units = unitStates,
            Traces = traces,
            AimRings = aimRings.ToArray(),
            Impacts = impacts,
            HitVolumes = hitVolumes,
            HitVolumeUnitIndex = capturedHitVolumeUnitIndex,
            ActiveProjectiles = projectiles.ActiveCount,
            ProjectileCapacity = projectiles.Capacity,
            TotalShotsFired = simulation.TotalShotsFired,
            TotalRoundsConsumed = simulation.TotalRoundsConsumed,
            TotalProjectilesSpawned = simulation.TotalProjectilesSpawned,
            TotalHits = projectiles.TotalHits,
            LastHitPart = projectiles.LastHitPart.ToString(),
            AiEnabled = simulation.AI.Enabled,
            VisionEnabled = simulation.VisionEnabled,
            BlueAlive = blueAlive,
            BlueDead = blueDead,
            RedAlive = redAlive,
            RedDead = redDead,
            AttackCount = attack,
            SearchCount = search,
            CoverCount = cover,
            IdleCount = idle,
            DeadCount = dead,
            SuppressedCount = suppressed,
            PanickedCount = panicked,
            SimulationMs = simulation.LastSimulationUpdateMilliseconds,
            VisionMs = vision.LastUpdateMilliseconds,
            VisionCandidatePairs = vision.LastCandidatePairs,
            VisionTargetEvaluations = vision.LastTargetEvaluations,
            VisionLineOfSightChecks = vision.LastLineOfSightChecks,
            VisionCandidatesScanned = vision.LastActiveCandidatesScanned,
            VisionMemoryEntriesScanned = vision.LastVisibilityMemoryEntriesScanned,
            AiMs = simulation.LastAIUpdateMilliseconds,
            MovementMs = simulation.LastMovementUpdateMilliseconds,
            WeaponsMs = simulation.LastWeaponUpdateMilliseconds,
            BallisticsMs = simulation.LastProjectileUpdateMilliseconds,
            ProjectileGridMs = simulation.LastProjectileGridBuildMilliseconds,
            ProjectilesVisited = simulation.LastProjectilesVisited,
            TerrainCellsTraced = simulation.LastProjectileTerrainCellsTraced,
            ProjectileUnitCandidates = simulation.LastProjectileUnitCandidates,
            HealthMs = simulation.LastHealthUpdateMilliseconds,
            RoutesBuilt = navigation.RoutesBuiltThisUpdate,
            RoutesFailed = navigation.RoutesFailedThisUpdate,
            NavigationSearches = navigation.SearchesThisUpdate,
            NavigationCellsExpanded = navigation.CellsExpandedThisUpdate,
            NavigationSearchMs = navigation.SearchMillisecondsThisUpdate,
            NavigationGridMs = navigation.NavigationGridBuildMilliseconds,
            RoutesReused = navigation.RoutesBuiltUsingPriorRoutesThisUpdate,
            RouteTrafficCells = navigation.RouteTrafficCellsConsideredThisUpdate
        };
    }
}
