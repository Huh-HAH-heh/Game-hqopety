using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

internal sealed class VoxelMeshData
{
    private int _returned;
    private readonly Vector3[] _vertices;
    private readonly Vector3[] _normals;
    private readonly byte[] _colors;
    private readonly ushort[] _indices;

    public ReadOnlySpan<Vector3> Vertices => _vertices.AsSpan(0, VertexCount);
    public ReadOnlySpan<Vector3> Normals => _normals.AsSpan(0, VertexCount);
    public ReadOnlySpan<byte> Colors => _colors.AsSpan(0, VertexCount * 4);
    public ReadOnlySpan<ushort> Indices => _indices.AsSpan(0, IndexCount);

    public int VertexCount { get; }
    public int IndexCount { get; }
    public int TriangleCount => IndexCount / 3;

    public VoxelMeshData(
        Vector3[] vertices,
        Vector3[] normals,
        byte[] colors,
        ushort[] indices,
        int vertexCount,
        int indexCount)
    {
        if (vertexCount <= 0 ||
            indexCount <= 0 ||
            vertexCount > vertices.Length ||
            vertexCount > normals.Length ||
            vertexCount * 4 > colors.Length ||
            indexCount > indices.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        }

        _vertices = vertices;
        _normals = normals;
        _colors = colors;
        _indices = indices;
        VertexCount = vertexCount;
        IndexCount = indexCount;
    }

    public void ReturnCpuArrays()
    {
        if (Interlocked.Exchange(ref _returned, 1) != 0)
            return;

        ArrayPool<Vector3>.Shared.Return(_vertices);
        ArrayPool<Vector3>.Shared.Return(_normals);
        ArrayPool<byte>.Shared.Return(_colors);
        ArrayPool<ushort>.Shared.Return(_indices);
    }
}

/// <summary>
/// Compact main-thread input: 16^3 block IDs plus a one-cell halo on all sides.
/// Zero is air; non-zero values are byte-sized material IDs.
/// Blocks are X-contiguous: x + y * SampleSide + z * SamplePlane.
/// </summary>
internal sealed class VoxelChunkBuildInput : IDisposable
{
    public const int DefaultCellsPerAxis = SparseVoxelField.ChunkSize;
    public const int HaloSize = 1;

    public readonly byte[] Blocks;

