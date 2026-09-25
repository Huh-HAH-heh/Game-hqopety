namespace Core.Map;

public sealed class TerrainRegion
{
    public const int TilesPerSide = 48;

    public const int TotalTiles =
        TilesPerSide *
        TilesPerSide;

    public int RegionX { get; }
    public int RegionY { get; }

    private readonly Tile[] _tiles;

    private readonly int[] _rangeStarts;
    private readonly byte[] _rangeCounts;
    private readonly byte[] _rangeCapacities;
    private readonly ushort[] _surfaceHeights;

    private TileRange[] _ranges;
    private int _rangeUsed;

    public TerrainRegion(
        int regionX,
        int regionY)
    {
        RegionX = regionX;
        RegionY = regionY;

        _tiles =
            new Tile[TotalTiles];

        _rangeStarts =
            new int[TotalTiles];

        _rangeCounts =
            new byte[TotalTiles];

        _rangeCapacities =
            new byte[TotalTiles];

        _surfaceHeights =
            new ushort[TotalTiles];

        _ranges =
            new TileRange[TotalTiles];

        for (int y = 0;
             y < TilesPerSide;
             y++)
        {
            for (int x = 0;
                 x < TilesPerSide;
                 x++)
            {
                _tiles[
                    x +
                    y * TilesPerSide] =
                    new Tile(
                        (byte)x,
                        (byte)y);
            }
        }
    }

    // ============================================================
    // TILE
    // ============================================================

    public ref Tile GetLocalTile(
        int localX,
        int localY)
    {
        return ref _tiles[
            localX +
            localY * TilesPerSide];
    }

    // ============================================================
    // RANGES
    // ============================================================

    public ReadOnlySpan<TileRange> GetRanges(
        int localX,
        int localY)
    {
        int tileIndex =
            localX +
            localY * TilesPerSide;

        int count =
            _rangeCounts[tileIndex];

        if (count == 0)
            return ReadOnlySpan<TileRange>.Empty;

        return _ranges.AsSpan(
            _rangeStarts[tileIndex],
            count);
    }

    public int GetRangeCount(
        int localX,
        int localY)
    {
        return _rangeCounts[
            localX +
            localY * TilesPerSide];
    }

    public void ClearRanges(
        int localX,
        int localY)
    {
        int tileIndex =
            localX +
            localY * TilesPerSide;

        _rangeCounts[tileIndex] = 0;
        _surfaceHeights[tileIndex] = 0;
    }

    public bool TryAddRange(
        int localX,
        int localY,
        ushort startZ,
        ushort endZ,
        ushort materialId,
        byte state)
    {
        if (startZ > endZ)
        {
            throw new ArgumentException(
                "StartZ не может быть больше EndZ.");
        }

        int tileIndex =
            localX +
            localY * TilesPerSide;

        int count =
            _rangeCounts[tileIndex];

        if (count >= Tile.MaxRanges)
            return false;

        EnsureRangeCapacity(
            tileIndex,
            count + 1);

        int rangeIndex =
            _rangeStarts[tileIndex] +
            count;

        _ranges[rangeIndex] =
            new TileRange
            {
                StartZ = startZ,
                EndZ = endZ,
                MaterialId = materialId,
                State = state
            };

        _rangeCounts[tileIndex] =
            (byte)(count + 1);

        if (state == WorldMap.StateSolid &&
            endZ > _surfaceHeights[tileIndex])
        {
            _surfaceHeights[tileIndex] =
                endZ;
        }

        return true;
    }

    public void SetRanges(
        int localX,
        int localY,
        ReadOnlySpan<TileRange> ranges)
    {
        if (ranges.Length > Tile.MaxRanges)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ranges));
        }

        int tileIndex =
            localX +
            localY * TilesPerSide;

        if (ranges.Length == 0)
        {
            _rangeCounts[tileIndex] = 0;
            _surfaceHeights[tileIndex] = 0;
            return;
        }

        EnsureRangeCapacity(
            tileIndex,
            ranges.Length);

        ranges.CopyTo(
            _ranges.AsSpan(
                _rangeStarts[tileIndex],
                ranges.Length));

        _rangeCounts[tileIndex] =
            (byte)ranges.Length;

        ushort surfaceZ = 0;

        for (int i = 0;
             i < ranges.Length;
             i++)
        {
            ref readonly TileRange range =
                ref ranges[i];

            if (range.State == WorldMap.StateSolid &&
                range.EndZ > surfaceZ)
            {
                surfaceZ =
                    range.EndZ;
            }
        }

        _surfaceHeights[tileIndex] =
            surfaceZ;
    }

    public ushort GetSurfaceHeightUnits(
        int localX,
        int localY)
    {
        return _surfaceHeights[
            localX +
            localY * TilesPerSide];
    }

    // ============================================================
    // STORAGE
    // ============================================================

    private void EnsureRangeCapacity(
        int tileIndex,
        int required)
    {
        int currentCapacity =
            _rangeCapacities[tileIndex];

        if (required <= currentCapacity)
            return;

        int newCapacity =
            currentCapacity == 0
                ? 1
                : currentCapacity * 2;

        if (newCapacity < required)
            newCapacity = required;

        if (newCapacity > Tile.MaxRanges)
            newCapacity = Tile.MaxRanges;

        int newStart =
            AllocateRangeBlock(
                newCapacity);

        int oldCount =
            _rangeCounts[tileIndex];

        if (oldCount > 0)
        {
            _ranges
                .AsSpan(
                    _rangeStarts[tileIndex],
                    oldCount)
                .CopyTo(
                    _ranges.AsSpan(
                        newStart,
                        oldCount));
        }

        _rangeStarts[tileIndex] =
            newStart;

        _rangeCapacities[tileIndex] =
            (byte)newCapacity;
    }

    private int AllocateRangeBlock(
        int count)
    {
        EnsureRangeStorageCapacity(
            _rangeUsed + count);

        int start =
            _rangeUsed;

        _rangeUsed +=
            count;

        return start;
    }

    private void EnsureRangeStorageCapacity(
        int required)
    {
        if (required <= _ranges.Length)
            return;

        int newCapacity =
            _ranges.Length * 2;

        if (newCapacity < required)
            newCapacity = required;

        Array.Resize(
            ref _ranges,
            newCapacity);
    }

    public ReadOnlySpan<Tile> Tiles =>
        _tiles.AsSpan();
}
