using System;

namespace Core.Map;

/// <summary>
/// A region owned by exactly one TerrainLayer (one Z level).
/// Each entry is a 1 x 1 x 0.1 meter terrain cell's material ID.
/// </summary>
public sealed class TerrainRegion
{
    public const int TilesPerSide = 48;

    public const int TotalTiles =
        TilesPerSide * TilesPerSide;

    public int RegionX { get; }
    public int RegionY { get; }
    public int ZLevel { get; }

    // Most voxel chunks are entirely empty or entirely solid at a given Z.
    // Store those chunks as one material ID and allocate the 48x48 buffer
    // only for layers that actually contain mixed materials.
    private ushort[]? _materialIds;
    private ushort _uniformMaterialId;
    private ushort _nonZeroMaterialId;
    private int _nonZeroCount;
    private bool _allNonZeroSame = true;

    public ReadOnlySpan<ushort> MaterialIds =>
        GetOrCreateMaterialBuffer();

    internal bool HasDenseMaterialBuffer =>
        _materialIds != null;

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

        return _materialIds == null
            ? _uniformMaterialId
            : _materialIds[localX + localY * TilesPerSide];
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

        return _materialIds == null
            ? _uniformMaterialId
            : _materialIds[localIndex];
    }

    internal void SetMaterialId(
        int localX,
        int localY,
        ushort materialId)
    {
        if (!IsInside(localX, localY))
            throw new IndexOutOfRangeException();

        SetCellMaterial(
            localX + localY * TilesPerSide,
            materialId);
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

        SetCellMaterial(localIndex, materialId);
    }

    internal void Clear()
    {
        _materialIds = null;
        _uniformMaterialId = 0;
        _nonZeroMaterialId = 0;
        _nonZeroCount = 0;
        _allNonZeroSame = true;
    }

    private void SetCellMaterial(
        int index,
        ushort materialId)
    {
        ushort previous = _materialIds == null
            ? _uniformMaterialId
            : _materialIds[index];

        if (previous == materialId)
            return;

        ushort[] values = GetOrCreateMaterialBuffer();

        if (previous == 0 && materialId != 0)
        {
            if (_nonZeroCount == 0)
            {
                _nonZeroMaterialId = materialId;
                _allNonZeroSame = true;
            }
            else if (_allNonZeroSame && materialId != _nonZeroMaterialId)
            {
                _allNonZeroSame = false;
            }

            _nonZeroCount++;
        }
        else if (previous != 0 && materialId == 0)
        {
            _nonZeroCount--;

            if (_nonZeroCount == 0)
            {
                _nonZeroMaterialId = 0;
                _allNonZeroSame = true;
            }
        }
        else if (previous != 0 && materialId != 0 &&
                 previous != materialId && _allNonZeroSame)
        {
            if (_nonZeroCount == 1)
            {
                _nonZeroMaterialId = materialId;
            }
            else
            {
                _allNonZeroSame = false;
            }
        }

        values[index] = materialId;

        if (_nonZeroCount == 0)
        {
            CollapseUniform(0);
        }
        else if (_nonZeroCount == TotalTiles && _allNonZeroSame)
        {
            CollapseUniform(_nonZeroMaterialId);
        }
    }

    private ushort[] GetOrCreateMaterialBuffer()
    {
        if (_materialIds != null)
            return _materialIds;

        _materialIds = new ushort[TotalTiles];

        if (_uniformMaterialId != 0)
        {
            Array.Fill(_materialIds, _uniformMaterialId);
            _nonZeroMaterialId = _uniformMaterialId;
            _nonZeroCount = TotalTiles;
        }
        else
        {
            _nonZeroMaterialId = 0;
            _nonZeroCount = 0;
        }

        _allNonZeroSame = true;
        return _materialIds;
    }

    private void CollapseUniform(ushort materialId)
    {
        _materialIds = null;
        _uniformMaterialId = materialId;
        _nonZeroMaterialId = materialId;
        _nonZeroCount = materialId == 0 ? 0 : TotalTiles;
        _allNonZeroSame = true;
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
