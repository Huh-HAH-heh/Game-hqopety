namespace Core.Map;

/// <summary>
/// Read-only voxel source shared by the world and immutable detached fragments.
/// Renderer workers consume byte block IDs; simulation may still query density.
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
