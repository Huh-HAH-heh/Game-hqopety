using System;
using Core.Map;
using Core.Structs;

namespace Core.Unit
{
    public static class MovementRules
    {
        // Максимальный перепад высоты, который муравей может преодолеть 
        // за один переход между соседними микро-ячейками.
        public const byte MaxHeightStep = 3;

        /// <summary>
        /// Проверяет, может ли муравей физически наступить на целевую микро-ячейку.
        /// Учитывает границы карты, наличие стен (EdificeId) и перепад высот.
        /// </summary>
        public static bool CanStep(WorldMap map, int srcX, int srcY, int dstX, int dstY, int z)
        {
            // 1. Запрашиваем информацию о целевой микро-ячейке
            MicroCellSlot dstSlot = map.GetMicroCellSlot(dstX, dstY, z);

            // Если чанк не существует (пустой космос/край карты) — идти нельзя
            if (!dstSlot.Exists)
                return false;

            // 2. Проверяем наличие построек/стен в целевой точке
            // Если EdificeId == 1 (стена) или 2 (скала) — путь заблокирован
            if (dstSlot.Cell.EdificeId > 0)
                return false;

            // 3. Запрашиваем исходную ячейку для проверки перепада высот
            MicroCellSlot srcSlot = map.GetMicroCellSlot(srcX, srcY, z);
            if (!srcSlot.Exists)
                return false;

            // Вычисляем разницу высот
            int difference = Math.Abs(dstSlot.Cell.Height - srcSlot.Cell.Height);

            return difference <= MaxHeightStep;
        }

        /// <summary>
        /// Возвращает модификатор скорости движения муравья в зависимости от крутизны подъёма/спуска.
        /// </summary>
        public static float GetHeightSpeedMultiplier(WorldMap map, int srcX, int srcY, int dstX, int dstY, int z)
        {
            MicroCellSlot srcSlot = map.GetMicroCellSlot(srcX, srcY, z);
            MicroCellSlot dstSlot = map.GetMicroCellSlot(dstX, dstY, z);

            if (!srcSlot.Exists || !dstSlot.Exists || dstSlot.Cell.EdificeId > 0)
                return 0.00f;

            int difference = Math.Abs(dstSlot.Cell.Height - srcSlot.Cell.Height);

            return difference switch
            {
                0 => 1.00f, // Ровная микро-поверхность
                1 => 0.85f, // Небольшой уклон
                2 => 0.65f, // Заметный склон
                3 => 0.40f, // Крутой склон (муравей карабкается медленно)
                _ => 0.00f  // Слишком отвесный обрыв/стена
            };
        }
    }
}
