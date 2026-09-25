using Core.Map;

namespace World;

public static class SurfaceGenerator
{
    private const ushort BaseMaterialId = 1;
    private const ushort GroundMaterialId = 2;
    private const ushort MountainMaterialId = 3;

    public static void Generate(
        WorldMap worldMap)
    {
        GenerateMountain(
            worldMap);

        GenerateHeightTerraces(
            worldMap);

        GenerateRoom(
            worldMap);

        GenerateOutpost(
            worldMap);
    }

    private static void GenerateRoom(
        WorldMap worldMap)
    {
        const int startX = 60;
        const int startY = 60;
        const int width = 40;
        const int height = 30;

        int endX =
            startX + width;

        int endY =
            startY + height;

        for (int x = startX;
             x <= endX;
             x++)
        {
            for (int y = startY;
                 y <= endY;
                 y++)
            {
                worldMap.SetSolidHeight(
                    x,
                    y,
                    120,
                    GroundMaterialId);
            }
        }
    }

    private static void GenerateOutpost(
        WorldMap worldMap)
    {
        const int startX = 20;
        const int startY = 20;
        const int width = 30;
        const int height = 20;

        int endX =
            startX + width;

        int endY =
            startY + height;

        for (int x = startX;
             x <= endX;
             x++)
        {
            for (int y = startY;
                 y <= endY;
                 y++)
            {
                worldMap.SetSolidHeight(
                    x,
                    y,
                    80,
                    GroundMaterialId);
            }
        }
    }

    private static void GenerateMountain(
        WorldMap worldMap)
    {
        const int minX = 5;
        const int maxX = 15;
        const int minY = 5;
        const int maxY = 15;

        for (int x = minX;
             x < maxX;
             x++)
        {
            for (int y = minY;
                 y < maxY;
                 y++)
            {
                worldMap.SetSolidHeight(
                    x,
                    y,
                    160,
                    MountainMaterialId);
            }
        }
    }

    private static void GenerateHeightTerraces(
        WorldMap worldMap)
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
                int localX =
                    x - startX;

                int localY =
                    y - startY;

                int terraceX =
                    localX / 12;

                int terraceY =
                    localY / 16;

                int height =
                    6 +
                    terraceX % 4 +
                    terraceY % 3;

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

                worldMap.SetSolidHeight(
                    x,
                    y,
                    checked((ushort)(height * 10)),
                    BaseMaterialId);
            }
        }
    }
}
