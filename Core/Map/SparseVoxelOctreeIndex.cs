using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Core.Map;

/// <summary>
/// One sparse octree owns both the spatial directory and the LOD0 page payloads.
/// Missing leaves are provider-backed space. LOD0 pages store full Content and
/// Material byte channels; parent nodes store only an eight-child mask.
/// </summary>
internal sealed class SparseVoxelOctreeIndex<T> where T : class
{
    private const int CoordinateBits = 18;
    private const uint CoordinateMask = (1u << CoordinateBits) - 1u;

    private sealed class Node
    {
        public int ChildMask;
        public int LeafCount;
        public T? Page;
    }

    private readonly ConcurrentDictionary<ulong, Node> _nodes = new();
    private readonly int _rootLevel;
    private int _leafCount;

    public int NodeCount => _nodes.Count;
    public int LeafCount => Volatile.Read(ref _leafCount);

    public SparseVoxelOctreeIndex(int chunksX, int chunksY, int chunksZ)
    {
        if (chunksX <= 0 || chunksY <= 0 || chunksZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunksX));

        int largest = Math.Max(chunksX, Math.Max(chunksY, chunksZ));
        int level = 0;
        int span = 1;
        while (span < largest)
        {
            span <<= 1;
            level++;
        }

        if (level >= 32)
            throw new ArgumentOutOfRangeException(nameof(chunksX));

        _rootLevel = level;
    }

    public bool TryGetLeaf(
        SparseVoxelField.ChunkCoordinate leaf,
        out T? page)
    {
        if (_nodes.TryGetValue(Pack(0, leaf.X, leaf.Y, leaf.Z), out Node? node))
        {
            page = Volatile.Read(ref node.Page);
            return page != null;
        }

        page = null;
        return false;
    }

    public T GetOrAddLeaf(
        SparseVoxelField.ChunkCoordinate leaf,
        T page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Node leafNode = _nodes.GetOrAdd(
            Pack(0, leaf.X, leaf.Y, leaf.Z),
            static _ => new Node());

        T? existing = Interlocked.CompareExchange(ref leafNode.Page, page, null);
        if (existing != null)
            return existing;

        Interlocked.Increment(ref _leafCount);
        AddParents(leaf);
        return page;
    }

    public void SetLeaf(
        SparseVoxelField.ChunkCoordinate leaf,
        T page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Node leafNode = _nodes.GetOrAdd(
            Pack(0, leaf.X, leaf.Y, leaf.Z),
            static _ => new Node());

        T? previous = Interlocked.Exchange(ref leafNode.Page, page);
        if (previous != null)
            return;

        Interlocked.Increment(ref _leafCount);
        AddParents(leaf);
    }

    public bool ContainsLeaf(SparseVoxelField.ChunkCoordinate leaf) =>
        TryGetLeaf(leaf, out _);

    public bool TryRemoveLeaf(
        SparseVoxelField.ChunkCoordinate leaf,
        out T? removed)
    {
        ulong leafKey = Pack(0, leaf.X, leaf.Y, leaf.Z);
        removed = null;

        if (!_nodes.TryGetValue(leafKey, out Node? leafNode))
            return false;

        removed = Interlocked.Exchange(ref leafNode.Page, null);
        if (removed == null)
            return false;

        _nodes.TryRemove(leafKey, out _);
        Interlocked.Decrement(ref _leafCount);
        RemoveEmptyParents(leaf);
        return true;
    }

    private void AddParents(SparseVoxelField.ChunkCoordinate leaf)
    {
        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int childX = (leaf.X >> (lod - 1)) & 1;
            int childY = (leaf.Y >> (lod - 1)) & 1;
            int childZ = (leaf.Z >> (lod - 1)) & 1;
            int childIndex = childX | (childY << 1) | (childZ << 2);

            ulong nodeKey = Pack(lod, leaf.X >> lod, leaf.Y >> lod, leaf.Z >> lod);
            Node node = _nodes.GetOrAdd(nodeKey, static _ => new Node());
            Interlocked.Or(ref node.ChildMask, 1 << childIndex);
            Interlocked.Increment(ref node.LeafCount);
        }
    }

    private void RemoveEmptyParents(SparseVoxelField.ChunkCoordinate leaf)
    {
        bool childBecameEmpty = true;
        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int nodeX = leaf.X >> lod;
            int nodeY = leaf.Y >> lod;
            int nodeZ = leaf.Z >> lod;
            ulong nodeKey = Pack(lod, nodeX, nodeY, nodeZ);

            if (!_nodes.TryGetValue(nodeKey, out Node? node))
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
                _nodes.TryRemove(nodeKey, out _);
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

        if (!_nodes.ContainsKey(Pack(_rootLevel, 0, 0, 0)))
            return;

        Span<NodeVisit> stack = stackalloc NodeVisit[128];
        int stackCount = 0;
        stack[stackCount++] = new NodeVisit(0, 0, 0, _rootLevel);

        while (stackCount > 0)
        {
            NodeVisit current = stack[--stackCount];
            int span = 1 << current.Lod;
            int minNodeX = current.X * span;
            int minNodeY = current.Y * span;
            int minNodeZ = current.Z * span;

            if (minNodeX + span - 1 < minX || minNodeX > maxX ||
                minNodeY + span - 1 < minY || minNodeY > maxY ||
                minNodeZ + span - 1 < minZ || minNodeZ > maxZ)
                continue;

            if (!_nodes.TryGetValue(
                    Pack(current.Lod, current.X, current.Y, current.Z),
                    out Node? node))
                continue;

            if (current.Lod == 0)
            {
                if (Volatile.Read(ref node.Page) != null)
                {
                    destination.Add(new SparseVoxelField.ChunkCoordinate(
                        current.X, current.Y, current.Z));
                }
                continue;
            }

            int childMask = Volatile.Read(ref node.ChildMask);
            while (childMask != 0)
            {
                int child = System.Numerics.BitOperations.TrailingZeroCount((uint)childMask);
                childMask &= childMask - 1;

                stack[stackCount++] = new NodeVisit(
                    current.X * 2 + (child & 1),
                    current.Y * 2 + ((child >> 1) & 1),
                    current.Z * 2 + ((child >> 2) & 1),
                    current.Lod - 1);
            }
        }
    }

    public void Clear()
    {
        _nodes.Clear();
        Volatile.Write(ref _leafCount, 0);
    }

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
