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
            $"Каньон создан на сетке макроблоков 5 м: " +
            $"{worldMap.MacroBlocksX}x{worldMap.MacroBlocksY}, " +
            $"{worldMap.LayerCount} уровней.");
    }

    private static void GenerateBaseTerrain(
        WorldMap worldMap)
    {
        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;
        int layerCount = worldMap.LayerCount;
        int blockSpan = WorldMap.MacroBlockTileSpan;

        // The surface is generated directly at macro-block resolution.
        // Fine cells are allocated only when a local terrain edit needs them.
        int rampDistance = Math.Max(1, worldMap.TileHeight / 12);
        float canyonSpacing = rampDistance * 2f;
        float[] canyonFloorY = new float[worldMap.TileWidth];

        for (int x = 0; x < worldMap.TileWidth; x++)
        {
            float dx = x - centerX;
            canyonFloorY[x] =
                centerY +
                MathF.Sin(dx * 0.018f) * 8f +
                MathF.Sin(dx * 0.006f) * 16f +
                MathF.Sin(dx * 0.061f) * 2f;
        }

        for (int macroY = 0; macroY < worldMap.MacroBlocksY; macroY++)
        {
            int sampleY = Math.Min(
                worldMap.TileHeight - 1,
                macroY * blockSpan + blockSpan / 2);

            for (int macroX = 0; macroX < worldMap.MacroBlocksX; macroX++)
            {
                int sampleX = Math.Min(
                    worldMap.TileWidth - 1,
                    macroX * blockSpan + blockSpan / 2);

                float offsetFromCanyonCenter =
                    sampleY - canyonFloorY[sampleX];

                float nearestCanyonOffset =
                    MathF.Round(offsetFromCanyonCenter / canyonSpacing) *
                    canyonSpacing;

                float distanceFromFloor =
                    MathF.Abs(offsetFromCanyonCenter - nearestCanyonOffset);

                int heightLayers =
                    1 +
                    (int)(
                        distanceFromFloor *
                        (layerCount - 1) /
                        rampDistance);

                heightLayers = Math.Clamp(heightLayers, 1, layerCount);

                worldMap.SetMacroSolidHeight(
                    macroX,
                    macroY,
                    checked((ushort)(heightLayers * WorldMap.LayerHeightUnits)),
                    BaseMaterialId);
            }
        }
    }

}
