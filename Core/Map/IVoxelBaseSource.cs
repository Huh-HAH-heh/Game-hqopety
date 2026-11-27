using System.IO;
using Core.Map;

namespace Core.Map;

/// <summary>
/// Immutable source of the unmodified voxel volume. Implementations must be deterministic,
/// thread-safe for reads and avoid per-sample allocations. Density uses the storage scale:
/// positive is solid, negative is air, and values are truncated to the configured SDF band.
/// </summary>
public interface IVoxelBaseSource
{
    string TypeId { get; }
    ushort DataVersion { get; }

    int Width { get; }
    int Height { get; }
    int Depth { get; }
    float VoxelSizeMeters { get; }

    VoxelVolumeStore.VoxelSample Sample(int x, int y, int z);

    void WriteState(BinaryWriter writer);

    bool TryGetUniformRegion(
        VoxelVolumeStore.VoxelBounds bounds,
        out VoxelVolumeStore.VoxelSample sample)
    {
        sample = default;
        return false;
    }

    void ReadRegion(
        VoxelVolumeStore.VoxelBounds bounds,
        Span<short> density,
        Span<ushort> material)
    {
        int sizeX = bounds.SizeX;
        int sizeY = bounds.SizeY;
        int index = 0;

        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            VoxelVolumeStore.VoxelSample sample = Sample(x, y, z);
            density[index] = sample.Density;
            material[index] = sample.Density > 0 ? sample.MaterialId : (ushort)0;
            index++;
        }
    }

    void ReadDensityRegion(
        VoxelVolumeStore.VoxelBounds bounds,
        Span<short> density)
    {
        int index = 0;
        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
            density[index++] = Sample(x, y, z).Density;
    }

    void ReadMaterialRegion(
        VoxelVolumeStore.VoxelBounds bounds,
        Span<ushort> material)
    {
        int index = 0;
        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            VoxelVolumeStore.VoxelSample sample = Sample(x, y, z);
            material[index++] = sample.Density > 0 ? sample.MaterialId : (ushort)0;
        }
    }
}

public delegate IVoxelBaseSource VoxelBaseSourceResolver(
    string typeId,
    ushort dataVersion,
    int width,
    int height,
    int depth,
    float voxelSizeMeters,
    BinaryReader reader);
