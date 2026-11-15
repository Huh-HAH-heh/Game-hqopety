using System;
using System.Collections.Generic;

namespace Core.Map;

/// <summary>
/// One solid leaf of the macro-volume octree. Size is measured in map cells
/// and 0.1 m Z layers; changing a smaller region splits only its owning path.
/// </summary>
public readonly struct BlockVolumeLeaf
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }
    public int Size { get; }
    public ushort MaterialId { get; }

    public BlockVolumeLeaf(int x, int y, int z, int size, ushort materialId)
    {
        X = x;
        Y = y;
        Z = z;
        Size = size;
        MaterialId = materialId;
    }
}

/// <summary>
/// Sparse octree for uniform cuboid volumes. A large homogeneous region is
/// stored as one leaf; editing a smaller part splits only the nodes along
/// that edit path. Coordinates use map tiles for X/Y and voxel layers for Z.
/// </summary>
public sealed class BlockVolumeTree
{
    private enum NodeState : byte
    {
        Inherit,
        Empty,
        Solid
    }

    private sealed class Node
    {
        public NodeState State;
        public ushort MaterialId;
        public Node[]? Children;

        public Node(NodeState state = NodeState.Inherit, ushort materialId = 0)
        {
            State = state;
            MaterialId = materialId;
        }

        public void SetUniform(NodeState state, ushort materialId)
        {
            State = state;
            MaterialId = state == NodeState.Solid ? materialId : (ushort)0;
            Children = null;
        }
    }

