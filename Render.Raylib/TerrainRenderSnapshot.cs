using System.Collections.Generic;
using Core.Map;

namespace RimClone.Render.Raylib;

internal readonly record struct CaveInterval(int StartLayer, int EndLayer);

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
    private readonly float[] _cutHeightsMeters;
    private readonly ushort[] _cutMaterialIds;
    private readonly bool[] _cutFaces;
    private readonly long[] _sourceRegionVersions;
    private readonly Dictionary<int, CaveInterval[]> _caveIntervals = new();
    private long _cutSourceWorldVersion = long.MinValue;
    private long _caveSourceWorldVersion = long.MinValue;

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
    public int CutawayLayer { get; private set; } = -1;
    public float CutawayHeightMeters =>
        CutawayLayer * WorldMap.LayerHeightUnits * 0.1f;
    public bool CutawayEnabled { get; private set; }

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
        _cutHeightsMeters = new float[_heightsMeters.Length];
        _cutMaterialIds = new ushort[_heightsMeters.Length];
        _cutFaces = new bool[_heightsMeters.Length];
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

        bool cavesChanged = RefreshCaveIntervals(worldMap);
        return changed || cavesChanged;
    }

    private bool RefreshCaveIntervals(WorldMap worldMap)
    {
        if (_caveSourceWorldVersion == worldMap.TerrainVersion)
            return false;

        _caveIntervals.Clear();

        for (int renderY = 0; renderY < RegionsY; renderY++)
        {
            int tileY = renderY * RegionSize + RegionSize / 2;

            for (int renderX = 0; renderX < RegionsX; renderX++)
            {
                int tileX = renderX * RegionSize + RegionSize / 2;

                if (!worldMap.Blocks.Volumes.HasEmptyOverridesInColumn(
                        tileX,
                        tileY))
                {
                    continue;
                }

                ushort surfaceUnits =
                    worldMap.GetSurfaceHeightUnits(tileX, tileY);
                ReadOnlySpan<TileRange> ranges =
                    worldMap.GetTileRanges(tileX, tileY);

                List<CaveInterval>? intervals = null;

                foreach (TileRange range in ranges)
                {
                    if (range.State != WorldMap.StateEmpty ||
                        range.StartZ == 0 ||
                        range.EndZ >= surfaceUnits)
                    {
                        continue;
                    }

                    int startLayer =
                        range.StartZ / WorldMap.LayerHeightUnits;
                    int endLayer =
                        range.EndZ / WorldMap.LayerHeightUnits;

                    if (endLayer <= startLayer)
                        continue;

                    intervals ??= new List<CaveInterval>(2);
                    intervals.Add(new CaveInterval(startLayer, endLayer));
                }

                if (intervals is { Count: > 0 })
                {
                    _caveIntervals.Add(
                        renderX + renderY * RegionsX,
                        intervals.ToArray());
                }
            }
        }

        _caveSourceWorldVersion = worldMap.TerrainVersion;
        return true;
    }

    internal ReadOnlySpan<CaveInterval> GetCaveIntervals(
        int regionX,
        int regionY)
    {
        int index = GetIndex(regionX, regionY);
        if (index >= 0 &&
            _caveIntervals.TryGetValue(index, out CaveInterval[]? intervals))
        {
            return intervals;
        }

        return ReadOnlySpan<CaveInterval>.Empty;
    }

    internal bool HasCaveIntervals(int regionX, int regionY)
    {
        int index = GetIndex(regionX, regionY);
        return index >= 0 && _caveIntervals.ContainsKey(index);
    }

    /// <summary>
    /// Rebuilds the visible terrain at a horizontal cut plane. Cells above the
    /// plane are clipped. Solid cells on the plane become hatched cut faces;
    /// cells inside caves expose their highest solid floor below the plane.
    /// </summary>
    public bool RefreshCutawaySlice(WorldMap worldMap, int layer)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        if (layer < 0 || layer >= worldMap.LayerCount)
            throw new ArgumentOutOfRangeException(nameof(layer));

        if (CutawayEnabled &&
            CutawayLayer == layer &&
            _cutSourceWorldVersion == worldMap.TerrainVersion)
        {
            return false;
        }

        float cutHeight = layer * WorldMap.LayerHeightUnits * 0.1f;

        for (int renderY = 0; renderY < RegionsY; renderY++)
        {
            int tileY = renderY * RegionSize + RegionSize / 2;
            int rowStart = renderY * RegionsX;

            for (int renderX = 0; renderX < RegionsX; renderX++)
            {
                int tileX = renderX * RegionSize + RegionSize / 2;
                int index = rowStart + renderX;
                ushort surfaceUnits =
                    worldMap.GetSurfaceHeightUnits(tileX, tileY);
                float surfaceHeight = surfaceUnits * 0.1f;

                if (surfaceUnits <= layer * WorldMap.LayerHeightUnits)
                {
                    _cutHeightsMeters[index] = surfaceHeight;
                    int surfaceLayer = surfaceUnits == 0
                        ? -1
                        : Math.Clamp(
                            (surfaceUnits + WorldMap.LayerHeightUnits - 1) /
                            WorldMap.LayerHeightUnits - 1,
                            0,
                            worldMap.LayerCount - 1);

                    _cutMaterialIds[index] = surfaceLayer >= 0
                        ? worldMap.GetMaterialId(tileX, tileY, surfaceLayer)
                        : (ushort)0;
                    _cutFaces[index] = false;
                    continue;
                }

                ushort cutMaterial = worldMap.GetMaterialId(
                    tileX,
                    tileY,
                    layer);

                if (cutMaterial != 0)
                {
                    _cutHeightsMeters[index] = cutHeight;
                    _cutMaterialIds[index] = cutMaterial;
                    _cutFaces[index] = true;
                    continue;
                }

                // The plane intersects an empty cavity. Show the actual solid
                // floor below it, not a flat plan-view map of the empty slice.
                int floorLayer = worldMap.FindTopOccupiedLayer(
                    tileX,
                    tileY,
                    layer - 1);

                _cutHeightsMeters[index] = floorLayer >= 0
                    ? (floorLayer + 1) * 0.1f
                    : 0f;
                _cutMaterialIds[index] = floorLayer >= 0
                    ? worldMap.GetMaterialId(tileX, tileY, floorLayer)
                    : (ushort)0;
                _cutFaces[index] = false;
            }
        }

        CutawayLayer = layer;
        CutawayEnabled = true;
        _cutSourceWorldVersion = worldMap.TerrainVersion;
        return true;
    }

    public void DisableCutaway()
    {
        CutawayEnabled = false;
        CutawayLayer = -1;
        _cutSourceWorldVersion = long.MinValue;
    }

    internal float GetRenderHeightMeters(int regionX, int regionY)
    {
        int index = GetIndex(regionX, regionY);
        if (index < 0)
            return 0f;

        return CutawayEnabled
            ? _cutHeightsMeters[index]
            : _heightsMeters[index];
    }

    internal ushort GetRenderMaterialId(int regionX, int regionY)
    {
        int index = GetIndex(regionX, regionY);
        if (index < 0)
            return 0;

        return CutawayEnabled
            ? _cutMaterialIds[index]
            : _materialIds[index];
    }

    internal bool IsCutFace(int regionX, int regionY)
    {
        int index = GetIndex(regionX, regionY);
        return index >= 0 && CutawayEnabled && _cutFaces[index];
    }

    private int GetIndex(int regionX, int regionY)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= RegionsX || regionY >= RegionsY)
        {
            return -1;
        }

        return regionX + regionY * RegionsX;
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
