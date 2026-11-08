using System;

namespace Core.Map;

public sealed class WorldMap
{
    public const byte StateEmpty = 0;
    public const byte StateSolid = 1;

    public const int DefaultTerrainLayerCount = 500;
    public const ushort HeightUnitsPerMeter = 10;
    public const ushort LayerHeightUnits = 1;

    public int RegionsX { get; }
    public int RegionsY { get; }

    public int TileWidth =>
        RegionsX * TerrainRegion.TilesPerSide;

    public int TileHeight =>
        RegionsY * TerrainRegion.TilesPerSide;

    public int MaxTileX => TileWidth - 1;
    public int MaxTileY => TileHeight - 1;

    public int LayerCount => _layers.Length;

    public long TerrainVersion { get; private set; }
    public long TerrainGeometryVersion { get; private set; }
    public long NavigationVersion { get; private set; }

    public WaterLayer Water { get; }

    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly TerrainLayer[] _layers;
    private readonly ushort[] _surfaceHeights;
    private readonly bool[] _navigationBlocked;
    private readonly bool[] _simpleFillColumns;
    private readonly ushort[] _columnMaterialIds;
    private readonly TileRange[]?[] _rangeCache;
    private readonly long[] _regionTerrainVersions;

    public WorldMap(
        int regionsX = 16,
        int regionsY = 16,
        int layerCount = DefaultTerrainLayerCount)
    {
        if (regionsX <= 0)
            throw new ArgumentOutOfRangeException(nameof(regionsX));

        if (regionsY <= 0)
            throw new ArgumentOutOfRangeException(nameof(regionsY));

        if (layerCount <= 0 ||
            layerCount > ushort.MaxValue / LayerHeightUnits)
        {
            throw new ArgumentOutOfRangeException(nameof(layerCount));
        }

        RegionsX = regionsX;
        RegionsY = regionsY;

        int width = TileWidth;
        int height = TileHeight;
        int columnCount = checked(width * height);

        _tileRegions =
            new TerrainTileRegion?[checked(regionsX * regionsY)];

        _layers = new TerrainLayer[layerCount];

        for (int z = 0; z < layerCount; z++)
        {
            _layers[z] =
                new TerrainLayer(
                    z,
                    width,
                    height,
                    regionsX,
                    regionsY,
                    _tileRegions,
                    this);
        }

        _surfaceHeights = new ushort[columnCount];
        _navigationBlocked = new bool[columnCount];
        _simpleFillColumns = new bool[columnCount];
        Array.Fill(_simpleFillColumns, true);
        _columnMaterialIds = new ushort[columnCount];
        _rangeCache = new TileRange[]?[columnCount];
        _regionTerrainVersions = new long[checked(regionsX * regionsY)];

        Water =
            new WaterLayer(
                width,
                height);
    }

    public long GetRegionTerrainVersion(int regionX, int regionY)
    {
        if (regionX < 0 || regionX >= RegionsX ||
            regionY < 0 || regionY >= RegionsY)
            return -1;

        return _regionTerrainVersions[regionX + regionY * RegionsX];
    }

    private void MarkTerrainChanged(int x, int y)
    {
        TerrainVersion++;
        TerrainGeometryVersion++;

        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        _regionTerrainVersions[regionX + regionY * RegionsX]++;
    }

    // ============================================================
    // LAYER -> REGION -> VOXEL STORAGE
    // Every TerrainLayer owns its XY TerrainRegion grid.
    // Each XY cell is 1 m across; each Z layer is 0.1 m tall.
    // All 500 levels are stored densely to preserve full vertical detail.
    // ID 0 means empty; every other ID identifies a material.
    // ============================================================

    public TerrainLayer GetLayer(
        int z)
    {
        if (z < 0 || z >= LayerCount)
            throw new IndexOutOfRangeException();

        return _layers[z];
    }

    public ushort GetMaterialId(
        int x,
        int y,
        int z)
    {
        if (!IsInside(x, y) ||
            z < 0 ||
            z >= LayerCount)
        {
            return 0;
        }

        int columnIndex = GetColumnIndex(x, y);

        if (_simpleFillColumns[columnIndex])
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
                LayerHeightUnits,
                0,
                LayerCount);

