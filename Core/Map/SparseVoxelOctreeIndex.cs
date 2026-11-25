using System;
using System.Collections.Generic;

namespace Core.Map;

/// <summary>
/// Compact sparse octree over 16^3 voxel leaf pages.
/// Missing branches are untouched provider-backed space.
/// This index is updated and queried by the world/render thread; voxel page
/// payloads themselves remain safe for concurrent readers.
/// </summary>
internal sealed class SparseVoxelOctreeIndex
{
    private const int CoordinateBits = 18;
    private const uint CoordinateMask = (1u << CoordinateBits) - 1u;

    private struct Node
    {
        public byte ChildMask;
        public int LeafCount;
    }

    private readonly Dictionary<ulong, Node> _nodes = new();
    private readonly int _rootLevel;

    public int NodeCount => _nodes.Count;

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

    public void AddLeaf(SparseVoxelField.ChunkCoordinate leaf)
    {
        ulong leafKey = Pack(0, leaf.X, leaf.Y, leaf.Z);
        if (_nodes.ContainsKey(leafKey))
            return;

        _nodes.Add(leafKey, new Node { LeafCount = 1 });

        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int childX = (leaf.X >> (lod - 1)) & 1;
            int childY = (leaf.Y >> (lod - 1)) & 1;
            int childZ = (leaf.Z >> (lod - 1)) & 1;
            int childIndex = childX | (childY << 1) | (childZ << 2);

            ulong nodeKey = Pack(lod, leaf.X >> lod, leaf.Y >> lod, leaf.Z >> lod);
            _nodes.TryGetValue(nodeKey, out Node node);
            node.ChildMask |= (byte)(1 << childIndex);
            node.LeafCount++;
            _nodes[nodeKey] = node;
        }
    }

    public void RemoveLeaf(SparseVoxelField.ChunkCoordinate leaf)
    {
        if (!_nodes.Remove(Pack(0, leaf.X, leaf.Y, leaf.Z)))
            return;

        bool childBecameEmpty = true;
        for (int lod = 1; lod <= _rootLevel; lod++)
        {
            int nodeX = leaf.X >> lod;
            int nodeY = leaf.Y >> lod;
            int nodeZ = leaf.Z >> lod;
            ulong nodeKey = Pack(lod, nodeX, nodeY, nodeZ);
            if (!_nodes.TryGetValue(nodeKey, out Node node))
                return;

            if (childBecameEmpty)
            {
                int childX = (leaf.X >> (lod - 1)) & 1;
                int childY = (leaf.Y >> (lod - 1)) & 1;
                int childZ = (leaf.Z >> (lod - 1)) & 1;
                int childIndex = childX | (childY << 1) | (childZ << 2);
                node.ChildMask &= (byte)~(1 << childIndex);
            }

            node.LeafCount--;
            childBecameEmpty = node.LeafCount == 0;
            if (childBecameEmpty)
                _nodes.Remove(nodeKey);
            else
                _nodes[nodeKey] = node;
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

            if (!_nodes.TryGetValue(Pack(current.Lod, current.X, current.Y, current.Z), out Node node))
                continue;

            if (current.Lod == 0)
            {
                destination.Add(new SparseVoxelField.ChunkCoordinate(current.X, current.Y, current.Z));
                continue;
            }

            int childMask = node.ChildMask;
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
