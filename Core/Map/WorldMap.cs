using System;

namespace Core.Map;

public sealed class WorldMap
{
    public const byte StateEmpty = 0;
    public const byte StateSolid = 1;

    public int RegionsX { get; }
    public int RegionsY { get; }

    public int TileWidth =>
        RegionsX *
        TerrainRegion.TilesPerSide;

    public int TileHeight =>
        RegionsY *
        TerrainRegion.TilesPerSide;

    public int MaxTileX =>
        TileWidth - 1;

    public int MaxTileY =>
        TileHeight - 1;

    private readonly TerrainRegion?[] _regions;

    public WorldMap(
        int regionsX = 16,
        int regionsY = 16)
    {
        if (regionsX <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(regionsX));

        if (regionsY <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(regionsY));

        RegionsX = regionsX;
        RegionsY = regionsY;

        _regions =
            new TerrainRegion?[
                regionsX * regionsY];
    }

    // ============================================================
    // REGION
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

    // ============================================================
    // TILE
    // ============================================================

    public bool TryGetTile(
        int globalX,
        int globalY,
        out Tile tile)
    {
        tile = default;

        if (!IsInside(
                globalX,
                globalY))
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
        if (!IsInside(
                globalX,
                globalY))
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

    // ============================================================
    // RANGES
    // ============================================================

    public ReadOnlySpan<TileRange> GetTileRanges(
        int globalX,
        int globalY)
    {
        if (!IsInside(
                globalX,
                globalY))
        {
            return ReadOnlySpan<TileRange>.Empty;
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
            return ReadOnlySpan<TileRange>.Empty;

        int localX =
            globalX %
            TerrainRegion.TilesPerSide;

        int localY =
            globalY %
            TerrainRegion.TilesPerSide;

        return region.GetRanges(
            localX,
            localY);
    }

    public void SetRanges(
        int globalX,
        int globalY,
        ReadOnlySpan<TileRange> ranges)
    {
        if (!IsInside(
                globalX,
                globalY))
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

        region.SetRanges(
            localX,
            localY,
            ranges);
    }

    public void ClearTileRanges(
        int globalX,
        int globalY)
    {
        if (!IsInside(
                globalX,
                globalY))
        {
            return;
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
            return;

        int localX =
            globalX %
            TerrainRegion.TilesPerSide;

        int localY =
            globalY %
            TerrainRegion.TilesPerSide;

        region.ClearRanges(
            localX,
            localY);
    }

    public bool TryAddRange(
        int globalX,
        int globalY,
        ushort startZ,
        ushort endZ,
        ushort materialId,
        byte state)
    {
        if (!IsInside(
                globalX,
                globalY))
        {
            return false;
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

        return region.TryAddRange(
            localX,
            localY,
            startZ,
            endZ,
            materialId,
            state);
    }

    // ============================================================
    // BASIC TERRAIN
    // ============================================================

    public void SetSolidHeight(
        int globalX,
        int globalY,
        ushort height,
        ushort materialId)
    {
        if (!IsInside(
                globalX,
                globalY))
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

        region.ClearRanges(
            localX,
            localY);

        if (height == 0)
            return;

        region.TryAddRange(
            localX,
            localY,
            0,
            height,
            materialId,
            StateSolid);
    }

    // ============================================================
    // SURFACE
    // ============================================================

    public ushort GetSurfaceHeightUnits(
        int globalX,
        int globalY)
    {
        if (!IsInside(
                globalX,
                globalY))
        {
            return 0;
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
            return 0;

        int localX =
            globalX %
            TerrainRegion.TilesPerSide;

        int localY =
            globalY %
            TerrainRegion.TilesPerSide;

        return region.GetSurfaceHeightUnits(
            localX,
            localY);
    }

    public float GetSurfaceHeight(
        int globalX,
        int globalY)
    {
        return
            GetSurfaceHeightUnits(
                globalX,
                globalY) *
            0.1f;
    }

    // ============================================================
    // INTERNAL
    // ============================================================

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
