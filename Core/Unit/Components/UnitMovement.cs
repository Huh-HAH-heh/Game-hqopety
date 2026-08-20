using Core.Unit.Components;

namespace Core.Unit
{
    /// <summary>
    /// Компонент движения юнита (муравья) на микро-уровне.
    /// Хранится в плоском массиве UnitStore.
    /// </summary>
    public struct UnitMovement
    {
        /// <summary>
        /// Исходная микро-ячейка, из которой муравей начал текущий шаг.
        /// </summary>
        public SpatialCoord SourceCell;

        /// <summary>
        /// Целевая микро-ячейка, в которую муравей сейчас ползет.
        /// </summary>
        public SpatialCoord TargetCell;

        /// <summary>
        /// Текущий активный Z-этаж, на котором происходит движение.
        /// </summary>
        public int ZLevel;

        /// <summary>
        /// Прогресс текущего микро-шага (от 0.0f до 1.0f).
        /// </summary>
        public float Progress;

        /// <summary>
        /// Базовая скорость перемещения данного муравья.
        /// </summary>
        public float Speed;

        /// <summary>
        /// Текущее состояние движения (Стоит, Идет, Заблокирован).
        /// </summary>
        public MovementState State;
    }

    public enum MovementState : byte
    {
        Idle,
        Moving,
        Blocked,
            InCover
    }
}
