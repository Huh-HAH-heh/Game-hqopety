using System;

namespace Core.Map;

/// <summary>
/// A logical horizontal Z slice over the map's chunked 3D terrain regions.
/// Voxel storage belongs to TerrainRegion, not to a map-sized layer array.
/// </summary>
public sealed class TerrainLayer
{
    private readonly TerrainRegion?[] _regions;
    private readonly int _regionsX;
    private readonly int _regionsY;
    private readonly int _layerCount;

    public int ZLevel { get; }
    public int Width { get; }
    public int Height { get; }

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
            Math.Max(
                WorldMap.DefaultTerrainLayerCount,
                zLevel + 1),
            new TerrainRegion?[
                checked(
                    ((width + TerrainRegion.TilesPerSide - 1) /
                        TerrainRegion.TilesPerSide) *
                    ((height + TerrainRegion.TilesPerSide - 1) /
                        TerrainRegion.TilesPerSide))])
    {
    }

    internal TerrainLayer(
        int zLevel,
        int width,
        int height,
        int regionsX,
        int regionsY,
        int layerCount,
        TerrainRegion?[] regions)
    {
        if (zLevel < 0 || zLevel >= layerCount)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        if (regionsX <= 0)
            throw new ArgumentOutOfRangeException(nameof(regionsX));

        if (regionsY <= 0)
            throw new ArgumentOutOfRangeException(nameof(regionsY));

        if (regions.Length != checked(regionsX * regionsY))
            throw new ArgumentException(
                "Размер массива регионов не соответствует их сетке.",
                nameof(regions));

        ZLevel = zLevel;
        Width = width;
        Height = height;
        _regionsX = regionsX;
        _regionsY = regionsY;
        _layerCount = layerCount;
        _regions = regions;
    }

    public ushort GetMaterialId(
        int x,
        int y)
    {
        if (!IsInside(x, y))
            return 0;

        TerrainRegion? region =
            GetRegion(x, y);

        return region == null
            ? (ushort)0
            : region.GetMaterialId(
                x % TerrainRegion.TilesPerSide,
                y % TerrainRegion.TilesPerSide,
                ZLevel);
    }

    internal void SetMaterialId(
        int x,
        int y,
        ushort materialId)
    {
        if (!IsInside(x, y))
            throw new IndexOutOfRangeException();

        int regionX =
            x / TerrainRegion.TilesPerSide;

        int regionY =
            y / TerrainRegion.TilesPerSide;

        int regionIndex =
            regionX + regionY * _regionsX;

        TerrainRegion? region =
            _regions[regionIndex];

        if (region == null)
        {
            if (materialId == 0)
                return;

            region =
                new TerrainRegion(
                    regionX,
                    regionY,
                    _layerCount);

            _regions[regionIndex] = region;
        }

        region.SetMaterialId(
            x % TerrainRegion.TilesPerSide,
            y % TerrainRegion.TilesPerSide,
            ZLevel,
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

    internal void Clear()
    {
        for (int i = 0; i < _regions.Length; i++)
            _regions[i]?.ClearLayer(ZLevel);
    }

    private TerrainRegion? GetRegion(
        int x,
        int y)
    {
        int regionX =
            x / TerrainRegion.TilesPerSide;

        int regionY =
            y / TerrainRegion.TilesPerSide;

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
