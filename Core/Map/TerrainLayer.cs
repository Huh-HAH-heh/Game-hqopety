namespace Core.Map;

public sealed class TerrainLayer
{
    public int ZLevel { get; }

    public int RegionsX { get; }
    public int RegionsY { get; }

    private readonly TerrainRegion?[] _regions;

    public TerrainLayer(
        int zLevel,
        int regionsX,
        int regionsY)
    {
        if (regionsX <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(regionsX));

        if (regionsY <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(regionsY));

        ZLevel = zLevel;

        RegionsX = regionsX;
        RegionsY = regionsY;

        _regions =
            new TerrainRegion?[
                regionsX *
                regionsY];
    }

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

        return _regions[
            regionX +
            regionY * RegionsX];
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

        int index =
            regionX +
            regionY * RegionsX;

        TerrainRegion? region =
            _regions[index];

        if (region != null)
            return region;

        region =
            new TerrainRegion(
                regionX,
                regionY);

        _regions[index] =
            region;

        return region;
    }

    public bool TryGetTile(
        int globalX,
        int globalY,
        out Tile tile)
    {
        tile = default;

        int tileWidth =
            RegionsX *
            TerrainRegion.TilesPerSide;

        int tileHeight =
            RegionsY *
            TerrainRegion.TilesPerSide;

        if (globalX < 0 ||
            globalY < 0 ||
            globalX >= tileWidth ||
            globalY >= tileHeight)
        {
            return false;
        }

        int regionX =
            globalX /
            TerrainRegion.TilesPerSide;

        int regionY =
            globalY /
            TerrainRegion.TilesPerSide;

        TerrainRegion? region =
            GetRegion(
                regionX,
                regionY);

        if (region == null)
            return false;

        int localX =
            globalX %
            TerrainRegion.TilesPerSide;

        int localY =
            globalY %
            TerrainRegion.TilesPerSide;

        tile =
            region.GetLocalTile(
                localX,
                localY);

        return true;
    }

    public ref Tile GetOrCreateTile(
        int globalX,
        int globalY)
    {
        int tileWidth =
            RegionsX *
            TerrainRegion.TilesPerSide;

        int tileHeight =
            RegionsY *
            TerrainRegion.TilesPerSide;

        if (globalX < 0 ||
            globalY < 0 ||
            globalX >= tileWidth ||
            globalY >= tileHeight)
        {
            throw new IndexOutOfRangeException();
        }

        int regionX =
            globalX /
            TerrainRegion.TilesPerSide;

        int regionY =
            globalY /
            TerrainRegion.TilesPerSide;

        TerrainRegion region =
            GetOrCreateRegion(
                regionX,
                regionY);

        int localX =
            globalX %
            TerrainRegion.TilesPerSide;

        int localY =
            globalY %
            TerrainRegion.TilesPerSide;

        return ref region.GetLocalTile(
            localX,
            localY);
    }
}