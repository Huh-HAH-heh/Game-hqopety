using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Map;

/// <summary>
/// Voxel-addressable 3D volume with compact base primitives and locally materialized edit bricks.
/// Positive density is solid, negative density is air. Samples are taken at voxel-cell centres.
/// This class is storage only: it has no dependency on meshing, rendering or GPU APIs.
/// </summary>
public sealed class VoxelVolumeStore
{
    public const int BrickSize = 16;
    public const int BrickCellCount = BrickSize * BrickSize * BrickSize;
    public const int SpatialBucketSize = 256;
    public const int DensityUnitsPerMeter = 256;
    public const int SurfaceBandVoxels = 4;

    private const float MaxDensityMeters =
        short.MaxValue / (float)DensityUnitsPerMeter;

    private readonly Dictionary<BucketKey, List<int>> _geometryBuckets = new();
    private readonly Dictionary<BucketKey, List<int>> _materialBuckets = new();
    private readonly List<GeometryOperation> _geometry = new();
    private readonly List<MaterialOperation> _materials = new();
    private readonly Dictionary<BrickKey, EditBrick> _editBricks = new();
    private bool _baseSealed;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public float VoxelSizeMeters { get; }
    public long Revision { get; private set; }
    public bool IsBaseSealed => _baseSealed;
    public int BaseGeometryRegionCount => _geometry.Count;
    public int BaseMaterialRegionCount => _materials.Count;
    public int EditBrickCount => _editBricks.Count;

    public long EditBytes
    {
        get
        {
            long bytes = 0;
            foreach (EditBrick brick in _editBricks.Values)
                bytes += (long)(brick.Density?.Length ?? 0) * sizeof(short) +
                    (long)(brick.Material?.Length ?? 0) * sizeof(ushort);
            return bytes;
        }
    }

    public readonly record struct VoxelSample(short Density, ushort MaterialId)
    {
        public bool IsSolid => Density > 0;
        public float DensityMeters => Density / (float)DensityUnitsPerMeter;
    }

    /// <summary>Half-open integer bounds: Min inclusive, Max exclusive.</summary>
    public readonly record struct VoxelBounds(
        int MinX, int MinY, int MinZ,
        int MaxX, int MaxY, int MaxZ)
    {
        public int SizeX => MaxX - MinX;
        public int SizeY => MaxY - MinY;
        public int SizeZ => MaxZ - MinZ;
        public bool IsEmpty => SizeX <= 0 || SizeY <= 0 || SizeZ <= 0;
        public long Volume => IsEmpty ? 0L : (long)SizeX * SizeY * SizeZ;

        public bool Contains(int x, int y, int z) =>
            x >= MinX && x < MaxX &&
            y >= MinY && y < MaxY &&
            z >= MinZ && z < MaxZ;
    }

    private enum ShapeKind : byte
    {
        Box,
        Sphere,
        Capsule
    }

    private enum GeometryOperationKind : byte
    {
        Union,
        Difference
    }

    private readonly record struct BucketKey(int X, int Y, int Z);
    private readonly record struct BrickKey(int X, int Y, int Z);

    private readonly record struct GeometryOperation(
        VoxelBounds Bounds,
        ShapeKind Shape,
        GeometryOperationKind Operation,
        Vector3 A,
        Vector3 B,
        float Radius,
        ushort MaterialId);

    private readonly record struct MaterialOperation(
        VoxelBounds Bounds,
        ShapeKind Shape,
        Vector3 A,
        Vector3 B,
        float Radius,
        ushort MaterialId);

    private sealed class EditBrick
    {
        public short[]? Density;
        public ushort[]? Material;
    }

