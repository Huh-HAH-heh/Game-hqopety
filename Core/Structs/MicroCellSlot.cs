using System;

namespace Core.Structs
{
    /// <summary>
    /// Легковесный контейнер для передачи информации о микро-ячейке во внешние системы.
    /// </summary>
    public struct MicroCellSlot
    {
        /// <summary>
        /// Существует ли вообще эта ячейка в мире (загружен ли чанк карты).
        /// </summary>
        public bool Exists { get; private set; }

        /// <summary>
        /// Глобальная микро-координата X.
        /// </summary>
        public int GlobalMicroX { get; private set; }

        /// <summary>
        /// Глобальная микро-координата Y.
        /// </summary>
        public int GlobalMicroY { get; private set; }

        /// <summary>
        /// Данные самой микро-ячейки (копия данных на момент запроса).
        /// </summary>
        public MicroCell Cell { get; private set; }

        /// <summary>
        /// Конструктор для существующей ячейки.
        /// </summary>
        public MicroCellSlot(ref MicroCell cell, int globalMicroX, int globalMicroY)
        {
            Exists = true;
            Cell = cell;
            GlobalMicroX = globalMicroX;
            GlobalMicroY = globalMicroY;
        }

        /// <summary>
        /// Конструктор для пустых областей (например, негенерированный космос или выход за границы карты).
        /// </summary>
        public MicroCellSlot(bool exists)
        {
            Exists = exists;
            GlobalMicroX = 0;
            GlobalMicroY = 0;
            Cell = default;
        }
    }
}
