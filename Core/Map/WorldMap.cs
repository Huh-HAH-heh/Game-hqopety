using System;
using System.Collections.Generic;

namespace Core.Map;

public sealed class WorldMap
{
    public const byte StateEmpty = 0;
    public const byte StateSolid = 1;

    public const int DefaultTerrainLayerCount = 50;
    public const ushort LayerHeightUnits = 10;

    // 15 map tiles span roughly 5 m; macro blocks are 5 m tall.
    public const int MacroBlockTileSpan = 15;
    public const ushort MacroBlockHeightUnits = 5 * LayerHeightUnits;

    public int RegionsX { get; }
    public int RegionsY { get; }

    public int TileWidth =>
        RegionsX * TerrainRegion.TilesPerSide;

    public int TileHeight =>
        RegionsY * TerrainRegion.TilesPerSide;

    public int MaxTileX => TileWidth - 1;
    public int MaxTileY => TileHeight - 1;

    public int LayerCount => _layers.Length;

    public int MacroBlocksX =>
        (TileWidth + MacroBlockTileSpan - 1) / MacroBlockTileSpan;

    public int MacroBlocksY =>
        (TileHeight + MacroBlockTileSpan - 1) / MacroBlockTileSpan;

    public long TerrainVersion { get; private set; }

    public WaterLayer Water { get; }

    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly TerrainLayer[] _layers;
    private sealed class DetailedTerrainColumn
    {
        public readonly ushort[] Materials;
        public ushort SurfaceHeight;
        public ushort SurfaceMaterialId;

        public DetailedTerrainColumn(int layerCount)
        {
            Materials = new ushort[layerCount];
        }
    }

    private readonly ushort[] _macroSurfaceHeights;
    private readonly ushort[] _macroSurfaceMaterials;
    private readonly Dictionary<int, DetailedTerrainColumn> _detailedColumns = new();
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
        int macroColumnCount = checked(MacroBlocksX * MacroBlocksY);
        int regionCount = checked(regionsX * regionsY);

        _tileRegions = new TerrainTileRegion?[regionCount];
        _macroSurfaceHeights = new ushort[macroColumnCount];
        _macroSurfaceMaterials = new ushort[macroColumnCount];
        _rangeCache = new TileRange[]?[columnCount];
        _layers = new TerrainLayer[layerCount];

        for (int z = 0; z < layerCount; z++)
        {
            int layerZ = z;
            _layers[z] = new TerrainLayer(
                z, width, height, regionsX, regionsY, _tileRegions,
                (x, y) => GetMaterialId(x, y, layerZ));
        }

