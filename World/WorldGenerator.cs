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

        // Two sparse material bands create real geological strata inside
        // the mesa. They remain separate 3D materials at their own Z layers.
        worldMap.SetStaticVolume(
            caveCenterX - 40,
            caveCenterY - 32,
            zLevel: 8,
            width: 80,
            depth: 64,
            heightInMeters: 2,
            materialId: 2);

        worldMap.SetStaticVolume(
            caveCenterX - 40,
            caveCenterY - 32,
            zLevel: 26,
            width: 80,
            depth: 64,
            heightInMeters: 3,
            materialId: 3);

        // The lower passage crosses the mesa and opens at both canyon flanks.
        // Its centre line bends in XY and changes floor height / cross-section;
        // this is a through-tunnel, not a blind horizontal adit.
        int lowerTunnelStartX = caveCenterX - 40;
        int lowerTunnelY = caveCenterY;
        int lowerTunnelLength = 80;
        CarveWindingTunnel(
            worldMap,
            heights,
            patchesX,
            patchSize,
            lowerTunnelStartX,
            lowerTunnelY,
            lowerTunnelLength,
            horizontal: true,
            floorLevel: 14,
            lateralSway: 5,
            seed: 19);

        // First underground chamber and a second, sealed passage on a higher
        // stratum. The upper tunnel bends independently of the lower one.
        worldMap.CarveStaticVolume(
            caveCenterX - 10,
            caveCenterY - 10,
            zLevel: 12,
            width: 20,
            depth: 20,
            heightInMeters: 8);

        int upperTunnelX = caveCenterX;
        int upperTunnelStartY = caveCenterY - 16;
        int upperTunnelLength = 32;
        CarveWindingTunnel(
            worldMap,
            heights,
            patchesX,
            patchSize,
            upperTunnelX,
            upperTunnelStartY,
            upperTunnelLength,
            horizontal: false,
            floorLevel: 26,
            lateralSway: 3,
            seed: 37);

        worldMap.CarveStaticVolume(
            caveCenterX - 10,
            caveCenterY - 10,
            zLevel: 24,
            width: 20,
            depth: 20,
            heightInMeters: 8);

        // A rising internal passage joins the two levels. Its overlapping
        // sections create a walkable sloped connection instead of a surface shaft.
        CarveWindingConnector(worldMap, caveCenterX, caveCenterY);

        int proofSliceLower = 16 * WorldMap.HeightUnitsPerMeter;
        int proofSliceUpper = 28 * WorldMap.HeightUnitsPerMeter;
        float lowerProofT = 16f / lowerTunnelLength;
        int lowerProbeX = caveCenterX - 24;
        int lowerProbeY = lowerTunnelY +
            GetTunnelLateralOffset(lowerProofT, 5);
        int upperProbeX = caveCenterX;
        int upperProbeY = caveCenterY + 12;

        bool lowerAir = worldMap.GetMaterialId(
            lowerProbeX,
            lowerProbeY,
            proofSliceLower) == 0;
        bool upperAir = worldMap.GetMaterialId(
            upperProbeX,
            upperProbeY,
            proofSliceUpper) == 0;
        bool connectorAir = worldMap.GetMaterialId(
            caveCenterX,
            caveCenterY,
            22 * WorldMap.HeightUnitsPerMeter) == 0;

        bool lowerFloorSolid = worldMap.GetMaterialId(
            caveCenterX,
            caveCenterY,
            11 * WorldMap.HeightUnitsPerMeter) != 0;
        bool lowerRoofSolid = worldMap.GetMaterialId(
            caveCenterX,
            caveCenterY,
            20 * WorldMap.HeightUnitsPerMeter) != 0;
        bool upperFloorSolid = worldMap.GetMaterialId(
            caveCenterX,
            caveCenterY,
            23 * WorldMap.HeightUnitsPerMeter) != 0;
        bool upperRoofSolid = worldMap.GetMaterialId(
            caveCenterX,
            caveCenterY,
            32 * WorldMap.HeightUnitsPerMeter) != 0;

        int lowerPortalWestX = lowerTunnelStartX;
        int lowerPortalEastX = lowerTunnelStartX + lowerTunnelLength;
        ushort westPortalSurface = worldMap.GetSurfaceHeightUnits(
            lowerPortalWestX,
            lowerTunnelY);
        ushort eastPortalSurface = worldMap.GetSurfaceHeightUnits(
            lowerPortalEastX,
            lowerTunnelY);

        bool lowerPortalsOpen =
            westPortalSurface < 14 * WorldMap.HeightUnitsPerMeter &&
            eastPortalSurface < 14 * WorldMap.HeightUnitsPerMeter &&
            worldMap.GetMaterialId(
                lowerPortalWestX,
                lowerTunnelY,
                proofSliceLower) == 0 &&
            worldMap.GetMaterialId(
                lowerPortalEastX,
                lowerTunnelY,
                proofSliceLower) == 0;

        bool upperEndsSealed =
            worldMap.GetMaterialId(
                upperTunnelX,
                upperTunnelStartY - 5,
                proofSliceUpper) != 0 &&
            worldMap.GetMaterialId(
                upperTunnelX,
                upperTunnelStartY + upperTunnelLength + 5,
                proofSliceUpper) != 0;

        ushort lowerStratum = worldMap.GetMaterialId(
            caveCenterX - 20,
            caveCenterY - 20,
            9 * WorldMap.HeightUnitsPerMeter);
        ushort upperStratum = worldMap.GetMaterialId(
            caveCenterX - 20,
            caveCenterY - 20,
            27 * WorldMap.HeightUnitsPerMeter);

        ushort surfaceCoverHeight = worldMap.GetSurfaceHeightUnits(
            caveCenterX,
            caveCenterY);
        int surfaceLayer = Math.Max(
            0,
            surfaceCoverHeight / WorldMap.LayerHeightUnits - 1);
        bool surfaceSealed = surfaceCoverHeight > 35 * WorldMap.HeightUnitsPerMeter &&
            worldMap.GetMaterialId(
                caveCenterX,
                caveCenterY,
                surfaceLayer) != 0;

        if (!lowerAir ||
            !upperAir ||
            !connectorAir ||
            !lowerFloorSolid ||
            !lowerRoofSolid ||
            !upperFloorSolid ||
            !upperRoofSolid ||
            !lowerPortalsOpen ||
            !upperEndsSealed ||
            lowerStratum != 2 ||
            upperStratum != 3 ||
            !surfaceSealed)
        {
            throw new InvalidOperationException(
                "3D cave proof failed: expected a through-tunnel, sealed upper passage, solid floors/roofs, " +
                "two open lower portals, sealed upper tunnel ends, an internal ramp and an unbroken mesa top.");
        }

        if (writeLog)
        {
            Console.WriteLine(
                "[WorldGenerator] 3D terrain verified: " +
                "through-tunnel at 16m, sealed upper passage at 28m, internal connector, " +
                "solid floors/roofs, two lower portals, sealed upper passage, continuous surface and strata at 9m/27m.");
            Console.WriteLine(
                "[WorldGenerator] Detailed canyon preview restored: " +
                $"{patchesX}x{patchesY} patches at {patchSize}x{patchSize} tiles; " +
                $"map {worldMap.TileWidth}x{worldMap.TileHeight} tiles; " +
                "static geology: 80m winding through-tunnel with two portals + 32m upper passage + chambers + rising connector.");
        }
    }

    private static void CarveWindingTunnel(
        WorldMap worldMap,
        ushort[] surfaceHeightPatches,
        int patchesX,
        int patchSize,
        int startX,
        int startY,
        int lengthMeters,
        bool horizontal,
        int floorLevel,
        int lateralSway,
        int seed)
    {
        const int step = 4;
        int steps = lengthMeters / step;

        for (int index = 0; index <= steps; index++)
        {
            float t = index / (float)steps;
            int lateralOffset = GetTunnelLateralOffset(t, lateralSway);
            int centerX = horizontal
                ? startX + index * step
                : startX + lateralOffset;
            int centerY = horizontal
                ? startY + lateralOffset
                : startY + index * step;

            int floorOffset = (int)MathF.Round(
                MathF.Sin(t * MathF.Tau * 1.5f + seed * 0.001f) * 0.6f);
            int sampledX = Math.Clamp(centerX, 0, worldMap.TileWidth - 1);
            int sampledY = Math.Clamp(centerY, 0, worldMap.TileHeight - 1);
            ushort originalSurfaceUnits = surfaceHeightPatches[
                sampledX / patchSize +
                sampledY / patchSize * patchesX];
            int terrainFloor = Math.Max(
                1,
                (int)MathF.Floor(originalSurfaceUnits * 0.1f) - 1);
            int currentFloor = Math.Max(
                1,
                Math.Min(floorLevel + floorOffset, terrainFloor));

            // Most of the passage is 8m wide; periodic 12m pockets and
            // taller sections break the uniform mine-shaft silhouette.
            int width = index % 9 == 4 ? 12 : 8;
            int depth = index % 13 == 7 ? 12 : 8;
            int height = index % 11 == 5 ? 6 : 5;

            worldMap.CarveStaticVolume(
                centerX - width / 2,
                centerY - depth / 2,
                currentFloor,
                width,
                depth,
                height);
        }
    }

    private static int GetTunnelLateralOffset(float t, int sway)
    {
        return (int)MathF.Round(
            MathF.Sin(t * MathF.Tau * 2f) * sway +
            MathF.Sin(t * MathF.Tau * 5f) * sway * 0.2f);
    }

    private static void CarveWindingConnector(
        WorldMap worldMap,
        int centerX,
        int centerY)
    {
        const int sectionSize = 8;
        const int sectionHeight = 6;

        for (int index = 0; index < 4; index++)
        {
            int sectionX = centerX - 8 + index * 4;
            int sectionY = centerY - 4 + index * 4;
            int floorLevel = 16 + index * 2;

            worldMap.CarveStaticVolume(
                sectionX - sectionSize / 2,
                sectionY - sectionSize / 2,
                floorLevel,
                sectionSize,
                sectionSize,
                sectionHeight);
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
