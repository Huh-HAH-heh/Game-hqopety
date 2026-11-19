using Core.Map;

namespace RimClone.Render.RaylibBackend.Data;

/// <summary>
/// Immutable, data-oriented render input. Uniform regions occupy one height/material
/// entry; only edited regions allocate tile-resolution samples.
/// No graphics API types or WorldMap references escape this capture boundary.
/// </summary>
public sealed class WorldTerrainSnapshot
{
    public int RegionsX { get; }
    public int RegionsY { get; }
    public int RegionTileSize { get; }
    public int TileWidth => RegionsX * RegionTileSize;
    public int TileHeight => RegionsY * RegionTileSize;
    public int RegionCount => RegionsX * RegionsY;
    public float AverageHeightMeters { get; }

    private readonly ushort[] _uniformHeightUnits;
    private readonly ushort[] _uniformMaterialIds;
    private readonly long[] _regionVersions;
    private readonly int[] _detailOffsets;
    private readonly ushort[] _detailHeightUnits;
    private readonly ushort[] _detailMaterialIds;

    public ReadOnlySpan<ushort> UniformHeightUnits => _uniformHeightUnits;
    public ReadOnlySpan<ushort> UniformMaterialIds => _uniformMaterialIds;
    public ReadOnlySpan<long> RegionVersions => _regionVersions;
    public ReadOnlySpan<int> DetailOffsets => _detailOffsets;

    private WorldTerrainSnapshot(
        int regionsX,
        int regionsY,
        int regionTileSize,
        float averageHeightMeters,
        ushort[] uniformHeightUnits,
        ushort[] uniformMaterialIds,
        long[] regionVersions,
        int[] detailOffsets,
        ushort[] detailHeightUnits,
        ushort[] detailMaterialIds)
    {
        RegionsX = regionsX;
        RegionsY = regionsY;
        RegionTileSize = regionTileSize;
        AverageHeightMeters = averageHeightMeters;
        _uniformHeightUnits = uniformHeightUnits;
        _uniformMaterialIds = uniformMaterialIds;
        _regionVersions = regionVersions;
        _detailOffsets = detailOffsets;
        _detailHeightUnits = detailHeightUnits;
        _detailMaterialIds = detailMaterialIds;
    }

    public static WorldTerrainSnapshot Capture(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        int regionCount = checked(worldMap.RegionsX * worldMap.RegionsY);
        int regionTileSize = TerrainRegion.TilesPerSide;
        int detailRegionCount = 0;

        foreach (WorldRegion region in worldMap.WorldRegions)
        {
            if (!region.HasUniformSurface)
                detailRegionCount++;
        }

        int detailTileCount = checked(
            detailRegionCount * regionTileSize * regionTileSize);

        ushort[] uniformHeightUnits = new ushort[regionCount];
        ushort[] uniformMaterialIds = new ushort[regionCount];
        long[] regionVersions = new long[regionCount];
        int[] detailOffsets = new int[regionCount];
        ushort[] detailHeightUnits = detailTileCount == 0
            ? Array.Empty<ushort>()
            : new ushort[detailTileCount];
        ushort[] detailMaterialIds = detailTileCount == 0
            ? Array.Empty<ushort>()
            : new ushort[detailTileCount];

        Array.Fill(detailOffsets, -1);

        long totalHeightUnits = 0;
        long totalSamples = 0;
        int detailCursor = 0;

        for (int regionY = 0; regionY < worldMap.RegionsY; regionY++)
        {
            for (int regionX = 0; regionX < worldMap.RegionsX; regionX++)
            {
                int regionIndex = regionX + regionY * worldMap.RegionsX;
                WorldRegion region = worldMap.GetWorldRegion(regionX, regionY)!;

                regionVersions[regionIndex] = region.TerrainVersion;

                if (region.HasUniformSurface)
                {
                    ushort height = region.UniformSurfaceHeightUnits;
                    uniformHeightUnits[regionIndex] = height;
                    uniformMaterialIds[regionIndex] =
                        height > 0 ? region.UniformSurfaceMaterialId : (ushort)0;

                    totalHeightUnits += (long)height *
                        regionTileSize * regionTileSize;
                    totalSamples += (long)regionTileSize * regionTileSize;
                    continue;
                }

                detailOffsets[regionIndex] = detailCursor;

                for (int localY = 0; localY < regionTileSize; localY++)
                {
                    int globalY = region.TileOriginY + localY;

                    for (int localX = 0; localX < regionTileSize; localX++)
                    {
                        int globalX = region.TileOriginX + localX;
                        ushort height = worldMap.GetSurfaceHeightUnits(
                            globalX,
                            globalY);

                        ushort material = 0;
                        if (height > 0)
                        {
                            int topLayer = Math.Clamp(
                                height - 1,
                                0,
                                worldMap.LayerCount - 1);

                            material = worldMap.GetMaterialId(
                                globalX,
                                globalY,
                                topLayer);
                        }

                        int detailIndex = detailCursor++;
                        detailHeightUnits[detailIndex] = height;
                        detailMaterialIds[detailIndex] = material;

                        totalHeightUnits += height;
                        totalSamples++;
                    }
                }
            }
        }

        float averageHeightMeters = totalSamples == 0
            ? 0f
            : totalHeightUnits / (float)totalSamples * 0.1f;

        return new WorldTerrainSnapshot(
            worldMap.RegionsX,
            worldMap.RegionsY,
            regionTileSize,
            averageHeightMeters,
            uniformHeightUnits,
            uniformMaterialIds,
            regionVersions,
            detailOffsets,
            detailHeightUnits,
            detailMaterialIds);
    }

    public int GetRegionIndex(int regionX, int regionY) =>
        regionX + regionY * RegionsX;

    public bool IsUniformRegion(int regionIndex) =>
        _detailOffsets[regionIndex] < 0;

    public ushort GetHeightUnitsAtTile(int tileX, int tileY)
    {
        if (!ContainsTile(tileX, tileY))
            return 0;

        int regionX = tileX / RegionTileSize;
        int regionY = tileY / RegionTileSize;
        int regionIndex = GetRegionIndex(regionX, regionY);
        int detailOffset = DetailOffsets[regionIndex];

        if (detailOffset < 0)
            return _uniformHeightUnits[regionIndex];

        int localX = tileX - regionX * RegionTileSize;
        int localY = tileY - regionY * RegionTileSize;
        return _detailHeightUnits[
            detailOffset + localX + localY * RegionTileSize];
    }

    public ushort GetMaterialIdAtTile(int tileX, int tileY)
    {
        if (!ContainsTile(tileX, tileY))
            return 0;

        int regionX = tileX / RegionTileSize;
        int regionY = tileY / RegionTileSize;
        int regionIndex = GetRegionIndex(regionX, regionY);
        int detailOffset = DetailOffsets[regionIndex];

        if (detailOffset < 0)
            return _uniformMaterialIds[regionIndex];

        int localX = tileX - regionX * RegionTileSize;
        int localY = tileY - regionY * RegionTileSize;
        return _detailMaterialIds[
            detailOffset + localX + localY * RegionTileSize];
    }

    private bool ContainsTile(int tileX, int tileY) =>
        tileX >= 0 &&
        tileY >= 0 &&
        tileX < TileWidth &&
        tileY < TileHeight;
}
