using Core.Unit.Components;

namespace Core.Unit
{
    /// <summary>
    /// Компонент движения юнита на микро-уровне.
    /// Хранится в плоском массиве UnitStore.
    /// </summary>
    public struct UnitMovement
    {
        /// <summary>
        /// Исходная микро-ячейка, из которой юнит начал текущий шаг.
        /// </summary>
        public SpatialCoord SourceCell;

        /// <summary>
        /// Целевая микро-ячейка текущего шага.
        /// </summary>
        public SpatialCoord TargetCell;

        /// <summary>
        /// Активный Z-уровень текущего шага.
        /// </summary>
        public int ZLevel;

        /// <summary>
        /// Прогресс текущего микро-шага от 0.0f до 1.0f.
        /// </summary>
        public float Progress;

        /// <summary>
        /// Время, в течение которого юнит не продвигался
        /// по текущему шагу.
        /// </summary>
        public float StuckTimer;

        /// <summary>
        /// Базовая скорость перемещения юнита.
        /// </summary>
        public float Speed;

        /// <summary>
        /// Текущее состояние движения.
        /// </summary>
        public MovementState State;
        public SpatialCoord LastAlternativeCell;
        public bool HasLastAlternativeCell;
        public bool IsAlternativeMove;
    }

    public enum MovementState : byte
    {
        Idle,
        Moving,
        Blocked,
        InCover
    }
}