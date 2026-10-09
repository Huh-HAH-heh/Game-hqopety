using System;
using Core.Map;

namespace World;

public static class WorldGenerator
{
    private const ushort BaseMaterialId = 1;

    public static void Generate(
        WorldMap worldMap)
    {
        worldMap.Water.Clear();

        GenerateBaseTerrain(worldMap);
        GenerateWater(worldMap);

        Console.WriteLine(
            "[WorldGenerator] " +
            $"Полнообъёмный тест каньона создан: " +
            $"{worldMap.LayerCount} уровней, шаг высоты 1 м.");
    }

    private static void GenerateBaseTerrain(
        WorldMap worldMap)
    {
        int centerX =
            worldMap.TileWidth / 2;

        int centerY =
            worldMap.TileHeight / 2;

        int layerCount =
            worldMap.LayerCount;

        // A winding canyon crosses the camera's starting view.
        // Its floor reaches the bottom layer and each terrace changes
        // elevation by exactly one 1 m voxel layer.
        int rampDistance =
            Math.Max(
                1,
                worldMap.TileHeight / 12);

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                float dx =
                    x - centerX;

                float canyonCenterY =
                    centerY +
                    MathF.Sin(dx * 0.018f) * 8f +
                    MathF.Sin(dx * 0.006f) * 16f +
                    MathF.Sin(dx * 0.061f) * 2f;

                float distanceFromFloor =
                    MathF.Abs(y - canyonCenterY);

                int heightLayers =
                    1 +
                    (int)(
                        distanceFromFloor *
                        (layerCount - 1) /
                        rampDistance);

                heightLayers =
                    Math.Clamp(
                        heightLayers,
                        1,
                        layerCount);

                worldMap.SetSolidHeight(
                    x,
                    y,
                    checked(
                        (ushort)(
                            heightLayers *
                            WorldMap.LayerHeightUnits)),
                    BaseMaterialId);
            }
        }
    }

    private static void GenerateWater(
        WorldMap worldMap)
    {
        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                float terrainHeight =
                    worldMap.GetSurfaceHeight(x, y);

                if (terrainHeight > 7f)
                    continue;

                int waterZ =
                    Math.Clamp(
                        (int)MathF.Ceiling(terrainHeight),
                        0,
                        worldMap.Water.Levels - 1);

                worldMap.Water.SetAmount(
                    x,
                    y,
                    waterZ,
                    byte.MaxValue);
            }
        }
    }
}
