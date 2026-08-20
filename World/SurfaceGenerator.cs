using Core.Map;
using Core.Structs;

namespace World
{
    public static class SurfaceGenerator
    {
        // Коэффициент масштабирования (из больших тайлов в микро-ячейки)
        private const int Scale = MapRegion.SubDivision; // 3

        public static void Generate(WorldMap worldMap)
        {
            MapLayer surfaceLayer = worldMap.GetLayer(0);
            if (surfaceLayer == null) return;

            GenerateMountain(surfaceLayer);
            GenerateHeightTest(surfaceLayer);

            // Генерируем первую комнату (чистые микро-координаты)
            GenerateRoom(surfaceLayer);

            // Генерируем вторую комнату/склад (масштабированные координаты)
            GenerateOutpost(surfaceLayer);
        }

        /// <summary>
        /// Первая комната: создается на основе чистых микро-координат.
        /// </summary>
        private static void GenerateRoom(MapLayer surfaceLayer)
        {
            const int startX = 60;
            const int startY = 60;
            const int width = 40;
            const int height = 30;

            int endX = startX + width;
            int endY = startY + height;

            // 1. ЗАСТИЛАЕМ ПОЛ КАФЕЛЕМ
            for (int mx = startX; mx <= endX; mx++)
            {
                for (int my = startY; my <= endY; my++)
                {
                    ref MicroCell cell = ref surfaceLayer.GetMicroCell(mx, my);
                    cell.FloorId = 2;
                }
            }

            // 2. ВОЗВОДИМ ИСКУССТВЕННЫЕ СТЕНЫ (EdificeId = 1)
            for (int mx = startX; mx <= endX; mx++)
            {
                surfaceLayer.GetMicroCell(mx, startY).EdificeId = 1;
                surfaceLayer.GetMicroCell(mx, endY).EdificeId = 1;
            }
            for (int my = startY; my <= endY; my++)
            {
                surfaceLayer.GetMicroCell(startX, my).EdificeId = 1;
                surfaceLayer.GetMicroCell(endX, my).EdificeId = 1;
            }

            // 3. ПРОБИВАЕМ МИКРО-ДВЕРЬ
            int doorX = startX + (width / 2);
            surfaceLayer.GetMicroCell(doorX, endY).EdificeId = 0;
        }

        /// <summary>
        /// Вторая комната (Аванпост): адаптирована из старого легаси-метода.
        /// </summary>
        private static void GenerateOutpost(MapLayer surfaceLayer)
        {
            int startX = 20 * Scale;
            int startY = 20 * Scale;
            int width = 30 * Scale;
            int height = 20 * Scale;

            int endX = startX + width;
            int endY = startY + height;

            // 1. Заполняем пол внутри аванпоста
            for (int mx = startX; mx <= endX; mx++)
            {
                for (int my = startY; my <= endY; my++)
                {
                    ref MicroCell cell = ref surfaceLayer.GetMicroCell(mx, my);
                    cell.FloorId = 2;
                }
            }

            // 2. Строим стены по периметру
            for (int mx = startX; mx <= endX; mx++)
            {
                surfaceLayer.GetMicroCell(mx, startY).EdificeId = 1;
                surfaceLayer.GetMicroCell(mx, endY).EdificeId = 1;
            }
            for (int my = startY; my <= endY; my++)
            {
                surfaceLayer.GetMicroCell(startX, my).EdificeId = 1;
                surfaceLayer.GetMicroCell(endX, my).EdificeId = 1;
            }

            // 3. Делаем микро-проход (дверь) в южной стене
            int doorX = startX + (width / 2);
            surfaceLayer.GetMicroCell(doorX, endY).EdificeId = 0;
        }

        private static void GenerateMountain(MapLayer surfaceLayer)
        {
            int minMcX = 5 * Scale;
            int maxMcX = 15 * Scale;
            int minMcY = 5 * Scale;
            int maxMcY = 15 * Scale;

            for (int mx = minMcX; mx < maxMcX; mx++)
            {
                for (int my = minMcY; my < maxMcY; my++)
                {
                    ref MicroCell cell = ref surfaceLayer.GetMicroCell(mx, my);
                    cell.EdificeId = 2; // Природная скала
                }
            }
        }

        private static void GenerateHeightTest(MapLayer surfaceLayer)
        {
            const byte mountainHeight = 4;

            int minMcX = 18 * Scale;
            int maxMcX = 50 * Scale;
            int minMcY = 18 * Scale;
            int maxMcY = 40 * Scale;

            for (int mx = minMcX; mx <= maxMcX; mx++)
            {
                ref MicroCell top = ref surfaceLayer.GetMicroCell(mx, minMcY);
                ref MicroCell bottom = ref surfaceLayer.GetMicroCell(mx, maxMcY);

                top.Height = mountainHeight;
                bottom.Height = mountainHeight;
            }

            for (int my = minMcY; my <= maxMcY; my++)
            {
                ref MicroCell left = ref surfaceLayer.GetMicroCell(minMcX, my);
                ref MicroCell right = ref surfaceLayer.GetMicroCell(maxMcX, my);

                left.Height = mountainHeight;
                right.Height = mountainHeight;
            }
        }
    }
}
