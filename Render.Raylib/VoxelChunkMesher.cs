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
/// Worker-owned copy of a fixed Step=1 render region.
/// Density and material arrays are pooled and released after mesh construction.
/// The optional CUT is applied as an SDF intersection with y <= cutHeight.
/// </summary>
internal sealed class VoxelChunkBuildInput : IDisposable
{
    internal const int DefaultCellsPerAxis = SparseVoxelField.ChunkSize * 4;
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
    public float CutHeightMeters { get; }

    public int CaptureCursor { get; private set; }
    public int CaptureCount => SampleCount + CellCount;
    public bool DensityCaptureComplete => CaptureCursor >= SampleCount;
    public bool IsComplete => CaptureCursor >= CaptureCount;
    public bool HasPotentialSurface =>
        _minimumDensity <= 0f && _maximumDensity >= 0f;

    private readonly float _cutSampleY;
    private float _minimumDensity = float.PositiveInfinity;
    private float _maximumDensity = float.NegativeInfinity;
    private int _disposed;

    public VoxelChunkBuildInput(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        int cellsPerAxis = DefaultCellsPerAxis,
        float cutHeightMeters = float.PositiveInfinity)
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

        CutHeightMeters = float.IsFinite(cutHeightMeters)
            ? cutHeightMeters
            : float.PositiveInfinity;
        _cutSampleY = CutHeightMeters / SparseVoxelField.VoxelSizeMeters;

        SampleSide = checked(cellsPerAxis + HaloSize * 2);
        SamplePlane = checked(SampleSide * SampleSide);
        SampleCount = checked(SamplePlane * SampleSide);
        CellCount = checked(cellsPerAxis * cellsPerAxis * cellsPerAxis);

