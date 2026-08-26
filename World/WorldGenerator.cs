using System;
using Core.Map;
using Core.Structs;
using Core.Items;

namespace World
{
    public static class WorldGenerator
    {
        private const int MapSizeInCells = 16 * 48; // 768х768 микро-ячеек

        public static void Generate(WorldMap worldMap, EdificeStore edificeStore)
        {
            // 1. Очищаем карту, заливая базовой ровной землей
            ClearToFlatGround(worldMap);

            // 2. Инициализируем элемент массива Configs по индексу (TypeId/ConfigId = 1)
            if (edificeStore.Configs == null || edificeStore.Configs.Length <= 1)
            {
                edificeStore.Configs = new EdificeConfig[10];
            }

            edificeStore.Configs[1] = new EdificeConfig
            {
                TypeId = 1,
                Name = "Бетонная стена",
                Type = EdificeType.Wall,
                WidthCells = 1,
                HeightCells = 1,
                MaxHitPoints = 600,
                CoverEffectiveness = 1.0f
            };

            // 3. Запускаем рекурсивную генерацию фрактального лабиринта в центре карты
            // Квадрат размером 486х486 идеально делится на 3 на всех уровнях рекурсии (486 -> 162 -> 54 -> 18 -> 6)
            int fractalSize = 486;
            int startX = (MapSizeInCells - fractalSize) / 2; // Центрирование на карте 768
            int startY = (MapSizeInCells - fractalSize) / 2;

            // ИСПРАВЛЕНО: Заменили имя именованного аргумента с zLevel на z, чтобы оно соответствовало сигнатуре метода
            GenerateFractalRooms(worldMap, edificeStore, startX, startY, fractalSize, z: 1, currentDepth: 0, maxDepth: 4);

            Console.WriteLine("[WorldGenerator] Фрактальная сквозная структура Серпинского успешно возведена.");
        }

        private static void ClearToFlatGround(WorldMap worldMap)
        {
            for (int z = worldMap.MinZ; z <= worldMap.MaxZ; z++)
            {
                MapLayer layer = worldMap.GetLayer(z);
                if (layer == null) continue;

                for (int y = 0; y < MapSizeInCells; y++)
                {
                    for (int x = 0; x < MapSizeInCells; x++)
                    {
                        ref MicroCell tile = ref layer.GetMicroCell(x, y);
                        tile.FloorId = 0;     // Базовая трава/пол
                        tile.EdificeId = 0;   // Пусто
                        tile.Flags = 0x0004;  // Проходимо
                        tile.Height = 10;     // Ровный ландшафт
                    }
                }
            }
        }

        private static void GenerateFractalRooms(WorldMap map, EdificeStore store, int x, int y, int size, int z, int currentDepth, int maxDepth)
        {
            if (currentDepth > maxDepth || size < 6) return;

            // Строим стены текущей квадратной комнаты
            BuildPassableRoomFrame(map, store, x, y, size, z);

            // Делим текущий квадрат на 9 равных частей (сетка 3х3)
            int subSize = size / 3;

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    // Центральный сектор (row == 1 && col == 1) оставляем пустым по принципу ковра Серпинского
                    if (row == 1 && col == 1) continue;

                    int subX = x + col * subSize;
                    int subY = y + row * subSize;

                    // Рекурсивный вызов для следующего под-уровня фрактала
                    GenerateFractalRooms(map, store, subX, subY, subSize, z, currentDepth + 1, maxDepth);
                }
            }
        }

        private static void BuildPassableRoomFrame(WorldMap map, EdificeStore store, int startX, int startY, int size, int z)
        {
            int endX = startX + size - 1;
            int endY = startY + size - 1;

            // Определяем центр стен для создания сквозных проемов (дверей)
            int midX = startX + size / 2;
            int midY = startY + size / 2;

            // Размер проема (для больших внешних комнат делаем шире, для внутренних — в 1 клетку)
            int doorRadius = size > 100 ? 2 : 1;

            for (int currY = startY; currY <= endY; currY++)
            {
                for (int currX = startX; currX <= endX; currX++)
                {
                    // Строим строго по периметру квадрата
                    bool isBorder = (currX == startX || currX == endX || currY == startY || currY == endY);
                    if (!isBorder) continue;

                    // --- МАГИЯ СКВОЗНОГО ПРОХОДА ---
                    // Пропускаем постройку стен ровно по центру каждой из 4-х сторон
                    if (Math.Abs(currX - midX) <= doorRadius && (currY == startY || currY == endY)) continue;
                    if (Math.Abs(currY - midY) <= doorRadius && (currX == startX || currX == endX)) continue;

                    ushort gx = unchecked((ushort)currX);
                    ushort gy = unchecked((ushort)currY);

                    // Проверка выхода за границы карты
                    if (gx >= MapSizeInCells || gy >= MapSizeInCells) continue;

                    // Передаем configId = 1
                    store.Build(map, 1, gx, gy, z);
                }
            }
        }
    }
}
