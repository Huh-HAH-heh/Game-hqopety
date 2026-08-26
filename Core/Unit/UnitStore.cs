//using Core.AI.UnitAI;
using Core.Items;
using Core.Map;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents.Core.Unit.Components;
using Core.Unit.Systems;
using System;

namespace Core.Unit
{
    public sealed class UnitStore
    {
        // ============================================================
        // GENERAL / SIMULATION DATA
        // ============================================================
        public int Count;

        public float[] MovementCooldowns;
        public int[] ActiveGridIds;

        public UnitType[] UnitType;
        public UnitPosition[] Positions;
        public UnitSize[] Sizes;
        public UnitMovement[] Movement;

        public int[] SquadIds;

        // ============================================================
        // HEALTH
        // ============================================================

        // [Head][Torso][Arms][Legs] по 8 бит.
        public uint[] HealthMasks;

        public float[] BleedRates;
        public float[] BloodLossLevels;

        // ============================================================
        // MASS
        // ============================================================

        public float[] BaseBodyMass;
        public float[] DynamicMass;

        // ============================================================
        // COMBAT
        // ============================================================

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

        // ============================================================
        // LEGACY / SENSOR DATA
        // Пока оставляем как raw sensor data.
        // ============================================================

        public bool[] VisionCacheFlags;
        public float[] VisionTickTimers;

        public bool[] IsInCombatMode;
        public float[] AlertTimers;

        public int[] LastGunshotSourceX;
        public int[] LastGunshotSourceY;
        public float[] GunshotInvestigateTimer;

        public int[] LastImpactSourceX;
        public int[] LastImpactSourceY;

        // ============================================================
        // MOVEMENT EVENTS
        // ============================================================

        public bool[] HasJustFinishedMoveStep;
        public SpatialCoord[] LastVisitedSourceCell;

        // ============================================================
        // DAMAGE EVENTS
        // ============================================================

        public int[] LastAttackerIds;

        // ============================================================
        // OLD SUPPRESSION DATA
        // Оставлено ради совместимости боевой системы.
        // Новая AI использует Suppression[].
        // ============================================================

        public float[] SuppressionLevels;

        public byte[] UnitAlertFlags;

        // ============================================================
        // COMMAND STACK
        // ============================================================

        public const int MaxAiStackDepth = 4;

        public AiCommand[] AiCommandStacks;
        public int[] AiStackPointers;

        // ============================================================
        // NEW UNIT AI DATA
        // ============================================================
        //
        // Ссылка на навигационный менеджер внутри хранилища компонентов
        //
        public Core.AI.GroupMovementManager GroupMovementManager;





        // Накопительное раздражение.
        public float[] Irritation;

        // Страх.
        public float[] Fear;

        // Воспринимаемый уровень угрозы.
        public float[] ThreatLevel;

        // Новое DOD-состояние подавления.
        public float[] Suppression;

        // Набор текущих behavior flags.
        public UnitBehaviorFlags[] BehaviorFlags;

        // Последняя известная позиция врага.
        public SpatialCoord[] LastKnownEnemyPosition;

        // AI Group ID.
        //
        // Это НЕ обязательно SquadId.
        // SquadId остаётся игровой организационной структурой.
        public int[] CurrentGroupId;

        // ============================================================
        // CONSTRUCTION
        // ============================================================

        public UnitStore(int maxUnits)
        {
            if (maxUnits <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxUnits));

            Count = 0;

            MovementCooldowns = new float[maxUnits];

            ActiveGridIds = new int[maxUnits];

            UnitType = new UnitType[maxUnits];
            Positions = new UnitPosition[maxUnits];
            Sizes = new UnitSize[maxUnits];
            Movement = new UnitMovement[maxUnits];

            SquadIds = new int[maxUnits];

            // --------------------------------------------------------
            // HEALTH
            // --------------------------------------------------------

            HealthMasks = new uint[maxUnits];
            BleedRates = new float[maxUnits];
            BloodLossLevels = new float[maxUnits];

            // --------------------------------------------------------
            // MASS
            // --------------------------------------------------------

            BaseBodyMass = new float[maxUnits];
            DynamicMass = new float[maxUnits];

            // --------------------------------------------------------
            // COMBAT
            // --------------------------------------------------------

            CurrentTargets = new int[maxUnits];

            WeaponSlot = new Weapon[maxUnits];

            HeadArmorSlot = new ArmorConfig[maxUnits];
            TorsoArmorSlot = new ArmorConfig[maxUnits];
            ArmsArmorSlot = new ArmorConfig[maxUnits];
            LegsArmorSlot = new ArmorConfig[maxUnits];

