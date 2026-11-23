using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Extracts an isosurface from the 3D density field. This is not a height-column
/// mesher: every output triangle is generated from eight 3D density samples and
/// the same field is used for horizontal cut caps.
/// </summary>
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
/// Immutable-to-worker, incrementally captured source data for one terrain
/// chunk. Capture reads the live field only on the render thread; workers never
/// touch WorldMap or mutable voxel dictionaries.
/// </summary>
internal sealed class VoxelChunkBuildInput
{
    internal const int CellsPerAxis = SparseVoxelField.ChunkSize;
    private const int Cells = CellsPerAxis;
    private const int HaloSize = 2;
    private const int SampleSide = Cells + HaloSize * 2;
    private const int SamplePlane = SampleSide * SampleSide;
    private const int SampleCount = SampleSide * SampleSide * SampleSide;
    private const int CellCount = Cells * Cells * Cells;

    public readonly float[] DensitySamples = new float[SampleCount];
    public readonly ushort[] Materials = new ushort[CellCount];

    public int ChunkX { get; }
    public int ChunkY { get; }
    public int ChunkZ { get; }
    public int Step { get; }
    public int OriginX { get; }
    public int OriginY { get; }
    public int OriginZ { get; }
    public int FieldWidth { get; }
    public int FieldHeight { get; }
    public int FieldDepth { get; }
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
        int step)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
        ChunkZ = chunkZ;
        Step = step;
        OriginX = chunkX * Cells * step;
        OriginY = chunkY * Cells * step;
        OriginZ = chunkZ * Cells * step;
        FieldWidth = field.Width;
        FieldHeight = field.Height;
        FieldDepth = field.Depth;
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
                int worldX = SparseVoxelField.GetChunkSampleCoordinate(ChunkX, x, Step, HaloSize);
                int worldY = SparseVoxelField.GetChunkSampleCoordinate(ChunkY, y, Step, HaloSize);
                int worldZ = SparseVoxelField.GetChunkSampleCoordinate(ChunkZ, z, Step, HaloSize);
                float density = field.GetDensity(worldX, worldY, worldZ);
                DensitySamples[index] = density;

                // The symmetric two-sample halo supplies central gradients
                // for the chunk's border cells and their immediate neighbours.
                // Only the 17^3 core lattice corners participate in empty-chunk
                // detection; halo values must not create false surfaces at world bounds.
                if (x >= HaloSize && x <= Cells + HaloSize &&
                    y >= HaloSize && y <= Cells + HaloSize &&
                    z >= HaloSize && z <= Cells + HaloSize)
                {
                    _minimumDensity = MathF.Min(_minimumDensity, density);
                    _maximumDensity = MathF.Max(_maximumDensity, density);
                }
            }
            else
            {
                int cellIndex = index - SampleCount;
                int x = cellIndex % Cells;
                int y = cellIndex / Cells % Cells;
                int z = cellIndex / (Cells * Cells);
                int worldX = Math.Clamp(OriginX + x * Step + Step / 2, 0, field.Width - 1);
                int worldY = Math.Clamp(OriginY + y * Step + Step / 2, 0, field.Height - 1);
                int worldZ = Math.Clamp(OriginZ + z * Step + Step / 2, 0, field.Depth - 1);
                Materials[cellIndex] = field.GetMaterialId(worldX, worldY, worldZ);
            }
            captured++;
        }

        return captured;
    }

    public ushort GetMaterialId(int x, int y, int z)
    {
        if ((uint)x >= Cells || (uint)y >= Cells || (uint)z >= Cells)
            return 0;
        return Materials[x + y * Cells + z * Cells * Cells];
    }

    public ushort GetMaterialAtWorldPosition(float x, float y, float z)
    {
        int localX = (int)MathF.Floor((x - OriginX) / Step);
        int localY = (int)MathF.Floor((y - OriginY) / Step);
        int localZ = (int)MathF.Floor((z - OriginZ) / Step);
        return GetMaterialId(localX, localY, localZ);
    }

    public float SampleDensity(float x, float y, float z)
    {
        float sx = (x - OriginX) / Step + HaloSize;
        float sy = (y - OriginY) / Step + HaloSize;
        float sz = (z - OriginZ) / Step + HaloSize;
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
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    private float GetSample(int x, int y, int z)
    {
        if ((uint)x >= SampleSide || (uint)y >= SampleSide || (uint)z >= SampleSide)
            return -1f;
        return DensitySamples[x + y * SampleSide + z * SamplePlane];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

internal readonly record struct VoxelMeshBuildResult(
    VoxelMeshData[] Surface,
    VoxelMeshData[] CutCap);

internal static class VoxelChunkMesher
{
    private const int CellsPerAxis = VoxelChunkBuildInput.CellsPerAxis;
    private const int HaloSize = 2;
    private const int SampleSide = CellsPerAxis + HaloSize * 2;
    private const int SamplePlane = SampleSide * SampleSide;
    private const int SampleCount = SampleSide * SampleSide * SampleSide;
    private const int MaximumVerticesPerMesh = 60_000;

    private static readonly int[] CornerX = { 0, 1, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] CornerY = { 0, 0, 1, 1, 0, 0, 1, 1 };
    private static readonly int[] CornerZ = { 0, 0, 0, 0, 1, 1, 1, 1 };

    private static readonly int[] CubeEdges =
    {
        0, 1, 1, 2, 2, 3, 3, 0,
        4, 5, 5, 6, 6, 7, 7, 4,
        0, 4, 1, 5, 2, 6, 3, 7
    };

    private readonly record struct SurfaceVertex(Vector3 Position, Vector3 Normal);
    private readonly record struct SlicePoint(Vector3 Position, float Density);

    private sealed class MeshBatch
    {
        private readonly List<Vector3> _vertices = new(MaximumVerticesPerMesh);
        private readonly List<Vector3> _normals = new(MaximumVerticesPerMesh);
        private readonly List<Color> _colors = new(MaximumVerticesPerMesh);
        private readonly List<ushort> _indices = new(MaximumVerticesPerMesh);
        private readonly List<VoxelMeshData> _output;

        public MeshBatch(List<VoxelMeshData> output) => _output = output;

        public void AddTriangle(
            SurfaceVertex a,
            SurfaceVertex b,
            SurfaceVertex c,
            Color color,
            Vector3 fallbackNormal)
        {
            if (!IsFinite(a.Position) || !IsFinite(b.Position) || !IsFinite(c.Position))
                return;

            Vector3 face = Vector3.Cross(
                b.Position - a.Position,
                c.Position - a.Position);
            float faceLengthSquared = face.LengthSquared();
            if (!float.IsFinite(faceLengthSquared) || faceLengthSquared < 1e-12f)
                return;

            Vector3 normal = NormalizeOr(
                fallbackNormal,
                NormalizeOr(face, Vector3.UnitY));
            if (Vector3.Dot(face, normal) < 0f)
                (b, c) = (c, b);

            if (_vertices.Count + 3 > MaximumVerticesPerMesh)
                Flush();

            ushort start = checked((ushort)_vertices.Count);
            AddVertex(a, color, normal);
            AddVertex(b, color, normal);
            AddVertex(c, color, normal);
            _indices.Add(start);
            _indices.Add(checked((ushort)(start + 1)));
            _indices.Add(checked((ushort)(start + 2)));
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
                _indices.CopyTo(indices, 0);

                for (int i = 0; i < vertexCount; i++)
                {
                    int offset = i * 4;
                    colors[offset] = _colors[i].R;
                    colors[offset + 1] = _colors[i].G;
                    colors[offset + 2] = _colors[i].B;
                    colors[offset + 3] = _colors[i].A;
                }

                _output.Add(new VoxelMeshData(
                    vertices,
                    normals,
                    colors,
                    indices,
                    vertexCount,
                    indexCount));
            }
            catch
            {
                if (vertices != null)
                    ArrayPool<Vector3>.Shared.Return(vertices);
                if (normals != null)
                    ArrayPool<Vector3>.Shared.Return(normals);
                if (colors != null)
                    ArrayPool<byte>.Shared.Return(colors);
                if (indices != null)
                    ArrayPool<ushort>.Shared.Return(indices);
                throw;
            }

            _vertices.Clear();
            _normals.Clear();
            _colors.Clear();
            _indices.Clear();
        }

        private void AddVertex(SurfaceVertex vertex, Color color, Vector3 fallbackNormal)
        {
            _vertices.Add(vertex.Position);
            _normals.Add(NormalizeOr(vertex.Normal, fallbackNormal));
            _colors.Add(color);
        }
    }

    public static Mesh[] BuildSurface(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        int step,
        TerrainPalette palette)
    {
        VoxelChunkBuildInput input = new(field, chunkX, chunkY, chunkZ, step);
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
            for (int i = 0; i < data.Length; i++)
                data[i].ReturnCpuArrays();

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
        int step = input.Step;
        int originX = input.OriginX;
        int originY = input.OriginY;
        int originZ = input.OriginZ;
        float[] density = input.DensitySamples;

        int gridSide = CellsPerAxis + 1;
        int gridPlane = gridSide * gridSide;
        int gridCount = gridSide * gridPlane;
        SurfaceVertex[] cellVertices = new SurfaceVertex[gridCount];
        bool[] hasVertex = new bool[gridCount];

        Span<Vector3> positions = stackalloc Vector3[8];
        Span<Vector3> normals = stackalloc Vector3[8];
        Span<float> values = stackalloc float[8];

        // Build the cell vertices in the one-cell skirt around this chunk.
        // All four vertices required by an owned lattice edge are therefore
        // available even when that edge touches a chunk border.
        for (int z = -1; z < CellsPerAxis; z++)
        for (int y = -1; y < CellsPerAxis; y++)
        for (int x = -1; x < CellsPerAxis; x++)
        {
            if (x == -1)
                cancellationToken.ThrowIfCancellationRequested();

            bool hasSolid = false;
            bool hasAir = false;
            for (int corner = 0; corner < 8; corner++)
            {
                int sx = x + HaloSize + CornerX[corner];
                int sy = y + HaloSize + CornerY[corner];
                int sz = z + HaloSize + CornerZ[corner];
                float value = density[GetSampleIndex(sx, sy, sz)];
                values[corner] = value;
                hasSolid |= value > 0f;
                hasAir |= value <= 0f;

                positions[corner] = new Vector3(
                    (originX + (x + CornerX[corner]) * step + 0.5f) *
                        SparseVoxelField.VoxelSizeMeters,
                    (originY + (y + CornerY[corner]) * step + 0.5f) *
                        SparseVoxelField.VoxelSizeMeters,
                    (originZ + (z + CornerZ[corner]) * step + 0.5f) *
                        SparseVoxelField.VoxelSizeMeters);

                float gx = density[GetSampleIndex(sx + 1, sy, sz)] -
                    density[GetSampleIndex(sx - 1, sy, sz)];
                float gy = density[GetSampleIndex(sx, sy + 1, sz)] -
                    density[GetSampleIndex(sx, sy - 1, sz)];
                float gz = density[GetSampleIndex(sx, sy, sz + 1)] -
                    density[GetSampleIndex(sx, sy, sz - 1)];
                normals[corner] = NormalizeOrZero(new Vector3(-gx, -gy, -gz));
            }

            if (!hasSolid || !hasAir)
                continue;

            Vector3 positionSum = Vector3.Zero;
            Vector3 normalSum = Vector3.Zero;
            int intersections = 0;
            for (int edge = 0; edge < CubeEdges.Length; edge += 2)
            {
                int a = CubeEdges[edge];
                int b = CubeEdges[edge + 1];
                float da = values[a];
                float db = values[b];
                if ((da > 0f) == (db > 0f))
                    continue;

                float denominator = da - db;
                float t = MathF.Abs(denominator) > 1e-8f
                    ? Math.Clamp(da / denominator, 0f, 1f)
                    : 0.5f;
                positionSum += Vector3.Lerp(positions[a], positions[b], t);
                normalSum += NormalizeOrZero(
                    Vector3.Lerp(normals[a], normals[b], t));
                intersections++;
            }

            if (intersections == 0)
                continue;

            int vertexIndex = GetSurfaceCellIndex(x, y, z, gridSide);
            cellVertices[vertexIndex] = new SurfaceVertex(
                positionSum / intersections,
                NormalizeOrZero(normalSum));
            hasVertex[vertexIndex] = true;
        }

        List<VoxelMeshData> output = new(2);
        MeshBatch batch = new(output);

        try
            {
            // Each sign-changing density edge belongs to exactly one chunk: the
            // chunk containing its lower-coordinate sample. Neighbouring chunks
            // use identical source samples and the same edge ownership rule.
            for (int z = 0; z < CellsPerAxis; z++)
            for (int y = 0; y < CellsPerAxis; y++)
            for (int x = 0; x < CellsPerAxis; x++)
            {
                if (x == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int sx = x + HaloSize;
                int sy = y + HaloSize;
                int sz = z + HaloSize;
                float value = density[GetSampleIndex(sx, sy, sz)];

                float nextX = density[GetSampleIndex(sx + 1, sy, sz)];
                if ((value > 0f) != (nextX > 0f))
                {
                    AddSurfaceQuad(
                        input, palette, batch, cellVertices, hasVertex, gridSide,
                        x, y - 1, z - 1,
                        x, y, z - 1,
                        x, y, z,
                        x, y - 1, z,
                        Vector3.UnitX * (value > 0f ? 1f : -1f));
                }

                float nextY = density[GetSampleIndex(sx, sy + 1, sz)];
                if ((value > 0f) != (nextY > 0f))
                {
                    AddSurfaceQuad(
                        input, palette, batch, cellVertices, hasVertex, gridSide,
                        x - 1, y, z - 1,
                        x, y, z - 1,
                        x, y, z,
                        x - 1, y, z,
                        Vector3.UnitY * (value > 0f ? 1f : -1f));
                }

                float nextZ = density[GetSampleIndex(sx, sy, sz + 1)];
                if ((value > 0f) != (nextZ > 0f))
                {
                    AddSurfaceQuad(
                        input, palette, batch, cellVertices, hasVertex, gridSide,
                        x - 1, y - 1, z,
                        x, y - 1, z,
                        x, y, z,
                        x - 1, y, z,
                        Vector3.UnitZ * (value > 0f ? 1f : -1f));
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

    private static int GetSurfaceCellIndex(int x, int y, int z, int side) =>
        (x + 1) + (y + 1) * side + (z + 1) * side * side;

    private static void AddSurfaceQuad(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        MeshBatch batch,
        SurfaceVertex[] vertices,
        bool[] hasVertex,
        int gridSide,
        int x0, int y0, int z0,
        int x1, int y1, int z1,
        int x2, int y2, int z2,
        int x3, int y3, int z3,
        Vector3 expectedNormal)
    {
        int i0 = GetSurfaceCellIndex(x0, y0, z0, gridSide);
        int i1 = GetSurfaceCellIndex(x1, y1, z1, gridSide);
        int i2 = GetSurfaceCellIndex(x2, y2, z2, gridSide);
        int i3 = GetSurfaceCellIndex(x3, y3, z3, gridSide);
        if (!hasVertex[i0] || !hasVertex[i1] ||
            !hasVertex[i2] || !hasVertex[i3])
            return;

        SurfaceVertex a = vertices[i0];
        SurfaceVertex b = vertices[i1];
        SurfaceVertex c = vertices[i2];
        SurfaceVertex d = vertices[i3];
        Vector3 normal = NormalizeOr(
            a.Normal + b.Normal + c.Normal + d.Normal,
            expectedNormal);

        int materialX = Math.Clamp((x0 + x1 + x2 + x3) / 4, 0, CellsPerAxis - 1);
        int materialY = Math.Clamp((y0 + y1 + y2 + y3) / 4, 0, CellsPerAxis - 1);
        int materialZ = Math.Clamp((z0 + z1 + z2 + z3) / 4, 0, CellsPerAxis - 1);
        Color color = GetMaterialColor(
            palette,
            input.GetMaterialId(materialX, materialY, materialZ));

        batch.AddTriangle(a, b, c, color, normal);
        batch.AddTriangle(a, c, d, color, normal);
    }

    public static Mesh[] BuildCutCap(
        SparseVoxelField field,
        int chunkX,
        int chunkZ,
        int step,
        float cutHeightMeters,
        TerrainPalette palette)
    {
        int cutSampleY = Math.Clamp(
            (int)MathF.Floor(cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f),
            0,
            field.Height - 1);
        int chunkY = Math.Clamp(
            cutSampleY / (CellsPerAxis * step),
            0,
            (field.Height - 1) / (CellsPerAxis * step));
        VoxelChunkBuildInput input = new(field, chunkX, chunkY, chunkZ, step);
        while (!input.IsComplete)
            input.CaptureNext(field, input.CaptureCount);
        return Upload(BuildCutCapData(input, cutHeightMeters, palette));
    }

    public static VoxelMeshData[] BuildCutCapData(
        VoxelChunkBuildInput field,
        float cutHeightMeters,
        TerrainPalette palette,
        CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(cutHeightMeters))
            return Array.Empty<VoxelMeshData>();

        float cutSampleY = cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f;
        if (cutSampleY <= -0.5f || cutSampleY >= field.FieldHeight - 0.5f)
            return Array.Empty<VoxelMeshData>();

        int step = field.Step;
        int originX = field.OriginX;
        int originZ = field.OriginZ;
        List<VoxelMeshData> output = new(1);
        MeshBatch batch = new(output);

        try
            {
            // Split every cut cell along one consistent diagonal. Clipping convex
            // triangles avoids self-crossing polygons in diagonal-inside cases.
            ReadOnlySpan<int> triangleA = stackalloc int[] { 0, 1, 2 };
            ReadOnlySpan<int> triangleB = stackalloc int[] { 0, 2, 3 };
            Span<SlicePoint> corners = stackalloc SlicePoint[4];
            Span<SlicePoint> triangle = stackalloc SlicePoint[3];
            Span<SlicePoint> polygon = stackalloc SlicePoint[4];

            for (int z = 0; z < CellsPerAxis; z++)
            for (int x = 0; x < CellsPerAxis; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                float x0 = originX + x * step;
                float x1 = x0 + step;
                float z0 = originZ + z * step;
                float z1 = z0 + step;

                corners[0] = MakeSlicePoint(field, x0, cutSampleY, z0, cutHeightMeters);
                corners[1] = MakeSlicePoint(field, x1, cutSampleY, z0, cutHeightMeters);
                corners[2] = MakeSlicePoint(field, x1, cutSampleY, z1, cutHeightMeters);
                corners[3] = MakeSlicePoint(field, x0, cutSampleY, z1, cutHeightMeters);

                ushort materialId = field.GetMaterialAtWorldPosition(
                    (x0 + x1) * 0.5f,
                    cutSampleY,
                    (z0 + z1) * 0.5f);
                Color color = GetMaterialColor(palette, materialId);

                for (int triangleIndex = 0; triangleIndex < 2; triangleIndex++)
                {
                    ReadOnlySpan<int> indices = triangleIndex == 0
                        ? triangleA
                        : triangleB;
                    triangle[0] = corners[indices[0]];
                    triangle[1] = corners[indices[1]];
                    triangle[2] = corners[indices[2]];

                    int polygonCount = ClipSolidTriangle(triangle, polygon);
                    if (polygonCount < 3)
                        continue;

                    SurfaceVertex anchor = new(
                        polygon[0].Position + Vector3.UnitY * 0.006f,
                        Vector3.UnitY);
                    for (int i = 1; i < polygonCount - 1; i++)
                    {
                        SurfaceVertex b = new(
                            polygon[i].Position + Vector3.UnitY * 0.006f,
                            Vector3.UnitY);
                        SurfaceVertex c = new(
                            polygon[i + 1].Position + Vector3.UnitY * 0.006f,
                            Vector3.UnitY);
                        batch.AddTriangle(anchor, b, c, color, Vector3.UnitY);
                    }
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

    private static SlicePoint MakeSlicePoint(
        VoxelChunkBuildInput field,
        float x,
        float ySample,
        float z,
        float cutHeightMeters)
    {
        Vector3 position = new(
            (x + 0.5f) * SparseVoxelField.VoxelSizeMeters,
            cutHeightMeters,
            (z + 0.5f) * SparseVoxelField.VoxelSizeMeters);
        float density = field.SampleDensity(x, ySample, z);
        return new SlicePoint(
            position,
            float.IsFinite(density) ? density : -1f);
    }

    private static int ClipSolidTriangle(
        ReadOnlySpan<SlicePoint> input,
        Span<SlicePoint> output)
    {
        int count = 0;
        for (int i = 0; i < input.Length; i++)
        {
            SlicePoint a = input[i];
            SlicePoint b = input[(i + 1) % input.Length];
            if (!float.IsFinite(a.Density) || !float.IsFinite(b.Density))
                continue;

            bool insideA = a.Density >= 0f;
            bool insideB = b.Density >= 0f;
            if (insideA && insideB)
            {
                AppendUnique(output, ref count, b);
            }
            else if (insideA && !insideB)
            {
                AppendUnique(output, ref count, Intersect(a, b));
            }
            else if (!insideA && insideB)
            {
                AppendUnique(output, ref count, Intersect(a, b));
                AppendUnique(output, ref count, b);
            }
        }

        if (count > 1 &&
            Vector3.DistanceSquared(
                output[0].Position,
                output[count - 1].Position) < 1e-10f)
            count--;

        return count;
    }

    private static void AppendUnique(
        Span<SlicePoint> output,
        ref int count,
        SlicePoint point)
    {
        if (!IsFinite(point.Position))
            return;
        if (count > 0 &&
            Vector3.DistanceSquared(
                output[count - 1].Position,
                point.Position) < 1e-10f)
            return;
        if (count < output.Length)
            output[count++] = point;
    }

    private static SlicePoint Intersect(SlicePoint a, SlicePoint b)
    {
        float denominator = a.Density - b.Density;
        float t = MathF.Abs(denominator) > 1e-8f
            ? Math.Clamp(a.Density / denominator, 0f, 1f)
            : 0.5f;
        Vector3 position = Vector3.Lerp(a.Position, b.Position, t);
        if (!IsFinite(position))
            position = IsFinite(a.Position) ? a.Position : b.Position;
        return new SlicePoint(position, 0f);
    }

    private static int GetSampleIndex(int x, int y, int z) =>
        x + y * SampleSide + z * SamplePlane;

    private static Color GetMaterialColor(TerrainPalette palette, ushort materialId)
    {
        return materialId switch
        {
            2 => new Color(112, 91, 68, 255),
            3 => new Color(82, 112, 136, 255),
            _ => palette.GetSurfaceColor(0f, materialId)
        };
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        if (!IsFinite(value))
            return Vector3.Zero;

        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-12f
            ? value / MathF.Sqrt(lengthSquared)
            : Vector3.Zero;
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        Vector3 normalized = NormalizeOrZero(value);
        if (normalized != Vector3.Zero)
            return normalized;

        Vector3 safeFallback = NormalizeOrZero(fallback);
        return safeFallback != Vector3.Zero ? safeFallback : Vector3.UnitY;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
