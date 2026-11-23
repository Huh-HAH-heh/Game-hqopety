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
/// Immutable-to-worker fixed-resolution render-region snapshot.
/// Density sampling uses a one-sample halo; render regions may group many
/// 16^3 storage chunks without changing the 0.1 m voxel resolution.
/// </summary>
internal sealed class VoxelChunkBuildInput
{
    internal const int DefaultCellsPerAxis = SparseVoxelField.ChunkSize;
    internal const int HaloSize = 1;
    private const int MaximumCellsPerAxis = 128;

    public readonly float[] DensitySamples;
    public readonly ushort[] Materials;

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
    public int SampleCount { get; }
    public int CellCount { get; }

    public int CaptureCursor { get; private set; }
    public int CaptureCount => SampleCount + CellCount;
    public bool DensityCaptureComplete => CaptureCursor >= SampleCount;
    public bool IsComplete => CaptureCursor >= CaptureCount;
    public bool HasPotentialSurface =>
        _minimumDensity <= 0f && _maximumDensity >= 0f;

    private float _minimumDensity = float.PositiveInfinity;
    private float _maximumDensity = float.NegativeInfinity;

    public VoxelChunkBuildInput(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        int cellsPerAxis = DefaultCellsPerAxis)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (chunkX < 0 || chunkY < 0 || chunkZ < 0)
            throw new ArgumentOutOfRangeException(nameof(chunkX));
        if (cellsPerAxis <= 0 || cellsPerAxis > MaximumCellsPerAxis)
            throw new ArgumentOutOfRangeException(nameof(cellsPerAxis));

        ChunkX = chunkX;
        ChunkY = chunkY;
        ChunkZ = chunkZ;
        CellsPerAxis = cellsPerAxis;
        OriginX = checked(chunkX * cellsPerAxis);
        OriginY = checked(chunkY * cellsPerAxis);
        OriginZ = checked(chunkZ * cellsPerAxis);
        FieldWidth = field.Width;
        FieldHeight = field.Height;
        FieldDepth = field.Depth;

        SampleSide = checked(cellsPerAxis + HaloSize * 2);
        SamplePlane = checked(SampleSide * SampleSide);
        SampleCount = checked(SamplePlane * SampleSide);
        CellCount = checked(cellsPerAxis * cellsPerAxis * cellsPerAxis);

