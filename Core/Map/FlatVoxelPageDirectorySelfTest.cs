using System.Numerics;

namespace Core.Map;

public static class FlatVoxelPageDirectorySelfTest
{
    public static bool Run() =>
        FlatDirectoryUsesLinearAddressing() &&
        FlatDirectoryCrossesStorageBlockBoundaries() &&
        ProductionDirectoryStaysSparse() &&
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
        return pages.AllocatedBlockCount == 0 &&
            !pages.TryGet(0, 0, 0, out _) &&
            !pages.TryGet(1, 0, 0, out _) &&
            !pages.TryGet(0, 0, 1, out _);
    }

    private static bool FlatDirectoryCrossesStorageBlockBoundaries()
    {
        FlatVoxelPageDirectory<object> pages = new(33, 18, 33);
        object low = new();
        object middle = new();
        object high = new();

        pages.Set(7, 7, 7, low);
        pages.Set(8, 8, 8, middle);
        pages.Set(32, 17, 32, high);

        return pages.AllocatedBlockCount == 3 &&
            pages.TryGet(7, 7, 7, out object? readLow) &&
            ReferenceEquals(readLow, low) &&
            pages.TryGet(8, 8, 8, out object? readMiddle) &&
            ReferenceEquals(readMiddle, middle) &&
            pages.TryGet(32, 17, 32, out object? readHigh) &&
            ReferenceEquals(readHigh, high) &&
            !pages.TryGet(33, 17, 32, out _);
    }

    private static bool ProductionDirectoryStaysSparse()
    {
        // 23040 x 500 x 23040 voxels, with 8^3 data pages.
        FlatVoxelPageDirectory<object> pages = new(2880, 63, 2880);
        long rootBytes = 360L * 8 * 360 * IntPtr.Size;

        if (pages.Length != 522_547_200 ||
            pages.AllocatedBlockCount != 0 ||
            pages.EstimatedDirectoryBytes != rootBytes ||
            pages.TryGet(1400, 30, 1400, out _) ||
            pages.AllocatedBlockCount != 0)
        {
            return false;
        }

        object pageA = new();
        object pageB = new();
        pages.Set(0, 0, 0, pageA);
        pages.Set(2879, 62, 2879, pageB);

        long expectedBytes = rootBytes + 2L * 512 * IntPtr.Size;
        if (pages.AllocatedBlockCount != 2 ||
            pages.EstimatedDirectoryBytes != expectedBytes ||
            !pages.TryGet(0, 0, 0, out object? readA) ||
            !ReferenceEquals(readA, pageA) ||
            !pages.TryGet(2879, 62, 2879, out object? readB) ||
            !ReferenceEquals(readB, pageB))
        {
            return false;
        }

        pages.Clear();
        return pages.AllocatedBlockCount == 0 &&
            pages.EstimatedDirectoryBytes == rootBytes;
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

        using (field.BeginUndoableEdit())
        {
            field.FillBox(
                new Vector3(0.7f, 0.7f, 0.7f),
                new Vector3(0.9f, 0.9f, 0.9f),
                materialId: 7);
        }

        bool written = field.GetMaterialId(7, 7, 7) == 7 &&
            field.GetMaterialId(8, 8, 8) == 7 &&
            field.GetBlockId(7, 7, 7) == 7 &&
            field.GetBlockId(8, 8, 8) == 7 &&
            field.GetMaterialId(6, 7, 7) == 0 &&
            field.GetDataCellStorageBytes(0, 0, 0) > 0 &&
            field.GetDataCellStorageBytes(1, 1, 1) > 0 &&
            field.DataCellDirectoryAllocatedBlocks > 0;

        if (!written || !field.Undo() ||
            field.GetMaterialId(7, 7, 7) != 0 ||
            field.GetDataCellStorageBytes(0, 0, 0) != 0)
        {
            return false;
        }

        return field.Redo() &&
            field.GetMaterialId(7, 7, 7) == 7 &&
            field.GetMaterialId(8, 8, 8) == 7 &&
            field.GetDataCellStorageBytes(0, 0, 0) > 0;
    }
}
