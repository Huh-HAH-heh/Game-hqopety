using System;
using System.Collections.Generic;
using Core.Map;

namespace World;

public static class WorldGenerator
{
    private const ushort BaseMaterialId = 1;

    public static void Generate(
        WorldMap worldMap,
        bool writeLog = true)
    {
        worldMap.DisableDiscreteBlockTerrain();
        worldMap.Water.Clear();

        GenerateBaseTerrain(worldMap);
        worldMap.EnableDiscreteBlockTerrain();

        if (writeLog)
            WriteRegionSummary(worldMap);
    }

    private static void WriteRegionSummary(WorldMap worldMap)
    {
        ushort minHeight = ushort.MaxValue;
        ushort maxHeight = 0;
        HashSet<ushort> levels = new();

        foreach (WorldRegion region in worldMap.WorldRegions)
        {
            ushort height = region.UniformSurfaceHeightUnits;
            minHeight = Math.Min(minHeight, height);
            maxHeight = Math.Max(maxHeight, height);
            levels.Add(height);
        }

        Console.WriteLine(
            "[WorldGenerator] " +
            $"Регионы: {worldMap.RegionsX}x{worldMap.RegionsY}, " +
            $"размер: {TerrainRegion.TilesPerSide}x{TerrainRegion.TilesPerSide} тайлов; " +
            "все блоки внутри региона на одном уровне. " +
            $"Диапазон высот: {minHeight * 0.1f:0.#}-{maxHeight * 0.1f:0.#} м, " +
            $"разных уровней: {levels.Count}; " +
            $"карта: {worldMap.TileWidth}x{worldMap.TileHeight} тайлов.");
    }

    public static void GenerateSmooth(
        WorldMap worldMap,
        bool writeLog = true)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        worldMap.DisableDiscreteBlockTerrain();
        worldMap.Water.Clear();

        const int patchSize = 4;
        int patchesX = worldMap.TileWidth / patchSize;
        int patchesY = worldMap.TileHeight / patchSize;
        ushort[] heights = new ushort[checked(patchesX * patchesY)];

        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;
        int rampDistance = Math.Max(1, worldMap.TileHeight / 12);
        float canyonSpacing = rampDistance * 2f;
        float[] canyonFloorY = new float[worldMap.TileWidth];

        // Restored from the earlier detailed canyon map. Evaluate its terrain
        // profile at the centre of each 4x4-tile render patch.
        for (int x = 0; x < worldMap.TileWidth; x++)
        {
            float dx = x - centerX;
            float broadBend = FractalNoise(x, centerY, 0.0025f, 17) * 30f;
            float smallBend = FractalNoise(x, centerY, 0.009f, 43) * 11f;

            canyonFloorY[x] =
                centerY +
                MathF.Sin(dx * 0.010f) * 13f +
                MathF.Sin(dx * 0.0035f) * 28f +
                MathF.Sin(dx * 0.032f) * 3f +
                broadBend +
                smallBend;
        }

