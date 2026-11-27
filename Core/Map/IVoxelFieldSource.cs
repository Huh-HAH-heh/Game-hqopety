namespace Core.Map;

public interface IVoxelFieldSource
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }

    float GetDensity(int x, int y, int z);
    ushort GetMaterialId(int x, int y, int z);

    byte GetBlockId(int x, int y, int z) =>
        checked((byte)GetMaterialId(x, y, z));
}

/// <summary>
/// Fills a vertical column from a three-dimensional density sampler.
/// XZ calculations may be shared, but every requested Y sample is evaluated.
/// </summary>
public delegate void VoxelDensityColumnSampler(
    int x,
    int z,
    int startY,
    int step,
    Span<float> destination);

public interface IVoxelColumnDensitySource
{
    bool TryGetDensityColumn(
        int x,
        int z,
        int startY,
        int step,
        Span<float> destination);
}
