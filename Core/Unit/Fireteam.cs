using Core.Map;
using Core.Structs; // Твой SpatialCoord
using Core.Unit.Components;
using Core.Unit.Squad;
using System;
using System.Collections.Generic;

namespace Core.Unit
{
    // Очередность тактических приоритетов БГ
    public enum FireteamTacticalState : byte
    {
        Idle,            // Отдых, патруль Wander
        Marching,        // Выполнение стратегического приказа Сквада (Марш в точку)
        SuppressedCover, // КРИТИЧЕСКИЙ ПРИОРИТЕТ: Нас активно пиздят! Бросаем всё и ищем окопы!
        CombatEngage     // Ответный огонь на подавление
    }

    public struct Fireteam
    {
        public bool IsMoving
        {
            get
            {
                // Если координата нулевая, БГ физически не может находиться в режиме марша!
                if (StrategicWaypoint.X == 0 && StrategicWaypoint.Y == 0) return false;

                return TacticalState == FireteamTacticalState.Marching ||
                       TacticalState == FireteamTacticalState.SuppressedCover;
            }
            set
            {
                // Запрещаем принудительный взвод марша, если штаб еще не прислал валидные координаты
                if (value && StrategicWaypoint.X == 0 && StrategicWaypoint.Y == 0)
                {
                    TacticalState = FireteamTacticalState.Idle;
                    return;
                }

                if (value)
                {
                    TacticalState = FireteamTacticalState.Marching;
                }
                else
                {
                    TacticalState = (StrategicWaypoint.X != 0 && StrategicWaypoint.Y != 0)
                        ? FireteamTacticalState.Marching
                        : FireteamTacticalState.Idle;
                }
            }
        }

        public int Id;                          // ID боевой группы (БГ)
        public int ParentSquadId;               // ID родительского Сквада-Штаба
        public string Name;
        public int LeaderUnitId;
        public SquadFormation Formation;        // Выбранный строй (Square, Line, Wedge, Diamond)

        // ТАКТИЧЕСКИЙ АВТОМАТ СОСТОЯНИЙ БГ
        public FireteamTacticalState TacticalState;
        public SpatialCoord StrategicWaypoint;  // Глобальная цель, которую спустил Сквад
        public SpatialCoord TacticalCoverPoint;  // Локальная точка мешков с песком, куда прыгаем при обстреле
        public float SuppressTimer;             // Сколько секунд сидим в укрытии после крайнего попадания

        public readonly List<int> MemberUnitIds;
        public readonly SpatialCoord[] StepTrails;

        public Fireteam(int id, int parentSquadId, string name, int maxCapacity = 5)
        {
            Id = id;
            ParentSquadId = parentSquadId;
            Name = name;
            LeaderUnitId = -1;
            Formation = SquadFormation.Square;

            TacticalState = FireteamTacticalState.Idle;
            StrategicWaypoint = default;
            TacticalCoverPoint = default;
            SuppressTimer = 0f;

            MemberUnitIds = new List<int>(maxCapacity);
            StepTrails = new SpatialCoord[maxCapacity + 5];
        }
        public void PushLeaderStep(SpatialCoord newStep)
        {
            // Если командир топчется на месте — игнорируем
            if (StepTrails[0].X == newStep.X && StepTrails[0].Y == newStep.Y && StepTrails[0].Z == newStep.Z)
                return;

            // Сдвигаем циклический буфер истории
            for (int i = StepTrails.Length - 1; i > 0; i--)
            {
                StepTrails[i] = StepTrails[i - 1];
            }
            StepTrails[0] = newStep; // Свежий след командира всегда на вершине
        }
        //public void PushTrailStep(SpatialCoord newStep)
        //{
        //    if (StepTrails[0].X == newStep.X && StepTrails[0].Y == newStep.Y && StepTrails[0].Z == newStep.Z)
        //        return;

        //    for (int i = StepTrails.Length - 1; i > 0; i--) StepTrails[i] = StepTrails[i - 1];
        //    StepTrails[0] = newStep;
        //}

