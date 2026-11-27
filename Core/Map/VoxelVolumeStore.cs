using System;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Map;

/// <summary>
/// Voxel-addressable 3D volume with compact base primitives and locally materialized edit bricks.
/// Positive density is solid, negative density is air. Samples are taken at voxel-cell centres.
/// This class is storage only: it has no dependency on meshing, rendering or GPU APIs.
/// </summary>
public sealed partial class VoxelVolumeStore
{
    public const int BrickSize = 16;
    public const int BrickCellCount = BrickSize * BrickSize * BrickSize;
    public const int SpatialBucketSize = 256;
    public const int DensityUnitsPerMeter = 256;
    public const int SurfaceBandVoxels = 4;

    private float DensityClampMeters => SurfaceBandVoxels * VoxelSizeMeters;

    private readonly Dictionary<BucketKey, List<int>> _geometryBuckets = new();
    private readonly Dictionary<BucketKey, List<int>> _materialBuckets = new();
    private readonly List<GeometryOperation> _geometry = new();
    private readonly List<MaterialOperation> _materials = new();
    private readonly Dictionary<BrickKey, BaseBrick> _baseBricks = new();
    private readonly Dictionary<BrickKey, EditBrick> _editBricks = new();
    private IVoxelBaseSource? _baseSource;
    private bool _baseSealed;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public float VoxelSizeMeters { get; }
    public long Revision { get; private set; }
    public bool IsBaseSealed => _baseSealed;
    public bool HasBaseSource => _baseSource != null;
    public int BaseGeometryRegionCount => _geometry.Count;
    public int BaseMaterialRegionCount => _materials.Count;
    public int BaseDenseBrickCount => _baseBricks.Count;
    public int EditBrickCount => _editBricks.Count;
    public long BaseDenseBytes
    {
        get
        {
            long bytes = 0;
            foreach (BaseBrick brick in _baseBricks.Values)
                bytes += (long)(brick.Density?.Length ?? 0) * sizeof(short) +
                    (long)(brick.Material?.Length ?? 0) * sizeof(ushort);
            return bytes;
        }
    }

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

    private sealed class BaseBrick
    {
        public short[]? Density;
        public ushort[]? Material;
        public bool IsUniform;
        public VoxelSample UniformSample;
    }

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
    /// Imports one exact 16^3 base brick. This is the path for arbitrary generated
    /// voxel data that cannot be represented by a small set of simple primitives.
    /// The arrays are copied and the material channel is normalized for air cells.
    /// An imported brick overrides primitive base operations in the same brick.
    /// </summary>
    public void WriteBaseBrick(
        int brickX,
        int brickY,
        int brickZ,
        ReadOnlySpan<short> density,
        ReadOnlySpan<ushort> material)
    {
        ValidateBaseMutable();
        if (brickX < 0 || brickY < 0 || brickZ < 0 ||
            (long)brickX * BrickSize >= Width ||
            (long)brickY * BrickSize >= Height ||
            (long)brickZ * BrickSize >= Depth)
            throw new ArgumentOutOfRangeException(nameof(brickX));
        if (density.Length != BrickCellCount)
            throw new ArgumentException("Density brick must contain exactly 16^3 values.", nameof(density));
        if (material.Length != BrickCellCount)
            throw new ArgumentException("Material brick must contain exactly 16^3 values.", nameof(material));

        short[] densityCopy = density.ToArray();
        ushort[] materialCopy = material.ToArray();
        for (int i = 0; i < BrickCellCount; i++)
        {
            densityCopy[i] = Quantize(Dequantize(densityCopy[i]));
            if (densityCopy[i] <= 0)
                materialCopy[i] = 0;
        }

        short firstDensity = densityCopy[0];
        ushort firstMaterial = materialCopy[0];
        bool uniform = true;
        for (int i = 1; i < BrickCellCount; i++)
        {
            if (densityCopy[i] != firstDensity || materialCopy[i] != firstMaterial)
            {
                uniform = false;
                break;
            }
        }

        _baseBricks[new BrickKey(brickX, brickY, brickZ)] = new BaseBrick
        {
            Density = uniform ? null : densityCopy,
            Material = uniform ? null : materialCopy,
            IsUniform = uniform,
            UniformSample = new VoxelSample(firstDensity, firstMaterial)
        };
        Revision++;
    }

