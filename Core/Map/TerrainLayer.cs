using System;

namespace Core.Map;

/// <summary>
/// One editable Z level. Every layer owns its own grid of TerrainRegion chunks.
/// Each region stores a dense 48 x 48 array of 1 m x 1 m x 0.1 m cells.
/// </summary>
public sealed class TerrainLayer
{
    private readonly TerrainRegion[] _regions;
    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly int _regionsX;
    private readonly int _regionsY;

    public int ZLevel { get; }
    public int Width { get; }
    public int Height { get; }
    public int RegionsX => _regionsX;
    public int RegionsY => _regionsY;
    public ReadOnlySpan<TerrainRegion> Regions =>
        _regions;

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
        TerrainTileRegion?[]? sharedTileRegions)
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

        // Voxel chunks are explicit and dense: one region for every
        // XY chunk on every Z level. No sparse or lazy voxel arrays.
        _tileRegions =
            sharedTileRegions ??
            new TerrainTileRegion?[regionCount];

        _regions =
            new TerrainRegion[regionCount];

        for (int regionY = 0; regionY < _regionsY; regionY++)
        {
            for (int regionX = 0; regionX < _regionsX; regionX++)
            {
                int index =
                    regionX + regionY * _regionsX;

                _regions[index] =
                    new TerrainRegion(
                        regionX,
                        regionY,
                        ZLevel,
                        _tileRegions,
                        index);
            }
        }
    }

    public ushort GetMaterialId(
        int x,
        int y)
    {
        if (!IsInside(x, y))
            return 0;

        return GetRegionForCell(x, y).GetMaterialId(
            x % TerrainRegion.TilesPerSide,
            y % TerrainRegion.TilesPerSide);
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

    internal ushort GetMaterialIdAtIndex(
        int index)
    {
        if (index < 0 || index >= checked(Width * Height))
            throw new IndexOutOfRangeException();

        return GetMaterialId(
            index % Width,
            index / Width);
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
        TerrainRegion? region =
            GetRegion(regionX, regionY);

        return region ??
            throw new IndexOutOfRangeException();
    }

    internal void Clear()
    {
        for (int i = 0; i < _regions.Length; i++)
            _regions[i].Clear();
    }

    private TerrainRegion GetRegionForCell(
        int x,
        int y)
    {
        int regionX =
            x / TerrainRegion.TilesPerSide;

        int regionY =
            y / TerrainRegion.TilesPerSide;

        return _regions[
            regionX + regionY * _regionsX];
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
