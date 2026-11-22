namespace Core.Map;

/// <summary>
/// Read-only local or world-space voxel sampling contract used by the common
/// surface extractor. Implementations must use the same 0.1 m sample lattice.
/// </summary>
public interface IVoxelFieldSource
{
    int Width { get; }
    int Height { get; }
    int Depth { get; }
    float GetDensity(int x, int y, int z);
    ushort GetMaterialId(int x, int y, int z);
}
