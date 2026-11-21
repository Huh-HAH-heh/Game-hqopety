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

    private WorldMap _worldMap;
    private readonly float[] _heightsMeters;
    private readonly ushort[] _materialIds;
    private readonly long[] _sourceRegionVersions;
    private readonly Dictionary<int, TileRange[]> _complexColumnRanges = new();

    private long _complexSourceWorldVersion = long.MinValue;
    private bool _initialized;

    public int RegionsX { get; }
    public int RegionsY { get; }
    public int RegionSize { get; }
    public bool IsVoxelMode { get; }
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

    private TerrainRenderSnapshot(
        WorldMap worldMap,
        int renderRegionSizeInTiles,
        bool voxelMode)
    {
        if (renderRegionSizeInTiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(renderRegionSizeInTiles));

        _worldMap = worldMap;
        RegionSize = renderRegionSizeInTiles;
        IsVoxelMode = voxelMode;
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

    internal static TerrainRenderSnapshot Create(
        WorldMap worldMap,
        TerrainRenderMode mode)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        int renderRegionSize = mode == TerrainRenderMode.Voxel
            ? VoxelRenderRegionSizeInTiles
            : HeightmapRenderRegionSizeInTiles;
        return new TerrainRenderSnapshot(
            worldMap,
            renderRegionSize,
            voxelMode: mode == TerrainRenderMode.Voxel);
    }

    /// <summary>
    /// Creates a coarser presentation snapshot from the same voxel world.
    /// Each coarse cell samples one real source column and retains its full
    /// vertical solid/air intervals when that source column is complex.
    /// The underlying WorldMap is never resampled or modified.
    /// </summary>
    internal static TerrainRenderSnapshot CreateVoxelLod(
        WorldMap worldMap,
        int cellSizeInMeters = 4)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        if (cellSizeInMeters <= VoxelRenderRegionSizeInTiles ||
            cellSizeInMeters > TerrainRegion.TilesPerSide ||
            TerrainRegion.TilesPerSide % cellSizeInMeters != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSizeInMeters));
        }

        return new TerrainRenderSnapshot(
            worldMap,
            cellSizeInMeters,
            voxelMode: true);
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

        // Every voxel LOD resolves complex columns on demand from WorldMap.
        // Its range cache retains queried columns, avoiding a full coarse-grid
        // scan whenever the terrain changes.
        if (IsVoxelMode)
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

                if (worldMap.IsColumnSimpleFill(tileX, tileY))
                    continue;

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

        if (IsVoxelMode)
        {
            // Both fine and coarse voxel LODs sample an exact source column.
            // Simple columns use the refreshed compact snapshot; complex
            // columns are fetched lazily from WorldMap's shared range cache.
            int sourceX = regionX * RegionSize + RegionSize / 2;
            int sourceY = regionY * RegionSize + RegionSize / 2;
            if (_worldMap.IsColumnSimpleFill(sourceX, sourceY))
            {
                if (destination.Length < 2)
                    throw new ArgumentException(
                        "Destination must hold at least two voxel ranges.",
                        nameof(destination));

                int snapshotIndex = regionX + regionY * RegionsX;
                int topLayer = Math.Clamp(
                    (int)MathF.Round(
                        _heightsMeters[snapshotIndex] /
                            (WorldMap.LayerHeightUnits * 0.1f),
                        MidpointRounding.AwayFromZero),
                    0,
                    LayerCount);
                ushort simpleMaterial = _materialIds[snapshotIndex];

                if (topLayer == 0 || simpleMaterial == 0)
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
                    EndZ = checked((ushort)topLayer),
                    MaterialId = simpleMaterial,
                    State = WorldMap.StateSolid
                };

                if (topLayer >= LayerCount)
                    return 1;

                destination[1] = new TileRange
                {
                    StartZ = checked((ushort)topLayer),
                    EndZ = checked((ushort)LayerCount),
                    MaterialId = 0,
                    State = WorldMap.StateEmpty
                };
                return 2;
            }

            ReadOnlySpan<TileRange> exactRanges =
                _worldMap.GetTileRanges(sourceX, sourceY);
            if (exactRanges.IsEmpty)
                return 0;

            if (destination.Length < exactRanges.Length)
                throw new ArgumentException(
                    "Destination is too small for the voxel column ranges.",
                    nameof(destination));

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

    /// <summary>
    /// Returns the material exposed by a horizontal cut plane in one render cell.
    /// The common bottom-filled case is resolved directly from the compact
    /// height/material snapshot; only edited columns need their exact run list.
    /// A cut cap is required only when the plane crosses the inside of a solid run.
    /// If EndZ equals the cut layer, the base mesh already owns that natural top face.
    /// </summary>
    internal ushort GetCutSurfaceMaterial(int regionX, int regionY, int cutLayer)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= RegionsX || regionY >= RegionsY ||
            cutLayer <= 0 || cutLayer >= LayerCount)
        {
            return 0;
        }

        int index = regionX + regionY * RegionsX;
        int sourceX = regionX * RegionSize + RegionSize / 2;
        int sourceY = regionY * RegionSize + RegionSize / 2;

        if (_worldMap.IsColumnSimpleFill(sourceX, sourceY))
        {
            int topLayer = Math.Clamp(
                (int)MathF.Round(
                    _heightsMeters[index] /
                    (WorldMap.LayerHeightUnits * 0.1f),
                    MidpointRounding.AwayFromZero),
                0,
                LayerCount);

            return topLayer > cutLayer
                ? _materialIds[index]
                : (ushort)0;
        }

        ReadOnlySpan<TileRange> ranges =
            _worldMap.GetTileRanges(sourceX, sourceY);
        for (int i = 0; i < ranges.Length; i++)
        {
            TileRange range = ranges[i];
            if (range.State != WorldMap.StateSolid ||
                range.MaterialId == 0 ||
                range.StartZ >= cutLayer)
            {
                continue;
            }

            if (range.EndZ > cutLayer)
                return range.MaterialId;

            // A material boundary can lie exactly on the section plane while
            // solid material continues above it. The base mesher intentionally
            // emits no face between two solid ranges, so the lower material
            // must still supply the section cap.
            if (range.EndZ == cutLayer &&
                i + 1 < ranges.Length &&
                ranges[i + 1].State == WorldMap.StateSolid &&
                ranges[i + 1].StartZ == cutLayer)
            {
                return range.MaterialId;
            }
        }

        return 0;
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
