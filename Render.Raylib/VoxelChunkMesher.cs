using System;
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
    public Vector3[] Vertices { get; }
    public Vector3[] Normals { get; }
    public byte[] Colors { get; }
    public ushort[] Indices { get; }

    public int VertexCount => Vertices.Length;
    public int TriangleCount => Indices.Length / 3;

    public VoxelMeshData(
        Vector3[] vertices,
        Vector3[] normals,
        byte[] colors,
        ushort[] indices)
    {
        Vertices = vertices;
        Normals = normals;
        Colors = colors;
        Indices = indices;
    }
}

/// <summary>
/// Immutable-to-worker, incrementally captured source data for one terrain
/// chunk. Capture reads the live field only on the render thread; workers never
/// touch WorldMap or mutable voxel dictionaries.
/// </summary>
internal sealed class VoxelChunkBuildInput
{
    private const int Cells = SparseVoxelField.ChunkSize;
    private const int SampleSide = Cells + 3;
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
                int worldX = SparseVoxelField.GetChunkSampleCoordinate(ChunkX, x, Step);
                int worldY = SparseVoxelField.GetChunkSampleCoordinate(ChunkY, y, Step);
                int worldZ = SparseVoxelField.GetChunkSampleCoordinate(ChunkZ, z, Step);
                float density = field.GetDensity(worldX, worldY, worldZ);
                DensitySamples[index] = density;
                _minimumDensity = MathF.Min(_minimumDensity, density);
                _maximumDensity = MathF.Max(_maximumDensity, density);
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
        float sx = (x - OriginX) / Step + 1f;
        float sy = (y - OriginY) / Step + 1f;
        float sz = (z - OriginZ) / Step + 1f;
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
    private const int CellsPerAxis = SparseVoxelField.ChunkSize;
    private const int SampleSide = CellsPerAxis + 3;
    private const int SamplePlane = SampleSide * SampleSide;
    private const int SampleCount = SampleSide * SampleSide * SampleSide;
    private const int MaximumVerticesPerMesh = 3_000;

    private static readonly int[] CornerX = { 0, 1, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] CornerY = { 0, 0, 1, 1, 0, 0, 1, 1 };
    private static readonly int[] CornerZ = { 0, 0, 0, 0, 1, 1, 1, 1 };

    // A consistent six-tetrahedra decomposition around cube diagonal 0-6.
    private static readonly int[,] Tetrahedra =
    {
        { 0, 5, 1, 6 },
        { 0, 1, 2, 6 },
        { 0, 2, 3, 6 },
        { 0, 3, 7, 6 },
        { 0, 7, 4, 6 },
        { 0, 4, 5, 6 }
    };

