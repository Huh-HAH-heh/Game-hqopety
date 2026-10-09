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

        float canyonSpacing =
            rampDistance * 2f;

        float[] canyonFloorY =
            new float[worldMap.TileWidth];

        // The centerline depends only on X; compute it once rather than
        // evaluating three trigonometric functions for every map cell.
        for (int x = 0; x < worldMap.TileWidth; x++)
        {
            float dx =
                x - centerX;

            canyonFloorY[x] =
                centerY +
                MathF.Sin(dx * 0.018f) * 8f +
                MathF.Sin(dx * 0.006f) * 16f +
                MathF.Sin(dx * 0.061f) * 2f;
        }

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                float offsetFromCanyonCenter =
                    y - canyonFloorY[x];

                float nearestCanyonOffset =
                    MathF.Round(
                        offsetFromCanyonCenter /
                        canyonSpacing) *
                    canyonSpacing;

                float distanceFromFloor =
                    MathF.Abs(
                        offsetFromCanyonCenter -
                        nearestCanyonOffset);

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

}
