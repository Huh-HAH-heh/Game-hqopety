using System.Numerics;

namespace Core.Map;

public static class FlatVoxelPageDirectorySelfTest
{
    public static bool Run() =>
        FlatDirectoryUsesLinearAddressing() &&
        VoxelReadsCrossDataCellBoundaries();

    private static bool FlatDirectoryUsesLinearAddressing()
    {
        FlatVoxelPageDirectory<object> pages = new(3, 2, 3);
        object origin = new();
        object x = new();
        object y = new();
        object z = new();

        if (pages.Length != 18 ||
            pages.GetLinearIndex(2, 1, 2) != 17 ||
            pages.GetLinearIndex(1, 0, 0) != 1 ||
            pages.GetLinearIndex(0, 1, 0) != 3 ||
            pages.GetLinearIndex(0, 0, 1) != 6)
        {
            return false;
        }

        pages.Set(0, 0, 0, origin);
        pages.Set(1, 0, 0, x);
        pages.Set(0, 1, 0, y);
        pages.Set(0, 0, 1, z);

        if (!pages.TryGet(0, 0, 0, out object? readOrigin) ||
            !ReferenceEquals(readOrigin, origin) ||
            !pages.TryGet(1, 0, 0, out object? readX) ||
            !ReferenceEquals(readX, x) ||
            !pages.TryGet(0, 1, 0, out object? readY) ||
            !ReferenceEquals(readY, y) ||
            !pages.TryGet(0, 0, 1, out object? readZ) ||
            !ReferenceEquals(readZ, z))
        {
            return false;
        }

        if (pages.TryGet(3, 0, 0, out _) ||
            !pages.Remove(0, 1, 0) ||
            pages.TryGet(0, 1, 0, out _))
        {
            return false;
        }

        pages.Clear();
        return !pages.TryGet(0, 0, 0, out _) &&
            !pages.TryGet(1, 0, 0, out _) &&
            !pages.TryGet(0, 0, 1, out _);
    }

    private static bool VoxelReadsCrossDataCellBoundaries()
    {
        SparseVoxelField field = new(
            width: 24,
            height: 16,
            depth: 24,
            baseDensity: static (_, _, _) => -4f,
            baseMaterial: static (_, _, _) => 0);

        if (field.DataCellCountX != 3 ||
            field.DataCellCountY != 2 ||
            field.DataCellCountZ != 3 ||
            field.DataCellDirectoryLength != 18 ||
            field.GetDataCellLinearIndex(2, 1, 2) != 17)
        {
            return false;
        }

        field.FillBox(
            new Vector3(0.7f, 0.7f, 0.7f),
            new Vector3(0.9f, 0.9f, 0.9f),
            materialId: 7);

        return field.GetMaterialId(7, 7, 7) == 7 &&
            field.GetMaterialId(8, 8, 8) == 7 &&
            field.GetBlockId(7, 7, 7) == 7 &&
            field.GetBlockId(8, 8, 8) == 7 &&
            field.GetMaterialId(6, 7, 7) == 0 &&
            field.GetDataCellStorageBytes(0, 0, 0) > 0 &&
            field.GetDataCellStorageBytes(1, 1, 1) > 0;
    }
}