        DensitySamples = new float[SampleCount];
        Materials = new ushort[CellCount];
    }

    public int CaptureNext(IVoxelFieldSource field, int maximumValues) =>
        CaptureNext(field, maximumValues, captureMaterials: true);

    public int CaptureNext(
        IVoxelFieldSource field,
        int maximumValues,
        bool captureMaterials)
    {
        if (maximumValues <= 0 || IsComplete)
            return 0;

        if (CaptureCursor >= SampleCount && !captureMaterials)
        {
            CaptureCursor = CaptureCount;
            return 0;
        }

        int end = captureMaterials
            ? Math.Min(CaptureCount, CaptureCursor + maximumValues)
            : Math.Min(SampleCount, CaptureCursor + maximumValues);

        int captured = 0;
        while (CaptureCursor < end)
        {
            int index = CaptureCursor++;

            if (index < SampleCount)
            {
                int x = index % SampleSide;
                int y = index / SampleSide % SampleSide;
                int z = index / SamplePlane;

                int worldX = OriginX + x - HaloSize;
                int worldY = OriginY + y - HaloSize;
                int worldZ = OriginZ + z - HaloSize;

                float density = field.GetDensity(worldX, worldY, worldZ);
                DensitySamples[index] = density;
                _minimumDensity = MathF.Min(_minimumDensity, density);
                _maximumDensity = MathF.Max(_maximumDensity, density);
            }
            else
            {
                int cellIndex = index - SampleCount;
                int x = cellIndex % CellsPerAxis;
                int y = cellIndex / CellsPerAxis % CellsPerAxis;
                int z = cellIndex / (CellsPerAxis * CellsPerAxis);

                if (GetDensity(x, y, z) > 0f)
                {
                    Materials[cellIndex] = field.GetMaterialId(
                        OriginX + x,
                        OriginY + y,
                        OriginZ + z);
                }
            }

            captured++;
        }

        return captured;
    }

    public float GetDensity(int x, int y, int z)
    {
        x += HaloSize;
        y += HaloSize;
        z += HaloSize;

        if ((uint)x >= SampleSide ||
            (uint)y >= SampleSide ||
            (uint)z >= SampleSide)
            return -1f;

        return DensitySamples[x + y * SampleSide + z * SamplePlane];
    }

    public ushort GetMaterialId(int x, int y, int z)
    {
        if ((uint)x >= CellsPerAxis ||
            (uint)y >= CellsPerAxis ||
            (uint)z >= CellsPerAxis)
            return 0;

        return Materials[x + y * CellsPerAxis + z * CellsPerAxis * CellsPerAxis];
    }

    /// <summary>
    /// Trilinear sample in voxel-centre coordinates for the horizontal CUT cap.
    /// </summary>
    public float SampleDensity(float x, float y, float z)
    {
        float sx = x - OriginX + HaloSize;
        float sy = y - OriginY + HaloSize;
        float sz = z - OriginZ + HaloSize;

        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        int z0 = (int)MathF.Floor(sz);
        float tx = sx - x0;
        float ty = sy - y0;
        float tz = sz - z0;

        float c000 = GetSample(x0, y0, z0);
        float c100 = GetSample(x0 + 1, y0, z0);
        float c010 = GetSample(x0, y0 + 1, z0);
        float c110 = GetSample(x0 + 1, y0 + 1, z0);
        float c001 = GetSample(x0, y0, z0 + 1);
        float c101 = GetSample(x0 + 1, y0, z0 + 1);
        float c011 = GetSample(x0, y0 + 1, z0 + 1);
        float c111 = GetSample(x0 + 1, y0 + 1, z0 + 1);

        float x00 = Lerp(c000, c100, tx);
        float x10 = Lerp(c010, c110, tx);
        float x01 = Lerp(c001, c101, tx);
        float x11 = Lerp(c011, c111, tx);

        return Lerp(
            Lerp(x00, x10, ty),
            Lerp(x01, x11, ty),
            tz);
    }

    public ushort GetMaterialAtWorldPosition(float x, float y, float z)
    {
        int localX = (int)MathF.Floor(x - OriginX);
        int localY = (int)MathF.Floor(y - OriginY);
        int localZ = (int)MathF.Floor(z - OriginZ);
        return GetMaterialId(localX, localY, localZ);
    }

    private float GetSample(int x, int y, int z)
    {
        if ((uint)x >= SampleSide ||
            (uint)y >= SampleSide ||
            (uint)z >= SampleSide)
            return -1f;

        return DensitySamples[x + y * SampleSide + z * SamplePlane];
    }

    private static float Lerp(float a, float b, float t) =>
        a + (b - a) * t;
}

internal readonly record struct VoxelMeshBuildResult(
    VoxelMeshData[] Surface,
    VoxelMeshData[] CutCap);

internal static class VoxelChunkMesher
{
    private const int MaximumVerticesPerMesh = 60_000;

    private readonly struct FaceKey : IEquatable<FaceKey>
    {
        public readonly ushort MaterialId;
        public readonly sbyte NormalSign;
        public readonly bool Visible;

        public FaceKey(ushort materialId, sbyte normalSign)
        {
            MaterialId = materialId;
            NormalSign = normalSign;
            Visible = true;
        }

        public bool Equals(FaceKey other) =>
            Visible == other.Visible &&
            MaterialId == other.MaterialId &&
            NormalSign == other.NormalSign;
    }