            ShotCooldowns = new float[maxUnits];

            RemainingBurstShots =
                new float[maxUnits];

            IsAiming =
                new bool[maxUnits];

            AimingTimers =
                new float[maxUnits];

            LeanOffsetX =
                new int[maxUnits];

            LeanOffsetY =
                new int[maxUnits];

            AttachedCoverEdificeId =
                new ushort[maxUnits];

            // --------------------------------------------------------
            // SENSOR / LEGACY
            // --------------------------------------------------------

            VisionCacheFlags =
                new bool[maxUnits];

            VisionTickTimers =
                new float[maxUnits];

            IsInCombatMode =
                new bool[maxUnits];

            AlertTimers =
                new float[maxUnits];

            LastGunshotSourceX =
                new int[maxUnits];

            LastGunshotSourceY =
                new int[maxUnits];

            GunshotInvestigateTimer =
                new float[maxUnits];

            LastImpactSourceX =
                new int[maxUnits];

            LastImpactSourceY =
                new int[maxUnits];

            // --------------------------------------------------------
            // MOVEMENT EVENTS
            // --------------------------------------------------------

            HasJustFinishedMoveStep =
                new bool[maxUnits];

            LastVisitedSourceCell =
                new SpatialCoord[maxUnits];

            // --------------------------------------------------------
            // DAMAGE
            // --------------------------------------------------------

            LastAttackerIds =
                new int[maxUnits];

            // --------------------------------------------------------
            // LEGACY COMBAT SUPPRESSION
            // --------------------------------------------------------

            SuppressionLevels =
                new float[maxUnits];

            UnitAlertFlags =
                new byte[maxUnits];

            // --------------------------------------------------------
            // COMMAND STACK
            // --------------------------------------------------------

            AiCommandStacks =
                new AiCommand[
                    maxUnits * MaxAiStackDepth];

            AiStackPointers =
                new int[maxUnits];

            // --------------------------------------------------------
            // NEW UNIT AI
            // --------------------------------------------------------

            Irritation =
                new float[maxUnits];

            Fear =
                new float[maxUnits];

            ThreatLevel =
                new float[maxUnits];

            Suppression =
                new float[maxUnits];

            BehaviorFlags =
                new UnitBehaviorFlags[maxUnits];

            LastKnownEnemyPosition =
                new SpatialCoord[maxUnits];

            CurrentGroupId =
                new int[maxUnits];

            // --------------------------------------------------------
            // DEFAULT VALUES
            // --------------------------------------------------------

            Array.Fill(
                ActiveGridIds,
                -1);

            Array.Fill(
                CurrentTargets,
                -1);

            Array.Fill(
                SquadIds,
                -1);

            Array.Fill(
                LastAttackerIds,
                -1);

            Array.Fill(
                AiStackPointers,
                -1);

            Array.Fill(
                CurrentGroupId,
                -1);

            Array.Fill(
                BehaviorFlags,
                UnitBehaviorFlags.Idle);
        }

        // ============================================================
        // CREATE UNIT
        // ============================================================

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

            if (Count >= Positions.Length)
            {
                ResizeStorage(
                    Math.Max(
                        Positions.Length * 2,
                        Count + 100));
            }

            int id = Count++;

            SpatialCoord start =
                new SpatialCoord(
                    mx,
                    my,
                    mz);

            // --------------------------------------------------------
            // POSITION
            // --------------------------------------------------------

            Positions[id] =
                new UnitPosition(start);

            Sizes[id] =
                new UnitSize(
                    width,
                    height);

            Movement[id] =
                new UnitMovement
                {
                    SourceCell = start,
                    TargetCell = start,
                    ZLevel = mz,
                    Progress = 0f,
                    Speed = speed,
                    State = MovementState.Idle
                };

            // --------------------------------------------------------
            // BASIC
            // --------------------------------------------------------

            UnitType[id] = type;

            SquadIds[id] = -1;

            BaseBodyMass[id] = mass;
            DynamicMass[id] = mass;

            // --------------------------------------------------------
            // HEALTH
            // --------------------------------------------------------

            HealthMasks[id] =
                0xFFFFFFFFu;

            BleedRates[id] =
                0f;

            BloodLossLevels[id] =
                0f;

            // --------------------------------------------------------
            // COMBAT
            // --------------------------------------------------------

            CurrentTargets[id] =
                -1;

            WeaponSlot[id] =
                null;

            HeadArmorSlot[id] =
                null;

