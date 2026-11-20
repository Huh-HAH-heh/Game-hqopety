using System.Collections.Generic;
using Core.Map;

namespace RimClone.Render.Raylib;

/// <summary>
/// Presentation-owned terrain data. Simple columns stay as compact height/material
/// pairs; only columns with real vertical edits or sparse volume overrides copy
/// their full solid/air interval list.
/// </summary>
public sealed class TerrainRenderSnapshot
{
    public const int VoxelRenderRegionSizeInTiles = 1;
    public const int HeightmapRenderRegionSizeInTiles = 4;

    private readonly WorldMap _worldMap;
    private readonly float[] _heightsMeters;
    private readonly ushort[] _materialIds;
    private readonly long[] _sourceRegionVersions;
    private readonly Dictionary<int, TileRange[]> _complexColumnRanges = new();

    private long _complexSourceWorldVersion = long.MinValue;
    private bool _initialized;

    public int RegionsX { get; }
    public int RegionsY { get; }
    public int RegionSize { get; }
    public bool UsesVoxelCells => RegionSize == VoxelRenderRegionSizeInTiles;
    public int RegionCount => _heightsMeters.Length;

    public int StorageRegionsX { get; }
    public int StorageRegionsY { get; }
    public int StorageRegionCount => _sourceRegionVersions.Length;
    public int StorageRegionSize { get; }
    public int RenderRegionsPerStorageRegion { get; }

    public int LayerCount { get; }
    public float WorldWidth => RegionsX * RegionSize;
    public float WorldDepth => RegionsY * RegionSize;
    public float MaximumHeightMeters { get; private set; }
    public float VerticalWorldExtentMeters { get; }
    public long SourceWorldVersion { get; private set; }

    public int CutawayLayer { get; private set; } = -1;
    public float CutawayHeightMeters =>
        CutawayLayer * WorldMap.LayerHeightUnits * 0.1f;
    public bool CutawayEnabled { get; private set; }

    internal long[] SourceRegionVersions => _sourceRegionVersions;

    private TerrainRenderSnapshot(WorldMap worldMap, int renderRegionSizeInTiles)
    {
        if (renderRegionSizeInTiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(renderRegionSizeInTiles));

        _worldMap = worldMap;
        RegionSize = renderRegionSizeInTiles;
        StorageRegionsX = worldMap.RegionsX;
        StorageRegionsY = worldMap.RegionsY;
        StorageRegionSize = TerrainRegion.TilesPerSide;
        LayerCount = worldMap.LayerCount;
        VerticalWorldExtentMeters =
            LayerCount * WorldMap.LayerHeightUnits * 0.1f;

        if (StorageRegionSize % RegionSize != 0)
        {
            throw new InvalidOperationException(
                "Storage region size must be divisible by the render region size.");
        }

        RenderRegionsPerStorageRegion =
            StorageRegionSize / RegionSize;

        RegionsX = checked(StorageRegionsX * RenderRegionsPerStorageRegion);
        RegionsY = checked(StorageRegionsY * RenderRegionsPerStorageRegion);

        _heightsMeters = new float[checked(RegionsX * RegionsY)];
        _materialIds = new ushort[_heightsMeters.Length];
        _sourceRegionVersions =
            new long[checked(StorageRegionsX * StorageRegionsY)];

        Array.Fill(_sourceRegionVersions, long.MinValue);
        RefreshFromWorld(worldMap);
    }

    public static TerrainRenderSnapshot Create(WorldMap worldMap) =>
        Create(worldMap, TerrainRenderMode.HeightmapFallback);

