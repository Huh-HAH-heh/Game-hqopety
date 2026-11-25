using System;
using System.Threading;

namespace Core.Map;

/// <summary>
/// Flat, direct-addressed directory for sparse voxel data pages.
/// X is the fastest-changing axis; no hashing or tree traversal is used for reads.
/// </summary>
internal sealed class FlatVoxelPageDirectory<T> where T : class
{
    private readonly T?[] _items;
    private readonly int _plane;

    public int CountX { get; }
    public int CountY { get; }
    public int CountZ { get; }
    public int Length => _items.Length;

    public FlatVoxelPageDirectory(int countX, int countY, int countZ)
    {
        if (countX <= 0) throw new ArgumentOutOfRangeException(nameof(countX));
        if (countY <= 0) throw new ArgumentOutOfRangeException(nameof(countY));
        if (countZ <= 0) throw new ArgumentOutOfRangeException(nameof(countZ));

        CountX = countX;
        CountY = countY;
        CountZ = countZ;
        _plane = checked(countX * countY);
        _items = new T?[checked(_plane * countZ)];
    }

    public int GetLinearIndex(int x, int y, int z)
    {
        if ((uint)x >= (uint)CountX ||
            (uint)y >= (uint)CountY ||
            (uint)z >= (uint)CountZ)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        return x + y * CountX + z * _plane;
    }

    public bool TryGet(int x, int y, int z, out T? value)
    {
        if ((uint)x >= (uint)CountX ||
            (uint)y >= (uint)CountY ||
            (uint)z >= (uint)CountZ)
        {
            value = null;
            return false;
        }

        value = Volatile.Read(ref _items[x + y * CountX + z * _plane]);
        return value != null;
    }

    public void Set(int x, int y, int z, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref _items[GetLinearIndex(x, y, z)], value);
    }

    public bool Remove(int x, int y, int z)
    {
        if ((uint)x >= (uint)CountX ||
            (uint)y >= (uint)CountY ||
            (uint)z >= (uint)CountZ)
        {
            return false;
        }

        int index = x + y * CountX + z * _plane;
        return Interlocked.Exchange(ref _items[index], null) != null;
    }

    public void Clear() => Array.Clear(_items);
}
