using System;
using System.IO;
using System.Numerics;

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
    public SparseVoxelField Voxels { get; }
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

        Water = new WaterLayer(width, height);
        Voxels = new SparseVoxelField(
            checked(width * 10),
            layerCount,
            checked(height * 10),
            SampleBaseVoxelDensity,
            SampleBaseVoxelMaterial);
        Voxels.Changed += OnVoxelFieldChanged;
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

        Blocks.ClearTransitions();
        Array.Clear(_volumeSurfaceHeights);
        UsesDiscreteBlockTerrain = true;

        for (int regionY = 0; regionY < RegionsY; regionY++)
        {
            for (int regionX = 0; regionX < RegionsX; regionX++)
            {
                WorldRegion region = _worldRegions[regionX + regionY * RegionsX];
                if (region.HasUniformSurface)
                {
                    int levels = Math.Clamp(
                        (int)MathF.Round(
                            region.UniformSurfaceHeightUnits /
                            (float)HeightUnitsPerMeter,
                            MidpointRounding.AwayFromZero),
                        0,
                        LayerCount / HeightUnitsPerMeter);

                    Blocks.SetSurfaceRegion(
                        region.TileOriginX,
                        region.TileOriginY,
                        region.TileWidth,
                        region.TileHeight,
                        levels,
                        levels > 0 ? region.UniformSurfaceMaterialId : (ushort)0);
                    continue;
                }

                for (int localY = 0; localY < region.TileHeight; localY++)
                {
                    int worldY = region.TileOriginY + localY;
                    for (int localX = 0; localX < region.TileWidth; localX++)
                    {
                        int worldX = region.TileOriginX + localX;
                        int columnIndex = GetColumnIndex(worldX, worldY);
                        int levels;
                        ushort materialId;

                        if (_simpleFillColumns[columnIndex])
                        {
                            levels = Math.Clamp(
                                (int)MathF.Round(
                                    _surfaceHeights[columnIndex] /
                                        (float)HeightUnitsPerMeter,
                                    MidpointRounding.AwayFromZero),
                                0,
                                LayerCount / HeightUnitsPerMeter);
                            materialId = levels > 0
                                ? _columnMaterialIds[columnIndex]
                                : (ushort)0;
                        }
                        else
                        {
                            int topLayer = Blocks.Volumes.FindHighestSolidLayerInColumn(
                                worldX, worldY, LayerCount - 1);
                            levels = topLayer < 0
                                ? 0
                                : Math.Clamp(
                                    (int)MathF.Round(
                                        (topLayer + 1) / (float)HeightUnitsPerMeter,
                                        MidpointRounding.AwayFromZero),
                                    0,
                                    LayerCount / HeightUnitsPerMeter);
                            materialId = topLayer < 0
                                ? (ushort)0
                                : GetMaterialId(worldX, worldY, topLayer);
                        }

                        Blocks.SetSurfaceLevel(worldX, worldY, levels, materialId);
                    }
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

        // Disabling block-style movement must never erase terrain voxels.
        UsesDiscreteBlockTerrain = false;
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
    /// Compatibility cuboid API. X/Y and width/depth use 1 m map tiles;
    /// Z and height use 0.1 m vertical layers. It writes into the finer
    /// 0.1 m isotropic 3D voxel field. Material zero removes the volume.
    /// </summary>
        public void SetVoxelVolume(
        int x,
        int y,
        int zLayer,
        int width,
        int depth,
        int heightLayers,
        ushort materialId)
    {
        if (x < 0 || y < 0 || width <= 0 || depth <= 0 ||
            (long)x + width > TileWidth ||
            (long)y + depth > TileHeight ||
            zLayer < 0 || heightLayers <= 0 ||
            (long)zLayer + heightLayers > LayerCount)
            throw new ArgumentOutOfRangeException(nameof(width));

        Vector3 minimum = new(x, zLayer * 0.1f, y);
        Vector3 maximum = new(x + width, (zLayer + heightLayers) * 0.1f, y + depth);
        if (materialId == 0)
            Voxels.CarveBox(minimum, maximum);
        else
            Voxels.FillBox(minimum, maximum, materialId);
    }

    public void ClearVoxelVolume(
        int x,
        int y,
        int zLayer,
        int width,
        int depth,
        int heightLayers) =>
        SetVoxelVolume(
            x, y, zLayer, width, depth, heightLayers,
            materialId: 0);

    /// <summary>
    /// Compatibility helper for the gameplay grid: one 1 m x 1 m tile
    /// across one 0.1 m vertical layer. The edit is translated into the
    /// underlying 0.1 m x 0.1 m x 0.1 m voxel field.
    /// </summary>
    public void SetVoxel(int x, int y, int zLayer, ushort materialId) =>
        SetMaterialId(x, y, zLayer, materialId);

    public void ClearVoxel(int x, int y, int zLayer) =>
        SetVoxel(x, y, zLayer, materialId: 0);

    /// <summary>
    /// Compatibility cuboid helper. X/Y and width/depth use one-metre map
    /// cells; Z and height use metres. Material zero removes the cuboid.
    /// The actual edit is evaluated in the isotropic 0.1 m 3D density field.
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
            (long)y + depth > TileHeight ||
            zLevel < 0 || heightInMeters <= 0 ||
            (long)zLevel + heightInMeters > LayerCount * 0.1f)
            throw new ArgumentOutOfRangeException(nameof(heightInMeters));

        Vector3 minimum = new(x, zLevel, y);
        Vector3 maximum = new(x + width, zLevel + heightInMeters, y + depth);
        if (materialId == 0)
            Voxels.CarveBox(minimum, maximum);
        else
            Voxels.FillBox(minimum, maximum, materialId);
    }

    /// <summary>
    /// Adds a static material cuboid to the sparse 3D voxel field.
    /// X/Y and width/depth use 1 m map tiles; Z and height use metres.
    /// Material zero creates an empty cut. The untouched broad base surface
    /// remains a procedural signed-distance field; local 3D edits materialize
    /// only affected 16^3 voxel chunks.
    /// </summary>
        public void SetStaticVolume(
        int x,
        int y,
        int zLevel,
        int width,
        int depth,
        int heightInMeters,
        ushort materialId)
    {
        if (x < 0 || y < 0 || width <= 0 || depth <= 0 ||
            (long)x + width > TileWidth ||
            (long)y + depth > TileHeight ||
            zLevel < 0 || heightInMeters <= 0 ||
            (long)zLevel + heightInMeters > LayerCount * 0.1f)
            throw new ArgumentOutOfRangeException(nameof(heightInMeters));

        Vector3 minimum = new(x, zLevel, y);
        Vector3 maximum = new(x + width, zLevel + heightInMeters, y + depth);
        if (materialId == 0)
            Voxels.CarveBox(minimum, maximum);
        else
            Voxels.FillBox(minimum, maximum, materialId);
    }

    /// <summary>
    /// Carves a static empty cuboid out of the procedural base/voxel field.
    /// </summary>
        public void CarveStaticVolume(
        int x,
        int y,
        int zLevel,
        int width,
        int depth,
        int heightInMeters)
    {
        SetStaticVolume(
            x, y, zLevel, width, depth, heightInMeters,
            materialId: 0);
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
            throw new ArgumentOutOfRangeException(nameof(minimumBlockSize));

        return Blocks.Volumes.Compact();
    }

    public int CompactUniformBlockVolumes(int minimumBlockSize = 32) =>
        CompactHierarchicalBlockVolumes(minimumBlockSize);

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

        Vector3 minimum = new(x, zLevel, y);
        Vector3 maximum = minimum + Vector3.One;
        if (materialId == 0)
            Voxels.CarveBox(minimum, maximum);
        else
            Voxels.FillBox(minimum, maximum, materialId);
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

        Vector3 minimum = new(x, zLevel, y);
        Voxels.CarveBox(minimum, minimum + Vector3.One);
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
    // VOXEL STORAGE AND COMPATIBILITY VIEWS
    // SparseVoxelStore is the sole owner of solid/air occupancy.
    // XY cells are 1 m x 1 m; vertical layers are 0.1 m.
    // TerrainLayer/TerrainRegion are lazy read/write adapters only.
    // ============================================================

    public TerrainLayer GetLayer(
        int z)
    {
        if (z < 0 || z >= LayerCount)
            throw new IndexOutOfRangeException();

        return _layers[z];
    }

        public ushort GetMaterialId(int x, int y, int z)
    {
        if (!IsInside(x, y) || z < 0 || z >= LayerCount)
            return 0;

        int index = GetColumnIndex(x, y);
        if (_simpleFillColumns[index] &&
            !Voxels.HasEditsInColumn(x * 10 + 5, y * 10 + 5))
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[index] + LayerHeightUnits - 1) / LayerHeightUnits,
                0,
                LayerCount);
            return z < filledLayers ? _columnMaterialIds[index] : (ushort)0;
        }

        return Voxels.GetMaterialId(
            checked(x * 10 + 5),
            z,
            checked(y * 10 + 5));
    }

        public void SetMaterialId(
        int x,
        int y,
        int z,
        ushort materialId)
    {
        if (!IsInside(x, y) || z < 0 || z >= LayerCount)
            throw new IndexOutOfRangeException();

        int index = GetColumnIndex(x, y);
        if (_simpleFillColumns[index])
        {
            Blocks.Volumes.SetSurfaceColumn(
                x, y,
                _surfaceHeights[index], _columnMaterialIds[index]);
            _simpleFillColumns[index] = false;
        }

        Blocks.Volumes.SetVoxel(x, y, z, materialId);
        Vector3 minimum = new(x, z * 0.1f, y);
        Vector3 maximum = new(x + 1f, (z + 1) * 0.1f, y + 1f);
        if (materialId == 0)
            Voxels.CarveBox(minimum, maximum);
        else
            Voxels.FillBox(minimum, maximum, materialId);

        _rangeCache[index] = null;
        RecalculateSurfaceHeight(index);
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
    // Uniform columns resolve through one compressed run without scanning Z.
        public int FindTopOccupiedLayer(int x, int y, int maxLayer)
    {
        if (!IsInside(x, y) || maxLayer < 0)
            return -1;

        int lastLayer = Math.Min(maxLayer, LayerCount - 1);
        if (_simpleFillColumns[GetColumnIndex(x, y)])
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[GetColumnIndex(x, y)] + LayerHeightUnits - 1) /
                    LayerHeightUnits,
                0,
                LayerCount);
            return filledLayers == 0 ? -1 : Math.Min(filledLayers - 1, lastLayer);
        }

        for (int z = lastLayer; z >= 0; z--)
        {
            if (GetMaterialId(x, y, z) != 0)
                return z;
        }
        return -1;
    }

    // Inclusive interval: is any voxel in this vertical column solid?
        public bool IsColumnSolidBetween(
        int x,
        int y,
        int firstLayer,
        int lastLayer)
    {
        if (!IsInside(x, y))
            return true;

        int first = Math.Max(0, firstLayer);
        int last = Math.Min(LayerCount - 1, lastLayer);
        if (first > last)
            return false;

        int index = GetColumnIndex(x, y);
        if (_simpleFillColumns[index])
        {
            if (_columnMaterialIds[index] == 0)
                return false;
            int filledLayers = Math.Clamp(
                (_surfaceHeights[index] + LayerHeightUnits - 1) / LayerHeightUnits,
                0, LayerCount);
            return first < filledLayers;
        }

        for (int z = first; z <= last; z++)
        {
            if (GetMaterialId(x, y, z) != 0)
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

        int index = GetColumnIndex(globalX, globalY);
        TileRange[]? cached = _rangeCache[index];
        if (cached != null)
            return cached;

        if (_simpleFillColumns[index] &&
            !Voxels.HasEditsInColumn(globalX * 10 + 5, globalY * 10 + 5))
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[index] + LayerHeightUnits - 1) /
                    LayerHeightUnits,
                0,
                LayerCount);
            if (filledLayers == 0 || _columnMaterialIds[index] == 0)
            {
                cached = new TileRange[1];
                cached[0] = new TileRange
                {
                    StartZ = 0,
                    EndZ = checked((ushort)LayerCount),
                    MaterialId = 0,
                    State = StateEmpty
                };
            }
            else
            {
                cached = new TileRange[2];
                cached[0] = new TileRange
                {
                    StartZ = 0,
                    EndZ = checked((ushort)filledLayers),
                    MaterialId = _columnMaterialIds[index],
                    State = StateSolid
                };
                cached[1] = new TileRange
                {
                    StartZ = checked((ushort)filledLayers),
                    EndZ = checked((ushort)LayerCount),
                    MaterialId = 0,
                    State = StateEmpty
                };
            }

            _rangeCache[index] = cached;
            return cached;
        }

        Span<TileRange> ranges = stackalloc TileRange[LayerCount];
        int count = Blocks.Volumes.CopyColumnRanges(globalX, globalY, ranges);
        cached = new TileRange[count];
        ranges[..count].CopyTo(cached);
        _rangeCache[index] = cached;
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

        int index = GetColumnIndex(globalX, globalY);
        Blocks.Volumes.SetColumnRanges(globalX, globalY, ranges);
        _rangeCache[index] = null;
        RecalculateSurfaceHeight(index);

        Vector3 minimum = new(globalX, 0f, globalY);
        Vector3 maximum = new(globalX + 1f, LayerCount * 0.1f, globalY + 1f);
        Voxels.CarveBox(minimum, maximum);
        foreach (TileRange range in ranges)
        {
            if (range.State != StateSolid || range.MaterialId == 0 ||
                range.EndZ <= range.StartZ)
                continue;

            Voxels.FillBox(
                new Vector3(globalX, range.StartZ * 0.1f, globalY),
                new Vector3(globalX + 1f, range.EndZ * 0.1f, globalY + 1f),
                range.MaterialId);
        }

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
            throw new ArgumentException("StartZ не может быть больше EndZ.");

        int startLayer = Math.Clamp(
            startZ / LayerHeightUnits, 0, LayerCount);
        int endLayer = Math.Clamp(
            (endZ + LayerHeightUnits - 1) / LayerHeightUnits, 0, LayerCount);
        if (endLayer <= startLayer)
            return true;

        int index = GetColumnIndex(globalX, globalY);
        if (_simpleFillColumns[index])
        {
            Blocks.Volumes.SetSurfaceColumn(
                globalX, globalY,
                _surfaceHeights[index], _columnMaterialIds[index]);
            _simpleFillColumns[index] = false;
        }

        Blocks.Volumes.SetVolume(
            globalX, globalY, startLayer,
            1, 1, endLayer - startLayer,
            state == StateSolid ? materialId : (ushort)0);

        Vector3 minimum = new(globalX, startLayer * 0.1f, globalY);
        Vector3 maximum = new(globalX + 1f, endLayer * 0.1f, globalY + 1f);
        if (state == StateSolid && materialId != 0)
            Voxels.FillBox(minimum, maximum, materialId);
        else
            Voxels.CarveBox(minimum, maximum);

        _rangeCache[index] = null;
        RecalculateSurfaceHeight(index);
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
        int lastLayer = Math.Min(LayerCount, endLayerExclusive);
        if (firstLayer >= lastLayer)
            return;

        int index = GetColumnIndex(globalX, globalY);
        if (_simpleFillColumns[index])
        {
            Blocks.Volumes.SetSurfaceColumn(
                globalX, globalY,
                _surfaceHeights[index], _columnMaterialIds[index]);
            _simpleFillColumns[index] = false;
        }

        Blocks.Volumes.SetVolume(
            globalX, globalY, firstLayer,
            1, 1, lastLayer - firstLayer, 0);
        Voxels.CarveBox(
            new Vector3(globalX, firstLayer * 0.1f, globalY),
            new Vector3(globalX + 1f, lastLayer * 0.1f, globalY + 1f));

        _rangeCache[index] = null;
        RecalculateSurfaceHeight(index);
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
            throw new IndexOutOfRangeException();

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
        ushort normalizedMaterial = clampedHeight > 0 ? materialId : (ushort)0;
        int filledLayers = clampedHeight <= 0
            ? 0
            : Math.Clamp(
                (clampedHeight + LayerHeightUnits - 1) / LayerHeightUnits,
                0,
                LayerCount);

        if (region.HasUniformSurface &&
            region.UniformSurfaceHeightUnits == normalizedHeight &&
            region.UniformSurfaceMaterialId == normalizedMaterial)
            return;

        Blocks.Volumes.SetSurfaceRegion(
            region.TileOriginX, region.TileOriginY,
            region.TileWidth, region.TileHeight,
            filledLayers, normalizedMaterial);

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
                region.TileOriginX, region.TileOriginY,
                region.TileWidth, region.TileHeight,
                levels, normalizedMaterial);
        }

        region.SetUniformSurface(normalizedHeight, normalizedMaterial);
        Voxels.InvalidateBaseField();
        NavigationVersion++;
        TerrainVersion++;
        TerrainGeometryVersion++;
    }

    /// <summary>
    /// Replaces the world surface from a compact grid of uniform XY patches.
    /// Each sample covers patchSize x patchSize tiles. Values are written to
    /// flat surface caches without materializing every vertical voxel.
    /// Intended for bulk world generation before simulation starts.
    /// </summary>
    public void SetSurfaceHeightPatches(
        ReadOnlySpan<ushort> patchHeights,
        int patchSize,
        ushort materialId)
    {
        if (UsesDiscreteBlockTerrain)
            throw new InvalidOperationException(
                "Surface patches must be generated before enabling discrete block terrain.");
        if (patchSize <= 0 || TileWidth % patchSize != 0 || TileHeight % patchSize != 0)
            throw new ArgumentOutOfRangeException(nameof(patchSize));

        int patchesX = TileWidth / patchSize;
        int patchesY = TileHeight / patchSize;
        if (patchHeights.Length != checked(patchesX * patchesY))
            throw new ArgumentException(
                "Patch height grid does not match the world dimensions.",
                nameof(patchHeights));

        Blocks.Volumes.SetSurfaceHeightPatches(
            patchHeights, patchSize, materialId);
        Array.Clear(_volumeSurfaceHeights);

        for (int patchY = 0; patchY < patchesY; patchY++)
        {
            int firstTileY = patchY * patchSize;
            int patchRow = patchY * patchesX;

            for (int localY = 0; localY < patchSize; localY++)
            {
                int rowStart = GetColumnIndex(0, firstTileY + localY);
                for (int patchX = 0; patchX < patchesX; patchX++)
                {
                    int sourceIndex = patchRow + patchX;
                    int targetIndex = rowStart + patchX * patchSize;
                    int height = Math.Min(
                        (int)patchHeights[sourceIndex],
                        LayerCount * LayerHeightUnits);
                    ushort storedHeight = checked((ushort)height);
                    ushort storedMaterial = height > 0 && materialId != 0
                        ? materialId
                        : (ushort)0;

                    Array.Fill(_surfaceHeights, storedHeight, targetIndex, patchSize);
                    Array.Fill(_columnMaterialIds, storedMaterial, targetIndex, patchSize);
                    Array.Fill(_simpleFillColumns, true, targetIndex, patchSize);
                    Array.Clear(_rangeCache, targetIndex, patchSize);
                }
            }
        }

        for (int i = 0; i < _worldRegions.Length; i++)
            _worldRegions[i].MarkTerrainChanged();

        NavigationVersion++;
        TerrainVersion++;
        TerrainGeometryVersion++;

        Voxels.InvalidateBaseField();
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

        int clampedHeight = Math.Min(
            (int)height,
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

        int index = GetColumnIndex(globalX, globalY);
        ushort storedHeight = checked((ushort)(
            clampedHeight > 0 && materialId != 0 ? clampedHeight : 0));
        ushort storedMaterial = storedHeight > 0 ? materialId : (ushort)0;

        // Keep ordinary terrain in the compact surface arrays. The detailed
        // volume tree is created only when a column receives a complex edit.
        Blocks.Volumes.ClearColumn(globalX, globalY);
        _surfaceHeights[index] = storedHeight;
        _volumeSurfaceHeights[index] = 0;
        _simpleFillColumns[index] = true;
        _columnMaterialIds[index] = storedMaterial;
        _rangeCache[index] = null;

        Voxels.ResetBaseColumn(globalX, globalY);
        Voxels.InvalidateBaseField();
        _simpleFillColumns[index] = true;
        _columnMaterialIds[index] = storedMaterial;
        MarkTerrainChanged(globalX, globalY);
    }

    // ============================================================
    // SURFACE HEIGHT CACHE
    // Height remains available to movement / ballistic systems.
    // One Z layer represents 0.1 m; stored height units remain decimeters.
    // ============================================================

    /// <summary>
    /// True when the column is represented by the compact implicit fill height
    /// rather than individually authored vertical layer data.
    /// </summary>
    public bool IsColumnSimpleFill(int globalX, int globalY) =>
        IsInside(globalX, globalY) &&
        _simpleFillColumns[GetColumnIndex(globalX, globalY)];

    public ushort GetSurfaceHeightUnits(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            return 0;

        int index = GetColumnIndex(globalX, globalY);
        if (_simpleFillColumns[index])
            return _surfaceHeights[index];

        int topLayer = FindTopOccupiedLayer(globalX, globalY, LayerCount - 1);
        return topLayer < 0
            ? (ushort)0
            : checked((ushort)((topLayer + 1) * LayerHeightUnits));
    }

    public float GetSurfaceHeight(
        int globalX,
        int globalY)
    {
        return GetSurfaceHeightUnits(globalX, globalY) * 0.1f;
    }

    private void ClearColumn(int globalX, int globalY)
    {
        int index = GetColumnIndex(globalX, globalY);
        Blocks.Volumes.ClearColumn(globalX, globalY);
        _volumeSurfaceHeights[index] = 0;
        _surfaceHeights[index] = 0;
        _rangeCache[index] = null;
        _simpleFillColumns[index] = true;
        _columnMaterialIds[index] = 0;
        Voxels.ResetBaseColumn(globalX, globalY);
        Voxels.InvalidateBaseField();
        _simpleFillColumns[index] = true;
        _columnMaterialIds[index] = 0;
    }

    private void RecalculateSurfaceHeight(int columnIndex)
    {
        int globalX = columnIndex % TileWidth;
        int globalY = columnIndex / TileWidth;
        GetWorldRegionAtTile(globalX, globalY)?.InvalidateUniformSurface();

        _volumeSurfaceHeights[columnIndex] = 0;

        if (!Voxels.HasEditsInColumn(globalX * 10 + 5, globalY * 10 + 5) &&
            Blocks.Volumes.TryGetSimpleColumn(
                globalX, globalY,
                out ushort topExclusive,
                out ushort simpleMaterial))
        {
            _surfaceHeights[columnIndex] =
                checked((ushort)(topExclusive * LayerHeightUnits));
            _simpleFillColumns[columnIndex] = true;
            _columnMaterialIds[columnIndex] = simpleMaterial;
            return;
        }

        // _surfaceHeights is also the procedural SDF base input. Keep it intact:
        // the effective top surface is derived on demand from Voxels below.
        _simpleFillColumns[columnIndex] = false;
        _columnMaterialIds[columnIndex] = 0;
    }


    public void CarveVoxelSphere(Vector3 centerMeters, float radiusMeters) =>
        Voxels.CarveSphere(centerMeters, radiusMeters);

    public void AddVoxelSphere(Vector3 centerMeters, float radiusMeters, ushort materialId) =>
        Voxels.AddSphere(centerMeters, radiusMeters, materialId);

    public void CarveVoxelCapsule(Vector3 startMeters, Vector3 endMeters, float radiusMeters) =>
        Voxels.CarveCapsule(startMeters, endMeters, radiusMeters);

    public void AddVoxelCapsule(
        Vector3 startMeters, Vector3 endMeters, float radiusMeters, ushort materialId) =>
        Voxels.AddCapsule(startMeters, endMeters, radiusMeters, materialId);


    public bool HasVoxelEditsAt(float xMeters, float yMeters)
    {
        if (!float.IsFinite(xMeters) || !float.IsFinite(yMeters))
            return false;

        int sampleX = (int)MathF.Round(xMeters / SparseVoxelField.VoxelSizeMeters - 0.5f);
        int sampleY = (int)MathF.Round(yMeters / SparseVoxelField.VoxelSizeMeters - 0.5f);
        return Voxels.HasEditsInColumn(sampleX, sampleY);
    }

    public int FindVoxelWalkableSurfacesAt(
        float xMeters,
        float yMeters,
        float minimumUpNormal,
        Span<SparseVoxelField.WalkableSurface> destination) =>
        Voxels.FindWalkableSurfacesAt(
            xMeters, yMeters, minimumUpNormal, destination);

    public bool TryFindVoxelWalkableSurface(
        float xMeters,
        float yMeters,
        float referenceHeightMeters,
        float searchBelowMeters,
        float searchAboveMeters,
        float bodyHeightMeters,
        out SparseVoxelField.WalkableSurface surface) =>
        Voxels.TryFindClosestWalkableSurface(
            xMeters, yMeters,
            referenceHeightMeters,
            searchBelowMeters,
            searchAboveMeters,
            bodyHeightMeters,
            minimumUpNormal: 0.72f,
            out surface);

    public bool IsVoxelCapsuleClear(
        float xMeters,
        float feetHeightMeters,
        float yMeters,
        float radiusMeters,
        float bodyHeightMeters) =>
        Voxels.IsCapsuleClear(
            xMeters, feetHeightMeters, yMeters, radiusMeters, bodyHeightMeters);

    /// <summary>
    /// Saves locally edited voxel chunks only. The base world remains compact
    /// and must be regenerated with the same world seed before loading this file.
    /// </summary>
    public void SaveVoxelEdits(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A save path is required.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = fullPath + ".tmp";
        try
        {
            using (FileStream stream = new(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Voxels.SaveEdits(stream, ComputeVoxelBaseSignature());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private ulong ComputeVoxelBaseSignature()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        static ulong Add(ulong current, ushort value)
        {
            const ulong hashPrime = 1099511628211UL;
            current ^= (byte)value;
            current *= hashPrime;
            current ^= (byte)(value >> 8);
            current *= hashPrime;
            return current;
        }

        hash = Add(hash, checked((ushort)TileWidth));
        hash = Add(hash, checked((ushort)TileHeight));
        hash = Add(hash, checked((ushort)LayerCount));
        for (int index = 0; index < _surfaceHeights.Length; index++)
        {
            hash = Add(hash, _surfaceHeights[index]);
            hash = Add(hash, _columnMaterialIds[index]);
            hash = Add(hash, _volumeSurfaceHeights[index]);
        }

        // Keep the hash useful even when the world contains more than one
        // million columns; no string serialization or temporary world copy.
        hash ^= (ulong)_surfaceHeights.Length;
        hash *= prime;
        return hash;
    }

    public bool LoadVoxelEdits(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A save path is required.", nameof(path));
        if (!File.Exists(path))
            return false;

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Voxels.LoadEdits(stream, ComputeVoxelBaseSignature());
        return true;
    }


    public void CarveVoxelBox(Vector3 minimumMeters, Vector3 maximumMeters) =>
        Voxels.CarveBox(minimumMeters, maximumMeters);

    public void AddVoxelBox(
        Vector3 minimumMeters,
        Vector3 maximumMeters,
        ushort materialId) =>
        Voxels.FillBox(minimumMeters, maximumMeters, materialId);

    public void FlattenVoxelPlane(
        Vector3 centerMeters,
        Vector3 normal,
        float radiusMeters,
        ushort materialId,
        float strength = 1f) =>
        Voxels.FlattenPlane(centerMeters, normal, radiusMeters, materialId, strength);

    public void PaintVoxelSphere(Vector3 centerMeters, float radiusMeters, ushort materialId) =>
        Voxels.PaintSphere(centerMeters, radiusMeters, materialId);

    public void SmoothVoxelSphere(Vector3 centerMeters, float radiusMeters, float strength = 0.5f) =>
        Voxels.SmoothSphere(centerMeters, radiusMeters, strength);

    private float SampleBaseVoxelDensity(int voxelX, int voxelY, int voxelZ)
    {
        if (voxelX < 0 || voxelY < 0 || voxelZ < 0 ||
            voxelX >= Voxels.Width || voxelY > Voxels.Height || voxelZ >= Voxels.Depth)
            return -1f;

        // The procedural base is authored at 1 m XY resolution. Keep each
        // base map tile constant across its 10x10 horizontal voxel footprint;
        // local edits are the full 0.1 m 3D detail layer.
        int tileX = Math.Clamp(voxelX / 10, 0, MaxTileX);
        int tileY = Math.Clamp(voxelZ / 10, 0, MaxTileY);
        ushort surfaceHeight = _surfaceHeights[GetColumnIndex(tileX, tileY)];
        return surfaceHeight - (voxelY + 0.5f);
    }

    private ushort SampleBaseVoxelMaterial(int voxelX, int voxelY, int voxelZ)
    {
        if (voxelX < 0 || voxelY < 0 || voxelZ < 0 ||
            voxelX >= Voxels.Width || voxelY >= Voxels.Height || voxelZ >= Voxels.Depth)
            return 0;

        int tileX = voxelX / 10;
        int tileY = voxelZ / 10;
        int column = GetColumnIndex(tileX, tileY);
        if (_simpleFillColumns[column])
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[column] + LayerHeightUnits - 1) / LayerHeightUnits,
                0, LayerCount);
            return voxelY < filledLayers ? _columnMaterialIds[column] : (ushort)0;
        }

        return Blocks.Volumes.GetMaterialId(tileX, tileY, voxelY);
    }

    private void OnVoxelFieldChanged(SparseVoxelField.VoxelBounds bounds)
    {
        if (bounds.MinX <= 0 && bounds.MinY <= 0 && bounds.MinZ <= 0 &&
            bounds.MaxX >= Voxels.Width &&
            bounds.MaxY >= Voxels.Height &&
            bounds.MaxZ >= Voxels.Depth)
        {
            Array.Fill(_simpleFillColumns, true);
            Array.Clear(_rangeCache);
            foreach (WorldRegion region in _worldRegions)
                region.MarkTerrainChanged();

            NavigationVersion++;
            TerrainVersion++;
            TerrainGeometryVersion++;
            return;
        }

        int firstTileX = Math.Clamp(bounds.MinX / 10, 0, TileWidth - 1);
        int firstTileY = Math.Clamp(bounds.MinZ / 10, 0, TileHeight - 1);
        int lastTileX = Math.Clamp((Math.Max(bounds.MinX, bounds.MaxX - 1)) / 10, 0, TileWidth - 1);
        int lastTileY = Math.Clamp((Math.Max(bounds.MinZ, bounds.MaxZ - 1)) / 10, 0, TileHeight - 1);

        for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                int columnIndex = GetColumnIndex(tileX, tileY);
                _rangeCache[columnIndex] = null;

                // A local 3D edit promotes an ordinary implicit surface column
                // to the compatibility volume tree once, preserving old range
                // and navigation readers while the renderer uses SDF voxels.
                bool fullColumnReset =
                    bounds.MinY <= 0 && bounds.MaxY >= Voxels.Height;
                if (_simpleFillColumns[columnIndex] && !fullColumnReset)
                {
                    Blocks.Volumes.SetSurfaceColumn(
                        tileX, tileY,
                        _surfaceHeights[columnIndex],
                        _columnMaterialIds[columnIndex]);
                }

                _simpleFillColumns[columnIndex] = false;
                MarkTerrainChanged(tileX, tileY);
            }
        }

        NavigationVersion++;
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
