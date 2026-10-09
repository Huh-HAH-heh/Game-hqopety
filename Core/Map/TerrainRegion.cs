namespace Core.Map;

public sealed class TerrainRegion
{
    public const int TilesPerSide = 48;

    public const int TotalTiles =
        TilesPerSide * TilesPerSide;

    public int RegionX { get; }
    public int RegionY { get; }

    private readonly Tile[] _tiles;

    public TerrainRegion(
        int regionX,
        int regionY)
    {
        RegionX = regionX;
        RegionY = regionY;

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

    public ref Tile GetLocalTile(
        int localX,
        int localY)
    {
        return ref _tiles[
            localX + localY * TilesPerSide];
    }

    public ReadOnlySpan<Tile> Tiles =>
        _tiles.AsSpan();
}
