using System;
using Core.Map;

namespace World;

public static class WorldGenerator
{
    private const ushort BaseMaterialId = 1;
    private const int HorizontalStepWidth = 4;

    public static void Generate(
        WorldMap worldMap)
    {
        worldMap.Water.Clear();

        GenerateBaseTerrain(worldMap);
        GenerateWater(worldMap);

        Console.WriteLine(
            "[WorldGenerator] " +
            $"Полнообъёмный тест-рельеф создан: " +
            $"{worldMap.LayerCount} уровней, шаг высоты 1 м.");
    }

    private static void GenerateBaseTerrain(
        WorldMap worldMap)
    {
        int layerCount = worldMap.LayerCount;
        int maxHeight = layerCount;

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            // A broad offset bends the contour of the long stair bands,
            // while their elevation still changes in exact 1 m steps.
            int continentOffset =
                (int)MathF.Round(
                    MathF.Sin(y * 0.010f) * 4f +
                    MathF.Cos(y * 0.013f) * 5f +
                    MathF.Sin(y * 0.004f) * 3f);

            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                float broadShape =
                    MathF.Sin(x * 0.008f) * 2.5f +
                    MathF.Cos(y * 0.010f) * 2.0f +
                    MathF.Sin((x + y) * 0.005f) * 1.5f;

                int terrainStep =
                    x / HorizontalStepWidth +
                    continentOffset +
                    (int)MathF.Round(broadShape);

                terrainStep %= layerCount;

                if (terrainStep < 0)
                    terrainStep += layerCount;

                // Surface height runs from 1 m to the lowest/highest
                // configured layer. Every column is solid from Z=0
                // up to that height; no floating surface-only terrain.
                int heightLayers =
                    maxHeight - terrainStep;

                ushort heightUnits =
                    checked(
                        (ushort)(
                            heightLayers *
                            WorldMap.LayerHeightUnits));

                worldMap.SetSolidHeight(
                    x,
                    y,
                    heightUnits,
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
