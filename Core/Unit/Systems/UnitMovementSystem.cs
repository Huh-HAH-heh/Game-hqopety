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
using System.Collections.Generic;

namespace Core.Unit
{
    public sealed class UnitMovementSystem
    {
        /// <summary>
        /// Попытка запустить физическое перемещение в соседнюю ячейку сетки.
        /// </summary>
        public bool TryStartMove(UnitStore units, int unitId, SpatialCoord targetCell, UnitSpatialGrid spatialGrid, WorldMap map)
        {
            if (unitId < 0 || unitId >= units.Count) return false;
            if (units.HealthMasks[unitId] == 0) return false;

            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            if (movement.State == MovementState.Moving) return false;

            // Проверка проходимости тайла по логике карты
            if (!MovementRules.CanStep(map, position.Spatial.X, position.Spatial.Y, targetCell.X, targetCell.Y, position.Spatial.Z))
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            // ЖЕСТКИЙ ЛИМИТ: На одну ячейку памяти может наступить МАКСИМУМ 3 юнита!
            if (spatialGrid.GetUnitsAt(targetCell).Count >= 3)
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            movement.SourceCell = position.Spatial;
            movement.TargetCell = targetCell;
            movement.ZLevel = position.Spatial.Z;
            movement.Progress = 0f;
            movement.State = MovementState.Moving;

            return true;
        }
        /// <summary>
        /// Основной конвейер обновления движения муравьев.
        /// </summary>
        public void Update(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, EdificeStore edifices, float deltaTime, GroupMovementManager groupMovementManager)
        {
            if (deltaTime <= 0f) return;

            for (int unitId = 0; unitId < units.Count; unitId++)
            {
                if (units.MovementCooldowns[unitId] > 0f)
                {
                    units.MovementCooldowns[unitId] -= deltaTime;
                    if (units.MovementCooldowns[unitId] > 0f)
                    {
                        var curPos = units.Positions[unitId].Spatial;
                        units.Positions[unitId].RenderX += (curPos.X - units.Positions[unitId].RenderX) * 12f * deltaTime;
                        units.Positions[unitId].RenderY += (curPos.Y - units.Positions[unitId].RenderY) * 12f * deltaTime;
                        continue;
                    }
                }

                if (units.MovementCooldowns[unitId] <= 0f && units.Movement[unitId].State == MovementState.Idle)
                {
                    units.MovementCooldowns[unitId] -= deltaTime;
                }

                if (units.HealthMasks[unitId] == 0)
                {
                    ref var m = ref units.Movement[unitId];
                    if (m.State == MovementState.Moving) Stop(units, unitId);
                    continue;
                }

                ref var movement = ref units.Movement[unitId];
                ref var position = ref units.Positions[unitId];

                // // 2. ЮНИТ СТОИТ И ГОТОВ ДЕЙСТВОВАТЬ (IDLE)
                if (movement.State != MovementState.Moving)
                {
                    if (groupMovementManager._unitSubWaypointsCount[unitId] > 0)
                    {
                        ref var waypoint = ref groupMovementManager._unitSubWaypointsBuffer[unitId, 0];
                        SpatialCoord finalTarget = new SpatialCoord(waypoint.X, waypoint.Y, position.Spatial.Z);

                        if (units.MovementCooldowns[unitId] < -2.0f)
                        {
                            movement.State = MovementState.Idle;
                            movement.Progress = 0f;
                            groupMovementManager._unitSubWaypointsCount[unitId] = 0;
                            units.MovementCooldowns[unitId] = 0.4f;
                            continue;
                        }

                        // ГИДРО-РАСПИХИВАНИЕ: Если вейпоинт физически забит (>= 3 муравьев), 
                        // пускаем волну BFS для поиска ближайшей свободной ячейки на дорожке
                        if (spatialGrid.GetUnitsAt(finalTarget).Count >= 3 && (position.Spatial.X != finalTarget.X || position.Spatial.Y != finalTarget.Y))
                        {
                            finalTarget = FindNearestFreeTile(finalTarget, spatialGrid, map);
                            waypoint.X = (short)finalTarget.X;
                            waypoint.Y = (short)finalTarget.Y;
                        }

                        SpatialCoord nextTileStep = CalculateImmediateGreedyStep(position.Spatial, finalTarget.X, finalTarget.Y);

                        if (spatialGrid.GetUnitsAt(nextTileStep).Count >= 3)
                        {
                            units.MovementCooldowns[unitId] = 0.05f;
                            continue;
                        }

                        bool isPathClear = VisibilityChecker.HasLineOfSight(map, edifices, position.Spatial.X, position.Spatial.Y, finalTarget.X, finalTarget.Y, position.Spatial.Z);
                        if (!isPathClear)
                        {
                            units.MovementCooldowns[unitId] = 1.0f;
                            movement.State = MovementState.Idle;
                            groupMovementManager._unitSubWaypointsCount[unitId] = 0;
                            continue;
                        }

                        units.MovementCooldowns[unitId] = 0f;
                        TryStartMove(units, unitId, nextTileStep, spatialGrid, map);
                    }
                    else
                    {
                        // ЖИДКАЯ ПАРКОВКА: Вместо жесткого Clampa мы используем вязкое затухание (6.0f).
                        // Сетка притягивает муравьев к центру, но делает это ОЧЕНЬ мягко,
                        // позволяя гидро-пушу распределять их по всей площади тайла дорожки!
                        position.RenderX += (position.Spatial.X - position.RenderX) * 6.0f * deltaTime;
                        position.RenderY += (position.Spatial.Y - position.RenderY) * 6.0f * deltaTime;
                    }
                }

                // // 3. ЮНИТ НАХОДИТСЯ В ПРОЦЕССЕ ДВИЖЕНИЯ
                if (movement.State == MovementState.Moving)
                {
                    float speed = CalculateSpeed(units, unitId);
                    float heightMultiplier = MovementRules.GetHeightSpeedMultiplier(map, movement.SourceCell.X, movement.SourceCell.Y, movement.TargetCell.X, movement.TargetCell.Y, movement.ZLevel);
                    speed *= heightMultiplier;

                    movement.Progress += speed * deltaTime;
                    if (movement.Progress > 1f) movement.Progress = 1f;

                    float targetRenderX = movement.SourceCell.X + (movement.TargetCell.X - movement.SourceCell.X) * movement.Progress;
                    float targetRenderY = movement.SourceCell.Y + (movement.TargetCell.Y - movement.SourceCell.Y) * movement.Progress;

                    position.RenderX += (targetRenderX - position.RenderX) * 16f * deltaTime;
                    position.RenderY += (targetRenderY - position.RenderY) * 16f * deltaTime;

                    if (movement.Progress >= 1f)
                    {
                        ref var currentWp = ref groupMovementManager._unitSubWaypointsBuffer[unitId, 0];
                        FinishMove(units, spatialGrid, unitId);

                        if (position.Spatial.X == currentWp.X && position.Spatial.Y == currentWp.Y)
                        {
                            int remainingWps = groupMovementManager._unitSubWaypointsCount[unitId];
                            for (int w = 0; w < remainingWps - 1; w++)
                            {
                                groupMovementManager._unitSubWaypointsBuffer[unitId, w] = groupMovementManager._unitSubWaypointsBuffer[unitId, w + 1];
                            }
                            groupMovementManager._unitSubWaypointsCount[unitId]--;
                            movement.State = MovementState.Idle;
                            movement.Progress = 0f;
                            continue;
                        }

                        if (groupMovementManager._unitSubWaypointsCount[unitId] > 0)
                        {
                            SpatialCoord nextTile = CalculateImmediateGreedyStep(position.Spatial, currentWp.X, currentWp.Y);
                            bool canContinue = spatialGrid.GetUnitsAt(nextTile).Count < 3 &&
                                               MovementRules.CanStep(map, position.Spatial.X, position.Spatial.Y, nextTile.X, nextTile.Y, position.Spatial.Z);

                            if (canContinue)
                            {
                                movement.SourceCell = position.Spatial;
                                movement.TargetCell = nextTile;
                                movement.Progress = 0f;
                                movement.State = MovementState.Moving;
                            }
                            else
                            {
                                movement.State = MovementState.Idle;
                                movement.Progress = 0f;
                                units.MovementCooldowns[unitId] = 0.15f;
                            }
                        }
                        else
                        {
                            movement.State = MovementState.Idle;
                            movement.Progress = 0f;
                        }
                    }
                }
            }
        }
        private SpatialCoord FindNearestFreeTile(SpatialCoord center, UnitSpatialGrid spatialGrid, WorldMap map)
        {
            int x = 0, y = 0;
            int dx = 0, dy = -1;
            int maxSteps = 81; // Радиус поиска до 4 клеток вокруг вейпоинта (9х9 тайлов)

            for (int i = 0; i < maxSteps; i++)
            {
                if (x >= -4 && x <= 4 && y >= -4 && y <= 4)
                {
                    SpatialCoord testTile = new SpatialCoord(center.X + x, center.Y + y, center.Z);

                    // Ячейка подходит, если на ней в памяти строго меньше 3 муравьев
                    if (spatialGrid.GetUnitsAt(testTile).Count < 3 &&
                        MovementRules.CanStep(map, center.X, center.Y, testTile.X, testTile.Y, center.Z))
                    {
                        return testTile;
                    }
                }

                if (x == y || (x < 0 && x == -y) || (x > 0 && x == 1 - y))
                {
                    int temp = dx; dx = -dy; dy = temp;
                }
                x += dx; y += dy;
            }
            return center;
        }

        private SpatialCoord CalculateImmediateGreedyStep(SpatialCoord current, int targetX, int targetY)
        {
            int nextX = current.X; int nextY = current.Y;
            if (targetX > current.X) nextX++; else if (targetX < current.X) nextX--;
            if (targetY > current.Y) nextY++; else if (targetY < current.Y) nextY--;
            return new SpatialCoord(nextX, nextY, current.Z);
        }

        private void FinishMove(UnitStore units, UnitSpatialGrid spatialGrid, int unitId)
        {
            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];
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
            return units.Movement[unitId].Speed * massFactor;
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
