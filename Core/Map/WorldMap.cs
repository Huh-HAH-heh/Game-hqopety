using Core.Structs;
using Core.Unit;
using System;
using System.Collections.Generic;

namespace Core.Map
{
    /// <summary>
    /// Глобальная карта мира, управляющая многоэтажными слоями (MapLayer).
    /// </summary>
    public class WorldMap
    {
        public MicroCellSlot GetMicroCellSlot(int globalMicroX, int globalMicroY, int z)
        {
            MapLayer layer = GetLayer(z);
            if (layer == null) return new MicroCellSlot(false);

            // 1. Вычисляем координаты региона
            int rx = globalMicroX / MapRegion.MicroSize;
            int ry = globalMicroY / MapRegion.MicroSize;

            // 2. Получаем регион из слоя (в космосе/море вы просто проверяете, создан ли этот чанк)
            MapRegion region = layer.GetRegion(rx, ry);
            if (region == null) return new MicroCellSlot(false); // Чанка нет (пустой космос)

            // 3. Достаем локальную микро-ячейку
            int lmx = globalMicroX % MapRegion.MicroSize;
            int lmy = globalMicroY % MapRegion.MicroSize;

            ref MicroCell cell = ref region.GetLocalMicroCell(lmx, lmy);

            return new MicroCellSlot(ref cell, globalMicroX, globalMicroY);
        }


        public int MinZ { get; private set; }
        public int MaxZ { get; private set; }

        /// <summary>
        /// Текущий активный этаж, который сейчас видит игрок на экране.
        /// </summary>
        public int CurrentViewZ { get; private set; }

        private readonly Dictionary<int, MapLayer> _layers;

        public WorldMap(int minZ, int maxZ)
        {
            if (minZ > maxZ)
                throw new ArgumentException("Минимальный этаж Z не может быть больше максимального.");

            MinZ = minZ;
            MaxZ = maxZ;

            CurrentViewZ = (0 >= MinZ && 0 <= MaxZ) ? 0 : MinZ;
            _layers = new Dictionary<int, MapLayer>();

            for (int z = MinZ; z <= MaxZ; z++)
            {
                _layers[z] = new MapLayer(z);
            }
        }

        public MapLayer GetLayer(int z)
        {
            if (_layers.TryGetValue(z, out var layer))
            {
                return layer;
            }
            return null;
        }

        public void ChangeViewFloor(int offset)
        {
            int targetZ = CurrentViewZ + offset;

            if (targetZ < MinZ) targetZ = MinZ;
            if (targetZ > MaxZ) targetZ = MaxZ;

            CurrentViewZ = targetZ;
        }
    }
}
