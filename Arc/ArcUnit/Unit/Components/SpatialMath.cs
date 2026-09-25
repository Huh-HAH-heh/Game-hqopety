using Core.Unit.Components;

namespace Core.Unit
{
    public static class SpatialMath
    {
        // --- СТАРАЯ КОНСТАНТА УДАЛЕНА ЗА НЕНАДОБНОСТЬЮ ---
        // public const int CellsPerTile = 8;

        /// <summary>
        /// Легаси-мост: Возвращает X как есть, так как он уже глобальный.
        /// </summary>
        public static int ToGlobalX(SpatialCoord coord) => coord.X;

        /// <summary>
        /// Легаси-мост: Возвращает Y как есть, так как он уже глобальный.
        /// </summary>
        public static int ToGlobalY(SpatialCoord coord) => coord.Y;

        /// <summary>
        /// Создает координату напрямую из чистых глобальных микро-координат.
        /// </summary>
        public static SpatialCoord FromGlobal(int x, int y, int z)
        {
            // Больше никаких FloorDiv и Mod! Просто создаем структуру.
            return new SpatialCoord(x, y, z);
        }

        /// <summary>
        /// Сдвигает муравья в пространстве на дельту dx, dy.
        /// Работает со скоростью света, так как это просто сложение примитивов.
        /// </summary>
        public static SpatialCoord Move(SpatialCoord source, int dx, int dy)
        {
            return new SpatialCoord(source.X + dx, source.Y + dy, source.Z);
        }
    }
}
