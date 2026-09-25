using Core.Items;
using Core.Map;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents;
using Core.Unit.Systems;
using System;

namespace Core.Unit;

public sealed class UnitStore
{
    public int Count;

    public float[] MovementCooldowns;
    public int[] ActiveGridIds;

    public UnitType[] UnitType;
    public UnitPosition[] Positions;
    public UnitSize[] Sizes;
    public UnitMovement[] Movement;

    public float[] SeparationOffsetX;
    public float[] SeparationOffsetY;

    public int[] SquadIds;

    public uint[] HealthMasks;
    public float[] BleedRates;
    public float[] BloodLossLevels;

    public float[] BaseBodyMass;
    public float[] DynamicMass;

    public int[] CurrentTargets;
    public Weapon[] WeaponSlot;

    public ArmorConfig[] HeadArmorSlot;
    public ArmorConfig[] TorsoArmorSlot;
    public ArmorConfig[] ArmsArmorSlot;
    public ArmorConfig[] LegsArmorSlot;

    public float[] ShotCooldowns;
    public float[] RemainingBurstShots;
    public bool[] IsAiming;
    public float[] AimingTimers;
    public int[] LeanOffsetX;
    public int[] LeanOffsetY;
    public ushort[] AttachedCoverEdificeId;

    public bool[] VisionCacheFlags;
    public float[] VisionTickTimers;
    public bool[] IsInCombatMode;
    public float[] AlertTimers;

    public int[] LastGunshotSourceX;
    public int[] LastGunshotSourceY;
    public float[] GunshotInvestigateTimer;

    public int[] LastImpactSourceX;
    public int[] LastImpactSourceY;

    public bool[] HasJustFinishedMoveStep;
    public SpatialCoord[] LastVisitedSourceCell;

    public int[] LastAttackerIds;

    public float[] SuppressionLevels;
    public byte[] UnitAlertFlags;

    public const int MaxAiStackDepth = 4;

    public AiCommand[] AiCommandStacks;
    public int[] AiStackPointers;

    public float[] Irritation;
    public float[] Fear;
    public float[] ThreatLevel;
    public float[] Suppression;

    public UnitBehaviorFlags[] BehaviorFlags;
    public SpatialCoord[] LastKnownEnemyPosition;
    public int[] CurrentGroupId;

    public UnitStore(int maxUnits)
    {
        if (maxUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUnits));