        float[]? densities = null;
        ushort[]? materials = null;
        try
        {
            densities = ArrayPool<float>.Shared.Rent(SampleCount);
            materials = ArrayPool<ushort>.Shared.Rent(CellCount);
            Array.Clear(materials, 0, CellCount);

            DensitySamples = densities;
            Materials = materials;
        }
        catch
        {
            if (densities != null)
                ArrayPool<float>.Shared.Return(densities);
            if (materials != null)
                ArrayPool<ushort>.Shared.Return(materials);
            throw;
        }
    }

    public int CaptureNext(IVoxelFieldSource field, int maximumValues) =>
        CaptureNext(field, maximumValues, captureMaterials: true);

    public int CaptureNext(
        IVoxelFieldSource field,
        int maximumValues,
        bool captureMaterials)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

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

                // Positive density is solid. Intersect the terrain SDF with
                // the half-space below the cut plane: min(terrain, plane).
                // Unlike forcing samples above the cut to -1, this keeps the
                // generated cap exactly horizontal and independent of terrain SDF magnitude.
                if (float.IsFinite(_cutSampleY))
                {
                    float cutDensity = _cutSampleY - (worldY + 0.5f);
                    density = MathF.Min(density, cutDensity);
                }

                if (!float.IsFinite(density))
                    density = -1f;

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

    /// <summary>Trilinear sample in voxel-centre coordinates.</summary>
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

    private float GetSample(int x, int y, int z)
    {
        if ((uint)x >= SampleSide ||
            (uint)y >= SampleSide ||
            (uint)z >= SampleSide)
            return -1f;

        return DensitySamples[x + y * SampleSide + z * SamplePlane];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        ArrayPool<float>.Shared.Return(DensitySamples);
        ArrayPool<ushort>.Shared.Return(Materials);
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
    private const int MaximumIndicesPerMesh = 180_000;
    private const float InterpolationEpsilon = 1e-20f;
    private const float NormalEpsilon = 1e-20f;

    private static readonly byte[] CornerX = { 0, 1, 1, 0, 0, 1, 1, 0 };
    private static readonly byte[] CornerY = { 0, 0, 1, 1, 0, 0, 1, 1 };
    private static readonly byte[] CornerZ = { 0, 0, 0, 0, 1, 1, 1, 1 };

    private static readonly byte[] EdgeA =
    {
        0, 1, 2, 3,
        4, 5, 6, 7,
        0, 1, 2, 3
    };

    private static readonly byte[] EdgeB =
    {
        1, 2, 3, 0,
        5, 6, 7, 4,
        4, 5, 6, 7
    };

    private sealed class SurfaceMeshBatch : IDisposable
    {
        private readonly List<VoxelMeshData> _output;
        private readonly Vector3[] _sourceVertices;
        private readonly Vector3[] _sourceNormals;
        private readonly Color[] _sourceColors;
        private readonly int[] _localIndexBySource;
        private readonly int[] _touchedSourceIndices;

        private readonly Vector3[] _vertices;
        private readonly Vector3[] _normals;
        private readonly byte[] _colors;
        private readonly ushort[] _indices;

        private int _vertexCount;
        private int _indexCount;
        private int _touchedCount;

        public SurfaceMeshBatch(
            List<VoxelMeshData> output,
            Vector3[] sourceVertices,
            Vector3[] sourceNormals,
            Color[] sourceColors,
            int[] localIndexBySource,
            int[] touchedSourceIndices)
        {
            _output = output;
            _sourceVertices = sourceVertices;
            _sourceNormals = sourceNormals;
            _sourceColors = sourceColors;
            _localIndexBySource = localIndexBySource;
            _touchedSourceIndices = touchedSourceIndices;

            _vertices = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            _normals = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            _colors = ArrayPool<byte>.Shared.Rent(MaximumVerticesPerMesh * 4);
            _indices = ArrayPool<ushort>.Shared.Rent(MaximumIndicesPerMesh);
        }

        public void AddQuad(int a, int b, int c, int d, bool solidAtEdgeStart)
        {
            if (solidAtEdgeStart)
            {
                AddTriangle(a, b, c);
                AddTriangle(a, c, d);
            }
            else
            {
                AddTriangle(a, c, b);
                AddTriangle(a, d, c);
            }
        }

        private void AddTriangle(int a, int b, int c)
        {
            if (a == b || b == c || a == c)
                return;

            Vector3 faceNormal = Vector3.Cross(
                _sourceVertices[b] - _sourceVertices[a],
                _sourceVertices[c] - _sourceVertices[a]);

            if (faceNormal.LengthSquared() <= NormalEpsilon)
                return;

            int missing =
                (_localIndexBySource[a] < 0 ? 1 : 0) +
                (_localIndexBySource[b] < 0 ? 1 : 0) +
                (_localIndexBySource[c] < 0 ? 1 : 0);

            if (_vertexCount + missing > MaximumVerticesPerMesh ||
                _indexCount + 3 > MaximumIndicesPerMesh)
            {
                Flush();
            }

            ushort ia = GetLocalIndex(a);
            ushort ib = GetLocalIndex(b);
            ushort ic = GetLocalIndex(c);

            _indices[_indexCount++] = ia;
            _indices[_indexCount++] = ib;
            _indices[_indexCount++] = ic;
        }

        private ushort GetLocalIndex(int sourceIndex)
        {
            int localIndex = _localIndexBySource[sourceIndex];
            if (localIndex >= 0)
                return checked((ushort)localIndex);

            localIndex = _vertexCount++;
            _localIndexBySource[sourceIndex] = localIndex;
            _touchedSourceIndices[_touchedCount++] = sourceIndex;

            Vector3 position = _sourceVertices[sourceIndex];
            Vector3 normal = _sourceNormals[sourceIndex];
            Color color = _sourceColors[sourceIndex];

            _vertices[localIndex] = position;
            _normals[localIndex] = normal;

            int colorIndex = localIndex * 4;
            _colors[colorIndex] = color.R;
            _colors[colorIndex + 1] = color.G;
            _colors[colorIndex + 2] = color.B;
            _colors[colorIndex + 3] = color.A;

            return checked((ushort)localIndex);
        }

        public void Flush()
        {
            if (_indexCount == 0)
            {
                ResetMap();
                return;
            }

            Vector3[]? vertices = null;
            Vector3[]? normals = null;
            byte[]? colors = null;
            ushort[]? indices = null;

            try
            {
                vertices = ArrayPool<Vector3>.Shared.Rent(_vertexCount);
                normals = ArrayPool<Vector3>.Shared.Rent(_vertexCount);
                colors = ArrayPool<byte>.Shared.Rent(_vertexCount * 4);
                indices = ArrayPool<ushort>.Shared.Rent(_indexCount);

                Array.Copy(_vertices, vertices, _vertexCount);
                Array.Copy(_normals, normals, _vertexCount);
                Array.Copy(_colors, colors, _vertexCount * 4);
                Array.Copy(_indices, indices, _indexCount);

                _output.Add(new VoxelMeshData(
                    vertices,
                    normals,
                    colors,
                    indices,
                    _vertexCount,
                    _indexCount));

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

            _vertexCount = 0;
            _indexCount = 0;
            ResetMap();
        }

        private void ResetMap()
        {
            for (int i = 0; i < _touchedCount; i++)
                _localIndexBySource[_touchedSourceIndices[i]] = -1;

            _touchedCount = 0;
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

    /// <summary>
    /// Naive Surface Nets: one interpolated vertex per sign-changing cell.
    /// Each sign-changing lattice edge connects the four surrounding cells
    /// into a quad. The one-sample halo produces identical shared-border vertices.
    /// </summary>
    public static VoxelMeshData[] BuildSurfaceData(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(palette);

        int cells = input.CellsPerAxis;
        int side = cells + 1; // cell coordinates -1..cells-1 inclusive
        int cellCapacity = checked(side * side * side);

        int[]? cellVertices = null;
        Vector3[]? vertices = null;
        Vector3[]? normals = null;
        Color[]? colors = null;
        int[]? localIndexMap = null;
        int[]? touchedIndices = null;
        SurfaceMeshBatch? batch = null;

        List<VoxelMeshData> output = new(4);

        try
        {
            cellVertices = ArrayPool<int>.Shared.Rent(cellCapacity);
            vertices = ArrayPool<Vector3>.Shared.Rent(cellCapacity);
            normals = ArrayPool<Vector3>.Shared.Rent(cellCapacity);
            colors = ArrayPool<Color>.Shared.Rent(cellCapacity);
            localIndexMap = ArrayPool<int>.Shared.Rent(cellCapacity);
            touchedIndices = ArrayPool<int>.Shared.Rent(MaximumVerticesPerMesh);

            Array.Fill(cellVertices, -1, 0, cellCapacity);
            Array.Fill(localIndexMap, -1, 0, cellCapacity);

            int vertexCount = BuildCellVertices(
                input,
                palette,
                cellVertices,
                vertices,
                normals,
                colors,
                side,
                cancellationToken);

            if (vertexCount == 0)
                return Array.Empty<VoxelMeshData>();

            AccumulateVertexNormals(
                input,
                cellVertices,
                vertices,
                normals,
                side,
                cancellationToken);

            NormalizeVertexNormals(normals, vertexCount);

            batch = new SurfaceMeshBatch(
                output,
                vertices,
                normals,
                colors,
                localIndexMap,
                touchedIndices);

            BuildConnectivity(
                input,
                cellVertices,
                vertices,
                batch,
                side,
                cancellationToken);

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
            batch?.Dispose();

            if (cellVertices != null)
                ArrayPool<int>.Shared.Return(cellVertices);
            if (vertices != null)
                ArrayPool<Vector3>.Shared.Return(vertices);
            if (normals != null)
                ArrayPool<Vector3>.Shared.Return(normals);
            if (colors != null)
                ArrayPool<Color>.Shared.Return(colors);
            if (localIndexMap != null)
                ArrayPool<int>.Shared.Return(localIndexMap);
            if (touchedIndices != null)
                ArrayPool<int>.Shared.Return(touchedIndices);
        }
    }

    private static int BuildCellVertices(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        int[] cellVertices,
        Vector3[] vertices,
        Vector3[] normals,
        Color[] colors,
        int side,
        CancellationToken cancellationToken)
    {
        int cells = input.CellsPerAxis;
        float voxelSize = SparseVoxelField.VoxelSizeMeters;
        Vector3 origin = new(
            input.OriginX * voxelSize,
            input.OriginY * voxelSize,
            input.OriginZ * voxelSize);

        Span<float> cornerDensities = stackalloc float[8];
        int vertexCount = 0;

        for (int z = -1; z < cells; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int y = -1; y < cells; y++)
            for (int x = -1; x < cells; x++)
            {
                for (int corner = 0; corner < 8; corner++)
                {
                    cornerDensities[corner] = input.GetDensity(
                        x + CornerX[corner],
                        y + CornerY[corner],
                        z + CornerZ[corner]);
                }

                Vector3 intersectionSum = Vector3.Zero;
                int intersectionCount = 0;

                for (int edge = 0; edge < 12; edge++)
                {
                    int a = EdgeA[edge];
                    int b = EdgeB[edge];
                    float densityA = cornerDensities[a];
                    float densityB = cornerDensities[b];

                    if ((densityA > 0f) == (densityB > 0f))
                        continue;

                    float denominator = densityA - densityB;
                    float t = MathF.Abs(denominator) > InterpolationEpsilon
                        ? densityA / denominator
                        : 0.5f;

                    t = Math.Clamp(t, 0f, 1f);

                    Vector3 pointA = new(
                        x + CornerX[a],
                        y + CornerY[a],
                        z + CornerZ[a]);

                    Vector3 pointB = new(
                        x + CornerX[b],
                        y + CornerY[b],
                        z + CornerZ[b]);

                    intersectionSum += pointA + (pointB - pointA) * t;
                    intersectionCount++;
                }

                if (intersectionCount == 0)
                    continue;

                int index = CellIndex(x, y, z, side);
                Vector3 average = intersectionSum / intersectionCount;

                vertices[vertexCount] = origin + average * voxelSize;
                normals[vertexCount] = Vector3.Zero;

                ushort materialId = SelectCellMaterial(
                    input,
                    x, y, z,
                    cornerDensities);

                colors[vertexCount] = GetMaterialColor(palette, materialId);
                cellVertices[index] = vertexCount++;
            }
        }

        return vertexCount;
    }

    private static ushort SelectCellMaterial(
        VoxelChunkBuildInput input,
        int x,
        int y,
        int z,
        ReadOnlySpan<float> cornerDensities)
    {
        float strongestDensity = 0f;
        ushort materialId = 0;
        int cells = input.CellsPerAxis;

        for (int i = 0; i < 8; i++)
        {
            float density = cornerDensities[i];
            if (density <= strongestDensity)
                continue;

            int sampleX = Math.Clamp(x + CornerX[i], 0, cells - 1);
            int sampleY = Math.Clamp(y + CornerY[i], 0, cells - 1);
            int sampleZ = Math.Clamp(z + CornerZ[i], 0, cells - 1);

            ushort candidate = input.GetMaterialId(sampleX, sampleY, sampleZ);
            if (candidate == 0)
                continue;

            strongestDensity = density;
            materialId = candidate;
        }

        return materialId == 0 ? (ushort)1 : materialId;
    }

    private static void AccumulateVertexNormals(
        VoxelChunkBuildInput input,
        int[] cellVertices,
        Vector3[] vertices,
        Vector3[] normals,
        int side,
        CancellationToken cancellationToken)
    {
        int cells = input.CellsPerAxis;

        for (int z = 0; z < cells; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                if (TryGetQuad(input, cellVertices, side, 0, x, y, z,
                    out int a, out int b, out int c, out int d, out bool solidAtStart))
                {
                    AccumulateQuad(vertices, normals, a, b, c, d, solidAtStart);
                }

                if (TryGetQuad(input, cellVertices, side, 1, x, y, z,
                    out a, out b, out c, out d, out solidAtStart))
                {
                    AccumulateQuad(vertices, normals, a, b, c, d, solidAtStart);
                }

                if (TryGetQuad(input, cellVertices, side, 2, x, y, z,
                    out a, out b, out c, out d, out solidAtStart))
                {
                    AccumulateQuad(vertices, normals, a, b, c, d, solidAtStart);
                }
            }
        }
    }

    private static void AccumulateQuad(
        Vector3[] vertices,
        Vector3[] normals,
        int a,
        int b,
        int c,
        int d,
        bool solidAtStart)
    {
        if (solidAtStart)
        {
            AccumulateTriangle(vertices, normals, a, b, c);
            AccumulateTriangle(vertices, normals, a, c, d);
        }
        else
        {
            AccumulateTriangle(vertices, normals, a, c, b);
            AccumulateTriangle(vertices, normals, a, d, c);
        }
    }

    private static void AccumulateTriangle(
        Vector3[] vertices,
        Vector3[] normals,
        int a,
        int b,
        int c)
    {
        Vector3 faceNormal = Vector3.Cross(
            vertices[b] - vertices[a],
            vertices[c] - vertices[a]);

        if (faceNormal.LengthSquared() <= NormalEpsilon)
            return;

        normals[a] += faceNormal;
        normals[b] += faceNormal;
        normals[c] += faceNormal;
    }

    private static void NormalizeVertexNormals(Vector3[] normals, int vertexCount)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 normal = normals[i];
            float lengthSquared = normal.LengthSquared();

            normals[i] = lengthSquared > NormalEpsilon
                ? normal / MathF.Sqrt(lengthSquared)
                : Vector3.UnitY;
        }
    }

    private static void BuildConnectivity(
        VoxelChunkBuildInput input,
        int[] cellVertices,
        Vector3[] vertices,
        SurfaceMeshBatch batch,
        int side,
        CancellationToken cancellationToken)
    {
        int cells = input.CellsPerAxis;

        for (int z = 0; z < cells; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                if (TryGetQuad(input, cellVertices, side, 0, x, y, z,
                    out int a, out int b, out int c, out int d, out bool solidAtStart))
                {
                    batch.AddQuad(a, b, c, d, solidAtStart);
                }

                if (TryGetQuad(input, cellVertices, side, 1, x, y, z,
                    out a, out b, out c, out d, out solidAtStart))
                {
                    batch.AddQuad(a, b, c, d, solidAtStart);
                }

                if (TryGetQuad(input, cellVertices, side, 2, x, y, z,
                    out a, out b, out c, out d, out solidAtStart))
                {
                    batch.AddQuad(a, b, c, d, solidAtStart);
                }
            }
        }
    }

    /// <summary>
    /// Returns the four Surface Nets cells around one +axis sample edge.
    /// The cyclic order winds toward air when the edge starts in solid.
    /// </summary>
    private static bool TryGetQuad(
        VoxelChunkBuildInput input,
        int[] cellVertices,
        int side,
        int axis,
        int x,
        int y,
        int z,
        out int a,
        out int b,
        out int c,
        out int d,
        out bool solidAtStart)
    {
        float densityA = input.GetDensity(x, y, z);
        float densityB = axis switch
        {
            0 => input.GetDensity(x + 1, y, z),
            1 => input.GetDensity(x, y + 1, z),
            _ => input.GetDensity(x, y, z + 1)
        };

        solidAtStart = densityA > 0f;
        if (solidAtStart == (densityB > 0f))
        {
            a = b = c = d = -1;
            return false;
        }

        switch (axis)
        {
            case 0:
                a = cellVertices[CellIndex(x, y - 1, z - 1, side)];
                b = cellVertices[CellIndex(x, y, z - 1, side)];
                c = cellVertices[CellIndex(x, y, z, side)];
                d = cellVertices[CellIndex(x, y - 1, z, side)];
                break;

            case 1:
                a = cellVertices[CellIndex(x - 1, y, z - 1, side)];
                b = cellVertices[CellIndex(x - 1, y, z, side)];
                c = cellVertices[CellIndex(x, y, z, side)];
                d = cellVertices[CellIndex(x, y, z - 1, side)];
                break;

            default:
                a = cellVertices[CellIndex(x - 1, y - 1, z, side)];
                b = cellVertices[CellIndex(x, y - 1, z, side)];
                c = cellVertices[CellIndex(x, y, z, side)];
                d = cellVertices[CellIndex(x - 1, y, z, side)];
                break;
        }

        return a >= 0 && b >= 0 && c >= 0 && d >= 0;
    }

    private static int CellIndex(int x, int y, int z, int side) =>
        ((z + 1) * side + (y + 1)) * side + (x + 1);

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