    public int ChunkX { get; }
    public int ChunkY { get; }
    public int ChunkZ { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public int OriginZ { get; }

    public int FieldWidth { get; }
    public int FieldHeight { get; }
    public int FieldDepth { get; }

    public int CellsPerAxis { get; }
    public int SampleSide { get; }
    public int SamplePlane { get; }
    public int TotalSamples { get; }
    public int CutLayer { get; }

    public bool IsCaptured { get; private set; }
    public bool HasPotentialSurface { get; private set; }

    private int _disposed;

    public VoxelChunkBuildInput(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        int cutLayer = int.MinValue,
        int cellsPerAxis = DefaultCellsPerAxis)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (chunkX < 0 || chunkY < 0 || chunkZ < 0)
            throw new ArgumentOutOfRangeException(nameof(chunkX));
        if (cellsPerAxis < 8 || cellsPerAxis > 64 ||
            (cellsPerAxis & (cellsPerAxis - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(cellsPerAxis));

        ChunkX = chunkX;
        ChunkY = chunkY;
        ChunkZ = chunkZ;
        CellsPerAxis = cellsPerAxis;

        OriginX = checked(chunkX * CellsPerAxis);
        OriginY = checked(chunkY * CellsPerAxis);
        OriginZ = checked(chunkZ * CellsPerAxis);

        FieldWidth = field.Width;
        FieldHeight = field.Height;
        FieldDepth = field.Depth;
        CutLayer = cutLayer;

        SampleSide = CellsPerAxis + HaloSize * 2;
        SamplePlane = SampleSide * SampleSide;
        TotalSamples = SamplePlane * SampleSide;
        Blocks = ArrayPool<byte>.Shared.Rent(TotalSamples);
    }

    /// <summary>
    /// Monolithic sequential capture. All rented bytes are overwritten in one
    /// contiguous pass; no per-sample objects, intermediate arrays or batches.
    /// </summary>
    public void CaptureAll(IVoxelFieldSource source)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentNullException.ThrowIfNull(source);

        if (IsCaptured)
            return;

        bool hasSolid;
        if (source is SparseVoxelField voxelField)
        {
            hasSolid = voxelField.CopyBlockIds(
                OriginX - HaloSize,
                OriginY - HaloSize,
                OriginZ - HaloSize,
                SampleSide,
                Blocks.AsSpan(0, TotalSamples));

            if (CutLayer != int.MinValue)
            {
                for (int z = 0; z < SampleSide; z++)
                {
                    for (int y = 0; y < SampleSide; y++)
                    {
                        int worldY = OriginY + y - HaloSize;
                        if (worldY < CutLayer)
                            continue;

                        Blocks.AsSpan(
                            z * SamplePlane + y * SampleSide,
                            SampleSide).Clear();
                    }
                }

                hasSolid = false;
                for (int i = 0; i < TotalSamples; i++)
                    hasSolid |= Blocks[i] != 0;
            }
        }
        else
        {
            hasSolid = false;
            for (int index = 0; index < TotalSamples; index++)
            {
                int x = index % SampleSide;
                int y = index / SampleSide % SampleSide;
                int z = index / SamplePlane;

                int worldX = OriginX + x - HaloSize;
                int worldY = OriginY + y - HaloSize;
                int worldZ = OriginZ + z - HaloSize;

                byte blockId = CutLayer != int.MinValue && worldY >= CutLayer
                    ? (byte)0
                    : source.GetBlockId(worldX, worldY, worldZ);

                Blocks[index] = blockId;
                hasSolid |= blockId != 0;
            }
        }

        HasPotentialSurface = hasSolid;
        IsCaptured = true;
    }

    /// <summary>Returns a local block ID; coordinates -1..16 address the halo.</summary>
    public byte GetBlock(int x, int y, int z)
    {
        x += HaloSize;
        y += HaloSize;
        z += HaloSize;

        if ((uint)x >= SampleSide ||
            (uint)y >= SampleSide ||
            (uint)z >= SampleSide)
            return 0;

        return Blocks[x + y * SampleSide + z * SamplePlane];
    }

    // Mesher coordinates are bounded to -1..16 by its slice loops.
    public byte GetBlockUnchecked(int x, int y, int z) =>
        Blocks[
            (x + HaloSize) +
            (y + HaloSize) * SampleSide +
            (z + HaloSize) * SamplePlane];


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ArrayPool<byte>.Shared.Return(Blocks, clearArray: false);
    }
}

internal readonly record struct VoxelMeshBuildResult(
    VoxelMeshData[] Surface,
    VoxelMeshData[] CutCap);

internal static class VoxelChunkMesher
{
    private const int MaximumVerticesPerMesh = 60_000;
    private const int MaximumIndicesPerMesh = 90_000;

    private static readonly Vector3[] AxisNormals =
    {
        Vector3.UnitX,
        Vector3.UnitY,
        Vector3.UnitZ,
        -Vector3.UnitX,
        -Vector3.UnitY,
        -Vector3.UnitZ
    };

    private sealed class MeshBatch : IDisposable
    {
        private readonly List<VoxelMeshData> _output;

        private Vector3[] _vertices;
        private Vector3[] _normals;
        private byte[] _colors;
        private ushort[] _indices;

        private int _vertexCount;
        private int _indexCount;

        private const int InitialVertices = 512;
        private const int InitialIndices = InitialVertices / 4 * 6;

        public MeshBatch(List<VoxelMeshData> output)
        {
            _output = output;
            RentBuffers(InitialVertices, InitialIndices);
        }

        private void RentBuffers(int vertexCapacity, int indexCapacity)
        {
            _vertices = ArrayPool<Vector3>.Shared.Rent(vertexCapacity);
            _normals = ArrayPool<Vector3>.Shared.Rent(vertexCapacity);
            _colors = ArrayPool<byte>.Shared.Rent(vertexCapacity * 4);
            _indices = ArrayPool<ushort>.Shared.Rent(indexCapacity);
        }

