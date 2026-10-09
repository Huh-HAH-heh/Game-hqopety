using System;

namespace Core.Map;

/// <summary>
/// A region owned by exactly one TerrainLayer (one Z level).
/// Each entry is a 1 x 1 x 1 meter voxel's material ID.
/// </summary>
public sealed class TerrainRegion
{
    public const int TilesPerSide = 48;

    public const int TotalTiles =
        TilesPerSide * TilesPerSide;

    public int RegionX { get; }
    public int RegionY { get; }
    public int ZLevel { get; }

    private readonly ushort[] _materialIds =
        new ushort[TotalTiles];

    private readonly TerrainTileRegion?[] _tileRegions;
    private readonly int _regionIndex;

    internal TerrainRegion(
        int regionX,
        int regionY,
        int zLevel,
        TerrainTileRegion?[] tileRegions,
        int regionIndex)
    {
        if (regionX < 0)
            throw new ArgumentOutOfRangeException(nameof(regionX));

        if (regionY < 0)
            throw new ArgumentOutOfRangeException(nameof(regionY));

        if (zLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        if (regionIndex < 0 || regionIndex >= tileRegions.Length)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));

        RegionX = regionX;
        RegionY = regionY;
        ZLevel = zLevel;
        _tileRegions = tileRegions;
        _regionIndex = regionIndex;
    }

    public ushort GetMaterialId(
        int localX,
        int localY)
    {
        if (!IsInside(localX, localY))
            return 0;

        return _materialIds[
            localX + localY * TilesPerSide];
    }

    // Compatibility overload for callers that still pass Z explicitly.
    public ushort GetMaterialId(
        int localX,
        int localY,
        int z)
    {
        return z == ZLevel
            ? GetMaterialId(localX, localY)
            : (ushort)0;
    }

    internal ushort GetMaterialIdAtIndex(
        int localIndex)
    {
        if (localIndex < 0 || localIndex >= TotalTiles)
            throw new IndexOutOfRangeException();

        return _materialIds[localIndex];
    }

    internal void SetMaterialId(
        int localX,
        int localY,
        ushort materialId)
    {
        if (!IsInside(localX, localY))
            throw new IndexOutOfRangeException();

        _materialIds[
            localX + localY * TilesPerSide] =
            materialId;
    }

    // Compatibility overload for callers that still pass Z explicitly.
    internal void SetMaterialId(
        int localX,
        int localY,
        int z,
        ushort materialId)
    {
        if (z != ZLevel)
            throw new ArgumentOutOfRangeException(nameof(z));

        SetMaterialId(
            localX,
            localY,
            materialId);
    }

    internal void SetMaterialIdAtIndex(
        int localIndex,
        ushort materialId)
    {
        if (localIndex < 0 || localIndex >= TotalTiles)
            throw new IndexOutOfRangeException();

        _materialIds[localIndex] = materialId;
    }

    internal void Clear()
    {
        Array.Clear(_materialIds);
    }

    public ref Tile GetLocalTile(
        int localX,
        int localY)
    {
        return ref GetOrCreateTileMetadata().GetLocalTile(
            localX,
            localY);
    }

    public ReadOnlySpan<Tile> Tiles =>
        GetOrCreateTileMetadata().Tiles;

    private TerrainTileRegion GetOrCreateTileMetadata()
    {
        TerrainTileRegion? region =
            _tileRegions[_regionIndex];

        if (region != null)
            return region;

        region = new TerrainTileRegion(
            RegionX,
            RegionY);

        _tileRegions[_regionIndex] = region;
        return region;
    }

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