    public VoxelVolumeStore(
        int width,
        int height,
        int depth,
        float voxelSizeMeters = 0.1f)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth));
        if (!float.IsFinite(voxelSizeMeters) || voxelSizeMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(voxelSizeMeters));

        Width = width;
        Height = height;
        Depth = depth;
        VoxelSizeMeters = voxelSizeMeters;
    }

    // ---------------------------------------------------------------------
    // Base-volume authoring. These operations append compact shape records;
    // they do not allocate per-voxel arrays.
    // ---------------------------------------------------------------------

    public void AddSolidBox(Vector3 minMeters, Vector3 maxMeters, ushort materialId)
    {
        ValidateMaterial(materialId);
        ValidateBox(minMeters, maxMeters);
        Vector3 center = (minMeters + maxMeters) * 0.5f;
        Vector3 half = (maxMeters - minMeters) * 0.5f;
        AddGeometry(
            ShapeKind.Box, GeometryOperationKind.Union,
            center, half, 0f, materialId,
            minMeters, maxMeters);
    }

    public void SubtractBaseBox(Vector3 minMeters, Vector3 maxMeters)
    {
        ValidateBox(minMeters, maxMeters);
        Vector3 center = (minMeters + maxMeters) * 0.5f;
        Vector3 half = (maxMeters - minMeters) * 0.5f;
        AddGeometry(
            ShapeKind.Box, GeometryOperationKind.Difference,
            center, half, 0f, 0,
            minMeters, maxMeters);
    }

    public void AddSolidSphere(Vector3 centerMeters, float radiusMeters, ushort materialId)
    {
        ValidateBrush(centerMeters, radiusMeters);
        ValidateMaterial(materialId);
        Vector3 extent = new(radiusMeters);
        AddGeometry(
            ShapeKind.Sphere, GeometryOperationKind.Union,
            centerMeters, default, radiusMeters, materialId,
            centerMeters - extent, centerMeters + extent);
    }

    public void SubtractBaseSphere(Vector3 centerMeters, float radiusMeters)
    {
        ValidateBrush(centerMeters, radiusMeters);
        Vector3 extent = new(radiusMeters);
        AddGeometry(
            ShapeKind.Sphere, GeometryOperationKind.Difference,
            centerMeters, default, radiusMeters, 0,
            centerMeters - extent, centerMeters + extent);
    }

    public void AddSolidCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters,
        ushort materialId)
    {
        ValidateCapsule(startMeters, endMeters, radiusMeters);
        ValidateMaterial(materialId);
        Vector3 extent = new(radiusMeters);
        AddGeometry(
            ShapeKind.Capsule, GeometryOperationKind.Union,
            startMeters, endMeters, radiusMeters, materialId,
            Vector3.Min(startMeters, endMeters) - extent,
            Vector3.Max(startMeters, endMeters) + extent);
    }

    public void SubtractBaseCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters)
    {
        ValidateCapsule(startMeters, endMeters, radiusMeters);
        Vector3 extent = new(radiusMeters);
        AddGeometry(
            ShapeKind.Capsule, GeometryOperationKind.Difference,
            startMeters, endMeters, radiusMeters, 0,
            Vector3.Min(startMeters, endMeters) - extent,
            Vector3.Max(startMeters, endMeters) + extent);
    }

    public void PaintMaterialBox(Vector3 minMeters, Vector3 maxMeters, ushort materialId)
    {
        ValidateBaseMutable();
        ValidateMaterial(materialId);
        ValidateBox(minMeters, maxMeters);
        Vector3 center = (minMeters + maxMeters) * 0.5f;
        Vector3 half = (maxMeters - minMeters) * 0.5f;
        AddMaterial(
            ShapeKind.Box, center, half, 0f, materialId,
            minMeters, maxMeters);
    }

    public void PaintMaterialSphere(
        Vector3 centerMeters,
        float radiusMeters,
        ushort materialId)
    {
        ValidateBaseMutable();
        ValidateBrush(centerMeters, radiusMeters);
        ValidateMaterial(materialId);
        Vector3 extent = new(radiusMeters);
        AddMaterial(
            ShapeKind.Sphere, centerMeters, default, radiusMeters, materialId,
            centerMeters - extent, centerMeters + extent);
    }

    public void PaintMaterialCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters,
        ushort materialId)
    {
        ValidateBaseMutable();
        ValidateCapsule(startMeters, endMeters, radiusMeters);
        ValidateMaterial(materialId);
        Vector3 extent = new(radiusMeters);
        AddMaterial(
            ShapeKind.Capsule, startMeters, endMeters, radiusMeters, materialId,
            Vector3.Min(startMeters, endMeters) - extent,
            Vector3.Max(startMeters, endMeters) + extent);
    }

    /// <summary>
    /// Freezes the compact base description. Subsequent changes materialize only
    /// the touched 16^3 edit bricks.
    /// </summary>
    public void SealBase()
    {
        _baseSealed = true;
    }

    // ---------------------------------------------------------------------
    // Reads. Point reads are for small queries; ReadRegion is the hot path for
    // jobs that need a continuous block of voxels.
    // ---------------------------------------------------------------------

    public VoxelSample GetVoxel(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            return new VoxelSample(short.MinValue, 0);

        BrickKey key = GetBrickKey(x, y, z);
        if (_editBricks.TryGetValue(key, out EditBrick? brick))
        {
            int index = GetLocalIndex(x, y, z);
            if (brick.Density != null && brick.Material != null)
            {
                short density = brick.Density[index];
                return new VoxelSample(
                    density,
                    density > 0 ? brick.Material[index] : (ushort)0);
            }

            VoxelSample baseSample = EvaluateBaseVoxel(x, y, z);
            short resolvedDensity = brick.Density == null
                ? baseSample.Density
                : brick.Density[index];
            ushort resolvedMaterial = resolvedDensity <= 0
                ? (ushort)0
                : brick.Material == null
                    ? baseSample.MaterialId
                    : brick.Material[index];
            return new VoxelSample(resolvedDensity, resolvedMaterial);
        }

        return EvaluateBaseVoxel(x, y, z);
    }

    public short GetDensity(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            return short.MinValue;

        if (_editBricks.TryGetValue(GetBrickKey(x, y, z), out EditBrick? brick) &&
            brick.Density != null)
            return brick.Density[GetLocalIndex(x, y, z)];

        return EvaluateBaseDensity(
            x, y, z,
            GetBucketCandidates(_geometryBuckets, x, y, z));
    }

    public ushort GetMaterialId(int x, int y, int z) =>
        GetVoxel(x, y, z).MaterialId;

    /// <summary>
    /// Reads an exact integer region into dense caller-owned buffers.
    /// Layout is X-fastest: x + sizeX * (y + sizeY * z).
    /// Bounds must be inside the world; output spans must match the region volume.
    /// Candidate shape lists are resolved once per 16^3 brick, not once per voxel.
    /// </summary>
    public void ReadRegion(
        VoxelBounds bounds,
        Span<short> densityDestination,
        Span<ushort> materialDestination)
    {
        ValidateBounds(bounds);
        int sizeX = bounds.SizeX;
        int sizeY = bounds.SizeY;
        int sizeZ = bounds.SizeZ;
        int count = checked(sizeX * sizeY * sizeZ);

        if (densityDestination.Length != count)
            throw new ArgumentException("Density destination length does not match region.", nameof(densityDestination));
        if (materialDestination.Length != count)
            throw new ArgumentException("Material destination length does not match region.", nameof(materialDestination));
        if (count == 0) return;

        int minBrickX = bounds.MinX / BrickSize;
        int minBrickY = bounds.MinY / BrickSize;
        int minBrickZ = bounds.MinZ / BrickSize;
        int maxBrickX = (bounds.MaxX - 1) / BrickSize;
        int maxBrickY = (bounds.MaxY - 1) / BrickSize;
        int maxBrickZ = (bounds.MaxZ - 1) / BrickSize;

        for (int bz = minBrickZ; bz <= maxBrickZ; bz++)
        for (int by = minBrickY; by <= maxBrickY; by++)
        for (int bx = minBrickX; bx <= maxBrickX; bx++)
        {
            BrickKey key = new(bx, by, bz);
            _editBricks.TryGetValue(key, out EditBrick? edit);

            int brickOriginX = bx * BrickSize;
            int brickOriginY = by * BrickSize;
            int brickOriginZ = bz * BrickSize;

            int x0 = Math.Max(bounds.MinX, brickOriginX);
            int y0 = Math.Max(bounds.MinY, brickOriginY);
            int z0 = Math.Max(bounds.MinZ, brickOriginZ);
            int x1 = Math.Min(bounds.MaxX, brickOriginX + BrickSize);
            int y1 = Math.Min(bounds.MaxY, brickOriginY + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, brickOriginZ + BrickSize);

            List<int>? geometryCandidates = GetBucketCandidates(
                _geometryBuckets, brickOriginX, brickOriginY, brickOriginZ);
            List<int>? materialCandidates = GetBucketCandidates(
                _materialBuckets, brickOriginX, brickOriginY, brickOriginZ);

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            {
                int sourceIndex = GetLocalIndex(brickOriginX, brickOriginY, brickOriginZ,
                    x0, y, z);
                int destinationIndex =
                    (x0 - bounds.MinX) +
                    sizeX * ((y - bounds.MinY) + sizeY * (z - bounds.MinZ));

                for (int x = x0; x < x1; x++)
                {
                    int localIndex = sourceIndex + (x - x0);
                    bool needBase = edit == null ||
                        edit.Density == null ||
                        edit.Material == null;

                    VoxelSample baseSample = needBase
                        ? EvaluateBaseVoxel(x, y, z, geometryCandidates, materialCandidates)
                        : default;

                    short density = edit?.Density == null
                        ? baseSample.Density
                        : edit.Density[localIndex];

                    ushort material = density <= 0
                        ? (ushort)0
                        : edit?.Material == null
                            ? baseSample.MaterialId
                            : edit.Material[localIndex];

                    densityDestination[destinationIndex] = density;
                    materialDestination[destinationIndex] = material;
                    destinationIndex++;
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // Local edits. The base description is immutable after the first edit.
    // Only intersecting bricks are materialized.
    // ---------------------------------------------------------------------

    public void CarveSphere(Vector3 centerMeters, float radiusMeters) =>
        ApplyGeometryBrush(
            ShapeKind.Sphere, centerMeters, default, radiusMeters,
            add: false, materialId: 0);

    public void AddSphere(Vector3 centerMeters, float radiusMeters, ushort materialId)
    {
        ValidateMaterial(materialId);
        ApplyGeometryBrush(
            ShapeKind.Sphere, centerMeters, default, radiusMeters,
            add: true, materialId);
    }

    public void CarveCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters) =>
        ApplyGeometryBrush(
            ShapeKind.Capsule, startMeters, endMeters, radiusMeters,
            add: false, materialId: 0);

    public void AddCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters,
        ushort materialId)
    {
        ValidateMaterial(materialId);
        ApplyGeometryBrush(
            ShapeKind.Capsule, startMeters, endMeters, radiusMeters,
            add: true, materialId);
    }

    public void CarveBox(Vector3 minMeters, Vector3 maxMeters)
    {
        ValidateBox(minMeters, maxMeters);
        ApplyGeometryBrush(
            ShapeKind.Box,
            (minMeters + maxMeters) * 0.5f,
            (maxMeters - minMeters) * 0.5f,
            0f,
            add: false,
            materialId: 0);
    }

    public void AddBox(Vector3 minMeters, Vector3 maxMeters, ushort materialId)
    {
        ValidateBox(minMeters, maxMeters);
        ValidateMaterial(materialId);
        ApplyGeometryBrush(
            ShapeKind.Box,
            (minMeters + maxMeters) * 0.5f,
            (maxMeters - minMeters) * 0.5f,
            0f,
            add: true,
            materialId);
    }

    public void PaintSphere(Vector3 centerMeters, float radiusMeters, ushort materialId)
    {
        ValidateBrush(centerMeters, radiusMeters);
        ValidateMaterial(materialId);
        ApplyMaterialBrush(
            ShapeKind.Sphere, centerMeters, default, radiusMeters, materialId);
    }

    public void PaintCapsule(
        Vector3 startMeters,
        Vector3 endMeters,
        float radiusMeters,
        ushort materialId)
    {
        ValidateCapsule(startMeters, endMeters, radiusMeters);
        ValidateMaterial(materialId);
        ApplyMaterialBrush(
            ShapeKind.Capsule, startMeters, endMeters, radiusMeters, materialId);
    }

    public void PaintBox(Vector3 minMeters, Vector3 maxMeters, ushort materialId)
    {
        ValidateBox(minMeters, maxMeters);
        ValidateMaterial(materialId);
        ApplyMaterialBrush(
            ShapeKind.Box,
            (minMeters + maxMeters) * 0.5f,
            (maxMeters - minMeters) * 0.5f,
            0f,
            materialId);
    }

    public void SetDensity(int x, int y, int z, short density)
    {
        ValidateVoxelCoordinate(x, y, z);
        SealBase();
        EditBrick brick = GetOrCreateBrick(GetBrickKey(x, y, z));
        EnsureDensity(brick, GetBrickKey(x, y, z));
        brick.Density![GetLocalIndex(x, y, z)] = density;
        Revision++;
    }

    public void SetMaterial(int x, int y, int z, ushort materialId)
    {
        ValidateVoxelCoordinate(x, y, z);
        SealBase();
        EditBrick brick = GetOrCreateBrick(GetBrickKey(x, y, z));
        EnsureMaterial(brick, GetBrickKey(x, y, z));
        brick.Material![GetLocalIndex(x, y, z)] = materialId;
        Revision++;
    }

    private void ApplyGeometryBrush(
        ShapeKind shape,
        Vector3 a,
        Vector3 b,
        float radius,
        bool add,
        ushort materialId)
    {
        ValidateShape(shape, a, b, radius);
        SealBase();

        VoxelBounds bounds = GetShapeBounds(shape, a, b, radius, SurfaceBandVoxels);
        if (bounds.IsEmpty) return;

        int minBrickX = bounds.MinX / BrickSize;
        int minBrickY = bounds.MinY / BrickSize;
        int minBrickZ = bounds.MinZ / BrickSize;
        int maxBrickX = (bounds.MaxX - 1) / BrickSize;
        int maxBrickY = (bounds.MaxY - 1) / BrickSize;
        int maxBrickZ = (bounds.MaxZ - 1) / BrickSize;

        for (int bz = minBrickZ; bz <= maxBrickZ; bz++)
        for (int by = minBrickY; by <= maxBrickY; by++)
        for (int bx = minBrickX; bx <= maxBrickX; bx++)
        {
            BrickKey key = new(bx, by, bz);
            EditBrick brick = GetOrCreateBrick(key);
            EnsureDensity(brick, key);
            if (add) EnsureMaterial(brick, key);

            int ox = bx * BrickSize;
            int oy = by * BrickSize;
            int oz = bz * BrickSize;
            List<int>? geometryCandidates = GetBucketCandidates(_geometryBuckets, ox, oy, oz);
            int x0 = Math.Max(bounds.MinX, ox);
            int y0 = Math.Max(bounds.MinY, oy);
            int z0 = Math.Max(bounds.MinZ, oz);
            int x1 = Math.Min(bounds.MaxX, ox + BrickSize);
            int y1 = Math.Min(bounds.MaxY, oy + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, oz + BrickSize);

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                float sdf = EvaluateInsideDistance(
                    shape, VoxelCenter(x, y, z), a, b, radius);
                int index = GetLocalIndex(ox, oy, oz, x, y, z);
                float current = Dequantize(brick.Density![index]);
                float next = add
                    ? MathF.Max(current, sdf)
                    : MathF.Min(current, -sdf);

                brick.Density[index] = Quantize(next);
                if (add && sdf > 0f && sdf > current)
                    brick.Material![index] = materialId;
            }
        }

        Revision++;
    }

    private void ApplyMaterialBrush(
        ShapeKind shape,
        Vector3 a,
        Vector3 b,
        float radius,
        ushort materialId)
    {
        ValidateShape(shape, a, b, radius);
        SealBase();

        VoxelBounds bounds = GetShapeBounds(shape, a, b, radius, 0);
        if (bounds.IsEmpty) return;

        int minBrickX = bounds.MinX / BrickSize;
        int minBrickY = bounds.MinY / BrickSize;
        int minBrickZ = bounds.MinZ / BrickSize;
        int maxBrickX = (bounds.MaxX - 1) / BrickSize;
        int maxBrickY = (bounds.MaxY - 1) / BrickSize;
        int maxBrickZ = (bounds.MaxZ - 1) / BrickSize;

        for (int bz = minBrickZ; bz <= maxBrickZ; bz++)
        for (int by = minBrickY; by <= maxBrickY; by++)
        for (int bx = minBrickX; bx <= maxBrickX; bx++)
        {
            BrickKey key = new(bx, by, bz);
            EditBrick brick = GetOrCreateBrick(key);
            EnsureMaterial(brick, key);

            int ox = bx * BrickSize;
            int oy = by * BrickSize;
            int oz = bz * BrickSize;
            int x0 = Math.Max(bounds.MinX, ox);
            int y0 = Math.Max(bounds.MinY, oy);
            int z0 = Math.Max(bounds.MinZ, oz);
            int x1 = Math.Min(bounds.MaxX, ox + BrickSize);
            int y1 = Math.Min(bounds.MaxY, oy + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, oz + BrickSize);

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (EvaluateInsideDistance(
                        shape, VoxelCenter(x, y, z), a, b, radius) < 0f)
                    continue;

                short density = brick.Density == null
                    ? EvaluateBaseDensity(x, y, z, geometryCandidates)
                    : brick.Density[GetLocalIndex(ox, oy, oz, x, y, z)];

                if (density > 0)
                    brick.Material![GetLocalIndex(ox, oy, oz, x, y, z)] = materialId;
            }
        }

        Revision++;
    }

    private void AddGeometry(
        ShapeKind shape,
        GeometryOperationKind operation,
        Vector3 a,
        Vector3 b,
        float radius,
        ushort materialId,
        Vector3 minMeters,
        Vector3 maxMeters)
    {
        ValidateBaseMutable();
        VoxelBounds bounds = BoundsFromMeters(
            minMeters, maxMeters, SurfaceBandVoxels);
        if (bounds.IsEmpty) return;

        int id = _geometry.Count;
        _geometry.Add(new GeometryOperation(
            bounds, shape, operation, a, b, radius, materialId));
        AddToBuckets(_geometryBuckets, bounds, id);
        Revision++;
    }

    private void AddMaterial(
        ShapeKind shape,
        Vector3 a,
        Vector3 b,
        float radius,
        ushort materialId,
        Vector3 minMeters,
        Vector3 maxMeters)
    {
        ValidateBaseMutable();
        VoxelBounds bounds = BoundsFromMeters(minMeters, maxMeters, 0);
        if (bounds.IsEmpty) return;

        int id = _materials.Count;
        _materials.Add(new MaterialOperation(
            bounds, shape, a, b, radius, materialId));
        AddToBuckets(_materialBuckets, bounds, id);
        Revision++;
    }

    private VoxelSample EvaluateBaseVoxel(int x, int y, int z)
    {
        return EvaluateBaseVoxel(
            x, y, z,
            GetBucketCandidates(_geometryBuckets, x, y, z),
            GetBucketCandidates(_materialBuckets, x, y, z));
    }

    private VoxelSample EvaluateBaseVoxel(
        int x,
        int y,
        int z,
        List<int>? geometryCandidates,
        List<int>? materialCandidates)
    {
        float density = -MaxDensityMeters;
        ushort materialId = 0;
        Vector3 position = VoxelCenter(x, y, z);

        if (geometryCandidates != null)
        {
            for (int i = 0; i < geometryCandidates.Count; i++)
            {
                GeometryOperation operation = _geometry[geometryCandidates[i]];
                if (!operation.Bounds.Contains(x, y, z)) continue;

                float shapeDistance = EvaluateInsideDistance(
                    operation.Shape, position,
                    operation.A, operation.B, operation.Radius);

                if (operation.Operation == GeometryOperationKind.Union)
                {
                    if (shapeDistance > density)
                    {
                        density = shapeDistance;
                        if (shapeDistance > 0f)
                            materialId = operation.MaterialId;
                    }
                }
                else
                {
                    density = MathF.Min(density, -shapeDistance);
                    if (density <= 0f)
                        materialId = 0;
                }
            }
        }

        if (density <= 0f)
            return new VoxelSample(Quantize(density), 0);

        if (materialCandidates != null)
        {
            for (int i = 0; i < materialCandidates.Count; i++)
            {
                MaterialOperation operation = _materials[materialCandidates[i]];
                if (!operation.Bounds.Contains(x, y, z)) continue;

                if (EvaluateInsideDistance(
                        operation.Shape, position,
                        operation.A, operation.B, operation.Radius) >= 0f)
                    materialId = operation.MaterialId;
            }
        }

        return new VoxelSample(Quantize(density), materialId);
    }

    private short EvaluateBaseDensity(
        int x,
        int y,
        int z,
        List<int>? geometryCandidates)
    {
        float density = -MaxDensityMeters;
        Vector3 position = VoxelCenter(x, y, z);

        if (geometryCandidates != null)
        {
            for (int i = 0; i < geometryCandidates.Count; i++)
            {
                GeometryOperation operation = _geometry[geometryCandidates[i]];
                if (!operation.Bounds.Contains(x, y, z)) continue;

                float shapeDistance = EvaluateInsideDistance(
                    operation.Shape, position,
                    operation.A, operation.B, operation.Radius);

                density = operation.Operation == GeometryOperationKind.Union
                    ? MathF.Max(density, shapeDistance)
                    : MathF.Min(density, -shapeDistance);
            }
        }

        return Quantize(density);
    }

    private void EnsureDensity(EditBrick brick, BrickKey key)
    {
        if (brick.Density != null) return;
        short[] values = new short[BrickCellCount];
        int ox = key.X * BrickSize;
        int oy = key.Y * BrickSize;
        int oz = key.Z * BrickSize;
        List<int>? candidates = GetBucketCandidates(_geometryBuckets, ox, oy, oz);

        for (int z = 0; z < BrickSize; z++)
        for (int y = 0; y < BrickSize; y++)
        for (int x = 0; x < BrickSize; x++)
        {
            int wx = ox + x;
            int wy = oy + y;
            int wz = oz + z;
            values[x + BrickSize * (y + BrickSize * z)] =
                IsInside(wx, wy, wz)
                    ? EvaluateBaseDensity(wx, wy, wz, candidates)
                    : short.MinValue;
        }

        brick.Density = values;
    }

    private void EnsureMaterial(EditBrick brick, BrickKey key)
    {
        if (brick.Material != null) return;
        ushort[] values = new ushort[BrickCellCount];
        int ox = key.X * BrickSize;
        int oy = key.Y * BrickSize;
        int oz = key.Z * BrickSize;
        List<int>? geometryCandidates = GetBucketCandidates(_geometryBuckets, ox, oy, oz);
        List<int>? materialCandidates = GetBucketCandidates(_materialBuckets, ox, oy, oz);

        for (int z = 0; z < BrickSize; z++)
        for (int y = 0; y < BrickSize; y++)
        for (int x = 0; x < BrickSize; x++)
        {
            int wx = ox + x;
            int wy = oy + y;
            int wz = oz + z;
            values[x + BrickSize * (y + BrickSize * z)] =
                IsInside(wx, wy, wz)
                    ? EvaluateBaseVoxel(wx, wy, wz, geometryCandidates, materialCandidates).MaterialId
                    : (ushort)0;
        }

        brick.Material = values;
    }

    private EditBrick GetOrCreateBrick(BrickKey key)
    {
        if (_editBricks.TryGetValue(key, out EditBrick? brick))
            return brick;

        brick = new EditBrick();
        _editBricks.Add(key, brick);
        return brick;
    }

    private VoxelBounds GetShapeBounds(
        ShapeKind shape,
        Vector3 a,
        Vector3 b,
        float radius,
        int paddingVoxels)
    {
        Vector3 min;
        Vector3 max;

        switch (shape)
        {
            case ShapeKind.Box:
                min = a - b;
                max = a + b;
                break;
            case ShapeKind.Sphere:
                min = a - new Vector3(radius);
                max = a + new Vector3(radius);
                break;
            case ShapeKind.Capsule:
                Vector3 extent = new(radius);
                min = Vector3.Min(a, b) - extent;
                max = Vector3.Max(a, b) + extent;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        return BoundsFromMeters(min, max, paddingVoxels);
    }

    private VoxelBounds BoundsFromMeters(
        Vector3 minMeters,
        Vector3 maxMeters,
        int paddingVoxels)
    {
        int minX = ClampFloor(minMeters.X / VoxelSizeMeters - paddingVoxels, Width);
        int minY = ClampFloor(minMeters.Y / VoxelSizeMeters - paddingVoxels, Height);
        int minZ = ClampFloor(minMeters.Z / VoxelSizeMeters - paddingVoxels, Depth);
        int maxX = ClampCeiling(maxMeters.X / VoxelSizeMeters + paddingVoxels, Width);
        int maxY = ClampCeiling(maxMeters.Y / VoxelSizeMeters + paddingVoxels, Height);
        int maxZ = ClampCeiling(maxMeters.Z / VoxelSizeMeters + paddingVoxels, Depth);
        return new VoxelBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static int ClampFloor(float value, int limit)
    {
        if (value <= 0f) return 0;
        if (value >= limit) return limit;
        return (int)MathF.Floor(value);
    }

    private static int ClampCeiling(float value, int limit)
    {
        if (value <= 0f) return 0;
        if (value >= limit) return limit;
        return (int)MathF.Ceiling(value);
    }

    private void AddToBuckets(
        Dictionary<BucketKey, List<int>> buckets,
        VoxelBounds bounds,
        int regionId)
    {
        if (bounds.IsEmpty) return;

        int minX = bounds.MinX / SpatialBucketSize;
        int minY = bounds.MinY / SpatialBucketSize;
        int minZ = bounds.MinZ / SpatialBucketSize;
        int maxX = (bounds.MaxX - 1) / SpatialBucketSize;
        int maxY = (bounds.MaxY - 1) / SpatialBucketSize;
        int maxZ = (bounds.MaxZ - 1) / SpatialBucketSize;

        long bucketCount = (long)(maxX - minX + 1) *
            (maxY - minY + 1) * (maxZ - minZ + 1);
        if (bucketCount > 1_000_000)
            throw new InvalidOperationException("A base region spans too many spatial buckets.");

        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            BucketKey key = new(x, y, z);
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>(4);
                buckets.Add(key, list);
            }
            list.Add(regionId);
        }
    }

    private static List<int>? GetBucketCandidates(
        Dictionary<BucketKey, List<int>> buckets,
        int x,
        int y,
        int z) =>
        buckets.TryGetValue(
            new BucketKey(
                x / SpatialBucketSize,
                y / SpatialBucketSize,
                z / SpatialBucketSize),
            out List<int>? result)
            ? result
            : null;

    private static float EvaluateInsideDistance(
        ShapeKind shape,
        Vector3 p,
        Vector3 a,
        Vector3 b,
        float radius)
    {
        switch (shape)
        {
            case ShapeKind.Sphere:
                return radius - Vector3.Distance(p, a);

            case ShapeKind.Capsule:
                Vector3 ab = b - a;
                float denominator = Vector3.Dot(ab, ab);
                float t = denominator > 1e-12f
                    ? Math.Clamp(Vector3.Dot(p - a, ab) / denominator, 0f, 1f)
                    : 0f;
                Vector3 closest = a + ab * t;
                return radius - Vector3.Distance(p, closest);

            case ShapeKind.Box:
                Vector3 q = Vector3.Abs(p - a) - b;
                Vector3 outside = Vector3.Max(q, Vector3.Zero);
                float outsideDistance = outside.Length();
                float insideDistance = MathF.Min(
                    MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
                return -(outsideDistance + insideDistance);

            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private Vector3 VoxelCenter(int x, int y, int z) =>
        new(
            (x + 0.5f) * VoxelSizeMeters,
            (y + 0.5f) * VoxelSizeMeters,
            (z + 0.5f) * VoxelSizeMeters);

    private static short Quantize(float densityMeters)
    {
        float scaled = Math.Clamp(
            densityMeters * DensityUnitsPerMeter,
            short.MinValue,
            short.MaxValue);
        return (short)MathF.Round(scaled);
    }

    private static float Dequantize(short density) =>
        density / (float)DensityUnitsPerMeter;

    private bool IsInside(int x, int y, int z) =>
        (uint)x < (uint)Width &&
        (uint)y < (uint)Height &&
        (uint)z < (uint)Depth;

    private void ValidateVoxelCoordinate(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            throw new ArgumentOutOfRangeException(nameof(x), "Voxel coordinate is outside the world.");
    }

    private void ValidateBounds(VoxelBounds bounds)
    {
        if (bounds.MinX < 0 || bounds.MinY < 0 || bounds.MinZ < 0 ||
            bounds.MaxX > Width || bounds.MaxY > Height || bounds.MaxZ > Depth ||
            bounds.MinX > bounds.MaxX ||
            bounds.MinY > bounds.MaxY ||
            bounds.MinZ > bounds.MaxZ)
            throw new ArgumentOutOfRangeException(nameof(bounds));
    }

    private void ValidateBaseMutable()
    {
        if (_baseSealed)
            throw new InvalidOperationException("Base volume is sealed; use local edit operations.");
    }

    private static void ValidateMaterial(ushort materialId)
    {
        if (materialId == 0)
            throw new ArgumentOutOfRangeException(nameof(materialId), "Material ID 0 is reserved for air.");
    }

    private static void ValidateBox(Vector3 min, Vector3 max)
    {
        if (!IsFinite(min) || !IsFinite(max) ||
            min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            throw new ArgumentOutOfRangeException(nameof(max));
    }

    private static void ValidateBrush(Vector3 center, float radius)
    {
        if (!IsFinite(center) || !float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
    }

    private static void ValidateCapsule(Vector3 start, Vector3 end, float radius)
    {
        if (!IsFinite(start) || !IsFinite(end) ||
            !float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
    }

    private static void ValidateShape(ShapeKind shape, Vector3 a, Vector3 b, float radius)
    {
        if (!IsFinite(a) || !IsFinite(b) || !float.IsFinite(radius) || radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (shape == ShapeKind.Box && (b.X <= 0f || b.Y <= 0f || b.Z <= 0f))
            throw new ArgumentOutOfRangeException(nameof(b));
        if (shape != ShapeKind.Box && radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static BrickKey GetBrickKey(int x, int y, int z) =>
        new(x / BrickSize, y / BrickSize, z / BrickSize);

    private static int GetLocalIndex(int x, int y, int z) =>
        (x & (BrickSize - 1)) +
        BrickSize * ((y & (BrickSize - 1)) +
        BrickSize * (z & (BrickSize - 1)));

    private static int GetLocalIndex(
        int originX, int originY, int originZ,
        int x, int y, int z) =>
        (x - originX) +
        BrickSize * ((y - originY) +
        BrickSize * (z - originZ));
}
