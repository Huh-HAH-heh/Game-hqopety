using System;

namespace Core.Map;

/// <summary>
/// A Z-level view over terrain. Ordinary terrain is resolved from the
/// compact surface column; sparse regions are retained for region metadata
/// and explicit low-level edits.
/// </summary>
public sealed class TerrainLayer
{
    private readonly TerrainRegion[] _regions;
    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly Func<int, int, ushort>? _materialProvider;
    private readonly int _regionsX;
    private readonly int _regionsY;

    public int ZLevel { get; }
    public int Width { get; }
    public int Height { get; }
    public int RegionsX => _regionsX;
    public int RegionsY => _regionsY;

    // Only materialized regions contain objects; unused entries are null.
    public ReadOnlySpan<TerrainRegion> Regions => _regions;

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
        Func<int, int, ushort>? materialProvider = null)
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

        int regionCount = checked(regionsX * regionsY);

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
        _tileRegions =
            sharedTileRegions ??
            new TerrainTileRegion?[regionCount];
        _regions = new TerrainRegion[regionCount];
        _materialProvider = materialProvider;
    }

    public ushort GetMaterialId(int x, int y)
    {
        if (!IsInside(x, y))
            return 0;

        if (_materialProvider != null)
            return _materialProvider(x, y);

        int regionIndex =
            x / TerrainRegion.TilesPerSide +
            (y / TerrainRegion.TilesPerSide) * _regionsX;

        TerrainRegion? region = _regions[regionIndex];

        return region?.GetMaterialId(
            x % TerrainRegion.TilesPerSide,
            y % TerrainRegion.TilesPerSide) ?? 0;
    }

    internal void SetMaterialId(
        int x,
        int y,
        ushort materialId)
    {
        if (!IsInside(x, y))
            throw new IndexOutOfRangeException();

        GetRegionForCell(x, y).SetMaterialId(
            x % TerrainRegion.TilesPerSide,
            y % TerrainRegion.TilesPerSide,
            materialId);
    }

    internal ushort GetMaterialIdAtIndex(int index)
    {
        if (index < 0 || index >= checked(Width * Height))
            throw new IndexOutOfRangeException();

        return GetMaterialId(index % Width, index / Width);
    }

    internal void SetMaterialIdAtIndex(
        int index,
        ushort materialId)
    {
        if (index < 0 || index >= checked(Width * Height))
            throw new IndexOutOfRangeException();

        SetMaterialId(index % Width, index / Width, materialId);
    }

    public TerrainRegion? GetRegion(int regionX, int regionY)
    {
        if (regionX < 0 || regionX >= _regionsX ||
            regionY < 0 || regionY >= _regionsY)
        {
            return null;
        }

        return _regions[regionX + regionY * _regionsX];
    }

    public TerrainRegion GetOrCreateRegion(
        int regionX,
        int regionY)
    {
        if (regionX < 0 || regionX >= _regionsX ||
            regionY < 0 || regionY >= _regionsY)
        {
            throw new IndexOutOfRangeException();
        }

        int index = regionX + regionY * _regionsX;
        TerrainRegion? region = _regions[index];

        if (region != null)
            return region;

        region = new TerrainRegion(
            regionX,
            regionY,
            ZLevel,
            _tileRegions,
            index);

        _regions[index] = region;
        return region;
    }

    internal void Clear()
    {
        for (int i = 0; i < _regions.Length; i++)
            _regions[i]?.Clear();
    }

    private TerrainRegion GetRegionForCell(int x, int y)
    {
        return GetOrCreateRegion(
            x / TerrainRegion.TilesPerSide,
            y / TerrainRegion.TilesPerSide);
    }

    private bool IsInside(int x, int y)
    {
        return x >= 0 && y >= 0 && x < Width && y < Height;
    }
}