        for (int patchY = 0; patchY < patchesY; patchY++)
        {
            int sampleY = patchY * patchSize + patchSize / 2;

            for (int patchX = 0; patchX < patchesX; patchX++)
            {
                int sampleX = patchX * patchSize + patchSize / 2;

                float broadRelief = FractalNoise(sampleX, sampleY, 0.0028f, 101);
                float mediumRelief = FractalNoise(sampleX, sampleY, 0.0085f, 211);
                float fineRelief = ValueNoise(
                    sampleX * 0.032f,
                    sampleY * 0.032f,
                    307);

                float warpedOffset =
                    sampleY - canyonFloorY[sampleX] +
                    broadRelief * 15f +
                    mediumRelief * 6f;

                float nearestCanyonOffset =
                    MathF.Round(warpedOffset / canyonSpacing) * canyonSpacing;
                float distanceFromFloor =
                    MathF.Abs(warpedOffset - nearestCanyonOffset);

                float floorWidth = rampDistance * 0.22f;
                float cliffEnd = rampDistance * 0.36f;
                float shapedHeight;

                if (distanceFromFloor < floorWidth)
                {
                    shapedHeight =
                        2f + distanceFromFloor / floorWidth * 3f;
                }
                else if (distanceFromFloor < cliffEnd)
                {
                    float amount =
                        (distanceFromFloor - floorWidth) /
                        (cliffEnd - floorWidth);
                    shapedHeight = Lerp(5f, 38f, amount);
                }
                else
                {
                    float amount =
                        (distanceFromFloor - cliffEnd) /
                        (rampDistance - cliffEnd);
                    shapedHeight = Lerp(38f, 48f, amount);
                }

                shapedHeight +=
                    broadRelief * 2.5f +
                    mediumRelief * 2f +
                    fineRelief * 1.2f;

                shapedHeight = ApplyMesa(
                    sampleX, sampleY,
                    centerX + 58f, centerY - 9f,
                    74f, 56f,
                    shapedHeight,
                    mediumRelief,
                    fineRelief);

                shapedHeight = ApplyRimmedBasin(
                    sampleX, sampleY,
                    centerX - 61f, centerY + 19f,
                    40f, 29f,
                    shapedHeight,
                    broadRelief,
                    mediumRelief,
                    fineRelief);

                int heightUnits = Math.Clamp(
                    (int)MathF.Round(
                        shapedHeight *
                        WorldMap.HeightUnitsPerMeter /
                        WorldMap.LayerHeightUnits),
                    1,
                    worldMap.LayerCount);

                heights[patchX + patchY * patchesX] =
                    checked((ushort)(heightUnits * WorldMap.LayerHeightUnits));
            }
        }

        worldMap.SetSurfaceHeightPatches(heights, patchSize, BaseMaterialId);

        int caveCenterX = centerX + 58;
        int caveCenterY = centerY - 9;
        int tunnelStartX = Math.Clamp(
            caveCenterX - 6,
            0,
            worldMap.TileWidth - 12);
        int tunnelStartY = Math.Clamp(
            caveCenterY - 26,
            0,
            worldMap.TileHeight - 52);

        // The tunnel runs across the mesa's short axis. Both ends stay inside
        // the high plateau and are sealed by rock; only the vertical shaft
        // connects the underground system to the surface.
        worldMap.CarveStaticVolume(
            tunnelStartX,
            tunnelStartY,
            zLevel: 14,
            width: 12,
            depth: 52,
            heightInMeters: 5);

        worldMap.CarveStaticVolume(
            Math.Clamp(caveCenterX - 10, 0, worldMap.TileWidth - 20),
            Math.Clamp(caveCenterY - 11, 0, worldMap.TileHeight - 20),
            zLevel: 12,
            width: 20,
            depth: 20,
            heightInMeters: 8);

        int shaftX = Math.Clamp(
            caveCenterX - 2,
            0,
            worldMap.TileWidth - 4);
        int shaftY = Math.Clamp(
            caveCenterY - 3,
            0,
            worldMap.TileHeight - 4);
        int maximumWorldHeightMeters =
            worldMap.LayerCount / WorldMap.HeightUnitsPerMeter;

        if (maximumWorldHeightMeters > 20)
        {
            ushort shaftSurfaceHeight = worldMap.GetSurfaceHeightUnits(
                shaftX + 2,
                shaftY + 2);
            int shaftTopMeters = Math.Clamp(
                (shaftSurfaceHeight + WorldMap.HeightUnitsPerMeter - 1) /
                WorldMap.HeightUnitsPerMeter,
                21,
                maximumWorldHeightMeters);

            worldMap.CarveStaticVolume(
                shaftX,
                shaftY,
                zLevel: 20,
                width: 4,
                depth: 4,
                heightInMeters: shaftTopMeters - 20);
        }

        int proofX = caveCenterX;
        int proofY = caveCenterY;
        int proofSliceLayer = 16 * WorldMap.HeightUnitsPerMeter;
        bool hasAirGap = false;
        bool hasSolidFloor = false;

