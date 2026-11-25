using System;
using System.Threading;

namespace Core.Map;

/// <summary>
/// Sparse two-level direct-address directory for 8x8x8 voxel pages.
/// The root is a compact flat array of 16x16x16-page block references.
/// Each second-level block is allocated only when its first page is written.
/// Reads use two array lookups; no hashing or tree traversal is involved.
/// </summary>
internal sealed class FlatVoxelPageDirectory<T> where T : class
{
    private const int BlockShift = 4;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;
    private const int BlockPlane = BlockSize * BlockSize;
    private const int BlockSlots = BlockSize * BlockSize * BlockSize;

    private readonly T?[][] _blocks;
    private readonly int _blockCountX;
    private readonly int _blockPlane;
    private int _allocatedBlockCount;

    public int CountX { get; }
    public int CountY { get; }
    public int CountZ { get; }
    public int Length { get; }

    public int AllocatedBlockCount => Volatile.Read(ref _allocatedBlockCount);

    /// <summary>
    /// Estimated reference-array payload, excluding CLR array/object headers.
    /// </summary>
    public long EstimatedDirectoryBytes =>
        ((long)_blocks.Length + (long)AllocatedBlockCount * BlockSlots) * IntPtr.Size;

    public FlatVoxelPageDirectory(int countX, int countY, int countZ)
    {
        if (countX <= 0) throw new ArgumentOutOfRangeException(nameof(countX));
        if (countY <= 0) throw new ArgumentOutOfRangeException(nameof(countY));
        if (countZ <= 0) throw new ArgumentOutOfRangeException(nameof(countZ));

        CountX = countX;
        CountY = countY;
        CountZ = countZ;

        int plane = checked(countX * countY);
        Length = checked(plane * countZ);

        int blockCountX = ((countX - 1) >> BlockShift) + 1;
        int blockCountY = ((countY - 1) >> BlockShift) + 1;
        int blockCountZ = ((countZ - 1) >> BlockShift) + 1;

        _blockCountX = blockCountX;
        _blockPlane = checked(blockCountX * blockCountY);
        _blocks = new T?[checked(_blockPlane * blockCountZ)][];
    }

    public int GetLinearIndex(int x, int y, int z)
    {
        ValidateCoordinates(x, y, z);
        return x + y * CountX + z * checked(CountX * CountY);
    }

    public bool TryGet(int x, int y, int z, out T? value)
    {
        if (!IsInside(x, y, z))
        {
            value = null;
            return false;
        }

        int blockIndex = GetBlockIndex(x, y, z);
        T?[]? block = Volatile.Read(ref _blocks[blockIndex]);
        if (block == null)
        {
            value = null;
            return false;
        }

        value = Volatile.Read(ref block[GetBlockLocalIndex(x, y, z)]);
        return value != null;
    }

    public void Set(int x, int y, int z, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCoordinates(x, y, z);

        int blockIndex = GetBlockIndex(x, y, z);
        T?[]? block = Volatile.Read(ref _blocks[blockIndex]);

        if (block == null)
        {
            T?[] candidate = new T?[BlockSlots];
            block = Interlocked.CompareExchange(
                ref _blocks[blockIndex], candidate, null) ?? candidate;

            if (ReferenceEquals(block, candidate))
                Interlocked.Increment(ref _allocatedBlockCount);
        }

        Volatile.Write(ref block[GetBlockLocalIndex(x, y, z)], value);
    }

    public bool Remove(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            return false;

        int blockIndex = GetBlockIndex(x, y, z);
        T?[]? block = Volatile.Read(ref _blocks[blockIndex]);
        if (block == null)
            return false;

        return Interlocked.Exchange(
            ref block[GetBlockLocalIndex(x, y, z)], null) != null;
    }

    public void Clear()
    {
        Array.Clear(_blocks);
        Volatile.Write(ref _allocatedBlockCount, 0);
    }

    private int GetBlockIndex(int x, int y, int z) =>
        (x >> BlockShift) +
        (y >> BlockShift) * _blockCountX +
        (z >> BlockShift) * _blockPlane;

    private static int GetBlockLocalIndex(int x, int y, int z) =>
        (x & BlockMask) +
        ((y & BlockMask) << BlockShift) +
        ((z & BlockMask) << (BlockShift * 2));

    private bool IsInside(int x, int y, int z) =>
        (uint)x < (uint)CountX &&
        (uint)y < (uint)CountY &&
        (uint)z < (uint)CountZ;

    private void ValidateCoordinates(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            throw new ArgumentOutOfRangeException(nameof(x));
    }
}