            TorsoArmorSlot[id] =
                null;

            ArmsArmorSlot[id] =
                null;

            LegsArmorSlot[id] =
                null;

            ShotCooldowns[id] =
                0f;

            RemainingBurstShots[id] =
                0f;

            IsAiming[id] =
                false;

            AimingTimers[id] =
                0f;

            LeanOffsetX[id] =
                0;

            LeanOffsetY[id] =
                0;

            AttachedCoverEdificeId[id] =
                0;

            // --------------------------------------------------------
            // SENSOR / EVENTS
            // --------------------------------------------------------

            VisionCacheFlags[id] =
                false;

            VisionTickTimers[id] =
                0f;

            IsInCombatMode[id] =
                false;

            AlertTimers[id] =
                0f;

            LastGunshotSourceX[id] =
                0;

            LastGunshotSourceY[id] =
                0;

            GunshotInvestigateTimer[id] =
                0f;

            LastImpactSourceX[id] =
                0;

            LastImpactSourceY[id] =
                0;

            LastAttackerIds[id] =
                -1;

            // --------------------------------------------------------
            // MOVEMENT EVENTS
            // --------------------------------------------------------

            HasJustFinishedMoveStep[id] =
                false;

            LastVisitedSourceCell[id] =
                start;

            // --------------------------------------------------------
            // LEGACY SUPPRESSION
            // --------------------------------------------------------

            SuppressionLevels[id] =
                0f;

            UnitAlertFlags[id] =
                0;

            // --------------------------------------------------------
            // NEW UNIT AI
            // --------------------------------------------------------

            Irritation[id] =
                0f;

            Fear[id] =
                0f;

            ThreatLevel[id] =
                0f;

            Suppression[id] =
                0f;

            BehaviorFlags[id] =
                UnitBehaviorFlags.Idle;

            LastKnownEnemyPosition[id] =
                start;

            CurrentGroupId[id] =
                -1;

            // --------------------------------------------------------
            // COMMAND STACK
            // --------------------------------------------------------

            AiStackPointers[id] =
                -1;

            int stackStart =
                id * MaxAiStackDepth;

            for (int i = 0;
                 i < MaxAiStackDepth;
                 i++)
            {
                AiCommandStacks[
                    stackStart + i] =
                    default;
            }

            // --------------------------------------------------------
            // SPATIAL REGISTRATION
            // --------------------------------------------------------

            spatialGrid.Add(
                start,
                id);