        foreach (TileRange range in worldMap.GetTileRanges(proofX, proofY))
        {
            if (range.State == WorldMap.StateEmpty &&
                range.StartZ <= proofSliceLayer &&
                range.EndZ > proofSliceLayer)
            {
                hasAirGap = true;
            }

            int floorProbeLayer = 11 * WorldMap.HeightUnitsPerMeter;
            if (range.State == WorldMap.StateSolid &&
                range.StartZ <= floorProbeLayer &&
                range.EndZ > floorProbeLayer)
            {
                hasSolidFloor = true;
            }
        }

        ushort solidOutsideTunnel = worldMap.GetMaterialId(
            proofX - 12,
            proofY,
            proofSliceLayer);
        ushort northTunnelCap = worldMap.GetMaterialId(
            proofX,
            tunnelStartY - 1,
            proofSliceLayer);
        ushort southTunnelCap = worldMap.GetMaterialId(
            proofX,
            tunnelStartY + 52,
            proofSliceLayer);
        ushort tunnelRoof = worldMap.GetMaterialId(
            proofX,
            tunnelStartY + 4,
            19 * WorldMap.HeightUnitsPerMeter);
        ushort openedShaftSurface = worldMap.GetSurfaceHeightUnits(
            shaftX + 2,
            shaftY + 2);

        if (!hasAirGap ||
            !hasSolidFloor ||
            solidOutsideTunnel == 0 ||
            northTunnelCap == 0 ||
            southTunnelCap == 0 ||
            tunnelRoof == 0 ||
            openedShaftSurface >= 20 * WorldMap.HeightUnitsPerMeter)
        {
            throw new InvalidOperationException(
                "Static cave proof failed: expected an air tunnel, solid floor, " +
                "solid rock outside the tunnel and an open surface shaft.");
        }

