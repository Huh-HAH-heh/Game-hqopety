using Core.Map;

namespace RimClone.Render.Raylib;

/// <summary>
/// Presentation-owned surface snapshot.
/// The overlay is partitioned into 4x4-tile render regions while the simulation
/// keeps its larger storage regions. The renderer never queries WorldMap.
/// </summary>
public sealed class TerrainRenderSnapshot
{
    public const int RenderRegionSizeInTiles = 4;

    private readonly float[] _heightsMeters;
    private readonly ushort[] _materialIds;
    private readonly ushort[] _sliceMaterialIds;
    private readonly long[] _sourceRegionVersions;
    private long _sliceSourceWorldVersion = long.MinValue;

    public int RegionsX { get; }
    public int RegionsY { get; }
    public int RegionSize => RenderRegionSizeInTiles;
    public int RegionCount => _heightsMeters.Length;

    public int StorageRegionsX { get; }
    public int StorageRegionsY { get; }
    public int StorageRegionCount => _sourceRegionVersions.Length;
    public int StorageRegionSize { get; }
    public int RenderRegionsPerStorageRegion { get; }

    public float WorldWidth => RegionsX * RegionSize;
    public float WorldDepth => RegionsY * RegionSize;
    public float MaximumHeightMeters { get; private set; }
    public long SourceWorldVersion { get; private set; }
    public int SliceLayer { get; private set; } = -1;
    public float SliceHeightMeters =>
        SliceLayer * WorldMap.LayerHeightUnits * 0.1f;

    internal long[] SourceRegionVersions => _sourceRegionVersions;

    private bool _initialized;

    private TerrainRenderSnapshot(WorldMap worldMap)
    {
        StorageRegionsX = worldMap.RegionsX;
        StorageRegionsY = worldMap.RegionsY;
        StorageRegionSize = TerrainRegion.TilesPerSide;

        if (StorageRegionSize % RenderRegionSizeInTiles != 0)
        {
            throw new InvalidOperationException(
                "Storage region size must be divisible by the render region size.");
        }

        RenderRegionsPerStorageRegion =
            StorageRegionSize / RenderRegionSizeInTiles;

        RegionsX = checked(StorageRegionsX * RenderRegionsPerStorageRegion);
        RegionsY = checked(StorageRegionsY * RenderRegionsPerStorageRegion);

        _heightsMeters = new float[checked(RegionsX * RegionsY)];
        _materialIds = new ushort[_heightsMeters.Length];
        _sliceMaterialIds = new ushort[_heightsMeters.Length];
        _sourceRegionVersions =
            new long[checked(StorageRegionsX * StorageRegionsY)];

        Array.Fill(_sourceRegionVersions, long.MinValue);
        RefreshFromWorld(worldMap);
    }

    public static TerrainRenderSnapshot Create(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        return new TerrainRenderSnapshot(worldMap);
    }

