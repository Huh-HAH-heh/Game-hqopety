using System;
using System.Collections.Generic;

namespace Core.Map;

/// <summary>
/// Sparse, chunked 3D voxel storage. Each 16x16 XY chunk keeps the common
/// bottom-filled columns as one compact run. Columns with caves, overhangs,
/// multiple materials or disconnected solids keep an arbitrary sorted run
/// list. Every voxel is still addressed by (X,Y,Z); runs are only compression.
///
/// Zero means air. Non-zero values are material IDs. Runs use an inclusive
/// start and exclusive end in the native 0.1 m vertical voxel-layer grid.
/// </summary>
public sealed class SparseVoxelStore
{
    public const int ChunkSize = 16;
    private const int ChunkShift = 4;
    private const int ChunkMask = ChunkSize - 1;
    private const int ColumnsPerChunk = ChunkSize * ChunkSize;

    private readonly struct SolidRun
    {
        public readonly ushort Start;
        public readonly ushort End;
        public readonly ushort MaterialId;

        public SolidRun(int start, int end, ushort materialId)
        {
            Start = checked((ushort)start);
            End = checked((ushort)end);
            MaterialId = materialId;
        }
    }

    private sealed class Chunk
    {
        // The usual terrain case is a single solid run from Z=0 to TopLayer.
        public readonly ushort[] TopLayers = new ushort[ColumnsPerChunk];
        public readonly ushort[] MaterialIds = new ushort[ColumnsPerChunk];

        // Only columns that cannot be represented by one bottom-filled run
        // allocate extra run arrays.
        public Dictionary<int, SolidRun[]>? ComplexColumns;

        public bool IsEmpty
        {
            get
            {
                if (ComplexColumns is { Count: > 0 })
                    return false;

                for (int i = 0; i < TopLayers.Length; i++)
                {
                    if (TopLayers[i] != 0 && MaterialIds[i] != 0)
                        return false;
                }

                return true;
            }
        }
    }