            return id;
        }

        // ============================================================
        // RESIZE
        // ============================================================

        private void ResizeStorage(
            int newSize)
        {
            int oldSize =
                Positions.Length;

            if (newSize <= oldSize)
                return;

            Array.Resize(
                ref MovementCooldowns,
                newSize);

            Array.Resize(
                ref ActiveGridIds,
                newSize);

            Array.Resize(
                ref UnitType,
                newSize);

            Array.Resize(
                ref Positions,
                newSize);

            Array.Resize(
                ref Sizes,
                newSize);

            Array.Resize(
                ref Movement,
                newSize);

            Array.Resize(
                ref SquadIds,
                newSize);

            Array.Resize(
                ref HealthMasks,
                newSize);

            Array.Resize(
                ref BleedRates,
                newSize);

            Array.Resize(
                ref BloodLossLevels,
                newSize);

            Array.Resize(
                ref BaseBodyMass,
                newSize);

            Array.Resize(
                ref DynamicMass,
                newSize);

            Array.Resize(
                ref CurrentTargets,
                newSize);

            Array.Resize(
                ref WeaponSlot,
                newSize);

            Array.Resize(
                ref HeadArmorSlot,
                newSize);

            Array.Resize(
                ref TorsoArmorSlot,
                newSize);

            Array.Resize(
                ref ArmsArmorSlot,
                newSize);

            Array.Resize(
                ref LegsArmorSlot,
                newSize);

            Array.Resize(
                ref ShotCooldowns,
                newSize);

            Array.Resize(
                ref RemainingBurstShots,
                newSize);

            Array.Resize(
                ref IsAiming,
                newSize);

            Array.Resize(
                ref AimingTimers,
                newSize);

            Array.Resize(
                ref LeanOffsetX,
                newSize);

            Array.Resize(
                ref LeanOffsetY,
                newSize);

            Array.Resize(
                ref AttachedCoverEdificeId,
                newSize);

            Array.Resize(
                ref VisionCacheFlags,
                newSize);

            Array.Resize(
                ref VisionTickTimers,
                newSize);

            Array.Resize(
                ref IsInCombatMode,
                newSize);

            Array.Resize(
                ref AlertTimers,
                newSize);

            Array.Resize(
                ref LastGunshotSourceX,
                newSize);

            Array.Resize(
                ref LastGunshotSourceY,
                newSize);

            Array.Resize(
                ref GunshotInvestigateTimer,
                newSize);

            Array.Resize(
                ref LastImpactSourceX,
                newSize);

            Array.Resize(
                ref LastImpactSourceY,
                newSize);

            Array.Resize(
                ref HasJustFinishedMoveStep,
                newSize);

            Array.Resize(
                ref LastVisitedSourceCell,
                newSize);

            Array.Resize(
                ref LastAttackerIds,
                newSize);

            Array.Resize(
                ref SuppressionLevels,
                newSize);

            Array.Resize(
                ref UnitAlertFlags,
                newSize);

            // ========================================================
            // NEW UNIT AI
            // ========================================================

            Array.Resize(
                ref Irritation,
                newSize);

            Array.Resize(
                ref Fear,
                newSize);

            Array.Resize(
                ref ThreatLevel,
                newSize);

            Array.Resize(
                ref Suppression,
                newSize);

            Array.Resize(
                ref BehaviorFlags,
                newSize);

            Array.Resize(
                ref LastKnownEnemyPosition,
                newSize);

            Array.Resize(
                ref CurrentGroupId,
                newSize);

            // ========================================================
            // COMMAND STACK
            // ========================================================

            Array.Resize(
                ref AiStackPointers,
                newSize);

            Array.Resize(
                ref AiCommandStacks,
                newSize *
                MaxAiStackDepth);

            // --------------------------------------------------------
            // INITIALIZE NEW SLOTS
            // --------------------------------------------------------

            Array.Fill(
                ActiveGridIds,
                -1,
                oldSize,
                newSize - oldSize);

            Array.Fill(
                CurrentTargets,
                -1,
                oldSize,
                newSize - oldSize);

            Array.Fill(
                SquadIds,
                -1,
                oldSize,
                newSize - oldSize);

            Array.Fill(
                LastAttackerIds,
                -1,
                oldSize,
                newSize - oldSize);

            Array.Fill(
                AiStackPointers,
                -1,
                oldSize,
                newSize - oldSize);

            Array.Fill(
                CurrentGroupId,
                -1,
                oldSize,
                newSize - oldSize);

            Array.Fill(
                BehaviorFlags,
                UnitBehaviorFlags.Idle,
                oldSize,
                newSize - oldSize);
            
            Array.Resize(ref BloodLossLevels, newSize);

            // --- ДОБАВИТЬ СТРОГО СЮДА ДЛЯ СИНХРОНИЗАЦИИ DOD-МАССИВОВ ---
            // Если менеджер движения инициализирован, принудительно увеличиваем его буферы под новый размер newSize
            if (GroupMovementManager != null)
            {
                GroupMovementManager.ResizeStorage(newSize);
            }
        

        }

        // ============================================================
        // HEALTH
        // ============================================================

        public void UpdateHealthSystems(
            float deltaTime)
        {
            for (int i = 0;
                 i < Count;
                 i++)
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

                CurrentTargets[i] =
                    -1;

                SquadIds[i] =
                    -1;

                CurrentGroupId[i] =
                    -1;

                BehaviorFlags[i] =
                    UnitBehaviorFlags.None;
            }
        }

        // ============================================================
        // AI COMMAND STACK
        // ============================================================

        public void CpuPushCommand(
            int unitId,
            AiCommand command)
        {
            int sp =
                AiStackPointers[unitId];

            if (sp >= MaxAiStackDepth - 1)
                return;

            sp++;

            AiStackPointers[unitId] =
                sp;

            AiCommandStacks[
                unitId *
                MaxAiStackDepth +
                sp] =
                command;
        }

        public void CpuPopCommand(
            int unitId)
        {
            int sp =
                AiStackPointers[unitId];

            if (sp < 0)
                return;

            AiCommandStacks[
                unitId *
                MaxAiStackDepth +
                sp] =
                default;

            AiStackPointers[unitId] =
                sp - 1;
        }

        public ref AiCommand CpuPeekCommand(
            int unitId)
        {
            int sp =
                AiStackPointers[unitId];

            if (sp < 0)
            {
                return ref AiCommandStacks[
                    unitId *
                    MaxAiStackDepth];
            }

            return ref AiCommandStacks[
                unitId *
                MaxAiStackDepth +
                sp];
        }
     
        [System.Flags]
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
}