        Water = new WaterLayer(width, height);
    }

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

    public ushort GetMaterialId(int x, int y, int z)
    {
        if (!IsInside(x, y) || z < 0 || z >= LayerCount)
            return 0;

        int columnIndex = GetColumnIndex(x, y);
        if (_detailedColumns.TryGetValue(columnIndex, out DetailedTerrainColumn? detailed))
            return detailed.Materials[z];

        int macroIndex = GetMacroIndex(x, y);
        return z * LayerHeightUnits < _macroSurfaceHeights[macroIndex]
            ? _macroSurfaceMaterials[macroIndex]
            : (ushort)0;
    }

    // Renderer representation: draw one face per 5 m macro-block,
    // while detailed columns continue to render every 1 m level.
    public ushort GetRenderMaterialId(int x, int y, int z)
    {
        if (!IsInside(x, y) || z < 0 || z >= LayerCount)
            return 0;

        int columnIndex = GetColumnIndex(x, y);
        if (_detailedColumns.TryGetValue(columnIndex, out DetailedTerrainColumn? detailed))
            return detailed.Materials[z];

        int blockStep = MacroBlockHeightUnits / LayerHeightUnits;
        if (z % blockStep != 0)
            return 0;

        return GetMaterialId(x, y, z);
    }

    public bool IsDetailedColumn(int x, int y)
    {
        return IsInside(x, y) &&
            _detailedColumns.ContainsKey(GetColumnIndex(x, y));
    }

    public void SetMaterialId(int x, int y, int z, ushort materialId)
    {
        if (!IsInside(x, y) || z < 0 || z >= LayerCount)
            throw new IndexOutOfRangeException();

        int columnIndex = GetColumnIndex(x, y);
        DetailedTerrainColumn detailed = GetOrCreateDetailedColumn(columnIndex);
        detailed.Materials[z] = materialId;
        _rangeCache[columnIndex] = null;
        RecalculateSurfaceHeight(columnIndex);
        TerrainVersion++;
    }

    public int GetSurfaceLayer(int x, int y)
    {
        ushort height = GetSurfaceHeightUnits(x, y);
        if (height == 0)
            return -1;

        int layer = (height + LayerHeightUnits - 1) / LayerHeightUnits - 1;
        return Math.Min(LayerCount - 1, layer);
    }

    public int GetHighestOccupiedLayer()
    {
        int highest = -1;
        for (int i = 0; i < _macroSurfaceHeights.Length; i++)
        {
            ushort height = _macroSurfaceHeights[i];
            if (height == 0)
                continue;

            int layer = (height + LayerHeightUnits - 1) / LayerHeightUnits - 1;
            if (layer > highest)
                highest = layer;
        }

        foreach (DetailedTerrainColumn column in _detailedColumns.Values)
        {
            int layer = (column.SurfaceHeight + LayerHeightUnits - 1) / LayerHeightUnits - 1;
            if (layer > highest)
                highest = layer;
        }

        return Math.Min(LayerCount - 1, highest);
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

    public ReadOnlySpan<TileRange> GetTileRanges(int globalX, int globalY)
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
            if (z < LayerCount && GetMaterialId(globalX, globalY, z) == runMaterial)
                continue;

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

    public void SetRanges(int globalX, int globalY, ReadOnlySpan<TileRange> ranges)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        for (int i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].StartZ > ranges[i].EndZ)
                throw new ArgumentException("StartZ не может быть больше EndZ.", nameof(ranges));
        }

        int columnIndex = GetColumnIndex(globalX, globalY);
        DetailedTerrainColumn detailed = ClearColumn(globalX, globalY);
        int surfaceHeight = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            ref readonly TileRange range = ref ranges[i];
            int startLayer = Math.Clamp(range.StartZ / LayerHeightUnits, 0, LayerCount);
            int endLayer = Math.Clamp(
                (range.EndZ + LayerHeightUnits - 1) / LayerHeightUnits, 0, LayerCount);
            ushort materialId = range.State == StateSolid ? range.MaterialId : (ushort)0;

            for (int z = startLayer; z < endLayer; z++)
                detailed.Materials[z] = materialId;

            if (range.State == StateSolid && materialId != 0)
                surfaceHeight = Math.Max(surfaceHeight,
                    Math.Min(range.EndZ, LayerCount * LayerHeightUnits));
        }

        detailed.SurfaceHeight = (ushort)surfaceHeight;
        detailed.SurfaceMaterialId = surfaceHeight > 0
            ? detailed.Materials[Math.Clamp(
                (surfaceHeight + LayerHeightUnits - 1) / LayerHeightUnits - 1,
                0, LayerCount - 1)]
            : (ushort)0;

        _rangeCache[columnIndex] = null;
        TerrainVersion++;
    }

    public void ClearTileRanges(int globalX, int globalY)
    {
        if (!IsInside(globalX, globalY))
            return;

        ClearColumn(globalX, globalY);
        TerrainVersion++;
    }

    public bool TryAddRange(
        int globalX, int globalY, ushort startZ, ushort endZ,
        ushort materialId, byte state)
    {
        if (!IsInside(globalX, globalY))
            return false;
        if (startZ > endZ)
            throw new ArgumentException("StartZ не может быть больше EndZ.");

        int columnIndex = GetColumnIndex(globalX, globalY);
        DetailedTerrainColumn detailed = GetOrCreateDetailedColumn(columnIndex);
        int startLayer = Math.Clamp(startZ / LayerHeightUnits, 0, LayerCount);
        int endLayer = Math.Clamp(
            (endZ + LayerHeightUnits - 1) / LayerHeightUnits, 0, LayerCount);
        ushort value = state == StateSolid ? materialId : (ushort)0;

        for (int z = startLayer; z < endLayer; z++)
            detailed.Materials[z] = value;

        if (state == StateSolid && value != 0)
        {
            int rangeEnd = Math.Min(endZ, LayerCount * LayerHeightUnits);
            if (rangeEnd > detailed.SurfaceHeight)
            {
                detailed.SurfaceHeight = (ushort)rangeEnd;
                detailed.SurfaceMaterialId = value;
            }
        }
        else if (detailed.SurfaceHeight > startZ)
        {
            RecalculateSurfaceHeight(columnIndex);
        }

        _rangeCache[columnIndex] = null;
        TerrainVersion++;
        return true;
    }

    public void SetSolidHeight(int globalX, int globalY, ushort height, ushort materialId)
    {
        if (!IsInside(globalX, globalY))
            throw new IndexOutOfRangeException();

        int columnIndex = GetColumnIndex(globalX, globalY);
        int clampedHeight = Math.Min(height, LayerCount * LayerHeightUnits);
        DetailedTerrainColumn detailed = new DetailedTerrainColumn(LayerCount);

        if (clampedHeight > 0 && materialId != 0)
        {
            int filledLayers = (clampedHeight + LayerHeightUnits - 1) / LayerHeightUnits;
            for (int z = 0; z < filledLayers; z++)
                detailed.Materials[z] = materialId;

            detailed.SurfaceHeight = (ushort)clampedHeight;
            detailed.SurfaceMaterialId = materialId;
        }

        _detailedColumns[columnIndex] = detailed;
        _rangeCache[columnIndex] = null;
        TerrainVersion++;
    }

    public void SetMacroSolidHeight(
        int macroX, int macroY, ushort height, ushort materialId)
    {
        if (macroX < 0 || macroX >= MacroBlocksX ||
            macroY < 0 || macroY >= MacroBlocksY)
            throw new IndexOutOfRangeException();

        int macroIndex = macroX + macroY * MacroBlocksX;
        int clampedHeight = Math.Min(height, LayerCount * LayerHeightUnits);
        int blockHeight = clampedHeight > 0 && materialId != 0
            ? Math.Min(
                ((clampedHeight + MacroBlockHeightUnits - 1) / MacroBlockHeightUnits) *
                MacroBlockHeightUnits,
                LayerCount * LayerHeightUnits)
            : 0;

        _macroSurfaceHeights[macroIndex] = (ushort)blockHeight;
        _macroSurfaceMaterials[macroIndex] = blockHeight > 0 ? materialId : (ushort)0;

        if (_detailedColumns.Count > 0)
        {
            int startX = macroX * MacroBlockTileSpan;
            int startY = macroY * MacroBlockTileSpan;
            int endX = Math.Min(TileWidth, startX + MacroBlockTileSpan);
            int endY = Math.Min(TileHeight, startY + MacroBlockTileSpan);
            for (int y = startY; y < endY; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    int index = GetColumnIndex(x, y);
                    _detailedColumns.Remove(index);
                    _rangeCache[index] = null;
                }
            }
        }

        TerrainVersion++;
    }

    // ============================================================
    // SURFACE HEIGHT CACHE
    // Height remains available to movement / ballistic systems.
    // One layer represents one meter; height units remain decimeters.
    // ============================================================

    public ushort GetSurfaceHeightUnits(int globalX, int globalY)
    {
        if (!IsInside(globalX, globalY))
            return 0;

        int columnIndex = GetColumnIndex(globalX, globalY);
        if (_detailedColumns.TryGetValue(columnIndex, out DetailedTerrainColumn? detailed))
            return detailed.SurfaceHeight;

        return _macroSurfaceHeights[GetMacroIndex(globalX, globalY)];
    }

    public float GetSurfaceHeight(int globalX, int globalY)
    {
        return GetSurfaceHeightUnits(globalX, globalY) * 0.1f;
    }

    private DetailedTerrainColumn ClearColumn(int globalX, int globalY)
    {
        int columnIndex = GetColumnIndex(globalX, globalY);
        DetailedTerrainColumn detailed = new DetailedTerrainColumn(LayerCount);
        _detailedColumns[columnIndex] = detailed;
        _rangeCache[columnIndex] = null;
        return detailed;
    }

    private void RecalculateSurfaceHeight(int columnIndex)
    {
        if (!_detailedColumns.TryGetValue(columnIndex, out DetailedTerrainColumn? detailed))
            return;

        for (int z = LayerCount - 1; z >= 0; z--)
        {
            ushort materialId = detailed.Materials[z];
            if (materialId == 0)
                continue;

            detailed.SurfaceHeight = (ushort)((z + 1) * LayerHeightUnits);
            detailed.SurfaceMaterialId = materialId;
            return;
        }

        detailed.SurfaceHeight = 0;
        detailed.SurfaceMaterialId = 0;
    }

    private DetailedTerrainColumn GetOrCreateDetailedColumn(int columnIndex)
    {
        if (_detailedColumns.TryGetValue(columnIndex, out DetailedTerrainColumn? detailed))
            return detailed;

        detailed = new DetailedTerrainColumn(LayerCount);
        int x = columnIndex % TileWidth;
        int y = columnIndex / TileWidth;
        int macroIndex = GetMacroIndex(x, y);
        int filledLayers =
            (_macroSurfaceHeights[macroIndex] + LayerHeightUnits - 1) / LayerHeightUnits;
        ushort materialId = _macroSurfaceMaterials[macroIndex];

        for (int z = 0; z < filledLayers; z++)
            detailed.Materials[z] = materialId;

        detailed.SurfaceHeight = _macroSurfaceHeights[macroIndex];
        detailed.SurfaceMaterialId = materialId;
        _detailedColumns.Add(columnIndex, detailed);
        return detailed;
    }

    private int GetMacroIndex(int x, int y)
    {
        return x / MacroBlockTileSpan +
            (y / MacroBlockTileSpan) * MacroBlocksX;
    }

    private int GetColumnIndex(int x, int y)
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
