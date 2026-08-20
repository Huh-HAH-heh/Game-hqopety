using Core.Items;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents.Core.Unit.Components;
using Core.Unit.Systems;
using System;

namespace Core.Unit;
    public sealed class UnitStore
    {
    public float[] VisionTickTimers;  // Таймер до следующего DDA-сканирования (в секундах)
    public bool[] VisionCacheFlags;   // Результат последнего сканирования: видит ли цель

    // Сколько пуль осталось выпустить в рамках текущей начатой очереди
    public int[] RemainingBurstShots;

    // Максимальная глубина стека задач для одной пешки (как глубина стека вызовов ЦП)
    public const int MaxAiStackDepth = 4;

        // Двумерный плоский массив стеков: [ID юнита * MaxAiStackDepth]
        // Чтобы не аллоцировать память, храним стек как плоскую матрицу структур
        public AiCommand[] AiCommandStacks;

        // Указатель стека (Stack Pointer - SP) для каждого юнита. Хранит индекс текущей верхней задачи (0..3).
        // -1 означает, что стек абсолютно пуст.
        public int[] AiStackPointers;
        public bool[] IsInCombatMode;
        public float[] AlertTimers;

        // ТАКТИЧЕСКАЯ ПАМЯТЬ 1: Источник звука выстрела (Вектор поиска врага)
        public int[] LastGunshotSourceX;
        public int[] LastGunshotSourceY;
        public float[] GunshotInvestigateTimer; // Время, сколько юнит будет искать врага по звуку

        // ТАКТИЧЕСКАЯ ПАМЯТЬ 2: Вектор физической угрозы (Откуда прилетела пуля, упавшая РЯДОМ)
        public int[] LastImpactSourceX;
        public int[] LastImpactSourceY;

        public int Count;
        public int[] CurrentTargets;
        public UnitType[] UnitType;
        public UnitPosition[] Positions;
        public UnitSize[] Sizes;
        public UnitMovement[] Movement;

        // Маска здоровья: [Голова][Торс][Руки][Ноги] по 8 бит на зону.
        // Полное здоровье юнита: 0xFFFFFFFF
        public uint[] HealthMasks;

        // Скорость потери крови в секунду. 0 = нет кровотечения.
        public float[] BleedRates;

        // Общий уровень потери крови от 0.0f до 1.0f
        public float[] BloodLossLevels;

        // Чистый вес пешки 
        public float[] BaseBodyMass;
        // Текущая масса юнита.
        public float[] DynamicMass;

        // СЛОТЫ ЭКИПИРОВКИ ЮНИТОВ
        public Weapon[] WeaponSlot;     // Активное оружие (ближнее или дальнее)
        public ArmorConfig[] HeadArmorSlot;   // Шлем / Шапка
        public ArmorConfig[] TorsoArmorSlot;  // Бронежилет / Куртка
        public ArmorConfig[] ArmsArmorSlot;   // Перчатки / Рукава
        public ArmorConfig[] LegsArmorSlot;   // Штаны / Поножи
                                             
    public bool[] HasJustFinishedMoveStep;
    public SpatialCoord[] LastVisitedSourceCell;


    public float[] AimingTimers;
    public bool[] IsAiming;
    public int[] LeanOffsetX;
    public int[] LeanOffsetY;
    public ushort[] AttachedCoverEdificeId;
    public int[] LastAttackerIds;     // ID последнего юнита, нанесшего урон (для поиска агрессора)
  
    // -1 = юнит не состоит в отряде.
    public int[] SquadIds;

        public float[] ShotCooldowns;

        public UnitStore(int maxUnits)
        {
        // Выделяем плоскую ECS-память под счетчики отсечки очередей
        VisionTickTimers = new float[maxUnits];
        VisionCacheFlags = new bool[maxUnits];

        RemainingBurstShots = new int[maxUnits];

        SquadIds = new int[maxUnits];
        LastAttackerIds = new int[maxUnits];

        HasJustFinishedMoveStep = new bool[maxUnits];
        LastVisitedSourceCell = new SpatialCoord[maxUnits];

        AiCommandStacks = new AiCommand[maxUnits * MaxAiStackDepth];
        AiStackPointers = new int[maxUnits];


        IsInCombatMode = new bool[maxUnits];
        AlertTimers = new float[maxUnits];

        LastGunshotSourceX = new int[maxUnits];
        LastGunshotSourceY = new int[maxUnits];
        GunshotInvestigateTimer = new float[maxUnits];

        LastImpactSourceX = new int[maxUnits];
        LastImpactSourceY = new int[maxUnits];

        // ИСПРАВЛЕНО: Выделяем память под массивы прицеливания, выглядывания и укрытий
        AimingTimers = new float[maxUnits];
        IsAiming = new bool[maxUnits];
        LeanOffsetX = new int[maxUnits];
        LeanOffsetY = new int[maxUnits];
        AttachedCoverEdificeId = new ushort[maxUnits];

        Count = 0;
        BaseBodyMass = new float[maxUnits];

        CurrentTargets = new int[maxUnits];
     

        UnitType = new UnitType[maxUnits];
        Positions = new UnitPosition[maxUnits];
        Sizes = new UnitSize[maxUnits];
        Movement = new UnitMovement[maxUnits];
        DynamicMass = new float[maxUnits];
        SquadIds = new int[maxUnits];
     

        // Инициализация массивов здоровья
        HealthMasks = new uint[maxUnits];
        BleedRates = new float[maxUnits];
        BloodLossLevels = new float[maxUnits];

        WeaponSlot = new Weapon[maxUnits];
        HeadArmorSlot = new ArmorConfig[maxUnits];
        TorsoArmorSlot = new ArmorConfig[maxUnits];
        ArmsArmorSlot = new ArmorConfig[maxUnits];
        LegsArmorSlot = new ArmorConfig[maxUnits];

        ShotCooldowns = new float[maxUnits];

        Array.Fill(CurrentTargets, -1);
        Array.Fill(SquadIds, -1);
        Array.Fill(AiStackPointers, -1);
        Array.Fill(SquadIds, -1);
        Array.Fill(LastAttackerIds, -1);
        Array.Fill(RemainingBurstShots, 0);
        Array.Fill(VisionTickTimers, 0f);
        Array.Fill(VisionCacheFlags, false);

    }

    /// <summary>
    /// Создает и физически регистрирует муравья в мире по чистым ГЛОБАЛЬНЫМ МИКРО-координатам.
    /// </summary>
    /// <param name="mx">Глобальная микро-координата X</param>
    /// <param name="my">Глобальная микро-координата Y</param>
    /// <param name="mz">Этаж карты (Z)</param>
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
            if (Count >= Positions.Length)
                throw new InvalidOperationException("UnitStore is full.");

            int id = Count++;

            // Создаем монолитную пространственную микро-координату
            var startSpatial = new SpatialCoord(mx, my, mz);

            // Инициализируем позицию (RenderX/Y автоматически станут равны mx и my без всяких делений на 8f)
            Positions[id] = new UnitPosition(startSpatial);

            Sizes[id] = new UnitSize(width, height);

            // Инициализируем компонент движения на микро-уровне
            Movement[id] = new UnitMovement
            {
                SourceCell = startSpatial,
                TargetCell = startSpatial,
                ZLevel = mz,
                Progress = 0f,
                Speed = speed,
                State = MovementState.Idle
            };

            BaseBodyMass[id] = mass;
            DynamicMass[id] = mass;
            SquadIds[id] = -1;
            UnitType[id] = type;

            // Настройки здоровья при создании (100% ХП, чистая кровь)
            HealthMasks[id] = 0xFFFFFFFF;
            BleedRates[id] = 0f;
            BloodLossLevels[id] = 0f;

            WeaponSlot[id] = null;
            HeadArmorSlot[id] = null;
            TorsoArmorSlot[id] = null;
            ArmsArmorSlot[id] = null;
            LegsArmorSlot[id] = null;
            ShotCooldowns[id] = 0f;

            // ШАГ 5: Физически прописываем созданного муравья в пространственную микро-сетку
            spatialGrid.Add(startSpatial, id);

            return id;
        }

        /// <summary>
        /// Ежекадровое обновление жизненных показателей всех живых муравьев в мире.
        /// </summary>
        public void UpdateHealthSystems(float deltaTime, SquadStore squadStore)
        {
            for (int i = 0; i < Count; i++)
            {
                if (HealthMasks[i] == 0) continue;

                // Ошибки исчерпаны: передаем корректные имена полей BleedRates и BloodLossLevels
                HealthSystem.UpdateTick(
                    ref HealthMasks[i],
                    ref BleedRates[i],
                    ref BloodLossLevels[i],
                    deltaTime
                );

                if (HealthMasks[i] == 0)
                {
                    CurrentTargets[i] = -1; // Сбрасываем боевую цель мертвеца

                    int squadId = SquadIds[i];
                    if (squadId != -1)
                    {
                        squadStore.Members[squadId].Remove(i);
                        SquadIds[i] = -1;
                    }
                }
            }
        }
        public void CpuPushCommand(int unitId, AiCommand cmd)
        {
            int sp = AiStackPointers[unitId];
            if (sp >= MaxAiStackDepth - 1) return; // Переполнение стека (Stack Overflow) - игнорируем

            sp++;
            AiStackPointers[unitId] = sp;
            AiCommandStacks[(unitId * MaxAiStackDepth) + sp] = cmd;
        }

        public void CpuPopCommand(int unitId)
        {
            int sp = AiStackPointers[unitId];
            if (sp < 0) return; // Стек и так пуст

            // Зануляем старую задачу
            AiCommandStacks[(unitId * MaxAiStackDepth) + sp] = default;
            sp--;
            AiStackPointers[unitId] = sp;
        }

        public ref AiCommand CpuPeekCommand(int unitId)
        {
            int sp = AiStackPointers[unitId];
            if (sp < 0)
            {
                // Если стек пуст, возвращаем дефолтную заглушку Idle
                return ref AiCommandStacks[unitId * MaxAiStackDepth];
            }
            return ref AiCommandStacks[(unitId * MaxAiStackDepth) + sp];
        }

    }

