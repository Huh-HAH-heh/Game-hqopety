using System;

namespace Core.Map;

/// <summary>
/// Shared navigation/tile metadata for one XY region.
/// Voxel material data belongs to a TerrainRegion inside a TerrainLayer.
/// </summary>
public sealed class TerrainTileRegion
{
    public int RegionX { get; }
    public int RegionY { get; }

    private readonly Tile[] _tiles =
        new Tile[TerrainRegion.TotalTiles];

    public TerrainTileRegion(
        int regionX,
        int regionY)
    {
        RegionX = regionX;
        RegionY = regionY;

        for (int y = 0; y < TerrainRegion.TilesPerSide; y++)
        {
            for (int x = 0; x < TerrainRegion.TilesPerSide; x++)
            {
                _tiles[x + y * TerrainRegion.TilesPerSide] =
                    new Tile((byte)x, (byte)y);
            }
        }
    }

    public ref Tile GetLocalTile(
        int localX,
        int localY)
    {
        if (localX < 0 ||
            localY < 0 ||
            localX >= TerrainRegion.TilesPerSide ||
            localY >= TerrainRegion.TilesPerSide)
        {
            throw new IndexOutOfRangeException();
        }

        return ref _tiles[
            localX + localY * TerrainRegion.TilesPerSide];
    }

    public ReadOnlySpan<Tile> Tiles =>
        _tiles.AsSpan();
}
