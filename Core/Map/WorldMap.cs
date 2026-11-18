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

    public ReadOnlySpan<WorldRegion> WorldRegions => _worldRegions;

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
    public BlockTerrain Blocks { get; }
    public bool UsesDiscreteBlockTerrain { get; private set; }

    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly TerrainLayer[] _layers;
    private readonly WorldRegion[] _worldRegions;
    private readonly ushort[] _surfaceHeights;
    private readonly ushort[] _volumeSurfaceHeights;
    private readonly bool[] _navigationBlocked;
    private readonly bool[] _simpleFillColumns;
    private readonly ushort[] _columnMaterialIds;
    private readonly TileRange[]?[] _rangeCache;

    public WorldMap(
        int regionsX = 4,
        int regionsY = 4,
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

        _worldRegions = new WorldRegion[checked(regionsX * regionsY)];
        for (int regionY = 0; regionY < regionsY; regionY++)
        {
            for (int regionX = 0; regionX < regionsX; regionX++)
            {
                int index = regionX + regionY * regionsX;
                _worldRegions[index] =
                    new WorldRegion(regionX, regionY, _layers);
            }
        }

        _surfaceHeights = new ushort[columnCount];
        _volumeSurfaceHeights = new ushort[columnCount];
        Blocks = new BlockTerrain(width, height, layerCount);
        _navigationBlocked = new bool[columnCount];
        _simpleFillColumns = new bool[columnCount];
        Array.Fill(_simpleFillColumns, true);
        _columnMaterialIds = new ushort[columnCount];
        _rangeCache = new TileRange[]?[columnCount];

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

        // Terrain smoothing and contour cells sample across chunk boundaries.
        // Include adjacent region revisions so their meshes cannot go stale
        // when an edit occurs on the other side of a shared edge.
        long version = 17;
        unchecked
        {
            for (int y = Math.Max(0, regionY - 1);
                 y <= Math.Min(RegionsY - 1, regionY + 1);
                 y++)
            {
                for (int x = Math.Max(0, regionX - 1);
                     x <= Math.Min(RegionsX - 1, regionX + 1);
                     x++)
                {
                    version = version * 31 +
                        _worldRegions[x + y * RegionsX].TerrainVersion;
                }
            }
        }

        return version;
    }

    private void MarkTerrainChanged(int x, int y)
    {
        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        _worldRegions[regionX + regionY * RegionsX].MarkTerrainChanged();

        if (UsesDiscreteBlockTerrain)
        {
            int topLayer = FindTopOccupiedLayer(x, y, LayerCount - 1);
            int level = topLayer < 0
                ? 0
                : (topLayer + HeightUnitsPerMeter) / HeightUnitsPerMeter;
            ushort topMaterial = topLayer >= 0
                ? GetMaterialId(x, y, topLayer)
                : (ushort)0;
            Blocks.SetSurfaceLevel(x, y, level, topMaterial);
        }

        TerrainVersion++;
        TerrainGeometryVersion++;
    }

    public void EnableDiscreteBlockTerrain()
    {
        if (UsesDiscreteBlockTerrain)
            return;

        Blocks.Volumes.Clear();
        Blocks.ClearTransitions();
        Array.Clear(_volumeSurfaceHeights);
        UsesDiscreteBlockTerrain = true;

        // Uniform regions convert in bulk. Only regions with local tile edits
        // fall back to per-column conversion.
        for (int i = 0; i < _worldRegions.Length; i++)
        {
            WorldRegion region = _worldRegions[i];
            if (region.HasUniformSurface)
            {
                int levels = (int)MathF.Round(
                    region.UniformSurfaceHeightUnits /
                    (float)HeightUnitsPerMeter,
                    MidpointRounding.AwayFromZero);
                ushort heightUnits = checked((ushort)(
                    levels * HeightUnitsPerMeter));
                ushort materialId = heightUnits > 0
                    ? region.UniformSurfaceMaterialId
                    : (ushort)0;
                int filledLayers = heightUnits == 0
                    ? 0
                    : Math.Clamp(
                        (heightUnits + LayerHeightUnits - 1) / LayerHeightUnits,
                        0,
                        LayerCount);

                for (int z = 0; z < LayerCount; z++)
                {
                    region.GetTerrainSlice(z).SetUniformMaterial(
                        z < filledLayers ? materialId : (ushort)0);
                }

                for (int row = 0; row < region.TileHeight; row++)
                {
                    int index = GetColumnIndex(
                        region.TileOriginX,
                        region.TileOriginY + row);
                    Array.Fill(_surfaceHeights, heightUnits, index, region.TileWidth);
                    Array.Fill(_columnMaterialIds, materialId, index, region.TileWidth);
                    Array.Fill(_simpleFillColumns, true, index, region.TileWidth);
                    Array.Clear(_rangeCache, index, region.TileWidth);
                }

                Blocks.SetSurfaceRegion(
                    region.TileOriginX,
                    region.TileOriginY,
                    region.TileWidth,
                    region.TileHeight,
                    levels,
                    materialId);

                region.SetUniformSurface(heightUnits, materialId);
                continue;
            }

            for (int localY = 0; localY < region.TileHeight; localY++)
            {
                int globalY = region.TileOriginY + localY;
                for (int localX = 0; localX < region.TileWidth; localX++)
                {
                    int globalX = region.TileOriginX + localX;
                    int columnIndex = GetColumnIndex(globalX, globalY);
                    ushort oldHeight = _surfaceHeights[columnIndex];
                    ushort material = GetMaterialId(globalX, globalY, 0);
                    int levels = (int)MathF.Round(
                        oldHeight / (float)HeightUnitsPerMeter,
                        MidpointRounding.AwayFromZero);

                    SetSolidHeight(
                        globalX,
                        globalY,
                        checked((ushort)(levels * HeightUnitsPerMeter)),
                        material);
                }
            }
        }

        NavigationVersion++;
        TerrainVersion++;
        TerrainGeometryVersion++;
    }

    public void DisableDiscreteBlockTerrain()
    {
        if (!UsesDiscreteBlockTerrain)
            return;

        UsesDiscreteBlockTerrain = false;
        Blocks.Volumes.Clear();
        Blocks.ClearTransitions();
        Array.Clear(_volumeSurfaceHeights);
        NavigationVersion++;
        TerrainVersion++;
        TerrainGeometryVersion++;
        for (int i = 0; i < _worldRegions.Length; i++)
            _worldRegions[i].MarkGeometryChanged();
    }

    public void SetBlockStack(int x, int y, int heightInMeters, ushort materialId)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (heightInMeters < 0 ||
            heightInMeters > LayerCount / HeightUnitsPerMeter)
            throw new ArgumentOutOfRangeException(nameof(heightInMeters));

        SetSolidHeight(
            x, y,
            checked((ushort)(heightInMeters * HeightUnitsPerMeter)),
            materialId);
    }

    /// <summary>
    /// Places or clears one large cuboid in the sparse volume tree.
    /// X/Y and width/depth use one-metre map cells. Z and height use metres.
    /// Material ID zero cuts an empty cuboid; editing a smaller part later
    /// splits only the affected octree nodes.
    /// </summary>
    public void SetBlockVolume(
        int x,
        int y,
        int zLevel,
        int width,
        int depth,
        int heightInMeters,
        ushort materialId)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (x < 0 || y < 0 || width <= 0 || depth <= 0 ||
            (long)x + width > TileWidth ||
            (long)y + depth > TileHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (zLevel < 0 || heightInMeters <= 0 ||
            ((long)zLevel + heightInMeters) * HeightUnitsPerMeter > LayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(heightInMeters));
        }

        int firstLayer = checked(zLevel * HeightUnitsPerMeter);
        int heightLayers = checked(heightInMeters * HeightUnitsPerMeter);

        // A destructive cut must preserve any simple-fill terrain underneath
        // the volume before the tree masks part of it.
        if (materialId == 0)
        {
            for (int localY = 0; localY < depth; localY++)
            {
                for (int localX = 0; localX < width; localX++)
                {
                    int globalX = x + localX;
                    int globalY = y + localY;
                    int columnIndex = GetColumnIndex(globalX, globalY);
                    MaterializeSimpleFillColumn(globalX, globalY, columnIndex);
                }
            }
        }

        Blocks.Volumes.SetVolume(
            x, y, firstLayer,
            width, depth, heightLayers,
            materialId);

        int topLayer = firstLayer + heightLayers - 1;
        ushort volumeHeight = checked((ushort)((topLayer + 1) * LayerHeightUnits));

        for (int localY = 0; localY < depth; localY++)
        {
            for (int localX = 0; localX < width; localX++)
            {
                int globalX = x + localX;
                int globalY = y + localY;
                int columnIndex = GetColumnIndex(globalX, globalY);
                _rangeCache[columnIndex] = null;

                if (materialId == 0)
                {
                    _simpleFillColumns[columnIndex] = false;
                    _columnMaterialIds[columnIndex] = 0;
                    RecalculateSurfaceHeight(columnIndex);
                }
                else if (!Blocks.Volumes.HasEmptyOverridesInColumn(globalX, globalY))
                {
                    _volumeSurfaceHeights[columnIndex] =
                        Math.Max(_volumeSurfaceHeights[columnIndex], volumeHeight);
                }
                else
                {
                    RecalculateSurfaceHeight(columnIndex);
                }

                MarkTerrainChanged(globalX, globalY);
            }
        }

        NavigationVersion++;
    }

    /// <summary>
    /// Packs shared lower terrain into large macro volumes. Regions that
    /// cannot share a common base are split recursively down to the requested
    /// block size; finer height differences stay in the existing compact
    /// column backing instead of creating one object per tile.
    /// </summary>
    public int CompactHierarchicalBlockVolumes(int minimumBlockSize = 32)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (minimumBlockSize < 2 ||
            (minimumBlockSize & (minimumBlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumBlockSize));
        }

        Blocks.Volumes.Clear();
        Array.Clear(_volumeSurfaceHeights);

        int rootSize = 1;
        int requiredSize = Math.Max(TileWidth, TileHeight);
        while (rootSize < requiredSize)
            rootSize = checked(rootSize * 2);

        int volumeCount = CompactHierarchicalRegion(
            0, 0, rootSize, minimumBlockSize, 0);

        if (volumeCount > 0)
        {
            TerrainVersion++;
            TerrainGeometryVersion++;
            for (int i = 0; i < _worldRegions.Length; i++)
                _worldRegions[i].MarkGeometryChanged();
        }

        return volumeCount;
    }

    public int CompactUniformBlockVolumes(int minimumBlockSize = 32) =>
        CompactHierarchicalBlockVolumes(minimumBlockSize);

    private int CompactHierarchicalRegion(
        int x,
        int y,
        int size,
        int minimumBlockSize,
        int baseLayer)
    {
        if (x >= TileWidth || y >= TileHeight || size < minimumBlockSize)
            return 0;

        if (x + size <= TileWidth &&
            y + size <= TileHeight &&
            TryGetBlockRegionSummary(
                x, y, size, baseLayer,
                out ushort materialId,
                out int minimumRemaining,
                out int maximumRemaining))
        {
            int volumeCount = 0;
            if (minimumRemaining > 0)
            {
                // Store the shared lower part once for this whole region.
                // Higher, varying surfaces continue to use the fine backing.
                Blocks.Volumes.SetVolume(
                    x, y, baseLayer,
                    size, size, minimumRemaining,
                    materialId);
                volumeCount++;
                baseLayer += minimumRemaining;
            }

            if (maximumRemaining <= minimumRemaining ||
                size <= minimumBlockSize)
            {
                return volumeCount;
            }

            int half = size / 2;
            volumeCount += CompactHierarchicalRegion(
                x, y, half, minimumBlockSize, baseLayer);
            volumeCount += CompactHierarchicalRegion(
                x + half, y, half, minimumBlockSize, baseLayer);
            volumeCount += CompactHierarchicalRegion(
                x, y + half, half, minimumBlockSize, baseLayer);
            volumeCount += CompactHierarchicalRegion(
                x + half, y + half, half, minimumBlockSize, baseLayer);
            return volumeCount;
        }

        int childSize = size / 2;
        return
            CompactHierarchicalRegion(
                x, y, childSize, minimumBlockSize, baseLayer) +
            CompactHierarchicalRegion(
                x + childSize, y, childSize, minimumBlockSize, baseLayer) +
            CompactHierarchicalRegion(
                x, y + childSize, childSize, minimumBlockSize, baseLayer) +
            CompactHierarchicalRegion(
                x + childSize, y + childSize,
                childSize, minimumBlockSize, baseLayer);
    }

    private bool TryGetBlockRegionSummary(
        int x,
        int y,
        int size,
        int baseLayer,
        out ushort materialId,
        out int minimumRemaining,
        out int maximumRemaining)
    {
        materialId = 0;
        minimumRemaining = int.MaxValue;
        maximumRemaining = 0;

        for (int localY = 0; localY < size; localY++)
        {
            int row = GetColumnIndex(x, y + localY);
            for (int localX = 0; localX < size; localX++)
            {
                int index = row + localX;
                if (!_simpleFillColumns[index])
                    return false;

                int totalLayers = Math.Clamp(
                    (_surfaceHeights[index] + LayerHeightUnits - 1) /
                    LayerHeightUnits,
                    0,
                    LayerCount);
                int remaining = Math.Max(0, totalLayers - baseLayer);

                if (remaining > 0)
                {
                    ushort columnMaterial = _columnMaterialIds[index];
                    if (columnMaterial == 0)
                        return false;

                    if (materialId == 0)
                        materialId = columnMaterial;
                    else if (materialId != columnMaterial)
                        return false;
                }

                minimumRemaining = Math.Min(minimumRemaining, remaining);
                maximumRemaining = Math.Max(maximumRemaining, remaining);
            }
        }

        if (minimumRemaining == int.MaxValue)
            minimumRemaining = 0;

        return true;
    }

    /// <summary>
    /// Places one independent 1 x 1 x 1 m block at a discrete block Z level.
    /// WorldMap keeps its existing 0.1 m voxel backing; ten layers represent
    /// the vertical volume of one metre block.
    /// </summary>
    public void SetBlock(
        int x,
        int y,
        int zLevel,
        ushort materialId)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (zLevel < 0 || (zLevel + 1) * HeightUnitsPerMeter > LayerCount)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        GetOrCreateRegion(
            x / TerrainRegion.TilesPerSide,
            y / TerrainRegion.TilesPerSide);

        int columnIndex = GetColumnIndex(x, y);
        MaterializeSimpleFillColumn(x, y, columnIndex);
        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        int localIndex =
            x % TerrainRegion.TilesPerSide +
            (y % TerrainRegion.TilesPerSide) * TerrainRegion.TilesPerSide;
        int firstLayer = zLevel * HeightUnitsPerMeter;
        Blocks.Volumes.SetVolume(
            x, y, firstLayer,
            1, 1, HeightUnitsPerMeter,
            materialId);

        for (int z = firstLayer; z < firstLayer + HeightUnitsPerMeter; z++)
        {
            _layers[z].GetRegion(regionX, regionY)!
                .SetMaterialIdAtIndex(localIndex, materialId);
        }

        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
        RecalculateSurfaceHeight(columnIndex);
        MarkTerrainChanged(x, y);
    }

    /// <summary>Removes one 1 m block without clearing blocks above or below it.</summary>
    public void RemoveBlock(int x, int y, int zLevel)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));
        if (zLevel < 0 || (zLevel + 1) * HeightUnitsPerMeter > LayerCount)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        int columnIndex = GetColumnIndex(x, y);
        MaterializeSimpleFillColumn(x, y, columnIndex);
        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        int localIndex =
            x % TerrainRegion.TilesPerSide +
            (y % TerrainRegion.TilesPerSide) * TerrainRegion.TilesPerSide;
        int firstLayer = zLevel * HeightUnitsPerMeter;
        Blocks.Volumes.SetVolume(
            x, y, firstLayer,
            1, 1, HeightUnitsPerMeter,
            0);

        for (int z = firstLayer; z < firstLayer + HeightUnitsPerMeter; z++)
        {
            _layers[z].GetRegion(regionX, regionY)!
                .SetMaterialIdAtIndex(localIndex, 0);
        }

        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
        RecalculateSurfaceHeight(columnIndex);
        MarkTerrainChanged(x, y);
    }

    public bool IsBlockOccupied(int x, int y, int zLevel)
    {
        if (!UsesDiscreteBlockTerrain ||
            !IsInside(x, y) ||
            zLevel < 0 ||
            (zLevel + 1) * HeightUnitsPerMeter > LayerCount)
        {
            return false;
        }

        return GetMaterialId(x, y, zLevel * HeightUnitsPerMeter) != 0;
    }

    public void SetBlockTransition(
        int x, int y, BlockTransitionType type, BlockDirection direction)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));

        if (type != BlockTransitionType.None)
        {
            int level = Blocks.GetSurfaceLevel(x, y);
            int dx = direction == BlockDirection.East ? 1 :
                direction == BlockDirection.West ? -1 : 0;
            int dy = direction == BlockDirection.South ? 1 :
                direction == BlockDirection.North ? -1 : 0;
            int nextLevel = Blocks.GetSurfaceLevel(x + dx, y + dy);
            int rise = nextLevel - level;

            if (level <= 0 ||
                (type == BlockTransitionType.Ramp && rise != 1) ||
                (type == BlockTransitionType.Stairs && rise is < 1 or > 3))
            {
                throw new InvalidOperationException(
                    "A ramp must connect adjacent solid surfaces exactly one metre apart. " +
                    "Stairs may connect surfaces one to three metres apart.");
            }
        }

        Blocks.SetTransition(x, y, type, direction);
        NavigationVersion++;
        MarkTerrainChanged(x, y);
    }

    public void SetRamp(int x, int y, BlockDirection direction) =>
        SetBlockTransition(x, y, BlockTransitionType.Ramp, direction);

    public void RemoveRamp(int x, int y) =>
        SetBlockTransition(x, y, BlockTransitionType.None, BlockDirection.North);

    public void ClearBlockTransitions()
    {
        if (!UsesDiscreteBlockTerrain)
            return;

        Blocks.ClearTransitions();
        NavigationVersion++;
        TerrainVersion++;
        TerrainGeometryVersion++;
        for (int i = 0; i < _worldRegions.Length; i++)
            _worldRegions[i].MarkGeometryChanged();
    }

    public float GetSurfaceHeightAt(float x, float y)
    {
        if (UsesDiscreteBlockTerrain)
            return Blocks.SampleSurfaceHeight(x, y);

        int x0 = Math.Clamp((int)MathF.Floor(x), 0, MaxTileX);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, MaxTileY);
        int x1 = Math.Min(x0 + 1, MaxTileX);
        int y1 = Math.Min(y0 + 1, MaxTileY);
        float tx = Math.Clamp(x - x0, 0f, 1f);
        float ty = Math.Clamp(y - y0, 0f, 1f);
        float top = GetSurfaceHeight(x0, y0) +
            (GetSurfaceHeight(x1, y0) - GetSurfaceHeight(x0, y0)) * tx;
        float bottom = GetSurfaceHeight(x0, y1) +
            (GetSurfaceHeight(x1, y1) - GetSurfaceHeight(x0, y1)) * tx;
        return top + (bottom - top) * ty;
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

        if (Blocks.Volumes.TryGetMaterialId(x, y, z, out ushort volumeMaterial))
            return volumeMaterial;

        WorldRegion? worldRegion = GetWorldRegionAtTile(x, y);
        if (worldRegion != null && worldRegion.HasUniformSurface)
        {
            int filledLayers = Math.Clamp(
                (worldRegion.UniformSurfaceHeightUnits +
                 LayerHeightUnits - 1) / LayerHeightUnits,
                0,
                LayerCount);

            return z < filledLayers
                ? worldRegion.UniformSurfaceMaterialId
                : (ushort)0;
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
        if (Blocks.Volumes.TryGetMaterialId(x, y, z, out _))
            Blocks.Volumes.SetVoxel(x, y, z, materialId);

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
    // Uniform macro volumes resolve through the octree without scanning Z.
    public int FindTopOccupiedLayer(int x, int y, int maxLayer)
    {
        if (!IsInside(x, y) || maxLayer < 0)
            return -1;

        int columnIndex = GetColumnIndex(x, y);
        int lastLayer = Math.Min(maxLayer, LayerCount - 1);

        WorldRegion? worldRegion = GetWorldRegionAtTile(x, y);
        int baseTop;
        if (worldRegion != null && worldRegion.HasUniformSurface)
        {
            int filledLayers = Math.Clamp(
                (worldRegion.UniformSurfaceHeightUnits +
                 LayerHeightUnits - 1) / LayerHeightUnits,
                0,
                LayerCount);
            baseTop = worldRegion.UniformSurfaceMaterialId == 0
                ? -1
                : Math.Min(filledLayers - 1, lastLayer);
        }
        else
        {
            baseTop = FindBaseTopOccupiedLayer(x, y, columnIndex, lastLayer);
        }

        if (!Blocks.Volumes.HasOverridesInColumn(x, y))
            return baseTop;

        int volumeTop = Blocks.Volumes.FindHighestSolidLayerInColumn(
            x, y, lastLayer);

        if (!Blocks.Volumes.HasEmptyOverridesInColumn(x, y))
            return Math.Max(baseTop, volumeTop);

        // Only columns that have been carved need a precise merged lookup.
        for (int z = Math.Max(baseTop, volumeTop); z >= 0; z--)
        {
            if (GetMaterialId(x, y, z) != 0)
                return z;
        }

        return -1;
    }

    private int FindBaseTopOccupiedLayer(
        int x,
        int y,
        int columnIndex,
        int lastLayer)
    {
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
        if (Blocks.Volumes.HasEmptyOverridesInColumn(x, y))
        {
            for (int z = firstLayer; z <= lastLayer; z++)
            {
                if (GetMaterialId(x, y, z) != 0)
                    return true;
            }
            return false;
        }

        if (Blocks.Volumes.HasSolidInColumnBetween(
                x, y, firstLayer, lastLayer))
        {
            return true;
        }

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

        WorldRegion? region = GetWorldRegionAtTile(x, y);
        ushort height = region != null && region.HasUniformSurface
            ? region.UniformSurfaceHeightUnits
            : _surfaceHeights[GetColumnIndex(x, y)];

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
        bool allRegionsUniform = true;
        ushort highestUniformHeight = 0;
        for (int i = 0; i < _worldRegions.Length; i++)
        {
            WorldRegion region = _worldRegions[i];
            if (!region.HasUniformSurface)
            {
                allRegionsUniform = false;
                break;
            }

            highestUniformHeight = Math.Max(
                highestUniformHeight,
                region.UniformSurfaceHeightUnits);
        }

        if (allRegionsUniform)
        {
            if (highestUniformHeight == 0)
                return -1;

            int highestLayer =
                (highestUniformHeight + LayerHeightUnits - 1) /
                LayerHeightUnits - 1;
            return Math.Min(LayerCount - 1, highestLayer);
        }

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
    // Logical world regions
    // A WorldRegion spans the complete vertical stack of one XY tile area.
    // ============================================================

    public WorldRegion? GetWorldRegion(
        int regionX,
        int regionY)
    {
        if (regionX < 0 || regionX >= RegionsX ||
            regionY < 0 || regionY >= RegionsY)
        {
            return null;
        }

        return _worldRegions[regionX + regionY * RegionsX];
    }

    public WorldRegion? GetWorldRegionAtTile(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            return null;

        return GetWorldRegion(
            globalX / TerrainRegion.TilesPerSide,
            globalY / TerrainRegion.TilesPerSide);
    }

    // ============================================================
    // Compatibility access to the Z=0 TerrainRegion.
    // Tile/navigation metadata is shared between Z levels.
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
        if (Blocks.Volumes.HasOverridesInColumn(globalX, globalY))
            Blocks.Volumes.ClearVolume(globalX, globalY, 0, 1, 1, LayerCount);
        _volumeSurfaceHeights[columnIndex] = 0;
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

    public void SetRegionSolidHeight(
        int regionX,
        int regionY,
        ushort height,
        ushort materialId)
    {
        if (regionX < 0 || regionX >= RegionsX ||
            regionY < 0 || regionY >= RegionsY)
        {
            throw new IndexOutOfRangeException();
        }

        WorldRegion region = _worldRegions[regionX + regionY * RegionsX];
        int clampedHeight = Math.Min(
            (int)height,
            LayerCount * LayerHeightUnits);

        if (materialId == 0)
            clampedHeight = 0;

        if (UsesDiscreteBlockTerrain)
        {
            clampedHeight = Math.Clamp(
                (int)MathF.Round(
                    clampedHeight / (float)HeightUnitsPerMeter,
                    MidpointRounding.AwayFromZero) * HeightUnitsPerMeter,
                0,
                LayerCount * LayerHeightUnits);
        }

        ushort normalizedHeight = checked((ushort)clampedHeight);
        ushort normalizedMaterial = clampedHeight > 0
            ? materialId
            : (ushort)0;

        if (region.HasUniformSurface &&
            region.UniformSurfaceHeightUnits == normalizedHeight &&
            region.UniformSurfaceMaterialId == normalizedMaterial)
        {
            return;
        }

        if (Blocks.Volumes.HasOverrides)
        {
            Blocks.Volumes.ClearVolume(
                region.TileOriginX,
                region.TileOriginY,
                0,
                region.TileWidth,
                region.TileHeight,
                LayerCount);
        }

        int filledLayers = clampedHeight == 0
            ? 0
            : Math.Clamp(
                (clampedHeight + LayerHeightUnits - 1) / LayerHeightUnits,
                0,
                LayerCount);

        for (int z = 0; z < LayerCount; z++)
        {
            region.GetTerrainSlice(z).SetUniformMaterial(
                z < filledLayers ? normalizedMaterial : (ushort)0);
        }

        for (int row = 0; row < region.TileHeight; row++)
        {
            int index = GetColumnIndex(
                region.TileOriginX,
                region.TileOriginY + row);

            Array.Fill(_surfaceHeights, normalizedHeight, index, region.TileWidth);
            Array.Clear(_volumeSurfaceHeights, index, region.TileWidth);
            Array.Fill(_columnMaterialIds, normalizedMaterial, index, region.TileWidth);
            Array.Fill(_simpleFillColumns, true, index, region.TileWidth);
            Array.Clear(_rangeCache, index, region.TileWidth);
        }

        if (UsesDiscreteBlockTerrain)
        {
            int levels = (int)MathF.Round(
                clampedHeight / (float)HeightUnitsPerMeter,
                MidpointRounding.AwayFromZero);

            Blocks.SetSurfaceRegion(
                region.TileOriginX,
                region.TileOriginY,
                region.TileWidth,
                region.TileHeight,
                levels,
                normalizedMaterial);
        }

        region.SetUniformSurface(normalizedHeight, normalizedMaterial);

        NavigationVersion++;
        TerrainVersion++;
        TerrainGeometryVersion++;
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
        if (Blocks.Volumes.HasOverridesInColumn(globalX, globalY))
            Blocks.Volumes.ClearVolume(globalX, globalY, 0, 1, 1, LayerCount);
        _volumeSurfaceHeights[columnIndex] = 0;

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

        if (UsesDiscreteBlockTerrain)
        {
            clampedHeight = Math.Clamp(
                (int)MathF.Round(
                    clampedHeight / (float)HeightUnitsPerMeter,
                    MidpointRounding.AwayFromZero) * HeightUnitsPerMeter,
                0,
                LayerCount * LayerHeightUnits);
        }

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

        // A local height edit materializes only this tile's vertical column
        // inside the owning region. Uniform neighbouring tiles stay compressed.
        int layersToUpdate = Math.Max(previousLayers, filledLayers);
        for (int z = 0; z < layersToUpdate; z++)
        {
            _layers[z].GetRegion(regionX, regionY)!
                .SetMaterialIdAtIndex(
                    localIndex,
                    z < filledLayers
                        ? materialId
                        : (ushort)0);
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

        WorldRegion? region = GetWorldRegionAtTile(globalX, globalY);
        if (region != null && region.HasUniformSurface)
            return region.UniformSurfaceHeightUnits;

        int index = GetColumnIndex(globalX, globalY);
        return Math.Max(_surfaceHeights[index], _volumeSurfaceHeights[index]);
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

        if (Blocks.Volumes.HasOverridesInColumn(globalX, globalY))
            Blocks.Volumes.ClearVolume(globalX, globalY, 0, 1, 1, LayerCount);
        _volumeSurfaceHeights[columnIndex] = 0;
        _surfaceHeights[columnIndex] = 0;
        _rangeCache[columnIndex] = null;
        _simpleFillColumns[columnIndex] = true;
        _columnMaterialIds[columnIndex] = 0;
    }

    private void RecalculateSurfaceHeight(int columnIndex)
    {
        int globalX = columnIndex % TileWidth;
        int globalY = columnIndex / TileWidth;
        GetWorldRegionAtTile(globalX, globalY)?.InvalidateUniformSurface();

        int topLayer = FindTopOccupiedLayer(
            globalX, globalY, LayerCount - 1);

        _surfaceHeights[columnIndex] = topLayer < 0
            ? (ushort)0
            : checked((ushort)((topLayer + 1) * LayerHeightUnits));
        _volumeSurfaceHeights[columnIndex] = 0;
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
