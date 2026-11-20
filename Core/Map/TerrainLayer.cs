using System;

namespace Core.Map;

/// <summary>
/// Compatibility view of one Z plane in the authoritative SparseVoxelStore.
/// TerrainRegion objects are lightweight and created only when a caller requests
/// the legacy per-layer API; occupancy is never stored in these objects.
/// </summary>
public sealed class TerrainLayer
{
    private readonly TerrainRegion?[] _regions;
    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly int _regionsX;
    private readonly int _regionsY;
    private readonly WorldMap? _ownerWorldMap;

    public int ZLevel { get; }
    public int Width { get; }
    public int Height { get; }
    public int RegionsX => _regionsX;
    public int RegionsY => _regionsY;
    public ReadOnlySpan<TerrainRegion> Regions
    {
        get
        {
            for (int regionY = 0; regionY < _regionsY; regionY++)
            {
                for (int regionX = 0; regionX < _regionsX; regionX++)
                    GetOrCreateRegion(regionX, regionY);
            }

            return _regions!;
        }
    }

    public TerrainLayer(
        int zLevel,
        int width,
        int height)
        : this(
            zLevel,
            width,
            height,
            (width + TerrainRegion.TilesPerSide - 1) /
                TerrainRegion.TilesPerSide,
            (height + TerrainRegion.TilesPerSide - 1) /
                TerrainRegion.TilesPerSide,
            null)
    {
    }

    internal TerrainLayer(
        int zLevel,
        int width,
        int height,
        int regionsX,
        int regionsY,
        TerrainTileRegion?[]? sharedTileRegions,
        WorldMap? ownerWorldMap = null)
    {
        if (zLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        if (regionsX <= 0)
            throw new ArgumentOutOfRangeException(nameof(regionsX));

        if (regionsY <= 0)
            throw new ArgumentOutOfRangeException(nameof(regionsY));

        int expectedRegionsX =
            (width + TerrainRegion.TilesPerSide - 1) /
            TerrainRegion.TilesPerSide;

        int expectedRegionsY =
            (height + TerrainRegion.TilesPerSide - 1) /
            TerrainRegion.TilesPerSide;

        if (regionsX != expectedRegionsX ||
            regionsY != expectedRegionsY)
        {
            throw new ArgumentException(
                "Сетка регионов не соответствует размеру слоя.");
        }

        int regionCount =
            checked(regionsX * regionsY);

        if (sharedTileRegions != null &&
            sharedTileRegions.Length != regionCount)
        {
            throw new ArgumentException(
                "Массив метаданных регионов имеет неверный размер.",
                nameof(sharedTileRegions));
        }

        ZLevel = zLevel;
        Width = width;
        Height = height;
        _regionsX = regionsX;
        _regionsY = regionsY;
        _ownerWorldMap = ownerWorldMap;

        // A Z layer is a view over the sparse 3D voxel store. XY region
        // facades are materialized only when a legacy caller requests one.
        _tileRegions =
            sharedTileRegions ??
            new TerrainTileRegion?[regionCount];
        _regions = new TerrainRegion?[regionCount];
    }

    public ushort GetMaterialId(
        int x,
        int y)
    {
        if (!IsInside(x, y))
            return 0;

        if (_ownerWorldMap != null)
            return _ownerWorldMap.GetMaterialId(x, y, ZLevel);

        return GetStoredMaterialId(x, y);
    }

    internal void SetMaterialId(
        int x,
        int y,
        ushort materialId)
    {
        if (!IsInside(x, y))
            throw new IndexOutOfRangeException();

        if (_ownerWorldMap != null)
        {
            _ownerWorldMap.SetMaterialId(x, y, ZLevel, materialId);
            return;
        }

        GetRegionForCell(x, y).SetMaterialId(
            x % TerrainRegion.TilesPerSide,
            y % TerrainRegion.TilesPerSide,
            materialId);
    }

    internal ushort GetMaterialIdAtIndex(
        int index)
    {
        if (index < 0 || index >= checked(Width * Height))
            throw new IndexOutOfRangeException();

        int x = index % Width;
        int y = index / Width;
        return _ownerWorldMap != null
            ? _ownerWorldMap.GetMaterialId(x, y, ZLevel)
            : GetStoredMaterialId(x, y);
    }

    private ushort GetStoredMaterialId(int x, int y)
    {
        return GetRegionForCell(x, y).GetMaterialId(
            x % TerrainRegion.TilesPerSide,
            y % TerrainRegion.TilesPerSide);
    }

    internal void SetMaterialIdAtIndex(
        int index,
        ushort materialId)
    {
        if (index < 0 || index >= checked(Width * Height))
            throw new IndexOutOfRangeException();

        SetMaterialId(
            index % Width,
            index / Width,
            materialId);
    }

    public TerrainRegion? GetRegion(
        int regionX,
        int regionY)
    {
        if (regionX < 0 ||
            regionX >= _regionsX ||
            regionY < 0 ||
            regionY >= _regionsY)
        {
            return null;
        }

        return _regions[
            regionX + regionY * _regionsX];
    }

    public TerrainRegion GetOrCreateRegion(
        int regionX,
        int regionY)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= _regionsX || regionY >= _regionsY)
        {
            throw new IndexOutOfRangeException();
        }

        int index = regionX + regionY * _regionsX;
        return _regions[index] ??= new TerrainRegion(
            regionX, regionY, ZLevel, _tileRegions, index, _ownerWorldMap);
    }

    internal void Clear()
    {
        for (int i = 0; i < _regions.Length; i++)
            _regions[i]?.Clear();
    }

    private TerrainRegion GetRegionForCell(
        int x,
        int y)
    {
        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        return GetOrCreateRegion(regionX, regionY);
    }

    private bool IsInside(
        int x,
        int y)
    {
        return
            x >= 0 &&
            y >= 0 &&
            x < Width &&
            y < Height;
    }
}
