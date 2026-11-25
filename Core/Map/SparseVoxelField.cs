using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Threading;

namespace Core.Map;

/// <summary>
/// Sparse, editable 3D scalar voxel field.
///
/// Coordinates are isotropic 0.1 m voxels on all three axes. Signed distance is
/// sampled at voxel centres; positive density is solid, negative density is air and
/// zero is the isosurface. Material IDs belong to cells, not to vertical columns.
/// Unmodified terrain is evaluated by a procedural base sampler; edited areas
/// materialize only the affected 16^3 chunks.
///
/// This keeps the 2304 x 2304 x 50 m world sparse while retaining a genuine
/// three-dimensional field for caves, arches, overhangs and CSG terrain edits.
/// </summary>
public sealed class SparseVoxelField : IVoxelFieldSource
{
    // 16^3 remains the render/edit grouping; storage follows VRage's 8^3 data cells.
    public const int ChunkSize = 16;
    public const int DataCellSize = 8;
    public const int DensitySamplesPerAxis = DataCellSize;
    public const float VoxelSizeMeters = 0.1f;
    public const float DensityScale = 32f;

    private const int SampleCount = DataCellSize * DataCellSize * DataCellSize;
    private const int CellCount = SampleCount;
    private const int SamplePlane = DataCellSize * DataCellSize;
    private const int CellPlane = DataCellSize * DataCellSize;
    private const int LegacyChunkSize = 16;
    private const int LegacySampleCount = LegacyChunkSize * LegacyChunkSize * LegacyChunkSize;
    private const int LegacyCellPlane = LegacyChunkSize * LegacyChunkSize;
    private const long MaximumEditSampleCount = 20_000_000;

    public readonly record struct ChunkCoordinate(int X, int Y, int Z);

    public readonly record struct VoxelBounds(
        int MinX, int MinY, int MinZ,
        int MaxX, int MaxY, int MaxZ);

    public readonly record struct WalkableSurface(
        float HeightMeters,
        Vector3 Normal,
        ushort MaterialId,
        float ClearanceMeters);

    private const int SaveMagic = 0x31585652;
    private const int SaveFormatVersion = 5;
    private const int LegacyContentMaskWordCount = (LegacySampleCount + 63) >> 6;
    private const byte ContentIsoLevel = 127;

    internal sealed class Chunk
    {
        private byte[]? _content;
        private byte[]? _material;
        private byte _uniformContent = DefaultEmptyContent;
        private byte _uniformMaterial = byte.MaxValue;
        private int _contentConstitution = -1;

        public long Revision;

        public bool IsContentUniform => Volatile.Read(ref _content) == null;
        public bool IsMaterialUniform => Volatile.Read(ref _material) == null;
        public byte UniformContent => Volatile.Read(ref _uniformContent);
        public byte UniformMaterial => Volatile.Read(ref _uniformMaterial);

        public byte GetContent(int index)
        {
            byte[]? values = Volatile.Read(ref _content);
            return values == null
                ? Volatile.Read(ref _uniformContent)
                : Volatile.Read(ref values[index]);
        }

        public byte GetMaterial(int index)
        {
            byte[]? values = Volatile.Read(ref _material);
            return values == null
                ? Volatile.Read(ref _uniformMaterial)
                : Volatile.Read(ref values[index]);
        }

        public void SetContent(int index, byte value)
        {
            byte[]? values = Volatile.Read(ref _content);
            if (values == null)
            {
                byte uniform = Volatile.Read(ref _uniformContent);
                if (uniform == value)
                    return;

                byte[] candidate = new byte[SampleCount];
                Array.Fill(candidate, uniform);
                values = Interlocked.CompareExchange(ref _content, candidate, null) ?? candidate;
            }

            Volatile.Write(ref values[index], value);
            Volatile.Write(ref _contentConstitution, -1);
        }

        public void SetMaterial(int index, byte value)
        {
            byte[]? values = Volatile.Read(ref _material);
            if (values == null)
            {
                byte uniform = Volatile.Read(ref _uniformMaterial);
                if (uniform == value)
                    return;

                byte[] candidate = new byte[CellCount];
                Array.Fill(candidate, uniform);
                values = Interlocked.CompareExchange(ref _material, candidate, null) ?? candidate;
            }

            Volatile.Write(ref values[index], value);
        }

        public void SetUniformContent(byte value)
        {
            Volatile.Write(ref _uniformContent, value);
            Volatile.Write(ref _content, null);
            Volatile.Write(ref _contentConstitution, -1);
        }

        public void SetUniformMaterial(byte value)
        {
            Volatile.Write(ref _uniformMaterial, value);
            Volatile.Write(ref _material, null);
        }

        public void SetContentData(ReadOnlySpan<byte> source)
        {
            if (source.Length != SampleCount)
                throw new ArgumentException("Invalid voxel content page size.", nameof(source));

            byte first = source[0];
            bool uniform = true;
            for (int i = 1; i < source.Length; i++)
            {
                if (source[i] == first)
                    continue;
                uniform = false;
                break;
            }

            if (uniform)
            {
                SetUniformContent(first);
                return;
            }

            Volatile.Write(ref _uniformContent, first);
            Volatile.Write(ref _content, source.ToArray());
            Volatile.Write(ref _contentConstitution, -1);
        }

        public void SetMaterialData(ReadOnlySpan<byte> source)
        {
            if (source.Length != CellCount)
                throw new ArgumentException("Invalid voxel material page size.", nameof(source));

            byte first = source[0];
            bool uniform = true;
            for (int i = 1; i < source.Length; i++)
            {
                if (source[i] == first)
                    continue;
                uniform = false;
                break;
            }

            if (uniform)
            {
                SetUniformMaterial(first);
                return;
            }

            Volatile.Write(ref _uniformMaterial, first);
            Volatile.Write(ref _material, source.ToArray());
        }

        public byte[] CopyContent()
        {
            byte[] result = new byte[SampleCount];
            byte[]? values = Volatile.Read(ref _content);
            if (values == null)
                Array.Fill(result, Volatile.Read(ref _uniformContent));
            else
                values.AsSpan().CopyTo(result);
            return result;
        }

        public byte[] CopyMaterial()
        {
            byte[] result = new byte[CellCount];
            byte[]? values = Volatile.Read(ref _material);
            if (values == null)
                Array.Fill(result, Volatile.Read(ref _uniformMaterial));
            else
                values.AsSpan().CopyTo(result);
            return result;
        }

        public void Compress()
        {
            byte[]? content = Volatile.Read(ref _content);
            if (content != null)
            {
                byte first = content[0];
                bool uniform = true;
                for (int i = 1; i < content.Length; i++)
                {
                    if (content[i] == first)
                        continue;
                    uniform = false;
                    break;
                }

                if (uniform)
                    SetUniformContent(first);
            }

            byte[]? material = Volatile.Read(ref _material);
            if (material != null)
            {
                byte first = material[0];
                bool uniform = true;
                for (int i = 1; i < material.Length; i++)
                {
                    if (material[i] == first)
                        continue;
                    uniform = false;
                    break;
                }

                if (uniform)
                    SetUniformMaterial(first);
            }
        }

        public VoxelContentConstitution GetContentConstitution()
        {
            int cached = Volatile.Read(ref _contentConstitution);
            if (cached >= 0)
                return (VoxelContentConstitution)cached;

            byte[]? content = Volatile.Read(ref _content);
            if (content == null)
            {
                VoxelContentConstitution uniform =
                    Volatile.Read(ref _uniformContent) > ContentIsoLevel
                        ? VoxelContentConstitution.Full
                        : VoxelContentConstitution.Empty;
                Volatile.Write(ref _contentConstitution, (int)uniform);
                return uniform;
            }

            bool hasSolid = false;
            bool hasAir = false;
            for (int i = 0; i < content.Length; i++)
            {
                if (Volatile.Read(ref content[i]) > ContentIsoLevel)
                    hasSolid = true;
                else
                    hasAir = true;

                if (hasSolid && hasAir)
                    break;
            }

            VoxelContentConstitution result = hasSolid
                ? hasAir ? VoxelContentConstitution.Mixed : VoxelContentConstitution.Full
                : VoxelContentConstitution.Empty;
            Volatile.Write(ref _contentConstitution, (int)result);
            return result;
        }

        public int StorageBytes =>
            (Volatile.Read(ref _content)?.Length ?? 1) +
            (Volatile.Read(ref _material)?.Length ?? 1);

        public void Write(BinaryWriter writer)
        {
            byte[]? content = Volatile.Read(ref _content);
            writer.Write(content == null ? (byte)0 : (byte)1);
            if (content == null)
                writer.Write(Volatile.Read(ref _uniformContent));
            else
                writer.Write(content);

            byte[]? material = Volatile.Read(ref _material);
            writer.Write(material == null ? (byte)0 : (byte)1);
            if (material == null)
                writer.Write(Volatile.Read(ref _uniformMaterial));
            else
                writer.Write(material);
        }

        public void Read(BinaryReader reader)
        {
            byte contentMode = reader.ReadByte();
            if (contentMode == 0)
            {
                SetUniformContent(reader.ReadByte());
            }
            else if (contentMode == 1)
            {
                byte[] values = reader.ReadBytes(SampleCount);
                if (values.Length != SampleCount)
                    throw new EndOfStreamException();
                SetContentData(values);
            }
            else
            {
                throw new InvalidDataException("Invalid voxel content page encoding.");
            }

            byte materialMode = reader.ReadByte();
            if (materialMode == 0)
            {
                SetUniformMaterial(reader.ReadByte());
            }
            else if (materialMode == 1)
            {
                byte[] values = reader.ReadBytes(CellCount);
                if (values.Length != CellCount)
                    throw new EndOfStreamException();
                SetMaterialData(values);
            }
            else
            {
                throw new InvalidDataException("Invalid voxel material page encoding.");
            }
        }
    }

    public enum VoxelContentConstitution : byte
    {
        Empty,
        Full,
        Mixed
    }

    private sealed class ChunkSnapshot
    {
        public required byte[] Content { get; init; }
        public required byte[] Material { get; init; }

        public static ChunkSnapshot From(Chunk chunk) => new()
        {
            Content = chunk.CopyContent(),
            Material = chunk.CopyMaterial()
        };

        public Chunk ToChunk(long revision)
        {
            Chunk chunk = new() { Revision = revision };
            chunk.SetContentData(Content);
            chunk.SetMaterialData(Material);
            return chunk;
        }

        public byte GetContent(int index) => Content[index];
        public byte GetMaterial(int index) => Material[index];

        public bool ContentEquals(ChunkSnapshot other) =>
            Content.AsSpan().SequenceEqual(other.Content) &&
            Material.AsSpan().SequenceEqual(other.Material);
    }

    private sealed class ChunkFieldSnapshot : IVoxelFieldSource
    {
        private readonly Func<int, int, int, float> _baseDensity;
        private readonly Func<int, int, int, ushort> _baseMaterial;
        private readonly Dictionary<ChunkCoordinate, ChunkSnapshot> _chunks;

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        public ChunkFieldSnapshot(
            int width,
            int height,
            int depth,
            Func<int, int, int, float> baseDensity,
            Func<int, int, int, ushort> baseMaterial,
            Dictionary<ChunkCoordinate, ChunkSnapshot> chunks)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _baseDensity = baseDensity;
            _baseMaterial = baseMaterial;
            _chunks = chunks;
        }

        public float GetDensity(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y > Height || z >= Depth)
                return -1f;
            if (y == Height)
                return DecodeContent(EncodeContent(_baseDensity(x, y, z)));

            ChunkCoordinate key = new(x / DataCellSize, y / DataCellSize, z / DataCellSize);
            int index = x % DataCellSize +
                (y % DataCellSize) * DensitySamplesPerAxis +
                (z % DataCellSize) * SamplePlane;

