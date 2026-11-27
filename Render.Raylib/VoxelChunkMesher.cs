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
    private readonly int _vertexOffset;
    private readonly int _indexOffset;
    private readonly int _indexBase;
    private readonly bool _ownsArrays;

    public ReadOnlySpan<Vector3> Vertices => _vertices.AsSpan(_vertexOffset, VertexCount);
    public ReadOnlySpan<Vector3> Normals => _normals.AsSpan(_vertexOffset, VertexCount);
    public ReadOnlySpan<byte> Colors => _colors.AsSpan(_vertexOffset * 4, VertexCount * 4);
    public ReadOnlySpan<ushort> Indices => _indices.AsSpan(_indexOffset, IndexCount);
    internal int IndexBase => _indexBase;

    public int VertexCount { get; }
    public int IndexCount { get; }
    public int TriangleCount => IndexCount / 3;

    public VoxelMeshData(
        Vector3[] vertices, Vector3[] normals, byte[] colors, ushort[] indices,
        int vertexCount, int indexCount)
        : this(vertices, normals, colors, indices, 0, vertexCount, 0, indexCount, 0, true)
    {
    }

    private VoxelMeshData(
        Vector3[] vertices, Vector3[] normals, byte[] colors, ushort[] indices,
        int vertexOffset, int vertexCount, int indexOffset, int indexCount,
        int indexBase, bool ownsArrays)
    {
        if (vertexCount <= 0 || indexCount <= 0 ||
            vertexOffset < 0 || indexOffset < 0 ||
            vertexOffset + vertexCount > vertices.Length ||
            vertexOffset + vertexCount > normals.Length ||
            (vertexOffset + vertexCount) * 4 > colors.Length ||
            indexOffset + indexCount > indices.Length)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));

        _vertices = vertices;
        _normals = normals;
        _colors = colors;
        _indices = indices;
        _vertexOffset = vertexOffset;
        _indexOffset = indexOffset;
        _indexBase = indexBase;
        _ownsArrays = ownsArrays;
        VertexCount = vertexCount;
        IndexCount = indexCount;
    }

    internal VoxelMeshData CreateView(
        int vertexOffset, int vertexCount, int indexOffset, int indexCount) =>
        new(
            _vertices, _normals, _colors, _indices,
            _vertexOffset + vertexOffset, vertexCount,
            _indexOffset + indexOffset, indexCount,
            _indexBase + vertexOffset, false);

    public void ReturnCpuArrays()
    {
        if (!_ownsArrays || Interlocked.Exchange(ref _returned, 1) != 0)
            return;

        ArrayPool<Vector3>.Shared.Return(_vertices);
        ArrayPool<Vector3>.Shared.Return(_normals);
        ArrayPool<byte>.Shared.Return(_colors);
        ArrayPool<ushort>.Shared.Return(_indices);
    }
}

/// <summary>
/// Compact signed-density input: configurable N^3 scalar samples plus a one-sample halo.
/// Positive values are solid, zero/negative values are air.
/// Samples are Y-contiguous per XZ column for fast vertical capture.
/// </summary>
internal sealed class VoxelChunkBuildInput : IDisposable
{
    public const int DefaultCellsPerAxis = 64;
    public const int HaloSize = 1;

    public readonly float[] Densities;

    private IVoxelFieldSource _source = null!;
    private int _disposed;

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
    public int Step { get; }
    public bool IsCaptured { get; private set; }
    public bool HasPotentialSurface { get; private set; }

