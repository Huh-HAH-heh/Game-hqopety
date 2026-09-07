using Core.Map;
using Core.Structs;

namespace World;

public static class SurfaceGenerator
{
    private const int Scale =
        MapRegion.SubDivision;

    public static void Generate(
        WorldMap worldMap)
    {
        MapLayer surfaceLayer =
            worldMap.GetLayer(0);

        if (surfaceLayer == null)
            return;

        GenerateMountain(
            surfaceLayer);

        GenerateHeightTerraces(
            surfaceLayer);

        GenerateRoom(
            surfaceLayer);

        GenerateOutpost(
            surfaceLayer);
    }

    // ============================================================
    // ROOM
    // ============================================================

    private static void GenerateRoom(
        MapLayer surfaceLayer)
    {
        const int startX = 60;
        const int startY = 60;
        const int width = 40;
        const int height = 30;

        int endX =
            startX + width;

        int endY =
            startY + height;

        for (int mx = startX;
             mx <= endX;
             mx++)
        {
            for (int my = startY;
                 my <= endY;
                 my++)
            {
                ref MicroCell cell =
                    ref surfaceLayer.GetMicroCell(
                        mx,
                        my);

                cell.FloorId = 2;

                // Комната стоит на ровной площадке.
                cell.Height = 12;
            }
        }

        for (int mx = startX;
             mx <= endX;
             mx++)
        {
            surfaceLayer
                .GetMicroCell(
                    mx,
                    startY)
                .EdificeId = 1;

            surfaceLayer
                .GetMicroCell(
                    mx,
                    endY)
                .EdificeId = 1;
        }

        for (int my = startY;
             my <= endY;
             my++)
        {
            surfaceLayer
                .GetMicroCell(
                    startX,
                    my)
                .EdificeId = 1;

            surfaceLayer
                .GetMicroCell(
                    endX,
                    my)
                .EdificeId = 1;
        }

        int doorX =
            startX + width / 2;

        surfaceLayer
            .GetMicroCell(
                doorX,
                endY)
            .EdificeId = 0;
    }

    // ============================================================
    // OUTPOST
    // ============================================================

    private static void GenerateOutpost(
        MapLayer surfaceLayer)
    {
        int startX =
            20 * Scale;

        int startY =
            20 * Scale;

        int width =
            30 * Scale;

        int height =
            20 * Scale;

        int endX =
            startX + width;

        int endY =
            startY + height;

        for (int mx = startX;
             mx <= endX;
             mx++)
        {
            for (int my = startY;
                 my <= endY;
                 my++)
            {
                ref MicroCell cell =
                    ref surfaceLayer.GetMicroCell(
                        mx,
                        my);

                cell.FloorId = 2;

                // Аванпост на другой высоте.
                cell.Height = 8;
            }
        }

        for (int mx = startX;
             mx <= endX;
             mx++)
        {
            surfaceLayer
                .GetMicroCell(
                    mx,
                    startY)
                .EdificeId = 1;

            surfaceLayer
                .GetMicroCell(
                    mx,
                    endY)
                .EdificeId = 1;
        }

        for (int my = startY;
             my <= endY;
             my++)
        {
            surfaceLayer
                .GetMicroCell(
                    startX,
                    my)
                .EdificeId = 1;

            surfaceLayer
                .GetMicroCell(
                    endX,
                    my)
                .EdificeId = 1;
        }

        int doorX =
            startX + width / 2;

        surfaceLayer
            .GetMicroCell(
                doorX,
                endY)
            .EdificeId = 0;
    }

    // ============================================================
    // MOUNTAIN
    // ============================================================

    private static void GenerateMountain(
        MapLayer surfaceLayer)
    {
        int minMcX =
            5 * Scale;

        int maxMcX =
            15 * Scale;

        int minMcY =
            5 * Scale;

        int maxMcY =
            15 * Scale;

        for (int mx = minMcX;
             mx < maxMcX;
             mx++)
        {
            for (int my = minMcY;
                 my < maxMcY;
                 my++)
            {
                ref MicroCell cell =
                    ref surfaceLayer.GetMicroCell(
                        mx,
                        my);

                cell.EdificeId = 2;

                // Скала выше обычной поверхности.
                cell.Height = 16;
            }
        }
    }

    // ============================================================
    // HEIGHT TERRACES
    // ============================================================

    private static void GenerateHeightTerraces(
        MapLayer layer)
    {
        const int startX = 54;
        const int endX = 180;

        const int startY = 150;
        const int endY = 300;

        for (int y = startY;
             y <= endY;
             y++)
        {
            for (int x = startX;
                 x <= endX;
                 x++)
            {
                ref MicroCell cell =
                    ref layer.GetMicroCell(
                        x,
                        y);

                int localX =
                    x - startX;

                int localY =
                    y - startY;

                // Крупные ступени.
                int terraceX =
                    localX / 12;

                int terraceY =
                    localY / 16;

                int height =
                    6 +
                    (terraceX % 4) +
                    (terraceY % 3);

                // Центральный холм.
                float cx =
                    (startX + endX) * 0.5f;

                float cy =
                    (startY + endY) * 0.5f;

                float dx =
                    x - cx;

                float dy =
                    y - cy;

                float distance =
                    MathF.Sqrt(
                        dx * dx +
                        dy * dy);

                if (distance < 25f)
                    height += 3;
                else if (distance < 45f)
                    height += 2;
                else if (distance < 65f)
                    height += 1;

                if (height < 0)
                    height = 0;

                if (height > 20)
                    height = 20;

                cell.Height =
                    (byte)height;
            }
        }
    }
}