using System;
using Core.Map;
using Core.Structs;
using Core.Items;

namespace World;

public static class WorldGenerator
{
    private const int MapSizeInCells =
        16 * 48; // 768x768 micro cells

    public static void Generate(
        WorldMap worldMap,
        EdificeStore edificeStore)
    {
        // --------------------------------------------------------
        // 1. Базовый рельеф
        // --------------------------------------------------------

        GenerateBaseTerrain(worldMap);

        // --------------------------------------------------------
        // 2. Edifice config
        // --------------------------------------------------------

        if (edificeStore.Configs == null ||
            edificeStore.Configs.Length <= 1)
        {
            edificeStore.Configs =
                new EdificeConfig[10];
        }

        edificeStore.Configs[1] =
            new EdificeConfig
            {
                TypeId = 1,
                Name = "Бетонная стена",
                Type = EdificeType.Wall,
                WidthCells = 1,
                HeightCells = 1,
                MaxHitPoints = 600,
                CoverEffectiveness = 1.0f
            };

        // --------------------------------------------------------
        // 3. Fractal structure
        // --------------------------------------------------------

        int fractalSize = 486;

        int startX =
            (MapSizeInCells - fractalSize) / 2;

        int startY =
            (MapSizeInCells - fractalSize) / 2;

        GenerateFractalRooms(
            worldMap,
            edificeStore,
            startX,
            startY,
            fractalSize,
            z: 1,
            currentDepth: 0,
            maxDepth: 4);

        Console.WriteLine(
            "[WorldGenerator] " +
            "Фрактальная структура создана.");
    }

    // ============================================================
    // BASE TERRAIN
    // ============================================================

    private static void GenerateBaseTerrain(
        WorldMap worldMap)
    {
        for (
            int z = worldMap.MinZ;
            z <= worldMap.MaxZ;
            z++)
        {
            MapLayer layer =
                worldMap.GetLayer(z);

            if (layer == null)
                continue;

            for (int y = 0;
                 y < MapSizeInCells;
                 y++)
            {
                for (int x = 0;
                     x < MapSizeInCells;
                     x++)
                {
                    ref MicroCell cell =
                        ref layer.GetMicroCell(x, y);

                    cell.FloorId = 0;
                    cell.EdificeId = 0;
                    cell.Flags = 0x0004;

                    // ------------------------------------------------
                    // Верхний уровень имеет рельеф.
                    // Подземные уровни оставляем ниже.
                    // ------------------------------------------------

                    if (z == 0)
                    {
                        cell.Height =
                            GenerateHeight(x, y);
                    }
                    else if (z > 0)
                    {
                        cell.Height =
                            GenerateUpperLevelHeight(x, y);
                    }
                    else
                    {
                        cell.Height = 0;
                    }
                }
            }
        }
    }

    // ============================================================
    // MAIN TERRAIN HEIGHT
    // ============================================================

    private static byte GenerateHeight(
        int x,
        int y)
    {
        // Крупная форма рельефа.
        float large =
            MathF.Sin(x * 0.018f) * 2.0f +
            MathF.Cos(y * 0.015f) * 1.8f;

        // Вторая частота.
        float medium =
            MathF.Sin(
                (x + y) * 0.045f) * 1.2f;

        // Мелкие неровности.
        float small =
            MathF.Sin(x * 0.11f) *
            MathF.Cos(y * 0.09f) *
            0.45f;

        float height =
            10.0f +
            large +
            medium +
            small;

        // Террасируем рельеф.
        height =
            MathF.Round(height);

        return ClampHeight(height);
    }

    // ============================================================
    // UPPER LEVEL
    // ============================================================

    private static byte GenerateUpperLevelHeight(
     int x,
     int y)
    {
        int baseHeight =
            GenerateHeight(x, y);

        int result =
            baseHeight + 2;

        return (byte)Math.Min(
            result,
            32);
    }

    // ============================================================
    // CLAMP
    // ============================================================

    private static byte ClampHeight(
        float value)
    {
        if (value < 4f)
            return 4;

        if (value > 20f)
            return 20;

        return (byte)value;
    }

    // ============================================================
    // FRACTAL ROOMS
    // ============================================================

    private static void GenerateFractalRooms(
        WorldMap map,
        EdificeStore store,
        int x,
        int y,
        int size,
        int z,
        int currentDepth,
        int maxDepth)
    {
        if (currentDepth > maxDepth ||
            size < 6)
        {
            return;
        }

        BuildPassableRoomFrame(
            map,
            store,
            x,
            y,
            size,
            z);

        int subSize =
            size / 3;

        for (int row = 0;
             row < 3;
             row++)
        {
            for (int col = 0;
                 col < 3;
                 col++)
            {
                if (row == 1 &&
                    col == 1)
                {
                    continue;
                }

                int subX =
                    x + col * subSize;

                int subY =
                    y + row * subSize;

                GenerateFractalRooms(
                    map,
                    store,
                    subX,
                    subY,
                    subSize,
                    z,
                    currentDepth + 1,
                    maxDepth);
            }
        }
    }

    // ============================================================
    // ROOM FRAME
    // ============================================================

    private static void BuildPassableRoomFrame(
        WorldMap map,
        EdificeStore store,
        int startX,
        int startY,
        int size,
        int z)
    {
        int endX =
            startX + size - 1;

        int endY =
            startY + size - 1;

        int midX =
            startX + size / 2;

        int midY =
            startY + size / 2;

        int doorRadius =
            size > 100
                ? 2
                : 1;

        for (int currY = startY;
             currY <= endY;
             currY++)
        {
            for (int currX = startX;
                 currX <= endX;
                 currX++)
            {
                bool isBorder =
                    currX == startX ||
                    currX == endX ||
                    currY == startY ||
                    currY == endY;

                if (!isBorder)
                    continue;

                bool horizontalDoor =
                    Math.Abs(currX - midX) <= doorRadius &&
                    (currY == startY ||
                     currY == endY);

                bool verticalDoor =
                    Math.Abs(currY - midY) <= doorRadius &&
                    (currX == startX ||
                     currX == endX);

                if (horizontalDoor ||
                    verticalDoor)
                {
                    continue;
                }

                if (currX < 0 ||
                    currY < 0 ||
                    currX >= MapSizeInCells ||
                    currY >= MapSizeInCells)
                {
                    continue;
                }

                ushort gx =
                    (ushort)currX;

                ushort gy =
                    (ushort)currY;

                store.Build(
                    map,
                    1,
                    gx,
                    gy,
                    z);
            }
        }
    }
}