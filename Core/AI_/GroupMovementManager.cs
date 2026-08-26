// Path: Assets/Scripts/AI/GroupMovementManager.cs
using Core.Map;
using Core.Structs;
using Core.Unit; // Пространство имен вашего класса UnitStore и UnitMovement
using Core.Unit.Components;
using System;
using System.Runtime.InteropServices;
using static Core.Unit.UnitStore;

namespace Core.AI
{


    /// <summary>
    /// Компактная структура тактического вейпоинта для юнита, упакованная в один int
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketWaypoint
    {
        public short X;
        public short Y;
    }

    /// <summary>
    /// DOD-структура макро-пути A* для конкретной группы отряда
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GroupMacroPath
    {
        public int GroupId;
        public int TotalSteps;
        // Плоский буфер хэшей всего маршрута (максимум 512 шагов)
        public int[] GlobalSteps;
    }

    /// <summary>
    /// Автономная Data-Oriented система управления движением групп.
    /// Полностью динамическая — массивы расширяются синхронно с базой данных UnitStore.
    /// </summary>
    public class GroupMovementManager
    {
        public const int MaxWaypointsPerPath = 512;
        public const int MaxSubWaypointsToUnit = 4; // Лимит выдачи вейпоинтов юниту на один такт
        public const int MaxGridSize = 768;

        // Хранилище макро-маршрутов для групп
        public GroupMacroPath[] _groupPaths;

        // --- ДИНАМИЧЕСКИЕ ПАРАЛЛЕЛЬНЫЕ МАССИВЫ КОМПОНЕНТОВ НАВИГАЦИИ ЮНИТОВ ---
        public int[] _unitGlobalPathIndices;
        public PacketWaypoint[,] _unitSubWaypointsBuffer;
        public byte[] _unitSubWaypointsCount;

        public GroupMovementManager(int initialMaxGroups, int initialMaxUnits)
        {
            _groupPaths = new GroupMacroPath[initialMaxGroups];
            for (int i = 0; i < initialMaxGroups; i++)
            {
                _groupPaths[i].GlobalSteps = new int[MaxWaypointsPerPath];
            }

            // Первичная аллокация параллельных DOD-массивов под юнитов
            _unitGlobalPathIndices = new int[initialMaxUnits];
            _unitSubWaypointsBuffer = new PacketWaypoint[initialMaxUnits, MaxSubWaypointsToUnit];
            _unitSubWaypointsCount = new byte[initialMaxUnits];
        }

        /// <summary>
        /// ДИНАМИЧЕСКОЕ РАСШИРЕНИЕ ХРАНИЛИЩА.
        /// Должно вызываться внутри вашего оригинального UnitStore.ResizeStorage().
        /// Исключает вылеты по границам массивов при спавне новых существ.
        /// </summary>
        public void ResizeStorage(int newSize)
        {
            int oldSize = _unitGlobalPathIndices.Length;
            if (newSize <= oldSize) return;

            // 1. Расширяем массив глобальных индексов прогресса A*
            Array.Resize(ref _unitGlobalPathIndices, newSize);

            // 2. Расширяем массив количества активных вейпоинтов
            Array.Resize(ref _unitSubWaypointsCount, newSize);

            // 3. Расширяем многомерный плоский буфер под-целей [UnitId, 4]
            // Так как Array.Resize не работает с двумерными матрицами напрямую, пересоздаем ее
            PacketWaypoint[,] newSubWaypointsBuffer = new PacketWaypoint[newSize, MaxSubWaypointsToUnit];

            // Пошагово копируем старые данные без потерь
            for (int u = 0; u < oldSize; u++)
            {
                for (int w = 0; w < MaxSubWaypointsToUnit; w++)
                {
                    newSubWaypointsBuffer[u, w] = _unitSubWaypointsBuffer[u, w];
                }
            }
            _unitSubWaypointsBuffer = newSubWaypointsBuffer;
        }

        /// <summary>
        /// Динамическое расширение лимита зарегистрированных ИИ-групп на карте при необходимости.
        /// </summary>
        public void EnsureGroupsCapacity(int requiredGroupId)
        {
            if (requiredGroupId >= _groupPaths.Length)
            {
                int newCapacity = Math.Max(_groupPaths.Length * 2, requiredGroupId + 4);
                GroupMacroPath[] newArray = new GroupMacroPath[newCapacity];
                Array.Copy(_groupPaths, 0, newArray, 0, _groupPaths.Length);

                for (int i = _groupPaths.Length; i < newCapacity; i++)
                {
                    newArray[i].GlobalSteps = new int[MaxWaypointsPerPath];
                }
                _groupPaths = newArray;
            }
        }

