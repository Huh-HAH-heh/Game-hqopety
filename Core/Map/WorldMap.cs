using System;
using System.IO;
using System.Numerics;
using System.Threading;

namespace Core.Map;

public sealed class WorldMap
{
    private readonly ReaderWriterLockSlim _voxelBaseSamplerLock =
        new(LockRecursionPolicy.SupportsRecursion);
    private readonly int _baseVoxelWidth;
    private readonly int _baseVoxelHeight;
    private readonly int _baseVoxelDepth;

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
    public VoxelVolumeStore VoxelVolume { get; private set; }
    public bool UsesDiscreteBlockTerrain { get; private set; }

    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly TerrainLayer[] _layers;
    private readonly WorldRegion[] _worldRegions;
    private readonly ushort[] _surfaceHeights;
    private readonly ushort[] _volumeSurfaceHeights;
    private readonly bool[] _navigationBlocked;
    private readonly bool[] _simpleFillColumns;
    private readonly ushort[] _columnMaterialIds;
    private readonly byte[] _voxelSurfaceCacheState;
    private readonly ushort[] _voxelSurfaceHeightCache;
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
        WriteSimpleFillFlags(0, _simpleFillColumns.Length, true);
        _columnMaterialIds = new ushort[columnCount];
        _voxelSurfaceCacheState = new byte[columnCount];
        _voxelSurfaceHeightCache = new ushort[columnCount];
        _rangeCache = new TileRange[]?[columnCount];

        _baseVoxelWidth = checked(width * 10);
        _baseVoxelHeight = layerCount;
        _baseVoxelDepth = checked(height * 10);