        private void EnsureCapacity(int vertexCount, int indexCount)
        {
            if (_vertices.Length < vertexCount ||
                _normals.Length < vertexCount ||
                _colors.Length < vertexCount * 4)
            {
                int capacity = Math.Min(
                    MaximumVerticesPerMesh,
                    Math.Max(vertexCount, _vertices.Length * 2));
                Vector3[] nextVertices = ArrayPool<Vector3>.Shared.Rent(capacity);
                Vector3[] nextNormals = ArrayPool<Vector3>.Shared.Rent(capacity);
                byte[] nextColors = ArrayPool<byte>.Shared.Rent(capacity * 4);

                Array.Copy(_vertices, nextVertices, _vertexCount);
                Array.Copy(_normals, nextNormals, _vertexCount);
                Array.Copy(_colors, nextColors, _vertexCount * 4);

                ArrayPool<Vector3>.Shared.Return(_vertices);
                ArrayPool<Vector3>.Shared.Return(_normals);
                ArrayPool<byte>.Shared.Return(_colors);
                _vertices = nextVertices;
                _normals = nextNormals;
                _colors = nextColors;
            }

            if (_indices.Length < indexCount)
            {
                int capacity = Math.Min(
                    MaximumIndicesPerMesh,
                    Math.Max(indexCount, _indices.Length * 2));
                ushort[] nextIndices = ArrayPool<ushort>.Shared.Rent(capacity);
                Array.Copy(_indices, nextIndices, _indexCount);
                ArrayPool<ushort>.Shared.Return(_indices);
                _indices = nextIndices;
            }
        }

        public void AddQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            Color color,
            bool frontFace)
        {
            if (_vertexCount + 4 > MaximumVerticesPerMesh ||
                _indexCount + 6 > MaximumIndicesPerMesh)
            {
                Flush();
            }

            EnsureCapacity(_vertexCount + 4, _indexCount + 6);
            ushort start = checked((ushort)_vertexCount);
            AddVertex(p0, normal, color);
            AddVertex(p1, normal, color);
            AddVertex(p2, normal, color);
            AddVertex(p3, normal, color);

            if (frontFace)
            {
                _indices[_indexCount++] = start;
                _indices[_indexCount++] = (ushort)(start + 1);
                _indices[_indexCount++] = (ushort)(start + 2);
                _indices[_indexCount++] = start;
                _indices[_indexCount++] = (ushort)(start + 2);
                _indices[_indexCount++] = (ushort)(start + 3);
            }
            else
            {
                _indices[_indexCount++] = start;
                _indices[_indexCount++] = (ushort)(start + 2);
                _indices[_indexCount++] = (ushort)(start + 1);
                _indices[_indexCount++] = start;
                _indices[_indexCount++] = (ushort)(start + 3);
                _indices[_indexCount++] = (ushort)(start + 2);
            }
        }

        public void Flush()
        {
            if (_vertexCount == 0)
                return;

            _output.Add(new VoxelMeshData(
                _vertices,
                _normals,
                _colors,
                _indices,
                _vertexCount,
                _indexCount));

            RentBuffers(InitialVertices, InitialIndices);
            _vertexCount = 0;
            _indexCount = 0;
        }

        private void AddVertex(Vector3 position, Vector3 normal, Color color)
        {
            _vertices[_vertexCount] = position;
            _normals[_vertexCount] = normal;

            int colorIndex = _vertexCount * 4;
            _colors[colorIndex] = color.R;
            _colors[colorIndex + 1] = color.G;
            _colors[colorIndex + 2] = color.B;
            _colors[colorIndex + 3] = color.A;
            _vertexCount++;
        }

