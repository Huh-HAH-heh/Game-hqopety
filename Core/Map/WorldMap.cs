using System;
using System.Collections.Generic;

namespace Core.Map;

public sealed class WorldMap
{
    public const byte StateEmpty = 0;
    public const byte StateSolid = 1;

    public const int DefaultTerrainLayerCount = 50;
    public const ushort LayerHeightUnits = 10;

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

    public WaterLayer Water { get; }

    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly TerrainLayer[] _layers;
    private readonly ushort[] _surfaceHeights;
    private readonly ushort[] _surfaceMaterials;
    private readonly Dictionary<int, ushort[]> _detailedColumns = new();
    private readonly TileRange[]?[] _rangeCache;

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
        int regionCount = checked(regionsX * regionsY);

        _tileRegions = new TerrainTileRegion?[regionCount];
        _surfaceHeights = new ushort[columnCount];
        _surfaceMaterials = new ushort[columnCount];
        _rangeCache = new TileRange[]?[columnCount];
        _layers = new TerrainLayer[layerCount];

        for (int z = 0; z < layerCount; z++)
        {
            int layerZ = z;
            _layers[z] = new TerrainLayer(
                z,
                width,
                height,
                regionsX,
                regionsY,
                _tileRegions,
                (x, y) => GetMaterialId(x, y, layerZ));
        }

