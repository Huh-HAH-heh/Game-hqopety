using Core.Unit.Components;

namespace Core.Unit
{
    /// <summary>
    /// Компонент позиционирования юнита (муравья) в микро-мире.
    /// Хранится в плоском массиве UnitStore.
    /// </summary>
    public struct UnitPosition
    {
        /// <summary>
        /// Глобальные микро-координаты ячейки (X, Y, Z) в мире.
        /// Теперь это физическое открытое поле, доступное для чтения и записи.
        /// </summary>
        public SpatialCoord Spatial;

        /// <summary>
        /// Float-координата X на экране для плавной отрисовки в SFML.
        /// </summary>
        public float RenderX;

        /// <summary>
        /// Float-координата Y на экране для плавной отрисовки в SFML.
        /// </summary>
        public float RenderY;

        /// <summary>
        /// Конструктор для инициализации позиции муравья на старте.
        /// </summary>
        public UnitPosition(SpatialCoord spatial)
        {
            Spatial = spatial;
            // Изначально рендер-координаты совпадают с логической микро-ячейкой
            RenderX = spatial.X;
            RenderY = spatial.Y;
        }

        /// <summary>
        /// Быстрый доступ к координатам напрямую для удобства систем.
        /// </summary>
        public int X => Spatial.X;
        public int Y => Spatial.Y;
        public int Z => Spatial.Z;
    }
}