        Allocate(maxUnits);
        InitializeDefaults(maxUnits);
    }

    private void Allocate(int size)
    {
        MovementCooldowns = new float[size];
        ActiveGridIds = new int[size];

        UnitType = new UnitType[size];
        Positions = new UnitPosition[size];
        Sizes = new UnitSize[size];
        Movement = new UnitMovement[size];

        SeparationOffsetX = new float[size];
        SeparationOffsetY = new float[size];

        SquadIds = new int[size];

        HealthMasks = new uint[size];
        BleedRates = new float[size];
        BloodLossLevels = new float[size];

        BaseBodyMass = new float[size];
        DynamicMass = new float[size];

        CurrentTargets = new int[size];
        WeaponSlot = new Weapon[size];

        HeadArmorSlot = new ArmorConfig[size];
        TorsoArmorSlot = new ArmorConfig[size];
        ArmsArmorSlot = new ArmorConfig[size];
        LegsArmorSlot = new ArmorConfig[size];

        ShotCooldowns = new float[size];
        RemainingBurstShots = new float[size];
        IsAiming = new bool[size];
        AimingTimers = new float[size];
        LeanOffsetX = new int[size];
        LeanOffsetY = new int[size];
        AttachedCoverEdificeId = new ushort[size];

        VisionCacheFlags = new bool[size];
        VisionTickTimers = new float[size];
        IsInCombatMode = new bool[size];
        AlertTimers = new float[size];

        LastGunshotSourceX = new int[size];
        LastGunshotSourceY = new int[size];
        GunshotInvestigateTimer = new float[size];

        LastImpactSourceX = new int[size];
        LastImpactSourceY = new int[size];

        HasJustFinishedMoveStep = new bool[size];
        LastVisitedSourceCell = new SpatialCoord[size];

        LastAttackerIds = new int[size];

        SuppressionLevels = new float[size];
        UnitAlertFlags = new byte[size];

        AiCommandStacks = new AiCommand[size * MaxAiStackDepth];
        AiStackPointers = new int[size];

        Irritation = new float[size];
        Fear = new float[size];
        ThreatLevel = new float[size];
        Suppression = new float[size];

        BehaviorFlags = new UnitBehaviorFlags[size];
        LastKnownEnemyPosition = new SpatialCoord[size];
        CurrentGroupId = new int[size];
    }

    private void InitializeDefaults(int size)
    {
        Array.Fill(ActiveGridIds, -1);
        Array.Fill(CurrentTargets, -1);
        Array.Fill(SquadIds, -1);
        Array.Fill(LastAttackerIds, -1);
        Array.Fill(AiStackPointers, -1);
        Array.Fill(CurrentGroupId, -1);
        Array.Fill(BehaviorFlags, UnitBehaviorFlags.Idle);
    }

    public int CreateUnit(
        int mx,
        int my,
        int mz,
        byte width,
        byte height,
        float mass,
        float speed,
        UnitType type,
        UnitSpatialGrid spatialGrid)
    {
        if (spatialGrid == null)
            throw new ArgumentNullException(nameof(spatialGrid));

        if (Count == Positions.Length)
            ResizeStorage(Math.Max(Positions.Length * 2, Count + 100));

        int id = Count++;
        SpatialCoord start = new(mx, my, mz);

        Positions[id] = new UnitPosition(start);
        Sizes[id] = new UnitSize(width, height);

        Movement[id] = new UnitMovement
        {
            SourceCell = start,
            TargetCell = start,
            ZLevel = mz,
            Speed = speed,
            State = MovementState.Idle
        };

        SeparationOffsetX[id] = 0f;
        SeparationOffsetY[id] = 0f;

        UnitType[id] = type;
        BaseBodyMass[id] = mass;
        DynamicMass[id] = mass;

        HealthMasks[id] = uint.MaxValue;

        CurrentTargets[id] = -1;
        SquadIds[id] = -1;
        LastAttackerIds[id] = -1;
        CurrentGroupId[id] = -1;

        LastVisitedSourceCell[id] = start;
        LastKnownEnemyPosition[id] = start;

        BehaviorFlags[id] = UnitBehaviorFlags.Idle;
        AiStackPointers[id] = -1;

        spatialGrid.Add(start, id);

        return id;
    }

    private void ResizeStorage(int newSize)
    {
        int oldSize = Positions.Length;

        Array.Resize(ref MovementCooldowns, newSize);
        Array.Resize(ref ActiveGridIds, newSize);
        Array.Resize(ref UnitType, newSize);
        Array.Resize(ref Positions, newSize);
        Array.Resize(ref Sizes, newSize);
        Array.Resize(ref Movement, newSize);
        Array.Resize(ref SeparationOffsetX, newSize);
        Array.Resize(ref SeparationOffsetY, newSize);
        Array.Resize(ref SquadIds, newSize);

        Array.Resize(ref HealthMasks, newSize);
        Array.Resize(ref BleedRates, newSize);
        Array.Resize(ref BloodLossLevels, newSize);

        Array.Resize(ref BaseBodyMass, newSize);
        Array.Resize(ref DynamicMass, newSize);

        Array.Resize(ref CurrentTargets, newSize);
        Array.Resize(ref WeaponSlot, newSize);

        Array.Resize(ref HeadArmorSlot, newSize);
        Array.Resize(ref TorsoArmorSlot, newSize);
        Array.Resize(ref ArmsArmorSlot, newSize);
        Array.Resize(ref LegsArmorSlot, newSize);

        Array.Resize(ref ShotCooldowns, newSize);
        Array.Resize(ref RemainingBurstShots, newSize);
        Array.Resize(ref IsAiming, newSize);
        Array.Resize(ref AimingTimers, newSize);
        Array.Resize(ref LeanOffsetX, newSize);
        Array.Resize(ref LeanOffsetY, newSize);
        Array.Resize(ref AttachedCoverEdificeId, newSize);

        Array.Resize(ref VisionCacheFlags, newSize);
        Array.Resize(ref VisionTickTimers, newSize);
        Array.Resize(ref IsInCombatMode, newSize);
        Array.Resize(ref AlertTimers, newSize);

        Array.Resize(ref LastGunshotSourceX, newSize);
        Array.Resize(ref LastGunshotSourceY, newSize);
        Array.Resize(ref GunshotInvestigateTimer, newSize);

        Array.Resize(ref LastImpactSourceX, newSize);
        Array.Resize(ref LastImpactSourceY, newSize);

        Array.Resize(ref HasJustFinishedMoveStep, newSize);
        Array.Resize(ref LastVisitedSourceCell, newSize);

        Array.Resize(ref LastAttackerIds, newSize);

        Array.Resize(ref SuppressionLevels, newSize);
        Array.Resize(ref UnitAlertFlags, newSize);

        Array.Resize(ref AiStackPointers, newSize);
        Array.Resize(ref AiCommandStacks, newSize * MaxAiStackDepth);

        Array.Resize(ref Irritation, newSize);
        Array.Resize(ref Fear, newSize);
        Array.Resize(ref ThreatLevel, newSize);
        Array.Resize(ref Suppression, newSize);

        Array.Resize(ref BehaviorFlags, newSize);
        Array.Resize(ref LastKnownEnemyPosition, newSize);
        Array.Resize(ref CurrentGroupId, newSize);

        int added = newSize - oldSize;

        Array.Fill(ActiveGridIds, -1, oldSize, added);
        Array.Fill(CurrentTargets, -1, oldSize, added);
        Array.Fill(SquadIds, -1, oldSize, added);
        Array.Fill(LastAttackerIds, -1, oldSize, added);
        Array.Fill(AiStackPointers, -1, oldSize, added);
        Array.Fill(CurrentGroupId, -1, oldSize, added);
        Array.Fill(BehaviorFlags, UnitBehaviorFlags.Idle, oldSize, added);
    }

    public void UpdateHealthSystems(float deltaTime)
    {
        for (int i = 0; i < Count; i++)
        {
            if (HealthMasks[i] == 0)
                continue;

            HealthSystem.UpdateTick(
                ref HealthMasks[i],
                ref BleedRates[i],
                ref BloodLossLevels[i],
                deltaTime);

            if (HealthMasks[i] != 0)
                continue;

            CurrentTargets[i] = -1;
            SquadIds[i] = -1;
            CurrentGroupId[i] = -1;
            BehaviorFlags[i] = UnitBehaviorFlags.None;

            SeparationOffsetX[i] = 0f;
            SeparationOffsetY[i] = 0f;
        }
    }

    public void CpuPushCommand(int unitId, AiCommand command)
    {
        int count = AiStackPointers[unitId] + 1;

        if (count >= MaxAiStackDepth)
            return;

        int start = unitId * MaxAiStackDepth;

        for (int i = count; i > 0; i--)
            AiCommandStacks[start + i] =
                AiCommandStacks[start + i - 1];

        AiCommandStacks[start] = command;
        AiStackPointers[unitId] = count;
    }

    public void CpuAppendCommand(
        int unitId,
        AiCommand command)
    {
        int count =
            AiStackPointers[unitId] + 1;

        if (count >= MaxAiStackDepth)
            return;

        AiCommandStacks[
            unitId * MaxAiStackDepth + count] =
            command;

        AiStackPointers[unitId] =
            count;
    }

    public void CpuClearCommands(
        int unitId)
    {
        int count =
            AiStackPointers[unitId] + 1;

        if (count <= 0)
            return;

        int start =
            unitId * MaxAiStackDepth;

        for (int i = 0;
             i < count;
             i++)
        {
            AiCommandStacks[start + i] =
                default;
        }

        AiStackPointers[unitId] =
            -1;
    }

    public void CpuPopCommand(
        int unitId)
    {
        int count =
            AiStackPointers[unitId] + 1;

        if (count <= 0)
            return;

        int start =
            unitId * MaxAiStackDepth;

        for (int i = 1;
             i < count;
             i++)
        {
            AiCommandStacks[start + i - 1] =
                AiCommandStacks[start + i];
        }

        AiCommandStacks[start + count - 1] =
            default;

        AiStackPointers[unitId] =
            count - 2;
    }

    public ref AiCommand CpuPeekCommand(
        int unitId)
    {
        return ref AiCommandStacks[
            unitId * MaxAiStackDepth];
    }

    [Flags]
    public enum UnitBehaviorFlags : ushort
    {
        None = 0,
        Idle = 1 << 0,
        Alert = 1 << 1,
        Suppressed = 1 << 2,
        Retreating = 1 << 3,
        Advancing = 1 << 4,
        Assault = 1 << 5,
        TakingCover = 1 << 6,
        HoldingPosition = 1 << 7,
        Investigating = 1 << 8,
        FollowingGroupOrder = 1 << 9,
        IndividualOverride = 1 << 10
    }
}