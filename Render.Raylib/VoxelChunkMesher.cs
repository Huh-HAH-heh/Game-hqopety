using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

/// <summary>
/// Extracts an isosurface from the 3D density field. This is not a height-column
/// mesher: every output triangle is generated from eight 3D density samples and
/// the same field is used for horizontal cut caps.
/// </summary>
internal static class VoxelChunkMesher
{
    private const int CellsPerAxis = SparseVoxelField.ChunkSize;
    private const int SampleSide = CellsPerAxis + 3;
    private const int SamplePlane = SampleSide * SampleSide;
    private const int SampleCount = SampleSide * SampleSide * SampleSide;
    private const int MaximumVerticesPerMesh = 60_000;

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
        private readonly List<Mesh> _output;

        public MeshBatch(List<Mesh> output) => _output = output;

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

            Mesh mesh = new(_vertices.Count, _indices.Count / 3);
            mesh.AllocVertices();
            mesh.AllocNormals();
            mesh.AllocColors();
            mesh.AllocIndices();

            _vertices.CopyTo(mesh.VerticesAs<Vector3>());
            _normals.CopyTo(mesh.NormalsAs<Vector3>());

            Span<byte> colorData = mesh.ColorsAs<byte>();
            for (int i = 0; i < _colors.Count; i++)
            {
                int offset = i * 4;
                colorData[offset] = _colors[i].R;
                colorData[offset + 1] = _colors[i].G;
                colorData[offset + 2] = _colors[i].B;
                colorData[offset + 3] = _colors[i].A;
            }

            _indices.CopyTo(mesh.IndicesAs<ushort>());
            UploadMesh(ref mesh, false);
            _output.Add(mesh);

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
        if (step < 1)
            throw new ArgumentOutOfRangeException(nameof(step));

        int originX = chunkX * CellsPerAxis * step;
        int originY = chunkY * CellsPerAxis * step;
        int originZ = chunkZ * CellsPerAxis * step;

        float[] density = new float[SampleCount];
        for (int z = 0; z < SampleSide; z++)
        for (int y = 0; y < SampleSide; y++)
        for (int x = 0; x < SampleSide; x++)
        {
            int worldX = SparseVoxelField.GetChunkSampleCoordinate(chunkX, x, step);
            int worldY = SparseVoxelField.GetChunkSampleCoordinate(chunkY, y, step);
            int worldZ = SparseVoxelField.GetChunkSampleCoordinate(chunkZ, z, step);
            density[GetSampleIndex(x, y, z)] =
                field.GetDensity(worldX, worldY, worldZ);
        }

        List<Mesh> output = new(2);
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
            ushort materialId = field.GetMaterialId(
                Math.Min(materialX, field.Width - 1),
                Math.Min(materialY, field.Height - 1),
                Math.Min(materialZ, field.Depth - 1));
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
        float cutSampleY = cutHeightMeters / SparseVoxelField.VoxelSizeMeters - 0.5f;
        if (cutSampleY <= -0.5f || cutSampleY >= field.Height - 0.5f)
            return Array.Empty<Mesh>();

        int originX = chunkX * CellsPerAxis * step;
        int originZ = chunkZ * CellsPerAxis * step;
        List<Mesh> output = new(1);
        MeshBatch batch = new(output);

        for (int z = 0; z < CellsPerAxis; z++)
        for (int x = 0; x < CellsPerAxis; x++)
        {
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

            ushort materialId = field.GetMaterialId(
                Math.Clamp((int)MathF.Floor((x0 + x1) * 0.5f), 0, field.Width - 1),
                Math.Clamp((int)MathF.Floor(cutSampleY), 0, field.Height - 1),
                Math.Clamp((int)MathF.Floor((z0 + z1) * 0.5f), 0, field.Depth - 1));
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
        SparseVoxelField field,
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
