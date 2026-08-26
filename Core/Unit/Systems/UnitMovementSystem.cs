using Core.AI;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents.Core.Unit.Components;
using Core.Unit.Systems;
using Core.Unit.Systems.CombatPath;
using System;

namespace Core.Unit
{
    public sealed class UnitMovementSystem
    {
        public bool TryStartMove(UnitStore units, int unitId, SpatialCoord targetCell, UnitSpatialGrid spatialGrid, WorldMap map)
        {
            if (unitId < 0 || unitId >= units.Count) return false;
            if (units.HealthMasks[unitId] == 0) return false;

            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            if (movement.State == MovementState.Moving) return false;

            if (!MovementRules.CanStep(map, position.Spatial.X, position.Spatial.Y, targetCell.X, targetCell.Y, position.Spatial.Z))
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            if (spatialGrid.GetUnitsAt(targetCell).Count > 0)
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            movement.SourceCell = position.Spatial;
            movement.TargetCell = targetCell;
            movement.ZLevel = position.Spatial.Z;

            movement.Progress = 0f;
            movement.State = MovementState.Moving;
            // --- Внутри метода TryStartMove класса UnitMovementSystem ---
            // На замену предыдущим двум строчкам сброса:

            float distToPhysicalX = Math.Abs(position.RenderX - position.Spatial.X);
            float distToPhysicalY = Math.Abs(position.RenderY - position.Spatial.Y);

            // Если push-система унесла визуал слишком далеко (аномалия из-за давки),
            // принудительно подтягиваем картинку ближе к физическому телу перед стартом
            if (distToPhysicalX > 1.5f || distToPhysicalY > 1.5f)
            {
                position.RenderX = position.Spatial.X;
                position.RenderY = position.Spatial.Y;
            }

            return true;
        }

