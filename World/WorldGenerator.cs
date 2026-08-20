using Core.Map;
using Core.Structs;
using Core.Items;
using System;

namespace World
{
    public static class WorldGenerator
    {
        public static void Generate(WorldMap worldMap, EdificeStore edificeStore)
        {
            // 1. Полная базовая зачистка карты грунтом (Земля/Трава)
            GenerateBaseLayers(worldMap);

            // 2. Строим вертикальный проспект с кибитками на Z-этаже 1
            GenerateVerticalTestStreet(worldMap, edificeStore, zLevel: 1);
        }

        private static void GenerateBaseLayers(WorldMap worldMap)
        {
            int totalMicroX = MapLayer.WidthInRegions * MapRegion.Size * MapRegion.SubDivision;
            int totalMicroY = MapLayer.HeightInRegions * MapRegion.Size * MapRegion.SubDivision;

            for (int z = worldMap.MinZ; z <= worldMap.MaxZ; z++)
            {
                MapLayer layer = worldMap.GetLayer(z);
                if (layer == null) continue;

                for (int y = 0; y < totalMicroY; y++)
                {
                    for (int x = 0; x < totalMicroX; x++)
                    {
                        ref MicroCell tile = ref layer.GetMicroCell(x, y);

                        tile.FloorId = 0;    // Трава/Грунт вокруг города
                        tile.EdificeId = 0;  // Чистое поле
                        tile.Height = 10;    // Плоская равнина
                        tile.Flags = 0x0004; // Взводим Бит 2 (Проходимо)
                    }
                }
            }
        }