    /// <summary>
    /// Freezes the compact base description. Subsequent changes materialize only
    /// the touched 16^3 edit bricks.
    /// </summary>
    /// <summary>
    /// Sets the immutable procedural source for all base voxels not overridden by
    /// imported bricks. CSG primitives and material regions are applied on top.
    /// The source must be deterministic, thread-safe for reads and serializable.
    /// </summary>
    public void SetBaseSource(IVoxelBaseSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateBaseMutable();

        if (_baseSource != null)
            throw new InvalidOperationException("A base voxel source is already assigned.");
        if (string.IsNullOrWhiteSpace(source.TypeId) || source.TypeId.Length > 256)
            throw new ArgumentException("Base source TypeId must contain 1..256 characters.", nameof(source));
        if (source.Width != Width || source.Height != Height || source.Depth != Depth ||
            MathF.Abs(source.VoxelSizeMeters - VoxelSizeMeters) > 1e-6f)
            throw new ArgumentException("Base source dimensions and voxel size must match the store.", nameof(source));

        _baseSource = source;
        Revision++;
    }

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
        int index = GetLocalIndex(x, y, z);
        _editBricks.TryGetValue(key, out EditBrick? edit);
        _baseBricks.TryGetValue(key, out BaseBrick? baseBrick);

        short baseDensity;
        ushort baseMaterial;
        if (baseBrick != null)
        {
            baseDensity = baseBrick.IsUniform
                ? baseBrick.UniformSample.Density
                : baseBrick.Density![index];
            baseMaterial = baseBrick.IsUniform
                ? baseBrick.UniformSample.MaterialId
                : baseBrick.Material![index];
        }
        else
        {
            VoxelSample baseSample = EvaluateBaseVoxel(x, y, z);
            baseDensity = baseSample.Density;
            baseMaterial = baseSample.MaterialId;
        }

        short density = edit?.Density == null
            ? baseDensity
            : edit.Density[index];
        ushort material = density <= 0
            ? (ushort)0
            : edit?.Material == null
                ? baseMaterial
                : edit.Material[index];