    internal float GetHeightMeters(int regionX, int regionY)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= RegionsX || regionY >= RegionsY)
        {
            return 0f;
        }

        return _heightsMeters[regionX + regionY * RegionsX];
    }

    internal ushort GetMaterialId(int regionX, int regionY)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= RegionsX || regionY >= RegionsY)
        {
            return 0;
        }

        return _materialIds[regionX + regionY * RegionsX];
    }

    /// <summary>
    /// Copies only storage regions whose own or adjacent terrain revision changed.
    /// Each storage region is sampled at a 4x4-tile presentation resolution.
    /// Call on the owning/game thread, never from the render worker.
    /// </summary>
    public bool RefreshFromWorld(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        if (worldMap.RegionsX != StorageRegionsX ||
            worldMap.RegionsY != StorageRegionsY)
        {
            throw new InvalidOperationException(
                "World topology changed. Create a new terrain snapshot.");
        }

        if (_initialized && SourceWorldVersion == worldMap.TerrainVersion)
            return false;

        bool changed = false;
        ReadOnlySpan<WorldRegion> regions = worldMap.WorldRegions;

        for (int i = 0; i < regions.Length; i++)
        {
            WorldRegion region = regions[i];
            long regionVersion = worldMap.GetRegionTerrainVersion(
                region.RegionX,
                region.RegionY);

            if (_sourceRegionVersions[i] == regionVersion)
                continue;

            CopyStorageRegion(worldMap, region);
            _sourceRegionVersions[i] = regionVersion;
            changed = true;
        }

        if (changed)
        {
            float maximumHeight = 0f;
            for (int i = 0; i < _heightsMeters.Length; i++)
                maximumHeight = MathF.Max(maximumHeight, _heightsMeters[i]);

            MaximumHeightMeters = maximumHeight;
        }

        SourceWorldVersion = worldMap.TerrainVersion;
        _initialized = true;
        return changed;
    }

    /// <summary>
    /// Samples a horizontal underground section from the actual 3D occupancy.
    /// This is a presentation snapshot only; the renderer never reads WorldMap.
    /// One material sample is taken at the centre of every 4x4-tile render patch.
    /// </summary>
    public bool RefreshUndergroundSlice(WorldMap worldMap, int layer)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        if (layer < 0 || layer >= worldMap.LayerCount)
            throw new ArgumentOutOfRangeException(nameof(layer));

        if (SliceLayer == layer &&
            _sliceSourceWorldVersion == worldMap.TerrainVersion)
        {
            return false;
        }

        for (int renderY = 0; renderY < RegionsY; renderY++)
        {
            int tileY = renderY * RegionSize + RegionSize / 2;
            int rowStart = renderY * RegionsX;

            for (int renderX = 0; renderX < RegionsX; renderX++)
            {
                int tileX = renderX * RegionSize + RegionSize / 2;
                _sliceMaterialIds[rowStart + renderX] =
                    worldMap.GetMaterialId(tileX, tileY, layer);
            }
        }

        SliceLayer = layer;
        _sliceSourceWorldVersion = worldMap.TerrainVersion;
        return true;
    }

    internal ushort GetSliceMaterialId(int regionX, int regionY)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= RegionsX || regionY >= RegionsY)
        {
            return 0;
        }

        return _sliceMaterialIds[regionX + regionY * RegionsX];
    }

    private void CopyStorageRegion(
        WorldMap worldMap,
        WorldRegion region)
    {
        int firstRenderX =
            region.RegionX * RenderRegionsPerStorageRegion;
        int firstRenderY =
            region.RegionY * RenderRegionsPerStorageRegion;

        float uniformHeight = region.HasUniformSurface
            ? region.UniformSurfaceHeightUnits * 0.1f
            : 0f;
        ushort uniformMaterial = region.HasUniformSurface
            ? region.UniformSurfaceMaterialId
            : (ushort)0;

        for (int localY = 0;
             localY < RenderRegionsPerStorageRegion;
             localY++)
        {
            int rowStart =
                firstRenderX +
                (firstRenderY + localY) * RegionsX;

            if (region.HasUniformSurface)
            {
                Array.Fill(
                    _heightsMeters,
                    uniformHeight,
                    rowStart,
                    RenderRegionsPerStorageRegion);
                Array.Fill(
                    _materialIds,
                    uniformMaterial,
                    rowStart,
                    RenderRegionsPerStorageRegion);
                continue;
            }

            for (int localX = 0;
                 localX < RenderRegionsPerStorageRegion;
                 localX++)
            {
                int sampleX = region.TileOriginX +
                    localX * RegionSize + RegionSize / 2;
                int sampleY = region.TileOriginY +
                    localY * RegionSize + RegionSize / 2;

                ushort heightUnits =
                    worldMap.GetSurfaceHeightUnits(sampleX, sampleY);
                int index = rowStart + localX;

                _heightsMeters[index] = heightUnits * 0.1f;
                _materialIds[index] = GetSurfaceMaterial(
                    worldMap,
                    sampleX,
                    sampleY,
                    heightUnits);
            }
        }
    }

    private static ushort GetSurfaceMaterial(
        WorldMap worldMap,
        int x,
        int y,
        ushort heightUnits)
    {
        if (heightUnits == 0)
            return 0;

        int topLayer = Math.Clamp(
            (heightUnits + WorldMap.LayerHeightUnits - 1) /
            WorldMap.LayerHeightUnits - 1,
            0,
            worldMap.LayerCount - 1);

        return worldMap.GetMaterialId(x, y, topLayer);
    }
}