            return _chunks.TryGetValue(key, out ChunkSnapshot? chunk)
                ? DecodeContent(chunk.GetContent(index))
                : DecodeContent(EncodeContent(_baseDensity(x, y, z)));
        }

        public ushort GetMaterialId(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth)
                return 0;

            ChunkCoordinate key = new(x / DataCellSize, y / DataCellSize, z / DataCellSize);
            int index = x % DataCellSize +
                (y % DataCellSize) * DataCellSize +
                (z % DataCellSize) * CellPlane;

            if (_chunks.TryGetValue(key, out ChunkSnapshot? chunk))
                return chunk.GetContent(index) > ContentIsoLevel &&
                    chunk.GetMaterial(index) != byte.MaxValue
                        ? chunk.GetMaterial(index)
                        : (ushort)0;

            byte baseContent = EncodeContent(_baseDensity(x, y, z));
            if (baseContent <= ContentIsoLevel)
                return 0;
            return DecodeMaterial(EncodeMaterial(_baseMaterial(x, y, z)));
        }

        public byte GetBlockId(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth)
                return 0;

            ChunkCoordinate key = new(x / DataCellSize, y / DataCellSize, z / DataCellSize);
            int index = x % DataCellSize +
                (y % DataCellSize) * DataCellSize +
                (z % DataCellSize) * CellPlane;

            if (_chunks.TryGetValue(key, out ChunkSnapshot? chunk))
                return chunk.GetContent(index) > ContentIsoLevel &&
                    chunk.GetMaterial(index) != byte.MaxValue
                        ? chunk.GetMaterial(index)
                        : (byte)0;

            if (EncodeContent(_baseDensity(x, y, z)) <= ContentIsoLevel)
                return 0;

            byte baseMaterial = EncodeMaterial(_baseMaterial(x, y, z));
            return baseMaterial == byte.MaxValue ? (byte)0 : baseMaterial;
        }
    }

    private sealed class EditHistoryCapture
    {
        public readonly Dictionary<ChunkCoordinate, ChunkSnapshot?> Before = new();
        public VoxelBounds Bounds;
        public bool HasBounds;
    }

    private sealed class EditHistoryEntry
    {
        public required Dictionary<ChunkCoordinate, ChunkSnapshot?> Before { get; init; }
        public required Dictionary<ChunkCoordinate, ChunkSnapshot?> After { get; init; }
        public required VoxelBounds Bounds { get; init; }
        public long EstimatedBytes { get; init; }
    }


    /// <summary>
    /// Incremental bake of a settled dynamic fragment into the authoritative
    /// world lattice. The field is modified in bounded batches; one Changed
    /// event and one undo record are emitted only after the complete merge.
    /// </summary>
    public sealed class FragmentMergeJob
    {
        internal readonly SparseVoxelField Owner;
        internal readonly VoxelFragmentSnapshot Fragment;
        internal readonly Vector3 BodyPosition;
        internal readonly Quaternion InverseRotation;
        internal readonly HashSet<ChunkCoordinate> Touched = new();
        internal readonly IDisposable HistoryScope;
        internal readonly long TotalSamples;
        internal long Cursor;
        internal long Revision;
        internal int MinX, MinY, MinZ;
        internal int MaxX, MaxY, MaxZ;
        public VoxelBounds Bounds { get; }
        public bool IsCompleted { get; internal set; }
        public long ProcessedSamples => Cursor;
        public long Total => TotalSamples;

        internal FragmentMergeJob(
            SparseVoxelField owner,
            VoxelFragmentSnapshot fragment,
            Vector3 bodyPosition,
            Quaternion inverseRotation,
            VoxelBounds bounds,
            IDisposable historyScope)
        {
            Owner = owner;
            Fragment = fragment;
            BodyPosition = bodyPosition;
            InverseRotation = inverseRotation;
            Bounds = bounds;
            HistoryScope = historyScope;
            TotalSamples = (long)(bounds.MaxX - bounds.MinX) *
                (bounds.MaxY - bounds.MinY) *
                (bounds.MaxZ - bounds.MinZ);
            Revision = owner.Version + 1;
            MinX = owner.Width;
            MinY = owner.Height;
            MinZ = owner.Depth;
            MaxX = 0;
            MaxY = 0;
            MaxZ = 0;
        }
    }

    private sealed class UndoScope : IDisposable
    {
        private SparseVoxelField? _owner;

        public UndoScope(SparseVoxelField owner) => _owner = owner;

        public void Dispose()
        {
            SparseVoxelField? owner = _owner;
            if (owner == null)
                return;

            _owner = null;
            owner.FinishUndoableEdit();
        }
    }

    private const int MaximumUndoEntries = 16;
    private const long MaximumUndoBytes = 64L * 1024 * 1024;
    private const long ChunkSnapshotBytes = SampleCount + CellCount;

    private readonly SparseVoxelOctreeIndex<Chunk> _octreeIndex;
    private readonly HashSet<(int X, int Z)> _editedChunkColumns = new();
    private readonly List<EditHistoryEntry> _undoHistory = new();
    private readonly List<EditHistoryEntry> _redoHistory = new();
    private readonly Func<int, int, int, float> _baseDensity;
    private readonly Func<int, int, int, ushort> _baseMaterial;
    private EditHistoryCapture? _historyCapture;
    private long _undoHistoryBytes;
    private bool _isRestoringHistory;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }

    /// <summary>
    /// Maps an 8^3 data-cell halo sample index (0..10) onto the global
    /// density lattice. Adjacent data cells therefore address identical boundary
    /// samples, including all supported LOD steps.
    /// </summary>
    public static int GetChunkSampleCoordinate(int chunkIndex, int sampleIndex, int step) =>
        GetChunkSampleCoordinate(chunkIndex, sampleIndex, step, haloSize: 1);

    public static int GetChunkSampleCoordinate(
        int chunkIndex,
        int sampleIndex,
        int step,
        int haloSize)
    {
        if (chunkIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(chunkIndex));
        if (haloSize < 1 || haloSize > 4)
            throw new ArgumentOutOfRangeException(nameof(haloSize));
        if (sampleIndex < 0 ||
            sampleIndex >= DataCellSize + haloSize * 2 + 1)
            throw new ArgumentOutOfRangeException(nameof(sampleIndex));
        if (step < 1)
            throw new ArgumentOutOfRangeException(nameof(step));

        return checked(chunkIndex * DataCellSize * step +
            (sampleIndex - haloSize) * step);
    }

    public int ChunkCount => _octreeIndex.LeafCount;
    public int OctreeNodeCount => _octreeIndex.NodeCount;
    public int StorageDataCellSize => DataCellSize;

    public int GetDataCellStorageBytes(int cellX, int cellY, int cellZ) =>
        _octreeIndex.TryGetLeaf(new ChunkCoordinate(cellX, cellY, cellZ), out Chunk? page)
            ? page?.StorageBytes ?? 0
            : 0;

    public VoxelContentConstitution GetDataCellConstitution(int cellX, int cellY, int cellZ) =>
        _octreeIndex.TryGetLeaf(new ChunkCoordinate(cellX, cellY, cellZ), out Chunk? page) && page != null
            ? page.GetContentConstitution()
            : VoxelContentConstitution.Mixed;

    public VoxelContentConstitution GetOctreeContentConstitution(
        int lod, int cellX, int cellY, int cellZ) =>
        _octreeIndex.GetConstitution(lod, cellX, cellY, cellZ);
    public int UndoCount => _undoHistory.Count;
    public int RedoCount => _redoHistory.Count;
    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;
    public long Version { get; private set; }
    public long BaseVersion { get; private set; }

    public event Action<VoxelBounds>? Changed;

    public SparseVoxelField(
        int width,
        int height,
        int depth,
        Func<int, int, int, float> baseDensity,
        Func<int, int, int, ushort> baseMaterial)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));

        Width = width;
        Height = height;
        Depth = depth;
        _baseDensity = baseDensity ?? throw new ArgumentNullException(nameof(baseDensity));
        _baseMaterial = baseMaterial ?? throw new ArgumentNullException(nameof(baseMaterial));
        _octreeIndex = new SparseVoxelOctreeIndex<Chunk>(
            (width + DataCellSize - 1) / DataCellSize,
            (height + DataCellSize - 1) / DataCellSize,
            (depth + DataCellSize - 1) / DataCellSize,
            static page => page.GetContentConstitution());
    }

    /// <summary>
    /// Copies only edited storage chunks needed by one render chunk. The returned
    /// source is immutable and safe for worker-thread density/material capture;
    /// procedural base samplers are shared because they are read-only.
    /// </summary>
    public IVoxelFieldSource CreateChunkSnapshot(
        int chunkX,
        int chunkY,
        int chunkZ,
        int step)
    {
        if (chunkX < 0 || chunkY < 0 || chunkZ < 0 || step <= 0)
            throw new ArgumentOutOfRangeException(nameof(step));

        int originX = chunkX * DataCellSize * step;
        int originY = chunkY * DataCellSize * step;
        int originZ = chunkZ * DataCellSize * step;

        int minX = Math.Max(0, originX - step * 2);
        int minY = Math.Max(0, originY - step * 2);
        int minZ = Math.Max(0, originZ - step * 2);
        int maxX = Math.Min(Width - 1, originX + (DataCellSize + 1) * step);
        int maxY = Math.Min(Height, originY + (DataCellSize + 1) * step);
        int maxZ = Math.Min(Depth - 1, originZ + (DataCellSize + 1) * step);

        int minChunkX = minX / DataCellSize;
        int minChunkY = minY / DataCellSize;
        int minChunkZ = minZ / DataCellSize;
        int maxChunkX = maxX / DataCellSize;
        int maxChunkY = maxY / DataCellSize;
        int maxChunkZ = maxZ / DataCellSize;

        Dictionary<ChunkCoordinate, ChunkSnapshot> snapshots = new();
        for (int z = minChunkZ; z <= maxChunkZ; z++)
        for (int y = minChunkY; y <= maxChunkY; y++)
        for (int x = minChunkX; x <= maxChunkX; x++)
        {
            ChunkCoordinate key = new(x, y, z);
            if (_octreeIndex.TryGetLeaf(key, out Chunk? chunk))
                snapshots.Add(key, ChunkSnapshot.From(chunk));
        }

        return new ChunkFieldSnapshot(
            Width,
            Height,
            Depth,
            _baseDensity,
            _baseMaterial,
            snapshots);
    }

    /// <summary>
    /// Captures the sparse edited storage chunks touched by an arbitrary
    /// fixed-resolution render region. The region uses voxel coordinates,
    /// independently of the 16^3 storage chunk dimensions.
    /// </summary>
    public IVoxelFieldSource CreateRegionSnapshot(
        int originX,
        int originY,
        int originZ,
        int cellsPerAxis)
    {
        if (originX < 0 || originY < 0 || originZ < 0 ||
            originX >= Width || originY >= Height || originZ >= Depth ||
            cellsPerAxis <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellsPerAxis));
        }

        const int halo = 1;
        int minX = Math.Max(0, originX - halo);
        int minY = Math.Max(0, originY - halo);
        int minZ = Math.Max(0, originZ - halo);
        int maxX = Math.Min(Width - 1, originX + cellsPerAxis);
        int maxY = Math.Min(Height - 1, originY + cellsPerAxis);
        int maxZ = Math.Min(Depth - 1, originZ + cellsPerAxis);

        int minChunkX = minX / DataCellSize;
        int minChunkY = minY / DataCellSize;
        int minChunkZ = minZ / DataCellSize;
        int maxChunkX = maxX / DataCellSize;
        int maxChunkY = maxY / DataCellSize;
        int maxChunkZ = maxZ / DataCellSize;

        Dictionary<ChunkCoordinate, ChunkSnapshot> snapshots = new();
        for (int z = minChunkZ; z <= maxChunkZ; z++)
        for (int y = minChunkY; y <= maxChunkY; y++)
        for (int x = minChunkX; x <= maxChunkX; x++)
        {
            ChunkCoordinate key = new(x, y, z);
            if (_octreeIndex.TryGetLeaf(key, out Chunk? chunk))
                snapshots.Add(key, ChunkSnapshot.From(chunk));
        }

        return new ChunkFieldSnapshot(
            Width,
            Height,
            Depth,
            _baseDensity,
            _baseMaterial,
            snapshots);
    }

    public float GetDensity(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 ||
            x >= Width || y > Height || z >= Depth)
            return -1f;
        if (y == Height)
            return DecodeContent(EncodeContent(_baseDensity(x, y, z)));

        ChunkCoordinate key = GetSampleChunk(x, y, z, out int index);
        if (_octreeIndex.TryGetLeaf(key, out Chunk? chunk))
            return DecodeContent(chunk.GetContent(index));

        return DecodeContent(EncodeContent(_baseDensity(x, y, z)));
    }

    /// <summary>Trilinearly samples the same scalar field used by the mesher.</summary>
    public float SampleDensity(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return -1f;

        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);
        float tx = x - x0;
        float ty = y - y0;
        float tz = z - z0;

        float c000 = GetDensity(x0, y0, z0);
        float c100 = GetDensity(x0 + 1, y0, z0);
        float c010 = GetDensity(x0, y0 + 1, z0);
        float c110 = GetDensity(x0 + 1, y0 + 1, z0);
        float c001 = GetDensity(x0, y0, z0 + 1);
        float c101 = GetDensity(x0 + 1, y0, z0 + 1);
        float c011 = GetDensity(x0, y0 + 1, z0 + 1);
        float c111 = GetDensity(x0 + 1, y0 + 1, z0 + 1);

        float x00 = Lerp(c000, c100, tx);
        float x10 = Lerp(c010, c110, tx);
        float x01 = Lerp(c001, c101, tx);
        float x11 = Lerp(c011, c111, tx);
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    /// <summary>Returns the material occupying one 0.1 m cubic cell, or zero for air.</summary>
    public ushort GetMaterialId(int x, int y, int z)
    {
        if (!IsInsideCell(x, y, z))
            return 0;

        ChunkCoordinate key = GetCellChunk(x, y, z, out int index);
        if (_octreeIndex.TryGetLeaf(key, out Chunk? chunk))
        {
            byte material = chunk.GetMaterial(index);
            return chunk.GetContent(index) > ContentIsoLevel &&
                material != byte.MaxValue
                    ? material
                    : (ushort)0;
        }

        if (EncodeContent(_baseDensity(x, y, z)) <= ContentIsoLevel)
            return 0;
        return DecodeMaterial(EncodeMaterial(_baseMaterial(x, y, z)));
    }

    /// <summary>
    /// Renderer fast path. Resolves occupancy from byte-sized material overrides
    /// and the procedural base material; no float SDF sampling is performed.
    /// </summary>
    public byte GetBlockId(int x, int y, int z)
    {
        if (!IsInsideCell(x, y, z))
            return 0;

        ChunkCoordinate key = GetCellChunk(x, y, z, out int index);
        if (_octreeIndex.TryGetLeaf(key, out Chunk? chunk))
        {
            byte material = chunk.GetMaterial(index);
            return chunk.GetContent(index) > ContentIsoLevel &&
                material != byte.MaxValue
                    ? material
                    : (byte)0;
        }

        if (EncodeContent(_baseDensity(x, y, z)) <= ContentIsoLevel)
            return 0;

        byte baseMaterial = EncodeMaterial(_baseMaterial(x, y, z));
        return baseMaterial == byte.MaxValue ? (byte)0 : baseMaterial;
    }



    /// <summary>
    /// Finds the highest upward-facing solid/air surface at one horizontal point.
    /// Used by legacy systems that expect one surface height for an XY column.
    /// Multi-level navigation must use FindWalkableSurfacesAt instead.
    /// </summary>
    public bool TryFindHighestWalkableSurfaceAt(
        float xMeters,
        float zMeters,
        float minimumUpNormal,
        out WalkableSurface surface)
    {
        surface = default;
        if (!float.IsFinite(xMeters) || !float.IsFinite(zMeters))
            return false;

        float sampleX = xMeters / VoxelSizeMeters - 0.5f;
        float sampleZ = zMeters / VoxelSizeMeters - 0.5f;
        if (sampleX < 0f || sampleZ < 0f ||
            sampleX > Width - 1 || sampleZ > Depth - 1)
            return false;

        float minNormal = Math.Clamp(minimumUpNormal, 0.05f, 1f);
        int xCell = Math.Clamp((int)MathF.Round(sampleX), 0, Width - 1);
        int zCell = Math.Clamp((int)MathF.Round(sampleZ), 0, Depth - 1);

        for (int y = Height - 2; y >= 0; y--)
        {
            float lower = SampleDensity(sampleX, y, sampleZ);
            float upper = SampleDensity(sampleX, y + 1, sampleZ);
            if (lower <= 0f || upper > 0f)
                continue;

            float denominator = lower - upper;
            float blend = denominator > 0.00001f
                ? Math.Clamp(lower / denominator, 0f, 1f)
                : 0.5f;
            float surfaceSampleY = y + blend;
            float heightMeters = (surfaceSampleY + 0.5f) * VoxelSizeMeters;
            Vector3 gradient = new(
                SampleDensity(sampleX + 1f, surfaceSampleY, sampleZ) -
                    SampleDensity(sampleX - 1f, surfaceSampleY, sampleZ),
                SampleDensity(sampleX, surfaceSampleY + 1f, sampleZ) -
                    SampleDensity(sampleX, surfaceSampleY - 1f, sampleZ),
                SampleDensity(sampleX, surfaceSampleY, sampleZ + 1f) -
                    SampleDensity(sampleX, surfaceSampleY, sampleZ - 1f));
            Vector3 normal = gradient.LengthSquared() > 0.000001f
                ? -Vector3.Normalize(gradient)
                : Vector3.UnitY;
            if (normal.Y < minNormal)
                continue;

            surface = new WalkableSurface(
                heightMeters,
                normal,
                GetMaterialId(xCell, Math.Clamp(y, 0, Height - 1), zCell),
                MathF.Max(0f, Height * VoxelSizeMeters - heightMeters));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tests a legacy one-metre map column against the 0.1 m density field.
    /// The vertical interval is inclusive and uses the world's 0.1 m layer index.
    /// </summary>
    public bool IsColumnSolidBetweenTiles(
        int tileX,
        int tileZ,
        int firstLayer,
        int lastLayer)
    {
        if (tileX < 0 || tileZ < 0 ||
            tileX * 10 + 9 >= Width || tileZ * 10 + 9 >= Depth)
            return true;

        int first = Math.Max(0, firstLayer);
        int last = Math.Min(Height - 1, lastLayer);
        if (first > last)
            return false;

        float sampleX = tileX * 10 + 4.5f;
        float sampleZ = tileZ * 10 + 4.5f;
        for (int y = first; y <= last; y++)
        {
            if (SampleDensity(sampleX, y, sampleZ) > 0f)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Finds every upward-facing solid/air boundary below this horizontal point.
    /// The returned list is ordered from the lowest floor to the highest bridge.
    /// Heights and horizontal coordinates are in metres; field Y is world height.
    /// </summary>
    public int FindWalkableSurfacesAt(
        float xMeters,
        float zMeters,
        float minimumUpNormal,
        Span<WalkableSurface> destination)
    {
        if (destination.IsEmpty ||
            !float.IsFinite(xMeters) || !float.IsFinite(zMeters))
            return 0;

        float sampleX = xMeters / VoxelSizeMeters - 0.5f;
        float sampleZ = zMeters / VoxelSizeMeters - 0.5f;
        if (sampleX < 0f || sampleZ < 0f ||
            sampleX > Width - 1 || sampleZ > Depth - 1)
            return 0;

        float minNormal = Math.Clamp(minimumUpNormal, 0.05f, 1f);
        int xCell = Math.Clamp((int)MathF.Round(sampleX), 0, Width - 1);
        int zCell = Math.Clamp((int)MathF.Round(sampleZ), 0, Depth - 1);
        int count = 0;

        for (int y = 0; y < Height - 1 && count < destination.Length; y++)
        {
            float lower = SampleDensity(sampleX, y, sampleZ);
            float upper = SampleDensity(sampleX, y + 1, sampleZ);
            if (lower <= 0f || upper > 0f)
                continue;

            float denominator = lower - upper;
            float blend = denominator > 0.00001f
                ? Math.Clamp(lower / denominator, 0f, 1f)
                : 0.5f;
            float surfaceSampleY = y + blend;
            float heightMeters = (surfaceSampleY + 0.5f) * VoxelSizeMeters;

            Vector3 gradient = new(
                SampleDensity(sampleX + 1f, surfaceSampleY, sampleZ) -
                    SampleDensity(sampleX - 1f, surfaceSampleY, sampleZ),
                SampleDensity(sampleX, surfaceSampleY + 1f, sampleZ) -
                    SampleDensity(sampleX, surfaceSampleY - 1f, sampleZ),
                SampleDensity(sampleX, surfaceSampleY, sampleZ + 1f) -
                    SampleDensity(sampleX, surfaceSampleY, sampleZ - 1f));

            // Density is positive inside matter, so the outward normal is -gradient.
            Vector3 normal = gradient.LengthSquared() > 0.000001f
                ? -Vector3.Normalize(gradient)
                : Vector3.UnitY;
            if (normal.Y < minNormal)
                continue;

            float ceilingMeters = Height * VoxelSizeMeters;
            for (int ceilingY = y + 1; ceilingY < Height; ceilingY++)
            {
                if (SampleDensity(sampleX, ceilingY, sampleZ) <= 0f)
                    continue;

                ceilingMeters = (ceilingY + 0.5f) * VoxelSizeMeters;
                break;
            }

            float clearance = MathF.Max(0f, ceilingMeters - heightMeters);
            ushort material = GetMaterialId(xCell, y, zCell);
            destination[count++] = new WalkableSurface(
                heightMeters, normal, material, clearance);
        }

        return count;
    }

    /// <summary>
    /// Resolves the closest walkable floor near an existing foot height. The
    /// bounded vertical search is intended for per-step movement collision.
    /// </summary>
    public bool TryFindClosestWalkableSurface(
        float xMeters,
        float zMeters,
        float referenceHeightMeters,
        float searchBelowMeters,
        float searchAboveMeters,
        float minimumBodyClearanceMeters,
        float minimumUpNormal,
        out WalkableSurface surface)
    {
        surface = default;
        if (!float.IsFinite(referenceHeightMeters) ||
            !float.IsFinite(searchBelowMeters) ||
            !float.IsFinite(searchAboveMeters) ||
            searchBelowMeters < 0f || searchAboveMeters < 0f)
            return false;

        float sampleX = xMeters / VoxelSizeMeters - 0.5f;
        float sampleZ = zMeters / VoxelSizeMeters - 0.5f;
        if (sampleX < 0f || sampleZ < 0f ||
            sampleX > Width - 1 || sampleZ > Depth - 1)
            return false;

        int firstY = Math.Clamp(
            (int)MathF.Floor(
                (referenceHeightMeters - searchBelowMeters) / VoxelSizeMeters - 0.5f),
            0, Height - 2);
        int lastY = Math.Clamp(
            (int)MathF.Ceiling(
                (referenceHeightMeters + searchAboveMeters) / VoxelSizeMeters - 0.5f),
            firstY + 1, Height - 1);

        float minNormal = Math.Clamp(minimumUpNormal, 0.05f, 1f);
        int xCell = Math.Clamp((int)MathF.Round(sampleX), 0, Width - 1);
        int zCell = Math.Clamp((int)MathF.Round(sampleZ), 0, Depth - 1);
        float bestDistance = float.PositiveInfinity;
        bool found = false;

        for (int y = firstY; y < lastY; y++)
        {
            float lower = SampleDensity(sampleX, y, sampleZ);
            float upper = SampleDensity(sampleX, y + 1, sampleZ);
            if (lower <= 0f || upper > 0f)
                continue;

            float denominator = lower - upper;
            float blend = denominator > 0.00001f
                ? Math.Clamp(lower / denominator, 0f, 1f)
                : 0.5f;
            float surfaceSampleY = y + blend;
            float heightMeters = (surfaceSampleY + 0.5f) * VoxelSizeMeters;

            Vector3 gradient = new(
                SampleDensity(sampleX + 1f, surfaceSampleY, sampleZ) -
                    SampleDensity(sampleX - 1f, surfaceSampleY, sampleZ),
                SampleDensity(sampleX, surfaceSampleY + 1f, sampleZ) -
                    SampleDensity(sampleX, surfaceSampleY - 1f, sampleZ),
                SampleDensity(sampleX, surfaceSampleY, sampleZ + 1f) -
                    SampleDensity(sampleX, surfaceSampleY, sampleZ - 1f));
            Vector3 normal = gradient.LengthSquared() > 0.000001f
                ? -Vector3.Normalize(gradient)
                : Vector3.UnitY;
            if (normal.Y < minNormal)
                continue;

            float ceilingMeters = Height * VoxelSizeMeters;
            for (int ceilingY = y + 1; ceilingY < Height; ceilingY++)
            {
                if (SampleDensity(sampleX, ceilingY, sampleZ) <= 0f)
                    continue;

                ceilingMeters = (ceilingY + 0.5f) * VoxelSizeMeters;
                break;
            }

            float clearance = MathF.Max(0f, ceilingMeters - heightMeters);
            if (clearance + 0.0001f < minimumBodyClearanceMeters)
                continue;

            float distance = MathF.Abs(heightMeters - referenceHeightMeters);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            surface = new WalkableSurface(
                heightMeters, normal, GetMaterialId(xCell, y, zCell), clearance);
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Tests a vertical capsule approximation against the same signed-distance
    /// field used by rendering. The centre plus eight radial samples catches
    /// overhangs and tunnel walls without creating a dense collision grid.
    /// </summary>
    public bool IsCapsuleClear(
        float xMeters,
        float feetHeightMeters,
        float zMeters,
        float radiusMeters,
        float heightMeters)
    {
        if (!float.IsFinite(xMeters) || !float.IsFinite(feetHeightMeters) ||
            !float.IsFinite(zMeters) || !float.IsFinite(radiusMeters) ||
            !float.IsFinite(heightMeters) || radiusMeters <= 0f ||
            heightMeters <= 2f * radiusMeters)
            return false;

        float sampleX = xMeters / VoxelSizeMeters - 0.5f;
        float sampleZ = zMeters / VoxelSizeMeters - 0.5f;
        float offset = radiusMeters * 0.82f;
        float diagonal = offset * 0.70710678f;
        Span<Vector2> ring = stackalloc Vector2[9]
        {
            Vector2.Zero,
            new Vector2(offset, 0f),
            new Vector2(-offset, 0f),
            new Vector2(0f, offset),
            new Vector2(0f, -offset),
            new Vector2(diagonal, diagonal),
            new Vector2(-diagonal, diagonal),
            new Vector2(diagonal, -diagonal),
            new Vector2(-diagonal, -diagonal)
        };

        float bottom = feetHeightMeters + MathF.Max(0.12f, radiusMeters * 0.35f);
        float top = feetHeightMeters + heightMeters - MathF.Max(0.12f, radiusMeters * 0.35f);
        for (float height = bottom; height <= top; height += 0.1f)
        {
            float sampleY = height / VoxelSizeMeters - 0.5f;
            for (int i = 0; i < ring.Length; i++)
            {
                float density = SampleDensity(
                    sampleX + ring[i].X / VoxelSizeMeters,
                    sampleY,
                    sampleZ + ring[i].Y / VoxelSizeMeters);
                if (density > 0.02f)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes only materialized 16^3 edit chunks. The compact procedural base
    /// remains supplied by the owning WorldMap and is not duplicated in saves.
    /// </summary>
    public void SaveEdits(Stream destination) => SaveEdits(destination, 0UL);

    public void SaveEdits(Stream destination, ulong worldSignature)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));

        using GZipStream compressed = new(destination, CompressionLevel.Optimal, leaveOpen: true);
        using BinaryWriter writer = new(compressed, Encoding.UTF8, leaveOpen: true);
        writer.Write(SaveMagic);
        writer.Write(SaveFormatVersion);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(Depth);
        writer.Write(worldSignature);

        List<ChunkCoordinate> keys = new();
        _octreeIndex.CollectLeaves(
            0, 0, 0,
            (Width - 1) / DataCellSize,
            (Height - 1) / DataCellSize,
            (Depth - 1) / DataCellSize,
            keys);
        keys.Sort(static (a, b) =>
        {
            int order = a.Z.CompareTo(b.Z);
            if (order != 0) return order;
            order = a.Y.CompareTo(b.Y);
            return order != 0 ? order : a.X.CompareTo(b.X);
        });

        writer.Write(keys.Count);
        foreach (ChunkCoordinate key in keys)
        {
            if (!_octreeIndex.TryGetLeaf(key, out Chunk? chunk) || chunk == null)
                continue;

            writer.Write(key.X);
            writer.Write(key.Y);
            writer.Write(key.Z);
            writer.Write(chunk.Revision);
            chunk.Write(writer);
        }

        writer.Flush();
    }

    public void LoadEdits(Stream source) => LoadEdits(source, 0UL);

    public void LoadEdits(Stream source, ulong expectedWorldSignature)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The source stream is not readable.", nameof(source));

        Dictionary<ChunkCoordinate, Chunk> loaded = new();
        HashSet<ChunkCoordinate> serializedKeys = new();
        HashSet<(int X, int Z)> editedColumns = new();
        long maximumRevision = 0;

        using (GZipStream decompressed = new(source, CompressionMode.Decompress, leaveOpen: true))
        using (BinaryReader reader = new(decompressed, Encoding.UTF8, leaveOpen: true))
        {
            if (reader.ReadInt32() != SaveMagic)
                throw new InvalidDataException("The voxel save has an invalid signature.");

            int formatVersion = reader.ReadInt32();
            if (formatVersion < 2 || formatVersion > SaveFormatVersion)
                throw new InvalidDataException("The voxel save version is not supported.");

            if (reader.ReadInt32() != Width ||
                reader.ReadInt32() != Height ||
                reader.ReadInt32() != Depth)
                throw new InvalidDataException("The voxel save belongs to a different world size.");

            if (reader.ReadUInt64() != expectedWorldSignature)
                throw new InvalidDataException("The voxel save belongs to a different base world.");

            bool currentFormat = formatVersion == SaveFormatVersion;
            int serializedCellSize = currentFormat ? DataCellSize : LegacyChunkSize;
            int chunkCount = reader.ReadInt32();
            long capacity = ((Width + serializedCellSize - 1L) / serializedCellSize) *
                ((Height + serializedCellSize - 1L) / serializedCellSize) *
                ((Depth + serializedCellSize - 1L) / serializedCellSize);
            if (chunkCount < 0 || chunkCount > capacity)
                throw new InvalidDataException("The voxel save contains an invalid data-cell count.");

            for (int pageIndex = 0; pageIndex < chunkCount; pageIndex++)
            {
                int pageX = reader.ReadInt32();
                int pageY = reader.ReadInt32();
                int pageZ = reader.ReadInt32();
                long revision = reader.ReadInt64();

                if (pageX < 0 || pageY < 0 || pageZ < 0 ||
                    pageX >= (Width + serializedCellSize - 1) / serializedCellSize ||
                    pageY >= (Height + serializedCellSize - 1) / serializedCellSize ||
                    pageZ >= (Depth + serializedCellSize - 1) / serializedCellSize ||
                    revision < 0)
                {
                    throw new InvalidDataException("The voxel save contains an invalid data-cell coordinate.");
                }

                ChunkCoordinate serializedKey = new(pageX, pageY, pageZ);
                if (!serializedKeys.Add(serializedKey))
                    throw new InvalidDataException("The voxel save contains a duplicate data cell.");

                if (currentFormat)
                {
                    Chunk page = new() { Revision = revision };
                    page.Read(reader);
                    if (PageMatchesBase(page, serializedKey))
                        continue;

                    loaded.Add(serializedKey, page);
                    editedColumns.Add((pageX, pageZ));
                    maximumRevision = Math.Max(maximumRevision, revision);
                    continue;
                }

                // V2-V4 saves used dense 16^3 pages. Expand them into eight
                // 8^3 VRage-style data cells without changing voxel coordinates.
                byte[] legacyContent = new byte[LegacySampleCount];
                byte[] legacyMaterial = new byte[LegacySampleCount];

                if (formatVersion == 4)
                {
                    byte[] content = reader.ReadBytes(LegacySampleCount);
                    byte[] material = reader.ReadBytes(LegacySampleCount);
                    if (content.Length != LegacySampleCount ||
                        material.Length != LegacySampleCount)
                    {
                        throw new EndOfStreamException();
                    }

                    content.CopyTo(legacyContent, 0);
                    material.CopyTo(legacyMaterial, 0);
                }
                else
                {
                    FillLegacyBasePage(
                        pageX, pageY, pageZ,
                        legacyContent,
                        legacyMaterial);

                    if (formatVersion == 2)
                    {
                        short[] oldDensity = new short[LegacySampleCount];
                        for (int i = 0; i < LegacySampleCount; i++)
                            oldDensity[i] = reader.ReadInt16();

                        byte[] oldDensityMask = reader.ReadBytes(LegacySampleCount);
                        if (oldDensityMask.Length != LegacySampleCount)
                            throw new EndOfStreamException();

                        for (int i = 0; i < LegacySampleCount; i++)
                        {
                            if (oldDensityMask[i] > 1)
                                throw new InvalidDataException("The voxel save contains an invalid density mask.");
                            if (oldDensityMask[i] != 0)
                                legacyContent[i] = EncodeContent(oldDensity[i] / DensityScale);
                        }

                        ushort[] oldMaterials = new ushort[LegacySampleCount];
                        for (int i = 0; i < LegacySampleCount; i++)
                            oldMaterials[i] = reader.ReadUInt16();

                        byte[] oldMaterialMask = reader.ReadBytes(LegacySampleCount);
                        if (oldMaterialMask.Length != LegacySampleCount)
                            throw new EndOfStreamException();

                        for (int i = 0; i < LegacySampleCount; i++)
                        {
                            if (oldMaterialMask[i] > 1)
                                throw new InvalidDataException("The voxel save contains an invalid material mask.");
                            if (oldMaterialMask[i] == 0)
                                continue;
                            if (oldMaterials[i] > byte.MaxValue)
                                throw new InvalidDataException("The voxel save contains an unsupported material ID.");

                            legacyMaterial[i] = oldMaterials[i] == 0
                                ? byte.MaxValue
                                : (byte)oldMaterials[i];
                        }
                    }
                    else
                    {
                        byte[] oldContent = reader.ReadBytes(LegacySampleCount);
                        if (oldContent.Length != LegacySampleCount)
                            throw new EndOfStreamException();

                        long[] oldContentMask = new long[LegacyContentMaskWordCount];
                        for (int i = 0; i < LegacyContentMaskWordCount; i++)
                            oldContentMask[i] = reader.ReadInt64();

                        for (int i = 0; i < LegacySampleCount; i++)
                        {
                            if ((oldContentMask[i >> 6] & (1L << (i & 63))) != 0)
                                legacyContent[i] = oldContent[i];
                        }

                        if (reader.ReadBoolean())
                        {
                            byte[] oldMaterial = reader.ReadBytes(LegacySampleCount);
                            if (oldMaterial.Length != LegacySampleCount)
                                throw new EndOfStreamException();

                            for (int i = 0; i < LegacySampleCount; i++)
                            {
                                if (oldMaterial[i] != byte.MaxValue)
                                    legacyMaterial[i] = oldMaterial[i] == 0
                                        ? byte.MaxValue
                                        : oldMaterial[i];
                            }
                        }
                    }
                }

                ExpandLegacyPage(
                    pageX, pageY, pageZ, revision,
                    legacyContent, legacyMaterial,
                    loaded, editedColumns);
                maximumRevision = Math.Max(maximumRevision, revision);
            }
        }

        _undoHistory.Clear();
        _redoHistory.Clear();
        _undoHistoryBytes = 0;
        _historyCapture = null;
        _octreeIndex.Clear();

        foreach ((ChunkCoordinate key, Chunk chunk) in loaded)
            _octreeIndex.SetLeaf(key, chunk);

        _editedChunkColumns.Clear();
        foreach ((int x, int z) in editedColumns)
            _editedChunkColumns.Add((x, z));

        Version = Math.Max(Version, maximumRevision) + 1;
        Changed?.Invoke(new VoxelBounds(0, 0, 0, Width, Height, Depth));
    }

    private void FillLegacyBasePage(
        int pageX,
        int pageY,
        int pageZ,
        byte[] content,
        byte[] material)
    {
        int originX = pageX * LegacyChunkSize;
        int originY = pageY * LegacyChunkSize;
        int originZ = pageZ * LegacyChunkSize;

        for (int z = 0; z < LegacyChunkSize; z++)
        for (int y = 0; y < LegacyChunkSize; y++)
        for (int x = 0; x < LegacyChunkSize; x++)
        {
            int worldX = originX + x;
            int worldY = originY + y;
            int worldZ = originZ + z;
            int index = x + y * LegacyChunkSize + z * LegacyCellPlane;

            float density = worldX < Width && worldY <= Height && worldZ < Depth
                ? _baseDensity(worldX, worldY, worldZ)
                : -1f;

            content[index] = EncodeContent(density);
            material[index] =
                density > 0f && worldX < Width && worldY < Height && worldZ < Depth
                    ? EncodeMaterial(_baseMaterial(worldX, worldY, worldZ))
                    : byte.MaxValue;
        }
    }

    private void ExpandLegacyPage(
        int pageX,
        int pageY,
        int pageZ,
        long revision,
        byte[] legacyContent,
        byte[] legacyMaterial,
        Dictionary<ChunkCoordinate, Chunk> loaded,
        HashSet<(int X, int Z)> editedColumns)
    {
        for (int partZ = 0; partZ < LegacyChunkSize / DataCellSize; partZ++)
        for (int partY = 0; partY < LegacyChunkSize / DataCellSize; partY++)
        for (int partX = 0; partX < LegacyChunkSize / DataCellSize; partX++)
        {
            int newX = pageX * (LegacyChunkSize / DataCellSize) + partX;
            int newY = pageY * (LegacyChunkSize / DataCellSize) + partY;
            int newZ = pageZ * (LegacyChunkSize / DataCellSize) + partZ;
            if (newX * DataCellSize >= Width ||
                newY * DataCellSize >= Height ||
                newZ * DataCellSize >= Depth)
                continue;

            Chunk page = new() { Revision = revision };
            byte[] content = new byte[SampleCount];
            byte[] material = new byte[CellCount];

            for (int z = 0; z < DataCellSize; z++)
            for (int y = 0; y < DataCellSize; y++)
            for (int x = 0; x < DataCellSize; x++)
            {
                int oldX = partX * DataCellSize + x;
                int oldY = partY * DataCellSize + y;
                int oldZ = partZ * DataCellSize + z;
                int sourceIndex = oldX + oldY * LegacyChunkSize + oldZ * LegacyCellPlane;
                int targetIndex = x + y * DataCellSize + z * CellPlane;
                content[targetIndex] = legacyContent[sourceIndex];
                material[targetIndex] = legacyMaterial[sourceIndex];
            }

            page.SetContentData(content);
            page.SetMaterialData(material);
            page.Compress();

            ChunkCoordinate key = new(newX, newY, newZ);
            if (PageMatchesBase(page, key))
                continue;

            loaded.Add(key, page);
            editedColumns.Add((newX, newZ));
        }
    }

    public VoxelMaterialDefinition GetMaterialDefinition(int x, int y, int z) =>
        VoxelMaterialCatalog.Get(GetMaterialId(x, y, z));

    public float GetCellMassKg(int x, int y, int z) =>
        GetMaterialDefinition(x, y, z).MassPerVoxelKg;

    public long GetChunkRevision(int chunkX, int chunkY, int chunkZ)
    {
        return _octreeIndex.TryGetLeaf(
            new ChunkCoordinate(chunkX, chunkY, chunkZ),
            out Chunk? chunk)
                ? chunk.Revision
                : 0L;
    }

    public bool IsChunkKnown(int chunkX, int chunkY, int chunkZ) =>
        _octreeIndex.ContainsLeaf(new ChunkCoordinate(chunkX, chunkY, chunkZ));

    /// <summary>Enumerates materialized edit leaves intersecting a chunk-space AABB.</summary>
    public void CollectEditedChunks(
        int minChunkX,
        int minChunkY,
        int minChunkZ,
        int maxChunkX,
        int maxChunkY,
        int maxChunkZ,
        List<ChunkCoordinate> destination) =>
        _octreeIndex.CollectLeaves(
            Math.Max(0, minChunkX),
            Math.Max(0, minChunkY),
            Math.Max(0, minChunkZ),
            Math.Min((Width - 1) / DataCellSize, maxChunkX),
            Math.Min((Height - 1) / DataCellSize, maxChunkY),
            Math.Min((Depth - 1) / DataCellSize, maxChunkZ),
            destination);

    public bool HasEditsInArea(int minX, int maxX, int minZ, int maxZ)
    {
        if (maxX < 0 || maxZ < 0 || minX >= Width || minZ >= Depth)
            return false;

        int firstChunkX = Math.Max(0, minX) / DataCellSize;
        int lastChunkX = Math.Min(Width - 1, maxX) / DataCellSize;
        int firstChunkZ = Math.Max(0, minZ) / DataCellSize;
        int lastChunkZ = Math.Min(Depth - 1, maxZ) / DataCellSize;
        for (int chunkZ = firstChunkZ; chunkZ <= lastChunkZ; chunkZ++)
        for (int chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
        {
            if (_editedChunkColumns.Contains((chunkX, chunkZ)))
                return true;
        }

        return false;
    }

    public bool HasEditsInColumn(int sampleX, int sampleZ)
    {
        if (sampleX < 0 || sampleX >= Width ||
            sampleZ < 0 || sampleZ >= Depth)
            return false;

        return _editedChunkColumns.Contains((
            sampleX / DataCellSize,
            sampleZ / DataCellSize));
    }

    /// <summary>
    /// Marks a procedural base-surface change without allocating voxel chunks.
    /// The owner is responsible for invalidating its navigation/surface caches.
    /// </summary>
    public void InvalidateBaseField()
    {
        Version++;
        BaseVersion++;
    }

    /// <summary>
    /// Raycasts the same trilinear density field used by the surface mesher.
    /// The ray is expressed in metres; hit and normal are returned in world space.
    /// </summary>
    public bool TryRaycast(
        Vector3 originMeters,
        Vector3 direction,
        float maximumDistanceMeters,
        out Vector3 hitPosition,
        out Vector3 hitNormal)
    {
        hitPosition = default;
        hitNormal = Vector3.UnitY;

        if (!IsFinite(originMeters) || !IsFinite(direction) ||
            direction.LengthSquared() < 0.000001f ||
            !float.IsFinite(maximumDistanceMeters) || maximumDistanceMeters <= 0f)
            return false;

        direction = Vector3.Normalize(direction);
        float distance = 0f;
        float previousDistance = 0f;
        Vector3 samplePosition = originMeters / VoxelSizeMeters - new Vector3(0.5f);
        float previousDensity = SampleDensity(
            samplePosition.X, samplePosition.Y, samplePosition.Z);
        bool previousWasAir = previousDensity < 0f;

        for (int iteration = 0; iteration < 20_000 && distance < maximumDistanceMeters; iteration++)
        {
            float stepMeters = Math.Clamp(
                MathF.Abs(previousDensity) * VoxelSizeMeters * 0.45f,
                0.025f,
                1f);
            previousDistance = distance;
            distance = MathF.Min(maximumDistanceMeters, distance + stepMeters);

            Vector3 position = originMeters + direction * distance;
            Vector3 voxelPosition = position / VoxelSizeMeters - new Vector3(0.5f);
            float density = SampleDensity(
                voxelPosition.X, voxelPosition.Y, voxelPosition.Z);

            if (previousWasAir && density >= 0f)
            {
                float low = previousDistance;
                float high = distance;
                for (int refine = 0; refine < 12; refine++)
                {
                    float middle = (low + high) * 0.5f;
                    Vector3 middlePosition = originMeters + direction * middle;
                    Vector3 middleVoxel = middlePosition / VoxelSizeMeters -
                        new Vector3(0.5f);
                    float middleDensity = SampleDensity(
                        middleVoxel.X, middleVoxel.Y, middleVoxel.Z);
                    if (middleDensity >= 0f)
                        high = middle;
                    else
                        low = middle;
                }

                hitPosition = originMeters + direction * ((low + high) * 0.5f);
                Vector3 hitVoxel = hitPosition / VoxelSizeMeters - new Vector3(0.5f);
                const float normalStep = 0.5f;
                Vector3 gradient = new(
                    SampleDensity(hitVoxel.X + normalStep, hitVoxel.Y, hitVoxel.Z) -
                    SampleDensity(hitVoxel.X - normalStep, hitVoxel.Y, hitVoxel.Z),
                    SampleDensity(hitVoxel.X, hitVoxel.Y + normalStep, hitVoxel.Z) -
                    SampleDensity(hitVoxel.X, hitVoxel.Y - normalStep, hitVoxel.Z),
                    SampleDensity(hitVoxel.X, hitVoxel.Y, hitVoxel.Z + normalStep) -
                    SampleDensity(hitVoxel.X, hitVoxel.Y, hitVoxel.Z - normalStep));

                hitNormal = gradient.LengthSquared() > 0.000001f
                    ? Vector3.Normalize(-gradient)
                    : Vector3.UnitY;
                return true;
            }

            previousDensity = density;
            previousWasAir = density < 0f;
        }

        return false;
    }

    /// <summary>
    /// Removes voxel overrides inside one coarse 1 m map column and restores
    /// samples from the procedural/legacy base callbacks. Used when a legacy
    /// height column is intentionally replaced with a new simple fill.
    /// </summary>
    public void ResetBaseColumn(int tileX, int tileZ)
    {
        if (tileX < 0 || tileX * 10 >= Width ||
            tileZ < 0 || tileZ * 10 >= Depth)
            throw new ArgumentOutOfRangeException(nameof(tileX));

        int minX = tileX * 10;
        int maxX = Math.Min(Width, minX + 10);
        int minZ = tileZ * 10;
        int maxZ = Math.Min(Depth, minZ + 10);

        if (!HasEditsInArea(minX, maxX - 1, minZ, maxZ - 1))
            return;

        int firstChunkX = minX / DataCellSize;
        int lastChunkX = (maxX - 1) / DataCellSize;
        int firstChunkZ = minZ / DataCellSize;
        int lastChunkZ = (maxZ - 1) / DataCellSize;
        int chunkCountY = (Height + DataCellSize - 1) / DataCellSize;

        List<ChunkCoordinate>? removeChunks = null;
        long revision = Version + 1;
        bool anyChanged = false;

        for (int chunkZ = firstChunkZ; chunkZ <= lastChunkZ; chunkZ++)
        for (int chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
        for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
        {
            ChunkCoordinate key = new(chunkX, chunkY, chunkZ);
            if (!_octreeIndex.TryGetLeaf(key, out Chunk? chunk))
                continue;

            CaptureChunkBeforeWrite(key);
            int localMinX = Math.Max(0, minX - chunkX * DataCellSize);
            int localMaxX = Math.Min(DataCellSize, maxX - chunkX * DataCellSize);
            int localMinZ = Math.Max(0, minZ - chunkZ * DataCellSize);
            int localMaxZ = Math.Min(DataCellSize, maxZ - chunkZ * DataCellSize);
            int chunkYStart = chunkY * DataCellSize;
            int localMaxY = Math.Min(DataCellSize, Height - chunkYStart);
            bool changed = false;

            for (int localZ = localMinZ; localZ < localMaxZ; localZ++)
            for (int localX = localMinX; localX < localMaxX; localX++)
            {
                int worldX = chunkX * DataCellSize + localX;
                int worldZ = chunkZ * DataCellSize + localZ;

                for (int localY = 0; localY < localMaxY; localY++)
                {
                    int worldY = chunkYStart + localY;
                    int index = localX + localY * DataCellSize + localZ * CellPlane;

                    byte baseContent = EncodeContent(_baseDensity(worldX, worldY, worldZ));
                    byte baseMaterial = baseContent > ContentIsoLevel
                        ? EncodeMaterial(_baseMaterial(worldX, worldY, worldZ))
                        : byte.MaxValue;

                    if (chunk.GetContent(index) == baseContent &&
                        chunk.GetMaterial(index) == baseMaterial)
                        continue;

                    chunk.SetContent(index, baseContent);
                    chunk.SetMaterial(index, baseMaterial);
                    changed = true;
                }
            }

            if (changed)
            {
                anyChanged = true;
                chunk.Revision = revision;
            }

            chunk.Compress();
            if (PageMatchesBase(chunk, key))
                (removeChunks ??= new List<ChunkCoordinate>()).Add(key);
            else
                _octreeIndex.UpdateLeafConstitution(key, chunk);

        }

        if (removeChunks != null)
        {
            foreach (ChunkCoordinate key in removeChunks)
            {
                _octreeIndex.TryRemoveLeaf(key, out _);
            }
        }

        for (int chunkZ = firstChunkZ; chunkZ <= lastChunkZ; chunkZ++)
        for (int chunkX = firstChunkX; chunkX <= lastChunkX; chunkX++)
        {
            bool stillEdited = false;
            for (int chunkY = 0; chunkY < chunkCountY; chunkY++)
            {
                if (_octreeIndex.ContainsLeaf(new ChunkCoordinate(chunkX, chunkY, chunkZ)))
                {
                    stillEdited = true;
                    break;
                }
            }

            if (!stillEdited)
                _editedChunkColumns.Remove((chunkX, chunkZ));
        }

        if (!anyChanged)
            return;

        Version = revision;
        IncludeHistoryBounds(new VoxelBounds(
            minX, 0, minZ,
            maxX, Height, maxZ));
        Changed?.Invoke(new VoxelBounds(
            minX, 0, minZ,
            maxX, Height, maxZ));
    }

    public void ClearEdits()
    {
        if (_octreeIndex.LeafCount == 0)
            return;

        List<ChunkCoordinate> keys = new();
        _octreeIndex.CollectLeaves(
            0, 0, 0,
            (Width - 1) / DataCellSize,
            (Height - 1) / DataCellSize,
            (Depth - 1) / DataCellSize,
            keys);
        foreach (ChunkCoordinate key in keys)
            CaptureChunkBeforeWrite(key);

        _octreeIndex.Clear();
        _editedChunkColumns.Clear();
        Version++;
        VoxelBounds bounds = new(0, 0, 0, Width, Height, Depth);
        IncludeHistoryBounds(bounds);
        Changed?.Invoke(bounds);
    }

    /// <summary>
    /// Starts one undoable user operation. All voxel mutations until the
    /// returned scope is disposed are grouped into a single undo step.
    /// </summary>
    public IDisposable BeginUndoableEdit()
    {
        if (_historyCapture != null)
            throw new InvalidOperationException("Nested voxel edit history scopes are not supported.");

        _historyCapture = new EditHistoryCapture();
        return new UndoScope(this);
    }

    public bool Undo()
    {
        if (_historyCapture != null)
            throw new InvalidOperationException("Finish the active voxel edit before undoing.");

        if (_undoHistory.Count == 0)
            return false;

        int last = _undoHistory.Count - 1;
        EditHistoryEntry entry = _undoHistory[last];
        _undoHistory.RemoveAt(last);
        _undoHistoryBytes -= entry.EstimatedBytes;
        RestoreHistory(entry.Before, entry.Bounds);
        _redoHistory.Add(entry);
        return true;
    }

    public bool Redo()
    {
        if (_historyCapture != null)
            throw new InvalidOperationException("Finish the active voxel edit before redoing.");

        if (_redoHistory.Count == 0)
            return false;

        int last = _redoHistory.Count - 1;
        EditHistoryEntry entry = _redoHistory[last];
        _redoHistory.RemoveAt(last);
        RestoreHistory(entry.After, entry.Bounds);
        _undoHistory.Add(entry);
        _undoHistoryBytes += entry.EstimatedBytes;
        TrimUndoHistory();
        return true;
    }

    public void CarveSphere(Vector3 centerMeters, float radiusMeters) =>
        EditSphere(centerMeters, radiusMeters, add: false, materialId: 0);

    public void AddSphere(Vector3 centerMeters, float radiusMeters, ushort materialId) =>
        EditSphere(centerMeters, radiusMeters, add: true, materialId);

    public void CarveCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters) =>
        EditCapsule(startMeters, endMeters, radiusMeters, add: false, materialId: 0);

    public void AddCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters,
        ushort materialId) =>
        EditCapsule(startMeters, endMeters, radiusMeters, add: true, materialId);

    public void FillBox(
        Vector3 minimumMeters,
        Vector3 maximumMeters,
        ushort materialId) =>
        EditBox(minimumMeters, maximumMeters, add: true, materialId);

    public void CarveBox(Vector3 minimumMeters, Vector3 maximumMeters) =>
        EditBox(minimumMeters, maximumMeters, add: false, materialId: 0);

    public void PaintSphere(Vector3 centerMeters, float radiusMeters, ushort materialId)
    {
        ValidateBrush(radiusMeters, materialId, requiresMaterial: true);
        Vector3 center = ToVoxelPosition(centerMeters);
        float radius = radiusMeters / VoxelSizeMeters;
        GetCellBounds(center, radius, out VoxelBounds bounds);
        ValidateEditWork(bounds);

        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();
        float radiusSquared = radius * radius;

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            float dx = x + 0.5f - center.X;
            float dy = y + 0.5f - center.Y;
            float dz = z + 0.5f - center.Z;
            if (dx * dx + dy * dy + dz * dz > radiusSquared ||
                GetDensity(x, y, z) <= 0f)
                continue;

            SetMaterialOverride(x, y, z, materialId, revision, touched);
        }

        CompleteEdit(bounds, revision, touched);
    }

    /// <summary>
    /// Smooths a local part of the signed-distance field without affecting
    /// unrelated chunks. Strength is the interpolation factor toward a six-neighbour average.
    /// </summary>
    /// <summary>
    /// Blends a local part of the density field toward a plane. The plane
    /// normal points toward air; points below it become solid and points
    /// above it become air. A smooth radial falloff avoids a hard brush rim.
    /// </summary>

    /// <summary>
    /// Adds or carves a ramp-shaped triangular prism. The horizontal direction
    /// is taken from the two drag endpoints; vertical change uses the endpoint
    /// height when present, otherwise the brush radius supplies the ramp rise.
    /// </summary>
    public void AddRamp(
        Vector3 startMeters,
        Vector3 endMeters,
        float widthMeters,
        float thicknessMeters,
        ushort materialId) =>
        EditRamp(startMeters, endMeters, widthMeters, thicknessMeters, true, materialId);

    public void CarveRamp(
        Vector3 startMeters,
        Vector3 endMeters,
        float widthMeters,
        float thicknessMeters) =>
        EditRamp(startMeters, endMeters, widthMeters, thicknessMeters, false, 0);

    private void EditRamp(
        Vector3 startMeters,
        Vector3 endMeters,
        float widthMeters,
        float thicknessMeters,
        bool add,
        ushort materialId)
    {
        if (!IsFinite(startMeters) || !IsFinite(endMeters))
            throw new ArgumentOutOfRangeException(nameof(endMeters));
        if (!float.IsFinite(widthMeters) || widthMeters <= 0f || widthMeters > 8f)
            throw new ArgumentOutOfRangeException(nameof(widthMeters));
        if (!float.IsFinite(thicknessMeters) ||
            thicknessMeters <= 0f || thicknessMeters > 8f)
            throw new ArgumentOutOfRangeException(nameof(thicknessMeters));
        if (add && materialId == 0)
            throw new ArgumentOutOfRangeException(nameof(materialId));

        Vector2 horizontal = new(
            endMeters.X - startMeters.X,
            endMeters.Z - startMeters.Z);
        float length = horizontal.Length();
        if (length < 0.2f)
            throw new ArgumentOutOfRangeException(nameof(endMeters));

        Vector2 direction = horizontal / length;
        Vector2 side = new(-direction.Y, direction.X);
        float halfWidth = widthMeters * 0.5f;
        float verticalDelta = endMeters.Y - startMeters.Y;
        float topEnd = MathF.Abs(verticalDelta) >= 0.1f
            ? endMeters.Y
            : startMeters.Y + MathF.Max(0.2f, thicknessMeters);

        float baseY = MathF.Min(startMeters.Y, topEnd) - thicknessMeters;
        float maximumTop = MathF.Max(startMeters.Y, topEnd);
        float slope = (topEnd - startMeters.Y) / length;
        float slopeScale = MathF.Sqrt(1f + slope * slope);

        float minWorldX = MathF.Min(startMeters.X, endMeters.X) -
            MathF.Abs(side.X) * halfWidth - VoxelSizeMeters;
        float maxWorldX = MathF.Max(startMeters.X, endMeters.X) +
            MathF.Abs(side.X) * halfWidth + VoxelSizeMeters;
        float minWorldZ = MathF.Min(startMeters.Z, endMeters.Z) -
            MathF.Abs(side.Y) * halfWidth - VoxelSizeMeters;
        float maxWorldZ = MathF.Max(startMeters.Z, endMeters.Z) +
            MathF.Abs(side.Y) * halfWidth + VoxelSizeMeters;
        float minWorldY = baseY - VoxelSizeMeters;
        float maxWorldY = maximumTop + VoxelSizeMeters;

        int minX = Math.Clamp(
            (int)MathF.Floor(minWorldX / VoxelSizeMeters - 0.5f), 0, Width);
        int maxX = Math.Clamp(
            (int)MathF.Ceiling(maxWorldX / VoxelSizeMeters - 0.5f), 0, Width);
        int minY = Math.Clamp(
            (int)MathF.Floor(minWorldY / VoxelSizeMeters - 0.5f), 0, Height);
        int maxY = Math.Clamp(
            (int)MathF.Ceiling(maxWorldY / VoxelSizeMeters - 0.5f), 0, Height);
        int minZ = Math.Clamp(
            (int)MathF.Floor(minWorldZ / VoxelSizeMeters - 0.5f), 0, Depth);
        int maxZ = Math.Clamp(
            (int)MathF.Ceiling(maxWorldZ / VoxelSizeMeters - 0.5f), 0, Depth);
        VoxelBounds bounds = new(minX, minY, minZ, maxX, maxY, maxZ);
        if (minX >= maxX || minY >= maxY || minZ >= maxZ)
            return;

        ValidateEditWork(bounds);
        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();

        for (int z = minZ; z < maxZ; z++)
        for (int y = minY; y < maxY; y++)
        for (int x = minX; x < maxX; x++)
        {
            float pointX = (x + 0.5f) * VoxelSizeMeters;
            float pointY = (y + 0.5f) * VoxelSizeMeters;
            float pointZ = (z + 0.5f) * VoxelSizeMeters;
            float relativeX = pointX - startMeters.X;
            float relativeZ = pointZ - startMeters.Z;
            float along = relativeX * direction.X + relativeZ * direction.Y;
            float across = MathF.Abs(
                relativeX * side.X + relativeZ * side.Y) - halfWidth;

            float signedDistance = MathF.Max(
                MathF.Max(-along, along - length),
                MathF.Max(
                    across,
                    MathF.Max(
                        baseY - pointY,
                        (pointY - (startMeters.Y + slope * along)) / slopeScale)));

            float oldDensity = GetDensity(x, y, z);
            float nextDensity = add
                ? MathF.Max(oldDensity, -signedDistance)
                : MathF.Min(oldDensity, signedDistance);
            if (Quantize(nextDensity) == Quantize(oldDensity))
                continue;

            SetDensityOverride(
                x, y, z, nextDensity, oldDensity, revision, touched);

            if (add && nextDensity > 0f)
                SetMaterialOverride(x, y, z, materialId, revision, touched);
            else if (!add && nextDensity <= 0f)
                SetMaterialOverride(x, y, z, 0, revision, touched);
        }

        CompleteEdit(bounds, revision, touched);
    }

    public void FlattenPlane(
        Vector3 centerMeters,
        Vector3 normal,
        float radiusMeters,
        ushort materialId,
        float strength = 1f)
    {
        if (!IsFinite(centerMeters) || !IsFinite(normal) ||
            normal.LengthSquared() < 0.000001f)
            throw new ArgumentOutOfRangeException(nameof(normal));
        if (!float.IsFinite(radiusMeters) || radiusMeters <= 0f || radiusMeters > 4f)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        if (!float.IsFinite(strength) || strength <= 0f || strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(strength));
        if (materialId == 0)
            throw new ArgumentOutOfRangeException(nameof(materialId));

        normal = Vector3.Normalize(normal);
        Vector3 center = ToVoxelPosition(centerMeters);
        float radius = radiusMeters / VoxelSizeMeters;
        GetCellBounds(center, radius + 1f, out VoxelBounds bounds);
        ValidateEditWork(bounds);

        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();
        float innerRadius = radius * 0.72f;
        float outerRange = MathF.Max(1f, radius - innerRadius);

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            Vector3 point = new(x + 0.5f, y + 0.5f, z + 0.5f);
            Vector3 relative = point - center;
            float planeDistance = Vector3.Dot(relative, normal);
            float tangentSquared = MathF.Max(
                0f, relative.LengthSquared() - planeDistance * planeDistance);
            float tangentDistance = MathF.Sqrt(tangentSquared);
            if (tangentDistance >= radius)
                continue;

            float edge = Math.Clamp(
                (radius - tangentDistance) / outerRange,
                0f,
                1f);
            edge = edge * edge * (3f - 2f * edge);
            float blend = strength * edge;
            float oldDensity = GetDensity(x, y, z);
            float targetDensity = -planeDistance;
            float nextDensity = Lerp(oldDensity, targetDensity, blend);
            if (Quantize(nextDensity) == Quantize(oldDensity))
                continue;

            SetDensityOverride(
                x, y, z, nextDensity, oldDensity, revision, touched);

            if (oldDensity > 0f && nextDensity <= 0f)
            {
                SetMaterialOverride(x, y, z, 0, revision, touched);
            }
            else if (oldDensity <= 0f && nextDensity > 0f)
            {
                ushort resolvedMaterial = _baseMaterial(x, y, z);
                SetMaterialOverride(
                    x, y, z,
                    resolvedMaterial == 0 ? materialId : resolvedMaterial,
                    revision, touched);
            }
        }

        CompleteEdit(bounds, revision, touched);
    }

    public void SmoothSphere(Vector3 centerMeters, float radiusMeters, float strength = 0.5f)
    {
        if (!float.IsFinite(strength) || strength <= 0f || strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(strength));
        if (!float.IsFinite(radiusMeters) || radiusMeters <= 0f || radiusMeters > 4f)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));

        Vector3 center = ToVoxelPosition(centerMeters);
        float radius = radiusMeters / VoxelSizeMeters;
        GetCellBounds(center, radius + 1f, out VoxelBounds bounds);
        ValidateEditWork(bounds);
        int sizeX = bounds.MaxX - bounds.MinX;
        int sizeY = bounds.MaxY - bounds.MinY;
        int sizeZ = bounds.MaxZ - bounds.MinZ;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
            return;

        float[] smoothed = new float[checked(sizeX * sizeY * sizeZ)];
        float radiusSquared = radius * radius;
        int index = 0;

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            float dx = x + 0.5f - center.X;
            float dy = y + 0.5f - center.Y;
            float dz = z + 0.5f - center.Z;
            float current = GetDensity(x, y, z);

            if (dx * dx + dy * dy + dz * dz > radiusSquared)
            {
                smoothed[index++] = current;
                continue;
            }

            float neighbourAverage = (
                GetDensity(x - 1, y, z) + GetDensity(x + 1, y, z) +
                GetDensity(x, y - 1, z) + GetDensity(x, y + 1, z) +
                GetDensity(x, y, z - 1) + GetDensity(x, y, z + 1)) / 6f;
            smoothed[index++] = current + (neighbourAverage - current) * strength;
        }

        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();
        index = 0;
        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            float previous = GetDensity(x, y, z);
            float next = smoothed[index++];
            if (MathF.Abs(next - previous) <= 0.001f)
                continue;

            SetDensityOverride(x, y, z, next, previous, revision, touched);

            if (previous > 0f && next <= 0f)
                SetMaterialOverride(x, y, z, 0, revision, touched);
            else if (previous <= 0f && next > 0f)
            {
                ushort materialId = _baseMaterial(x, y, z);
                if (materialId == 0)
                    materialId = 1;
                SetMaterialOverride(x, y, z, materialId, revision, touched);
            }
        }

        CompleteEdit(bounds, revision, touched);
    }

    /// <summary>Writes one native 0.1 m cubic cell using an exact box CSG operation.</summary>
    public void SetCell(int x, int y, int z, ushort materialId)
    {
        if (!IsInsideCell(x, y, z))
            throw new ArgumentOutOfRangeException(nameof(x));

        Vector3 minimum = new(
            x * VoxelSizeMeters,
            y * VoxelSizeMeters,
            z * VoxelSizeMeters);
        Vector3 maximum = minimum + new Vector3(VoxelSizeMeters);
        if (materialId == 0)
            CarveBox(minimum, maximum);
        else
            FillBox(minimum, maximum, materialId);
    }

    private void EditSphere(Vector3 centerMeters, float radiusMeters, bool add, ushort materialId)
    {
        ValidateBrush(radiusMeters, materialId, requiresMaterial: add);
        Vector3 center = ToVoxelPosition(centerMeters);
        float radius = radiusMeters / VoxelSizeMeters;
        GetCellBounds(center, radius + 1f, out VoxelBounds bounds);
        ValidateEditWork(bounds);

        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();
        int minX = Math.Max(0, (int)MathF.Floor(center.X - radius - 1f));
        int minY = Math.Max(0, (int)MathF.Floor(center.Y - radius - 1f));
        int minZ = Math.Max(0, (int)MathF.Floor(center.Z - radius - 1f));
        int maxX = Math.Min(Width, (int)MathF.Ceiling(center.X + radius + 1f));
        int maxY = Math.Min(Height, (int)MathF.Ceiling(center.Y + radius + 1f));
        int maxZ = Math.Min(Depth, (int)MathF.Ceiling(center.Z + radius + 1f));
        float radiusSquared = radius * radius;

        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float dx = x + 0.5f - center.X;
            float dy = y + 0.5f - center.Y;
            float dz = z + 0.5f - center.Z;
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float shapeDensity = radius - distance;
            float oldDensity = GetDensity(x, y, z);
            float nextDensity = add
                ? MathF.Max(oldDensity, shapeDensity)
                : MathF.Min(oldDensity, -shapeDensity);
            SetDensityOverride(x, y, z, nextDensity, oldDensity, revision, touched);
        }

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            float dx = x + 0.5f - center.X;
            float dy = y + 0.5f - center.Y;
            float dz = z + 0.5f - center.Z;
            bool inside = dx * dx + dy * dy + dz * dz <= radiusSquared;
            if (!inside)
                continue;

            float density = GetDensity(x, y, z);
            if (add && density > 0f)
                SetMaterialOverride(x, y, z, materialId, revision, touched);
            else if (!add && density <= 0f)
                SetMaterialOverride(x, y, z, 0, revision, touched);
        }

        CompleteEdit(bounds, revision, touched);
    }

    private void EditCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters,
        bool add,
        ushort materialId)
    {
        ValidateBrush(radiusMeters, materialId, requiresMaterial: add);
        Vector3 start = ToVoxelPosition(startMeters);
        Vector3 end = ToVoxelPosition(endMeters);
        float radius = radiusMeters / VoxelSizeMeters;
        GetCapsuleBounds(start, end, radius + 1f, out VoxelBounds bounds);
        ValidateEditWork(bounds);

        Vector3 segment = end - start;
        float segmentLengthSquared = segment.LengthSquared();
        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(start.X, end.X) - radius - 1f));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(start.Y, end.Y) - radius - 1f));
        int minZ = Math.Max(0, (int)MathF.Floor(MathF.Min(start.Z, end.Z) - radius - 1f));
        int maxX = Math.Min(Width, (int)MathF.Ceiling(MathF.Max(start.X, end.X) + radius + 1f));
        int maxY = Math.Min(Height, (int)MathF.Ceiling(MathF.Max(start.Y, end.Y) + radius + 1f));
        int maxZ = Math.Min(Depth, (int)MathF.Ceiling(MathF.Max(start.Z, end.Z) + radius + 1f));

        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            Vector3 point = new(x + 0.5f, y + 0.5f, z + 0.5f);
            float t = segmentLengthSquared > 0.0001f
                ? Math.Clamp(Vector3.Dot(point - start, segment) / segmentLengthSquared, 0f, 1f)
                : 0f;
            float distance = Vector3.Distance(point, start + segment * t);
            float shapeDensity = radius - distance;
            float oldDensity = GetDensity(x, y, z);
            float nextDensity = add
                ? MathF.Max(oldDensity, shapeDensity)
                : MathF.Min(oldDensity, -shapeDensity);
            SetDensityOverride(x, y, z, nextDensity, oldDensity, revision, touched);
        }

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            Vector3 point = new(x + 0.5f, y + 0.5f, z + 0.5f);
            float t = segmentLengthSquared > 0.0001f
                ? Math.Clamp(Vector3.Dot(point - start, segment) / segmentLengthSquared, 0f, 1f)
                : 0f;
            float distance = Vector3.Distance(point, start + segment * t);
            if (distance > radius)
                continue;

            float density = GetDensity(x, y, z);
            if (add && density > 0f)
                SetMaterialOverride(x, y, z, materialId, revision, touched);
            else if (!add && density <= 0f)
                SetMaterialOverride(x, y, z, 0, revision, touched);
        }

        CompleteEdit(bounds, revision, touched);
    }

    private void EditBox(Vector3 minimumMeters, Vector3 maximumMeters, bool add, ushort materialId)
    {
        if (materialId == 0 && add)
            throw new ArgumentOutOfRangeException(nameof(materialId));
        if (!IsFinite(minimumMeters) || !IsFinite(maximumMeters) ||
            maximumMeters.X <= minimumMeters.X ||
            maximumMeters.Y <= minimumMeters.Y ||
            maximumMeters.Z <= minimumMeters.Z)
            throw new ArgumentOutOfRangeException(nameof(maximumMeters));

        Vector3 min = ToVoxelPosition(minimumMeters);
        Vector3 max = ToVoxelPosition(maximumMeters);
        VoxelBounds bounds = ClampCellBounds(
            (int)MathF.Floor(min.X - 1f),
            (int)MathF.Floor(min.Y - 1f),
            (int)MathF.Floor(min.Z - 1f),
            (int)MathF.Ceiling(max.X + 1f),
            (int)MathF.Ceiling(max.Y + 1f),
            (int)MathF.Ceiling(max.Z + 1f));

        ValidateEditWork(bounds);

        Vector3 center = (min + max) * 0.5f;
        // Density samples sit at cell centres, so a one-cell box still has
        // positive samples and produces a closed isosurface.
        Vector3 half = (max - min) * 0.5f;
        int minX = bounds.MinX;
        int minY = bounds.MinY;
        int minZ = bounds.MinZ;
        int maxX = bounds.MaxX;
        int maxY = bounds.MaxY;
        int maxZ = bounds.MaxZ;
        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();

        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            Vector3 q = Vector3.Abs(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - center) - half;
            Vector3 outside = Vector3.Max(q, Vector3.Zero);
            float signedDistance = outside.Length() +
                MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
            float shapeDensity = -signedDistance;
            float oldDensity = GetDensity(x, y, z);
            float nextDensity = add
                ? MathF.Max(oldDensity, shapeDensity)
                : MathF.Min(oldDensity, -shapeDensity);
            SetDensityOverride(x, y, z, nextDensity, oldDensity, revision, touched);
        }

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            Vector3 point = new(x + 0.5f, y + 0.5f, z + 0.5f);
            Vector3 q = Vector3.Abs(point - center) - half;
            Vector3 outside = Vector3.Max(q, Vector3.Zero);
            float signedDistance = outside.Length() +
                MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
            if (signedDistance > 0f)
                continue;

            // Density samples are owned by voxel centres and indexed by x/y/z.
            // Sampling at point (x+0.5,y+0.5,z+0.5) would add a second half-cell
            // interpolation offset and can erase material exactly at box faces.
            float density = GetDensity(x, y, z);
            if (add && density > 0f)
                SetMaterialOverride(x, y, z, materialId, revision, touched);
            else if (!add && density <= 0f)
                SetMaterialOverride(x, y, z, 0, revision, touched);
        }

        CompleteEdit(bounds, revision, touched);
    }

    /// <summary>
    /// Detaches compact, fully enclosed solid components from the static field.
    /// Connectivity is tested on a conservative 0.2 m occupancy grid (a coarse
    /// cell is occupied when any of its eight source samples is solid). The
    /// exact 0.1 m density/material samples are copied only for accepted bodies.
    /// Components touching the scan boundary remain static because they may
    /// still be connected to terrain outside the local analysis window.
    /// </summary>
    public List<VoxelFragmentSnapshot> DetachLooseComponents(
        Vector3 centerMeters,
        Vector3 halfExtentsMeters,
        int maximumFragments = 1)
    {
        List<VoxelFragmentSnapshot> fragments = new();
        if (!IsFinite(centerMeters) || !IsFinite(halfExtentsMeters) ||
            halfExtentsMeters.X <= 0f ||
            halfExtentsMeters.Y <= 0f ||
            halfExtentsMeters.Z <= 0f ||
            halfExtentsMeters.X > 6f ||
            halfExtentsMeters.Y > 6f ||
            halfExtentsMeters.Z > 6f ||
            maximumFragments <= 0)
            return fragments;

        const int scanStep = 2;
        const int minimumCoarseCells = 48;
        const int maximumCoarseCells = 12_000;
        const int maximumScanCells = 180_000;
        const int maximumSnapshotSamples = 2_000_000;
        maximumFragments = Math.Min(maximumFragments, 2);

        int minX = Math.Clamp(
            (int)MathF.Floor((centerMeters.X - halfExtentsMeters.X) /
                (VoxelSizeMeters * scanStep)) * scanStep,
            0, Math.Max(0, Width - scanStep));
        int minY = Math.Clamp(
            (int)MathF.Floor((centerMeters.Y - halfExtentsMeters.Y) /
                (VoxelSizeMeters * scanStep)) * scanStep,
            0, Math.Max(0, Height - scanStep));
        int minZ = Math.Clamp(
            (int)MathF.Floor((centerMeters.Z - halfExtentsMeters.Z) /
                (VoxelSizeMeters * scanStep)) * scanStep,
            0, Math.Max(0, Depth - scanStep));
        int maxX = Math.Clamp(
            (int)MathF.Ceiling((centerMeters.X + halfExtentsMeters.X) /
                (VoxelSizeMeters * scanStep)) * scanStep,
            minX + scanStep, Width);
        int maxY = Math.Clamp(
            (int)MathF.Ceiling((centerMeters.Y + halfExtentsMeters.Y) /
                (VoxelSizeMeters * scanStep)) * scanStep,
            minY + scanStep, Height);
        int maxZ = Math.Clamp(
            (int)MathF.Ceiling((centerMeters.Z + halfExtentsMeters.Z) /
                (VoxelSizeMeters * scanStep)) * scanStep,
            minZ + scanStep, Depth);

        int nx = (maxX - minX) / scanStep;
        int ny = (maxY - minY) / scanStep;
        int nz = (maxZ - minZ) / scanStep;
        if (nx < 3 || ny < 3 || nz < 3)
            return fragments;

        int scanCount = checked(nx * ny * nz);
        if (scanCount > maximumScanCells)
            return fragments;

        byte[] occupied = new byte[scanCount];
        byte[] visited = new byte[scanCount];
        byte[] componentMask = new byte[scanCount];
        int[] queue = new int[scanCount];

        for (int cz = 0; cz < nz; cz++)
        for (int cy = 0; cy < ny; cy++)
        for (int cx = 0; cx < nx; cx++)
        {
            int sx = minX + cx * scanStep;
            int sy = minY + cy * scanStep;
            int sz = minZ + cz * scanStep;
            bool solid = false;

            for (int dz = 0; dz < scanStep && !solid; dz++)
            for (int dy = 0; dy < scanStep && !solid; dy++)
            for (int dx = 0; dx < scanStep; dx++)
            {
                if (GetDensity(sx + dx, sy + dy, sz + dz) > 0f)
                {
                    solid = true;
                    break;
                }
            }

            if (solid)
                occupied[cx + cy * nx + cz * nx * ny] = 1;
        }

        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();
        int changedMinX = Width;
        int changedMinY = Height;
        int changedMinZ = Depth;
        int changedMaxX = 0;
        int changedMaxY = 0;
        int changedMaxZ = 0;

        for (int seed = 0; seed < scanCount && fragments.Count < maximumFragments; seed++)
        {
            if (occupied[seed] == 0 || visited[seed] != 0)
                continue;

            int head = 0;
            int tail = 0;
            queue[tail++] = seed;
            visited[seed] = 1;
            bool touchesBoundary = false;
            int componentMinX = nx;
            int componentMinY = ny;
            int componentMinZ = nz;
            int componentMaxX = -1;
            int componentMaxY = -1;
            int componentMaxZ = -1;

            while (head < tail)
            {
                int current = queue[head++];
                int cx = current % nx;
                int cy = current / nx % ny;
                int cz = current / (nx * ny);

                touchesBoundary |=
                    cx == 0 || cy == 0 || cz == 0 ||
                    cx == nx - 1 || cy == ny - 1 || cz == nz - 1;

                componentMinX = Math.Min(componentMinX, cx);
                componentMinY = Math.Min(componentMinY, cy);
                componentMinZ = Math.Min(componentMinZ, cz);
                componentMaxX = Math.Max(componentMaxX, cx);
                componentMaxY = Math.Max(componentMaxY, cy);
                componentMaxZ = Math.Max(componentMaxZ, cz);

                if (cx > 0)
                {
                    int next = current - 1;
                    if (occupied[next] != 0 && visited[next] == 0)
                    {
                        visited[next] = 1;
                        queue[tail++] = next;
                    }
                }
                if (cx + 1 < nx)
                {
                    int next = current + 1;
                    if (occupied[next] != 0 && visited[next] == 0)
                    {
                        visited[next] = 1;
                        queue[tail++] = next;
                    }
                }
                if (cy > 0)
                {
                    int next = current - nx;
                    if (occupied[next] != 0 && visited[next] == 0)
                    {
                        visited[next] = 1;
                        queue[tail++] = next;
                    }
                }
                if (cy + 1 < ny)
                {
                    int next = current + nx;
                    if (occupied[next] != 0 && visited[next] == 0)
                    {
                        visited[next] = 1;
                        queue[tail++] = next;
                    }
                }
                if (cz > 0)
                {
                    int next = current - nx * ny;
                    if (occupied[next] != 0 && visited[next] == 0)
                    {
                        visited[next] = 1;
                        queue[tail++] = next;
                    }
                }
                if (cz + 1 < nz)
                {
                    int next = current + nx * ny;
                    if (occupied[next] != 0 && visited[next] == 0)
                    {
                        visited[next] = 1;
                        queue[tail++] = next;
                    }
                }
            }

            if (touchesBoundary || tail < minimumCoarseCells ||
                tail > maximumCoarseCells)
                continue;

            for (int i = 0; i < tail; i++)
                componentMask[queue[i]] = 1;

            // Keep one 0.2 m air shell around the component so the
            // detached mesh retains the source SDF gradients at its surface.
            int fineMinX = minX + (componentMinX - 1) * scanStep;
            int fineMinY = minY + (componentMinY - 1) * scanStep;
            int fineMinZ = minZ + (componentMinZ - 1) * scanStep;
            int fineMaxX = Math.Min(Width, minX + (componentMaxX + 2) * scanStep);
            int fineMaxY = Math.Min(Height, minY + (componentMaxY + 2) * scanStep);
            int fineMaxZ = Math.Min(Depth, minZ + (componentMaxZ + 2) * scanStep);
            int fragmentWidth = fineMaxX - fineMinX + 2;
            int fragmentHeight = fineMaxY - fineMinY + 2;
            int fragmentDepth = fineMaxZ - fineMinZ + 2;
            long snapshotSampleCount =
                (long)fragmentWidth * fragmentHeight * fragmentDepth;

            if (snapshotSampleCount <= maximumSnapshotSamples)
            {
                short[] density = new short[(int)snapshotSampleCount];
                ushort[] materials = new ushort[(int)snapshotSampleCount];
                Array.Fill(density, checked((short)-DensityScale));

                int solidSamples = 0;
                float massKg = 0f;

                for (int z = fineMinZ; z < fineMaxZ; z++)
                for (int y = fineMinY; y < fineMaxY; y++)
                for (int x = fineMinX; x < fineMaxX; x++)
                {
                    int coarseX = (x - minX) / scanStep;
                    int coarseY = (y - minY) / scanStep;
                    int coarseZ = (z - minZ) / scanStep;
                    int coarseIndex = coarseX + coarseY * nx + coarseZ * nx * ny;
                    bool inComponent = componentMask[coarseIndex] != 0;
                    bool inAirHalo =
                        !inComponent &&
                        ((coarseX > 0 && componentMask[coarseIndex - 1] != 0) ||
                         (coarseX + 1 < nx && componentMask[coarseIndex + 1] != 0) ||
                         (coarseY > 0 && componentMask[coarseIndex - nx] != 0) ||
                         (coarseY + 1 < ny && componentMask[coarseIndex + nx] != 0) ||
                         (coarseZ > 0 && componentMask[coarseIndex - nx * ny] != 0) ||
                         (coarseZ + 1 < nz && componentMask[coarseIndex + nx * ny] != 0));
                    if (!inComponent && !inAirHalo)
                        continue;

                    float oldDensity = GetDensity(x, y, z);
                    // The shell is for SDF interpolation only. Never pull a
                    // second positive component into this fragment's bounds.
                    if (oldDensity > 0f && !inComponent)
                        continue;

                    int localX = x - fineMinX + 1;
                    int localY = y - fineMinY + 1;
                    int localZ = z - fineMinZ + 1;
                    int localIndex = localX +
                        localY * fragmentWidth +
                        localZ * fragmentWidth * fragmentHeight;
                    density[localIndex] = checked((short)Math.Clamp(
                        (int)MathF.Round(oldDensity * DensityScale),
                        short.MinValue,
                        short.MaxValue));

                    if (oldDensity <= 0f)
                        continue;

                    ushort materialId = GetMaterialId(x, y, z);
                    if (materialId == 0)
                        materialId = 1;

                    materials[localIndex] = materialId;
                    massKg += VoxelMaterialCatalog.Get(materialId).MassPerVoxelKg;
                    solidSamples++;

                    SetDensityOverride(
                        x, y, z, -1f, oldDensity, revision, touched);
                    SetMaterialOverride(x, y, z, 0, revision, touched);

                    changedMinX = Math.Min(changedMinX, x);
                    changedMinY = Math.Min(changedMinY, y);
                    changedMinZ = Math.Min(changedMinZ, z);
                    changedMaxX = Math.Max(changedMaxX, x + 1);
                    changedMaxY = Math.Max(changedMaxY, y + 1);
                    changedMaxZ = Math.Max(changedMaxZ, z + 1);
                }

                if (solidSamples >= minimumCoarseCells && massKg > 0f)
                {
                    fragments.Add(new VoxelFragmentSnapshot(
                        density,
                        materials,
                        fragmentWidth,
                        fragmentHeight,
                        fragmentDepth,
                        new Vector3(
                            (fineMinX - 1) * VoxelSizeMeters,
                            (fineMinY - 1) * VoxelSizeMeters,
                            (fineMinZ - 1) * VoxelSizeMeters),
                        massKg));
                }
            }

            for (int i = 0; i < tail; i++)
                componentMask[queue[i]] = 0;
        }

        if (touched.Count > 0)
        {
            Version = revision;
            VoxelBounds changedBounds = new(
                changedMinX, changedMinY, changedMinZ,
                changedMaxX, changedMaxY, changedMaxZ);
            IncludeHistoryBounds(changedBounds);
            Changed?.Invoke(changedBounds);
        }

        return fragments;
    }

    /// <summary>
    /// Restores a detached fragment to the static field when the dynamic-body
    /// budget is full or its mesh cannot be built.
    /// </summary>
    public void RestoreFragment(VoxelFragmentSnapshot fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        int originX = (int)MathF.Round(fragment.OriginMeters.X / VoxelSizeMeters);
        int originY = (int)MathF.Round(fragment.OriginMeters.Y / VoxelSizeMeters);
        int originZ = (int)MathF.Round(fragment.OriginMeters.Z / VoxelSizeMeters);
        ReadOnlySpan<short> density = fragment.DensitySamples;
        ReadOnlySpan<ushort> materials = fragment.MaterialSamples;
        int plane = fragment.Width * fragment.Height;

        long revision = Version + 1;
        HashSet<ChunkCoordinate> touched = new();
        int minX = Width, minY = Height, minZ = Depth;
        int maxX = 0, maxY = 0, maxZ = 0;

        for (int z = 1; z < fragment.Depth - 1; z++)
        for (int y = 1; y < fragment.Height - 1; y++)
        for (int x = 1; x < fragment.Width - 1; x++)
        {
            int sourceIndex = x + y * fragment.Width + z * plane;
            float nextDensity = density[sourceIndex] / (float)DensityScale;
            if (nextDensity <= 0f)
                continue;

            int targetX = originX + x;
            int targetY = originY + y;
            int targetZ = originZ + z;
            if (!IsInsideCell(targetX, targetY, targetZ))
                continue;

            float oldDensity = GetDensity(targetX, targetY, targetZ);
            SetDensityOverride(
                targetX, targetY, targetZ,
                nextDensity, oldDensity, revision, touched);
            SetMaterialOverride(
                targetX, targetY, targetZ,
                materials[sourceIndex] == 0 ? (ushort)1 : materials[sourceIndex],
                revision, touched);

            minX = Math.Min(minX, targetX);
            minY = Math.Min(minY, targetY);
            minZ = Math.Min(minZ, targetZ);
            maxX = Math.Max(maxX, targetX + 1);
            maxY = Math.Max(maxY, targetY + 1);
            maxZ = Math.Max(maxZ, targetZ + 1);
        }

        if (touched.Count == 0)
            return;

        foreach (ChunkCoordinate key in touched)
        {
            if (!_octreeIndex.TryGetLeaf(key, out Chunk? page) || page == null)
                continue;
            page.Compress();
        }

        Version = revision;
        VoxelBounds bounds = new(minX, minY, minZ, maxX, maxY, maxZ);
        IncludeHistoryBounds(bounds);
        Changed?.Invoke(bounds);
    }

    /// <summary>
    /// Starts returning a fragment to the main planet grid. The input transform
    /// is the body's current world transform, not its original detach position.
    /// Its rotated bounds are converted to global sample indices with a one-cell
    /// interpolation halo, so negative and positive world coordinates cannot
    /// accumulate local fragment offsets.
    /// </summary>
    public FragmentMergeJob BeginFragmentMerge(
        VoxelFragmentSnapshot fragment,
        Vector3 bodyPositionMeters,
        Quaternion bodyRotation)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (!IsFinite(bodyPositionMeters) ||
            !float.IsFinite(bodyRotation.X) ||
            !float.IsFinite(bodyRotation.Y) ||
            !float.IsFinite(bodyRotation.Z) ||
            !float.IsFinite(bodyRotation.W) ||
            bodyRotation.LengthSquared() < 0.000001f)
            throw new ArgumentOutOfRangeException(nameof(bodyRotation));

        bodyRotation = Quaternion.Normalize(bodyRotation);
        Vector3 axisX = Vector3.Transform(Vector3.UnitX, bodyRotation);
        Vector3 axisY = Vector3.Transform(Vector3.UnitY, bodyRotation);
        Vector3 axisZ = Vector3.Transform(Vector3.UnitZ, bodyRotation);
        Vector3 half = fragment.HalfExtentsMeters;
        Vector3 worldHalf = new(
            MathF.Abs(axisX.X) * half.X + MathF.Abs(axisY.X) * half.Y + MathF.Abs(axisZ.X) * half.Z,
            MathF.Abs(axisX.Y) * half.X + MathF.Abs(axisY.Y) * half.Y + MathF.Abs(axisZ.Y) * half.Z,
            MathF.Abs(axisX.Z) * half.X + MathF.Abs(axisY.Z) * half.Y + MathF.Abs(axisZ.Z) * half.Z);
        worldHalf += new Vector3(VoxelSizeMeters * 1.5f);

        static int MinSample(float value) =>
            (int)MathF.Floor(value / VoxelSizeMeters - 0.5f) - 1;
        static int MaxSample(float value) =>
            (int)MathF.Ceiling(value / VoxelSizeMeters - 0.5f) + 2;

        int minX = Math.Clamp(MinSample(bodyPositionMeters.X - worldHalf.X), 0, Width);
        int minY = Math.Clamp(MinSample(bodyPositionMeters.Y - worldHalf.Y), 0, Height);
        int minZ = Math.Clamp(MinSample(bodyPositionMeters.Z - worldHalf.Z), 0, Depth);
        int maxX = Math.Clamp(MaxSample(bodyPositionMeters.X + worldHalf.X), minX, Width);
        int maxY = Math.Clamp(MaxSample(bodyPositionMeters.Y + worldHalf.Y), minY, Height);
        int maxZ = Math.Clamp(MaxSample(bodyPositionMeters.Z + worldHalf.Z), minZ, Depth);

        VoxelBounds bounds = new(minX, minY, minZ, maxX, maxY, maxZ);
        long sampleCount = (long)(maxX - minX) *
            (maxY - minY) * (maxZ - minZ);
        if (sampleCount <= 0 || sampleCount > 12_000_000)
            throw new InvalidOperationException(
                "Settled fragment is outside the supported merge volume.");

        IDisposable historyScope = BeginUndoableEdit();
        return new FragmentMergeJob(
            this,
            fragment,
            bodyPositionMeters,
            Quaternion.Inverse(bodyRotation),
            bounds,
            historyScope);
    }

    /// <summary>
    /// Bakes at most maximumSamples target lattice positions. Density is
    /// resampled in fragment-local coordinates, then combined with the world
    /// using an SDF union (max). This preserves existing terrain and avoids
    /// replacing the planet with a fragment's local bounding-box field.
    /// </summary>
    public bool ProcessFragmentMerge(
        FragmentMergeJob job,
        int maximumSamples = 12_000)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!ReferenceEquals(job.Owner, this))
            throw new ArgumentException("Merge job belongs to another voxel field.", nameof(job));
        if (maximumSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSamples));
        if (job.IsCompleted)
            return true;

        int width = job.Bounds.MaxX - job.Bounds.MinX;
        int height = job.Bounds.MaxY - job.Bounds.MinY;
        long plane = (long)width * height;
        int processed = 0;
        long revision = Version + 1;

        while (job.Cursor < job.TotalSamples && processed < maximumSamples)
        {
            long linear = job.Cursor++;
            int x = job.Bounds.MinX + (int)(linear % width);
            int y = job.Bounds.MinY + (int)(linear / width % height);
            int z = job.Bounds.MinZ + (int)(linear / plane);
            processed++;

            Vector3 worldSamplePosition = new(
                (x + 0.5f) * VoxelSizeMeters,
                (y + 0.5f) * VoxelSizeMeters,
                (z + 0.5f) * VoxelSizeMeters);
            Vector3 fragmentLocalPosition =
                Vector3.Transform(
                    worldSamplePosition - job.BodyPosition,
                    job.InverseRotation) +
                job.Fragment.LocalPivotMeters;

            float fragmentDensity =
                job.Fragment.SampleDensityLocal(fragmentLocalPosition);
            float oldDensity = GetDensity(x, y, z);
            float mergedDensity = MathF.Max(oldDensity, fragmentDensity);
            if (Quantize(mergedDensity) == Quantize(oldDensity))
                continue;

            SetDensityOverride(
                x, y, z,
                mergedDensity,
                oldDensity,
                revision,
                job.Touched);

            ushort materialId = mergedDensity > 0f &&
                fragmentDensity > oldDensity
                    ? job.Fragment.GetMaterialAtLocalPosition(fragmentLocalPosition)
                    : GetMaterialId(x, y, z);
            if (mergedDensity > 0f && materialId == 0)
                materialId = 1;
            if (mergedDensity <= 0f)
                materialId = 0;

            SetMaterialOverride(
                x, y, z, materialId, revision, job.Touched);

            job.MinX = Math.Min(job.MinX, x);
            job.MinY = Math.Min(job.MinY, y);
            job.MinZ = Math.Min(job.MinZ, z);
            job.MaxX = Math.Max(job.MaxX, x + 1);
            job.MaxY = Math.Max(job.MaxY, y + 1);
            job.MaxZ = Math.Max(job.MaxZ, z + 1);
        }

        if (job.Cursor < job.TotalSamples)
            return false;

        if (job.Touched.Count > 0)
        {
            CompactTouchedPages(job.Touched);
            Version = revision;
            VoxelBounds changedBounds = new(
                job.MinX, job.MinY, job.MinZ,
                job.MaxX, job.MaxY, job.MaxZ);
            IncludeHistoryBounds(changedBounds);
            Changed?.Invoke(changedBounds);
        }

        job.HistoryScope.Dispose();
        job.IsCompleted = true;
        return true;
    }

    private void SetDensityOverride(
        int x, int y, int z,
        float density,
        float previousDensity,
        long revision,
        HashSet<ChunkCoordinate> touched)
    {
        if (x < 0 || y < 0 || z < 0 ||
            x >= Width || y >= Height || z >= Depth ||
            EncodeContent(density) == EncodeContent(previousDensity))
            return;

        ChunkCoordinate key = GetSampleChunk(x, y, z, out int index);
        CaptureChunkBeforeWrite(key);
        Chunk chunk = GetOrCreateChunk(key);
        chunk.SetContent(index, EncodeContent(density));
        chunk.Revision = revision;
        touched.Add(key);
        _editedChunkColumns.Add((key.X, key.Z));
    }

    private void SetMaterialOverride(
        int x, int y, int z,
        ushort materialId,
        long revision,
        HashSet<ChunkCoordinate> touched)
    {
        if (!IsInsideCell(x, y, z))
            return;
        if (materialId >= byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(materialId), "Material ID 255 is reserved for NULL_MATERIAL.");

        byte material = materialId == 0 ? byte.MaxValue : (byte)materialId;
        ChunkCoordinate key = GetCellChunk(x, y, z, out int index);
        if (_octreeIndex.TryGetLeaf(key, out Chunk? existing) &&
            existing.GetMaterial(index) == material)
            return;

        CaptureChunkBeforeWrite(key);
        Chunk chunk = GetOrCreateChunk(key);
        chunk.SetMaterial(index, material);
        chunk.Revision = revision;
        touched.Add(key);
        _editedChunkColumns.Add((key.X, key.Z));
    }

    private void CompleteEdit(
        VoxelBounds bounds,
        long revision,
        HashSet<ChunkCoordinate> touched)
    {
        if (touched.Count == 0)
            return;

        CompactTouchedPages(touched);
        Version = revision;
        IncludeHistoryBounds(bounds);
        Changed?.Invoke(bounds);
    }

    private void CompactTouchedPages(HashSet<ChunkCoordinate> touched)
    {
        foreach (ChunkCoordinate key in touched)
        {
            if (!_octreeIndex.TryGetLeaf(key, out Chunk? page) || page == null)
                continue;

            page.Compress();
            _octreeIndex.UpdateLeafConstitution(key, page);
        }
    }

    private void CaptureChunkBeforeWrite(ChunkCoordinate key)
    {
        if (_historyCapture == null || _isRestoringHistory ||
            _historyCapture.Before.ContainsKey(key))
            return;

        _historyCapture.Before.Add(
            key,
            _octreeIndex.TryGetLeaf(key, out Chunk? chunk)
                ? ChunkSnapshot.From(chunk)
                : null);
    }

    private void IncludeHistoryBounds(VoxelBounds bounds)
    {
        if (_historyCapture == null)
            return;

        if (!_historyCapture.HasBounds)
        {
            _historyCapture.Bounds = bounds;
            _historyCapture.HasBounds = true;
            return;
        }

        VoxelBounds current = _historyCapture.Bounds;
        _historyCapture.Bounds = new VoxelBounds(
            Math.Min(current.MinX, bounds.MinX),
            Math.Min(current.MinY, bounds.MinY),
            Math.Min(current.MinZ, bounds.MinZ),
            Math.Max(current.MaxX, bounds.MaxX),
            Math.Max(current.MaxY, bounds.MaxY),
            Math.Max(current.MaxZ, bounds.MaxZ));
    }

    private void FinishUndoableEdit()
    {
        EditHistoryCapture? capture = _historyCapture;
        _historyCapture = null;
        if (capture == null || capture.Before.Count == 0)
            return;

        Dictionary<ChunkCoordinate, ChunkSnapshot?> after = new(capture.Before.Count);
        long estimatedBytes = 0;
        bool anyDifference = false;
        foreach ((ChunkCoordinate key, ChunkSnapshot? before) in capture.Before)
        {
            ChunkSnapshot? current = _octreeIndex.TryGetLeaf(key, out Chunk? chunk)
                ? ChunkSnapshot.From(chunk)
                : null;
            after.Add(key, current);

            if (before == null && current == null)
                continue;
            if (before != null && current != null && before.ContentEquals(current))
                continue;

            anyDifference = true;
            if (before != null)
                estimatedBytes += ChunkSnapshotBytes;
            if (current != null)
                estimatedBytes += ChunkSnapshotBytes;
        }

        if (!anyDifference)
            return;

        _redoHistory.Clear();
        EditHistoryEntry entry = new()
        {
            Before = capture.Before,
            After = after,
            Bounds = capture.HasBounds
                ? capture.Bounds
                : new VoxelBounds(0, 0, 0, Width, Height, Depth),
            EstimatedBytes = estimatedBytes
        };

        _undoHistory.Add(entry);
        _undoHistoryBytes += estimatedBytes;
        TrimUndoHistory();
    }

    private void TrimUndoHistory()
    {
        while (_undoHistory.Count > MaximumUndoEntries ||
               (_undoHistoryBytes > MaximumUndoBytes && _undoHistory.Count > 1))
        {
            EditHistoryEntry oldest = _undoHistory[0];
            _undoHistory.RemoveAt(0);
            _undoHistoryBytes -= oldest.EstimatedBytes;
        }
    }

    private void RestoreHistory(
        Dictionary<ChunkCoordinate, ChunkSnapshot?> snapshots,
        VoxelBounds bounds)
    {
        _isRestoringHistory = true;
        Version++;
        try
        {
            foreach ((ChunkCoordinate key, ChunkSnapshot? snapshot) in snapshots)
            {
                if (snapshot == null)
                {
                    _octreeIndex.TryRemoveLeaf(key, out _);
                    continue;
                }

                _octreeIndex.SetLeaf(key, snapshot.ToChunk(Version));
            }
        }
        finally
        {
            _isRestoringHistory = false;
        }

        _editedChunkColumns.Clear();
        List<ChunkCoordinate> leaves = new();
        _octreeIndex.CollectLeaves(
            0, 0, 0,
            (Width - 1) / DataCellSize,
            (Height - 1) / DataCellSize,
            (Depth - 1) / DataCellSize,
            leaves);
        foreach (ChunkCoordinate key in leaves)
            _editedChunkColumns.Add((key.X, key.Z));

        Changed?.Invoke(bounds);
    }

    private Chunk GetOrCreateChunk(ChunkCoordinate key)
    {
        if (_octreeIndex.TryGetLeaf(key, out Chunk? existing) && existing != null)
            return existing;

        Chunk created = CreateChunkData(key, revision: 0);
        return _octreeIndex.GetOrAddLeaf(key, created);
    }

    private Chunk CreateChunkData(ChunkCoordinate key, long revision)
    {
        Chunk chunk = new() { Revision = revision };
        int originX = key.X * DataCellSize;
        int originY = key.Y * DataCellSize;
        int originZ = key.Z * DataCellSize;

        for (int z = 0; z < DataCellSize; z++)
        for (int y = 0; y < DataCellSize; y++)
        for (int x = 0; x < DataCellSize; x++)
        {
            int worldX = originX + x;
            int worldY = originY + y;
            int worldZ = originZ + z;
            int index = x + y * DataCellSize + z * CellPlane;

            byte content = worldX < Width && worldY < Height && worldZ < Depth
                ? EncodeContent(_baseDensity(worldX, worldY, worldZ))
                : (byte)0;

            chunk.SetContent(index, content);
            chunk.SetMaterial(
                index,
                content > ContentIsoLevel
                    ? EncodeMaterial(_baseMaterial(worldX, worldY, worldZ))
                    : byte.MaxValue);
        }

        return chunk;
    }

    private bool PageMatchesBase(Chunk chunk, ChunkCoordinate key)
    {
        int originX = key.X * DataCellSize;
        int originY = key.Y * DataCellSize;
        int originZ = key.Z * DataCellSize;

        for (int z = 0; z < DataCellSize; z++)
        for (int y = 0; y < DataCellSize; y++)
        for (int x = 0; x < DataCellSize; x++)
        {
            int worldX = originX + x;
            int worldY = originY + y;
            int worldZ = originZ + z;
            int index = x + y * DataCellSize + z * CellPlane;

            byte content = worldX < Width && worldY < Height && worldZ < Depth
                ? EncodeContent(_baseDensity(worldX, worldY, worldZ))
                : (byte)0;

            if (chunk.GetContent(index) != content)
                return false;

            byte material =
                content > ContentIsoLevel
                    ? EncodeMaterial(_baseMaterial(worldX, worldY, worldZ))
                    : byte.MaxValue;
            if (chunk.GetMaterial(index) != material)
                return false;
        }

        return true;
    }

    private ChunkCoordinate GetSampleChunk(int x, int y, int z, out int index)
    {
        int cx = x / DataCellSize;
        int cy = y / DataCellSize;
        int cz = z / DataCellSize;
        int lx = x % DataCellSize;
        int ly = y % DataCellSize;
        int lz = z % DataCellSize;

        // A lattice sample on a chunk boundary belongs to the chunk on its
        // positive side. Each sample therefore has exactly one owner.
        index = lx + ly * DensitySamplesPerAxis +
            lz * SamplePlane;
        return new ChunkCoordinate(cx, cy, cz);
    }

    private ChunkCoordinate GetCellChunk(int x, int y, int z, out int index)
    {
        int cx = x / DataCellSize;
        int cy = y / DataCellSize;
        int cz = z / DataCellSize;
        int lx = x % DataCellSize;
        int ly = y % DataCellSize;
        int lz = z % DataCellSize;
        index = lx + ly * DataCellSize + lz * CellPlane;
        return new ChunkCoordinate(cx, cy, cz);
    }

    private bool IsInsideCell(int x, int y, int z) =>
        x >= 0 && y >= 0 && z >= 0 &&
        x < Width && y < Height && z < Depth;

    private VoxelBounds ClampCellBounds(
        int minX, int minY, int minZ,
        int maxX, int maxY, int maxZ) =>
        new(
            Math.Clamp(minX, 0, Width),
            Math.Clamp(minY, 0, Height),
            Math.Clamp(minZ, 0, Depth),
            Math.Clamp(maxX, 0, Width),
            Math.Clamp(maxY, 0, Height),
            Math.Clamp(maxZ, 0, Depth));

    private void GetCellBounds(Vector3 center, float radius, out VoxelBounds bounds)
    {
        bounds = ClampCellBounds(
            (int)MathF.Floor(center.X - radius),
            (int)MathF.Floor(center.Y - radius),
            (int)MathF.Floor(center.Z - radius),
            (int)MathF.Ceiling(center.X + radius),
            (int)MathF.Ceiling(center.Y + radius),
            (int)MathF.Ceiling(center.Z + radius));
    }

    private void GetCapsuleBounds(Vector3 start, Vector3 end, float radius, out VoxelBounds bounds)
    {
        bounds = ClampCellBounds(
            (int)MathF.Floor(MathF.Min(start.X, end.X) - radius),
            (int)MathF.Floor(MathF.Min(start.Y, end.Y) - radius),
            (int)MathF.Floor(MathF.Min(start.Z, end.Z) - radius),
            (int)MathF.Ceiling(MathF.Max(start.X, end.X) + radius),
            (int)MathF.Ceiling(MathF.Max(start.Y, end.Y) + radius),
            (int)MathF.Ceiling(MathF.Max(start.Z, end.Z) + radius));
    }

    private static Vector3 ToVoxelPosition(Vector3 meters) =>
        meters / VoxelSizeMeters;

    private void ValidateBrush(float radiusMeters, ushort materialId, bool requiresMaterial)
    {
        if (!float.IsFinite(radiusMeters) || radiusMeters <= 0f || radiusMeters > 8f)
            throw new ArgumentOutOfRangeException(nameof(radiusMeters));
        if (requiresMaterial && materialId == 0)
            throw new ArgumentOutOfRangeException(nameof(materialId));
    }

    private static void ValidateEditWork(VoxelBounds bounds)
    {
        long x = Math.Max(0, bounds.MaxX - bounds.MinX + 1);
        long y = Math.Max(0, bounds.MaxY - bounds.MinY + 1);
        long z = Math.Max(0, bounds.MaxZ - bounds.MinZ + 1);

        if (x * y * z > MaximumEditSampleCount)
        {
            throw new InvalidOperationException(
                "Voxel edit is too large for one synchronous operation. " +
                "Split it into smaller local edits.");
        }
    }

    private static byte Quantize(float density) => EncodeContent(density);

    private static byte EncodeContent(float density)
    {
        if (float.IsNaN(density))
            return ContentIsoLevel;

        float signedDistance = Math.Clamp(density, -1f, 1f);
        float fillRatio = signedDistance * 0.5f + 0.5f;
        return (byte)(fillRatio * byte.MaxValue);
    }

    private static float DecodeContent(byte content) =>
        (content - ContentIsoLevel) / (float)(byte.MaxValue - ContentIsoLevel);

    private static byte EncodeMaterial(ushort materialId) =>
        materialId == 0 || materialId >= byte.MaxValue
            ? byte.MaxValue
            : (byte)materialId;

    private static ushort DecodeMaterial(byte materialId) =>
        materialId == byte.MaxValue ? (ushort)0 : materialId;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
