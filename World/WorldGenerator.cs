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

        // Main canyon centerline: broad bends remain legible, with finer
        // deterministic noise breaking the repeating wave pattern.
        for (int x = 0; x < worldMap.TileWidth; x++)
        {
            float dx =
                x - centerX;

            float broadBend =
                FractalNoise(x, centerY, 0.0045f, 17) * 18f;

            float smallBend =
                FractalNoise(x, centerY, 0.018f, 43) * 6f;

            canyonFloorY[x] =
                centerY +
                MathF.Sin(dx * 0.018f) * 8f +
                MathF.Sin(dx * 0.006f) * 16f +
                MathF.Sin(dx * 0.061f) * 2f +
                broadBend +
                smallBend;
        }

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                float broadRelief =
                    FractalNoise(x, y, 0.0045f, 101);

                float mediumRelief =
                    FractalNoise(x, y, 0.017f, 211);

                float fineRelief =
                    ValueNoise(x * 0.055f, y * 0.055f, 307);

                // Warp the canyon bands, then add rolling plateaus, ridges,
                // and smaller surface breaks. Heights remain 1 m voxel steps.
                float warpedOffset =
                    y - canyonFloorY[x] +
                    broadRelief * 11f +
                    mediumRelief * 4f;

                float nearestCanyonOffset =
                    MathF.Round(
                        warpedOffset /
                        canyonSpacing) *
                    canyonSpacing;

                float distanceFromFloor =
                    MathF.Abs(
                        warpedOffset -
                        nearestCanyonOffset);

                float ridgeRelief =
                    (1f - MathF.Abs(mediumRelief)) * 2.5f;

                float shapedDistance =
                    distanceFromFloor +
                    broadRelief * 5f +
                    mediumRelief * 3f +
                    fineRelief * 2f +
                    ridgeRelief;

                int heightLayers =
                    1 +
                    (int)(
                        shapedDistance *
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

    private static float FractalNoise(
        float x,
        float y,
        float baseFrequency,
        int seed)
    {
        float total = 0f;
        float amplitude = 1f;
        float amplitudeSum = 0f;
        float frequency = baseFrequency;

        for (int octave = 0; octave < 3; octave++)
        {
            total +=
                ValueNoise(
                    x * frequency,
                    y * frequency,
                    seed + octave * 1013) *
                amplitude;

            amplitudeSum += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return total / amplitudeSum;
    }

    private static float ValueNoise(
        float x,
        float y,
        int seed)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float tx = x - x0;
        float ty = y - y0;

        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);

        float top = Lerp(
            HashNoise(x0, y0, seed),
            HashNoise(x0 + 1, y0, seed),
            tx);

        float bottom = Lerp(
            HashNoise(x0, y0 + 1, seed),
            HashNoise(x0 + 1, y0 + 1, seed),
            tx);

        return Lerp(top, bottom, ty);
    }

    private static float HashNoise(
        int x,
        int y,
        int seed)
    {
        int hash = unchecked(
            x * 374761393 ^
            y * 668265263 ^
            seed * 1442695041);

        hash = unchecked((hash ^ (hash >> 13)) * 1274126177);
        hash ^= hash >> 16;

        return (hash & 0x7fffffff) / 1073741823.5f - 1f;
    }

    private static float Lerp(
        float a,
        float b,
        float amount)
    {
        return a + (b - a) * amount;
    }

}
