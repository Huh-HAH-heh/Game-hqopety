using Core.Map;
using Core.Structs;

namespace World
{
    public static class UndergroundGenerator
    {
        // Коэффициент масштабирования (из больших тайлов в микро-ячейки)
        private const int Scale = MapRegion.SubDivision; // 3

        public static void Generate(WorldMap worldMap)
        {
            // Получаем подземный слой (-1 этаж)
            MapLayer layer = worldMap.GetLayer(-1);

            if (layer == null)
                return;

            GenerateTestRoom(layer);
        }

        private static void GenerateTestRoom(MapLayer layer)
        {
            // ОШИБКА ИСПРАВЛЕНА: Переводим старые границы тайлов в микро-координаты (умножаем на 3)
            int minMcX = 22 * Scale;
            int maxMcX = 27 * Scale;
            int minMcY = 22 * Scale;
            int maxMcY = 27 * Scale;

            for (int my = minMcY; my <= maxMcY; my++)
            {
                for (int mx = minMcX; mx <= maxMcX; mx++)
                {
                    // ОШИБКА ИСПРАВЛЕНА: вызываем GetMicroCell вместо GetTile
                    ref MicroCell cell = ref layer.GetMicroCell(mx, my);

                    // ОШИБКА ИСПРАВЛЕНА: Выдалбливаем камень скалы. 
                    // Зануляем EdificeId (0 = пустое пространство, где можно ходить)
                    cell.EdificeId = 0;

                    // ОШИБКА ИСПРАВЛЕНА: Стелим подземный пол.
                    // Присваиваем напрямую в FloorId (2 = наш серый каменный кафель/тесаный камень)
                    cell.FloorId = 2;
                }
            }
        }
    }
}
