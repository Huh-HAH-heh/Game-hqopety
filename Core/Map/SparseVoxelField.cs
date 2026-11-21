using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Map;

/// <summary>
/// Sparse, editable 3D scalar voxel field.
///
/// Coordinates are isotropic 0.1 m voxels on all three axes. Density is sampled
/// on lattice points; positive density is solid, negative density is air and
/// zero is the isosurface. Material IDs belong to cells, not to vertical columns.
/// Unmodified terrain is evaluated by a procedural base sampler; edited areas
/// materialize only the affected 16^3 chunks.
///
/// This keeps the 2304 x 2304 x 50 m world sparse while retaining a genuine
/// three-dimensional field for caves, arches, overhangs and CSG terrain edits.
/// </summary>
public sealed class SparseVoxelField
{
    public const int ChunkSize = 16;
    public const int DensitySamplesPerAxis = ChunkSize;
    public const float VoxelSizeMeters = 0.1f;
    public const float DensityScale = 32f;

    private const int SampleCount = DensitySamplesPerAxis * DensitySamplesPerAxis * DensitySamplesPerAxis;
    private const int CellCount = ChunkSize * ChunkSize * ChunkSize;
    private const int SamplePlane = DensitySamplesPerAxis * DensitySamplesPerAxis;
    private const int CellPlane = ChunkSize * ChunkSize;
    private const long MaximumEditSampleCount = 20_000_000;

    public readonly record struct ChunkCoordinate(int X, int Y, int Z);

    public readonly record struct VoxelBounds(
        int MinX, int MinY, int MinZ,
        int MaxX, int MaxY, int MaxZ);

    private sealed class Chunk
    {
        public readonly short[] Density = new short[SampleCount];
        public readonly byte[] DensityMask = new byte[SampleCount];
        public readonly ushort[] Materials = new ushort[CellCount];
        public readonly byte[] MaterialMask = new byte[CellCount];
        public long Revision;
    }

    private readonly Dictionary<ChunkCoordinate, Chunk> _chunks = new();
    private readonly HashSet<(int X, int Z)> _editedChunkColumns = new();
    private readonly Func<int, int, int, float> _baseDensity;
    private readonly Func<int, int, int, ushort> _baseMaterial;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public int ChunkCount => _chunks.Count;
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
    }

    public float GetDensity(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 ||
            x >= Width || y >= Height || z >= Depth)
            return -1f;

        ChunkCoordinate key = GetSampleChunk(x, y, z, out int index);
        if (_chunks.TryGetValue(key, out Chunk? chunk) &&
            chunk.DensityMask[index] != 0)
            return chunk.Density[index] / DensityScale;

        return _baseDensity(x, y, z);
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
        if (!IsInsideCell(x, y, z) ||
            GetDensity(x, y, z) <= 0f)
            return 0;

        ChunkCoordinate key = GetCellChunk(x, y, z, out int index);
        if (_chunks.TryGetValue(key, out Chunk? chunk) &&
            chunk.MaterialMask[index] != 0)
            return chunk.Materials[index];

        return _baseMaterial(x, y, z);
    }

    public VoxelMaterialDefinition GetMaterialDefinition(int x, int y, int z) =>
        VoxelMaterialCatalog.Get(GetMaterialId(x, y, z));

    public float GetCellMassKg(int x, int y, int z) =>
        GetMaterialDefinition(x, y, z).MassPerVoxelKg;

    public long GetChunkRevision(int chunkX, int chunkY, int chunkZ)
    {
        return _chunks.TryGetValue(
            new ChunkCoordinate(chunkX, chunkY, chunkZ),
            out Chunk? chunk)
                ? chunk.Revision
                : 0L;
    }

    public bool IsChunkKnown(int chunkX, int chunkY, int chunkZ) =>
        _chunks.ContainsKey(new ChunkCoordinate(chunkX, chunkY, chunkZ));

    public bool HasEditsInArea(int minX, int maxX, int minZ, int maxZ)
    {
        if (maxX < 0 || maxZ < 0 || minX >= Width || minZ >= Depth)
            return false;

        int firstChunkX = Math.Max(0, minX) / ChunkSize;
        int lastChunkX = Math.Min(Width - 1, maxX) / ChunkSize;
        int firstChunkZ = Math.Max(0, minZ) / ChunkSize;
        int lastChunkZ = Math.Min(Depth - 1, maxZ) / ChunkSize;
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
            sampleX / ChunkSize,
            sampleZ / ChunkSize));
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

    public void ClearEdits()
    {
        if (_chunks.Count == 0)
            return;

        _chunks.Clear();
        _editedChunkColumns.Clear();
        Version++;
        Changed?.Invoke(new VoxelBounds(0, 0, 0, Width, Height, Depth));
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

            SetDensityOverride(x, y, z, next, revision, touched);

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
            SetDensityOverride(x, y, z, nextDensity, revision, touched);
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
        ValidateBrush(radiusMeters, materialId, allowAir: add);
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
            SetDensityOverride(x, y, z, nextDensity, revision, touched);
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
            SetDensityOverride(x, y, z, nextDensity, revision, touched);
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

            float density = SampleDensity(point.X, point.Y, point.Z);
            if (add && density > 0f)
                SetMaterialOverride(x, y, z, materialId, revision, touched);
            else if (!add && density <= 0f)
                SetMaterialOverride(x, y, z, 0, revision, touched);
        }

        CompleteEdit(bounds, revision, touched);
    }

    private void SetDensityOverride(
        int x, int y, int z,
        float density,
        long revision,
        HashSet<ChunkCoordinate> touched)
    {
        if (x < 0 || y < 0 || z < 0 ||
            x >= Width || y >= Height || z >= Depth)
            return;

        ChunkCoordinate key = GetSampleChunk(x, y, z, out int index);
        Chunk chunk = GetOrCreateChunk(key);
        chunk.Density[index] = Quantize(density);
        chunk.DensityMask[index] = 1;
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

        ChunkCoordinate key = GetCellChunk(x, y, z, out int index);
        Chunk chunk = GetOrCreateChunk(key);
        chunk.Materials[index] = materialId;
        chunk.MaterialMask[index] = 1;
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

        Version = revision;
        Changed?.Invoke(bounds);
    }

    private Chunk GetOrCreateChunk(ChunkCoordinate key)
    {
        if (_chunks.TryGetValue(key, out Chunk? chunk))
            return chunk;

        chunk = new Chunk();
        _chunks.Add(key, chunk);
        return chunk;
    }

    private ChunkCoordinate GetSampleChunk(int x, int y, int z, out int index)
    {
        int cx = x / ChunkSize;
        int cy = y / ChunkSize;
        int cz = z / ChunkSize;
        int lx = x % ChunkSize;
        int ly = y % ChunkSize;
        int lz = z % ChunkSize;

        // A lattice sample on a chunk boundary belongs to the chunk on its
        // positive side. Each sample therefore has exactly one owner.
        index = lx + ly * DensitySamplesPerAxis +
            lz * SamplePlane;
        return new ChunkCoordinate(cx, cy, cz);
    }

    private ChunkCoordinate GetCellChunk(int x, int y, int z, out int index)
    {
        int cx = x / ChunkSize;
        int cy = y / ChunkSize;
        int cz = z / ChunkSize;
        int lx = x % ChunkSize;
        int ly = y % ChunkSize;
        int lz = z % ChunkSize;
        index = lx + ly * ChunkSize + lz * CellPlane;
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

    private static short Quantize(float density)
    {
        float scaled = Math.Clamp(density * DensityScale, short.MinValue, short.MaxValue);
        return checked((short)MathF.Round(scaled));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
