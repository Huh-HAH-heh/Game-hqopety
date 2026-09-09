//using System;
//using Core.Map;
//using Core.Structs;
//using Core.Items;

//namespace Core.Unit.Systems.CombatPath
//{
//    public static class VisibilityChecker
//    {
//        /// <summary>
//        /// Дешёвый псевдо-3D DDA LOS.
//        ///
//        /// XY определяет траекторию луча.
//        /// MicroCell.Height определяет высоту поверхности.
//        ///
//        /// Сейчас проверяется только текущий Z-слой.
//        /// </summary>
//        public static bool HasLineOfSight(
//            WorldMap map,
//            EdificeStore edifices,
//            int x0,
//            int y0,
//            int x1,
//            int y1,
//            int z)
//        {
//            // Точки совпадают — всегда видимы.
//            if (x0 == x1 && y0 == y1)
//                return true;

//            MapLayer layer = map.GetLayer(z);
//            if (layer == null)
//                return false;

//            const int maxCoord =
//                MapLayer.WidthInRegions * MapRegion.MicroSize - 1;

//            if (x0 < 0 || x0 > maxCoord ||
//                y0 < 0 || y0 > maxCoord ||
//                x1 < 0 || x1 > maxCoord ||
//                y1 < 0 || y1 > maxCoord)
//            {
//                return false;
//            }

//            // ---------------------------------------------------------
//            // 1. Получаем высоту стартовой и конечной клеток
//            // ---------------------------------------------------------

//            ref MicroCell startCell =
//                ref layer.GetMicroCell(x0, y0);

//            ref MicroCell targetCell =
//                ref layer.GetMicroCell(x1, y1);

//            float startHeight = startCell.Height;
//            float targetHeight = targetCell.Height;

//            // ---------------------------------------------------------
//            // 2. DDA
//            // ---------------------------------------------------------

//            int dx = x1 - x0;
//            int dy = y1 - y0;

//            int stepX = dx < 0 ? -1 : 1;
//            int stepY = dy < 0 ? -1 : 1;

//            float deltaDistX =
//                dx == 0
//                    ? float.MaxValue
//                    : MathF.Abs(1f / dx);

//            float deltaDistY =
//                dy == 0
//                    ? float.MaxValue
//                    : MathF.Abs(1f / dy);

//            float sideDistX =
//                0.5f * deltaDistX;

//            float sideDistY =
//                0.5f * deltaDistY;

//            int currentX = x0;
//            int currentY = y0;

//            // ---------------------------------------------------------
//            // 3. Идём по клеткам
//            // ---------------------------------------------------------

//            while (true)
//            {
//                if (sideDistX < sideDistY)
//                {
//                    sideDistX += deltaDistX;
//                    currentX += stepX;
//                }
//                else
//                {
//                    sideDistY += deltaDistY;
//                    currentY += stepY;
//                }

//                // Дошли до цели.
//                if (currentX == x1 && currentY == y1)
//                    return true;

//                ref MicroCell cell =
//                    ref layer.GetMicroCell(currentX, currentY);

//                // -----------------------------------------------------
//                // 4. Проверка высоты рельефа
//                // -----------------------------------------------------

//                int distanceX = currentX - x0;
//                int distanceY = currentY - y0;

//                int totalDistanceX = x1 - x0;
//                int totalDistanceY = y1 - y0;

//                float t;

//                if (Math.Abs(totalDistanceX) >= Math.Abs(totalDistanceY))
//                {
//                    t = totalDistanceX == 0
//                        ? 0f
//                        : (float)distanceX / totalDistanceX;
//                }
//                else
//                {
//                    t = totalDistanceY == 0
//                        ? 0f
//                        : (float)distanceY / totalDistanceY;
//                }

//                t = Math.Clamp(t, 0f, 1f);

//                float rayHeight =
//                    startHeight +
//                    (targetHeight - startHeight) * t;

//                // -----------------------------------------------------
//                // Если поверхность выше луча — она закрывает обзор.
//                // -----------------------------------------------------

//                if (cell.Height > rayHeight)
//                    return false;

//                // -----------------------------------------------------
//                // 5. Старая проверка монолитной стены
//                // -----------------------------------------------------

//                if (cell.EdificeId > 0 && edifices != null)
//                {
//                    ushort id = cell.EdificeId;

//                    if (id < edifices.Instances.Length)
//                    {
//                        var instance =
//                            edifices.Instances[id];

//                        if (instance.ConfigId < edifices.Configs.Length)
//                        {
//                            var config =
//                                edifices.Configs[instance.ConfigId];

//                            if (config != null &&
//                                config.Type == EdificeType.Wall &&
//                                config.CoverEffectiveness >= 1.0f)
//                            {
//                                return false;
//                            }
//                        }
//                    }
//                }
//            }
//        }
//    }
//}