    private static readonly int[] TetraEdges =
    {
        0, 1, 0, 2, 0, 3, 1, 2, 1, 3, 2, 3
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
            Vector3 normal = NormalizeOr(fallbackNormal, Vector3.UnitY);
            if (Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), normal) < 0f)
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
            if (_vertices.Count == 0)
                return;

            byte[] colors = new byte[_colors.Count * 4];
            for (int i = 0; i < _colors.Count; i++)
            {
                int offset = i * 4;
                colors[offset] = _colors[i].R;
                colors[offset + 1] = _colors[i].G;
                colors[offset + 2] = _colors[i].B;
                colors[offset + 3] = _colors[i].A;
            }

            _output.Add(new VoxelMeshData(
                _vertices.ToArray(),
                _normals.ToArray(),
                colors,
                _indices.ToArray()));

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
            }
            return meshes;
        }
        catch
        {
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

        List<VoxelMeshData> output = new(2);
        MeshBatch batch = new(output);
        Span<Vector3> positions = stackalloc Vector3[8];
        Span<Vector3> normals = stackalloc Vector3[8];
        Span<float> values = stackalloc float[8];
        Span<SurfaceVertex> intersections = stackalloc SurfaceVertex[4];
        Span<float> angles = stackalloc float[4];

        for (int z = 0; z < CellsPerAxis; z++)
        for (int y = 0; y < CellsPerAxis; y++)
        for (int x = 0; x < CellsPerAxis; x++)
        {
            if (x == 0)
                cancellationToken.ThrowIfCancellationRequested();

            float minimum = float.PositiveInfinity;
            float maximum = float.NegativeInfinity;

            for (int corner = 0; corner < 8; corner++)
            {
                int sx = x + 1 + CornerX[corner];
                int sy = y + 1 + CornerY[corner];
                int sz = z + 1 + CornerZ[corner];
                int sampleIndex = GetSampleIndex(sx, sy, sz);
                float value = density[sampleIndex];
                values[corner] = value;
                minimum = MathF.Min(minimum, value);
                maximum = MathF.Max(maximum, value);

                positions[corner] = new Vector3(
                    (originX + (x + CornerX[corner]) * step + 0.5f) * SparseVoxelField.VoxelSizeMeters,
                    (originY + (y + CornerY[corner]) * step + 0.5f) * SparseVoxelField.VoxelSizeMeters,
                    (originZ + (z + CornerZ[corner]) * step + 0.5f) * SparseVoxelField.VoxelSizeMeters);

                float gx = density[GetSampleIndex(sx + 1, sy, sz)] -
                    density[GetSampleIndex(sx - 1, sy, sz)];
                float gy = density[GetSampleIndex(sx, sy + 1, sz)] -
                    density[GetSampleIndex(sx, sy - 1, sz)];
                float gz = density[GetSampleIndex(sx, sy, sz + 1)] -
                    density[GetSampleIndex(sx, sy, sz - 1)];
                normals[corner] = NormalizeOr(
                    new Vector3(-gx, -gy, -gz),
                    Vector3.UnitY);
            }

            if (minimum > 0f || maximum < 0f)
                continue;

            int materialX = originX + x * step + step / 2;
            int materialY = originY + y * step + step / 2;
            int materialZ = originZ + z * step + step / 2;
            ushort materialId = input.GetMaterialId(x, y, z);
            Color color = GetMaterialColor(palette, materialId);

            for (int tetra = 0; tetra < 6; tetra++)
            {
                int a = Tetrahedra[tetra, 0];
                int b = Tetrahedra[tetra, 1];
                int c = Tetrahedra[tetra, 2];
                int d = Tetrahedra[tetra, 3];
                int count = 0;

                for (int edge = 0; edge < 6; edge++)
                {
                    int first = TetraEdges[edge * 2];
                    int second = TetraEdges[edge * 2 + 1];
                    int ia = first switch { 0 => a, 1 => b, 2 => c, _ => d };
                    int ib = second switch { 0 => a, 1 => b, 2 => c, _ => d };
                    float da = values[ia];
                    float db = values[ib];
                    if ((da > 0f) == (db > 0f))
                        continue;

                    float t = Math.Clamp(da / (da - db), 0f, 1f);
                    Vector3 p = Vector3.Lerp(positions[ia], positions[ib], t);
                    Vector3 n = NormalizeOr(
                        Vector3.Lerp(normals[ia], normals[ib], t),
                        Vector3.UnitY);
                    intersections[count++] = new SurfaceVertex(p, n);
                }

                if (count < 3)
                    continue;

                Vector3 average = Vector3.Zero;
                Vector3 center = Vector3.Zero;
                for (int i = 0; i < count; i++)
                {
                    average += intersections[i].Normal;
                    center += intersections[i].Position;
                }
                center /= count;
                average = NormalizeOr(average, Vector3.UnitY);

                Vector3 axis = NormalizeOr(
                    Vector3.Cross(
                        MathF.Abs(average.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX,
                        average),
                    Vector3.UnitX);
                Vector3 secondAxis = NormalizeOr(
                    Vector3.Cross(average, axis),
                    Vector3.UnitZ);

                for (int i = 0; i < count; i++)
                {
                    Vector3 relative = intersections[i].Position - center;
                    angles[i] = MathF.Atan2(
                        Vector3.Dot(relative, secondAxis),
                        Vector3.Dot(relative, axis));
                }

                for (int i = 1; i < count; i++)
                {
                    int j = i;
                    while (j > 0 && angles[j] < angles[j - 1])
                    {
                        (angles[j], angles[j - 1]) = (angles[j - 1], angles[j]);
                        (intersections[j], intersections[j - 1]) =
                            (intersections[j - 1], intersections[j]);
                        j--;
                    }
                }

                for (int i = 1; i < count - 1; i++)
                {
                    batch.AddTriangle(
                        intersections[0],
                        intersections[i],
                        intersections[i + 1],
                        color,
                        average);
                }
            }
        }

        batch.Flush();
        return output.ToArray();
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
        float cutSampleY = cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f;
        if (cutSampleY <= -0.5f || cutSampleY >= field.FieldHeight - 0.5f)
            return Array.Empty<VoxelMeshData>();

        int step = field.Step;
        int originX = field.OriginX;
        int originZ = field.OriginZ;
        List<VoxelMeshData> output = new(1);
        MeshBatch batch = new(output);

        for (int z = 0; z < CellsPerAxis; z++)
        for (int x = 0; x < CellsPerAxis; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float x0 = originX + x * step;
            float x1 = x0 + step;
            float z0 = originZ + z * step;
            float z1 = z0 + step;
            Span<SlicePoint> input = stackalloc SlicePoint[4];
            Span<SlicePoint> polygon = stackalloc SlicePoint[8];

            input[0] = MakeSlicePoint(field, x0, cutSampleY, z0, cutHeightMeters);
            input[1] = MakeSlicePoint(field, x1, cutSampleY, z0, cutHeightMeters);
            input[2] = MakeSlicePoint(field, x1, cutSampleY, z1, cutHeightMeters);
            input[3] = MakeSlicePoint(field, x0, cutSampleY, z1, cutHeightMeters);

            int polygonCount = ClipSolidPolygon(input, polygon);
            if (polygonCount < 3)
                continue;

            ushort materialId = field.GetMaterialAtWorldPosition(
                (x0 + x1) * 0.5f,
                cutSampleY,
                (z0 + z1) * 0.5f);
            Color color = GetMaterialColor(palette, materialId);
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

        batch.Flush();
        return output.ToArray();
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
        return new SlicePoint(position, field.SampleDensity(x, ySample, z));
    }

    private static int ClipSolidPolygon(
        ReadOnlySpan<SlicePoint> input,
        Span<SlicePoint> output)
    {
        int count = 0;
        for (int i = 0; i < input.Length; i++)
        {
            SlicePoint a = input[i];
            SlicePoint b = input[(i + 1) % input.Length];
            bool insideA = a.Density >= 0f;
            bool insideB = b.Density >= 0f;

            if (insideA && insideB)
            {
                output[count++] = b;
            }
            else if (insideA && !insideB)
            {
                output[count++] = Intersect(a, b);
            }
            else if (!insideA && insideB)
            {
                output[count++] = Intersect(a, b);
                output[count++] = b;
            }
        }
        return count;
    }

    private static SlicePoint Intersect(SlicePoint a, SlicePoint b)
    {
        float t = Math.Clamp(a.Density / (a.Density - b.Density), 0f, 1f);
        return new SlicePoint(
            Vector3.Lerp(a.Position, b.Position, t),
            0f);
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

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 0.000001f
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}
