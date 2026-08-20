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
            // Если юниты стоят в одной и той же микро-ячейке — они гарантированно видят друг друга
            if (x0 == x1 && y0 == y1) return true;

            MapLayer layer = map.GetLayer(z);
            if (layer == null) return false;

            int maxCoord = (16 * 48) - 1; // 767 ячеек макс границы

            // Центрируем стартовую и целевую точки луча (встаем строго в геометрический центр микро-ячеек)
            float startX = x0 + 0.5f;
            float startY = y0 + 0.5f;
            float targetX = x1 + 0.5f;
            float targetY = y1 + 0.5f;

            float dirX = targetX - startX;
            float dirY = targetY - startY;

            // Вычисляем длину вектора взгляда
            float distance = MathF.Sqrt(dirX * dirX + dirY * dirY);
            if (distance <= 0f) return true;

            // Нормализуем направление луча
            dirX /= distance;
            dirY /= distance;

            // Математика DDA: сколько нужно пройти по лучу, чтобы сдвинуться на 1 целую ячейку по X или Y
            float deltaDistX = (MathF.Abs(dirX) < 0.00001f) ? float.MaxValue : MathF.Abs(1f / dirX);
            float deltaDistY = (MathF.Abs(dirY) < 0.00001f) ? float.MaxValue : MathF.Abs(1f / dirY);

            int currentX = x0;
            int currentY = y0;

            float sideDistX;
            float sideDistY;

            int stepX;
            int stepY;

            // Рассчитываем стартовые направления шагов и расстояния до первых границ сетки
            if (dirX < 0)
            {
                stepX = -1;
                sideDistX = (startX - currentX) * deltaDistX;
            }
            else
            {
                stepX = 1;
                sideDistX = (currentX + 1.0f - startX) * deltaDistX;
            }

            if (dirY < 0)
            {
                stepY = -1;
                sideDistY = (startY - currentY) * deltaDistY;
            }
            else
            {
                stepY = 1;
                sideDistY = (currentY + 1.0f - startY) * deltaDistY;
            }

            // Главный маршевый цикл DDA — прыгаем СТРОГО по пересечениям линий сетки карты
            // Перебираем ячейки, пока не доберемся до целевой точки (x1, y1)
            while (true)
            {
                // Выбираем, какая граница сетки ближе к лучу: вертикальная (X) или горизонтальная (Y)
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

                // УСПЕШНЫЙ ФИНИШ: Если луч долетел до целевой микро-ячейки врага, преград на пути нет!
                if (currentX == x1 && currentY == y1)
                {
                    return true;
                }

                // Жесткая проверка физических границ массива микро-карты
                if (currentX >= 0 && currentX <= maxCoord && currentY >= 0 && currentY <= maxCoord)
                {
                    ref MicroCell cell = ref layer.GetMicroCell(currentX, currentY);

                    // Если в текущей ячейке пересечения луча стоит объект/постройка
                    if (cell.EdificeId > 0 && edifices != null)
                    {
                        ushort id = cell.EdificeId;
                        if (id < edifices.Instances.Length)
                        {
                            var instance = edifices.Instances[id];

                            // Проверяем существование паспорта/конфига, полностью блокируя NullReferenceException вылеты
                            if (instance.ConfigId < edifices.Configs.Length && edifices.Configs[instance.ConfigId] != null)
                            {
                                var config = edifices.Configs[instance.ConfigId];

                                // ТАКТИЧЕСКОЕ ПРАВИЛО: Взор и траекторию пули блокируют ТОЛЬКО сплошные белые стены (Cover = 1.0)!
                                // Низкие мешки с песком (Cover = 0.65) или промышленные генераторы обзор НЕ перекрывают.
                                if (config.Type == EdificeType.Wall && config.CoverEffectiveness >= 1.0f)
                                {
                                    return false; // Путь наглухо перекрыт толщей бетона кибитки!
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Если луч каким-то образом вылетел за пределы карты (0..767) — обрываем проверку
                    return false;
                }
            }
        }
    }
}
