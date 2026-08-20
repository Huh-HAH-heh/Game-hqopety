using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents.Core.Unit.Components;
using Core.Unit.Systems.CombatPath;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class UnitCpuBrainSystem
    {
        private SpatialCoord FindDirectedCover(UnitStore units, int unitId, int hazardX, int hazardY, WorldMap map, EdificeStore edifices)
        {
            ref var pos = ref units.Positions[unitId];
            SpatialCoord currentCell = pos.Spatial;

            MapLayer layer = map.GetLayer(pos.Spatial.Z);
            if (layer == null) return currentCell;

            int maxCoord = (16 * 48) - 1;
            int vecX = Math.Sign(pos.Spatial.X - hazardX);
            int vecY = Math.Sign(pos.Spatial.Y - hazardY);

            int bestCx = -1, bestCy = -1;
            float bestDist = float.MaxValue;

            for (int dy = -5; dy <= 5; dy++)
            {
                for (int dx = -5; dx <= 5; dx++)
                {
                    int cx = pos.Spatial.X + dx;
                    int cy = pos.Spatial.Y + dy;

                    if (cx >= 0 && cx <= maxCoord && cy >= 0 && cy <= maxCoord)
                    {
                        ref MicroCell targetCell = ref layer.GetMicroCell(cx, cy);

                        if (targetCell.EdificeId > 0 && edifices != null)
                        {
                            var instance = edifices.Instances[targetCell.EdificeId];
                            var config = edifices.Configs[instance.ConfigId];

                            if (config != null && config.Type == EdificeType.Wall && config.CoverEffectiveness < 1.0f)
                            {
                                int safeX = cx + vecX;
                                int safeY = cy + vecY;

                                if (safeX >= 0 && safeX <= maxCoord && safeY >= 0 && safeY <= maxCoord)
                                {
                                    ref MicroCell hideCell = ref layer.GetMicroCell(safeX, safeY);
                                    if (hideCell.EdificeId == 0 && (hideCell.Flags & 0x0004) != 0)
                                    {
                                        float d = MathF.Sqrt(dx * dx + dy * dy);
                                        if (d < bestDist) { bestDist = d; bestCx = safeX; bestCy = safeY; }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (bestCx != -1) return new SpatialCoord(bestCx, bestCy, pos.Spatial.Z);
            return currentCell;
        }

        private readonly List<int> _nearbyUnitsBuffer = new List<int>(32);

        public void Update(
            UnitStore units,
            SquadStore squads,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            EdificeStore edifices,
            CombatEffectSystem effects,
            float pixelSize,
            float deltaTime)
        {
            if (deltaTime <= 0f) return;

            int currentZ = map.CurrentViewZ;
            MapLayer layer = map.GetLayer(currentZ);
            if (layer == null) return;

            int maxCoord = (16 * 48) - 1;

            // ========================================================
            // ГЛАВНЫЙ ИГРОВОЙ ЦИКЛ ОБСЛУЖИВАНИЯ ПРОЦЕССОРОВ ЮНИТОВ
            // ========================================================
            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue; // Мертвые процессоры обесточены

                ref var pos = ref units.Positions[i];
                ref var move = ref units.Movement[i];

                // ========================================================
                // ИСПРАВЛЕНО: ФИЗИКА ХОДЬБЫ УДАЛЕНА ИЗ МОЗГА (ТОЛЬКО ЧИСТЫЙ ИИ)
                // Мозг больше не двигает RenderX/Y руками, чтобы не ломать Progress
                // и не конфликтовать со SquadMovementSystem!
                // ========================================================

                // Охлаждение тактических микро-кулдаунов оружия
                if (units.ShotCooldowns[i] > 0f)
                {
                    units.ShotCooldowns[i] -= deltaTime;
                    if (units.ShotCooldowns[i] < 0f) units.ShotCooldowns[i] = 0f;
                }

                // Страховка стека: если задач нет вообще, пушаем базовый Idle
                if (units.AiStackPointers[i] < 0)
                {
                    units.CpuPushCommand(i, new AiCommand { OpCode = AiOpCode.Idle, Timer = 0f });
                }

                // Считываем текущую активную задачу с вершины стека (Регистр Команд)
                ref AiCommand currentCmd = ref units.CpuPeekCommand(i);

                // ========================================================
                // ОБРАБОТКА ХАРДВЕРНЫХ ПРЕРЫВАНИЙ (INTERRUPTS)
                // ========================================================

                // ПРЕРЫВАНИЕ 1: Свистят пули над ухом (Вектор прямого обстрела)
                if (units.LastImpactSourceX[i] != 0)
                {
                    if (currentCmd.OpCode != AiOpCode.TakeCover)
                    {
                        SpatialCoord safeCoverCell = FindDirectedCover(units, i, units.LastImpactSourceX[i], units.LastImpactSourceY[i], map, edifices);

                        units.CpuPushCommand(i, new AiCommand
                        {
                            OpCode = AiOpCode.TakeCover,
                            TargetX = safeCoverCell.X,
                            TargetY = safeCoverCell.Y,
                            Timer = 8f
                        });

                        units.LastImpactSourceX[i] = 0;
                        currentCmd = ref units.CpuPeekCommand(i);
                    }
                }

                // ПРЕРЫВАНИЕ 2: Услышали далекий выстрел (Вектор звука)
                if (units.LastGunshotSourceX[i] != 0 && currentCmd.OpCode == AiOpCode.Idle)
                {
                    units.CpuPushCommand(i, new AiCommand { OpCode = AiOpCode.InvestigateSound, TargetX = units.LastGunshotSourceX[i], TargetY = units.LastGunshotSourceY[i], Timer = 5f });
                    units.LastGunshotSourceX[i] = 0;
                    currentCmd = ref units.CpuPeekCommand(i);
                }

                // ПРЕРЫВАНИЕ 3 — РЕГИСТР БОЛИ И DDA-СКАНЕР (Раз в 10-12 кадров)
                units.VisionTickTimers[i] -= deltaTime;
                if (units.VisionTickTimers[i] <= 0f)
                {
                    units.VisionTickTimers[i] = 0.18f + Random.Shared.NextSingle() * 0.04f;

                    int attackerId = units.LastAttackerIds[i];
                    if (attackerId != -1 && units.HealthMasks[attackerId] > 0 && units.Positions[attackerId].Spatial.Z == pos.Spatial.Z)
                    {
                        if (VisibilityChecker.HasLineOfSight(map, edifices, pos.Spatial.X, pos.Spatial.Y, units.Positions[attackerId].Spatial.X, units.Positions[attackerId].Spatial.Y, pos.Spatial.Z))
                        {
                            if (currentCmd.OpCode != AiOpCode.CombatEngage)
                            {
                                units.CpuPushCommand(i, new AiCommand { OpCode = AiOpCode.CombatEngage, TargetX = attackerId });
                                currentCmd = ref units.CpuPeekCommand(i);
                            }
                            units.LastAttackerIds[i] = -1;
                        }
                    }

                    // АВТОНОМНОЕ СКАН-ЗОНДИРОВАНИЕ ОКРУЖЕНИЯ ЧЕРЕЗ GRID ЧАНКА
                    if (currentCmd.OpCode != AiOpCode.CombatEngage)
                    {
                        int scanRadius = units.IsInCombatMode[i] ? 45 : 15;
                        _nearbyUnitsBuffer.Clear();

                        spatialGrid.GetNearby(pos.Spatial, scanRadius, _nearbyUnitsBuffer);

                        int closestEnemyId = -1;
                        float minDist = float.MaxValue;

                        for (int idx = 0; idx < _nearbyUnitsBuffer.Count; idx++)
                        {
                            int enemyId = _nearbyUnitsBuffer[idx];
                            if (i == enemyId || units.HealthMasks[enemyId] == 0) continue;
                            if (units.UnitType[i] == units.UnitType[enemyId]) continue;
                            if (units.Positions[enemyId].Spatial.Z != pos.Spatial.Z) continue;

                            if (VisibilityChecker.HasLineOfSight(map, edifices, pos.Spatial.X, pos.Spatial.Y, units.Positions[enemyId].Spatial.X, units.Positions[enemyId].Spatial.Y, pos.Spatial.Z))
                            {
                                float rdx = pos.Spatial.X - units.Positions[enemyId].Spatial.X;
                                float rdy = pos.Spatial.Y - units.Positions[enemyId].Spatial.Y;
                                float d = MathF.Sqrt(rdx * rdx + rdy * rdy);

                                if (d < minDist) { minDist = d; closestEnemyId = enemyId; }
                            }
                        }

                        if (closestEnemyId != -1)
                        {
                            units.IsInCombatMode[i] = true;
                            units.CpuPushCommand(i, new AiCommand { OpCode = AiOpCode.CombatEngage, TargetX = closestEnemyId });
                            currentCmd = ref units.CpuPeekCommand(i);
                        }
                    }
                }

                int targetUid = currentCmd.TargetX;
                // ========================================================
                // ИСПРАВЛЕНО: ИСПОЛНЕНИЕ МИРНЫХ ОПЕРАЦИЙ (КОМАНДЫ) ИИ 
                // Теперь ИИ не ломает физику Progress, давая работать SquadMovementSystem!
                // ========================================================
                if (currentCmd.OpCode != AiOpCode.CombatEngage)
                {
                    switch (currentCmd.OpCode)
                    {
                        case AiOpCode.Idle:
                            if (units.UnitType[i] == UnitType.Human)
                            {
                                units.CpuPushCommand(i, new AiCommand { OpCode = AiOpCode.Wander, Timer = (float)Random.Shared.Next(4, 8) });
                            }
                            break;

                        case AiOpCode.Wander:
                            currentCmd.Timer -= deltaTime;

                            // Если отряд никуда не идет, одиночная пешка может слегка поблуждать по асфальту
                            if (move.State == MovementState.Idle && !squads.IsMoving[units.SquadIds[i]])
                            {
                                if (currentCmd.TargetX == 0 && currentCmd.TargetY == 0)
                                {
                                    currentCmd.TargetX = Math.Clamp(pos.Spatial.X + Random.Shared.Next(-5, 6), 0, maxCoord);
                                    currentCmd.TargetY = Math.Clamp(pos.Spatial.Y + Random.Shared.Next(-5, 6), 0, maxCoord);
                                }

                                int sx = Math.Sign(currentCmd.TargetX - pos.Spatial.X);
                                int sy = Math.Sign(currentCmd.TargetY - pos.Spatial.Y);

                                if (sx != 0 || sy != 0)
                                {
                                    // Передаем управление пошаговому легаси-движку симуляции шага
                                    units.Movement[i].TargetCell = new SpatialCoord(pos.Spatial.X + sx, pos.Spatial.Y + sy, pos.Spatial.Z);
                                    units.Movement[i].SourceCell = pos.Spatial;
                                    units.Movement[i].Progress = 0f;
                                    units.Movement[i].State = MovementState.Moving;
                                }
                            }

                            if (currentCmd.Timer <= 0f)
                            {
                                currentCmd.TargetX = 0; currentCmd.TargetY = 0; // Сброс кэша
                                units.CpuPopCommand(i);
                            }
                            break;

                        case AiOpCode.MoveToTarget:
                            // Если активен глобальный марш сквада, одиночный процессор пешки просто ждет финиша.
                            // Перемещение целиком и полностью выполняет твоя SquadMovementSystem!
                            if (pos.Spatial.X == currentCmd.TargetX && pos.Spatial.Y == currentCmd.TargetY)
                            {
                                units.CpuPopCommand(i); // Пришли — счищаем задачу марша
                            }
                            break;

                        case AiOpCode.TakeCover:
                            currentCmd.Timer -= deltaTime;

                            // Индивидуальный пошаговый бег под мешки с песком (если сквад стоит)
                            if (pos.Spatial.X == currentCmd.TargetX && pos.Spatial.Y == currentCmd.TargetY)
                            {
                                move.State = MovementState.InCover;
                            }
                            else if (move.State == MovementState.Idle)
                            {
                                int sx = Math.Sign(currentCmd.TargetX - pos.Spatial.X);
                                int sy = Math.Sign(currentCmd.TargetY - pos.Spatial.Y);

                                units.Movement[i].TargetCell = new SpatialCoord(pos.Spatial.X + sx, pos.Spatial.Y + sy, pos.Spatial.Z);
                                units.Movement[i].SourceCell = pos.Spatial;
                                units.Movement[i].Progress = 0f;
                                units.Movement[i].State = MovementState.Moving;
                            }

                            if (currentCmd.Timer <= 0f)
                            {
                                if (move.State == MovementState.InCover) move.State = MovementState.Idle;
                                units.CpuPopCommand(i);
                            }
                            break;

                        case AiOpCode.InvestigateSound:
                            currentCmd.Timer -= deltaTime;
                            if (move.State == MovementState.Idle)
                            {
                                int sx = Math.Sign(currentCmd.TargetX - pos.Spatial.X);
                                int sy = Math.Sign(currentCmd.TargetY - pos.Spatial.Y);

                                units.Movement[i].TargetCell = new SpatialCoord(pos.Spatial.X + sx, pos.Spatial.Y + sy, pos.Spatial.Z);
                                units.Movement[i].SourceCell = pos.Spatial;
                                units.Movement[i].Progress = 0f;
                                units.Movement[i].State = MovementState.Moving;
                            }
                            if (currentCmd.Timer <= 0f) units.CpuPopCommand(i);
                            break;
                    }
                    continue; // Пропускаем боевой блок стрельбы, если пешка занята мирной операцией!
                }

                // ========================================================
                // ТАКТИЧЕСКИЙ ОГНЕВОЙ БЛОК COMBAT ENGAGE (ВЕДЕНИЕ БОЯ)
                // ========================================================
                int targetId = targetUid;

                // ФАЗА А: Зажим очереди активен (Режим отсечки burst-зажима)
                if (units.RemainingBurstShots[i] > 0)
                {
                    if (units.ShotCooldowns[i] <= 0f)
                    {
                        // !!! Твоя баллистика выстрела будет выполняться здесь !!!
                        // PerformAttack(units, i, targetId, map, edifices, spatialGrid, effects, pixelSize);

                        units.RemainingBurstShots[i]--;

                        if (units.RemainingBurstShots[i] <= 0)
                        {
                            // Очередь отстреляна: убираем голову обратно под защиту мешков, вешаем FireRate-паузу
                            units.IsAiming[i] = false;
                            pos.RenderX = pos.Spatial.X; pos.RenderY = pos.Spatial.Y;
                            units.ShotCooldowns[i] = (units.WeaponSlot[i] != null) ? units.WeaponSlot[i].BaseStats.FireRate : 1.2f;
                        }
                        else
                        {
                            units.ShotCooldowns[i] = 0.1f; // Темп строчки внутри зажима (0.1с между пулями)
                        }
                    }
                    break;
                }

                // ФАЗА Б: Начало вскидки ствола (ИИ блокирует прицеливание, если пушка словила клин перегрева!)
                bool gunReady = units.WeaponSlot[i] == null || !units.WeaponSlot[i].IsOverheated;

                if (!units.IsAiming[i] && units.ShotCooldowns[i] <= 0f && gunReady)
                {
                    units.IsAiming[i] = true;
                    // Таймер изготовления выстрела привязан к скорости стабилизации отдачи оружия
                    currentCmd.Timer = (units.WeaponSlot[i] != null) ? units.WeaponSlot[i].BaseStats.RecoilRecovery * 1.4f : 0.4f;

                    if (move.State == MovementState.InCover)
                    {
                        ref var enemyPos = ref units.Positions[targetId];
                        units.LeanOffsetX[i] = Math.Sign(enemyPos.Spatial.X - pos.Spatial.X);
                        units.LeanOffsetY[i] = Math.Sign(enemyPos.Spatial.Y - pos.Spatial.Y);
                    }
                    break;
                }

                // ФАЗА В: Lean-Выглядывание и полиморфный зажим пушки
                if (units.IsAiming[i] && units.RemainingBurstShots[i] <= 0)
                {
                    currentCmd.Timer -= deltaTime;

                    if (move.State == MovementState.InCover)
                    {
                        // Сдвигаем рендер-координату из-за угла укрытия на 40% ячейки (Тактическое выглядывание RimWorld)
                        pos.RenderX = pos.Spatial.X + (units.LeanOffsetX[i] * 0.4f);
                        pos.RenderY = pos.Spatial.Y + (units.LeanOffsetY[i] * 0.4f);
                    }

                    if (currentCmd.Timer <= 0f)
                    {
                        ref var enemyPos = ref units.Positions[targetId];
                        float ex = pos.Spatial.X - enemyPos.Spatial.X;
                        float ey = pos.Spatial.Y - enemyPos.Spatial.Y;
                        float distanceToEnemy = MathF.Sqrt(ex * ex + ey * ey);

                        int burstCount = 1;
                        if (units.WeaponSlot[i] != null)
                        {
                            // Оружие само оценивает дистанцию и выдает зажим
                            burstCount = units.WeaponSlot[i].GetBurstCountForDistance(distanceToEnemy);
                        }
                        units.RemainingBurstShots[i] = burstCount;
                    }
                }
            } // Конец огромного цикла по юнитам for (int i = 0; i < units.Count; i++)
        }
    }
}
