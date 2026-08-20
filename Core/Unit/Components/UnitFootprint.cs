using System;
using Core.Unit.Components;

namespace Core.Unit
{
    public static class UnitFootprint
    {
        /// <summary>
        /// Вызывает onCellFound для каждой микро-ячейки, которую юнит занимает на глобальной сетке.
        /// Поддерживает как маленьких муравьев (1х1), так и больших жуков/машины (2х2, 3х3).
        /// </summary>
        public static void GetOccupiedCells(SpatialCoord center, UnitSize size, Action<SpatialCoord> onCellFound)
        {
            // Теперь center.X и center.Y — это уже готовые глобальные микро-координаты!
            int centerX = center.X;
            int centerY = center.Y;

            // Физический центр юнита лежит посередине микро-ячейки: +0.5f
            float halfWidth = size.WidthCells / 2f;
            float halfHeight = size.HeightCells / 2f;

            float minXf = (centerX + 0.5f) - halfWidth;
            float maxXf = (centerX + 0.5f) + halfWidth;
            float minYf = (centerY + 0.5f) - halfHeight;
            float maxYf = (centerY + 0.5f) + halfHeight;

            // Определяем границы индексов микро-ячеек, которые перекрывает геометрия муравья/жука
            int minCellX = (int)Math.Floor(minXf);
            int maxCellX = (int)Math.Ceiling(maxXf) - 1;
            int minCellY = (int)Math.Floor(minYf);
            int maxCellY = (int)Math.Ceiling(maxYf) - 1;

            // Итерируемся по получившемуся прямоугольнику микро-ячеек
            for (int y = minCellY; y <= maxCellY; y++)
            {
                for (int x = minCellX; x <= maxCellX; x++)
                {
                    // Ошибка исправлена: заменяем center.Tile.Z на прямой center.Z.
                    // Создаем новую SpatialCoord напрямую, без вызова легаси SpatialMath. FromGlobal
                    onCellFound(new SpatialCoord(x, y, center.Z));
                }
            }
        }
    }
}