    public static TerrainRenderSnapshot Create(
        WorldMap worldMap,
        TerrainRenderMode mode)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        int renderRegionSize = mode == TerrainRenderMode.Voxel
            ? VoxelRenderRegionSizeInTiles
            : HeightmapRenderRegionSizeInTiles;
        return new TerrainRenderSnapshot(worldMap, renderRegionSize);
    }

    /// <summary>
    /// Refreshes presentation data only when source terrain changes. A full
    /// vertical interval copy is retained only for non-simple or volume-edited
    /// columns; ordinary solid terrain stays compact.
    /// </summary>
    public bool RefreshFromWorld(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        if (worldMap.RegionsX != StorageRegionsX ||
            worldMap.RegionsY != StorageRegionsY ||
            worldMap.LayerCount != LayerCount)
        {
            throw new InvalidOperationException(
                "World topology changed. Create a new terrain snapshot.");
        }

        _worldMap = worldMap;
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
            {
                maximumHeight = MathF.Max(
                    maximumHeight,
                    _heightsMeters[i]);
            }

            MaximumHeightMeters = maximumHeight;
        }

        bool volumeDataChanged = RefreshComplexColumnRanges(worldMap);

        SourceWorldVersion = worldMap.TerrainVersion;
        _initialized = true;
        return changed || volumeDataChanged;
    }

    private bool RefreshComplexColumnRanges(WorldMap worldMap)
    {
        if (_complexSourceWorldVersion == worldMap.TerrainVersion)
            return false;

        _complexColumnRanges.Clear();

        // Voxel mode resolves exact 1x1 m columns on demand as visible chunks
        // are meshed. This avoids scanning and retaining ranges for the entire
        // multi-million-column world during snapshot creation.
        if (UsesVoxelCells)
        {
            _complexSourceWorldVersion = worldMap.TerrainVersion;
            return false;
        }

        for (int renderY = 0; renderY < RegionsY; renderY++)
        {
            int tileY = renderY * RegionSize + RegionSize / 2;
            int rowStart = renderY * RegionsX;

            for (int renderX = 0; renderX < RegionsX; renderX++)
            {
                int tileX = renderX * RegionSize + RegionSize / 2;

                bool hasVolumeOverrides =
                    worldMap.Blocks.Volumes.HasOverridesInColumn(
                        tileX,
                        tileY);

                if (worldMap.IsColumnSimpleFill(tileX, tileY) &&
                    !hasVolumeOverrides)
                {
                    continue;
                }

                TileRange[] ranges =
                    worldMap.GetTileRanges(tileX, tileY).ToArray();

                _complexColumnRanges.Add(
                    rowStart + renderX,
                    ranges);
            }
        }

        _complexSourceWorldVersion = worldMap.TerrainVersion;
        return true;
    }

    /// <summary>
    /// Copies one render column's vertical occupancy intervals into caller
    /// storage. The common height-field case is reconstructed from two values;
    /// caves, strata and edited columns use their exact source intervals.
    /// </summary>
    internal int CopyColumnRanges(
        int regionX,
        int regionY,
        Span<TileRange> destination)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= RegionsX || regionY >= RegionsY)
        {
            return 0;
        }

        int index = regionX + regionY * RegionsX;
        if (_complexColumnRanges.TryGetValue(
                index,
                out TileRange[]? complexRanges))
        {
            if (destination.Length < complexRanges.Length)
            {
                throw new ArgumentException(
                    "Destination is too small for the column ranges.",
                    nameof(destination));
            }

            complexRanges.AsSpan().CopyTo(destination);
            return complexRanges.Length;
        }

        if (UsesVoxelCells)
        {
            ReadOnlySpan<TileRange> exactRanges =
                _worldMap.GetTileRanges(regionX, regionY);
            if (exactRanges.IsEmpty)
                return 0;

            if (destination.Length < exactRanges.Length)
            {
                throw new ArgumentException(
                    "Destination is too small for the voxel column ranges.",
                    nameof(destination));
            }

            exactRanges.CopyTo(destination);
            return exactRanges.Length;
        }

        if (destination.Length < 2)
        {
            throw new ArgumentException(
                "Destination must hold at least two ranges.",
                nameof(destination));
        }

        int filledLayers = Math.Clamp(
            (int)MathF.Round(
                _heightsMeters[index] /
                (WorldMap.LayerHeightUnits * 0.1f)),
            0,
            LayerCount);
        ushort materialId = _materialIds[index];

        if (filledLayers <= 0 || materialId == 0)
        {
            destination[0] = new TileRange
            {
                StartZ = 0,
                EndZ = checked((ushort)LayerCount),
                MaterialId = 0,
                State = WorldMap.StateEmpty
            };
            return 1;
        }

        destination[0] = new TileRange
        {
            StartZ = 0,
            EndZ = checked((ushort)filledLayers),
            MaterialId = materialId,
            State = WorldMap.StateSolid
        };

        if (filledLayers >= LayerCount)
            return 1;

        destination[1] = new TileRange
        {
            StartZ = checked((ushort)filledLayers),
            EndZ = checked((ushort)LayerCount),
            MaterialId = 0,
            State = WorldMap.StateEmpty
        };

        return 2;
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

    public bool SetCutawayLayer(int layer)
    {
        if (layer < 0 || layer >= LayerCount)
            throw new ArgumentOutOfRangeException(nameof(layer));

        if (CutawayEnabled && CutawayLayer == layer)
            return false;

        CutawayLayer = layer;
        CutawayEnabled = true;
        return true;
    }

    public void DisableCutaway()
    {
        CutawayEnabled = false;
        CutawayLayer = -1;
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