    private sealed class MeshBatch
    {
        private readonly List<Vector3> _vertices = new(MaximumVerticesPerMesh);
        private readonly List<Vector3> _normals = new(MaximumVerticesPerMesh);
        private readonly List<byte> _colors = new(MaximumVerticesPerMesh * 4);
        private readonly List<ushort> _indices = new(MaximumVerticesPerMesh * 3);
        private readonly List<VoxelMeshData> _output;

        public MeshBatch(List<VoxelMeshData> output) => _output = output;

        public void AddQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            Color color)
        {
            if (_vertices.Count + 4 > MaximumVerticesPerMesh)
                Flush();

            ushort start = checked((ushort)_vertices.Count);

            AddVertex(p0, normal, color);
            AddVertex(p1, normal, color);
            AddVertex(p2, normal, color);
            AddVertex(p3, normal, color);

            _indices.Add(start);
            if (normal.X + normal.Y + normal.Z >= 0f)
            {
                _indices.Add((ushort)(start + 1));
                _indices.Add((ushort)(start + 2));
                _indices.Add(start);
                _indices.Add((ushort)(start + 2));
                _indices.Add((ushort)(start + 3));
            }
            else
            {
                _indices.Add((ushort)(start + 2));
                _indices.Add((ushort)(start + 1));
                _indices.Add(start);
                _indices.Add((ushort)(start + 3));
                _indices.Add((ushort)(start + 2));
            }
        }

        public void Flush()
        {
            int vertexCount = _vertices.Count;
            if (vertexCount == 0)
                return;

            int indexCount = _indices.Count;
            Vector3[]? vertices = null;
            Vector3[]? normals = null;
            byte[]? colors = null;
            ushort[]? indices = null;

            try
            {
                vertices = ArrayPool<Vector3>.Shared.Rent(vertexCount);
                normals = ArrayPool<Vector3>.Shared.Rent(vertexCount);
                colors = ArrayPool<byte>.Shared.Rent(vertexCount * 4);
                indices = ArrayPool<ushort>.Shared.Rent(indexCount);

                _vertices.CopyTo(vertices, 0);
                _normals.CopyTo(normals, 0);
                _colors.CopyTo(colors, 0);
                _indices.CopyTo(indices, 0);

                _output.Add(new VoxelMeshData(
                    vertices,
                    normals,
                    colors,
                    indices,
                    vertexCount,
                    indexCount));

                vertices = null;
                normals = null;
                colors = null;
                indices = null;
            }
            finally
            {
                if (vertices != null)
                    ArrayPool<Vector3>.Shared.Return(vertices);
                if (normals != null)
                    ArrayPool<Vector3>.Shared.Return(normals);
                if (colors != null)
                    ArrayPool<byte>.Shared.Return(colors);
                if (indices != null)
                    ArrayPool<ushort>.Shared.Return(indices);
            }

            _vertices.Clear();
            _normals.Clear();
            _colors.Clear();
            _indices.Clear();
        }