        if (writeLog)
        {
            Console.WriteLine(
                "[WorldGenerator] Enclosed tunnel verified: " +
                $"air at {proofSliceLayer * 0.1f:0.0}m, solid floor at 11m, " +
                "solid floor and roof, side walls, both end caps closed, shaft open.");
            Console.WriteLine(
                "[WorldGenerator] Detailed canyon preview restored: " +
                $"{patchesX}x{patchesY} patches at {patchSize}x{patchSize} tiles; " +
                $"map {worldMap.TileWidth}x{worldMap.TileHeight} tiles; " +
                "static cave: enclosed 52m tunnel + 20x20m chamber + 4x4m shaft.");
        }
    }

    private static void GenerateBaseTerrain(
        WorldMap worldMap)
    {
        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;
        int rampDistance = Math.Max(1, worldMap.TileHeight / 12);
        float canyonSpacing = rampDistance * 2f;

        float[] canyonFloorY = new float[worldMap.TileWidth];

        for (int x = 0; x < worldMap.TileWidth; x++)
        {
            float dx = x - centerX;
            float broadBend = FractalNoise(x, centerY, 0.0025f, 17) * 30f;
            float smallBend = FractalNoise(x, centerY, 0.009f, 43) * 11f;

            canyonFloorY[x] =
                centerY +
                MathF.Sin(dx * 0.010f) * 13f +
                MathF.Sin(dx * 0.0035f) * 28f +
                MathF.Sin(dx * 0.032f) * 3f +
                broadBend +
                smallBend;
        }

        // One height sample generates the whole region. Tile-level detail is
        // preserved in storage and can be expanded by later local edits.
        for (int regionY = 0; regionY < worldMap.RegionsY; regionY++)
        {
            for (int regionX = 0; regionX < worldMap.RegionsX; regionX++)
            {
                WorldRegion region = worldMap.GetWorldRegion(regionX, regionY)!;
                int x = region.TileOriginX + region.TileWidth / 2;
                int y = region.TileOriginY + region.TileHeight / 2;

                float broadRelief = FractalNoise(x, y, 0.0028f, 101);
                float mediumRelief = FractalNoise(x, y, 0.0085f, 211);
                float fineRelief = ValueNoise(x * 0.032f, y * 0.032f, 307);

                float warpedOffset =
                    y - canyonFloorY[x] +
                    broadRelief * 15f +
                    mediumRelief * 6f;

                float nearestCanyonOffset =
                    MathF.Round(warpedOffset / canyonSpacing) * canyonSpacing;
                float distanceFromFloor =
                    MathF.Abs(warpedOffset - nearestCanyonOffset);

                // The sampling interval is now one full region, so the
                // canyon profile is widened to remain visible at this scale.
                float floorWidth = rampDistance * 0.55f;
                float cliffEnd = rampDistance;
                float plateauEnd = rampDistance * 1.75f;
                float shapedHeight;

                if (distanceFromFloor < floorWidth)
                {
                    shapedHeight =
                        2f + distanceFromFloor / floorWidth * 3f;
                }
                else if (distanceFromFloor < cliffEnd)
                {
                    float amount =
                        (distanceFromFloor - floorWidth) /
                        (cliffEnd - floorWidth);
                    shapedHeight = Lerp(5f, 38f, amount);
                }
                else
                {
                    float amount = Math.Clamp(
                        (distanceFromFloor - cliffEnd) /
                        (plateauEnd - cliffEnd),
                        0f,
                        1f);
                    shapedHeight = Lerp(38f, 48f, amount);
                }

                shapedHeight +=
                    broadRelief * 2.5f +
                    mediumRelief * 2f +
                    fineRelief * 1.2f;

                float regionScale =
                    TerrainRegion.TilesPerSide / 48f;

                // These landforms used to be smaller than one region.
                // Scale them with the region step so their silhouettes survive
                // coarse one-height-per-region generation.
                shapedHeight = ApplyMesa(
                    x, y,
                    centerX + 58f * regionScale,
                    centerY - 9f * regionScale,
                    48f * regionScale,
                    36f * regionScale,
                    shapedHeight,
                    mediumRelief,
                    fineRelief);

                shapedHeight = ApplyRimmedBasin(
                    x, y,
                    centerX - 61f * regionScale,
                    centerY + 19f * regionScale,
                    40f * regionScale,
                    29f * regionScale,
                    shapedHeight,
                    broadRelief,
                    mediumRelief,
                    fineRelief);

                // Region heights are deliberately stepped so region
                // boundaries are easy to inspect before detailed terrain returns.
                shapedHeight = MathF.Round(shapedHeight / 2f) * 2f;

                int heightUnits = Math.Clamp(
                    (int)MathF.Round(
                        shapedHeight *
                        WorldMap.HeightUnitsPerMeter /
                        WorldMap.LayerHeightUnits),
                    1,
                    worldMap.LayerCount);

                worldMap.SetRegionSolidHeight(
                    regionX,
                    regionY,
                    checked((ushort)heightUnits),
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

        float topHeight = 43f + mediumRelief * 1.5f + fineRelief;
        if (distance <= 0.62f)
            return topHeight;

        if (distance <= 0.76f)
            return Lerp(topHeight, 12f, (distance - 0.62f) / 0.14f);

        if (distance <= 0.88f)
            return Lerp(12f, 7f, (distance - 0.76f) / 0.12f);

        return Lerp(7f, currentHeight, (distance - 0.88f) / 0.12f);
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
            return Lerp(5f, 40f, (distance - 0.52f) / 0.18f);

        if (distance <= 0.80f)
            return Lerp(40f, 34f, (distance - 0.70f) / 0.10f);

        if (distance <= 0.92f)
            return Lerp(34f, 11f, (distance - 0.80f) / 0.12f);

        return Lerp(11f, currentHeight, (distance - 0.92f) / 0.08f);
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
            total += ValueNoise(
                x * frequency,
                y * frequency,
                seed + octave * 1013) * amplitude;

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
