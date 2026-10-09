using System;

namespace Core.Map;

public sealed class TerrainRegion
{
    public const int TilesPerSide = 48;

    public const int TotalTiles =
        TilesPerSide * TilesPerSide;

    public int RegionX { get; }
    public int RegionY { get; }

    private readonly int _layerCount;
    private readonly Tile[] _tiles;

    // Packed as one contiguous 48 x 48 x Z voxel block.
    // Allocated only when a non-empty voxel is written.
    private ushort[]? _materialIds;

    public TerrainRegion(
        int regionX,
        int regionY,
        int layerCount = WorldMap.DefaultTerrainLayerCount)
    {
        if (layerCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(layerCount));

        RegionX = regionX;
        RegionY = regionY;
        _layerCount = layerCount;

        _tiles = new Tile[TotalTiles];

        for (int y = 0; y < TilesPerSide; y++)
        {
            for (int x = 0; x < TilesPerSide; x++)
            {
                _tiles[x + y * TilesPerSide] =
                    new Tile((byte)x, (byte)y);
            }
        }
    }

    public ushort GetMaterialId(
        int localX,
        int localY,
        int z)
    {
        if (!IsInside(localX, localY) ||
            z < 0 ||
            z >= _layerCount)
        {
            return 0;
        }

        ushort[]? data = _materialIds;

        if (data == null)
            return 0;

        int localIndex =
            localX + localY * TilesPerSide;

        return data[z * TotalTiles + localIndex];
    }

    internal ushort GetMaterialIdAtIndex(
        int localIndex,
        int z)
    {
        if (localIndex < 0 ||
            localIndex >= TotalTiles ||
            z < 0 ||
            z >= _layerCount)
        {
            return 0;
        }

        ushort[]? data = _materialIds;

        return data == null
            ? (ushort)0
            : data[z * TotalTiles + localIndex];
    }

    internal void SetMaterialId(
        int localX,
        int localY,
        int z,
        ushort materialId)
    {
        if (!IsInside(localX, localY))
            throw new IndexOutOfRangeException();

        SetMaterialIdAtIndex(
            localX + localY * TilesPerSide,
            z,
            materialId);
    }

    internal void SetMaterialIdAtIndex(
        int localIndex,
        int z,
        ushort materialId)
    {
        if (localIndex < 0 ||
            localIndex >= TotalTiles ||
            z < 0 ||
            z >= _layerCount)
        {
            throw new IndexOutOfRangeException();
        }

        if (_materialIds == null)
        {
            if (materialId == 0)
                return;

            _materialIds =
                new ushort[checked(TotalTiles * _layerCount)];
        }

        _materialIds[z * TotalTiles + localIndex] =
            materialId;
    }

    internal void ClearColumn(
        int localIndex)
    {
        if (localIndex < 0 || localIndex >= TotalTiles)
            throw new IndexOutOfRangeException();

        ushort[]? data = _materialIds;

        if (data == null)
            return;

        for (int z = 0; z < _layerCount; z++)
            data[z * TotalTiles + localIndex] = 0;
    }

    internal void ClearLayer(
        int z)
    {
        if (z < 0 || z >= _layerCount)
            throw new IndexOutOfRangeException();

        ushort[]? data = _materialIds;

        if (data == null)
            return;

        Array.Clear(
            data,
            z * TotalTiles,
            TotalTiles);
    }

    public ref Tile GetLocalTile(
        int localX,
        int localY)
    {
        if (!IsInside(localX, localY))
            throw new IndexOutOfRangeException();

        return ref _tiles[
            localX + localY * TilesPerSide];
    }

    public ReadOnlySpan<Tile> Tiles =>
        _tiles.AsSpan();

    private static bool IsInside(
        int x,
        int y)
    {
        return
            x >= 0 &&
            y >= 0 &&
            x < TilesPerSide &&
            y < TilesPerSide;
    }
}
