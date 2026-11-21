using System;

namespace Core.Map;

/// <summary>
/// Shared physical description for voxels. Occupancy and density live in the
/// voxel field; material properties are stored once and referenced by ushort ID.
/// </summary>
public readonly record struct VoxelMaterialDefinition(
    ushort Id,
    string Name,
    float MassDensityKgPerM3,
    float Hardness,
    float CompressiveStrengthMpa,
    float Porosity,
    float ThermalConductivityWPerMK)
{
    public float MassPerVoxelKg =>
        MassDensityKgPerM3 *
        SparseVoxelField.VoxelSizeMeters *
        SparseVoxelField.VoxelSizeMeters *
        SparseVoxelField.VoxelSizeMeters;
}

/// <summary>
/// Canonical terrain material catalogue. Material IDs remain compact in voxel
/// chunks; physical values are shared by every cell with the same ID.
/// </summary>
public static class VoxelMaterialCatalog
{
    private static readonly VoxelMaterialDefinition[] Definitions =
    {
        new(0, "Air", 0f, 0f, 0f, 1f, 0f),
        new(1, "Base rock", 2_700f, 6.5f, 85f, 0.025f, 2.7f),
        new(2, "Sedimentary rock", 2_200f, 3.5f, 28f, 0.16f, 1.5f),
        new(3, "Ore-bearing rock", 3_250f, 6.0f, 105f, 0.055f, 3.1f)
    };

    public static ReadOnlySpan<VoxelMaterialDefinition> All => Definitions;

    public static VoxelMaterialDefinition Get(ushort materialId)
    {
        int index = materialId < Definitions.Length
            ? materialId
            : 0;
        return Definitions[index];
    }

    public static bool TryGet(ushort materialId, out VoxelMaterialDefinition material)
    {
        if (materialId < Definitions.Length)
        {
            material = Definitions[materialId];
            return true;
        }

        material = Definitions[0];
        return false;
    }
}