        /// <summary>
        /// ДИНАМИЧЕСКИЙ ВЫЧИСЛИТЕЛЬ ОЧЕРЕДНОСТИ ДЕЙСТВИЙ БГ
        /// </summary>
        public void UpdateMovement(
    UnitStore units,
    UnitSpatialGrid spatialGrid,
    UnitMovementSystem movementMotor,
    WorldMap map,
    float deltaTime,
    out bool allArrived)
        {
            allArrived = true;
            // ИСПРАВЛЕНО: Проверяем валидность стратегического вейпоинта штаба
            if (!IsMoving || MemberUnitIds.Count == 0 || StrategicWaypoint.X == 0) return;

            int maxCoord = (16 * 48) - 1;

            // ========================================================
            // ШАГ 1. СЕНСОРНЫЙ АНАЛИЗ ОКРУЖЕНИЯ: НАС ПИЗДЯТ?
            // ========================================================
            bool underActiveAttack = false;
            int hazardX = 0, hazardY = 0;

            for (int i = 0; i < MemberUnitIds.Count; i++)
            {
                int uid = MemberUnitIds[i];
                if (units.HealthMasks[uid] == 0) continue;

                if (units.LastImpactSourceX[uid] != 0 || units.LastAttackerIds[uid] != -1)
                {
                    underActiveAttack = true;
                    hazardX = units.LastImpactSourceX[uid] != 0 ? units.LastImpactSourceX[uid] : units.Positions[uid].Spatial.X;
                    hazardY = units.LastImpactSourceY[uid] != 0 ? units.LastImpactSourceY[uid] : units.Positions[uid].Spatial.Y;

                    units.LastImpactSourceX[uid] = 0;
                    units.LastAttackerIds[uid] = -1;
                }
            }

            // ========================================================
            // ШАГ 2. АВТОМАТ ИЕРАРХИИ ПРИОРИТЕТОВ БГ
            // ========================================================
            if (underActiveAttack)
            {
                TacticalState = FireteamTacticalState.SuppressedCover;
                SuppressTimer = 8.0f;

                if (LeaderUnitId >= 0)
                {
                    TacticalCoverPoint = FindDirectedCoverCell(units, LeaderUnitId, hazardX, hazardY, map);
                    Console.WriteLine($"🔥 [{Name}] ПОПАЛ ПОД ОБСТРЕЛ! Срываем приказ, уходим в укрытие ({TacticalCoverPoint.X}, {TacticalCoverPoint.Y})");
                }
            }

            if (TacticalState == FireteamTacticalState.SuppressedCover)
            {
                SuppressTimer -= deltaTime;
                if (SuppressTimer <= 0f)
                {
                    TacticalState = (StrategicWaypoint.X != 0) ? FireteamTacticalState.Marching : FireteamTacticalState.Idle;
                    Console.WriteLine($"☀️ [{Name}] Обстрел прекратился. Встаем и продолжаем марш.");
                }
            }

            if (TacticalState == FireteamTacticalState.Idle) return;

            // ========================================================
            // ШАГ 3. ИСПОЛНЕНИЕ ТЕКУЩЕГО ВЫБРАННОГО ПРИОРИТЕТА
            // ========================================================
            SpatialCoord activeTargetCell = (TacticalState == FireteamTacticalState.SuppressedCover) ? TacticalCoverPoint : StrategicWaypoint;
            SquadFormation activeFormation = (TacticalState == FireteamTacticalState.SuppressedCover) ? SquadFormation.Square : Formation;

            // Если мы далеко от цели, принудительно стягиваемся в Колонну-Гусеницу
            ref var leaderPos = ref units.Positions[LeaderUnitId];
            int distToFinalX = Math.Abs(activeTargetCell.X - leaderPos.Spatial.X);
            int distToFinalY = Math.Abs(activeTargetCell.Y - leaderPos.Spatial.Y);
            if (distToFinalX > 5 || distToFinalY > 5)
            {
                activeFormation = SquadFormation.Column;
            }

            // ИСПРАВЛЕНО: Вызов ломающегося PushTrailStep отсюда ПОЛНОСТЬЮ УДАЛЕН.
            // Шаги лидера теперь пишутся по честной ref-ссылке внутри SquadMovementSystem!

            for (int i = 0; i < MemberUnitIds.Count; i++)
            {
                int unitId = MemberUnitIds[i];
                if (units.HealthMasks[unitId] == 0) continue;

                ref var movement = ref units.Movement[unitId];
                ref var position = ref units.Positions[unitId];

                if (movement.State == MovementState.Moving)
                {
                    allArrived = false;
                    continue;
                }

                SpatialCoord personalTarget;
                int offsetX = 0, offsetY = 0;

                switch (activeFormation)
                {
                    case SquadFormation.Line:
                        offsetX = (i % 4) - 2; offsetY = (i / 4);
                        personalTarget = new SpatialCoord(Math.Clamp(activeTargetCell.X + offsetX, 0, maxCoord), Math.Clamp(activeTargetCell.Y + offsetY, 0, maxCoord), activeTargetCell.Z);
                        break;

                    case SquadFormation.Wedge:
                        int wedgeRow = (int)MathF.Sqrt(2 * i + 0.25f);
                        if (wedgeRow == 0) { offsetX = 0; offsetY = 0; }
                        else
                        {
                            int side = (i % 2 == 0) ? 1 : -1;
                            offsetX = (i % wedgeRow + 1) * side; offsetY = wedgeRow;
                        }
                        personalTarget = new SpatialCoord(Math.Clamp(activeTargetCell.X + offsetX, 0, maxCoord), Math.Clamp(activeTargetCell.Y + offsetY, 0, maxCoord), activeTargetCell.Z);
                        break;

                    case SquadFormation.Column:
                        // ИСПРАВЛЕНО: Читаем кэш из локального StepTrails структуры
                        if (unitId == LeaderUnitId || i == 0)
                        {
                            personalTarget = activeTargetCell;
                        }
                        else
                        {
                            int historyIndex = i * 2;
                            historyIndex = Math.Clamp(historyIndex, 0, StepTrails.Length - 1);

                            SpatialCoord historyStep = StepTrails[historyIndex];

                            if (historyStep.X == 0 && historyStep.Y == 0)
                            {
                                int guyInFrontId = MemberUnitIds[i - 1];
                                personalTarget = units.Positions[guyInFrontId].Spatial;
                            }
                            else
                            {
                                personalTarget = historyStep;
                            }
                        }
                        break;

                    case SquadFormation.Square:
                    default:
                        offsetX = (i % 2) - 1; offsetY = (i / 2) - 1;
                        personalTarget = new SpatialCoord(Math.Clamp(activeTargetCell.X + offsetX, 0, maxCoord), Math.Clamp(activeTargetCell.Y + offsetY, 0, maxCoord), activeTargetCell.Z);
                        break;
                }

                if (position.Spatial.X == personalTarget.X && position.Spatial.Y == personalTarget.Y && position.Spatial.Z == personalTarget.Z)
                {
                    if (TacticalState == FireteamTacticalState.SuppressedCover) movement.State = MovementState.InCover;
                    continue;
                }

                int stepX = Math.Sign(personalTarget.X - position.Spatial.X);
                int stepY = Math.Sign(personalTarget.Y - position.Spatial.Y);

                SpatialCoord nextStepCell = new SpatialCoord(position.Spatial.X + stepX, position.Spatial.Y + stepY, activeTargetCell.Z);

                bool started = movementMotor.TryStartMove(units, unitId, nextStepCell, spatialGrid, map);
                if (started) allArrived = false;
                else
                {
                    SpatialCoord alternativeCell = FindAlternativeCell(nextStepCell, spatialGrid);
                    if (spatialGrid.GetUnitsAt(alternativeCell).Count == 0)
                    {
                        if (movementMotor.TryStartMove(units, unitId, alternativeCell, spatialGrid, map)) allArrived = false;
                    }
                }
            }
        }

