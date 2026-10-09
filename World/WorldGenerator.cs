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
            $"{worldMap.LayerCount} уровней, шаг высоты 0,1 м.");
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
        // Its floor reaches the bottom layer. Heights are stored in 0.1 m layers.
        int rampDistance =
            Math.Max(
                1,
                worldMap.TileHeight / 12);

        float canyonSpacing =
            rampDistance * 2f;

        float[] canyonFloorY =
            new float[worldMap.TileWidth];

        // Large, slowly winding canyon belts: lower frequencies make the
        // landforms span hundreds of tiles rather than looking like small noise.
        for (int x = 0; x < worldMap.TileWidth; x++)
        {
            float dx =
                x - centerX;

            float broadBend =
                FractalNoise(x, centerY, 0.0025f, 17) * 30f;

            float smallBend =
                FractalNoise(x, centerY, 0.009f, 43) * 11f;

            canyonFloorY[x] =
                centerY +
                MathF.Sin(dx * 0.010f) * 13f +
                MathF.Sin(dx * 0.0035f) * 28f +
                MathF.Sin(dx * 0.032f) * 3f +
                broadBend +
                smallBend;
        }

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                float broadRelief =
                    FractalNoise(x, y, 0.0028f, 101);

                float mediumRelief =
                    FractalNoise(x, y, 0.0085f, 211);

                float fineRelief =
                    ValueNoise(x * 0.032f, y * 0.032f, 307);

                // Distort the winding canyon path without softening its cliffs.
                float warpedOffset =
                    y - canyonFloorY[x] +
                    broadRelief * 15f +
                    mediumRelief * 6f;

                float nearestCanyonOffset =
                    MathF.Round(
                        warpedOffset /
                        canyonSpacing) *
                    canyonSpacing;

                float distanceFromFloor =
                    MathF.Abs(
                        warpedOffset -
                        nearestCanyonOffset);

                // Narrow low canyon floor, a short and steep escarpment,
                // then a broad high plateau. The one-meter vertical grid stays intact.
                float floorWidth =
                    rampDistance * 0.22f;

                float cliffEnd =
                    rampDistance * 0.36f;

                float shapedHeight;

                if (distanceFromFloor < floorWidth)
                {
                    shapedHeight =
                        2f +
                        distanceFromFloor / floorWidth * 3f;
                }
                else if (distanceFromFloor < cliffEnd)
                {
                    float amount =
                        (distanceFromFloor - floorWidth) /
                        (cliffEnd - floorWidth);

                    shapedHeight =
                        Lerp(5f, 38f, amount);
                }
                else
                {
                    float amount =
                        (distanceFromFloor - cliffEnd) /
                        (rampDistance - cliffEnd);

                    shapedHeight =
                        Lerp(38f, 48f, amount);
                }

                shapedHeight +=
                    broadRelief * 2.5f +
                    mediumRelief * 2f +
                    fineRelief * 1.2f;

                // Large flat-topped mesa to one side of the central canyon.
                shapedHeight =
                    ApplyMesa(
                        x,
                        y,
                        centerX + 58f,
                        centerY - 9f,
                        48f,
                        36f,
                        shapedHeight,
                        mediumRelief,
                        fineRelief);

                // Deep basin with a raised, steep outer rim on the opposite side.
                shapedHeight =
                    ApplyRimmedBasin(
                        x,
                        y,
                        centerX - 61f,
                        centerY + 19f,
                        40f,
                        29f,
                        shapedHeight,
                        broadRelief,
                        mediumRelief,
                        fineRelief);

                int heightLayers =
                    Math.Clamp(
                        (int)MathF.Round(
                            shapedHeight *
                            WorldMap.HeightUnitsPerMeter /
                            WorldMap.LayerHeightUnits),
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

    private static float ApplyMesa(
        int x,
        int y,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float currentHeight,
        float mediumRelief,
        float fineRelief)
    {
        float dx = (x - centerX) / radiusX;
        float dy = (y - centerY) / radiusY;
        float distance = MathF.Sqrt(dx * dx + dy * dy);

        if (distance >= 1f)
            return currentHeight;

        float topHeight =
            43f +
            mediumRelief * 1.5f +
            fineRelief;

        if (distance <= 0.62f)
            return topHeight;

        if (distance <= 0.76f)
        {
            float amount = (distance - 0.62f) / 0.14f;
            return Lerp(topHeight, 12f, amount);
        }

        if (distance <= 0.88f)
        {
            float amount = (distance - 0.76f) / 0.12f;
            return Lerp(12f, 7f, amount);
        }

        return Lerp(
            7f,
            currentHeight,
            (distance - 0.88f) / 0.12f);
    }

    private static float ApplyRimmedBasin(
        int x,
        int y,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float currentHeight,
        float broadRelief,
        float mediumRelief,
        float fineRelief)
    {
        float dx = (x - centerX) / radiusX;
        float dy = (y - centerY) / radiusY;
        float distance = MathF.Sqrt(dx * dx + dy * dy);

        if (distance >= 1f)
            return currentHeight;

        if (distance <= 0.52f)
        {
            return
                4f +
                broadRelief * 1.5f +
                mediumRelief +
                fineRelief;
        }

        if (distance <= 0.70f)
        {
            float amount = (distance - 0.52f) / 0.18f;
            return Lerp(5f, 40f, amount);
        }

        if (distance <= 0.80f)
        {
            float amount = (distance - 0.70f) / 0.10f;
            return Lerp(40f, 34f, amount);
        }

        if (distance <= 0.92f)
        {
            float amount = (distance - 0.80f) / 0.12f;
            return Lerp(34f, 11f, amount);
        }

        return Lerp(
            11f,
            currentHeight,
            (distance - 0.92f) / 0.08f);
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