            ushort materialId = _columnMaterialIds[columnIndex];
            return z < filledLayers ? materialId : (ushort)0;
        }

        return _layers[z].GetMaterialIdAtIndex(columnIndex);
    }

    public void SetMaterialId(
        int x,
        int y,
        int z,
        ushort materialId)
    {
        if (!IsInside(x, y) ||
            z < 0 ||
            z >= LayerCount)
        {
            throw new IndexOutOfRangeException();
        }

        GetOrCreateRegion(
            x / TerrainRegion.TilesPerSide,
            y / TerrainRegion.TilesPerSide);

        int columnIndex = GetColumnIndex(x, y);
        MaterializeSimpleFillColumn(x, y, columnIndex);

        _layers[z].SetMaterialId(
            x,
            y,
            materialId);

        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
        RecalculateSurfaceHeight(columnIndex);
        MarkTerrainChanged(x, y);
    }

    public bool IsNavigationBlocked(int x, int y)
    {
        if (!IsInside(x, y))
            return true;

        return _navigationBlocked[GetColumnIndex(x, y)];
    }

    public void SetNavigationBlocked(int x, int y, bool blocked)
    {
        if (!IsInside(x, y))
            throw new IndexOutOfRangeException();

        int index = GetColumnIndex(x, y);

        if (_navigationBlocked[index] == blocked)
            return;

        _navigationBlocked[index] = blocked;
        NavigationVersion++;
        TerrainVersion++;
    }

    // Uppermost occupied voxel at or below a horizontal Z slice.
    // Continuous/simple columns resolve in O(1); edited columns scan downward.
    public int FindTopOccupiedLayer(int x, int y, int maxLayer)
    {
        if (!IsInside(x, y) || maxLayer < 0)
            return -1;
        int columnIndex = GetColumnIndex(x, y);
        int lastLayer = Math.Min(maxLayer, LayerCount - 1);
        if (_simpleFillColumns[columnIndex])
        {
            if (_columnMaterialIds[columnIndex] == 0)
                return -1;
            int filledLayers = Math.Clamp(
                (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
                LayerHeightUnits, 0, LayerCount);
            return Math.Min(filledLayers - 1, lastLayer);
        }

        int surfaceLayer = Math.Min(
            lastLayer,
            Math.Clamp(
                (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
                LayerHeightUnits - 1,
                -1,
                LayerCount - 1));
        if (surfaceLayer < 0)
            return -1;

        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        int localIndex = x % TerrainRegion.TilesPerSide +
            (y % TerrainRegion.TilesPerSide) * TerrainRegion.TilesPerSide;
        for (int z = surfaceLayer; z >= 0; z--)
        {
            if (_layers[z].GetRegion(regionX, regionY)!
                    .GetMaterialIdAtIndex(localIndex) != 0)
                return z;
        }
        return -1;
    }

    // Inclusive interval: is any voxel in this vertical column solid?
    public bool IsColumnSolidBetween(int x, int y, int firstLayer, int lastLayer)
    {
        if (!IsInside(x, y))
            return true;
        firstLayer = Math.Max(0, firstLayer);
        lastLayer = Math.Min(LayerCount - 1, lastLayer);
        if (firstLayer > lastLayer)
            return false;

        int columnIndex = GetColumnIndex(x, y);
        if (_simpleFillColumns[columnIndex])
        {
            if (_columnMaterialIds[columnIndex] == 0)
                return false;
            int filledLayers = Math.Clamp(
                (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
                LayerHeightUnits, 0, LayerCount);
            return firstLayer < filledLayers;
        }

        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        int localIndex = x % TerrainRegion.TilesPerSide +
            (y % TerrainRegion.TilesPerSide) * TerrainRegion.TilesPerSide;
        for (int z = firstLayer; z <= lastLayer; z++)
        {
            if (_layers[z].GetRegion(regionX, regionY)!
                    .GetMaterialIdAtIndex(localIndex) != 0)
                return true;
        }
        return false;
    }

    public bool IsColumnVolumeEmpty(int x, int y, int firstLayer, int lastLayer) =>
        !IsColumnSolidBetween(x, y, firstLayer, lastLayer);

    // Finds a solid floor voxel with enough empty vertical clearance above it.
    public bool TryFindWalkableSurface(
        int x, int y, int minFloorLayer, int maxFloorLayer,
        int clearanceLayers, out int floorLayer)
    {
        floorLayer = -1;
        if (!IsInside(x, y) || clearanceLayers < 1)
            return false;
        int minLayer = Math.Max(0, minFloorLayer);
        int maxLayer = Math.Min(LayerCount - 1, maxFloorLayer);
        for (int z = maxLayer; z >= minLayer; z--)
        {
            if (GetMaterialId(x, y, z) == 0)
                continue;
            int clearanceTop = Math.Min(LayerCount - 1, z + clearanceLayers);
            if (IsColumnVolumeEmpty(x, y, z + 1, clearanceTop))
            {
                floorLayer = z;
                return true;
            }
        }
        return false;
    }

    public int GetSurfaceLayer(
        int x,
        int y)
    {
        if (!IsInside(x, y))
            return -1;

        ushort height =
            _surfaceHeights[GetColumnIndex(x, y)];

        if (height == 0)
            return -1;

        int layer =
            (height + LayerHeightUnits - 1) /
            LayerHeightUnits - 1;

        return Math.Min(
            LayerCount - 1,
            layer);
    }

    public int GetHighestOccupiedLayer()
    {
        int highest = -1;

        for (int i = 0; i < _surfaceHeights.Length; i++)
        {
            ushort height = _surfaceHeights[i];

            if (height == 0)
                continue;

            int layer =
                (height + LayerHeightUnits - 1) /
                LayerHeightUnits - 1;

            if (layer > highest)
                highest = layer;
        }

        return Math.Min(
            LayerCount - 1,
            highest);
    }

    // ============================================================
    // Tile/navigation metadata is shared between Z levels.
    // Voxel regions themselves belong to exactly one TerrainLayer.
    // ============================================================

    public TerrainRegion? GetRegion(
        int regionX,
        int regionY)
    {
        if (regionX < 0 ||
            regionX >= RegionsX ||
            regionY < 0 ||
            regionY >= RegionsY)
        {
            return null;
        }

        int index = regionX + regionY * RegionsX;

        if (_tileRegions[index] == null)
            return null;

        return _layers[0].GetRegion(regionX, regionY);
    }

    public TerrainRegion GetOrCreateRegion(
        int regionX,
        int regionY)
    {
        if (regionX < 0 ||
            regionX >= RegionsX ||
            regionY < 0 ||
            regionY >= RegionsY)
        {
            throw new IndexOutOfRangeException();
        }

        int index = regionX + regionY * RegionsX;

        _tileRegions[index] ??=
            new TerrainTileRegion(
                regionX,
                regionY);

        return _layers[0].GetOrCreateRegion(
            regionX,
            regionY);
    }

    public bool TryGetTile(
        int globalX,
        int globalY,
        out Tile tile)
    {
        tile = default;

        if (!IsInside(globalX, globalY))
            return false;

        TerrainRegion? region =
            GetRegion(
                globalX / TerrainRegion.TilesPerSide,
                globalY / TerrainRegion.TilesPerSide);

        if (region == null)
            return false;

        tile = region.GetLocalTile(
            globalX % TerrainRegion.TilesPerSide,
            globalY % TerrainRegion.TilesPerSide);

        return true;
    }

    public ref Tile GetOrCreateTile(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        TerrainRegion region =
            GetOrCreateRegion(
                globalX / TerrainRegion.TilesPerSide,
                globalY / TerrainRegion.TilesPerSide);

        return ref region.GetLocalTile(
            globalX % TerrainRegion.TilesPerSide,
            globalY % TerrainRegion.TilesPerSide);
    }

    // ============================================================
    // COMPATIBILITY ADAPTERS
    // Existing generators may still describe meter-height ranges.
    // These methods translate them immediately into voxel IDs.
    // No range data is stored in TerrainRegion.
    // ============================================================

    public ReadOnlySpan<TileRange> GetTileRanges(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            return ReadOnlySpan<TileRange>.Empty;

        int columnIndex = GetColumnIndex(globalX, globalY);
        TileRange[]? cached = _rangeCache[columnIndex];

        if (cached != null)
            return cached;

        int totalHeightUnits = LayerCount * LayerHeightUnits;

        // The generated terrain is a continuous, single-material column.
        // Keep that common case O(1): don't scan all 500 Z levels on every
        // first query for a tile reached by vision or a projectile.
        if (_simpleFillColumns[columnIndex])
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
                LayerHeightUnits,
                0,
                LayerCount);
            ushort materialId = _columnMaterialIds[columnIndex];

            if (filledLayers == 0 || materialId == 0)
            {
                cached = new[]
                {
                    new TileRange
                    {
                        StartZ = 0,
                        EndZ = (ushort)totalHeightUnits,
                        MaterialId = 0,
                        State = StateEmpty
                    }
                };
            }
            else if (filledLayers >= LayerCount)
            {
                cached = new[]
                {
                    new TileRange
                    {
                        StartZ = 0,
                        EndZ = (ushort)totalHeightUnits,
                        MaterialId = materialId,
                        State = StateSolid
                    }
                };
            }
            else
            {
                cached = new TileRange[2];
                cached[0] = new TileRange
                {
                    StartZ = 0,
                    EndZ = (ushort)(filledLayers * LayerHeightUnits),
                    MaterialId = materialId,
                    State = StateSolid
                };
                cached[1] = new TileRange
                {
                    StartZ = (ushort)(filledLayers * LayerHeightUnits),
                    EndZ = (ushort)totalHeightUnits,
                    MaterialId = 0,
                    State = StateEmpty
                };
            }

            _rangeCache[columnIndex] = cached;
            return cached;
        }

        // Complex editable columns may contain several materials and air gaps.
        // Use stack memory for the scan, then retain only the exact number of
        // ranges. The former List<TileRange>(LayerCount) allocated ~4 KB of
        // garbage for every previously unseen tile, even for just two ranges.
        Span<TileRange> ranges = stackalloc TileRange[LayerCount];
        int rangeCount = 0;
        int runStart = 0;
        ushort runMaterial =
            _layers[0].GetMaterialIdAtIndex(columnIndex);

        for (int z = 1; z <= LayerCount; z++)
        {
            if (z < LayerCount &&
                _layers[z].GetMaterialIdAtIndex(columnIndex) == runMaterial)
            {
                continue;
            }

            ranges[rangeCount++] = new TileRange
            {
                StartZ = (ushort)(runStart * LayerHeightUnits),
                EndZ = (ushort)(z * LayerHeightUnits),
                MaterialId = runMaterial,
                State = runMaterial == 0
                    ? StateEmpty
                    : StateSolid
            };

            if (z < LayerCount)
            {
                runStart = z;
                runMaterial =
                    _layers[z].GetMaterialIdAtIndex(columnIndex);
            }
        }

        cached = new TileRange[rangeCount];
        ranges[..rangeCount].CopyTo(cached);
        _rangeCache[columnIndex] = cached;
        return cached;
    }

    public void SetRanges(
        int globalX,
        int globalY,
        ReadOnlySpan<TileRange> ranges)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        GetOrCreateRegion(
            globalX / TerrainRegion.TilesPerSide,
            globalY / TerrainRegion.TilesPerSide);

        int columnIndex = GetColumnIndex(globalX, globalY);
        ClearColumn(globalX, globalY);

        int regionX = globalX / TerrainRegion.TilesPerSide;
        int regionY = globalY / TerrainRegion.TilesPerSide;
        int localIndex =
            globalX % TerrainRegion.TilesPerSide +
            (globalY % TerrainRegion.TilesPerSide) *
            TerrainRegion.TilesPerSide;

        int surfaceHeight = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            ref readonly TileRange range = ref ranges[i];

            if (range.StartZ > range.EndZ)
                throw new ArgumentException(
                    "StartZ не может быть больше EndZ.",
                    nameof(ranges));

            int startLayer =
                Math.Clamp(
                    range.StartZ / LayerHeightUnits,
                    0,
                    LayerCount);

            int endLayer =
                Math.Clamp(
                    (range.EndZ + LayerHeightUnits - 1) /
                    LayerHeightUnits,
                    0,
                    LayerCount);

            ushort materialId =
                range.State == StateSolid
                    ? range.MaterialId
                    : (ushort)0;

            for (int z = startLayer; z < endLayer; z++)
            {
                _layers[z].GetRegion(regionX, regionY)!
                    .SetMaterialIdAtIndex(
                        localIndex,
                        materialId);
            }

            if (range.State == StateSolid &&
                materialId != 0)
            {
                surfaceHeight = Math.Max(
                    surfaceHeight,
                    Math.Min(
                        range.EndZ,
                        LayerCount * LayerHeightUnits));
            }
        }

        _surfaceHeights[columnIndex] = (ushort)surfaceHeight;
        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
        MarkTerrainChanged(globalX, globalY);
    }

    public void ClearTileRanges(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            return;

        int columnIndex = GetColumnIndex(globalX, globalY);
        ClearColumn(globalX, globalY);
        MarkTerrainChanged(globalX, globalY);
    }

    public bool TryAddRange(
        int globalX,
        int globalY,
        ushort startZ,
        ushort endZ,
        ushort materialId,
        byte state)
    {
        if (!IsInside(globalX, globalY))
            return false;

        if (startZ > endZ)
            throw new ArgumentException(
                "StartZ не может быть больше EndZ.");

        GetOrCreateRegion(
            globalX / TerrainRegion.TilesPerSide,
            globalY / TerrainRegion.TilesPerSide);

        int columnIndex = GetColumnIndex(globalX, globalY);
        int regionX = globalX / TerrainRegion.TilesPerSide;
        int regionY = globalY / TerrainRegion.TilesPerSide;
        int localIndex =
            globalX % TerrainRegion.TilesPerSide +
            (globalY % TerrainRegion.TilesPerSide) *
            TerrainRegion.TilesPerSide;

        MaterializeSimpleFillColumn(globalX, globalY, columnIndex);

        int startLayer =
            Math.Clamp(
                startZ / LayerHeightUnits,
                0,
                LayerCount);

        int endLayer =
            Math.Clamp(
                (endZ + LayerHeightUnits - 1) /
                LayerHeightUnits,
                0,
                LayerCount);

        ushort value =
            state == StateSolid
                ? materialId
                : (ushort)0;

        for (int z = startLayer; z < endLayer; z++)
        {
            _layers[z].GetRegion(regionX, regionY)!
                .SetMaterialIdAtIndex(
                    localIndex,
                    value);
        }

        if (state == StateSolid && value != 0)
        {
            int rangeEnd =
                Math.Min(
                    endZ,
                    LayerCount * LayerHeightUnits);

            if (rangeEnd > _surfaceHeights[columnIndex])
            {
                _surfaceHeights[columnIndex] =
                    (ushort)rangeEnd;
            }
        }
        else if (_surfaceHeights[columnIndex] > startZ)
        {
            RecalculateSurfaceHeight(columnIndex);
        }

        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
        MarkTerrainChanged(globalX, globalY);
        return true;
    }

    // Removes a vertical voxel interval from one XY column.
    // startLayer is inclusive; endLayerExclusive is exclusive.
    public void ClearVoxelRange(
        int globalX,
        int globalY,
        int startLayer,
        int endLayerExclusive)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        int firstLayer = Math.Max(0, startLayer);
        int lastLayerExclusive = Math.Min(LayerCount, endLayerExclusive);
        if (firstLayer >= lastLayerExclusive)
            return;

        int columnIndex = GetColumnIndex(globalX, globalY);
        MaterializeSimpleFillColumn(globalX, globalY, columnIndex);

        int regionX = globalX / TerrainRegion.TilesPerSide;
        int regionY = globalY / TerrainRegion.TilesPerSide;
        int localIndex =
            globalX % TerrainRegion.TilesPerSide +
            (globalY % TerrainRegion.TilesPerSide) * TerrainRegion.TilesPerSide;

        for (int z = firstLayer; z < lastLayerExclusive; z++)
        {
            _layers[z].GetRegion(regionX, regionY)!
                .SetMaterialIdAtIndex(localIndex, 0);
        }

        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
        RecalculateSurfaceHeight(columnIndex);
        MarkTerrainChanged(globalX, globalY);
    }

    public void SetSolidHeight(
        int globalX,
        int globalY,
        ushort height,
        ushort materialId)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        GetOrCreateRegion(
            globalX / TerrainRegion.TilesPerSide,
            globalY / TerrainRegion.TilesPerSide);

        int columnIndex = GetColumnIndex(globalX, globalY);
        int regionX = globalX / TerrainRegion.TilesPerSide;
        int regionY = globalY / TerrainRegion.TilesPerSide;
        int localIndex =
            globalX % TerrainRegion.TilesPerSide +
            (globalY % TerrainRegion.TilesPerSide) *
            TerrainRegion.TilesPerSide;

        int clampedHeight =
            Math.Min(
                height,
                LayerCount * LayerHeightUnits);

        bool wasSimpleFill = _simpleFillColumns[columnIndex];
        int previousLayers =
            (_surfaceHeights[columnIndex] +
             LayerHeightUnits - 1) /
            LayerHeightUnits;

        int filledLayers =
            clampedHeight > 0 && materialId != 0
                ? (clampedHeight + LayerHeightUnits - 1) /
                  LayerHeightUnits
                : 0;

        _rangeCache[columnIndex] = null;

        // Only touch the old/new occupied height span.
        // This avoids clearing and refilling unused vertical layers
        // whenever a terrain generator replaces its height.
        if (!wasSimpleFill)
        {
            int layersToUpdate =
                Math.Max(
                    previousLayers,
                    filledLayers);

            for (int z = 0; z < layersToUpdate; z++)
            {
                _layers[z].GetRegion(regionX, regionY)!
                    .SetMaterialIdAtIndex(
                        localIndex,
                        z < filledLayers
                            ? materialId
                            : (ushort)0);
            }
        }

        _surfaceHeights[columnIndex] =
            (ushort)(filledLayers > 0 ? clampedHeight : 0);
        _simpleFillColumns[columnIndex] = true;
        _columnMaterialIds[columnIndex] = filledLayers > 0 ? materialId : (ushort)0;

        MarkTerrainChanged(globalX, globalY);
    }

    // ============================================================
    // SURFACE HEIGHT CACHE
    // Height remains available to movement / ballistic systems.
    // One Z layer represents 0.1 m; stored height units remain decimeters.
    // ============================================================

    public ushort GetSurfaceHeightUnits(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            return 0;

        return _surfaceHeights[
            GetColumnIndex(globalX, globalY)];
    }

    public float GetSurfaceHeight(
        int globalX,
        int globalY)
    {
        return GetSurfaceHeightUnits(globalX, globalY) * 0.1f;
    }

    private void MaterializeSimpleFillColumn(
        int globalX,
        int globalY,
        int columnIndex)
    {
        if (!_simpleFillColumns[columnIndex])
            return;

        int filledLayers = Math.Clamp(
            (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
            LayerHeightUnits,
            0,
            LayerCount);
        ushort materialId = _columnMaterialIds[columnIndex];

        if (materialId != 0 && filledLayers > 0)
        {
            int regionX = globalX / TerrainRegion.TilesPerSide;
            int regionY = globalY / TerrainRegion.TilesPerSide;
            int localIndex =
                globalX % TerrainRegion.TilesPerSide +
                (globalY % TerrainRegion.TilesPerSide) *
                TerrainRegion.TilesPerSide;

            for (int z = 0; z < filledLayers; z++)
            {
                _layers[z].GetRegion(regionX, regionY)!
                    .SetMaterialIdAtIndex(localIndex, materialId);
            }
        }

        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
    }

    private void ClearColumn(
        int globalX,
        int globalY)
    {
        int regionX =
            globalX / TerrainRegion.TilesPerSide;

        int regionY =
            globalY / TerrainRegion.TilesPerSide;

        int localIndex =
            globalX % TerrainRegion.TilesPerSide +
            (globalY % TerrainRegion.TilesPerSide) *
            TerrainRegion.TilesPerSide;

        for (int z = 0; z < LayerCount; z++)
        {
            _layers[z].GetRegion(regionX, regionY)!
                .SetMaterialIdAtIndex(
                    localIndex,
                    0);
        }

        int columnIndex =
            GetColumnIndex(globalX, globalY);

        _surfaceHeights[columnIndex] = 0;
        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = true;
        _columnMaterialIds[columnIndex] = 0;
    }

    private void RecalculateSurfaceHeight(
        int columnIndex)
    {
        int globalX =
            columnIndex % TileWidth;

        int globalY =
            columnIndex / TileWidth;

        int regionX =
            globalX / TerrainRegion.TilesPerSide;

        int regionY =
            globalY / TerrainRegion.TilesPerSide;

        int localIndex =
            globalX % TerrainRegion.TilesPerSide +
            (globalY % TerrainRegion.TilesPerSide) *
            TerrainRegion.TilesPerSide;

        for (int z = LayerCount - 1; z >= 0; z--)
        {
            if (_layers[z].GetRegion(regionX, regionY)!
                    .GetMaterialIdAtIndex(localIndex) == 0)
            {
                continue;
            }

            _surfaceHeights[columnIndex] =
                (ushort)((z + 1) * LayerHeightUnits);

            return;
        }

        _surfaceHeights[columnIndex] = 0;
    }

    private int GetColumnIndex(
        int x,
        int y)
    {
        return x + y * TileWidth;
    }

    private bool IsInside(
        int x,
        int y)
    {
        return
            x >= 0 &&
            y >= 0 &&
            x < TileWidth &&
            y < TileHeight;
    }
}
