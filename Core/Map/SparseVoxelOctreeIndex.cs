using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Core.Map;

/// <summary>
/// Sparse octree index over 16^3 voxel leaf pages.
/// Missing nodes represent untouched provider data; only pages containing edits
/// are indexed. Each node stores an eight-bit child mask and descendant count.
/// </summary>
internal sealed class SparseVoxelOctreeIndex
{
    private const int CoordinateBits = 18;
    private const uint CoordinateMask = (1u << CoordinateBits) - 1u;

    private sealed class Node
    {
        public int ChildMask;
        public int LeafCount;
    }

    private readonly ConcurrentDictionary<ulong, Node> _nodes = new();
    private readonly int _rootLevel;

    public int NodeCount => _nodes.Count;

    public SparseVoxelOctreeIndex(int chunksX, int chunksY, int chunksZ)
    {
        int largest = Math.Max(chunksX, Math.Max(chunksY, chunksZ));
        int level = 0;
        int span = 1;
        while (span < largest)
        {
            span <<= 1;
            level++;
        }

        if (level >= 32)
            throw new ArgumentOutOfRangeException(nameof(chunksX), "Voxel storage exceeds octree address capacity.");

        _rootLevel = level;
    }

    public void AddLeaf(SparseVoxelField.ChunkCoordinate leaf)
    {
        if (!_nodes.TryAdd(Pack(0, leaf.X, leaf.Y, leaf.Z), new Node { LeafCount = 1 }))
            return;

        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int childX = (leaf.X >> (lod - 1)) & 1;
            int childY = (leaf.Y >> (lod - 1)) & 1;
            int childZ = (leaf.Z >> (lod - 1)) & 1;
            int childIndex = childX | (childY << 1) | (childZ << 2);

            int nodeX = leaf.X >> lod;
            int nodeY = leaf.Y >> lod;
            int nodeZ = leaf.Z >> lod;
            Node node = _nodes.GetOrAdd(Pack(lod, nodeX, nodeY, nodeZ), static _ => new Node());

            Interlocked.Or(ref node.ChildMask, 1 << childIndex);
            Interlocked.Increment(ref node.LeafCount);
        }
    }

    public void RemoveLeaf(SparseVoxelField.ChunkCoordinate leaf)
    {
        if (!_nodes.TryRemove(Pack(0, leaf.X, leaf.Y, leaf.Z), out _))
            return;

        bool childBecameEmpty = true;
        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int nodeX = leaf.X >> lod;
            int nodeY = leaf.Y >> lod;
            int nodeZ = leaf.Z >> lod;
            ulong key = Pack(lod, nodeX, nodeY, nodeZ);

            if (!_nodes.TryGetValue(key, out Node? node))
                return;

            if (childBecameEmpty)
            {
                int childX = (leaf.X >> (lod - 1)) & 1;
                int childY = (leaf.Y >> (lod - 1)) & 1;
                int childZ = (leaf.Z >> (lod - 1)) & 1;
                int childIndex = childX | (childY << 1) | (childZ << 2);
                Interlocked.And(ref node.ChildMask, ~(1 << childIndex));
            }

            childBecameEmpty = Interlocked.Decrement(ref node.LeafCount) == 0;
            if (childBecameEmpty)
                _nodes.TryRemove(key, out _);
        }
    }

    public void CollectLeaves(
        int minX,
        int minY,
        int minZ,
        int maxX,
        int maxY,
        int maxZ,
        List<SparseVoxelField.ChunkCoordinate> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        if (minX > maxX || minY > maxY || minZ > maxZ)
            return;

        ulong rootKey = Pack(_rootLevel, 0, 0, 0);
        if (!_nodes.ContainsKey(rootKey))
            return;

        Span<NodeVisit> stack = stackalloc NodeVisit[128];
        int stackCount = 0;
        stack[stackCount++] = new NodeVisit(0, 0, 0, _rootLevel);

        while (stackCount > 0)
        {
            NodeVisit current = stack[--stackCount];

            int nodeSpan = 1 << current.Lod;
            int nodeMinX = current.X * nodeSpan;
            int nodeMinY = current.Y * nodeSpan;
            int nodeMinZ = current.Z * nodeSpan;
            int nodeMaxX = nodeMinX + nodeSpan - 1;
            int nodeMaxY = nodeMinY + nodeSpan - 1;
            int nodeMaxZ = nodeMinZ + nodeSpan - 1;

            if (nodeMaxX < minX || nodeMinX > maxX ||
                nodeMaxY < minY || nodeMinY > maxY ||
                nodeMaxZ < minZ || nodeMinZ > maxZ)
                continue;

            if (!_nodes.TryGetValue(Pack(current.Lod, current.X, current.Y, current.Z), out Node? node))
                continue;

            if (current.Lod == 0)
            {
                destination.Add(new SparseVoxelField.ChunkCoordinate(current.X, current.Y, current.Z));
                continue;
            }

            int childMask = Volatile.Read(ref node.ChildMask);
            while (childMask != 0)
            {
                int child = System.Numerics.BitOperations.TrailingZeroCount((uint)childMask);
                childMask &= childMask - 1;

                int childX = current.X * 2 + (child & 1);
                int childY = current.Y * 2 + ((child >> 1) & 1);
                int childZ = current.Z * 2 + ((child >> 2) & 1);

                stack[stackCount++] = new NodeVisit(
                    childX, childY, childZ, current.Lod - 1);
            }
        }
    }

    public void Clear() => _nodes.Clear();

    private static ulong Pack(int lod, int x, int y, int z)
    {
        if ((uint)x > CoordinateMask || (uint)y > CoordinateMask ||
            (uint)z > CoordinateMask || (uint)lod >= 64)
            throw new ArgumentOutOfRangeException(nameof(x));

        return ((ulong)(uint)lod << 54) |
            ((ulong)(uint)x << 36) |
            ((ulong)(uint)y << 18) |
            (uint)z;
    }

    private readonly record struct NodeVisit(int X, int Y, int Z, int Lod);
}
