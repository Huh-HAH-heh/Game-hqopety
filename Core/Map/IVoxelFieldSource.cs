namespace Core.Map;

/// <summary>
/// Read-only signed-density voxel source shared by the world and immutable
/// detached fragments. The renderer samples density for Surface Nets and
/// reads material IDs only when assigning surface colours.
/// </summary>
public interface IVoxelFieldSource
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }

    float GetDensity(int x, int y, int z);
    ushort GetMaterialId(int x, int y, int z);

    /// <summary>
    /// Returns zero for air and a compact byte material ID for a solid cell.
    /// Terrain IDs used by the renderer must fit in one byte.
    /// </summary>
    byte GetBlockId(int x, int y, int z) =>
        checked((byte)GetMaterialId(x, y, z));
}


/// <summary>
/// Optional fast path for heightfield-backed sources. Implementations fill a
/// complete vertical density column, including sparse voxel overrides.
/// </summary>
public interface IVoxelColumnDensitySource
{
    bool TryGetDensityColumn(
        int x,
        int z,
        int startY,
        int step,
        Span<float> destination);
}