        public void Dispose()
        {
            ArrayPool<Vector3>.Shared.Return(_vertices);
            ArrayPool<Vector3>.Shared.Return(_normals);
            ArrayPool<byte>.Shared.Return(_colors);
            ArrayPool<ushort>.Shared.Return(_indices);
        }
    }

    public static Mesh[] BuildSurface(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        TerrainPalette palette)
    {
        using VoxelChunkBuildInput input = new(field, chunkX, chunkY, chunkZ);
        input.CaptureAll(field);
        return Upload(BuildSurfaceData(input, palette));
    }

    public static Mesh UploadOne(VoxelMeshData source)
    {
        Mesh mesh = new(source.VertexCount, source.TriangleCount);
        try
        {
            mesh.AllocVertices();
            mesh.AllocNormals();
            mesh.AllocColors();
            mesh.AllocIndices();
            source.Vertices.CopyTo(mesh.VerticesAs<Vector3>());
            source.Normals.CopyTo(mesh.NormalsAs<Vector3>());
            source.Colors.CopyTo(mesh.ColorsAs<byte>());
            source.Indices.CopyTo(mesh.IndicesAs<ushort>());
            UploadMesh(ref mesh, false);
            return mesh;
        }
        catch
        {
            UnloadMesh(mesh);
            throw;
        }
    }

    public static Mesh[] Upload(VoxelMeshData[] data)
    {
        if (data.Length == 0)
            return Array.Empty<Mesh>();

        Mesh[] meshes = new Mesh[data.Length];
        int uploaded = 0;

        try
        {
            for (; uploaded < data.Length; uploaded++)
            {
                meshes[uploaded] = UploadOne(data[uploaded]);
                data[uploaded].ReturnCpuArrays();
            }

            return meshes;
        }
        catch
        {
            foreach (VoxelMeshData meshData in data)
                meshData.ReturnCpuArrays();

            for (int i = 0; i < uploaded; i++)
                UnloadMesh(meshes[i]);

            throw;
        }
    }

    /// <summary>
    /// Greedy cube meshing. A mask cell stores a byte block ID; zero means no
    /// face. The parallel sign mask distinguishes +axis and -axis faces.
    /// Identical block IDs and directions merge into one indexed quad.
    /// </summary>
    public static VoxelMeshData[] BuildSurfaceData(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(palette);

        if (!input.IsCaptured)
            throw new InvalidOperationException("Voxel block capture must complete before meshing.");

        if (!input.HasPotentialSurface)
            return Array.Empty<VoxelMeshData>();

        int cells = input.CellsPerAxis;
        int maskLength = cells * cells;

        byte[] mask = ArrayPool<byte>.Shared.Rent(maskLength);
        sbyte[] signs = ArrayPool<sbyte>.Shared.Rent(maskLength);
        List<VoxelMeshData> output = new(2);

        try
        {
            using MeshBatch batch = new(output);

            for (int axis = 0; axis < 3; axis++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int uAxis = (axis + 1) % 3;
                int vAxis = (axis + 2) % 3;

                for (int slice = 0; slice <= cells; slice++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Array.Clear(mask, 0, maskLength);
                    Array.Clear(signs, 0, maskLength);

                    BuildFaceMask(input, mask, signs, axis, slice, cells);

                    GreedyEmitSlice(
                        input,
                        palette,
                        batch,
                        mask,
                        signs,
                        axis,
                        uAxis,
                        vAxis,
                        slice,
                        cells);
                }
            }

            batch.Flush();
            return output.ToArray();
        }
        catch
        {
            foreach (VoxelMeshData mesh in output)
                mesh.ReturnCpuArrays();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(mask, clearArray: false);
            ArrayPool<sbyte>.Shared.Return(signs, clearArray: false);
        }
    }

    private static void BuildFaceMask(
        VoxelChunkBuildInput input,
        byte[] mask,
        sbyte[] signs,
        int axis,
        int slice,
        int cells)
    {
        if (axis == 0)
        {
            for (int z = 0; z < cells; z++)
            {
                int row = z * cells;
                for (int y = 0; y < cells; y++)
                {
                    byte a = input.GetBlockUnchecked(slice - 1, y, z);
                    byte b = input.GetBlockUnchecked(slice, y, z);
                    SetFaceMask(mask, signs, row + y, a, b);
                }
            }
        }
        else if (axis == 1)
        {
            for (int x = 0; x < cells; x++)
            {
                int row = x * cells;
                for (int z = 0; z < cells; z++)
                {
                    byte a = input.GetBlockUnchecked(x, slice - 1, z);
                    byte b = input.GetBlockUnchecked(x, slice, z);
                    SetFaceMask(mask, signs, row + z, a, b);
                }
            }
        }
        else
        {
            for (int y = 0; y < cells; y++)
            {
                int row = y * cells;
                for (int x = 0; x < cells; x++)
                {
                    byte a = input.GetBlockUnchecked(x, y, slice - 1);
                    byte b = input.GetBlockUnchecked(x, y, slice);
                    SetFaceMask(mask, signs, row + x, a, b);
                }
            }
        }
    }

    private static void SetFaceMask(
        byte[] mask,
        sbyte[] signs,
        int index,
        byte blockA,
        byte blockB)
    {
        if (blockA != 0 && blockB == 0)
        {
            mask[index] = blockA;
            signs[index] = 1;
        }
        else if (blockA == 0 && blockB != 0)
        {
            mask[index] = blockB;
            signs[index] = -1;
        }
    }

    private static void GreedyEmitSlice(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        MeshBatch batch,
        byte[] mask,
        sbyte[] signs,
        int axis,
        int uAxis,
        int vAxis,
        int slice,
        int cells)
    {
        for (int v = 0; v < cells; v++)
        {
            int u = 0;
            while (u < cells)
            {
                int index = u + v * cells;
                byte blockId = mask[index];
                sbyte sign = signs[index];

                if (blockId == 0)
                {
                    u++;
                    continue;
                }

                int width = 1;
                while (u + width < cells)
                {
                    int next = index + width;
                    if (mask[next] != blockId || signs[next] != sign)
                        break;
                    width++;
                }

                int height = 1;
                bool canGrow = true;

                while (v + height < cells && canGrow)
                {
                    int row = (v + height) * cells + u;
                    for (int x = 0; x < width; x++)
                    {
                        if (mask[row + x] != blockId ||
                            signs[row + x] != sign)
                        {
                            canGrow = false;
                            break;
                        }
                    }

                    if (canGrow)
                        height++;
                }

                EmitSurfaceQuad(
                    input,
                    palette,
                    batch,
                    axis,
                    uAxis,
                    vAxis,
                    slice,
                    u,
                    v,
                    width,
                    height,
                    sign,
                    blockId);

                for (int row = v; row < v + height; row++)
                {
                    int start = row * cells + u;
                    Array.Clear(mask, start, width);
                    Array.Clear(signs, start, width);
                }

                u += width;
            }
        }
    }

    private static void EmitSurfaceQuad(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        MeshBatch batch,
        int axis,
        int uAxis,
        int vAxis,
        int slice,
        int u,
        int v,
        int width,
        int height,
        sbyte normalSign,
        byte blockId)
    {
        Vector3 origin = new(
            input.OriginX * SparseVoxelField.VoxelSizeMeters,
            input.OriginY * SparseVoxelField.VoxelSizeMeters,
            input.OriginZ * SparseVoxelField.VoxelSizeMeters);

        Vector3 p0 = MakePoint(origin, axis, uAxis, vAxis, slice, u, v);
        Vector3 p1 = MakePoint(origin, axis, uAxis, vAxis, slice, u + width, v);
        Vector3 p2 = MakePoint(origin, axis, uAxis, vAxis, slice, u + width, v + height);
        Vector3 p3 = MakePoint(origin, axis, uAxis, vAxis, slice, u, v + height);

        Vector3 normal = AxisNormals[axis + (normalSign < 0 ? 3 : 0)];
        batch.AddQuad(
            p0,
            p1,
            p2,
            p3,
            normal,
            GetMaterialColor(palette, blockId),
            frontFace: normalSign > 0);
    }

    private static Vector3 MakePoint(
        Vector3 origin,
        int axis,
        int uAxis,
        int vAxis,
        int slice,
        int u,
        int v)
    {
        Vector3 point = origin;
        SetComponent(ref point, axis, GetComponent(origin, axis) +
            slice * SparseVoxelField.VoxelSizeMeters);
        SetComponent(ref point, uAxis, GetComponent(origin, uAxis) +
            u * SparseVoxelField.VoxelSizeMeters);
        SetComponent(ref point, vAxis, GetComponent(origin, vAxis) +
            v * SparseVoxelField.VoxelSizeMeters);
        return point;
    }

    private static void GetCellCoordinates(
        int axis,
        int axisCoordinate,
        int u,
        int v,
        out int x,
        out int y,
        out int z)
    {
        x = y = z = 0;
        int uAxis = (axis + 1) % 3;
        int vAxis = (axis + 2) % 3;

        SetCoordinate(axis, axisCoordinate, ref x, ref y, ref z);
        SetCoordinate(uAxis, u, ref x, ref y, ref z);
        SetCoordinate(vAxis, v, ref x, ref y, ref z);
    }

    private static void SetCoordinate(
        int axis,
        int value,
        ref int x,
        ref int y,
        ref int z)
    {
        switch (axis)
        {
            case 0: x = value; break;
            case 1: y = value; break;
            default: z = value; break;
        }
    }

    private static float GetComponent(Vector3 value, int axis) =>
        axis switch
        {
            0 => value.X,
            1 => value.Y,
            _ => value.Z
        };

    private static void SetComponent(ref Vector3 value, int axis, float component)
    {
        switch (axis)
        {
            case 0: value.X = component; break;
            case 1: value.Y = component; break;
            default: value.Z = component; break;
        }
    }

    private static Color GetMaterialColor(TerrainPalette palette, byte blockId)
    {
        return blockId switch
        {
            2 => new Color(112, 91, 68, 255),
            3 => new Color(82, 112, 136, 255),
            _ => palette.GetSurfaceColor(0f, blockId)
        };
    }
}