    public VoxelChunkBuildInput(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
        int cutLayer = int.MinValue,
        int step = 1,
        int cellsPerAxis = DefaultCellsPerAxis)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (chunkX < 0 || chunkY < 0 || chunkZ < 0 ||
            step is not (1 or 2 or 4 or 8 or 16 or 32 or 64) || cellsPerAxis <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkX));

        _source = field;
        ChunkX = chunkX;
        ChunkY = chunkY;
        ChunkZ = chunkZ;
        CellsPerAxis = cellsPerAxis;
        Step = step;

        OriginX = checked(chunkX * cellsPerAxis * step);
        OriginY = checked(chunkY * cellsPerAxis * step);
        OriginZ = checked(chunkZ * cellsPerAxis * step);

        FieldWidth = field.Width;
        FieldHeight = field.Height;
        FieldDepth = field.Depth;
        CutLayer = cutLayer;

        SampleSide = cellsPerAxis + 3;
        SamplePlane = SampleSide * SampleSide;
        TotalSamples = SamplePlane * SampleSide;
        Densities = ArrayPool<float>.Shared.Rent(TotalSamples);
    }

    public void CaptureAll(CancellationToken cancellationToken = default) =>
        CaptureAll(_source, cancellationToken);

    public void CaptureAll(
        IVoxelFieldSource source,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(source);
        if (IsCaptured)
            return;

        _source = source;
        bool hasSolid = false;
        bool hasAir = false;
        Span<float> densityColumn = stackalloc float[SampleSide];
        IVoxelColumnDensitySource? columnSource =
            source as IVoxelColumnDensitySource;
        int firstWorldY = OriginY - HaloSize * Step;

        for (int z = -HaloSize; z <= CellsPerAxis + HaloSize; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int worldZ = OriginZ + z * Step;

            for (int x = -HaloSize; x <= CellsPerAxis + HaloSize; x++)
            {
                int worldX = OriginX + x * Step;
                bool columnCaptured = columnSource != null &&
                    columnSource.TryGetDensityColumn(
                        worldX, worldZ, firstWorldY, Step, densityColumn);

                int columnOffset = (x + HaloSize) * SampleSide +
                    (z + HaloSize) * SamplePlane;

                for (int y = -HaloSize; y <= CellsPerAxis + HaloSize; y++)
                {
                    int sampleY = y + HaloSize;
                    int worldY = OriginY + y * Step;
                    float density = columnCaptured
                        ? densityColumn[sampleY]
                        : source.GetDensity(worldX, worldY, worldZ);

                    if (CutLayer != int.MinValue)
                    {
                        // Apply the cut after reading the authoritative density,
                        // including sparse edits restored by the column sampler.
                        float cutDensity = CutLayer - (worldY + 0.5f);
                        density = MathF.Min(density, cutDensity);
                    }

                    Densities[columnOffset + sampleY] = density;
                    hasSolid |= density > 0f;
                    hasAir |= density <= 0f;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        HasPotentialSurface = hasSolid && hasAir;
        IsCaptured = true;
    }

    // Sample-point coordinates include the one-sample halo: -1..N+1.
    // Vertical samples are contiguous within each XZ column.
    public float GetDensity(int x, int y, int z)
    {
        x += HaloSize;
        y += HaloSize;
        z += HaloSize;
        if ((uint)x >= SampleSide || (uint)y >= SampleSide || (uint)z >= SampleSide)
            return -1f;

        return Densities[y + x * SampleSide + z * SamplePlane];
    }

    public byte GetMaterialId(int x, int y, int z)
    {
        int worldX = OriginX + x * Step;
        int worldY = OriginY + y * Step;
        int worldZ = OriginZ + z * Step;
        ushort material = _source.GetMaterialId(worldX, worldY, worldZ);
        return checked((byte)material);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        ArrayPool<float>.Shared.Return(Densities, clearArray: false);
    }
}

internal readonly record struct VoxelMeshBuildResult(
    VoxelMeshData[] Surface,
    VoxelMeshData[] CutCap);

internal readonly record struct VoxelRegionMeshRange(
    int PageIndex, int VertexOffset, int VertexCount, int IndexOffset, int IndexCount);

internal static class VoxelChunkMesher
{
    internal const int MaximumVerticesPerMesh = 60_000;
    internal const int MaximumIndicesPerMesh = 90_000;


    private sealed class MeshBatch : IDisposable
    {
        private readonly List<VoxelMeshData> _output;
        private readonly int[] _globalToLocal;
        private readonly int[] _usedGlobalIds;

        private Vector3[] _vertices;
        private Vector3[] _normals;
        private byte[] _colors;
        private ushort[] _indices;

        private int _vertexCount;
        private int _indexCount;
        private int _usedGlobalCount;

        public MeshBatch(List<VoxelMeshData> output, int[] globalToLocal, int[] usedGlobalIds)
        {
            _output = output;
            _globalToLocal = globalToLocal;
            _usedGlobalIds = usedGlobalIds;
            _vertices = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            _normals = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            _colors = ArrayPool<byte>.Shared.Rent(MaximumVerticesPerMesh * 4);
            _indices = ArrayPool<ushort>.Shared.Rent(MaximumIndicesPerMesh);
        }

        public void AddQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 normal,
            Color color,
            bool frontFace) =>
            AddQuad(p0, p1, p2, p3, normal, normal, normal, normal, color, frontFace);

        public void AddQuad(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 p3,
            Vector3 n0,
            Vector3 n1,
            Vector3 n2,
            Vector3 n3,
            Color color,
            bool frontFace)
        {
            if (_vertexCount + 4 > MaximumVerticesPerMesh ||
                _indexCount + 6 > MaximumIndicesPerMesh)
                Flush();

            ushort start = checked((ushort)_vertexCount);
            AddVertex(p0, n0, color);
            AddVertex(p1, n1, color);
            AddVertex(p2, n2, color);
            AddVertex(p3, n3, color);

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

            for (int i = 0; i < _usedGlobalCount; i++)
                _globalToLocal[_usedGlobalIds[i]] = -1;
            _usedGlobalCount = 0;

            _vertices = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            _normals = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            _colors = ArrayPool<byte>.Shared.Rent(MaximumVerticesPerMesh * 4);
            _indices = ArrayPool<ushort>.Shared.Rent(MaximumIndicesPerMesh);
            _vertexCount = 0;
            _indexCount = 0;
        }

        public void AddIndexedQuad(
            int i0,
            int i1,
            int i2,
            int i3,
            Vector3[] positions,
            Vector3[] normals,
            byte[] materials,
            TerrainPalette palette,
            bool frontFace)
        {
            if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 ||
                i0 >= _globalToLocal.Length || i1 >= _globalToLocal.Length ||
                i2 >= _globalToLocal.Length || i3 >= _globalToLocal.Length)
                return;

            if (_vertexCount + 4 > MaximumVerticesPerMesh ||
                _indexCount + 6 > MaximumIndicesPerMesh)
                Flush();

            ushort a = GetOrAddVertex(i0, positions, normals, materials, palette);
            ushort b = GetOrAddVertex(i1, positions, normals, materials, palette);
            ushort c = GetOrAddVertex(i2, positions, normals, materials, palette);
            ushort d = GetOrAddVertex(i3, positions, normals, materials, palette);

            if (frontFace)
            {
                _indices[_indexCount++] = a;
                _indices[_indexCount++] = b;
                _indices[_indexCount++] = c;
                _indices[_indexCount++] = a;
                _indices[_indexCount++] = c;
                _indices[_indexCount++] = d;
            }
            else
            {
                _indices[_indexCount++] = a;
                _indices[_indexCount++] = c;
                _indices[_indexCount++] = b;
                _indices[_indexCount++] = a;
                _indices[_indexCount++] = d;
                _indices[_indexCount++] = c;
            }
        }

        private ushort GetOrAddVertex(
            int globalId,
            Vector3[] positions,
            Vector3[] normals,
            byte[] materials,
            TerrainPalette palette)
        {
            int localId = _globalToLocal[globalId];
            if (localId >= 0)
                return checked((ushort)localId);

            localId = _vertexCount;
            _globalToLocal[globalId] = localId;
            _usedGlobalIds[_usedGlobalCount++] = globalId;
            AddVertex(
                positions[globalId],
                normals[globalId],
                GetMaterialColor(palette, materials[globalId]));
            return checked((ushort)localId);
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
        using VoxelChunkBuildInput input = new(
            field, chunkX, chunkY, chunkZ,
            cellsPerAxis: SparseVoxelField.ChunkSize);
        input.CaptureAll(field);
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

    public static VoxelMeshData[] CombineSurfaceData(
        IReadOnlyList<VoxelMeshData> sources,
        IReadOnlyList<int> owners,
        int regionCount,
        out VoxelRegionMeshRange[][] rangesByRegion)
    {
        if (sources.Count != owners.Count)
            throw new ArgumentException("Source and owner counts differ.");

        List<VoxelMeshData> output = new(Math.Max(1, sources.Count));
        List<VoxelRegionMeshRange>[] ranges = new List<VoxelRegionMeshRange>[regionCount];
        for (int i = 0; i < ranges.Length; i++) ranges[i] = new();

        Vector3[]? v = null, n = null;
        byte[]? c = null;
        ushort[]? ix = null;
        int vc = 0, ic = 0, pageIndex = 0;

        void Rent()
        {
            if (v != null) return;
            v = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            n = ArrayPool<Vector3>.Shared.Rent(MaximumVerticesPerMesh);
            c = ArrayPool<byte>.Shared.Rent(MaximumVerticesPerMesh * 4);
            ix = ArrayPool<ushort>.Shared.Rent(MaximumIndicesPerMesh);
        }

        void Flush()
        {
            if (vc == 0) return;
            output.Add(new VoxelMeshData(v!, n!, c!, ix!, vc, ic));
            v = null; n = null; c = null; ix = null;
            vc = 0; ic = 0; pageIndex++;
        }

        try
        {
            for (int s = 0; s < sources.Count; s++)
            {
                VoxelMeshData source = sources[s];
                if (source.VertexCount == 0 || source.IndexCount == 0) continue;
                if (source.VertexCount > MaximumVerticesPerMesh ||
                    source.IndexCount > MaximumIndicesPerMesh)
                    throw new InvalidOperationException("Source mesh exceeds page limits.");

                if (vc > 0 &&
                    (vc + source.VertexCount > MaximumVerticesPerMesh ||
                     ic + source.IndexCount > MaximumIndicesPerMesh))
                    Flush();

                Rent();
                int owner = owners[s];
                if ((uint)owner >= (uint)regionCount)
                    throw new ArgumentOutOfRangeException(nameof(owners));

                ranges[owner].Add(new VoxelRegionMeshRange(
                    pageIndex, vc, source.VertexCount, ic, source.IndexCount));
                source.Vertices.CopyTo(v!.AsSpan(vc));
                source.Normals.CopyTo(n!.AsSpan(vc));
                source.Colors.CopyTo(c!.AsSpan(vc * 4));

                ReadOnlySpan<ushort> sourceIndices = source.Indices;
                for (int i = 0; i < sourceIndices.Length; i++)
                    ix![ic + i] = checked((ushort)(
                        sourceIndices[i] - source.IndexBase + vc));

                vc += source.VertexCount;
                ic += source.IndexCount;
            }

            Flush();
            rangesByRegion = new VoxelRegionMeshRange[regionCount][];
            for (int i = 0; i < regionCount; i++) rangesByRegion[i] = ranges[i].ToArray();
            return output.ToArray();
        }
        catch
        {
            foreach (VoxelMeshData mesh in output) mesh.ReturnCpuArrays();
            if (v != null) ArrayPool<Vector3>.Shared.Return(v);
            if (n != null) ArrayPool<Vector3>.Shared.Return(n);
            if (c != null) ArrayPool<byte>.Shared.Return(c);
            if (ix != null) ArrayPool<ushort>.Shared.Return(ix);
            throw;
        }
    }

    public static unsafe bool TryUpdateUploaded(
        ref Mesh mesh, int vertexCapacity, int triangleCapacity, VoxelMeshData source)
    {
        if (mesh.VaoId == 0 || mesh.VboId == null ||
            source.VertexCount > vertexCapacity ||
            source.TriangleCount > triangleCapacity)
            return false;

        ReadOnlySpan<Vector3> vertices = source.Vertices;
        ReadOnlySpan<Vector3> normals = source.Normals;
        ReadOnlySpan<byte> colors = source.Colors;
        ReadOnlySpan<ushort> indices = source.Indices;

        fixed (Vector3* p = vertices)
            UpdateMeshBuffer(mesh, Mesh.VboIdIndexVertices, p, vertices.Length * sizeof(Vector3), 0);
        fixed (Vector3* p = normals)
            UpdateMeshBuffer(mesh, Mesh.VboIdIndexNormals, p, normals.Length * sizeof(Vector3), 0);
        fixed (byte* p = colors)
            UpdateMeshBuffer(mesh, Mesh.VboIdIndexColors, p, colors.Length, 0);
        fixed (ushort* p = indices)
            UpdateMeshBuffer(mesh, Mesh.VboIdIndexIndices, p, indices.Length * sizeof(ushort), 0);

        mesh.VertexCount = source.VertexCount;
        mesh.TriangleCount = source.TriangleCount;
        return true;
    }

    public static int CountTriangleVertices(IReadOnlyList<VoxelMeshData> sources)
    {
        int count = 0;
        for (int i = 0; i < sources.Count; i++)
            count = checked(count + sources[i].IndexCount);
        return count;
    }

    public static int CopyTriangleSoup(
        IReadOnlyList<VoxelMeshData> sources,
        Span<Vector3> vertices,
        Span<Vector3> normals,
        Span<byte> colors)
    {
        int required = CountTriangleVertices(sources);
        if (vertices.Length < required || normals.Length < required ||
            colors.Length < checked(required * 4))
            throw new ArgumentException("Triangle-soup destination is too small.");

        int destination = 0;
        for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            VoxelMeshData source = sources[sourceIndex];
            ReadOnlySpan<Vector3> sourceVertices = source.Vertices;
            ReadOnlySpan<Vector3> sourceNormals = source.Normals;
            ReadOnlySpan<byte> sourceColors = source.Colors;
            ReadOnlySpan<ushort> indices = source.Indices;

            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i] - source.IndexBase;
                if ((uint)index >= (uint)sourceVertices.Length)
                    throw new InvalidOperationException("Surface index is outside its source mesh.");

                vertices[destination] = sourceVertices[index];
                normals[destination] = sourceNormals[index];
                sourceColors.Slice(index * 4, 4).CopyTo(colors.Slice(destination * 4, 4));
                destination++;
            }
        }

        return destination;
    }

    public static unsafe void UpdateTriangleSoup(
        ref Mesh mesh,
        int destinationOffset,
        int destinationCapacity,
        IReadOnlyList<VoxelMeshData> sources)
    {
        if (destinationOffset < 0 || destinationCapacity <= 0 ||
            mesh.VaoId == 0 || mesh.VboId == null)
            throw new ArgumentOutOfRangeException(nameof(destinationCapacity));

        int required = CountTriangleVertices(sources);
        if (required > destinationCapacity)
            throw new ArgumentException("Triangle-soup region is too small.", nameof(destinationCapacity));

        // Keep CPU copies temporary. GPU pages own the persistent data; a changed
        // chunk updates only its byte ranges through glBufferSubData.
        Vector3[] vertexArray = ArrayPool<Vector3>.Shared.Rent(destinationCapacity);
        Vector3[] normalArray = ArrayPool<Vector3>.Shared.Rent(destinationCapacity);
        byte[] colorArray = ArrayPool<byte>.Shared.Rent(checked(destinationCapacity * 4));

        try
        {
            Span<Vector3> vertices = vertexArray.AsSpan(0, destinationCapacity);
            Span<Vector3> normals = normalArray.AsSpan(0, destinationCapacity);
            Span<byte> colors = colorArray.AsSpan(0, destinationCapacity * 4);
            vertices.Clear();
            normals.Clear();
            colors.Clear();
            CopyTriangleSoup(sources, vertices, normals, colors);

            fixed (Vector3* p = vertices)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexVertices, p,
                    destinationCapacity * sizeof(Vector3),
                    destinationOffset * sizeof(Vector3));
            fixed (Vector3* p = normals)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexNormals, p,
                    destinationCapacity * sizeof(Vector3),
                    destinationOffset * sizeof(Vector3));
            fixed (byte* p = colors)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexColors, p,
                    destinationCapacity * 4,
                    destinationOffset * 4);
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(vertexArray, clearArray: false);
            ArrayPool<Vector3>.Shared.Return(normalArray, clearArray: false);
            ArrayPool<byte>.Shared.Return(colorArray, clearArray: false);
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
    /// Surface Nets over the signed density field. A vertex is interpolated
    /// in each mixed-sign cell; sign-changing lattice edges connect four
    /// neighbouring cell vertices into a smooth, indexed surface.
    /// </summary>
        private static readonly Vector3[] CubeCorners =
    {
        new(0f, 0f, 0f), new(1f, 0f, 0f),
        new(1f, 1f, 0f), new(0f, 1f, 0f),
        new(0f, 0f, 1f), new(1f, 0f, 1f),
        new(1f, 1f, 1f), new(0f, 1f, 1f)
    };

    private static readonly byte[] EdgeStart = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
    private static readonly byte[] EdgeEnd   = { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };

    public static VoxelMeshData[] BuildSurfaceData(
        VoxelChunkBuildInput input,
        TerrainPalette palette,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(palette);
        if (!input.IsCaptured)
            throw new InvalidOperationException("Voxel density capture must complete before meshing.");
        if (!input.HasPotentialSurface)
            return Array.Empty<VoxelMeshData>();

        int cells = input.CellsPerAxis;
        int side = cells + 2;
        int plane = side * side;
        int cellCount = side * side * side;

        int[] vertexMap = ArrayPool<int>.Shared.Rent(cellCount);
        int[] vertexToMesh = ArrayPool<int>.Shared.Rent(cellCount);
        int[] usedGlobalIds = ArrayPool<int>.Shared.Rent(MaximumVerticesPerMesh);
        Vector3[] positions = ArrayPool<Vector3>.Shared.Rent(cellCount);
        Vector3[] normals = ArrayPool<Vector3>.Shared.Rent(cellCount);
        byte[] materials = ArrayPool<byte>.Shared.Rent(cellCount);
        List<VoxelMeshData> output = new(4);
        Span<float> d = stackalloc float[8];

        try
        {
            Array.Fill(vertexMap, -1, 0, cellCount);
            Array.Fill(vertexToMesh, -1, 0, cellCount);
            int vertexCount = 0;

            // One Surface Nets vertex per mixed-sign density cell.
            // The sample halo lets boundary quads use neighbouring cell vertices.
            for (int z = -1; z <= cells; z++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int y = -1; y <= cells; y++)
                for (int x = -1; x <= cells; x++)
                {
                    d[0] = input.GetDensity(x, y, z);
                    d[1] = input.GetDensity(x + 1, y, z);
                    d[2] = input.GetDensity(x + 1, y + 1, z);
                    d[3] = input.GetDensity(x, y + 1, z);
                    d[4] = input.GetDensity(x, y, z + 1);
                    d[5] = input.GetDensity(x + 1, y, z + 1);
                    d[6] = input.GetDensity(x + 1, y + 1, z + 1);
                    d[7] = input.GetDensity(x, y + 1, z + 1);

                    bool inside = false;
                    bool outside = false;
                    for (int i = 0; i < 8; i++)
                    {
                        inside |= d[i] > 0f;
                        outside |= d[i] <= 0f;
                    }
                    if (!inside || !outside)
                        continue;

                    Vector3 intersectionSum = Vector3.Zero;
                    int intersectionCount = 0;
                    int materialCorner = -1;

                    for (int edge = 0; edge < 12; edge++)
                    {
                        int a = EdgeStart[edge];
                        int b = EdgeEnd[edge];
                        float da = d[a];
                        float db = d[b];
                        if ((da > 0f) == (db > 0f))
                            continue;

                        float t = Math.Clamp(da / (da - db), 0f, 1f);
                        intersectionSum += CubeCorners[a] +
                            (CubeCorners[b] - CubeCorners[a]) * t;
                        intersectionCount++;

                        if (materialCorner < 0 && da > 0f)
                            materialCorner = a;
                        else if (materialCorner < 0 && db > 0f)
                            materialCorner = b;
                    }

                    if (intersectionCount == 0 || vertexCount >= cellCount)
                        continue;

                    Vector3 local = intersectionSum / intersectionCount;
                    float u = local.X;
                    float v = local.Y;
                    float w = local.Z;

                    float dx0 = Lerp(d[1] - d[0], d[5] - d[4], w);
                    float dx1 = Lerp(d[2] - d[3], d[6] - d[7], w);
                    float dy0 = Lerp(d[3] - d[0], d[7] - d[4], w);
                    float dy1 = Lerp(d[2] - d[1], d[6] - d[5], w);
                    float dz0 = Lerp(d[4] - d[0], d[5] - d[1], u);
                    float dz1 = Lerp(d[7] - d[3], d[6] - d[2], u);

                    Vector3 normal = new(
                        -Lerp(dx0, dx1, v),
                        -Lerp(dy0, dy1, u),
                        -Lerp(dz0, dz1, v));

                    float normalLengthSquared = normal.LengthSquared();
                    normal = normalLengthSquared > 1e-12f
                        ? normal / MathF.Sqrt(normalLengthSquared)
                        : Vector3.UnitY;

                    const float sampleOffset = 0.5f;
                    positions[vertexCount] = new Vector3(
                        (input.OriginX + (x + u) * input.Step + sampleOffset) * SparseVoxelField.VoxelSizeMeters,
                        (input.OriginY + (y + v) * input.Step + sampleOffset) * SparseVoxelField.VoxelSizeMeters,
                        (input.OriginZ + (z + w) * input.Step + sampleOffset) * SparseVoxelField.VoxelSizeMeters);
                    normals[vertexCount] = normal;

                    if (materialCorner >= 0)
                    {
                        Vector3 corner = CubeCorners[materialCorner];
                        materials[vertexCount] = input.GetMaterialId(
                            x + (int)corner.X,
                            y + (int)corner.Y,
                            z + (int)corner.Z);
                    }

                    vertexMap[CellIndex(x, y, z, side, plane)] = vertexCount++;
                }
            }

            if (vertexCount == 0)
                return Array.Empty<VoxelMeshData>();

            using MeshBatch batch = new(output, vertexToMesh, usedGlobalIds);

            // Each grid edge belongs to exactly one render chunk. Its four
            // incident cells may be in the halo, so adjacent chunks stay watertight.
            for (int z = 0; z < cells; z++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Edges parallel to X; positive winding points toward +X.
                for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                {
                    float a = input.GetDensity(x, y, z);
                    float b = input.GetDensity(x + 1, y, z);
                    if ((a > 0f) == (b > 0f))
                        continue;

                    int i0 = vertexMap[CellIndex(x, y - 1, z - 1, side, plane)];
                    int i1 = vertexMap[CellIndex(x, y, z - 1, side, plane)];
                    int i2 = vertexMap[CellIndex(x, y, z, side, plane)];
                    int i3 = vertexMap[CellIndex(x, y - 1, z, side, plane)];
                    AddSurfaceQuad(batch, palette, positions, normals, materials,
                        i0, i1, i2, i3, a > 0f);
                }

                // Edges parallel to Y; reordered to produce +Y winding.
                for (int x = 0; x < cells; x++)
                for (int y = 0; y < cells; y++)
                {
                    float a = input.GetDensity(x, y, z);
                    float b = input.GetDensity(x, y + 1, z);
                    if ((a > 0f) == (b > 0f))
                        continue;

                    int i0 = vertexMap[CellIndex(x - 1, y, z - 1, side, plane)];
                    int i1 = vertexMap[CellIndex(x - 1, y, z, side, plane)];
                    int i2 = vertexMap[CellIndex(x, y, z, side, plane)];
                    int i3 = vertexMap[CellIndex(x, y, z - 1, side, plane)];
                    AddSurfaceQuad(batch, palette, positions, normals, materials,
                        i0, i1, i2, i3, a > 0f);
                }

                // Edges parallel to Z; positive winding points toward +Z.
                for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                {
                    float a = input.GetDensity(x, y, z);
                    float b = input.GetDensity(x, y, z + 1);
                    if ((a > 0f) == (b > 0f))
                        continue;

                    int i0 = vertexMap[CellIndex(x - 1, y - 1, z, side, plane)];
                    int i1 = vertexMap[CellIndex(x, y - 1, z, side, plane)];
                    int i2 = vertexMap[CellIndex(x, y, z, side, plane)];
                    int i3 = vertexMap[CellIndex(x - 1, y, z, side, plane)];
                    AddSurfaceQuad(batch, palette, positions, normals, materials,
                        i0, i1, i2, i3, a > 0f);
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
            ArrayPool<int>.Shared.Return(vertexMap, clearArray: false);
            ArrayPool<int>.Shared.Return(vertexToMesh, clearArray: false);
            ArrayPool<int>.Shared.Return(usedGlobalIds, clearArray: false);
            ArrayPool<Vector3>.Shared.Return(positions, clearArray: false);
            ArrayPool<Vector3>.Shared.Return(normals, clearArray: false);
            ArrayPool<byte>.Shared.Return(materials, clearArray: false);
        }
    }

    private static int CellIndex(int x, int y, int z, int side, int plane) =>
        (x + 1) + (y + 1) * side + (z + 1) * plane;

    private static float Lerp(float a, float b, float t) =>
        a + (b - a) * t;

    private static void AddSurfaceQuad(
        MeshBatch batch,
        TerrainPalette palette,
        Vector3[] positions,
        Vector3[] normals,
        byte[] materials,
        int i0,
        int i1,
        int i2,
        int i3,
        bool positiveSide)
    {
        if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0 ||
            (uint)i0 >= (uint)positions.Length ||
            (uint)i1 >= (uint)positions.Length ||
            (uint)i2 >= (uint)positions.Length ||
            (uint)i3 >= (uint)positions.Length)
            return;

        batch.AddIndexedQuad(
            i0, i1, i2, i3,
            positions, normals, materials, palette,
            frontFace: positiveSide);
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
