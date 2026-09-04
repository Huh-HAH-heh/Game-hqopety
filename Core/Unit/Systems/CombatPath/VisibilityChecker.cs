using System;
using Core.Map;
using Core.Structs;
using Core.Items;

namespace Core.Unit.Systems.CombatPath
{
    public static class VisibilityChecker
    {
        /// <summary>
        /// Профессиональный DDA-рейкаст (Grid Raycast). 
        /// Возвращает true, если между точками А и Б нет сплошных монолитных стен.
        /// Не имеет асимметрии, не цепляет углы кибиток по касательной и работает сверхдешево.
        /// </summary>
        public static bool HasLineOfSight(WorldMap map, EdificeStore edifices, int x0, int y0, int x1, int y1, int z)
        {
            // 1. Быстрый выход, если точки совпадают
            if (x0 == x1 && y0 == y1) return true;

            // 2. Валидация слоя карты
            MapLayer layer = map.GetLayer(z);
            if (layer == null) return false;

            // 3. Быстрая проверка границ один раз на входе вместо проверок внутри цикла
            const int maxCoord = (16 * 48) - 1; // 767
            if (x0 < 0 || x0 > maxCoord || y0 < 0 || y0 > maxCoord ||
                x1 < 0 || x1 > maxCoord || y1 < 0 || y1 > maxCoord) return false;

            // Вычисляем ненормализованные дельты
            int dx = x1 - x0;
            int dy = y1 - y0;

            // Вычисляем шаг изменения координат
            int stepX = dx < 0 ? -1 : 1;
            int stepY = dy < 0 ? -1 : 1;

            // DDA-математика без нормализации вектора и без Mathf.Sqrt
            // Используем инверсию абсолютных значений разностей
            float deltaDistX = (dx == 0) ? float.MaxValue : MathF.Abs(1f / dx);
            float deltaDistY = (dy == 0) ? float.MaxValue : MathF.Abs(1f / dy);

            // Так как старт строго из центра ячейки (+0.5f), до границы всегда ровно половина пути ячейки
            float sideDistX = 0.5f * deltaDistX;
            float sideDistY = 0.5f * deltaDistY;

            int currentX = x0;
            int currentY = y0;

            // Кешируем ссылки на массивы для быстрого доступа внутри цикла
            var instances = edifices?.Instances;
            var configs = edifices?.Configs;
            int instancesLength = instances?.Length ?? 0;
            int configsLength = configs?.Length ?? 0;

            // Главный маршевый цикл DDA
            while (true)
            {
                // Выбираем ось для шага
                if (sideDistX < sideDistY)
                {
                    sideDistX += deltaDistX;
                    currentX += stepX;
                }
                else
                {
                    sideDistY += deltaDistY;
                    currentY += stepY;
                }

                // УСПЕШНЫЙ ФИНИШ: долетели до целевой клетки без препятствий
                if (currentX == x1 && currentY == y1)
                {
                    return true;
                }

                // ПРОВЕРКА ПРЕПЯТСТВИЙ (границы массива гарантированно соблюдены валидацией на входе)
                ref MicroCell cell = ref layer.GetMicroCell(currentX, currentY);

                if (cell.EdificeId > 0 && instances != null)
                {
                    ushort id = cell.EdificeId;
                    if (id < instancesLength)
                    {
                        var instance = instances[id];
                        if (instance.ConfigId < configsLength)
                        {
                            var config = configs[instance.ConfigId];
                            if (config != null && config.Type == EdificeType.Wall && config.CoverEffectiveness >= 1.0f)
                            {
                                return false; // Путь перекрыт сплошной стеной
                            }
                        }
                    }
                }
            }
        }
    }
}
