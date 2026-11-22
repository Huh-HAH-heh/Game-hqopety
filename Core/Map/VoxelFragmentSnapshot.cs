using System;
using System.Numerics;

namespace Core.Map;

/// <summary>
/// Compact immutable local fragment on the world's shared 0.1 m sample grid.
/// Density is quantized to the same signed 16-bit format as the main field;
/// material is stored once per sample cell. No secondary SparseVoxelField is
/// allocated for rendering or simulation.
/// </summary>
public sealed class VoxelFragmentSnapshot : IVoxelFieldSource
{
    private readonly short[] _density;
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
    public int SampleCount => _density.Length;
    public long EstimatedBytes =>
        (long)_density.Length * (sizeof(short) + sizeof(ushort));
    public float MassKg { get; }

    internal VoxelFragmentSnapshot(
        short[] density,
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

    public float GetDensity(int x, int y, int z)
    {
        if (!IsInside(x, y, z))
            return -1f;

        return _density[x + y * Width + z * Width * Height] /
            (float)SparseVoxelField.DensityScale;
    }

    public ushort GetMaterialId(int x, int y, int z)
    {
        if (!IsInside(x, y, z) || GetDensity(x, y, z) <= 0f)
            return 0;

        return _materials[x + y * Width + z * Width * Height];
    }

    /// <summary>
    /// Trilinearly samples local metres measured from the fragment's lower
    /// corner. Source and destination use voxel-centre coordinates.
    /// </summary>
    public float SampleDensityLocal(Vector3 localPositionMeters)
    {
        if (!float.IsFinite(localPositionMeters.X) ||
            !float.IsFinite(localPositionMeters.Y) ||
            !float.IsFinite(localPositionMeters.Z))
            return -1f;

        float sx = localPositionMeters.X / SparseVoxelField.VoxelSizeMeters - 0.5f;
        float sy = localPositionMeters.Y / SparseVoxelField.VoxelSizeMeters - 0.5f;
        float sz = localPositionMeters.Z / SparseVoxelField.VoxelSizeMeters - 0.5f;
        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        int z0 = (int)MathF.Floor(sz);
        float tx = sx - x0;
        float ty = sy - y0;
        float tz = sz - z0;

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

    public ushort GetMaterialAtLocalPosition(Vector3 localPositionMeters)
    {
        int x = (int)MathF.Round(
            localPositionMeters.X / SparseVoxelField.VoxelSizeMeters - 0.5f);
        int y = (int)MathF.Round(
            localPositionMeters.Y / SparseVoxelField.VoxelSizeMeters - 0.5f);
        int z = (int)MathF.Round(
            localPositionMeters.Z / SparseVoxelField.VoxelSizeMeters - 0.5f);
        return GetMaterialId(x, y, z);
    }

    internal ReadOnlySpan<short> DensitySamples => _density;
    internal ReadOnlySpan<ushort> MaterialSamples => _materials;

    private bool IsInside(int x, int y, int z) =>
        x >= 0 && y >= 0 && z >= 0 &&
        x < Width && y < Height && z < Depth;

    private static float Lerp(float a, float b, float t) =>
        a + (b - a) * t;
}