        private SpatialCoord FindDirectedCoverCell
            (UnitStore units, int unitId, int hazardX, int hazardY, WorldMap map) 
        { ref var pos = ref units.Positions[unitId]; MapLayer layer = 
                map.GetLayer(pos.Spatial.Z); if (layer == null) return pos.Spatial; 
            int vecX = Math.Sign(pos.Spatial.X - hazardX); int vecY = Math.Sign(pos.Spatial.Y - hazardY);
            // Ищем низкое укрытие (мешки с песком) в радиусе 4 ячеек сзади по вектору обстрела
            for (int d = 1; d <= 4; d++) 
            { int cx = Math.Clamp(pos.Spatial.X + vecX * d, 0, (16 * 48) - 1); 
                int cy = Math.Clamp(pos.Spatial.Y + vecY * d, 0, (16 * 48) - 1); 
                ref var cell = ref layer.GetMicroCell(cx, cy);
                // Если ячейка пуста и пригодна для окопа
                if (cell.EdificeId == 0 && (cell.Flags & 0x0004) != 0)
                {return new SpatialCoord(cx, cy, pos.Spatial.Z);
                }}
            return pos.Spatial;}
        private SpatialCoord FindAlternativeCell
            (SpatialCoord target, UnitSpatialGrid spatialGrid)
        {for (int dy = -1; dy <= 1; dy++)
            {for (int dx = -1; dx <= 1; dx++)
                {if (dx == 0 && dy == 0) continue;
                    SpatialCoord candidate = new SpatialCoord
                        (target.X + dx, target.Y + dy, target.Z);
                    if (spatialGrid.GetUnitsAt(candidate).Count == 0) 
                        return candidate;}}return target;}}}