        Water = new WaterLayer(width, height);
    }

    // ============================================================
    // LAYER -> REGION -> VOXEL STORAGE
    // Every TerrainLayer owns its XY TerrainRegion grid.
    // Every TerrainRegion owns a dense 48 x 48 grid of 1 m³ voxels.
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

        if (_detailedColumns.TryGetValue(columnIndex, out ushort[]? detailed))
            return detailed[z];

        return z * LayerHeightUnits < _surfaceHeights[columnIndex]
            ? _surfaceMaterials[columnIndex]
            : (ushort)0;
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

        int columnIndex = GetColumnIndex(x, y);
        ushort[] detailed = GetOrCreateDetailedColumn(columnIndex);
        detailed[z] = materialId;

        _rangeCache[columnIndex] = null;
        RecalculateSurfaceHeight(columnIndex);
        TerrainVersion++;
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

        List<TileRange> ranges = new List<TileRange>(LayerCount);
        int runStart = 0;
        ushort runMaterial = GetMaterialId(globalX, globalY, 0);

        for (int z = 1; z <= LayerCount; z++)
        {
            if (z < LayerCount &&
                GetMaterialId(globalX, globalY, z) == runMaterial)
            {
                continue;
            }

            ranges.Add(new TileRange
            {
                StartZ = (ushort)(runStart * LayerHeightUnits),
                EndZ = (ushort)(z * LayerHeightUnits),
                MaterialId = runMaterial,
                State = runMaterial == 0 ? StateEmpty : StateSolid
            });

            if (z < LayerCount)
            {
                runStart = z;
                runMaterial = GetMaterialId(globalX, globalY, z);
            }
        }

        cached = ranges.ToArray();
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

        for (int i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].StartZ > ranges[i].EndZ)
            {
                throw new ArgumentException(
                    "StartZ не может быть больше EndZ.",
                    nameof(ranges));
            }
        }

        int columnIndex = GetColumnIndex(globalX, globalY);
        ClearColumn(globalX, globalY);

        ushort[] detailed = new ushort[LayerCount];
        _detailedColumns[columnIndex] = detailed;
        int surfaceHeight = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            ref readonly TileRange range = ref ranges[i];

            int startLayer = Math.Clamp(
                range.StartZ / LayerHeightUnits,
                0,
                LayerCount);

            int endLayer = Math.Clamp(
                (range.EndZ + LayerHeightUnits - 1) / LayerHeightUnits,
                0,
                LayerCount);

            ushort materialId =
                range.State == StateSolid ? range.MaterialId : (ushort)0;

            for (int z = startLayer; z < endLayer; z++)
                detailed[z] = materialId;

            if (range.State == StateSolid && materialId != 0)
            {
                surfaceHeight = Math.Max(
                    surfaceHeight,
                    Math.Min(range.EndZ, LayerCount * LayerHeightUnits));
            }
        }

        _surfaceHeights[columnIndex] = (ushort)surfaceHeight;
        _surfaceMaterials[columnIndex] = surfaceHeight > 0
            ? detailed[Math.Clamp(
                (surfaceHeight + LayerHeightUnits - 1) / LayerHeightUnits - 1,
                0,
                LayerCount - 1)]
            : (ushort)0;

        _rangeCache[columnIndex] = null;
        TerrainVersion++;
    }

    public void ClearTileRanges(
        int globalX,
        int globalY)
    {
        if (!IsInside(globalX, globalY))
            return;

        int columnIndex = GetColumnIndex(globalX, globalY);
        ClearColumn(globalX, globalY);
        TerrainVersion++;
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

        int columnIndex = GetColumnIndex(globalX, globalY);
        ushort[] detailed = GetOrCreateDetailedColumn(columnIndex);

        int startLayer = Math.Clamp(
            startZ / LayerHeightUnits,
            0,
            LayerCount);

        int endLayer = Math.Clamp(
            (endZ + LayerHeightUnits - 1) / LayerHeightUnits,
            0,
            LayerCount);

        ushort value = state == StateSolid ? materialId : (ushort)0;

        for (int z = startLayer; z < endLayer; z++)
            detailed[z] = value;

        if (state == StateSolid && value != 0)
        {
            int rangeEnd = Math.Min(
                endZ,
                LayerCount * LayerHeightUnits);

            if (rangeEnd > _surfaceHeights[columnIndex])
            {
                _surfaceHeights[columnIndex] = (ushort)rangeEnd;
                _surfaceMaterials[columnIndex] = value;
            }
        }
        else if (_surfaceHeights[columnIndex] > startZ)
        {
            RecalculateSurfaceHeight(columnIndex);
        }

        _rangeCache[columnIndex] = null;
        TerrainVersion++;
        return true;
    }

    public void SetSolidHeight(
        int globalX,
        int globalY,
        ushort height,
        ushort materialId)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        int columnIndex = GetColumnIndex(globalX, globalY);
        int clampedHeight = Math.Min(
            height,
            LayerCount * LayerHeightUnits);

        _detailedColumns.Remove(columnIndex);
        _surfaceHeights[columnIndex] =
            (ushort)(clampedHeight > 0 && materialId != 0 ? clampedHeight : 0);
        _surfaceMaterials[columnIndex] =
            clampedHeight > 0 && materialId != 0 ? materialId : (ushort)0;
        _rangeCache[columnIndex] = null;
        TerrainVersion++;
    }

    // ============================================================
    // SURFACE HEIGHT CACHE
    // Height remains available to movement / ballistic systems.
    // One layer represents one meter; height units remain decimeters.
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

    private void ClearColumn(
        int globalX,
        int globalY)
    {
        int columnIndex = GetColumnIndex(globalX, globalY);
        _detailedColumns.Remove(columnIndex);
        _surfaceHeights[columnIndex] = 0;
        _surfaceMaterials[columnIndex] = 0;
        _rangeCache[columnIndex] = null;
    }

    private void RecalculateSurfaceHeight(
        int columnIndex)
    {
        if (!_detailedColumns.TryGetValue(columnIndex, out ushort[]? detailed))
            return;

        for (int z = LayerCount - 1; z >= 0; z--)
        {
            ushort materialId = detailed[z];

            if (materialId == 0)
                continue;

            _surfaceHeights[columnIndex] =
                (ushort)((z + 1) * LayerHeightUnits);
            _surfaceMaterials[columnIndex] = materialId;
            return;
        }

        _surfaceHeights[columnIndex] = 0;
        _surfaceMaterials[columnIndex] = 0;
    }

    private ushort[] GetOrCreateDetailedColumn(int columnIndex)
    {
        if (_detailedColumns.TryGetValue(columnIndex, out ushort[]? detailed))
            return detailed;

        detailed = new ushort[LayerCount];

        int filledLayers =
            (_surfaceHeights[columnIndex] + LayerHeightUnits - 1) /
            LayerHeightUnits;
        ushort materialId = _surfaceMaterials[columnIndex];

        for (int z = 0; z < filledLayers; z++)
            detailed[z] = materialId;

        _detailedColumns.Add(columnIndex, detailed);
        return detailed;
    }

    private int GetColumnIndex(
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