        public bool RequestAndStoreGroupRoute(MapLayer layer, int groupId, short startX, short startY, short targetX, short targetY, int firstUnitIndex, int unitCount)
        {
            if (layer == null) return false;

            // Автоматически контролируем емкость групп
            EnsureGroupsCapacity(groupId);

            short[] tempCoordBuffer = new short[MaxWaypointsPerPath * 2];
            int actualLength;

            bool pathFound = PureAStarPathfinder.FindRoute(
                layer,
                startX, startY,
                targetX, targetY,
                tempCoordBuffer,
                MaxWaypointsPerPath,
                out actualLength
            );

            if (pathFound && actualLength > 0)
            {
                ref GroupMacroPath pathStorage = ref _groupPaths[groupId];
                pathStorage.GroupId = groupId;
                pathStorage.TotalSteps = actualLength;

                for (int i = 0; i < actualLength; i++)
                {
                    short x = tempCoordBuffer[i * 2];
                    short y = tempCoordBuffer[i * 2 + 1];
                    pathStorage.GlobalSteps[i] = (y * MaxGridSize) + x;
                }

                int endIndex = firstUnitIndex + unitCount;
                for (int u = firstUnitIndex; u < endIndex; u++)
                {
                    if (u < _unitGlobalPathIndices.Length)
                    {
                        _unitGlobalPathIndices[u] = 0;
                        _unitSubWaypointsCount[u] = 0;
                    }
                }
                return true;
            }

            return false;
        }

        public void DistributePathsToUnits(UnitStore unitStore)
        {
            if (unitStore == null) return;

            // Если размер UnitStore динамически вырос во время игры, подтягиваем навигационные буферы
            if (unitStore.Count > _unitGlobalPathIndices.Length)
            {
                ResizeStorage(unitStore.Count);
            }

            for (int u = 0; u < unitStore.Count; u++)
            {
                if (unitStore.HealthMasks[u] == 0) continue;

                int groupId = unitStore.CurrentGroupId[u];
                if (groupId < 0) continue;

                EnsureGroupsCapacity(groupId);

                ref GroupMacroPath macroPath = ref _groupPaths[groupId];
                if (macroPath.TotalSteps == 0) continue;

                ref var movement = ref unitStore.Movement[u];

                bool readyForNextCell = movement.State == MovementState.Idle || movement.Progress >= 1.0f;
                if (!readyForNextCell) continue;

                int currentGlobalIdx = _unitGlobalPathIndices[u];

                // 1. ИЗВЛЕКАЕМ И РАСПАКОВЫВАЕМ ИЗ INT (ИСПРАВЛЕНО CS0029)
                int currentTargetHash = macroPath.GlobalSteps[currentGlobalIdx]; // Если массив int[], то все ок
                short targetX = (short)(currentTargetHash % MaxGridSize);
                short targetY = (short)(currentTargetHash / MaxGridSize);

                // Проверяем, достиг ли юнит текущего макро-вейпоинта
                if (unitStore.Positions[u].Spatial.X == targetX && unitStore.Positions[u].Spatial.Y == targetY)
                {
                    if (currentGlobalIdx < macroPath.TotalSteps - 1)
                    {
                        currentGlobalIdx++;
                        _unitGlobalPathIndices[u] = currentGlobalIdx;

                        // Берем координаты следующего шага
                        currentTargetHash = macroPath.GlobalSteps[currentGlobalIdx];
                        targetX = (short)(currentTargetHash % MaxGridSize);
                        targetY = (short)(currentTargetHash / MaxGridSize);
                    }
                    else
                    {
                        // Путь завершен
                        continue;
                    }
                }

                // 2. ЗАПОЛНЯЕМ ЛОКАЛЬНЫЙ БУФЕР ОПЕРЕЖЕНИЯ (ИСПРАВЛЕН РЕГИСТР БУКВ 'W')
                byte subCount = 0;
                for (int i = 0; i < MaxSubWaypointsToUnit; i++) // С большой 'W'!
                {
                    int lookAheadIdx = currentGlobalIdx + i;
                    if (lookAheadIdx < macroPath.TotalSteps)
                    {
                        int hash = macroPath.GlobalSteps[lookAheadIdx];

                        // Записываем чистые координаты в структуру юнита
                        _unitSubWaypointsBuffer[u, i] = new PacketWaypoint // С большой 'W'!
                        {
                            X = (short)(hash % MaxGridSize),
                            Y = (short)(hash / MaxGridSize)
                        };
                        subCount++;
                    }
                }
                _unitSubWaypointsCount[u] = subCount; // С большой 'W'!

                // 3. ФЛАГ ПОВЕДЕНИЯ ДЛЯ СИНХРОНИЗАЦИИ
                unitStore.BehaviorFlags[u] |= UnitBehaviorFlags.FollowingGroupOrder;
            }
        }


    }
}