        Water = new WaterLayer(width, height);
        VoxelVolume = new VoxelVolumeStore(
            _baseVoxelWidth,
            _baseVoxelHeight,
            _baseVoxelDepth,
            voxelSizeMeters: SparseVoxelField.VoxelSizeMeters);
        Voxels = new SparseVoxelField(
            _baseVoxelWidth,
            _baseVoxelHeight,
            _baseVoxelDepth,
            SampleVoxelVolumeDensity,
            SampleVoxelVolumeMaterial,
            GetBaseVoxelSurfaceRange,
            SampleVoxelVolumeDensityColumn);
        Voxels.Changed += OnVoxelFieldChanged;
    }

    /// <summary>
    /// Captures the generated height/material anchors as an immutable 3D base.
    /// Call after SetSurfaceHeightPatches and before adding static geology.
    /// </summary>
    public void InitializeVoxelVolumeBase()
    {
        VoxelVolume = new VoxelVolumeStore(
            _baseVoxelWidth,
            _baseVoxelHeight,
            _baseVoxelDepth,
            voxelSizeMeters: SparseVoxelField.VoxelSizeMeters);
        VoxelVolume.SetBaseSource(new WorldMapVoxelBaseSource(
            TileWidth,
            TileHeight,
            _baseVoxelHeight,
            _surfaceHeights,
            _columnMaterialIds,
            ComputeVoxelBaseSignature()));
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
                            int topLayer = FindTopOccupiedLayer(
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
    /// Removes empty voxel bricks left by cleared edits. The old method name
    /// remains as a compatibility entry point for callers managing block terrain.
    /// </summary>
    public int CompactVoxelStorage()
    {
        return Voxels.Compact();
    }

    public int CompactHierarchicalBlockVolumes(int minimumBlockSize = 32)
    {
        if (!UsesDiscreteBlockTerrain)
            throw new InvalidOperationException("Discrete block terrain is not enabled.");
        if (minimumBlockSize < 2 ||
            (minimumBlockSize & (minimumBlockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBlockSize));

        return CompactVoxelStorage();
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
    // SparseVoxelField owns density/material overrides. Height anchors are its
    // compact procedural base; legacy tile ranges are generated on demand.
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
        _voxelBaseSamplerLock.EnterWriteLock();
        try
        {
            if (!IsInside(x, y) || z < 0 || z >= LayerCount)
                throw new IndexOutOfRangeException();

            int index = GetColumnIndex(x, y);
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
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
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

    // Uppermost upward-facing surface in the effective terrain column.
    public int FindTopOccupiedLayer(int x, int y, int maxLayer)
    {
        if (!IsInside(x, y) || maxLayer < 0)
            return -1;

        int lastLayer = Math.Min(maxLayer, LayerCount - 1);
        int column = GetColumnIndex(x, y);
        if (HasVoxelEditsInColumn(x, y))
        {
            ushort height = GetVoxelSurfaceHeightUnits(x, y, column);
            int surfaceLayer = height == 0
                ? -1
                : (height + LayerHeightUnits - 1) / LayerHeightUnits - 1;
            return Math.Min(surfaceLayer, lastLayer);
        }

        if (_simpleFillColumns[column])
        {
            int filledLayers = Math.Clamp(
                (_surfaceHeights[column] + LayerHeightUnits - 1) /
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
        if (HasVoxelEditsInColumn(x, y))
            return Voxels.IsColumnSolidBetweenTiles(x, y, first, last);

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

    public int GetSurfaceLayer(int x, int y)
    {
        if (!IsInside(x, y))
            return -1;

        int column = GetColumnIndex(x, y);
        ushort height = HasVoxelEditsInColumn(x, y)
            ? GetVoxelSurfaceHeightUnits(x, y, column)
            : GetSurfaceHeightUnits(x, y);
        if (height == 0)
            return -1;

        int layer = (height + LayerHeightUnits - 1) /
            LayerHeightUnits - 1;
        return Math.Min(LayerCount - 1, layer);
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
            int x = i % TileWidth;
            int y = i / TileWidth;
            ushort height = HasVoxelEditsInColumn(x, y)
                ? GetVoxelSurfaceHeightUnits(x, y, i)
                : _simpleFillColumns[i]
                    ? _surfaceHeights[i]
                    : GetSurfaceHeightUnits(x, y);
            if (height == 0)
                continue;

            int layer = (height + LayerHeightUnits - 1) /
                LayerHeightUnits - 1;
            if (layer > highest)
                highest = layer;
        }

        return Math.Min(LayerCount - 1, highest);
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
        int count = 0;
        int runStart = 0;
        ushort runMaterial = GetMaterialId(globalX, globalY, 0);

        for (int layer = 1; layer < LayerCount; layer++)
        {
            ushort material = GetMaterialId(globalX, globalY, layer);
            if (material == runMaterial)
                continue;

            ranges[count++] = new TileRange
            {
                StartZ = checked((ushort)runStart),
                EndZ = checked((ushort)layer),
                MaterialId = runMaterial,
                State = runMaterial == 0 ? StateEmpty : StateSolid
            };
            runStart = layer;
            runMaterial = material;
        }

        ranges[count++] = new TileRange
        {
            StartZ = checked((ushort)runStart),
            EndZ = checked((ushort)LayerCount),
            MaterialId = runMaterial,
            State = runMaterial == 0 ? StateEmpty : StateSolid
        };

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
        _voxelBaseSamplerLock.EnterWriteLock();
        try
        {
            if (!IsInside(globalX, globalY))
                throw new IndexOutOfRangeException();

            GetOrCreateRegion(
                globalX / TerrainRegion.TilesPerSide,
                globalY / TerrainRegion.TilesPerSide);

            int index = GetColumnIndex(globalX, globalY);
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
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
    }

        public void ClearTileRanges(
        int globalX,
        int globalY)
    {
        _voxelBaseSamplerLock.EnterWriteLock();
        try
        {
            if (!IsInside(globalX, globalY))
                return;

            ClearColumn(globalX, globalY);
            MarkTerrainChanged(globalX, globalY);
        
        }
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
    }

        public bool TryAddRange(
        int globalX,
        int globalY,
        ushort startZ,
        ushort endZ,
        ushort materialId,
        byte state)
    {
        _voxelBaseSamplerLock.EnterWriteLock();
        try
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
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
    }

    // Removes a vertical voxel interval from one XY column.
    // startLayer is inclusive; endLayerExclusive is exclusive.
        public void ClearVoxelRange(
        int globalX,
        int globalY,
        int startLayer,
        int endLayerExclusive)
    {
        _voxelBaseSamplerLock.EnterWriteLock();
        try
        {
            if (!IsInside(globalX, globalY))
                throw new IndexOutOfRangeException();

            int firstLayer = Math.Max(0, startLayer);
            int lastLayer = Math.Min(LayerCount, endLayerExclusive);
            if (firstLayer >= lastLayer)
                return;

            int index = GetColumnIndex(globalX, globalY);
            Voxels.CarveBox(
                new Vector3(globalX, firstLayer * 0.1f, globalY),
                new Vector3(globalX + 1f, lastLayer * 0.1f, globalY + 1f));

            _rangeCache[index] = null;
            RecalculateSurfaceHeight(index);
            MarkTerrainChanged(globalX, globalY);
        
        }
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
    }

    public void SetRegionSolidHeight(
        int regionX,
        int regionY,
        ushort height,
        ushort materialId)
    {
        _voxelBaseSamplerLock.EnterWriteLock();
        try
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
            if (region.HasUniformSurface &&
                region.UniformSurfaceHeightUnits == normalizedHeight &&
                region.UniformSurfaceMaterialId == normalizedMaterial)
                return;

            for (int row = 0; row < region.TileHeight; row++)
            {
                int index = GetColumnIndex(
                    region.TileOriginX,
                    region.TileOriginY + row);
                WriteSurfaceHeights(index, region.TileWidth, normalizedHeight);
                Array.Clear(_voxelSurfaceCacheState, index, region.TileWidth);
                Array.Clear(_voxelSurfaceHeightCache, index, region.TileWidth);
                Array.Clear(_volumeSurfaceHeights, index, region.TileWidth);
                WriteColumnMaterials(index, region.TileWidth, normalizedMaterial);
                WriteSimpleFillFlags(index, region.TileWidth, true);
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
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
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
        _voxelBaseSamplerLock.EnterWriteLock();
        try
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

                        WriteSurfaceHeights(targetIndex, patchSize, storedHeight);
                        Array.Clear(_voxelSurfaceCacheState, targetIndex, patchSize);
                        Array.Clear(_voxelSurfaceHeightCache, targetIndex, patchSize);
                        WriteColumnMaterials(targetIndex, patchSize, storedMaterial);
                        WriteSimpleFillFlags(targetIndex, patchSize, true);
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
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
    }

    public void SetSolidHeight(
        int globalX,
        int globalY,
        ushort height,
        ushort materialId)
    {
        _voxelBaseSamplerLock.EnterWriteLock();
        try
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

            // The procedural base remains in the compact height/material arrays.
            Volatile.Write(ref _surfaceHeights[index], storedHeight);
            _volumeSurfaceHeights[index] = 0;
            _voxelSurfaceCacheState[index] = 0;
            _voxelSurfaceHeightCache[index] = 0;
            Volatile.Write(ref _simpleFillColumns[index], true);
            Volatile.Write(ref _columnMaterialIds[index], storedMaterial);
            _rangeCache[index] = null;

            if (VoxelVolume.HasBaseSource)
            {
                Vector3 columnMinimum = new(globalX, 0f, globalY);
                Vector3 columnMaximum = new(
                    globalX + 1f,
                    LayerCount * SparseVoxelField.VoxelSizeMeters,
                    globalY + 1f);
                VoxelVolume.CarveBox(columnMinimum, columnMaximum);

                if (storedHeight > 0)
                {
                    Vector3 solidMaximum = new(
                        globalX + 1f,
                        storedHeight * SparseVoxelField.VoxelSizeMeters,
                        globalY + 1f);
                    VoxelVolume.AddBox(
                        columnMinimum, solidMaximum, storedMaterial);
                }
            }

            Voxels.ResetBaseColumn(globalX, globalY);
            Voxels.InvalidateBaseField();
            Volatile.Write(ref _simpleFillColumns[index], true);
            Volatile.Write(ref _columnMaterialIds[index], storedMaterial);
            MarkTerrainChanged(globalX, globalY);
        
        }
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
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
        if (HasVoxelEditsInColumn(globalX, globalY))
            return GetVoxelSurfaceHeightUnits(globalX, globalY, index);

        if (_simpleFillColumns[index])
            return _surfaceHeights[index];

        int topLayer = FindTopOccupiedLayer(globalX, globalY, LayerCount - 1);
        return topLayer < 0
            ? (ushort)0
            : checked((ushort)((topLayer + 1) * LayerHeightUnits));
    }

    private bool HasVoxelEditsInColumn(int globalX, int globalY)
    {
        int index = GetColumnIndex(globalX, globalY);
        if (_voxelSurfaceCacheState[index] == 1 ||
            _voxelSurfaceCacheState[index] == 2)
            return true;

        if (!Voxels.HasEditsInColumn(globalX * 10 + 5, globalY * 10 + 5))
            return false;

        _voxelSurfaceCacheState[index] = 1;
        return true;
    }

    private ushort GetVoxelSurfaceHeightUnits(
        int globalX,
        int globalY,
        int columnIndex)
    {
        if (_voxelSurfaceCacheState[columnIndex] == 1)
        {
            bool found = Voxels.TryFindHighestWalkableSurfaceAt(
                globalX + 0.5f,
                globalY + 0.5f,
                minimumUpNormal: 0.35f,
                out SparseVoxelField.WalkableSurface surface);
            _voxelSurfaceHeightCache[columnIndex] = found
                ? checked((ushort)Math.Clamp(
                    (int)MathF.Round(
                        surface.HeightMeters * HeightUnitsPerMeter,
                        MidpointRounding.AwayFromZero),
                    0,
                    LayerCount * LayerHeightUnits))
                : (ushort)0;
            _voxelSurfaceCacheState[columnIndex] = 2;
        }

        return _voxelSurfaceHeightCache[columnIndex];
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
        _volumeSurfaceHeights[index] = 0;
        Volatile.Write(ref _surfaceHeights[index], (ushort)0);
        _rangeCache[index] = null;
        _voxelSurfaceCacheState[index] = 0;
        _voxelSurfaceHeightCache[index] = 0;
        Volatile.Write(ref _simpleFillColumns[index], true);
        Volatile.Write(ref _columnMaterialIds[index], (ushort)0);
        Voxels.ResetBaseColumn(globalX, globalY);
        Voxels.InvalidateBaseField();
        Volatile.Write(ref _simpleFillColumns[index], true);
        Volatile.Write(ref _columnMaterialIds[index], (ushort)0);
    }

    private void RecalculateSurfaceHeight(int columnIndex)
    {
        int globalX = columnIndex % TileWidth;
        int globalY = columnIndex / TileWidth;
        GetWorldRegionAtTile(globalX, globalY)?.InvalidateUniformSurface();

        _volumeSurfaceHeights[columnIndex] = 0;

        // The height/material arrays remain the immutable coarse base layer.
        // SparseVoxelField contains every local deviation from that base.
        bool hasEdits = Voxels.HasEditsInColumn(
            globalX * 10 + 5, globalY * 10 + 5);
        Volatile.Write(ref _simpleFillColumns[columnIndex], !hasEdits);
    }


    /// <summary>
    /// Expands the voxel volume in metres. Newly added space is empty and does
    /// not allocate voxel data until a volume is authored there.
    /// The legacy 1 m tile/navigation grid remains limited to its original map.
    /// </summary>
    public bool ExpandVoxelWorld(int widthMeters, int heightMeters, int depthMeters)
    {
        if (widthMeters <= 0) throw new ArgumentOutOfRangeException(nameof(widthMeters));
        if (heightMeters <= 0) throw new ArgumentOutOfRangeException(nameof(heightMeters));
        if (depthMeters <= 0) throw new ArgumentOutOfRangeException(nameof(depthMeters));

        return Voxels.ExpandTo(
            checked(widthMeters * 10),
            checked(heightMeters * 10),
            checked(depthMeters * 10));
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

    public void SaveVoxelVolume(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!VoxelVolume.HasBaseSource)
            throw new InvalidOperationException("Voxel volume base has not been initialized.");
        VoxelVolume.Save(destination);
    }

    public void LoadVoxelVolume(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        VoxelVolume = VoxelVolumeStore.Load(source, ResolveVoxelBaseSource);
    }

    private IVoxelBaseSource ResolveVoxelBaseSource(
        string typeId,
        ushort dataVersion,
        int width,
        int height,
        int depth,
        float voxelSizeMeters,
        BinaryReader reader)
    {
        if (typeId != WorldMapVoxelBaseSource.SourceTypeId || dataVersion != 1 ||
            width != _baseVoxelWidth || height != _baseVoxelHeight ||
            depth != _baseVoxelDepth ||
            MathF.Abs(voxelSizeMeters - SparseVoxelField.VoxelSizeMeters) > 1e-6f)
            throw new InvalidDataException("Voxel volume base descriptor does not match this WorldMap.");

        ulong signature = reader.ReadUInt64();
        if (signature != ComputeVoxelBaseSignature())
            throw new InvalidDataException("Voxel volume was saved for a different generated base terrain.");

        return new WorldMapVoxelBaseSource(
            TileWidth,
            TileHeight,
            _baseVoxelHeight,
            _surfaceHeights,
            _columnMaterialIds,
            signature);
    }

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

    public void AddVoxelRamp(
        Vector3 startMeters,
        Vector3 endMeters,
        float widthMeters,
        float thicknessMeters,
        ushort materialId) =>
        Voxels.AddRamp(startMeters, endMeters, widthMeters, thicknessMeters, materialId);

    public void CarveVoxelRamp(
        Vector3 startMeters,
        Vector3 endMeters,
        float widthMeters,
        float thicknessMeters) =>
        Voxels.CarveRamp(startMeters, endMeters, widthMeters, thicknessMeters);

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

    /// <summary>
    /// Returns the height range of the procedural base inside a voxel X/Z macro.
    /// Surface heights are already stored in 0.1 m units, so this query avoids
    /// sampling the 3D volume merely to discover its vertical exterior band.
    /// </summary>
    private (int Min, int Max) GetBaseVoxelSurfaceRange(
        int minX,
        int maxX,
        int minZ,
        int maxZ)
    {
        if (minX >= _baseVoxelWidth || minZ >= _baseVoxelDepth ||
            maxX <= 0 || maxZ <= 0 || maxX <= minX || maxZ <= minZ)
            return (-1, -1);

        // Bilinear base sampling can read one height anchor beyond either
        // macro edge. Include that halo so the range can never understate it.
        int firstTileX = Math.Clamp(minX / 10 - 1, 0, TileWidth - 1);
        int firstTileZ = Math.Clamp(minZ / 10 - 1, 0, TileHeight - 1);
        int lastTileX = Math.Clamp((Math.Min(maxX, _baseVoxelWidth) - 1) / 10 + 1, 0, TileWidth - 1);
        int lastTileZ = Math.Clamp((Math.Min(maxZ, _baseVoxelDepth) - 1) / 10 + 1, 0, TileHeight - 1);

        int minimum = int.MaxValue;
        int maximum = -1;
        for (int z = firstTileZ; z <= lastTileZ; z++)
        for (int x = firstTileX; x <= lastTileX; x++)
        {
            int height = Volatile.Read(ref _surfaceHeights[GetColumnIndex(x, z)]);
            if (height <= 0)
                continue;

            minimum = Math.Min(minimum, height);
            maximum = Math.Max(maximum, height);
        }

        return maximum < 0 ? (-1, -1) : (minimum, maximum);
    }

    /// <summary>
    /// Samples the procedural base as a continuous signed-distance approximation.
    /// Surface anchors are stored at 1 m tile centres, then reconstructed bilinearly
    /// on the shared 0.1 m voxel lattice. Positive values are solid; negative are air.
    /// Density is measured in voxel units, matching SparseVoxelField's edit scale.
    /// </summary>
    private float SampleVoxelVolumeDensity(int voxelX, int voxelY, int voxelZ)
    {
        if (!VoxelVolume.HasBaseSource)
            return SampleBaseVoxelDensity(voxelX, voxelY, voxelZ);

        return VoxelVolume.GetDensity(voxelX, voxelY, voxelZ) /
            (float)VoxelVolumeStore.DensityUnitsPerMeter /
            SparseVoxelField.VoxelSizeMeters;
    }

    private ushort SampleVoxelVolumeMaterial(int voxelX, int voxelY, int voxelZ)
    {
        if (!VoxelVolume.HasBaseSource)
            return SampleBaseVoxelMaterial(voxelX, voxelY, voxelZ);

        return VoxelVolume.GetMaterialId(voxelX, voxelY, voxelZ);
    }

    private void SampleVoxelVolumeDensityColumn(
        int voxelX,
        int voxelZ,
        int startY,
        int step,
        Span<float> destination)
    {
        if (!VoxelVolume.HasBaseSource)
        {
            SampleBaseVoxelDensityColumn(
                voxelX, voxelZ, startY, step, destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = VoxelVolume.GetDensity(
                voxelX, startY + i * step, voxelZ) /
                (float)VoxelVolumeStore.DensityUnitsPerMeter /
                SparseVoxelField.VoxelSizeMeters;
        }
    }

    private void SampleBaseVoxelDensityColumn(
        int voxelX,
        int voxelZ,
        int startY,
        int step,
        Span<float> destination)
    {
        if ((uint)voxelX >= (uint)_baseVoxelWidth ||
            (uint)voxelZ >= (uint)_baseVoxelDepth)
        {
            destination.Fill(-1f);
            return;
        }

        float surfaceHeight = SampleBaseSurfaceHeight(
            voxelX, voxelZ,
            out float slopeX,
            out float slopeZ);
        float gradientLength = MathF.Sqrt(
            1f + slopeX * slopeX + slopeZ * slopeZ);

        for (int i = 0; i < destination.Length; i++)
        {
            int voxelY = startY + i * step;
            destination[i] = voxelY < 0 || voxelY >= _baseVoxelHeight
                ? -1f
                : ComposeBaseVoxelDensity(
                    voxelX, voxelY, voxelZ,
                    surfaceHeight, gradientLength);
        }
    }

    private float SampleBaseVoxelDensity(int voxelX, int voxelY, int voxelZ)
    {
        if ((uint)voxelX >= (uint)_baseVoxelWidth ||
            (uint)voxelZ >= (uint)_baseVoxelDepth ||
            voxelY < 0 ||
            voxelY >= _baseVoxelHeight)
            return -1f;

        float surfaceHeight = SampleBaseSurfaceHeight(
            voxelX, voxelZ,
            out float slopeX,
            out float slopeZ);
        float gradientLength = MathF.Sqrt(
            1f + slopeX * slopeX + slopeZ * slopeZ);

        return ComposeBaseVoxelDensity(
            voxelX, voxelY, voxelZ,
            surfaceHeight, gradientLength);
    }

    private static float ComposeBaseVoxelDensity(
        int voxelX,
        int voxelY,
        int voxelZ,
        float surfaceHeight,
        float gradientLength)
    {
        float baseDistance =
            (surfaceHeight - (voxelY + 0.5f)) / gradientLength;

        // Keep the untouched surface gently irregular. The old 2.2 m,
        // high-frequency displacement dominated the 1 m base relief and
        // produced a cratered / lunar appearance at coarse render LODs.
        // Density is measured in 0.1 m voxels: 4 samples = 0.4 m maximum.
        if (baseDistance >= -24f && baseDistance <= 24f)
        {
            float displacement = SampleBaseVolumeNoise(
                voxelX * 0.014f,
                voxelY * 0.020f,
                voxelZ * 0.014f);
            return baseDistance + displacement * 4f;
        }

        // Subsurface cave network, limited to 12.8 m beneath the base surface.
        if (baseDistance > 24f &&
            baseDistance < SparseVoxelField.ProceduralCaveDepthVoxels)
        {
            float tunnelA = SampleBaseVolumeNoise(
                voxelX * 0.022f,
                voxelY * 0.037f,
                voxelZ * 0.022f);
            float tunnelB = SampleBaseVolumeNoise(
                voxelX * 0.022f + 19.1f,
                voxelY * 0.037f - 31.7f,
                voxelZ * 0.022f + 7.3f);
            float tunnelField = MathF.Abs(tunnelA) +
                MathF.Abs(tunnelB) * 0.72f;
            return MathF.Min(baseDistance, (tunnelField - 0.24f) * 24f);
        }

        return baseDistance;
    }

    private static float SampleBaseVolumeNoise(float x, float y, float z)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);

        float tx = SmoothNoise(x - x0);
        float ty = SmoothNoise(y - y0);
        float tz = SmoothNoise(z - z0);

        float x00 = LerpNoise(NoiseHash3D(x0, y0, z0), NoiseHash3D(x0 + 1, y0, z0), tx);
        float x10 = LerpNoise(NoiseHash3D(x0, y0 + 1, z0), NoiseHash3D(x0 + 1, y0 + 1, z0), tx);
        float x01 = LerpNoise(NoiseHash3D(x0, y0, z0 + 1), NoiseHash3D(x0 + 1, y0, z0 + 1), tx);
        float x11 = LerpNoise(NoiseHash3D(x0, y0 + 1, z0 + 1), NoiseHash3D(x0 + 1, y0 + 1, z0 + 1), tx);

        return LerpNoise(
            LerpNoise(x00, x10, ty),
            LerpNoise(x01, x11, ty),
            tz);
    }

    private static float SmoothNoise(float value) =>
        value * value * (3f - 2f * value);

    private static float LerpNoise(float a, float b, float t) =>
        a + (b - a) * t;

    private static float NoiseHash3D(int x, int y, int z)
    {
        unchecked
        {
            uint hash = (uint)x * 0x8DA6B343u ^
                (uint)y * 0xD8163841u ^
                (uint)z * 0xCB1AB31Fu;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) * (2f / 16777215f) - 1f;
        }
    }

    private ushort SampleBaseVoxelMaterial(int voxelX, int voxelY, int voxelZ)
    {
        if ((uint)voxelX >= (uint)_baseVoxelWidth ||
            voxelY < 0 ||
            voxelY >= _baseVoxelHeight ||
            (uint)voxelZ >= (uint)_baseVoxelDepth)
            return 0;

        int tileX = Math.Clamp(voxelX / 10, 0, MaxTileX);
        int tileZ = Math.Clamp(voxelZ / 10, 0, MaxTileY);
        ushort materialId = Volatile.Read(
            ref _columnMaterialIds[GetColumnIndex(tileX, tileZ)]);

        // Volumetric overhangs can be solid outside the 2D material column.
        return materialId == 0 ? (ushort)1 : materialId;
    }

    /// <summary>
    /// Reconstructs the 1 m height anchors at a voxel-centre position.
    /// Returns height in vertical voxel units and slopes per horizontal voxel.
    /// </summary>
    private float SampleBaseSurfaceHeight(
        int voxelX,
        int voxelZ,
        out float slopeX,
        out float slopeZ)
    {
        // Height anchors are located at tile centres, not tile corners.
        float gridX = (voxelX + 0.5f) * SparseVoxelField.VoxelSizeMeters - 0.5f;
        float gridZ = (voxelZ + 0.5f) * SparseVoxelField.VoxelSizeMeters - 0.5f;

        int rawX0 = (int)MathF.Floor(gridX);
        int rawZ0 = (int)MathF.Floor(gridZ);
        float tx = gridX - rawX0;
        float tz = gridZ - rawZ0;

        int x0 = Math.Clamp(rawX0, 0, MaxTileX);
        int x1 = Math.Clamp(rawX0 + 1, 0, MaxTileX);
        int z0 = Math.Clamp(rawZ0, 0, MaxTileY);
        int z1 = Math.Clamp(rawZ0 + 1, 0, MaxTileY);

        float h00 = Volatile.Read(ref _surfaceHeights[GetColumnIndex(x0, z0)]);
        float h10 = Volatile.Read(ref _surfaceHeights[GetColumnIndex(x1, z0)]);
        float h01 = Volatile.Read(ref _surfaceHeights[GetColumnIndex(x0, z1)]);
        float h11 = Volatile.Read(ref _surfaceHeights[GetColumnIndex(x1, z1)]);

        float row0 = h00 + (h10 - h00) * tx;
        float row1 = h01 + (h11 - h01) * tx;
        float height = row0 + (row1 - row0) * tz;

        float derivativeTileX =
            (h10 - h00) * (1f - tz) +
            (h11 - h01) * tz;
        float derivativeTileZ =
            (h01 - h00) * (1f - tx) +
            (h11 - h10) * tx;

        // One voxel step spans 0.1 m while one height anchor spans 1 m.
        slopeX = derivativeTileX * SparseVoxelField.VoxelSizeMeters;
        slopeZ = derivativeTileZ * SparseVoxelField.VoxelSizeMeters;

        return height;
    }

    private void OnVoxelFieldChanged(SparseVoxelField.VoxelBounds bounds)
    {
        _voxelBaseSamplerLock.EnterWriteLock();
        try
        {
            if (bounds.MinX <= 0 && bounds.MinY <= 0 && bounds.MinZ <= 0 &&
                bounds.MaxX >= Voxels.Width &&
                bounds.MaxY >= Voxels.Height &&
                bounds.MaxZ >= Voxels.Depth)
            {
                WriteSimpleFillFlags(0, _simpleFillColumns.Length, true);
                Array.Clear(_voxelSurfaceCacheState);
                Array.Clear(_voxelSurfaceHeightCache);
                Array.Clear(_rangeCache);
                foreach (WorldRegion region in _worldRegions)
                    region.MarkTerrainChanged();

                NavigationVersion++;
                TerrainVersion++;
                TerrainGeometryVersion++;
                return;
            }

            // Edits in the expanded voxel-only area do not touch legacy tile
            // navigation or the original heightfield surface caches.
            if (bounds.MaxX <= 0 || bounds.MaxY <= 0 || bounds.MaxZ <= 0 ||
                bounds.MinX >= _baseVoxelWidth ||
                bounds.MinY >= _baseVoxelHeight ||
                bounds.MinZ >= _baseVoxelDepth)
                return;

            int minVoxelX = Math.Max(0, bounds.MinX);
            int minVoxelZ = Math.Max(0, bounds.MinZ);
            int maxVoxelX = Math.Min(_baseVoxelWidth, bounds.MaxX);
            int maxVoxelZ = Math.Min(_baseVoxelDepth, bounds.MaxZ);

            int firstTileX = minVoxelX / 10;
            int firstTileY = minVoxelZ / 10;
            int lastTileX = (maxVoxelX - 1) / 10;
            int lastTileY = (maxVoxelZ - 1) / 10;

            for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
            {
                for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
                {
                    int columnIndex = GetColumnIndex(tileX, tileY);
                    _rangeCache[columnIndex] = null;
                    _voxelSurfaceCacheState[columnIndex] = 1;

                    // The procedural base stays in surface arrays; all local
                    // 3D deviations are already represented by SparseVoxelField.
                    Volatile.Write(ref _simpleFillColumns[columnIndex], false);
                    MarkTerrainChanged(tileX, tileY);
                }
            }

            NavigationVersion++;
        
        }
        finally
        {
            _voxelBaseSamplerLock.ExitWriteLock();
        }
    }

    private void WriteSurfaceHeights(
        int startIndex,
        int count,
        ushort value)
    {
        int end = startIndex + count;
        for (int index = startIndex; index < end; index++)
            Volatile.Write(ref _surfaceHeights[index], value);
    }

    private void WriteColumnMaterials(
        int startIndex,
        int count,
        ushort value)
    {
        int end = startIndex + count;
        for (int index = startIndex; index < end; index++)
            Volatile.Write(ref _columnMaterialIds[index], value);
    }

    private void WriteSimpleFillFlags(
        int startIndex,
        int count,
        bool value)
    {
        int end = startIndex + count;
        for (int index = startIndex; index < end; index++)
            Volatile.Write(ref _simpleFillColumns[index], value);
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

    private sealed class WorldMapVoxelBaseSource : IVoxelBaseSource
    {
        public const string SourceTypeId = "rimclone.worldmap-heightfield";

        private const float VoxelSize = SparseVoxelField.VoxelSizeMeters;
        private const float DensityClamp = VoxelVolumeStore.SurfaceBandVoxels * VoxelSize;

        private readonly int _tileWidth;
        private readonly int _tileHeight;
        private readonly ushort[] _surfaceHeights;
        private readonly ushort[] _materials;
        private readonly ulong _signature;

        public string TypeId => SourceTypeId;
        public ushort DataVersion => 1;
        public int Width => _tileWidth * 10;
        public int Height { get; }
        public int Depth => _tileHeight * 10;
        public float VoxelSizeMeters => VoxelSize;

        public WorldMapVoxelBaseSource(
            int tileWidth,
            int tileHeight,
            int height,
            ushort[] surfaceHeights,
            ushort[] materials,
            ulong signature)
        {
            if (tileWidth <= 0 || tileHeight <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(tileWidth));
            if (surfaceHeights.Length != checked(tileWidth * tileHeight) ||
                materials.Length != surfaceHeights.Length)
                throw new ArgumentException("WorldMap base arrays do not match their dimensions.");

            _tileWidth = tileWidth;
            _tileHeight = tileHeight;
            Height = height;
            _surfaceHeights = (ushort[])surfaceHeights.Clone();
            _materials = (ushort[])materials.Clone();
            _signature = signature;
        }

        public VoxelVolumeStore.VoxelSample Sample(int x, int y, int z)
        {
            if ((uint)x >= (uint)Width ||
                (uint)y >= (uint)Height ||
                (uint)z >= (uint)Depth)
                return new VoxelVolumeStore.VoxelSample(short.MinValue, 0);

            float surfaceHeight = SampleSurfaceHeight(x, z, out float slopeX, out float slopeZ);
            float gradient = MathF.Sqrt(1f + slopeX * slopeX + slopeZ * slopeZ);
            float distanceVoxels = ComposeBaseVoxelDensity(
                x, y, z, surfaceHeight, gradient);
            float distanceMeters = Math.Clamp(
                distanceVoxels * VoxelSize,
                -DensityClamp,
                DensityClamp);
            short density = (short)MathF.Round(
                distanceMeters * VoxelVolumeStore.DensityUnitsPerMeter);

            if (density <= 0)
                return new VoxelVolumeStore.VoxelSample(density, 0);

            int tileX = Math.Clamp(x / 10, 0, _tileWidth - 1);
            int tileZ = Math.Clamp(z / 10, 0, _tileHeight - 1);
            ushort material = _materials[tileX + tileZ * _tileWidth];
            return new VoxelVolumeStore.VoxelSample(
                density,
                material == 0 ? (ushort)1 : material);
        }

        public void WriteState(BinaryWriter writer) => writer.Write(_signature);

        private float SampleSurfaceHeight(
            int voxelX,
            int voxelZ,
            out float slopeX,
            out float slopeZ)
        {
            float gridX = (voxelX + 0.5f) * VoxelSize - 0.5f;
            float gridZ = (voxelZ + 0.5f) * VoxelSize - 0.5f;
            int rawX = (int)MathF.Floor(gridX);
            int rawZ = (int)MathF.Floor(gridZ);
            float tx = gridX - rawX;
            float tz = gridZ - rawZ;

            int x0 = Math.Clamp(rawX, 0, _tileWidth - 1);
            int x1 = Math.Clamp(rawX + 1, 0, _tileWidth - 1);
            int z0 = Math.Clamp(rawZ, 0, _tileHeight - 1);
            int z1 = Math.Clamp(rawZ + 1, 0, _tileHeight - 1);

            float h00 = _surfaceHeights[x0 + z0 * _tileWidth];
            float h10 = _surfaceHeights[x1 + z0 * _tileWidth];
            float h01 = _surfaceHeights[x0 + z1 * _tileWidth];
            float h11 = _surfaceHeights[x1 + z1 * _tileWidth];

            float row0 = h00 + (h10 - h00) * tx;
            float row1 = h01 + (h11 - h01) * tx;
            float height = row0 + (row1 - row0) * tz;

            float derivativeX = (h10 - h00) * (1f - tz) +
                (h11 - h01) * tz;
            float derivativeZ = (h01 - h00) * (1f - tx) +
                (h11 - h10) * tx;

            slopeX = derivativeX * VoxelSize;
            slopeZ = derivativeZ * VoxelSize;
            return height;
        }
    }

}