        private void AddVertex(Vector3 position, Vector3 normal, Color color)
        {
            _vertices.Add(position);
            _normals.Add(normal);
            _colors.Add(color.R);
            _colors.Add(color.G);
            _colors.Add(color.B);
            _colors.Add(color.A);
        }
    }

    public static Mesh[] BuildSurface(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        TerrainPalette palette)
    {
        VoxelChunkBuildInput input = new(field, chunkX, chunkY, chunkZ);
        while (!input.IsComplete)
            input.CaptureNext(field, input.CaptureCount);

        return Upload(BuildSurfaceData(input, palette));
    }

    public static Mesh UploadOne(VoxelMeshData source)
    {
        Mesh mesh = new(source.VertexCount, source.TriangleCount);
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

    public static VoxelMeshData[] BuildSurfaceData(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(palette);

        int cells = input.CellsPerAxis;
        List<VoxelMeshData> output = new(2);
        MeshBatch batch = new(output);
        Span<FaceKey> mask = stackalloc FaceKey[cells * cells];

        try
        {
            // Axis order is cyclic: u x v always points along +axis.
            for (int axis = 0; axis < 3; axis++)
            {
                int uAxis = (axis + 1) % 3;
                int vAxis = (axis + 2) % 3;

                for (int slice = 0; slice <= cells; slice++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    mask.Clear();

                    for (int v = 0; v < cells; v++)
                    for (int u = 0; u < cells; u++)
                    {
                        GetCellCoordinates(
                            axis, slice - 1, u, v,
                            out int ax, out int ay, out int az);
                        GetCellCoordinates(
                            axis, slice, u, v,
                            out int bx, out int by, out int bz);

                        bool aSolid = input.GetDensity(ax, ay, az) > 0f;
                        bool bSolid = input.GetDensity(bx, by, bz) > 0f;
                        if (aSolid == bSolid)
                            continue;

                        // A chunk emits only faces owned by one of its 16^3
                        // solid cells. Halo cells decide continuity but never
                        // duplicate a neighbouring chunk's boundary face.
                        int solidX = aSolid ? ax : bx;
                        int solidY = aSolid ? ay : by;
                        int solidZ = aSolid ? az : bz;
                        int solidAxis = axis == 0 ? solidX :
                            axis == 1 ? solidY : solidZ;

                        if ((uint)solidAxis  >= cells)
                            continue;

                        ushort materialId = input.GetMaterialId(
                            solidX, solidY, solidZ);
                        mask[u + v * cells] = new FaceKey(
                            materialId,
                            aSolid ? (sbyte)1 : (sbyte)-1);
                    }

                    GreedyEmitSlice(
                        input,
                        palette,
                        batch,
                        mask,
                        axis,
                        uAxis,
                        vAxis,
                        slice);
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
    }

    /// <summary>
    /// Greedy 2D cap: sample the scalar field at the cut plane, then merge
    /// adjacent solid cells of the same material into rectangular +Y quads.
    /// The cap uses the same Step=1 sample lattice as the terrain surface.
    /// </summary>
    public static VoxelMeshData[] BuildCutCapData(
        VoxelChunkBuildInput input,
        float cutHeightMeters,
        TerrainPalette palette,
        CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(cutHeightMeters))
            return Array.Empty<VoxelMeshData>();

        float cutSampleY =
            cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f;
        if (cutSampleY < -0.5f ||
            cutSampleY >= input.FieldHeight - 0.5f)
            return Array.Empty<VoxelMeshData>();

        int cells = input.CellsPerAxis;
        Span<FaceKey> mask = stackalloc FaceKey[cells * cells];
        List<VoxelMeshData> output = new(1);
        MeshBatch batch = new(output);

        try
        {
            for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                float density = input.SampleDensity(
                    input.OriginX + x + 0.5f,
                    cutSampleY,
                    input.OriginZ + z + 0.5f);

                if (density <= 0f || !float.IsFinite(density))
                    continue;

                ushort materialId = input.GetMaterialAtWorldPosition(
                    input.OriginX + x + 0.5f,
                    cutSampleY,
                    input.OriginZ + z + 0.5f);

                mask[x + z * cells] = new FaceKey(materialId, 1);
            }

            GreedyEmitCap(input, palette, batch, mask, cutHeightMeters, cells);
            batch.Flush();
            return output.ToArray();
        }
        catch
        {
            foreach (VoxelMeshData mesh in output)
                mesh.ReturnCpuArrays();
            throw;
        }
    }

    private static void GreedyEmitSlice(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        MeshBatch batch,
        Span<FaceKey> mask,
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
                FaceKey key = mask[u + v * cells];
                if (!key.Visible)
                {
                    u++;
                    continue;
                }

                int width = 1;
                while (u + width < cells &&
                    mask[u + width + v * cells].Equals(key))
                {
                    width++;
                }

                int height = 1;
                bool canGrow = true;
                while (v + height < cells && canGrow)
                {
                    int row = (v + height) * cells + u;
                    for (int x = 0; x < width; x++)
                    {
                        if (!mask[row + x].Equals(key))
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
                    key);

                for (int row = v; row < v + height; row++)
                {
                    int start = row * cells + u;
                    for (int x = 0; x < width; x++)
                        mask[start + x] = default;
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
        FaceKey key)
    {
        Vector3 origin = new(
            input.OriginX,
            input.OriginY,
            input.OriginZ);

        Vector3 p0 = MakePoint(origin, axis, uAxis, vAxis, slice, u, v);
        Vector3 p1 = MakePoint(origin, axis, uAxis, vAxis, slice, u + width, v);
        Vector3 p2 = MakePoint(origin, axis, uAxis, vAxis, slice, u + width, v + height);
        Vector3 p3 = MakePoint(origin, axis, uAxis, vAxis, slice, u, v + height);

        Vector3 normal = axis switch
        {
            0 => new Vector3(key.NormalSign, 0f, 0f),
            1 => new Vector3(0f, key.NormalSign, 0f),
            _ => new Vector3(0f, 0f, key.NormalSign)
        };

        batch.AddQuad(
            p0 * SparseVoxelField.VoxelSizeMeters,
            p1 * SparseVoxelField.VoxelSizeMeters,
            p2 * SparseVoxelField.VoxelSizeMeters,
            p3 * SparseVoxelField.VoxelSizeMeters,
            normal,
            GetMaterialColor(palette, key.MaterialId));
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
        SetComponent(ref point, axis, GetComponent(origin, axis) + slice);
        SetComponent(ref point, uAxis, GetComponent(origin, uAxis) + u);
        SetComponent(ref point, vAxis, GetComponent(origin, vAxis) + v);
        return point;
    }

    private static void GreedyEmitCap(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        MeshBatch batch,
        Span<FaceKey> mask,
        float cutHeightMeters,
        int cells)
    {
        for (int z = 0; z < cells; z++)
        {
            int x = 0;
            while (x < cells)
            {
                FaceKey key = mask[x + z * cells];
                if (!key.Visible)
                {
                    x++;
                    continue;
                }

                int width = 1;
                while (x + width < cells &&
                    mask[x + width + z * cells].Equals(key))
                {
                    width++;
                }

                int height = 1;
                bool canGrow = true;
                while (z + height < cells && canGrow)
                {
                    int row = (z + height) * cells + x;
                    for (int i = 0; i < width; i++)
                    {
                        if (!mask[row + i].Equals(key))
                        {
                            canGrow = false;
                            break;
                        }
                    }

                    if (canGrow)
                        height++;
                }

                float y = cutHeightMeters + 0.006f;
                float minX = (input.OriginX + x) * SparseVoxelField.VoxelSizeMeters;
                float maxX = (input.OriginX + x + width) * SparseVoxelField.VoxelSizeMeters;
                float minZ = (input.OriginZ + z) * SparseVoxelField.VoxelSizeMeters;
                float maxZ = (input.OriginZ + z + height) * SparseVoxelField.VoxelSizeMeters;

                batch.AddQuad(
                    new Vector3(minX, y, minZ),
                    new Vector3(maxX, y, minZ),
                    new Vector3(maxX, y, maxZ),
                    new Vector3(minX, y, maxZ),
                    Vector3.UnitY,
                    GetMaterialColor(palette, key.MaterialId));

                for (int row = z; row < z + height; row++)
                {
                    int start = row * cells + x;
                    for (int i = 0; i < width; i++)
                        mask[start + i] = default;
                }

                x += width;
            }
        }
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

    private static Color GetMaterialColor(TerrainPalette palette, ushort materialId)
    {
        return materialId switch
        {
            2 => new Color(112, 91, 68, 255),
            3 => new Color(82, 112, 136, 255),
            _ => palette.GetSurfaceColor(0f, materialId)
        };
    }
}
