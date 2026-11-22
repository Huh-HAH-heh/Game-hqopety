using System;
using System.Numerics;

namespace Core.Map;

/// <summary>
/// Immutable local snapshot of a detached voxel component.
/// The one-sample air border is included so the normal voxel mesher can build
/// a closed surface without reading the mutable source world.
/// </summary>
public sealed class VoxelFragmentSnapshot
{
    private readonly float[] _density;
    private readonly ushort[] _materials;

    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public Vector3 OriginMeters { get; }
    public Vector3 LocalPivotMeters { get; }
    public Vector3 InitialCenterMeters => OriginMeters + LocalPivotMeters;
    public Vector3 HalfExtentsMeters => new(
        (Width - 2) * SparseVoxelField.VoxelSizeMeters * 0.5f,
        (Height - 2) * SparseVoxelField.VoxelSizeMeters * 0.5f,
        (Depth - 2) * SparseVoxelField.VoxelSizeMeters * 0.5f);
    public float MassKg { get; }

    internal VoxelFragmentSnapshot(
        float[] density,
        ushort[] materials,
        int width,
        int height,
        int depth,
        Vector3 originMeters,
        float massKg)
    {
        ArgumentNullException.ThrowIfNull(density);
        ArgumentNullException.ThrowIfNull(materials);
        if (width < 3 || height < 3 || depth < 3)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (density.Length != checked(width * height * depth) ||
            materials.Length != density.Length)
            throw new ArgumentException("Fragment arrays do not match their dimensions.");

        _density = density;
        _materials = materials;
        Width = width;
        Height = height;
        Depth = depth;
        OriginMeters = originMeters;
        LocalPivotMeters = new Vector3(width, height, depth) *
            (SparseVoxelField.VoxelSizeMeters * 0.5f);
        MassKg = MathF.Max(0.01f, massKg);
    }

    /// <summary>
    /// Creates an immutable sampling view over this fragment. The local field
    /// has no allocated edit chunks; all samples come from this owned snapshot.
    /// </summary>
    internal ReadOnlySpan<float> DensitySamples => _density;
    internal ReadOnlySpan<ushort> MaterialSamples => _materials;

    internal SparseVoxelField CreateField()
    {
        float[] density = _density;
        ushort[] materials = _materials;
        int width = Width;
        int height = Height;
        int depth = Depth;
        int plane = width * height;

        return new SparseVoxelField(
            width,
            height,
            depth,
            (x, y, z) =>
            {
                if (x < 0 || y < 0 || z < 0 ||
                    x >= width || y >= height || z >= depth)
                    return -1f;
                return density[x + y * width + z * plane];
            },
            (x, y, z) =>
            {
                if (x < 0 || y < 0 || z < 0 ||
                    x >= width || y >= height || z >= depth)
                    return 0;
                return materials[x + y * width + z * plane];
            });
    }
}
