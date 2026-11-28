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
    public int Step { get; }
    public bool IsCaptured { get; private set; }
    public bool HasPotentialSurface { get; private set; }

    public VoxelChunkBuildInput(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ,
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

        public void AddTriangle(
            Vector3 p0,
            Vector3 p1,
            Vector3 p2,
            Vector3 normal,
            Color color,
            bool frontFace)
        {
            if (_vertexCount + 3 > MaximumVerticesPerMesh ||
                _indexCount + 3 > MaximumIndicesPerMesh)
                Flush();

            ushort start = checked((ushort)_vertexCount);
            AddVertex(p0, normal, color);
            AddVertex(p1, normal, color);
            AddVertex(p2, normal, color);

            _indices[_indexCount++] = start;
            _indices[_indexCount++] = frontFace
                ? (ushort)(start + 1)
                : (ushort)(start + 2);
            _indices[_indexCount++] = frontFace
                ? (ushort)(start + 2)
                : (ushort)(start + 1);
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

    public static int CountVertices(IReadOnlyList<VoxelMeshData> sources)
    {
        int count = 0;
        for (int i = 0; i < sources.Count; i++)
            count = checked(count + sources[i].VertexCount);
        return count;
    }

    public static int CountIndices(IReadOnlyList<VoxelMeshData> sources)
    {
        int count = 0;
        for (int i = 0; i < sources.Count; i++)
            count = checked(count + sources[i].IndexCount);
        return count;
    }

    public static int CountTriangleVertices(IReadOnlyList<VoxelMeshData> sources) =>
        CountIndices(sources);

    public static unsafe void UpdateIndexedMesh(
        ref Mesh mesh,
        int vertexOffset,
        int vertexCapacity,
        int indexOffset,
        int indexCapacity,
        VoxelMeshData[] sources,
        int firstSource,
        int sourceCount)
    {
        if (vertexOffset < 0 || vertexCapacity <= 0 ||
            indexOffset < 0 || indexCapacity <= 0 ||
            firstSource < 0 || sourceCount < 0 ||
            firstSource + sourceCount > sources.Length ||
            mesh.VaoId == 0 || mesh.VboId == null)
            throw new ArgumentOutOfRangeException(nameof(vertexCapacity));

        int vertexCount = 0;
        int indexCount = 0;
        for (int i = firstSource, end = firstSource + sourceCount; i < end; i++)
        {
            vertexCount = checked(vertexCount + sources[i].VertexCount);
            indexCount = checked(indexCount + sources[i].IndexCount);
        }

        if (vertexCount > vertexCapacity || indexCount > indexCapacity)
            throw new ArgumentException("Indexed GPU region is too small.");

        if (vertexCount == 0 || indexCount == 0)
            return;

        Vector3[] vertices = ArrayPool<Vector3>.Shared.Rent(vertexCount);
        Vector3[] normals = ArrayPool<Vector3>.Shared.Rent(vertexCount);
        byte[] colors = ArrayPool<byte>.Shared.Rent(checked(vertexCount * 4));
        ushort[] indices = ArrayPool<ushort>.Shared.Rent(indexCount);

        try
        {
            int vertexCursor = 0;
            int indexCursor = 0;

            for (int sourceIndex = firstSource, end = firstSource + sourceCount;
                 sourceIndex < end; sourceIndex++)
            {
                VoxelMeshData source = sources[sourceIndex];
                ReadOnlySpan<Vector3> sourceVertices = source.Vertices;
                ReadOnlySpan<Vector3> sourceNormals = source.Normals;
                ReadOnlySpan<byte> sourceColors = source.Colors;
                ReadOnlySpan<ushort> sourceIndices = source.Indices;

                sourceVertices.CopyTo(vertices.AsSpan(vertexCursor));
                sourceNormals.CopyTo(normals.AsSpan(vertexCursor));
                sourceColors.CopyTo(colors.AsSpan(vertexCursor * 4));

                int localBase = vertexOffset + vertexCursor;
                for (int i = 0; i < sourceIndices.Length; i++)
                {
                    int localIndex = sourceIndices[i] - source.IndexBase;
                    if ((uint)localIndex >= (uint)sourceVertices.Length)
                        throw new InvalidOperationException(
                            "Surface index is outside its source mesh.");

                    int destinationIndex = localBase + localIndex;
                    if ((uint)destinationIndex > ushort.MaxValue)
                        throw new InvalidOperationException(
                            "Indexed GPU page exceeds 16-bit vertex addressing.");

                    indices[indexCursor + i] = (ushort)destinationIndex;
                }

                vertexCursor += source.VertexCount;
                indexCursor += source.IndexCount;
            }

            fixed (Vector3* p = vertices)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexVertices, p,
                    vertexCount * sizeof(Vector3),
                    vertexOffset * sizeof(Vector3));
            fixed (Vector3* p = normals)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexNormals, p,
                    vertexCount * sizeof(Vector3),
                    vertexOffset * sizeof(Vector3));
            fixed (byte* p = colors)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexColors, p,
                    vertexCount * 4,
                    vertexOffset * 4);
            fixed (ushort* p = indices)
                UpdateMeshBuffer(
                    mesh, Mesh.VboIdIndexIndices, p,
                    indexCount * sizeof(ushort),
                    indexOffset * sizeof(ushort));
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(vertices, clearArray: false);
            ArrayPool<Vector3>.Shared.Return(normals, clearArray: false);
            ArrayPool<byte>.Shared.Return(colors, clearArray: false);
            ArrayPool<ushort>.Shared.Return(indices, clearArray: false);
        }
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

    private readonly record struct CutRunKey(
        int StartSampleX,
        int EndSampleX,
        byte MaterialId);

    private struct CutRunRectangle
    {
        public int StartSampleZ;
        public int EndSampleZ;
    }

    private readonly record struct CutVertex(Vector3 Position, float Density);

    /// <summary>
    /// Builds the filled horizontal section directly from the authoritative density field.
    /// Fully solid cells are greedily merged into rectangles; contour cells are clipped
    /// against density zero and triangulated locally, so air pockets remain open.
    /// </summary>
    public static VoxelMeshData[] BuildCutSurfaceData(
        IVoxelFieldSource field,
        int layer,
        TerrainPalette palette,
        int step = 4,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(palette);
        if ((uint)layer > (uint)field.Height)
            throw new ArgumentOutOfRangeException(nameof(layer));
        if (step is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
            throw new ArgumentOutOfRangeException(nameof(step));

        int lowerY = layer - 1;
        int upperY = layer;
        int cellsX = (field.Width + step - 1) / step + 1;
        int cellsZ = (field.Depth + step - 1) / step + 1;
        int nodesX = cellsX + 1;

        float[] previous = ArrayPool<float>.Shared.Rent(nodesX);
        float[] current = ArrayPool<float>.Shared.Rent(nodesX);
        List<VoxelMeshData> output = new(8);
        Dictionary<CutRunKey, CutRunRectangle> activeRuns =
            new(Math.Max(16, cellsX / 4));
        Dictionary<CutRunKey, CutRunRectangle> nextRuns =
            new(Math.Max(16, cellsX / 4));

        using MeshBatch batch = new(
            output,
            Array.Empty<int>(),
            Array.Empty<int>());

        try
        {
            IVoxelColumnDensitySource? columnSource =
                field as IVoxelColumnDensitySource;

            SampleCutDensityRow(
                field, columnSource, lowerY, -step, step,
                previous, nodesX, cancellationToken);

            for (int row = 0; row < cellsZ; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int sampleZ0 = (row - 1) * step;
                int sampleZ1 = row * step;
                SampleCutDensityRow(
                    field, columnSource, lowerY, sampleZ1, step,
                    current, nodesX, cancellationToken);

                nextRuns.Clear();
                int x = 0;
                while (x < cellsX)
                {
                    float d0 = previous[x];
                    float d1 = current[x];
                    float d2 = current[x + 1];
                    float d3 = previous[x + 1];

                    if (d0 > 0f && d1 > 0f && d2 > 0f && d3 > 0f)
                    {
                        int runStart = x;
                        byte materialId = GetCutMaterial(
                            field,
                            (x - 1) * step,
                            sampleZ0,
                            (x * step),
                            sampleZ1,
                            lowerY,
                            upperY,
                            d0, d1, d2, d3);

                        x++;
                        while (x < cellsX)
                        {
                            float a = previous[x];
                            float b = current[x];
                            float c = current[x + 1];
                            float d = previous[x + 1];
                            if (a <= 0f || b <= 0f || c <= 0f || d <= 0f)
                                break;

                            byte nextMaterial = GetCutMaterial(
                                field,
                                (x - 1) * step,
                                sampleZ0,
                                x * step,
                                sampleZ1,
                                lowerY,
                                upperY,
                                a, b, c, d);
                            if (nextMaterial != materialId)
                                break;

                            x++;
                        }

                        int startSampleX = (runStart - 1) * step;
                        int endSampleX = (x - 1) * step;
                        CutRunKey key = new(
                            startSampleX,
                            endSampleX,
                            materialId);

                        CutRunRectangle rectangle = activeRuns.TryGetValue(
                            key, out CutRunRectangle existing)
                                ? existing
                                : new CutRunRectangle { StartSampleZ = sampleZ0 };

                        rectangle.EndSampleZ = sampleZ1;
                        nextRuns.Add(key, rectangle);
                        continue;
                    }

                    if (d0 > 0f || d1 > 0f || d2 > 0f || d3 > 0f)
                    {
                        AddCutContourCell(
                            batch,
                            field,
                            palette,
                            lowerY,
                            upperY,
                            step,
                            (x - 1) * step,
                            sampleZ0,
                            sampleZ1,
                            d0, d1, d2, d3);
                    }

                    x++;
                }

                foreach ((CutRunKey key, CutRunRectangle rectangle) in activeRuns)
                {
                    if (nextRuns.ContainsKey(key))
                        continue;

                    AddCutRectangle(batch, palette, key, rectangle, layer);
                }

                (activeRuns, nextRuns) = (nextRuns, activeRuns);
                (previous, current) = (current, previous);
            }

            foreach ((CutRunKey key, CutRunRectangle rectangle) in activeRuns)
                AddCutRectangle(batch, palette, key, rectangle, layer);

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
            ArrayPool<float>.Shared.Return(previous, clearArray: false);
            ArrayPool<float>.Shared.Return(current, clearArray: false);
        }
    }

    private static void SampleCutDensityRow(
        IVoxelFieldSource field,
        IVoxelColumnDensitySource? columnSource,
        int lowerY,
        int sampleZ,
        int step,
        float[] destination,
        int count,
        CancellationToken cancellationToken)
    {
        Span<float> vertical = stackalloc float[2];
        int upperY = lowerY + 1;

        for (int i = 0; i < count; i++)
        {
            if ((i & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int sampleX = (i - 1) * step;
            float lower;
            float upper;

            if (columnSource != null &&
                columnSource.TryGetDensityColumn(
                    sampleX, sampleZ, lowerY, 1, vertical))
            {
                lower = vertical[0];
                upper = vertical[1];
            }
            else
            {
                lower = field.GetDensity(sampleX, lowerY, sampleZ);
                upper = field.GetDensity(sampleX, upperY, sampleZ);
            }

            // Density samples live at voxel centres. An integer cut layer is
            // exactly halfway between the samples on either side of the plane.
            destination[i] = (lower + upper) * 0.5f;
        }
    }

    private static byte GetCutMaterial(
        IVoxelFieldSource field,
        int x0,
        int z0,
        int x1,
        int z1,
        int lowerY,
        int upperY,
        float d0,
        float d1,
        float d2,
        float d3)
    {
        float maximum = d0;
        int sampleX = x0;
        int sampleZ = z0;

        if (d1 > maximum) { maximum = d1; sampleZ = z1; }
        if (d2 > maximum) { maximum = d2; sampleX = x1; sampleZ = z1; }
        if (d3 > maximum) { maximum = d3; sampleX = x1; sampleZ = z0; }

        if (maximum <= 0f)
            return 1;

        ushort material = field.GetMaterialId(sampleX, lowerY, sampleZ);
        if (material == 0)
            material = field.GetMaterialId(sampleX, upperY, sampleZ);

        return material == 0 ? (byte)1 : checked((byte)material);
    }

    private static void AddCutRectangle(
        MeshBatch batch,
        TerrainPalette palette,
        CutRunKey key,
        CutRunRectangle rectangle,
        int layer)
    {
        float voxel = SparseVoxelField.VoxelSizeMeters;
        float y = layer * voxel;
        float x0 = (key.StartSampleX + 0.5f) * voxel;
        float x1 = (key.EndSampleX + 0.5f) * voxel;
        float z0 = (rectangle.StartSampleZ + 0.5f) * voxel;
        float z1 = (rectangle.EndSampleZ + 0.5f) * voxel;
        Color color = GetMaterialColor(palette, key.MaterialId);

        batch.AddQuad(
            new Vector3(x0, y, z0),
            new Vector3(x0, y, z1),
            new Vector3(x1, y, z1),
            new Vector3(x1, y, z0),
            Vector3.UnitY,
            color,
            frontFace: true);
    }

    private static void AddCutContourCell(
        MeshBatch batch,
        IVoxelFieldSource field,
        TerrainPalette palette,
        int lowerY,
        int upperY,
        int step,
        int sampleX,
        int sampleZ0,
        int sampleZ1,
        float d0,
        float d1,
        float d2,
        float d3)
    {
        float voxel = SparseVoxelField.VoxelSizeMeters;
        float y = (lowerY + 1) * voxel;
        float x0 = (sampleX + 0.5f) * voxel;
        float x1 = (sampleX + step + 0.5f) * voxel;
        float z0 = (sampleZ0 + 0.5f) * voxel;
        float z1 = (sampleZ1 + 0.5f) * voxel;

        CutVertex p0 = new(new Vector3(x0, y, z0), d0);
        CutVertex p1 = new(new Vector3(x0, y, z1), d1);
        CutVertex p2 = new(new Vector3(x1, y, z1), d2);
        CutVertex p3 = new(new Vector3(x1, y, z0), d3);

        byte materialId = GetCutMaterial(
            field,
            sampleX,
            sampleZ0,
            sampleX + step,
            sampleZ1,
            lowerY,
            upperY,
            d0, d1, d2, d3);
        Color color = GetMaterialColor(palette, materialId);

        bool alternating =
            (d0 > 0f) == (d2 > 0f) &&
            (d1 > 0f) == (d3 > 0f) &&
            (d0 > 0f) != (d1 > 0f);

        bool diagonal02 = true;
        if (alternating)
        {
            float asymptoticDecider = d0 * d2 - d1 * d3;
            diagonal02 = asymptoticDecider > 0f ||
                (asymptoticDecider == 0f &&
                 (((sampleX / step) ^ (sampleZ0 / step)) & 1) == 0);
        }

        Span<CutVertex> polygon = stackalloc CutVertex[4];
        if (diagonal02)
        {
            AddClippedCutTriangle(batch, p0, p1, p2, polygon, color);
            AddClippedCutTriangle(batch, p0, p2, p3, polygon, color);
        }
        else
        {
            AddClippedCutTriangle(batch, p0, p1, p3, polygon, color);
            AddClippedCutTriangle(batch, p1, p2, p3, polygon, color);
        }
    }

    private static void AddClippedCutTriangle(
        MeshBatch batch,
        CutVertex a,
        CutVertex b,
        CutVertex c,
        Span<CutVertex> output,
        Color color)
    {
        Span<CutVertex> triangle = stackalloc CutVertex[3];
        triangle[0] = a;
        triangle[1] = b;
        triangle[2] = c;

        int count = 0;
        CutVertex previous = triangle[2];
        bool previousInside = previous.Density > 0f;

        for (int i = 0; i < 3; i++)
        {
            CutVertex current = triangle[i];
            bool currentInside = current.Density > 0f;

            if (currentInside != previousInside)
            {
                float t = previous.Density /
                    (previous.Density - current.Density);
                output[count++] = new CutVertex(
                    Vector3.Lerp(previous.Position, current.Position, t),
                    0f);
            }

            if (currentInside)
                output[count++] = current;

            previous = current;
            previousInside = currentInside;
        }

        for (int i = 1; i + 1 < count; i++)
        {
            batch.AddTriangle(
                output[0].Position,
                output[i].Position,
                output[i + 1].Position,
                Vector3.UnitY,
                color,
                frontFace: true);
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