        private static void GenerateVerticalTestStreet(WorldMap worldMap, EdificeStore edificeStore, int zLevel)
        {
            MapLayer layer = worldMap.GetLayer(zLevel);
            if (layer == null) return;

            int maxCoord = (16 * 48) - 1; // 767

            // ========================================================
            // 1. РЕГИСТРАЦИЯ ВСЕХ ТЕСТОВЫХ ШАБЛОНОВ ОБЪЕКТОВ (Configs)
            // ========================================================

            // Шаблон 1: Монолитная стена для кибиток (1х1 ячейка, непроходима)
            edificeStore.Configs[1] = new EdificeConfig
            {
                TypeId = 1,
                Name = "Бетонная стена",
                Type = EdificeType.Wall,
                WidthCells = 1,
                HeightCells = 1,
                MaxHitPoints = 600,
                CoverEffectiveness = 1.0f // 100% блок обзора и пуль
            };

            // Шаблон 2: Мешки с песком / Баррикады (1х1 ячейка, проходимы)
            edificeStore.Configs[2] = new EdificeConfig
            {
                TypeId = 2,
                Name = "Мешки с песком",
                Type = EdificeType.Wall,
                WidthCells = 1,
                HeightCells = 1,
                MaxHitPoints = 250,
                CoverEffectiveness = 0.65f // Оставляем честные 65% защиты
            };

            // Шаблон 3: Промышленный Генератор (Крупный тестовый объект 2х2 ячейки!)
            edificeStore.Configs[3] = new EdificeConfig
            {
                TypeId = 3,
                Name = "Тестовый Генератор",
                Type = EdificeType.Generator,
                WidthCells = 2,
                HeightCells = 2,
                MaxHitPoints = 400,
                CoverEffectiveness = 0.40f // Генератор крупный, за ним тоже можно частично укрыться!
            };

            // ========================================================
            // 2. ГЕОМЕТРИЯ ВЕРТИКАЛЬНОГО ПРОСПЕКТА (Дорога по центру X)
            // ========================================================
            // Улица пойдет сверху вниз ровно по центру карты (X от 40 до 60 микро-ячеек)
            int streetMinX = 40;
            int streetMaxX = 60;

            for (int y = 0; y <= maxCoord; y++)
            {
                for (int x = streetMinX; x <= streetMaxX; x++)
                {
                    ref MicroCell tile = ref layer.GetMicroCell(x, y);
                    tile.FloorId = 2; // Код серого асфальта
                }
            }

            // ========================================================
            // 3. СТРОИТЕЛЬСТВО ЗАПОЛНЕННЫХ СТЕНАМИ КИБИТОК (ДОМА-КОРОБКИ)
            // ========================================================
            // Построим жилые кибитки размером 6х6 ячеек слева и справа от проспекта
            // Левый ряд кибиток (X от 33 до 39) и правый ряд кибиток (X от 61 до 67)

            for (int houseY = 10; houseY < maxCoord - 20; houseY += 16)
            {
                // ---- ЛЕВАЯ КИБИТКА (Коробка 6х6 из монолитных стен) ----
                BuildBoxHouse(worldMap, edificeStore, startX: 33, startY: houseY, width: 6, height: 6, zLevel, doorFacingRight: true);

                // ---- ПРАВАЯ КИБИТКА (Коробка 6х6 из монолитных стен) ----
                BuildBoxHouse(worldMap, edificeStore, startX: 61, startY: houseY, width: 6, height: 6, zLevel, doorFacingRight: false);
            }

            // ========================================================
            // 4. РАССТАВЛЯЕМ ТЕСТОВЫЕ ОБЪЕКТЫ НА ДОРОГЕ (МЕШКИ, ГЕНЕРАТОРЫ И НЕПРОГЛЯДНЫЕ СТЕНЫ)
            // ========================================================

            // --- СЕКЦИЯ А: ВЕРХНИЙ БЛОКПОСТ СИНЕЙ КОМАНДЫ (Y = 30) ---
            // Слева ставим мешки с песком (проходимы, можно выглядывать), 
            // а справа возводим глухую непроглядную бетонную стену (configId: 1) в качестве ДОТа!
            for (int x = streetMinX + 2; x <= streetMaxX - 2; x++)
            {
                if (x < streetMinX + 10)
                {
                    // Левый фланг — низкие укрытия (мешки с песком, configId: 2)
                    edificeStore.Build(worldMap, configId: 2, startMx: x, startY: 30, z: zLevel);
                }
                else
                {
                    // Правый фланг — ЖЕСТКАЯ НЕПРОГЛЯДНАЯ СТЕНА (configId: 1)
                    // Сквозь нее нельзя стрелять и смотреть, это монолитный блокпост!
                    edificeStore.Build(worldMap, configId: 1, startMx: x, startY: 30, z: zLevel);
                }
            }

            // --- СЕКЦИЯ Б: ЦЕНТРАЛЬНЫЕ ТАКТИЧЕСКИЕ ЩИТЫ (Между генераторами) ---
            // Поставим прямо по центру дороги (Y = 75) глухую бетонную стенку длиной в 4 ячейки.
            // Она разорвет линию огня СВД по центру улицы и создаст укрытие для сближения!
            int ShieldY = 75;
            int roadCenter = (streetMinX + streetMaxX) / 2;
            for (int x = roadCenter - 2; x <= roadCenter + 2; x++)
            {
                edificeStore.Build(worldMap, configId: 1, startMx: x, startY: ShieldY, z: zLevel);
            }


            // --- СЕКЦИЯ В: НИЖНИЙ БЛОКПОСТ КРАСНОЙ КОМАНДЫ (Y = 120) ---
            // Зеркально меняем фланги: слева глухая бетонная стена, справа мешки с песком
            for (int x = streetMinX + 2; x <= streetMaxX - 2; x++)
            {
                if (x < streetMinX + 10)
                {
                    // Левый фланг — НЕПРОГЛЯДНАЯ СТЕНА (configId: 1)
                    edificeStore.Build(worldMap, configId: 1, startMx: x, startY: 120, z: zLevel);
                }
                else
                {
                    // Правый фланг — мешки с песком (configId: 2)
                    edificeStore.Build(worldMap, configId: 2, startMx: x, startY: 120, z: zLevel);
                }
            }

            // Центральные массивные 2х2 генераторы (из прошлого шага) оставляем для объема
            edificeStore.Build(worldMap, configId: 3, startMx: 45, startY: 65, z: zLevel);
            edificeStore.Build(worldMap, configId: 3, startMx: 53, startY: 85, z: zLevel);

            Console.WriteLine("🛡️ На вертикальный проспект добавлены глухие блокпосты и центральные щиты видимости!");

        }

        /// <summary>
        /// Вспомогательный метод, который собирает пустую внутри коробку дома (кибитку) и оставляет дверной проем.
        /// </summary>
        private static void BuildBoxHouse(WorldMap map, EdificeStore store, int startX, int startY, int width, int height, int z, bool doorFacingRight)
        {
            for (int dy = 0; dy < height; dy++)
            {
                for (int dx = 0; dx < width; dx++)
                {
                    // Проверяем, крайняя ли это ячейка, чтобы строить строго контур (стены)
                    bool isBorder = (dx == 0 || dx == width - 1 || dy == 0 || dy == height - 1);
                    if (!isBorder) continue;

                    // Оставляем пустой дверной проем (пропускаем постройку стены в центре одной из стен)
                    if (doorFacingRight && dx == width - 1 && dy == height / 2) continue; // Дверь смотрит на дорогу справа
                    if (!doorFacingRight && dx == 0 && dy == height / 2) continue;       // Дверь смотрит на дорогу слева

                    // Возводим блок монолитной стены (configId = 1)
                    store.Build(map, configId: 1, startMx: startX + dx, startY: startY + dy, z: z);
                }
            }
        }
    }
}