        public void Update(
     UnitStore units,
     UnitSpatialGrid spatialGrid,
     WorldMap map,
     EdificeStore edifices,
     float deltaTime,
     GroupMovementManager groupMovementManager)
        {
            if (deltaTime <= 0f) return;

            // Буфер для быстрого сканирования коллизий прямо внутри шага
            var localNeighborBuffer = new System.Collections.Generic.List<int>(8);

            for (int unitId = 0; unitId < units.Count; unitId++)
            {
                // // 0. ТИКАНИЕ И ПРОВЕРКА ТАЙМЕРА ОЖИДАНИЯ
                if (units.MovementCooldowns[unitId] > 0f)
                {
                    units.MovementCooldowns[unitId] -= deltaTime;
                    if (units.MovementCooldowns[unitId] > 0f)
                    {
                        var curPos = units.Positions[unitId].Spatial;
                        units.Positions[unitId].RenderX += (curPos.X - units.Positions[unitId].RenderX) * 15f * deltaTime;
                        units.Positions[unitId].RenderY += (curPos.Y - units.Positions[unitId].RenderY) * 15f * deltaTime;
                        continue;
                    }
                }

                // // 1. ПРОВЕРКА СМЕРТИ ЮНИТА
                if (units.HealthMasks[unitId] == 0)
                {
                    ref var m = ref units.Movement[unitId];
                    if (m.State == MovementState.Moving) Stop(units, unitId);
                    continue;
                }

                ref var movement = ref units.Movement[unitId];
                ref var position = ref units.Positions[unitId];

                // // 2. ЮНИТ СТОИТ И ГОТОВ ДЕЙСТВОВАТЬ
                if (movement.State != MovementState.Moving)
                {
                    if (groupMovementManager._unitSubWaypointsCount[unitId] > 0)
                    {
                        ref var waypoint = ref groupMovementManager._unitSubWaypointsBuffer[unitId, 0];
                        short targetX = waypoint.X;
                        short targetY = waypoint.Y;
                        var currentPos = position.Spatial;

                        SpatialCoord nextTileStep = CalculateImmediateGreedyStep(currentPos, targetX, targetY);

                        // --- ФИЗОН-ОБХОД: ПРОВЕРКА ОСВОБОЖДЕННОГО ПРОСТРАНСТВА ---
                        bool tileIsPhysicallyBlocked = false;

                        // ИСПРАВЛЕНО: Вызываем ваш метод без передачи буфера. 
                        // Он сам вернет оптимизированный _queryBuffer.
                        var nearbyUnits = spatialGrid.GetUnitsAt(nextTileStep);

                        if (nearbyUnits.Count > 0)
                        {
                            // Клетка занята в памяти, но проверяем визуальный сдвиг на рендере
                            tileIsPhysicallyBlocked = true;

                            // Так как nearbyUnits возвращает IReadOnlyList, мы можем безопасно пробежаться по нему циклом
                            for (int idx = 0; idx < nearbyUnits.Count; idx++)
                            {
                                int otherId = nearbyUnits[idx];
                                ref var otherPos = ref units.Positions[otherId];

                                // Считаем, насколько далеко картинка соседа улетела от центра его тайла
                                float offsetX = Math.Abs(otherPos.RenderX - otherPos.Spatial.X);
                                float offsetY = Math.Abs(otherPos.RenderY - otherPos.Spatial.Y);

                                // Если push-система выжала соседа к краю больше чем на 0.35 тайла,
                                // проход свободен — разрешаем протиснуться!
                                if (offsetX > 0.35f || offsetY > 0.35f)
                                {
                                    tileIsPhysicallyBlocked = false;
                                    break;
                                }
                            }
                        }

                        if (tileIsPhysicallyBlocked)
                        {
                            // Клетка наглухо занята по центру, берем микро-паузу
                            units.MovementCooldowns[unitId] = 0.10f;
                            continue;
                        }

                        // Проверка стен по DDA
                        bool isPathClear = VisibilityChecker.HasLineOfSight(map, edifices, currentPos.X, currentPos.Y, targetX, targetY, currentPos.Z);
                        if (!isPathClear)
                        {
                            units.MovementCooldowns[unitId] = 1.0f;
                            movement.State = MovementState.Idle;
                            groupMovementManager._unitSubWaypointsCount[unitId] = 0;
                            continue;
                        }

                        TryStartMove(units, unitId, nextTileStep, spatialGrid, map);
                    }
                    else
                    {
                        position.RenderX += (position.Spatial.X - position.RenderX) * 12f * deltaTime;
                        position.RenderY += (position.Spatial.Y - position.RenderY) * 12f * deltaTime;
                    }
                }


                // // 3. ЮНИТ ДВИГАЕТСЯ
                if (movement.State == MovementState.Moving)
                {
                    float speed = CalculateSpeed(units, unitId);
                    float heightMultiplier = MovementRules.GetHeightSpeedMultiplier(map, movement.SourceCell.X, movement.SourceCell.Y, movement.TargetCell.X, movement.TargetCell.Y, movement.ZLevel);
                    speed *= heightMultiplier;

                    movement.Progress += speed * deltaTime;
                    if (movement.Progress > 1f) movement.Progress = 1f;

                    // Линейная траектория движения между центрами ячеек
                    float targetRenderX = movement.SourceCell.X + (movement.TargetCell.X - movement.SourceCell.X) * movement.Progress;
                    float targetRenderY = movement.SourceCell.Y + (movement.TargetCell.Y - movement.SourceCell.Y) * movement.Progress;

                    // Упругое притягивание рендера (25f дает идеальный баланс четкости и работы PushSystem)
                    position.RenderX += (targetRenderX - position.RenderX) * 25f * deltaTime;
                    position.RenderY += (targetRenderY - position.RenderY) * 25f * deltaTime;

                    // // 4. ЮНИТ ФИЗИЧЕСКИ ЗАВЕРШИЛ ШАГ ТАЙЛА
                    if (movement.Progress >= 1f)
                    {
                        // Получаем текущую цель до очистки прогресса
                        ref var currentWp = ref groupMovementManager._unitSubWaypointsBuffer[unitId, 0];

                        // Фиксируем физическую смену клетки в памяти сетки SpatialGrid
                        FinishMove(units, spatialGrid, unitId);

                        // ПРОВЕРКА: Достиг ли юнит самого вейпоинта?
                        if (position.Spatial.X == currentWp.X && position.Spatial.Y == currentWp.Y)
                        {
                            // ЦЕЛЬ ДОСТИГНУТА: Удаляем вейпоинт из буфера
                            int remainingWps = groupMovementManager._unitSubWaypointsCount[unitId];
                            for (int w = 0; w < remainingWps - 1; w++)
                            {
                                groupMovementManager._unitSubWaypointsBuffer[unitId, w] = groupMovementManager._unitSubWaypointsBuffer[unitId, w + 1];
                            }
                            groupMovementManager._unitSubWaypointsCount[unitId]--;

                            // Полностью останавливаемся, так как пришли в конечную точку этого вейпоинта
                            movement.State = MovementState.Idle;
                            movement.Progress = 0f;
                            continue;
                        }

                        // ЦЕЛЬ ЕЩЕ ДАЛЕКО: Юнит просто прошел промежуточный тайл на пути к ней.
                        // Продолжаем движение бесшовно, без сброса в Idle!
                        if (groupMovementManager._unitSubWaypointsCount[unitId] > 0)
                        {
                            // Рассчитываем следующий микро-шаг в сторону той же далекой цели
                            SpatialCoord nextTile = CalculateImmediateGreedyStep(position.Spatial, currentWp.X, currentWp.Y);

                            // Наша стандартная физоновая проверка "обочины" через SpatialGrid
                            var nextTileUnits = spatialGrid.GetUnitsAt(nextTile);
                            bool nextTileFree = nextTileUnits.Count == 0;

                            if (!nextTileFree)
                            {
                                for (int idx = 0; idx < nextTileUnits.Count; idx++)
                                {
                                    int otherId = nextTileUnits[idx];
                                    if (Math.Abs(units.Positions[otherId].RenderX - units.Positions[otherId].Spatial.X) > 0.35f ||
                                        Math.Abs(units.Positions[otherId].RenderY - units.Positions[otherId].Spatial.Y) > 0.35f)
                                    {
                                        nextTileFree = true;
                                        break;
                                    }
                                }
                            }

                            bool canContinue = nextTileFree && MovementRules.CanStep(map, position.Spatial.X, position.Spatial.Y, nextTile.X, nextTile.Y, position.Spatial.Z);

                            if (canContinue)
                            {
                                // Плавный переход: сдвигаем Source строго в текущую клетку, обновляем цель шага
                                movement.SourceCell = position.Spatial;
                                movement.TargetCell = nextTile;
                                movement.Progress = 0f;
                                movement.State = MovementState.Moving; // Стейт остается Moving, дёргания не будет!
                            }
                            else
                            {
                                // ПРЕРЫВАНИЕ: Путь заблокирован, аварийно встаем в Idle на текущем тайле
                                movement.State = MovementState.Idle;
                                movement.Progress = 0f;
                                groupMovementManager._unitSubWaypointsCount[unitId] = 0; // Сбрасываем буфер
                                units.MovementCooldowns[unitId] = 0.4f;
                            }
                        }
                        else
                        {
                            // На всякий случай засыпаем, если буфер опустел
                            movement.State = MovementState.Idle;
                            movement.Progress = 0f;
                        }
                    }
                }


            }
        }
        


