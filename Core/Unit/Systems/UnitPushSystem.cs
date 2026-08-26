using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class UnitPushSystem
    {
        private readonly List<int> _neighborBuffer = new List<int>(16);

        public void Update(UnitStore units, UnitSpatialGrid spatialGrid, float deltaTime, WorldMap map)
        {
            if (deltaTime <= 0f) return;

            // MaxUnitsPerTile = 3 зашит в логику распределения сетки
            const int MaxUnitsPerTile = 3;
            const float UnitRadius = 0.35f; // Настроили радиус для красивого наслоения троих в тайле
            const float MinDistance = UnitRadius * 2f;
            const int GridSearchRadius = 1;

            // --- ШАГ 1: ГИДРАВЛИЧЕСКОЕ РАСТАЛКИВАНИЕ (МЯГКОЕ ТЕЧЕНИЕ) ---
            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue;

                ref var posA = ref units.Positions[i];
                ref var moveA = ref units.Movement[i];

                _neighborBuffer.Clear();
                spatialGrid.GetNearby(posA.Spatial, GridSearchRadius, _neighborBuffer);

                for (int idx = 0; idx < _neighborBuffer.Count; idx++)
                {
                    int j = _neighborBuffer[idx];
                    if (i == j || units.HealthMasks[j] == 0) continue;

                    ref var posB = ref units.Positions[j];
                    ref var moveB = ref units.Movement[j];

                    if (posA.Spatial.Z != posB.Spatial.Z) continue;

                    float dx = posA.RenderX - posB.RenderX;
                    float dy = posA.RenderY - posB.RenderY;
                    float distanceSqr = (dx * dx) + (dy * dy);

                    if (distanceSqr >= MinDistance * MinDistance || distanceSqr <= 0f) continue;

                    float distance = (float)Math.Sqrt(distanceSqr);
                    float penetration = MinDistance - distance;

                    // ФИЗИКА ВОДЫ: Сделали силу расталкивания более упругой (13.5f), 
                    // чтобы муравьи мощно занимали всё доступное пространство внутри тайла, 
                    // затекая в любые свободные щели между сородичами.
                    float fluidForce = penetration * 13.5f;

                    float pushX = dx / distance;
                    float pushY = dy / distance;

                    // Закручивание потока для эффекта обтекания
                    float perpX = -pushY * 0.15f;
                    float perpY = pushX * 0.15f;

                    float finalPushX = pushX + perpX;
                    float finalPushY = pushY + perpY;

                    float applyForce = fluidForce * deltaTime;

                    posA.RenderX += finalPushX * applyForce * 0.5f;
                    posA.RenderY += finalPushY * applyForce * 0.5f;
                    posB.RenderX -= finalPushX * applyForce * 0.5f;
                    posB.RenderY -= finalPushY * applyForce * 0.5f;
                }
            }

            // --- ШАГ 2: ВЯЗКОЕ СОПРОТИВЛЕНИЕ СТЕН И СИНХРОНИЗАЦИЯ СЕТКИ ---
            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue;

                ref var pos = ref units.Positions[i];
                ref var move = ref units.Movement[i];

                // ВЯЗКИЙ БАРЬЕР: Смотрим, куда расталкивание сместило визуал. 
                // Если картинка начинает выползать на непроходимый тайл (стену или черную зону),
                // мы принудительно выталкиваем её обратно в центр нашей проходимой белой дорожки!
                int checkTileX = (int)Math.Round(pos.RenderX);
                int checkTileY = (int)Math.Round(pos.RenderY);

                if (!MovementRules.CanStep(map, pos.Spatial.X, pos.Spatial.Y, checkTileX, checkTileY, pos.Spatial.Z))
                {
                    // Стена! Мягко, но упруго пружиним визуал обратно в границы нашей клетки
                    pos.RenderX += (pos.Spatial.X - pos.RenderX) * 22f * deltaTime;
                    pos.RenderY += (pos.Spatial.Y - pos.RenderY) * 22f * deltaTime;
                }

                // Логика физического выталкивания лишних по сетке (если N > 3)
                int unitsOnCurrentTile = spatialGrid.GetUnitsAt(pos.Spatial).Count;

                if (unitsOnCurrentTile > MaxUnitsPerTile)
                {
                    int actualTileX = (int)Math.Round(pos.RenderX);
                    int actualTileY = (int)Math.Round(pos.RenderY);

                    if (actualTileX != pos.Spatial.X || actualTileY != pos.Spatial.Y)
                    {
                        SpatialCoord candidateTile = new SpatialCoord(actualTileX, actualTileY, pos.Spatial.Z);

                        // Мягко выпихиваем на соседний свободный тайл дорожки, если там меньше 3 юнитов
                        if (spatialGrid.GetUnitsAt(candidateTile).Count < MaxUnitsPerTile &&
                            MovementRules.CanStep(map, pos.Spatial.X, pos.Spatial.Y, candidateTile.X, candidateTile.Y, pos.Spatial.Z))
                        {
                            spatialGrid.Remove(pos.Spatial, i);
                            pos.Spatial = candidateTile;
                            spatialGrid.Add(pos.Spatial, i);

                            if (move.State == MovementState.Moving)
                            {
                                move.SourceCell = pos.Spatial;
                                move.Progress = 0f;
                            }
                        }
                    }
                }
            }
        }
    }
}
