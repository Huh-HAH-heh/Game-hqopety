using System;
using System.Collections.Generic;
using Core.Unit;

namespace Core.Unit.Systems
{
    public sealed class UnitPushSystem
    {
        // Переиспользуемый буфер для поиска соседей (0 аллокаций в кадре)
        private readonly List<int> _neighborBuffer = new List<int>(16);

        public void Update(UnitStore units, UnitSpatialGrid spatialGrid, float deltaTime)
        {
            if (deltaTime <= 0f) return;

            // Радиус коллизии маленького муравья в масштабе микро-ячеек (например, 0.4 микро-ячейки)
            // Радиус 0.4 означает, что диаметр муравья 0.8 ячейки, и они идеально вписываются по одному в клетку
            const float UnitRadius = 0.4f;
            const float MinDistance = UnitRadius * 2f; // Расстояние, ближе которого начнется расталкивание
            const int GridSearchRadius = 1; // Радиус поиска в микро-сетке (квадрат 3х3 клетки)

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue; // Мертвые муравьи не расталкивают живых

                ref var posA = ref units.Positions[i];
                ref var moveA = ref units.Movement[i];

                // Собираем всех соседей в радиусе 1 микро-ячейки через быстрый пространственный граф
                _neighborBuffer.Clear();
                spatialGrid.GetNearby(posA.Spatial, GridSearchRadius, _neighborBuffer);

                for (int idx = 0; idx < _neighborBuffer.Count; idx++)
                {
                    int j = _neighborBuffer[idx];

                    // Не расталкиваем самого себя и мертвых
                    if (i == j || units.HealthMasks[j] == 0) continue;

                    ref var posB = ref units.Positions[j];
                    ref var moveB = ref units.Movement[j];

                    // Расталкивание работает только на одном Z-этаже
                    if (posA.Spatial.Z != posB.Spatial.Z) continue;

                    // Считаем вектор расстояния между муравьями по их визуальным render-координатам
                    float dx = posA.RenderX - posB.RenderX;
                    float dy = posA.RenderY - posB.RenderY;

                    float distanceSqr = (dx * dx) + (dy * dy);

                    // Если муравьи разошлись дальше безопасной дистанции — коллизии нет
                    if (distanceSqr >= MinDistance * MinDistance || distanceSqr <= 0f) continue;

                    float distance = (float)Math.Sqrt(distanceSqr);

                    // Вычисляем, насколько сильно муравьи налезли друг на друга
                    float penetration = MinDistance - distance;

                    // Нормализуем вектор направления расталкивания
                    float pushX = dx / distance;
                    float dyPushY = dy / distance;

                    // Сила расталкивания с коэффициентом мягкости
                    // Умножаем на deltaTime для плавного скольжения без рывков
                    float pushForce = penetration * 8f * deltaTime; // Слегка увеличили коэффициент до 8f для отзывчивости

                    // =========================================================================
                    // // ПРАВИЛО RIMWORLD: Кто движется, у того приоритет!
                    // =========================================================================
                    if (moveA.State == MovementState.Moving && moveB.State != MovementState.Moving)
                    {
                        // Муравей А идет напролом, муравей Б (Idle) уступает дорогу и отлетает в сторону
                        posB.RenderX -= pushX * pushForce * 1.5f;
                        posB.RenderY -= dyPushY * pushForce * 1.5f;
                    }
                    else if (moveA.State != MovementState.Moving && moveB.State == MovementState.Moving)
                    {
                        // Наоборот: муравей Б идет, значит муравей А (Idle) вежливо уступает дорогу
                        posA.RenderX += pushX * pushForce * 1.5f;
                        posA.RenderY += dyPushY * pushForce * 1.5f;
                    }
                    else
                    {
                        // Если оба стоят ИЛИ оба идут толпой — они расталкивают друг друга поровну (50 на 50)
                        float halfPush = pushForce * 0.5f;

                        // ИДУЩИЕ юниты теперь аддитивно смещаются, создавая естественное огибание плеч
                        posA.RenderX += pushX * halfPush;
                        posA.RenderY += dyPushY * halfPush;

                        posB.RenderX -= pushX * halfPush;
                        posB.RenderY -= dyPushY * halfPush;
                    }
                }
            }
        }
    }
}