        private SpatialCoord CalculateImmediateGreedyStep(SpatialCoord current, short targetX, short targetY)
        {
            int nextX = current.X;
            int nextY = current.Y;

            if (targetX > current.X) nextX++;
            else if (targetX < current.X) nextX--;

            if (targetY > current.Y) nextY++;
            else if (targetY < current.Y) nextY--;

            return new SpatialCoord(nextX, nextY, current.Z);
        }

        private void FinishMove(UnitStore units, UnitSpatialGrid spatialGrid, int unitId)
        {
            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            units.HasJustFinishedMoveStep[unitId] = true;
            units.LastVisitedSourceCell[unitId] = movement.SourceCell;

            spatialGrid.Remove(position.Spatial, unitId);
            position.Spatial = movement.TargetCell;
            spatialGrid.Add(position.Spatial, unitId);

            movement.Progress = 0f;
            movement.State = MovementState.Idle;
        }

        private float CalculateSpeed(UnitStore units, int unitId)
        {
            float mass = units.DynamicMass[unitId];
            float massFactor = 1f - mass / 1000f;
            if (massFactor < 0.1f) massFactor = 0.1f;

            float healthFactor = HealthSystem.GetSpeedModifier(units.HealthMasks[unitId], units.BloodLossLevels[unitId]);
            return units.Movement[unitId].Speed * massFactor * healthFactor;
        }

        public void Stop(UnitStore units, int unitId)
        {
            if (unitId < 0 || unitId >= units.Count) return;

            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            movement.SourceCell = position.Spatial;
            movement.TargetCell = position.Spatial;
            movement.Progress = 0f;
            movement.State = MovementState.Idle;
        }
    }
}
