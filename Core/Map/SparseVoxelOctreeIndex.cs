using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Core.Map;

/// <summary>
/// Sparse spatial octree for 8^3 voxel data cells.
/// Leaves own content/material pages; parent nodes cache child occupancy and
/// an Empty/Full/Mixed content constitution when a complete subtree is present.
/// Missing leaves remain provider-backed and are never incorrectly treated as air.
/// </summary>
internal sealed class SparseVoxelOctreeIndex<T> where T : class
{
    private const int CoordinateBits = 18;
    private const uint CoordinateMask = (1u << CoordinateBits) - 1u;

    private sealed class Node
    {
        public int ChildMask;
        public int LeafCount;
        public int ContentConstitution = (int)SparseVoxelField.VoxelContentConstitution.Mixed;
        public T? Page;
    }

    private readonly ConcurrentDictionary<ulong, Node> _nodes = new();
    private readonly Func<T, SparseVoxelField.VoxelContentConstitution> _getConstitution;
    private readonly int _rootLevel;
    private int _leafCount;

    public int NodeCount => _nodes.Count;
    public int LeafCount => Volatile.Read(ref _leafCount);

    public SparseVoxelOctreeIndex(
        int cellsX,
        int cellsY,
        int cellsZ,
        Func<T, SparseVoxelField.VoxelContentConstitution> getConstitution)
    {
        if (cellsX <= 0 || cellsY <= 0 || cellsZ <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellsX));

        _getConstitution = getConstitution ??
            throw new ArgumentNullException(nameof(getConstitution));

        int largest = Math.Max(cellsX, Math.Max(cellsY, cellsZ));
        int level = 0;
        int span = 1;
        while (span < largest)
        {
            span <<= 1;
            level++;
        }

        if (level >= 32)
            throw new ArgumentOutOfRangeException(nameof(cellsX));

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

        Volatile.Write(
            ref leafNode.ContentConstitution,
            (int)_getConstitution(page));
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
        Volatile.Write(
            ref leafNode.ContentConstitution,
            (int)_getConstitution(page));

        if (previous == null)
        {
            Interlocked.Increment(ref _leafCount);
            AddParents(leaf);
        }
        else
        {
            RefreshParents(leaf);
        }
    }

    public void UpdateLeafConstitution(SparseVoxelField.ChunkCoordinate leaf, T page)
    {
        ulong key = Pack(0, leaf.X, leaf.Y, leaf.Z);
        if (!_nodes.TryGetValue(key, out Node? node) ||
            !ReferenceEquals(Volatile.Read(ref node.Page), page))
        {
            return;
        }

        Volatile.Write(
            ref node.ContentConstitution,
            (int)_getConstitution(page));
        RefreshParents(leaf);
    }

    public SparseVoxelField.VoxelContentConstitution GetConstitution(
        int lod,
        int x,
        int y,
        int z)
    {
        if (lod < 0 || lod > _rootLevel ||
            !_nodes.TryGetValue(Pack(lod, x, y, z), out Node? node))
        {
            return SparseVoxelField.VoxelContentConstitution.Mixed;
        }

        return (SparseVoxelField.VoxelContentConstitution)
            Volatile.Read(ref node.ContentConstitution);
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

            int nodeX = leaf.X >> lod;
            int nodeY = leaf.Y >> lod;
            int nodeZ = leaf.Z >> lod;
            ulong nodeKey = Pack(lod, nodeX, nodeY, nodeZ);
            Node node = _nodes.GetOrAdd(nodeKey, static _ => new Node());
            Interlocked.Or(ref node.ChildMask, 1 << childIndex);
            Interlocked.Increment(ref node.LeafCount);
            RecomputeConstitution(lod, nodeX, nodeY, nodeZ, node);
        }
    }

    private void RefreshParents(SparseVoxelField.ChunkCoordinate leaf)
    {
        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int nodeX = leaf.X >> lod;
            int nodeY = leaf.Y >> lod;
            int nodeZ = leaf.Z >> lod;
            if (_nodes.TryGetValue(Pack(lod, nodeX, nodeY, nodeZ), out Node? node))
                RecomputeConstitution(lod, nodeX, nodeY, nodeZ, node);
        }
    }

    private void RecomputeConstitution(int lod, int x, int y, int z, Node node)
    {
        // A sparse or incomplete subtree is Mixed/unknown, never assumed empty.
        long expectedLeaves = 1L << (lod * 3);
        if (Volatile.Read(ref node.ChildMask) != byte.MaxValue ||
            Volatile.Read(ref node.LeafCount) != expectedLeaves)
        {
            Volatile.Write(
                ref node.ContentConstitution,
                (int)SparseVoxelField.VoxelContentConstitution.Mixed);
            return;
        }

        int constitution = -1;
        for (int child = 0; child < 8; child++)
        {
            int childX = x * 2 + (child & 1);
            int childY = y * 2 + ((child >> 1) & 1);
            int childZ = z * 2 + ((child >> 2) & 1);

            if (!_nodes.TryGetValue(
                    Pack(lod - 1, childX, childY, childZ),
                    out Node? childNode))
            {
                constitution = (int)SparseVoxelField.VoxelContentConstitution.Mixed;
                break;
            }

            int childConstitution = Volatile.Read(ref childNode.ContentConstitution);
            if (childConstitution == (int)SparseVoxelField.VoxelContentConstitution.Mixed ||
                (constitution >= 0 && constitution != childConstitution))
            {
                constitution = (int)SparseVoxelField.VoxelContentConstitution.Mixed;
                break;
            }

            constitution = childConstitution;
        }

        Volatile.Write(
            ref node.ContentConstitution,
            constitution < 0
                ? (int)SparseVoxelField.VoxelContentConstitution.Mixed
                : constitution);
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
            {
                _nodes.TryRemove(nodeKey, out _);
            }
            else
            {
                RecomputeConstitution(lod, nodeX, nodeY, nodeZ, node);
            }
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