        return new VoxelSample(density, material);
    }

    public short GetDensity(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            return short.MinValue;

        BrickKey key = GetBrickKey(x, y, z);
        int index = GetLocalIndex(x, y, z);
        if (_editBricks.TryGetValue(key, out EditBrick? edit) &&
            edit.Density != null)
            return edit.Density[index];

        if (_baseBricks.TryGetValue(key, out BaseBrick? baseBrick))
            return baseBrick.IsUniform
                ? baseBrick.UniformSample.Density
                : baseBrick.Density![index];

        return EvaluateBaseDensity(
            x, y, z,
            GetBucketCandidates(_geometryBuckets, x, y, z));
    }

    public ushort GetMaterialId(int x, int y, int z) =>
        GetVoxel(x, y, z).MaterialId;

    /// <summary>
    /// Returns a value only when every voxel in the requested region is provably
    /// identical. This allows large empty or uniform areas to be queried without
    /// expanding them into per-voxel buffers.
    /// </summary>
    public bool TryGetUniformRegion(VoxelBounds bounds, out VoxelSample sample)
    {
        ValidateBounds(bounds);
        sample = default;
        if (bounds.IsEmpty)
            return true;

        if (HasBrickOverlap(_editBricks, bounds))
            return false;

        if (HasBrickOverlap(_baseBricks, bounds))
        {
            int bx = bounds.MinX / BrickSize;
            int by = bounds.MinY / BrickSize;
            int bz = bounds.MinZ / BrickSize;
            if ((bounds.MaxX - 1) / BrickSize != bx ||
                (bounds.MaxY - 1) / BrickSize != by ||
                (bounds.MaxZ - 1) / BrickSize != bz ||
                !_baseBricks.TryGetValue(new BrickKey(bx, by, bz), out BaseBrick? brick) ||
                !brick.IsUniform)
                return false;

            sample = brick.UniformSample;
            return true;
        }

        int geometryId = -1;
        for (int i = 0; i < _geometry.Count; i++)
        {
            GeometryOperation operation = _geometry[i];
            if (!Overlaps(operation.Bounds, bounds))
                continue;

            if (geometryId >= 0)
                return false;

            geometryId = i;
        }

        if (geometryId < 0)
        {
            if (_baseSource == null)
            {
                sample = new VoxelSample(Quantize(-DensityClampMeters), 0);
                return true;
            }

            for (int i = 0; i < _materials.Count; i++)
                if (Overlaps(_materials[i].Bounds, bounds))
                    return false;

            if (!_baseSource.TryGetUniformRegion(bounds, out VoxelSample sourceUniform))
                return false;

            short sourceDensity = Quantize(Dequantize(sourceUniform.Density));
            sample = new VoxelSample(
                sourceDensity,
                sourceDensity > 0 ? sourceUniform.MaterialId : (ushort)0);
            return true;
        }

        if (_baseSource != null)
            return false;

        GeometryOperation geometry = _geometry[geometryId];
        if (geometry.Operation == GeometryOperationKind.Difference)
        {
            sample = new VoxelSample(Quantize(-DensityClampMeters), 0);
            return true;
        }

        if (geometry.Shape != ShapeKind.Box ||
            !IsRegionInsideBox(bounds, geometry.A, geometry.B, DensityClampMeters))
            return false;

        ushort materialId = geometry.MaterialId;
        for (int i = 0; i < _materials.Count; i++)
        {
            MaterialOperation operation = _materials[i];
            if (!Overlaps(operation.Bounds, bounds))
                continue;

            if (operation.Shape != ShapeKind.Box ||
                !IsRegionInsideBox(bounds, operation.A, operation.B, margin: 0f))
                return false;

            materialId = operation.MaterialId;
        }

        sample = new VoxelSample(Quantize(DensityClampMeters), materialId);
        return true;
    }

    /// <summary>Reads only the geometry channel into a caller-owned dense buffer.</summary>
    public void ReadDensityRegion(VoxelBounds bounds, Span<short> destination)
    {
        ValidateBounds(bounds);
        int count = checked(bounds.SizeX * bounds.SizeY * bounds.SizeZ);
        if (destination.Length != count)
            throw new ArgumentException("Density destination length does not match region.", nameof(destination));
        if (count == 0) return;
        if (TryGetUniformRegion(bounds, out VoxelSample uniform))
        {
            destination.Fill(uniform.Density);
            return;
        }
        if (CanReadBaseSourceDirect(bounds))
        {
            _baseSource!.ReadDensityRegion(bounds, destination);
            ClampSourceDensity(destination);
            return;
        }

        int sx = bounds.SizeX;
        int sy = bounds.SizeY;
        for (int bz = bounds.MinZ / BrickSize; bz <= (bounds.MaxZ - 1) / BrickSize; bz++)
        for (int by = bounds.MinY / BrickSize; by <= (bounds.MaxY - 1) / BrickSize; by++)
        for (int bx = bounds.MinX / BrickSize; bx <= (bounds.MaxX - 1) / BrickSize; bx++)
        {
            BrickKey key = new(bx, by, bz);
            _editBricks.TryGetValue(key, out EditBrick? edit);
            _baseBricks.TryGetValue(key, out BaseBrick? baseBrick);

            int ox = bx * BrickSize;
            int oy = by * BrickSize;
            int oz = bz * BrickSize;
            int x0 = Math.Max(bounds.MinX, ox);
            int y0 = Math.Max(bounds.MinY, oy);
            int z0 = Math.Max(bounds.MinZ, oz);
            int x1 = Math.Min(bounds.MaxX, ox + BrickSize);
            int y1 = Math.Min(bounds.MaxY, oy + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, oz + BrickSize);
            int rowLength = x1 - x0;
            short[]? density = edit?.Density ?? baseBrick?.Density;
            List<int>? candidates = baseBrick == null
                ? GetBucketCandidates(_geometryBuckets, ox, oy, oz)
                : null;

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            {
                int dst = (x0 - bounds.MinX) +
                    sx * ((y - bounds.MinY) + sy * (z - bounds.MinZ));
                int src = GetLocalIndex(ox, oy, oz, x0, y, z);

                if (density != null)
                {
                    density.AsSpan(src, rowLength).CopyTo(destination.Slice(dst, rowLength));
                    continue;
                }

                if (baseBrick?.IsUniform == true && edit?.Density == null)
                {
                    destination.Slice(dst, rowLength).Fill(baseBrick.UniformSample.Density);
                    continue;
                }

                for (int x = x0; x < x1; x++)
                    destination[dst++] = baseBrick?.IsUniform == true
                        ? baseBrick.UniformSample.Density
                        : EvaluateBaseDensity(x, y, z, candidates);
            }
        }
    }

    /// <summary>
    /// Reads only material values. Density is consulted only to mask cells that
    /// are currently air; no geometry data is copied to the caller.
    /// </summary>
    public void ReadMaterialRegion(VoxelBounds bounds, Span<ushort> destination)
    {
        ValidateBounds(bounds);
        int count = checked(bounds.SizeX * bounds.SizeY * bounds.SizeZ);
        if (destination.Length != count)
            throw new ArgumentException("Material destination length does not match region.", nameof(destination));
        if (count == 0) return;
        if (TryGetUniformRegion(bounds, out VoxelSample uniform))
        {
            destination.Fill(uniform.MaterialId);
            return;
        }
        if (CanReadBaseSourceDirect(bounds))
        {
            _baseSource!.ReadMaterialRegion(bounds, destination);
            return;
        }

        int sx = bounds.SizeX;
        int sy = bounds.SizeY;
        for (int bz = bounds.MinZ / BrickSize; bz <= (bounds.MaxZ - 1) / BrickSize; bz++)
        for (int by = bounds.MinY / BrickSize; by <= (bounds.MaxY - 1) / BrickSize; by++)
        for (int bx = bounds.MinX / BrickSize; bx <= (bounds.MaxX - 1) / BrickSize; bx++)
        {
            BrickKey key = new(bx, by, bz);
            _editBricks.TryGetValue(key, out EditBrick? edit);
            _baseBricks.TryGetValue(key, out BaseBrick? baseBrick);

            int ox = bx * BrickSize;
            int oy = by * BrickSize;
            int oz = bz * BrickSize;
            int x0 = Math.Max(bounds.MinX, ox);
            int y0 = Math.Max(bounds.MinY, oy);
            int z0 = Math.Max(bounds.MinZ, oz);
            int x1 = Math.Min(bounds.MaxX, ox + BrickSize);
            int y1 = Math.Min(bounds.MaxY, oy + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, oz + BrickSize);
            int rowLength = x1 - x0;

            short[]? editDensity = edit?.Density;
            ushort[]? editMaterial = edit?.Material;
            List<int>? geometryCandidates = baseBrick == null
                ? GetBucketCandidates(_geometryBuckets, ox, oy, oz)
                : null;
            List<int>? materialCandidates = baseBrick == null && editMaterial == null
                ? GetBucketCandidates(_materialBuckets, ox, oy, oz)
                : null;

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            {
                int dst = (x0 - bounds.MinX) +
                    sx * ((y - bounds.MinY) + sy * (z - bounds.MinZ));
                int src = GetLocalIndex(ox, oy, oz, x0, y, z);

                if (editDensity == null && editMaterial == null && baseBrick?.IsUniform == true)
                {
                    ushort material = baseBrick.UniformSample.IsSolid
                        ? baseBrick.UniformSample.MaterialId
                        : (ushort)0;
                    destination.Slice(dst, rowLength).Fill(material);
                    continue;
                }

                if (editDensity == null && editMaterial == null &&
                    baseBrick != null && !baseBrick.IsUniform)
                {
                    baseBrick.Material!.AsSpan(src, rowLength)
                        .CopyTo(destination.Slice(dst, rowLength));
                    for (int i = 0; i < rowLength; i++)
                        if (baseBrick.Density![src + i] <= 0)
                            destination[dst + i] = 0;
                    continue;
                }

                if (baseBrick?.IsUniform == true && editDensity == null && editMaterial != null)
                {
                    if (baseBrick.UniformSample.IsSolid)
                        editMaterial.AsSpan(src, rowLength).CopyTo(destination.Slice(dst, rowLength));
                    else
                        destination.Slice(dst, rowLength).Clear();
                    continue;
                }

                if (baseBrick?.IsUniform == true && editDensity != null && editMaterial == null)
                {
                    if (baseBrick.UniformSample.IsSolid)
                        destination.Slice(dst, rowLength).Fill(baseBrick.UniformSample.MaterialId);
                    else
                        destination.Slice(dst, rowLength).Clear();
                    for (int i = 0; i < rowLength; i++)
                        if (editDensity[src + i] <= 0)
                            destination[dst + i] = 0;
                    continue;
                }

                if (editDensity != null && editMaterial != null)
                {
                    editMaterial.AsSpan(src, rowLength).CopyTo(destination.Slice(dst, rowLength));
                    for (int i = 0; i < rowLength; i++)
                        if (editDensity[src + i] <= 0)
                            destination[dst + i] = 0;
                    continue;
                }

                for (int x = x0; x < x1; x++)
                {
                    int local = src + (x - x0);
                    VoxelSample baseSample = baseBrick != null
                        ? baseBrick.IsUniform
                            ? baseBrick.UniformSample
                            : new VoxelSample(baseBrick.Density![local], baseBrick.Material![local])
                        : EvaluateBaseVoxel(x, y, z, geometryCandidates, materialCandidates);

                    short density = editDensity != null
                        ? editDensity[local]
                        : baseSample.Density;

                    ushort material = density <= 0
                        ? (ushort)0
                        : editMaterial != null
                            ? editMaterial[local]
                            : baseSample.MaterialId;

                    destination[dst++] = material;
                }
            }
        }
    }

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
        int count = checked(sizeX * sizeY * bounds.SizeZ);

        if (densityDestination.Length != count)
            throw new ArgumentException("Density destination length does not match region.", nameof(densityDestination));
        if (materialDestination.Length != count)
            throw new ArgumentException("Material destination length does not match region.", nameof(materialDestination));
        if (count == 0) return;
        if (TryGetUniformRegion(bounds, out VoxelSample uniform))
        {
            densityDestination.Fill(uniform.Density);
            materialDestination.Fill(uniform.MaterialId);
            return;
        }
        if (CanReadBaseSourceDirect(bounds))
        {
            _baseSource!.ReadRegion(bounds, densityDestination, materialDestination);
            NormalizeSourceRegion(densityDestination, materialDestination);
            return;
        }

        for (int bz = bounds.MinZ / BrickSize; bz <= (bounds.MaxZ - 1) / BrickSize; bz++)
        for (int by = bounds.MinY / BrickSize; by <= (bounds.MaxY - 1) / BrickSize; by++)
        for (int bx = bounds.MinX / BrickSize; bx <= (bounds.MaxX - 1) / BrickSize; bx++)
        {
            BrickKey key = new(bx, by, bz);
            _editBricks.TryGetValue(key, out EditBrick? edit);
            _baseBricks.TryGetValue(key, out BaseBrick? baseBrick);

            int ox = bx * BrickSize;
            int oy = by * BrickSize;
            int oz = bz * BrickSize;
            int x0 = Math.Max(bounds.MinX, ox);
            int y0 = Math.Max(bounds.MinY, oy);
            int z0 = Math.Max(bounds.MinZ, oz);
            int x1 = Math.Min(bounds.MaxX, ox + BrickSize);
            int y1 = Math.Min(bounds.MaxY, oy + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, oz + BrickSize);
            int rowLength = x1 - x0;

            short[]? editDensity = edit?.Density;
            ushort[]? editMaterial = edit?.Material;
            List<int>? geometryCandidates = baseBrick == null
                ? GetBucketCandidates(_geometryBuckets, ox, oy, oz)
                : null;
            List<int>? materialCandidates = baseBrick == null && editMaterial == null
                ? GetBucketCandidates(_materialBuckets, ox, oy, oz)
                : null;

            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            {
                int src = GetLocalIndex(ox, oy, oz, x0, y, z);
                int dst =
                    (x0 - bounds.MinX) +
                    sizeX * ((y - bounds.MinY) + sizeY * (z - bounds.MinZ));

                if (editDensity == null && editMaterial == null && baseBrick?.IsUniform == true)
                {
                    densityDestination.Slice(dst, rowLength).Fill(baseBrick.UniformSample.Density);
                    ushort material = baseBrick.UniformSample.IsSolid
                        ? baseBrick.UniformSample.MaterialId
                        : (ushort)0;
                    materialDestination.Slice(dst, rowLength).Fill(material);
                    continue;
                }

                if (editDensity == null && editMaterial == null &&
                    baseBrick != null && !baseBrick.IsUniform)
                {
                    baseBrick.Density!.AsSpan(src, rowLength)
                        .CopyTo(densityDestination.Slice(dst, rowLength));
                    baseBrick.Material!.AsSpan(src, rowLength)
                        .CopyTo(materialDestination.Slice(dst, rowLength));
                    for (int i = 0; i < rowLength; i++)
                        if (densityDestination[dst + i] <= 0)
                            materialDestination[dst + i] = 0;
                    continue;
                }

                if (baseBrick?.IsUniform == true && editDensity == null && editMaterial != null)
                {
                    densityDestination.Slice(dst, rowLength).Fill(baseBrick.UniformSample.Density);
                    if (baseBrick.UniformSample.IsSolid)
                        editMaterial.AsSpan(src, rowLength).CopyTo(materialDestination.Slice(dst, rowLength));
                    else
                        materialDestination.Slice(dst, rowLength).Clear();
                    continue;
                }

                if (baseBrick?.IsUniform == true && editDensity != null && editMaterial == null)
                {
                    editDensity.AsSpan(src, rowLength).CopyTo(densityDestination.Slice(dst, rowLength));
                    if (baseBrick.UniformSample.IsSolid)
                        materialDestination.Slice(dst, rowLength).Fill(baseBrick.UniformSample.MaterialId);
                    else
                        materialDestination.Slice(dst, rowLength).Clear();
                    for (int i = 0; i < rowLength; i++)
                        if (densityDestination[dst + i] <= 0)
                            materialDestination[dst + i] = 0;
                    continue;
                }

                if (editDensity != null && editMaterial != null)
                {
                    editDensity.AsSpan(src, rowLength).CopyTo(densityDestination.Slice(dst, rowLength));
                    editMaterial.AsSpan(src, rowLength).CopyTo(materialDestination.Slice(dst, rowLength));
                    for (int i = 0; i < rowLength; i++)
                        if (densityDestination[dst + i] <= 0)
                            materialDestination[dst + i] = 0;
                    continue;
                }

                for (int x = x0; x < x1; x++)
                {
                    int local = src + (x - x0);
                    VoxelSample baseSample = baseBrick != null
                        ? baseBrick.IsUniform
                            ? baseBrick.UniformSample
                            : new VoxelSample(baseBrick.Density![local], baseBrick.Material![local])
                        : EvaluateBaseVoxel(x, y, z, geometryCandidates, materialCandidates);

                    short density = editDensity != null
                        ? editDensity[local]
                        : baseSample.Density;
                    ushort material = density <= 0
                        ? (ushort)0
                        : editMaterial != null
                            ? editMaterial[local]
                            : baseSample.MaterialId;

                    densityDestination[dst] = density;
                    materialDestination[dst] = material;
                    dst++;
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
            add: true, materialId: materialId);
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
            add: true, materialId: materialId);
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
            materialId: materialId);
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

        BrickKey key = GetBrickKey(x, y, z);
        EditBrick brick = GetOrCreateBrick(key);
        EnsureDensity(brick, key);

        int index = GetLocalIndex(x, y, z);
        short previousDensity = brick.Density![index];
        density = Quantize(Dequantize(density));
        brick.Density[index] = density;

        if (density <= 0)
        {
            if (previousDensity > 0 && brick.Material == null)
                EnsureMaterial(brick, key);
            if (brick.Material != null)
                brick.Material[index] = 0;
        }

        Revision++;
    }

    public void SetMaterial(int x, int y, int z, ushort materialId)
    {
        ValidateVoxelCoordinate(x, y, z);
        SealBase();

        BrickKey key = GetBrickKey(x, y, z);
        EditBrick brick = GetOrCreateBrick(key);
        int index = GetLocalIndex(x, y, z);
        short density = brick.Density != null
            ? brick.Density[index]
            : _baseBricks.TryGetValue(key, out BaseBrick? baseBrick)
                ? baseBrick.IsUniform
                    ? baseBrick.UniformSample.Density
                    : baseBrick.Density![index]
                : EvaluateBaseDensity(
                    x, y, z,
                    GetBucketCandidates(_geometryBuckets, x, y, z));

        EnsureMaterial(brick, key);
        brick.Material![index] = density > 0 ? materialId : (ushort)0;
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
            _editBricks.TryGetValue(key, out EditBrick? existingEdit);
            _baseBricks.TryGetValue(key, out BaseBrick? baseBrick);
            List<int>? baseCandidates = baseBrick == null
                ? GetBucketCandidates(
                    _geometryBuckets,
                    bx * BrickSize,
                    by * BrickSize,
                    bz * BrickSize)
                : null;

            if (_baseSource == null &&
                !add &&
                existingEdit?.Density == null &&
                (baseBrick == null || baseBrick.IsUniform && !baseBrick.UniformSample.IsSolid) &&
                !HasSolidBaseCandidate(baseCandidates))
                continue;

            EditBrick brick = existingEdit ?? GetOrCreateBrick(key);
            EnsureDensity(brick, key);
            if (add) EnsureMaterial(brick, key);

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
                float sdf = EvaluateInsideDistance(
                    shape, VoxelCenter(x, y, z), a, b, radius);
                int index = GetLocalIndex(ox, oy, oz, x, y, z);
                float current = Dequantize(brick.Density![index]);
                float next = add
                    ? MathF.Max(current, sdf)
                    : MathF.Min(current, -sdf);

                short updatedDensity = Quantize(next);
                brick.Density[index] = updatedDensity;

                if (updatedDensity <= 0)
                {
                    if (current > 0f && brick.Material == null)
                        EnsureMaterial(brick, key);
                    if (brick.Material != null)
                        brick.Material[index] = 0;
                }
                else if (add && sdf > 0f && sdf > current)
                {
                    if (brick.Material == null)
                        EnsureMaterial(brick, key);
                    brick.Material[index] = materialId;
                }
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
            _editBricks.TryGetValue(key, out EditBrick? existingEdit);
            _baseBricks.TryGetValue(key, out BaseBrick? baseBrick);

            int ox = bx * BrickSize;
            int oy = by * BrickSize;
            int oz = bz * BrickSize;
            List<int>? geometryCandidates = baseBrick == null
                ? GetBucketCandidates(_geometryBuckets, ox, oy, oz)
                : null;

            if (_baseSource == null &&
                existingEdit?.Density == null &&
                (baseBrick == null || baseBrick.IsUniform && !baseBrick.UniformSample.IsSolid) &&
                !HasSolidBaseCandidate(geometryCandidates))
                continue;

            int x0 = Math.Max(bounds.MinX, ox);
            int y0 = Math.Max(bounds.MinY, oy);
            int z0 = Math.Max(bounds.MinZ, oz);
            int x1 = Math.Min(bounds.MaxX, ox + BrickSize);
            int y1 = Math.Min(bounds.MaxY, oy + BrickSize);
            int z1 = Math.Min(bounds.MaxZ, oz + BrickSize);

            EditBrick? brick = existingEdit;
            for (int z = z0; z < z1; z++)
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                if (EvaluateInsideDistance(
                        shape, VoxelCenter(x, y, z), a, b, radius) < 0f)
                    continue;

                int localIndex = GetLocalIndex(ox, oy, oz, x, y, z);
                short density = brick?.Density != null
                    ? brick.Density[localIndex]
                    : baseBrick != null
                        ? baseBrick.IsUniform
                            ? baseBrick.UniformSample.Density
                            : baseBrick.Density![localIndex]
                        : EvaluateBaseDensity(x, y, z, geometryCandidates);

                if (density <= 0)
                    continue;

                brick ??= GetOrCreateBrick(key);
                if (brick.Material == null)
                    EnsureMaterial(brick, key);
                brick.Material![localIndex] = materialId;
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
        VoxelSample sourceSample = _baseSource?.Sample(x, y, z) ??
            new VoxelSample(Quantize(-DensityClampMeters), 0);
        float density = Dequantize(Quantize(Dequantize(sourceSample.Density)));
        ushort materialId = density > 0f ? sourceSample.MaterialId : (ushort)0;
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
        float density = _baseSource == null
            ? -DensityClampMeters
            : Dequantize(Quantize(Dequantize(_baseSource.Sample(x, y, z).Density)));
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
        if (_baseBricks.TryGetValue(key, out BaseBrick? baseBrick))
        {
            if (baseBrick.IsUniform)
                Array.Fill(values, baseBrick.UniformSample.Density);
            else
                Array.Copy(baseBrick.Density!, values, BrickCellCount);
            brick.Density = values;
            return;
        }

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
        if (_baseBricks.TryGetValue(key, out BaseBrick? baseBrick))
        {
            if (baseBrick.IsUniform)
                Array.Fill(values, baseBrick.UniformSample.MaterialId);
            else
                Array.Copy(baseBrick.Material!, values, BrickCellCount);
            brick.Material = values;
            return;
        }

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

    private bool HasSolidBaseCandidate(List<int>? candidates)
    {
        if (candidates == null)
            return false;

        for (int i = 0; i < candidates.Count; i++)
            if (_geometry[candidates[i]].Operation == GeometryOperationKind.Union)
                return true;

        return false;
    }

    private static bool HasBrickOverlap<T>(
        Dictionary<BrickKey, T> bricks,
        VoxelBounds bounds)
    {
        if (bricks.Count == 0 || bounds.IsEmpty)
            return false;

        int minX = bounds.MinX / BrickSize;
        int minY = bounds.MinY / BrickSize;
        int minZ = bounds.MinZ / BrickSize;
        int maxX = (bounds.MaxX - 1) / BrickSize;
        int maxY = (bounds.MaxY - 1) / BrickSize;
        int maxZ = (bounds.MaxZ - 1) / BrickSize;
        long queryBrickCount = (long)(maxX - minX + 1) *
            (maxY - minY + 1) * (maxZ - minZ + 1);

        if (queryBrickCount <= bricks.Count)
        {
            for (int z = minZ; z <= maxZ; z++)
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                if (bricks.ContainsKey(new BrickKey(x, y, z)))
                    return true;
            return false;
        }

        foreach (BrickKey key in bricks.Keys)
        {
            if (key.X >= minX && key.X <= maxX &&
                key.Y >= minY && key.Y <= maxY &&
                key.Z >= minZ && key.Z <= maxZ)
                return true;
        }

        return false;
    }

    private static bool Overlaps(VoxelBounds a, VoxelBounds b) =>
        a.MinX < b.MaxX && a.MaxX > b.MinX &&
        a.MinY < b.MaxY && a.MaxY > b.MinY &&
        a.MinZ < b.MaxZ && a.MaxZ > b.MinZ;

    private bool IsRegionInsideBox(
        VoxelBounds bounds,
        Vector3 center,
        Vector3 halfExtents,
        float margin)
    {
        float minX = (bounds.MinX + 0.5f) * VoxelSizeMeters;
        float minY = (bounds.MinY + 0.5f) * VoxelSizeMeters;
        float minZ = (bounds.MinZ + 0.5f) * VoxelSizeMeters;
        float maxX = (bounds.MaxX - 0.5f) * VoxelSizeMeters;
        float maxY = (bounds.MaxY - 0.5f) * VoxelSizeMeters;
        float maxZ = (bounds.MaxZ - 0.5f) * VoxelSizeMeters;

        return minX - (center.X - halfExtents.X) >= margin &&
            minY - (center.Y - halfExtents.Y) >= margin &&
            minZ - (center.Z - halfExtents.Z) >= margin &&
            (center.X + halfExtents.X) - maxX >= margin &&
            (center.Y + halfExtents.Y) - maxY >= margin &&
            (center.Z + halfExtents.Z) - maxZ >= margin;
    }

    private bool CanReadBaseSourceDirect(VoxelBounds bounds) =>
        _baseSource != null &&
        !HasBrickOverlap(_baseBricks, bounds) &&
        !HasBrickOverlap(_editBricks, bounds) &&
        !HasBaseOperationOverlap(bounds);

    private bool HasBaseOperationOverlap(VoxelBounds bounds)
    {
        for (int i = 0; i < _geometry.Count; i++)
            if (Overlaps(_geometry[i].Bounds, bounds))
                return true;

        for (int i = 0; i < _materials.Count; i++)
            if (Overlaps(_materials[i].Bounds, bounds))
                return true;

        return false;
    }

    private void ClampSourceDensity(Span<short> density)
    {
        for (int i = 0; i < density.Length; i++)
            density[i] = Quantize(Dequantize(density[i]));
    }

    private void NormalizeSourceRegion(
        Span<short> density,
        Span<ushort> material)
    {
        for (int i = 0; i < density.Length; i++)
        {
            density[i] = Quantize(Dequantize(density[i]));
            if (density[i] <= 0)
                material[i] = 0;
        }
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

    private short Quantize(float densityMeters)
    {
        float clamped = Math.Clamp(
            densityMeters,
            -DensityClampMeters,
            DensityClampMeters);
        return (short)MathF.Round(clamped * DensityUnitsPerMeter);
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