    private readonly Dictionary<long, Chunk> _chunks = new();

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }

    public int ChunkCount => _chunks.Count;
    public int ComplexColumnCount { get; private set; }
    public int SimpleColumnCount =>
        _chunks.Count * ColumnsPerChunk - ComplexColumnCount;

    /// <summary>
    /// True when the store contains any occupied voxel. Air is implicit and
    /// does not allocate chunks or individual empty voxel records.
    /// </summary>
    public bool HasOverrides => _chunks.Count > 0;

    public SparseVoxelStore(int width, int height, int depth)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (depth <= 0 || depth > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(depth));

        Width = width;
        Height = height;
        Depth = depth;
    }

    /// <summary>
    /// Replaces the complete volume with a surface generated from uniform XY
    /// patches. Each column is stored as a voxel run, not as a renderer-only
    /// height field. Later edits can split that run into any number of solids.
    /// </summary>
    public void SetSurfaceHeightPatches(
        ReadOnlySpan<ushort> patchHeights,
        int patchSize,
        ushort materialId)
    {
        if (patchSize <= 0 ||
            Width % patchSize != 0 ||
            Height % patchSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(patchSize));
        }

        int patchesX = Width / patchSize;
        int patchesY = Height / patchSize;
        if (patchHeights.Length != checked(patchesX * patchesY))
        {
            throw new ArgumentException(
                "Patch height grid does not match the voxel volume dimensions.",
                nameof(patchHeights));
        }

        Clear();
        if (materialId == 0)
            return;

        int chunksX = (Width + ChunkMask) >> ChunkShift;
        int chunksY = (Height + ChunkMask) >> ChunkShift;

        for (int chunkY = 0; chunkY < chunksY; chunkY++)
        {
            int firstY = chunkY << ChunkShift;

            for (int chunkX = 0; chunkX < chunksX; chunkX++)
            {
                int firstX = chunkX << ChunkShift;
                Chunk? chunk = null;

                for (int localY = 0; localY < ChunkSize; localY++)
                {
                    int worldY = firstY + localY;
                    if (worldY >= Height)
                        break;

                    int patchRow = (worldY / patchSize) * patchesX;
                    int localRow = localY * ChunkSize;

                    for (int localX = 0; localX < ChunkSize; localX++)
                    {
                        int worldX = firstX + localX;
                        if (worldX >= Width)
                            break;

                        int heightUnits = patchHeights[
                            patchRow + worldX / patchSize];
                        int topLayer = Math.Clamp(
                            (heightUnits + WorldMap.LayerHeightUnits - 1) /
                                WorldMap.LayerHeightUnits,
                            0,
                            Depth);

                        if (topLayer <= 0)
                            continue;

                        chunk ??= new Chunk();
                        int localIndex = localRow + localX;
                        chunk.TopLayers[localIndex] = checked((ushort)topLayer);
                        chunk.MaterialIds[localIndex] = materialId;
                    }
                }

                if (chunk != null)
                    _chunks.Add(GetChunkKey(chunkX, chunkY), chunk);
            }
        }
    }

    /// <summary>
    /// Sets a rectangular surface patch to one bottom-filled voxel column.
    /// </summary>
    public void SetSurfaceRegion(
        int x,
        int y,
        int width,
        int height,
        int heightLayers,
        ushort materialId)
    {
        ValidateRectangle(x, y, width, height);
        int topLayer = Math.Clamp(heightLayers, 0, Depth);
        if (materialId == 0)
            topLayer = 0;

        for (int worldY = y; worldY < y + height; worldY++)
        {
            for (int worldX = x; worldX < x + width; worldX++)
                SetSurfaceColumnByLayer(worldX, worldY, topLayer, materialId);
        }
    }

    /// <summary>
    /// Replaces one column with a single solid run starting at the world floor.
    /// Height is expressed in native 0.1 m layer units.
    /// </summary>
    public void SetSurfaceColumn(
        int x,
        int y,
        int heightUnits,
        ushort materialId)
    {
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));

        int topLayer = Math.Clamp(
            (heightUnits + WorldMap.LayerHeightUnits - 1) /
                WorldMap.LayerHeightUnits,
            0,
            Depth);
        if (materialId == 0)
            topLayer = 0;

        SetSurfaceColumnByLayer(x, y, topLayer, materialId);
    }

    private void SetSurfaceColumnByLayer(
        int x,
        int y,
        int topLayer,
        ushort materialId)
    {
        int chunkX = x >> ChunkShift;
        int chunkY = y >> ChunkShift;
        int localIndex = GetLocalIndex(x, y);
        long key = GetChunkKey(chunkX, chunkY);

        if (topLayer <= 0 || materialId == 0)
        {
            if (!_chunks.TryGetValue(key, out Chunk? existing))
                return;

            RemoveComplexColumn(existing, localIndex);
            existing.TopLayers[localIndex] = 0;
            existing.MaterialIds[localIndex] = 0;
            RemoveChunkIfEmpty(key, existing);
            return;
        }

        Chunk chunk = GetOrCreateChunk(key);
        RemoveComplexColumn(chunk, localIndex);
        chunk.TopLayers[localIndex] = checked((ushort)topLayer);
        chunk.MaterialIds[localIndex] = materialId;
    }

    /// <summary>
    /// Replaces a cuboid with one material. Material zero cuts an empty
    /// cuboid. Partial edits split only the affected vertical column runs.
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
        int endZ = checked(z + height);
        SolidRun[] scratch = new SolidRun[Depth + 2];

        for (int worldY = y; worldY < y + depth; worldY++)
        {
            for (int worldX = x; worldX < x + width; worldX++)
            {
                ApplyColumnRange(
                    worldX, worldY,
                    z, endZ,
                    materialId,
                    scratch);
            }
        }
    }

    public void ClearVolume(
        int x,
        int y,
        int z,
        int width,
        int depth,
        int height) =>
        SetVolume(x, y, z, width, depth, height, materialId: 0);

    public void SetVoxel(int x, int y, int z, ushort materialId) =>
        SetVolume(x, y, z, 1, 1, 1, materialId);

    public void ClearColumn(int x, int y)
    {
        if (!IsInside(x, y))
            return;

        long key = GetChunkKey(x >> ChunkShift, y >> ChunkShift);
        if (!_chunks.TryGetValue(key, out Chunk? chunk))
            return;

        int localIndex = GetLocalIndex(x, y);
        RemoveComplexColumn(chunk, localIndex);
        chunk.TopLayers[localIndex] = 0;
        chunk.MaterialIds[localIndex] = 0;
        RemoveChunkIfEmpty(key, chunk);
    }

    /// <summary>
    /// Replaces a column from the compatibility TileRange description.
    /// Only solid ranges are stored; all gaps are implicit air.
    /// </summary>
    public void SetColumnRanges(
        int x,
        int y,
        ReadOnlySpan<TileRange> ranges)
    {
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));

        SolidRun[] scratch = new SolidRun[Math.Max(1, ranges.Length)];
        int count = 0;

        for (int i = 0; i < ranges.Length; i++)
        {
            ref readonly TileRange range = ref ranges[i];
            if (range.StartZ > range.EndZ)
                throw new ArgumentException(
                    "Tile range start cannot be greater than its end.",
                    nameof(ranges));

            if (range.State != WorldMap.StateSolid || range.MaterialId == 0)
                continue;

            int start = Math.Clamp(
                range.StartZ / WorldMap.LayerHeightUnits,
                0,
                Depth);
            int end = Math.Clamp(
                (range.EndZ + WorldMap.LayerHeightUnits - 1) /
                    WorldMap.LayerHeightUnits,
                0,
                Depth);

            if (end > start)
                AppendMerged(scratch, ref count, start, end, range.MaterialId);
        }

        SetColumnRuns(x, y, scratch.AsSpan(0, count));
    }

    /// <summary>
    /// Returns true for every coordinate inside the volume. Material zero is
    /// a valid result and means air; false only means the coordinates are out
    /// of bounds.
    /// </summary>
    public bool TryGetMaterialId(
        int x,
        int y,
        int z,
        out ushort materialId)
    {
        materialId = GetMaterialId(x, y, z);
        return IsInside(x, y) && z >= 0 && z < Depth;
    }

    public ushort GetMaterialId(int x, int y, int z)
    {
        if (!IsInside(x, y) || z < 0 || z >= Depth)
            return 0;

        long key = GetChunkKey(x >> ChunkShift, y >> ChunkShift);
        if (!_chunks.TryGetValue(key, out Chunk? chunk))
            return 0;

        int localIndex = GetLocalIndex(x, y);
        if (chunk.ComplexColumns != null &&
            chunk.ComplexColumns.TryGetValue(localIndex, out SolidRun[]? runs))
        {
            int low = 0;
            int high = runs.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                SolidRun run = runs[middle];

                if (z < run.Start)
                    high = middle - 1;
                else if (z >= run.End)
                    low = middle + 1;
                else
                    return run.MaterialId;
            }

            return 0;
        }

        return z < chunk.TopLayers[localIndex]
            ? chunk.MaterialIds[localIndex]
            : (ushort)0;
    }

    public int FindHighestSolidLayerInColumn(int x, int y, int maxLayer)
    {
        if (!IsInside(x, y) || maxLayer < 0)
            return -1;

        int lastLayer = Math.Min(maxLayer, Depth - 1);
        long key = GetChunkKey(x >> ChunkShift, y >> ChunkShift);
        if (!_chunks.TryGetValue(key, out Chunk? chunk))
            return -1;

        int localIndex = GetLocalIndex(x, y);
        if (chunk.ComplexColumns != null &&
            chunk.ComplexColumns.TryGetValue(localIndex, out SolidRun[]? runs))
        {
            for (int i = runs.Length - 1; i >= 0; i--)
            {
                SolidRun run = runs[i];
                if (run.Start > lastLayer)
                    continue;

                return Math.Min(run.End - 1, lastLayer);
            }

            return -1;
        }

        int top = chunk.TopLayers[localIndex];
        return top == 0
            ? -1
            : Math.Min(top - 1, lastLayer);
    }

    public bool HasSolidInColumnBetween(
        int x,
        int y,
        int firstLayer,
        int lastLayer)
    {
        if (!IsInside(x, y) || firstLayer > lastLayer)
            return false;

        firstLayer = Math.Max(0, firstLayer);
        lastLayer = Math.Min(Depth - 1, lastLayer);
        if (firstLayer > lastLayer)
            return false;

        long key = GetChunkKey(x >> ChunkShift, y >> ChunkShift);
        if (!_chunks.TryGetValue(key, out Chunk? chunk))
            return false;

        int localIndex = GetLocalIndex(x, y);
        if (chunk.ComplexColumns != null &&
            chunk.ComplexColumns.TryGetValue(localIndex, out SolidRun[]? runs))
        {
            foreach (SolidRun run in runs)
            {
                if (run.End <= firstLayer)
                    continue;
                if (run.Start > lastLayer)
                    break;
                return true;
            }

            return false;
        }

        return chunk.TopLayers[localIndex] > firstLayer &&
            chunk.MaterialIds[localIndex] != 0;
    }

    /// <summary>
    /// Compatibility helper: true means the column requires a multi-run
    /// description rather than one bottom-filled solid interval.
    /// </summary>
    public bool HasOverridesInColumn(int x, int y) =>
        IsInside(x, y) &&
        TryGetChunkAndLocalIndex(x, y, out Chunk? chunk, out int localIndex) &&
        chunk?.ComplexColumns != null &&
        chunk.ComplexColumns.ContainsKey(localIndex);

    public bool HasEmptyOverridesInColumn(int x, int y)
    {
        if (!IsInside(x, y) ||
            !TryGetChunkAndLocalIndex(x, y, out Chunk? chunk, out int localIndex) ||
            chunk?.ComplexColumns == null ||
            !chunk.ComplexColumns.TryGetValue(localIndex, out SolidRun[]? runs))
        {
            return false;
        }

        if (runs.Length != 1 || runs[0].Start > 0 || runs[0].End < Depth)
            return true;

        return false;
    }

    public bool IsSimpleColumn(int x, int y) =>
        IsInside(x, y) &&
        (!TryGetChunkAndLocalIndex(x, y, out Chunk? chunk, out int localIndex) ||
         chunk?.ComplexColumns == null ||
         !chunk.ComplexColumns.ContainsKey(localIndex));

    public bool TryGetSimpleColumn(
        int x,
        int y,
        out ushort topLayer,
        out ushort materialId)
    {
        topLayer = 0;
        materialId = 0;
        if (!IsInside(x, y))
            return false;

        if (!TryGetChunkAndLocalIndex(x, y, out Chunk? chunk, out int localIndex) ||
            chunk == null)
        {
            return true;
        }

        if (chunk.ComplexColumns != null &&
            chunk.ComplexColumns.ContainsKey(localIndex))
        {
            return false;
        }

        topLayer = chunk.TopLayers[localIndex];
        materialId = chunk.MaterialIds[localIndex];
        return true;
    }

    /// <summary>
    /// Copies a complete ordered solid/air partition for one vertical column.
    /// The worst case is at most Depth runs because each run consumes at least
    /// one voxel layer.
    /// </summary>
    public int CopyColumnRanges(
        int x,
        int y,
        Span<TileRange> destination)
    {
        if (!IsInside(x, y))
            return 0;

        if (destination.Length < Depth)
        {
            throw new ArgumentException(
                "Destination must be large enough for the column's voxel runs.",
                nameof(destination));
        }

        if (TryGetSimpleColumn(x, y, out ushort topLayer, out ushort materialId))
        {
            if (topLayer <= 0 || materialId == 0)
            {
                destination[0] = CreateRange(0, Depth, 0);
                return 1;
            }

            destination[0] = CreateRange(0, topLayer, materialId);
            if (topLayer >= Depth)
                return 1;

            destination[1] = CreateRange(topLayer, Depth, 0);
            return 2;
        }

        TryGetChunkAndLocalIndex(x, y, out Chunk? chunk, out int localIndex);
        SolidRun[] runs = chunk!.ComplexColumns![localIndex];

        int count = 0;
        int cursor = 0;
        foreach (SolidRun run in runs)
        {
            if (run.Start > cursor)
                destination[count++] = CreateRange(cursor, run.Start, 0);

            destination[count++] = CreateRange(
                run.Start, run.End, run.MaterialId);
            cursor = run.End;
        }

        if (cursor < Depth)
            destination[count++] = CreateRange(cursor, Depth, 0);

        return count;
    }

    public int Compact() => ComplexColumnCount;

    public void Clear()
    {
        _chunks.Clear();
        ComplexColumnCount = 0;
    }

    private void ApplyColumnRange(
        int x,
        int y,
        int start,
        int end,
        ushort materialId,
        SolidRun[] scratch)
    {
        if (!TryGetChunkAndLocalIndex(x, y, out Chunk? existingChunk, out int localIndex))
        {
            if (materialId == 0)
                return;
        }

        Chunk? chunk = existingChunk;
        if (chunk == null && materialId != 0)
            chunk = GetOrCreateChunk(GetChunkKey(x >> ChunkShift, y >> ChunkShift));

        int currentCount = 0;
        SolidRun[]? complexRuns = null;
        if (chunk?.ComplexColumns != null)
            chunk.ComplexColumns.TryGetValue(localIndex, out complexRuns);

        if (complexRuns != null)
        {
            currentCount = complexRuns.Length;
        }
        else if (chunk != null && chunk.TopLayers[localIndex] > 0 &&
                 chunk.MaterialIds[localIndex] != 0)
        {
            currentCount = 1;
        }

        int resultCount = 0;
        bool inserted = false;

        for (int i = 0; i < currentCount; i++)
        {
            SolidRun current = complexRuns != null
                ? complexRuns[i]
                : new SolidRun(0, chunk!.TopLayers[localIndex],
                    chunk.MaterialIds[localIndex]);

            if (current.End <= start)
            {
                AppendMerged(
                    scratch, ref resultCount,
                    current.Start, current.End, current.MaterialId);
                continue;
            }

            if (current.Start >= end)
            {
                if (!inserted && materialId != 0)
                {
                    AppendMerged(
                        scratch, ref resultCount,
                        start, end, materialId);
                }

                inserted = true;
                AppendMerged(
                    scratch, ref resultCount,
                    current.Start, current.End, current.MaterialId);
                continue;
            }

            if (current.Start < start)
            {
                AppendMerged(
                    scratch, ref resultCount,
                    current.Start, start, current.MaterialId);
            }

            if (!inserted && materialId != 0)
            {
                AppendMerged(
                    scratch, ref resultCount,
                    start, end, materialId);
            }

            inserted = true;

            if (current.End > end)
            {
                AppendMerged(
                    scratch, ref resultCount,
                    end, current.End, current.MaterialId);
            }
        }

        if (!inserted && materialId != 0)
        {
            AppendMerged(
                scratch, ref resultCount,
                start, end, materialId);
        }

        if (chunk == null)
        {
            if (resultCount == 0)
                return;

            chunk = GetOrCreateChunk(GetChunkKey(x >> ChunkShift, y >> ChunkShift));
        }

        SetColumnRuns(chunk, localIndex, scratch.AsSpan(0, resultCount));
        RemoveChunkIfEmpty(GetChunkKey(x >> ChunkShift, y >> ChunkShift), chunk);
    }

    private void SetColumnRuns(
        int x,
        int y,
        ReadOnlySpan<SolidRun> runs)
    {
        long key = GetChunkKey(x >> ChunkShift, y >> ChunkShift);
        if (!_chunks.TryGetValue(key, out Chunk? chunk))
        {
            if (runs.IsEmpty)
                return;
            chunk = GetOrCreateChunk(key);
        }

        SetColumnRuns(chunk, GetLocalIndex(x, y), runs);
        RemoveChunkIfEmpty(key, chunk);
    }

    private void SetColumnRuns(
        Chunk chunk,
        int localIndex,
        ReadOnlySpan<SolidRun> runs)
    {
        if (runs.IsEmpty)
        {
            RemoveComplexColumn(chunk, localIndex);
            chunk.TopLayers[localIndex] = 0;
            chunk.MaterialIds[localIndex] = 0;
            return;
        }

        if (runs.Length == 1 &&
            runs[0].Start == 0 &&
            runs[0].MaterialId != 0)
        {
            RemoveComplexColumn(chunk, localIndex);
            chunk.TopLayers[localIndex] = runs[0].End;
            chunk.MaterialIds[localIndex] = runs[0].MaterialId;
            return;
        }

        chunk.TopLayers[localIndex] = 0;
        chunk.MaterialIds[localIndex] = 0;
        chunk.ComplexColumns ??= new Dictionary<int, SolidRun[]>();

        bool existed = chunk.ComplexColumns.ContainsKey(localIndex);
        chunk.ComplexColumns[localIndex] = runs.ToArray();
        if (!existed)
            ComplexColumnCount++;
    }

    private void RemoveComplexColumn(Chunk chunk, int localIndex)
    {
        if (chunk.ComplexColumns == null ||
            !chunk.ComplexColumns.Remove(localIndex))
        {
            return;
        }

        ComplexColumnCount--;
        if (chunk.ComplexColumns.Count == 0)
            chunk.ComplexColumns = null;
    }

    private void RemoveChunkIfEmpty(long key, Chunk chunk)
    {
        if (chunk.IsEmpty)
            _chunks.Remove(key);
    }

    private Chunk GetOrCreateChunk(long key)
    {
        if (_chunks.TryGetValue(key, out Chunk? chunk))
            return chunk;

        chunk = new Chunk();
        _chunks.Add(key, chunk);
        return chunk;
    }

    private bool TryGetChunkAndLocalIndex(
        int x,
        int y,
        out Chunk? chunk,
        out int localIndex)
    {
        localIndex = GetLocalIndex(x, y);
        return _chunks.TryGetValue(
            GetChunkKey(x >> ChunkShift, y >> ChunkShift),
            out chunk);
    }

    private static int GetLocalIndex(int x, int y) =>
        (x & ChunkMask) + ((y & ChunkMask) << ChunkShift);

    private static long GetChunkKey(int chunkX, int chunkY) =>
        ((long)chunkX << 32) | (uint)chunkY;

    private bool IsInside(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height;

    private void ValidateRectangle(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            (long)x + width > Width ||
            (long)y + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
    }

    private void ValidateBox(
        int x,
        int y,
        int z,
        int width,
        int depth,
        int height)
    {
        ValidateRectangle(x, y, width, depth);
        if (z < 0 || height <= 0 || (long)z + height > Depth)
            throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static void AppendMerged(
        Span<SolidRun> destination,
        ref int count,
        int start,
        int end,
        ushort materialId)
    {
        if (end <= start || materialId == 0)
            return;

        if (count > 0)
        {
            SolidRun previous = destination[count - 1];
            if (previous.End == start &&
                previous.MaterialId == materialId)
            {
                destination[count - 1] = new SolidRun(
                    previous.Start, end, materialId);
                return;
            }
        }

        destination[count++] = new SolidRun(start, end, materialId);
    }

    private static TileRange CreateRange(
        int start,
        int end,
        ushort materialId) =>
        new()
        {
            StartZ = checked((ushort)start),
            EndZ = checked((ushort)end),
            MaterialId = materialId,
            State = materialId == 0
                ? WorldMap.StateEmpty
                : WorldMap.StateSolid
        };
}