    private readonly Node _root = new();
    private readonly int _rootSize;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }

    public bool HasOverrides =>
        _root.State != NodeState.Inherit || _root.Children != null;

    public int SolidLeafCount => CountSolidLeaves(_root);
    public int AllocatedNodeCount => CountNodes(_root);

    /// <summary>
    /// Enumerates the current solid leaves. A uniform macro volume is returned
    /// as one leaf; after a partial edit, only its subdivided children appear.
    /// </summary>
    public IEnumerable<BlockVolumeLeaf> EnumerateSolidLeaves()
    {
        return EnumerateSolidLeaves(_root, 0, 0, 0, _rootSize);
    }

    public BlockVolumeTree(int width, int height, int depth)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));

        Width = width;
        Height = height;
        Depth = depth;

        int requiredSize = Math.Max(width, Math.Max(height, depth));
        int size = 1;
        while (size < requiredSize)
            size = checked(size * 2);
        _rootSize = size;
    }

    /// <summary>
    /// Replaces a cuboid with one material. Material ID zero is an explicit
    /// empty override, allowing a cut to be made through inherited terrain.
    /// </summary>
    public void SetVolume(
        int x,
        int y,
        int z,
        int width,
        int depth,
        int height,
        ushort materialId)
    {
        ValidateBox(x, y, z, width, depth, height);
        SetRegion(
            _root, 0, 0, 0, _rootSize,
            x, y, z, x + width, y + depth, z + height,
            materialId == 0 ? NodeState.Empty : NodeState.Solid,
            materialId);
    }

    /// <summary>
    /// Removes an override and reveals the previous terrain representation.
    /// Partial clears split the owning volume only as far as required.
    /// </summary>
    public void ClearVolume(
        int x,
        int y,
        int z,
        int width,
        int depth,
        int height)
    {
        ValidateBox(x, y, z, width, depth, height);
        SetRegion(
            _root, 0, 0, 0, _rootSize,
            x, y, z, x + width, y + depth, z + height,
            NodeState.Inherit,
            0);
    }

    public void SetVoxel(int x, int y, int z, ushort materialId) =>
        SetVolume(x, y, z, 1, 1, 1, materialId);

    public bool TryGetMaterialId(int x, int y, int z, out ushort materialId)
    {
        materialId = 0;
        if (!IsInside(x, y, z))
            return false;

        return TryGetMaterial(
            _root, 0, 0, 0, _rootSize,
            x, y, z, out materialId);
    }

    public bool HasOverridesInColumn(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || !HasOverrides)
            return false;

        return HasOverridesInColumn(
            _root, 0, 0, 0, _rootSize, x, y);
    }

    public bool HasEmptyOverridesInColumn(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || !HasOverrides)
            return false;

        return HasEmptyOverridesInColumn(
            _root, 0, 0, 0, _rootSize, x, y);
    }

    public int FindHighestSolidLayerInColumn(int x, int y, int maxLayer)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || maxLayer < 0 || !HasOverrides)
            return -1;

        return FindHighestSolidLayer(
            _root, 0, 0, 0, _rootSize,
            x, y, Math.Min(maxLayer, Depth - 1));
    }

    public bool HasSolidInColumnBetween(
        int x,
        int y,
        int firstLayer,
        int lastLayer)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height ||
            firstLayer > lastLayer || !HasOverrides)
        {
            return false;
        }

        firstLayer = Math.Max(0, firstLayer);
        lastLayer = Math.Min(Depth - 1, lastLayer);
        if (firstLayer > lastLayer)
            return false;

        return HasSolidInColumnBetween(
            _root, 0, 0, 0, _rootSize,
            x, y, firstLayer, lastLayer);
    }

    public void Clear()
    {
        _root.SetUniform(NodeState.Inherit, 0);
    }

    private void ValidateBox(
        int x,
        int y,
        int z,
        int width,
        int depth,
        int height)
    {
        if (x < 0 || y < 0 || z < 0 ||
            width <= 0 || depth <= 0 || height <= 0 ||
            (long)x + width > Width ||
            (long)y + depth > Height ||
            (long)z + height > Depth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The volume must be a positive cuboid fully inside the map.");
        }
    }

    private bool IsInside(int x, int y, int z) =>
        x >= 0 && y >= 0 && z >= 0 &&
        x < Width && y < Height && z < Depth;

    private static void SetRegion(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize,
        int boxLeft,
        int boxTop,
        int boxFront,
        int boxRight,
        int boxBottom,
        int boxBack,
        NodeState state,
        ushort materialId)
    {
        int nodeRight = nodeX + nodeSize;
        int nodeBottom = nodeY + nodeSize;
        int nodeBack = nodeZ + nodeSize;

        if (boxLeft >= nodeRight || boxRight <= nodeX ||
            boxTop >= nodeBottom || boxBottom <= nodeY ||
            boxFront >= nodeBack || boxBack <= nodeZ)
        {
            return;
        }

        if (boxLeft <= nodeX && boxRight >= nodeRight &&
            boxTop <= nodeY && boxBottom >= nodeBottom &&
            boxFront <= nodeZ && boxBack >= nodeBack)
        {
            node.SetUniform(state, materialId);
            return;
        }

        if (nodeSize == 1)
        {
            node.SetUniform(state, materialId);
            return;
        }

        EnsureChildren(node);
        int half = nodeSize / 2;
        for (int childIndex = 0; childIndex < 8; childIndex++)
        {
            int childX = nodeX + ((childIndex & 1) != 0 ? half : 0);
            int childY = nodeY + ((childIndex & 2) != 0 ? half : 0);
            int childZ = nodeZ + ((childIndex & 4) != 0 ? half : 0);

            SetRegion(
                node.Children![childIndex],
                childX, childY, childZ, half,
                boxLeft, boxTop, boxFront,
                boxRight, boxBottom, boxBack,
                state, materialId);
        }

        TryMerge(node);
    }

    private static void EnsureChildren(Node node)
    {
        if (node.Children != null)
            return;

        Node[] children = new Node[8];
        for (int i = 0; i < children.Length; i++)
            children[i] = new Node(node.State, node.MaterialId);

        node.Children = children;
        node.State = NodeState.Inherit;
        node.MaterialId = 0;
    }

    private static void TryMerge(Node node)
    {
        Node[]? children = node.Children;
        if (children == null)
            return;

        Node first = children[0];
        if (first.Children != null)
            return;

        NodeState state = first.State;
        ushort materialId = first.MaterialId;

        for (int i = 1; i < children.Length; i++)
        {
            Node child = children[i];
            if (child.Children != null ||
                child.State != state ||
                child.MaterialId != materialId)
            {
                return;
            }
        }

        node.SetUniform(state, materialId);
    }

    private static bool TryGetMaterial(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize,
        int x,
        int y,
        int z,
        out ushort materialId)
    {
        if (node.State != NodeState.Inherit)
        {
            materialId = node.State == NodeState.Solid ? node.MaterialId : (ushort)0;
            return true;
        }

        Node[]? children = node.Children;
        if (children == null)
        {
            materialId = 0;
            return false;
        }

        int half = nodeSize / 2;
        int childIndex =
            (x >= nodeX + half ? 1 : 0) |
            (y >= nodeY + half ? 2 : 0) |
            (z >= nodeZ + half ? 4 : 0);

        return TryGetMaterial(
            children[childIndex],
            nodeX + ((childIndex & 1) != 0 ? half : 0),
            nodeY + ((childIndex & 2) != 0 ? half : 0),
            nodeZ + ((childIndex & 4) != 0 ? half : 0),
            half,
            x, y, z,
            out materialId);
    }

    private static bool HasOverridesInColumn(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize,
        int x,
        int y)
    {
        if (x < nodeX || y < nodeY ||
            x >= nodeX + nodeSize || y >= nodeY + nodeSize)
        {
            return false;
        }

        if (node.State != NodeState.Inherit)
            return true;

        Node[]? children = node.Children;
        if (children == null)
            return false;

        int half = nodeSize / 2;
        int childX = x >= nodeX + half ? 1 : 0;
        int childY = y >= nodeY + half ? 1 : 0;
        int childBase = childX | (childY << 1);

        return HasOverridesInColumn(
                   children[childBase | 4],
                   nodeX + childX * half,
                   nodeY + childY * half,
                   nodeZ + half,
                   half, x, y) ||
               HasOverridesInColumn(
                   children[childBase],
                   nodeX + childX * half,
                   nodeY + childY * half,
                   nodeZ,
                   half, x, y);
    }

    private static bool HasEmptyOverridesInColumn(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize,
        int x,
        int y)
    {
        if (x < nodeX || y < nodeY ||
            x >= nodeX + nodeSize || y >= nodeY + nodeSize)
        {
            return false;
        }

        if (node.State == NodeState.Empty)
            return true;
        if (node.State == NodeState.Solid || node.Children == null)
            return false;

        int half = nodeSize / 2;
        int childX = x >= nodeX + half ? 1 : 0;
        int childY = y >= nodeY + half ? 1 : 0;
        int childBase = childX | (childY << 1);

        return HasEmptyOverridesInColumn(
                   node.Children[childBase | 4],
                   nodeX + childX * half,
                   nodeY + childY * half,
                   nodeZ + half,
                   half, x, y) ||
               HasEmptyOverridesInColumn(
                   node.Children[childBase],
                   nodeX + childX * half,
                   nodeY + childY * half,
                   nodeZ,
                   half, x, y);
    }

    private static int FindHighestSolidLayer(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize,
        int x,
        int y,
        int maxLayer)
    {
        if (x < nodeX || y < nodeY ||
            x >= nodeX + nodeSize || y >= nodeY + nodeSize ||
            maxLayer < nodeZ)
        {
            return -1;
        }

        if (node.State == NodeState.Solid)
            return Math.Min(maxLayer, nodeZ + nodeSize - 1);
        if (node.State == NodeState.Empty || node.Children == null)
            return -1;

        int half = nodeSize / 2;
        int childX = x >= nodeX + half ? 1 : 0;
        int childY = y >= nodeY + half ? 1 : 0;
        int childBase = childX | (childY << 1);

        if (maxLayer >= nodeZ + half)
        {
            int upper = FindHighestSolidLayer(
                node.Children[childBase | 4],
                nodeX + childX * half,
                nodeY + childY * half,
                nodeZ + half,
                half, x, y, maxLayer);
            if (upper >= 0)
                return upper;
        }

        return FindHighestSolidLayer(
            node.Children[childBase],
            nodeX + childX * half,
            nodeY + childY * half,
            nodeZ,
            half, x, y, maxLayer);
    }

    private static bool HasSolidInColumnBetween(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize,
        int x,
        int y,
        int firstLayer,
        int lastLayer)
    {
        int nodeTop = nodeZ + nodeSize - 1;
        if (x < nodeX || y < nodeY ||
            x >= nodeX + nodeSize || y >= nodeY + nodeSize ||
            lastLayer < nodeZ || firstLayer > nodeTop)
        {
            return false;
        }

        if (node.State == NodeState.Solid)
            return true;
        if (node.State == NodeState.Empty || node.Children == null)
            return false;

        int half = nodeSize / 2;
        int childX = x >= nodeX + half ? 1 : 0;
        int childY = y >= nodeY + half ? 1 : 0;
        int childBase = childX | (childY << 1);

        if (lastLayer >= nodeZ + half &&
            HasSolidInColumnBetween(
                node.Children[childBase | 4],
                nodeX + childX * half,
                nodeY + childY * half,
                nodeZ + half,
                half, x, y, firstLayer, lastLayer))
        {
            return true;
        }

        return firstLayer < nodeZ + half &&
            HasSolidInColumnBetween(
                node.Children[childBase],
                nodeX + childX * half,
                nodeY + childY * half,
                nodeZ,
                half, x, y, firstLayer, lastLayer);
    }

    private static IEnumerable<BlockVolumeLeaf> EnumerateSolidLeaves(
        Node node,
        int nodeX,
        int nodeY,
        int nodeZ,
        int nodeSize)
    {
        Node[]? children = node.Children;
        if (children == null)
        {
            if (node.State == NodeState.Solid)
            {
                yield return new BlockVolumeLeaf(
                    nodeX, nodeY, nodeZ, nodeSize, node.MaterialId);
            }

            yield break;
        }

        int half = nodeSize / 2;
        for (int childIndex = 0; childIndex < children.Length; childIndex++)
        {
            foreach (BlockVolumeLeaf leaf in EnumerateSolidLeaves(
                children[childIndex],
                nodeX + ((childIndex & 1) != 0 ? half : 0),
                nodeY + ((childIndex & 2) != 0 ? half : 0),
                nodeZ + ((childIndex & 4) != 0 ? half : 0),
                half))
            {
                yield return leaf;
            }
        }
    }

    private static int CountSolidLeaves(Node node)
    {
        Node[]? children = node.Children;
        if (children == null)
            return node.State == NodeState.Solid ? 1 : 0;

        int count = 0;
        for (int i = 0; i < children.Length; i++)
            count += CountSolidLeaves(children[i]);
        return count;
    }

    private static int CountNodes(Node node)
    {
        Node[]? children = node.Children;
        if (children == null)
            return 1;

        int count = 1;
        for (int i = 0; i < children.Length; i++)
            count += CountNodes(children[i]);
        return count;
    }
}
