using System;
using System.Collections.Generic;
using Core.Unit.Components;

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

            // Радиус поиска в микро-сетке. 1 ячейка вокруг себя (квадрат 3х3 микро-ячейки) — этого более чем достаточно
            const int GridSearchRadius = 1;

            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0) continue; // Мертвые муравьи не расталкивают живых

                ref var posA = ref units.Positions[i];
                ref var moveA = ref units.Movement[i];

                // Собираем всех соседей в радиусе 1 микро-ячейки через наш супер-быстрый грид
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
                    if (posA.Z != posB.Z) continue;

                    // Считаем вектор расстояния между муравьями по их текущим визуальным координатам рендера
                    float dx = posA.RenderX - posB.RenderX;
                    float dy = posA.RenderY - posB.RenderY;

                    float distanceSqr = dx * dx + dy * dy;

                    // Если муравьи разошлись дальше безопасной дистанции — коллизии нет
                    if (distanceSqr >= MinDistance * MinDistance || distanceSqr <= 0f) continue;

                    float distance = (float)Math.Sqrt(distanceSqr);
                    // Вычисляем, насколько сильно муравьи залезли друг на друга
                    float penetration = MinDistance - distance;

                    // Нормализуем вектор направления расталкивания
                    float pushX = dx / distance;
                    float pushY = dy / distance;

                    // Сила расталкивания (коэффициент мягкости)
                    // Умножаем на deltaTime, чтобы расталкивание происходило плавно, а не мгновенным рывком
                    float pushForce = penetration * 5f * deltaTime;

                    // ПРАВИЛО РИМВОРЛДА: Кто движется, у того приоритет!
                    // Если муравей А идет, а муравей Б стоит (Idle), то муравей А расталкивает муравья Б со всей силы, 
                    // а сам почти не отклоняется от своего маршрута, чтобы идти плавно.
                    if (moveA.State == MovementState.Moving && moveB.State != MovementState.Moving)
                    {
                        // Муравей А идет напролом, муравей Б (Idle) полностью принимает удар и отлетает в сторону
                        posB.RenderX -= pushX * pushForce;
                        posB.RenderY -= pushY * pushForce;
                    }
                    else if (moveA.State != MovementState.Moving && moveB.State == MovementState.Moving)
                    {
                        // Наоборот: муравей Б идет, значит муравей А (Idle) вежливо уступает дорогу
                        posA.RenderX += pushX * pushForce;
                        posA.RenderY += pushY * pushForce;
                    }
                    else
                    {
                        // Если оба стоят ИЛИ оба идут толпой — они расталкивают друг друга поровну (50 на 50)
                        float halfPush = pushForce * 0.5f;

                        posA.RenderX += pushX * halfPush;
                        posA.RenderY += pushY * halfPush;

                        posB.RenderX -= pushX * halfPush;
                        posB.RenderY -= pushY * halfPush;
                    }
                }
            }
        }
    }
}
