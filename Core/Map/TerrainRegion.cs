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

    // The region is a logical 48 x 48 x Z voxel volume.
    // Each horizontal Z slice is allocated only when it contains a solid voxel.
    private readonly ushort[]?[] _materialLayers;

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
        _materialLayers = new ushort[]?[layerCount];

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

        ushort[]? layer =
            _materialLayers[z];

        return layer == null
            ? (ushort)0
            : layer[localX + localY * TilesPerSide];
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

        ushort[]? layer =
            _materialLayers[z];

        return layer == null
            ? (ushort)0
            : layer[localIndex];
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

        ushort[]? layer =
            _materialLayers[z];

        if (layer == null)
        {
            if (materialId == 0)
                return;

            layer =
                new ushort[TotalTiles];

            _materialLayers[z] = layer;
        }

        layer[localIndex] = materialId;
    }

    internal void ClearColumn(
        int localIndex)
    {
        if (localIndex < 0 || localIndex >= TotalTiles)
            throw new IndexOutOfRangeException();

        for (int z = 0; z < _layerCount; z++)
        {
            ushort[]? layer =
                _materialLayers[z];

            if (layer != null)
                layer[localIndex] = 0;
        }
    }

    internal void ClearLayer(
        int z)
    {
        if (z < 0 || z >= _layerCount)
            throw new IndexOutOfRangeException();

        // Releasing the slice also releases its 48 x 48 storage.
        _materialLayers[z] = null;
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
