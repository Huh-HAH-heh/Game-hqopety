using System;
using System.Numerics;
using Core.Map;

internal static class Program
{
    private static int Main()
    {
        int failures = 0;
        Run("Uniform plateau stays compact and material vein is independent", TestPlateauAndVein, ref failures);
        Run("Capsule excavation creates a tunnel only in touched bricks", TestTunnelEdit, ref failures);
        Run("Batch reads match point reads for both channels", TestBatchReads, ref failures);
        Run("Imported dense base bricks support arbitrary generated voxels", TestDenseBaseBrick, ref failures);
        Run("Removed material does not reappear after local carve and refill", TestMaterialDoesNotResurrect, ref failures);
        Run("Large world does not allocate edit bricks until changed", TestLargeWorldStaysSparse, ref failures);

        Console.WriteLine();
        Console.WriteLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")} | failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(
        string name,
        Func<(bool Passed, string Detail)> test,
        ref int failures)
    {
        try
        {
            (bool passed, string detail) = test();
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}: {detail}");
            if (!passed) failures++;
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"[FAIL] {name}: {exception}");
        }
    }

    private static (bool Passed, string Detail) TestPlateauAndVein()
    {
        VoxelVolumeStore store = CreateTestStore();
        store.AddSolidBox(
            new Vector3(0.8f, 0.8f, 0.8f),
            new Vector3(5.6f, 5.6f, 5.6f),
            materialId: 1);

        short before = store.GetDensity(32, 32, 32);
        store.PaintMaterialCapsule(
            new Vector3(1.0f, 3.2f, 3.2f),
            new Vector3(5.4f, 3.2f, 3.2f),
            0.12f,
            materialId: 2);

        VoxelVolumeStore.VoxelSample sample = store.GetVoxel(32, 32, 32);
        bool correct =
            store.BaseGeometryRegionCount == 1 &&
            store.EditBrickCount == 0 &&
            before > 0 &&
            sample.Density == before &&
            sample.MaterialId == 2;

        return (
            correct,
            $"baseRegions={store.BaseGeometryRegionCount} editBricks={store.EditBrickCount} density={before} material={sample.MaterialId}");
    }

    private static (bool Passed, string Detail) TestTunnelEdit()
    {
        VoxelVolumeStore store = CreateTestStore();
        store.AddSolidBox(
            new Vector3(0.8f, 0.8f, 0.8f),
            new Vector3(5.6f, 5.6f, 5.6f),
            materialId: 1);
        store.SealBase();

        store.CarveCapsule(
            new Vector3(1.0f, 3.2f, 3.2f),
            new Vector3(5.4f, 3.2f, 3.2f),
            radiusMeters: 0.3f);

        VoxelVolumeStore.VoxelSample tunnel = store.GetVoxel(32, 32, 32);
        VoxelVolumeStore.VoxelSample wall = store.GetVoxel(32, 38, 32);
        bool correct =
            tunnel.Density <= 0 && tunnel.MaterialId == 0 &&
            wall.Density > 0 && wall.MaterialId == 1 &&
            store.EditBrickCount > 0 &&
            store.EditBytes > 0;

        return (
            correct,
            $"tunnel=({tunnel.Density},{tunnel.MaterialId}) wall=({wall.Density},{wall.MaterialId}) bricks={store.EditBrickCount} bytes={store.EditBytes}");
    }

    private static (bool Passed, string Detail) TestBatchReads()
    {
        VoxelVolumeStore store = CreateTestStore();
        store.AddSolidBox(
            new Vector3(0.8f, 0.8f, 0.8f),
            new Vector3(5.6f, 5.6f, 5.6f),
            materialId: 1);
        store.PaintMaterialSphere(
            new Vector3(3.2f, 3.2f, 3.2f),
            0.7f,
            materialId: 2);
        store.SealBase();
        store.CarveSphere(new Vector3(3.2f, 3.2f, 3.2f), 0.25f);

        VoxelVolumeStore.VoxelBounds bounds = new(24, 24, 24, 40, 40, 40);
        int count = checked((int)bounds.Volume);
        short[] density = new short[count];
        ushort[] material = new ushort[count];
        short[] densityOnly = new short[count];
        ushort[] materialOnly = new ushort[count];

        store.ReadRegion(bounds, density, material);
        store.ReadDensityRegion(bounds, densityOnly);
        store.ReadMaterialRegion(bounds, materialOnly);

        int mismatches = 0;
        int index = 0;
        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            VoxelVolumeStore.VoxelSample point = store.GetVoxel(x, y, z);
            if (density[index] != point.Density ||
                material[index] != point.MaterialId ||
                densityOnly[index] != point.Density ||
                materialOnly[index] != point.MaterialId)
                mismatches++;
            index++;
        }

        return (mismatches == 0, $"samples={count} mismatches={mismatches}");
    }

    private static (bool Passed, string Detail) TestDenseBaseBrick()
    {
        VoxelVolumeStore store = CreateTestStore();
        short[] density = new short[VoxelVolumeStore.BrickCellCount];
        ushort[] materials = new ushort[VoxelVolumeStore.BrickCellCount];

        for (int z = 0; z < VoxelVolumeStore.BrickSize; z++)
        for (int y = 0; y < VoxelVolumeStore.BrickSize; y++)
        for (int x = 0; x < VoxelVolumeStore.BrickSize; x++)
        {
            int index = x + VoxelVolumeStore.BrickSize *
                (y + VoxelVolumeStore.BrickSize * z);
            density[index] = (short)((y - 8) * 256);
            materials[index] = 3;
        }

        store.WriteBaseBrick(1, 1, 1, density, materials);
        store.SealBase();

        VoxelVolumeStore.VoxelSample solid = store.GetVoxel(24, 25, 24);
        VoxelVolumeStore.VoxelSample air = store.GetVoxel(24, 23, 24);

        store.PaintSphere(new Vector3(2.45f, 2.55f, 2.45f), 0.12f, materialId: 4);
        VoxelVolumeStore.VoxelSample painted = store.GetVoxel(24, 25, 24);
        store.CarveSphere(new Vector3(2.45f, 2.55f, 2.45f), 0.2f);
        VoxelVolumeStore.VoxelSample carved = store.GetVoxel(24, 25, 24);

        VoxelVolumeStore.VoxelBounds bounds = new(16, 16, 16, 32, 32, 32);
        short[] batchDensity = new short[VoxelVolumeStore.BrickCellCount];
        ushort[] batchMaterial = new ushort[VoxelVolumeStore.BrickCellCount];
        store.ReadRegion(bounds, batchDensity, batchMaterial);

        bool correct =
            solid.IsSolid && solid.MaterialId == 3 &&
            !air.IsSolid && air.MaterialId == 0 &&
            painted.IsSolid && painted.Density == solid.Density && painted.MaterialId == 4 &&
            !carved.IsSolid && carved.MaterialId == 0 &&
            batchDensity[8 + 16 * (9 + 16 * 8)] <= 0 &&
            batchMaterial[8 + 16 * (9 + 16 * 8)] == 0 &&
            store.BaseDenseBrickCount == 1 &&
            store.BaseDenseBytes == VoxelVolumeStore.BrickCellCount * 4L &&
            store.EditBrickCount == 1;

        return (
            correct,
            $"solid=({solid.Density},{solid.MaterialId}) air=({air.Density},{air.MaterialId}) painted=({painted.Density},{painted.MaterialId}) carved=({carved.Density},{carved.MaterialId}) baseBricks={store.BaseDenseBrickCount} editBricks={store.EditBrickCount}");
    }

    private static (bool Passed, string Detail) TestMaterialDoesNotResurrect()
    {
        VoxelVolumeStore store = CreateTestStore();
        store.AddSolidBox(
            new Vector3(0.8f, 0.8f, 0.8f),
            new Vector3(5.6f, 5.6f, 5.6f),
            materialId: 1);
        store.PaintSphere(new Vector3(3.2f), 0.3f, materialId: 2);

        store.CarveSphere(new Vector3(3.2f), 0.4f);
        VoxelVolumeStore.VoxelSample carved = store.GetVoxel(32, 32, 32);

        store.AddSphere(new Vector3(3.2f), 0.2f, materialId: 3);
        VoxelVolumeStore.VoxelSample refilled = store.GetVoxel(32, 32, 32);

        store.SetDensity(32, 32, 32, short.MinValue);
        VoxelVolumeStore.VoxelSample emptied = store.GetVoxel(32, 32, 32);
        store.SetDensity(32, 32, 32, short.MaxValue);
        VoxelVolumeStore.VoxelSample restored = store.GetVoxel(32, 32, 32);

        bool correct =
            !carved.IsSolid && carved.MaterialId == 0 &&
            refilled.IsSolid && refilled.MaterialId == 3 &&
            !emptied.IsSolid && emptied.MaterialId == 0 &&
            restored.IsSolid && restored.MaterialId == 0;

        return (
            correct,
            $"carved=({carved.Density},{carved.MaterialId}) refilled=({refilled.Density},{refilled.MaterialId}) emptied=({emptied.Density},{emptied.MaterialId}) restored=({restored.Density},{restored.MaterialId})");
    }

    private static (bool Passed, string Detail) TestLargeWorldStaysSparse()
    {
        VoxelVolumeStore store = new(
            width: 10_000,
            height: 500,
            depth: 10_000,
            voxelSizeMeters: 0.1f);

        store.AddSolidBox(
            Vector3.Zero,
            new Vector3(1_000f, 2f, 1_000f),
            materialId: 1);
        VoxelVolumeStore.VoxelSample sample = store.GetVoxel(5_000, 10, 5_000);

        bool correct =
            store.BaseGeometryRegionCount == 1 &&
            store.EditBrickCount == 0 &&
            store.EditBytes == 0 &&
            sample.IsSolid &&
            sample.MaterialId == 1;

        return (
            correct,
            $"logicalCells={10_000L * 500 * 10_000:N0} baseRegions={store.BaseGeometryRegionCount} editBricks={store.EditBrickCount} editBytes={store.EditBytes}");
    }

    private static VoxelVolumeStore CreateTestStore() =>
        new(width: 64, height: 64, depth: 64, voxelSizeMeters: 0.1f);
}
