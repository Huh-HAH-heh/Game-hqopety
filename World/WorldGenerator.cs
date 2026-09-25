using System;
using Core.Map;

namespace World;

public static class WorldGenerator
{
    private const ushort BaseMaterialId = 1;

    public static void Generate(
        WorldMap worldMap)
    {
        GenerateBaseTerrain(
            worldMap);

        Console.WriteLine(
            "[WorldGenerator] " +
            "Базовый рельеф создан.");
    }

    private static void GenerateBaseTerrain(
        WorldMap worldMap)
    {
        for (int y = 0;
             y < worldMap.TileHeight;
             y++)
        {
            for (int x = 0;
                 x < worldMap.TileWidth;
                 x++)
            {
                float height =
                    GenerateHeight(
                        x,
                        y);

                ushort heightUnits =
                    checked(
                        (ushort)MathF.Round(
                            height * 10f));

                worldMap.SetSolidHeight(
                    x,
                    y,
                    heightUnits,
                    BaseMaterialId);
            }
        }
    }

    private static float GenerateHeight(
        int x,
        int y)
    {
        float large =
            MathF.Sin(x * 0.018f) * 2.0f +
            MathF.Cos(y * 0.015f) * 1.8f;

        float medium =
            MathF.Sin(
                (x + y) * 0.045f) * 1.2f;

        float small =
            MathF.Sin(x * 0.11f) *
            MathF.Cos(y * 0.09f) *
            0.45f;

        float height =
            10.0f +
            large +
            medium +
            small;

        return ClampHeight(
            MathF.Round(height));
    }

    private static float ClampHeight(
        float value)
    {
        if (value < 4f)
            return 4f;

        if (value > 20f)
            return 20f;

        return value;
    }
}
