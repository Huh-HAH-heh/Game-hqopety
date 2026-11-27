using System;
using System.Numerics;
using System.IO;
using Core.Map;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench")
            return RunBenchmarks();

        int failures = 0;
        Run("Uniform plateau stays compact and material vein is independent", TestPlateauAndVein, ref failures);
        Run("Capsule excavation creates a tunnel only in touched bricks", TestTunnelEdit, ref failures);
        Run("Batch reads match point reads for both channels", TestBatchReads, ref failures);
        Run("Imported dense base bricks support arbitrary generated voxels", TestDenseBaseBrick, ref failures);
        Run("Uniform base bricks support independent channel reads and paint edits", TestUniformBaseBrickChannels, ref failures);
        Run("Removed material does not reappear after local carve and refill", TestMaterialDoesNotResurrect, ref failures);
        Run("Save/load preserves base regions, dense bricks and local edits", TestSaveLoadRoundTrip, ref failures);
        Run("Uniform and empty regions are described without voxel expansion", TestUniformRegionFastPath, ref failures);
        Run("Large world does not allocate edit bricks until changed", TestLargeWorldStaysSparse, ref failures);

        Console.WriteLine();
        Console.WriteLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")} | failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int RunBenchmarks()
    {
        Console.WriteLine("metric,iterations,total_ms,ns_per_op,allocated_bytes");

        VoxelVolumeStore store = new(
            width: 23_040,
            height: 500,
            depth: 23_040,
            voxelSizeMeters: 0.1f);
        store.AddSolidBox(
            Vector3.Zero,
            new Vector3(2_304f, 5f, 2_304f),
            materialId: 1);
        store.SealBase();

        VoxelVolumeStore.VoxelBounds uniformBounds =
            new(10_000, 10, 10_000, 12_000, 40, 12_000);
        VoxelVolumeStore.VoxelBounds readBounds =
            new(11_000, 10, 11_000, 11_032, 42, 11_032);

        short[] density = new short[checked((int)readBounds.Volume)];
        ushort[] material = new ushort[density.Length];
        Random random = new(20261127);
        long checksum = 0;

        // Warm JIT and the common query paths before timing.
        store.TryGetUniformRegion(uniformBounds, out _);
        store.GetVoxel(11_000, 20, 11_000);
        store.ReadRegion(readBounds, density, material);

        Measure("uniform_region_query", 10_000, () =>
        {
            if (store.TryGetUniformRegion(uniformBounds, out VoxelVolumeStore.VoxelSample sample))
                checksum += sample.Density + sample.MaterialId;
        });

        Measure("read_region_32cubed", 500, () =>
        {
            store.ReadRegion(readBounds, density, material);
            checksum += density[0] + material[^1];
        });

        Measure("point_sample", 200_000, () =>
        {
            int x = random.Next(10_000, 12_000);
            int y = random.Next(10, 40);
            int z = random.Next(10_000, 12_000);
            VoxelVolumeStore.VoxelSample sample = store.GetVoxel(x, y, z);
            checksum += sample.Density + sample.MaterialId;
        });

        VoxelVolumeStore warmup = new(64, 64, 64);
        warmup.AddSolidBox(new Vector3(0.8f), new Vector3(5.6f), 1);
        warmup.CarveSphere(new Vector3(3.2f), 0.2f);

        Measure("carve_sphere_first", 1, () =>
            store.CarveSphere(new Vector3(1_152f, 2.5f, 1_152f), 3f));

        Measure("carve_sphere_repeat", 4, () =>
            store.CarveSphere(new Vector3(1_152f, 2.5f, 1_152f), 3f));

        Measure("paint_sphere_first", 1, () =>
            store.PaintSphere(new Vector3(1_152f, 2.5f, 1_152f), 2.5f, materialId: 2));

        Console.WriteLine(
            $"storage_summary,{1},{0:0.###},0,{store.BaseDenseBytes + store.EditBytes}");
        Console.WriteLine(
            $"counts,1,{store.BaseGeometryRegionCount},{store.EditBrickCount},{checksum}");

        return 0;
    }

    private static void Measure(string name, int iterations, Action action)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            action();
        stopwatch.Stop();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double nsPerOperation = stopwatch.Elapsed.TotalMilliseconds * 1_000_000d / iterations;
        Console.WriteLine(
            $"{name},{iterations},{stopwatch.Elapsed.TotalMilliseconds:0.###},{nsPerOperation:0.##},{allocated}");
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

    private static (bool Passed, string Detail) TestUniformBaseBrickChannels()
    {
        VoxelVolumeStore store = CreateTestStore();
        short[] density = new short[VoxelVolumeStore.BrickCellCount];
        ushort[] material = new ushort[VoxelVolumeStore.BrickCellCount];
        Array.Fill(density, (short)102);
        Array.Fill(material, (ushort)5);

        store.WriteBaseBrick(1, 1, 1, density, material);
        store.SealBase();

        VoxelVolumeStore.VoxelBounds bounds = new(16, 16, 16, 32, 32, 32);
        int count = VoxelVolumeStore.BrickCellCount;
        short[] densityAll = new short[count];
        ushort[] materialAll = new ushort[count];
        short[] densityOnly = new short[count];
        ushort[] materialOnly = new ushort[count];

        store.ReadRegion(bounds, densityAll, materialAll);
        store.ReadDensityRegion(bounds, densityOnly);
        store.ReadMaterialRegion(bounds, materialOnly);

        bool initial = true;
        for (int i = 0; i < count; i++)
        {
            if (densityAll[i] != 102 || densityOnly[i] != 102 ||
                materialAll[i] != 5 || materialOnly[i] != 5)
            {
                initial = false;
                break;
            }
        }

        store.PaintSphere(new Vector3(2.4f), 0.25f, materialId: 8);
        store.ReadRegion(bounds, densityAll, materialAll);
        store.ReadDensityRegion(bounds, densityOnly);
        store.ReadMaterialRegion(bounds, materialOnly);

        int center = 8 + 16 * (8 + 16 * 8);
        int painted = 0;
        int mismatches = 0;
        for (int i = 0; i < count; i++)
        {
            if (densityAll[i] != 102 || densityOnly[i] != 102)
                mismatches++;
            if (materialAll[i] == 8)
                painted++;
            if (materialAll[i] != materialOnly[i])
                mismatches++;
        }

        bool correct =
            initial &&
            store.BaseDenseBytes == 0 &&
            store.EditBrickCount == 1 &&
            materialAll[center] == 8 &&
            densityAll[center] == 102 &&
            painted > 0 &&
            mismatches == 0;

        return (
            correct,
            $"uniformBaseBytes={store.BaseDenseBytes} editBricks={store.EditBrickCount} painted={painted} centerMaterial={materialAll[center]} mismatches={mismatches}");
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

    private static (bool Passed, string Detail) TestSaveLoadRoundTrip()
    {
        VoxelVolumeStore store = CreateTestStore();
        store.AddSolidBox(
            new Vector3(0.8f, 0.8f, 0.8f),
            new Vector3(5.6f, 5.6f, 5.6f),
            materialId: 1);
        store.PaintMaterialCapsule(
            new Vector3(1f, 3.2f, 3.2f),
            new Vector3(5.4f, 3.2f, 3.2f),
            0.12f,
            materialId: 2);

        short[] baseDensity = new short[VoxelVolumeStore.BrickCellCount];
        ushort[] baseMaterial = new ushort[VoxelVolumeStore.BrickCellCount];
        Array.Fill(baseDensity, (short)102);
        Array.Fill(baseMaterial, (ushort)7);
        store.WriteBaseBrick(0, 0, 0, baseDensity, baseMaterial);

        store.SealBase();
        store.CarveSphere(new Vector3(3.2f), 0.3f);
        store.PaintSphere(new Vector3(4.2f), 0.35f, materialId: 9);

        VoxelVolumeStore.VoxelSample[] before =
        [
            store.GetVoxel(8, 8, 8),
            store.GetVoxel(32, 32, 32),
            store.GetVoxel(32, 36, 32),
            store.GetVoxel(42, 42, 42)
        ];
        long revision = store.Revision;

        using MemoryStream stream = new();
        store.Save(stream);
        long compressedBytes = stream.Length;
        stream.Position = 0;
        VoxelVolumeStore loaded = VoxelVolumeStore.Load(stream);

        VoxelVolumeStore.VoxelSample[] after =
        [
            loaded.GetVoxel(8, 8, 8),
            loaded.GetVoxel(32, 32, 32),
            loaded.GetVoxel(32, 36, 32),
            loaded.GetVoxel(42, 42, 42)
        ];

        bool sameSamples = true;
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i])
                sameSamples = false;

        VoxelVolumeStore.VoxelBounds denseBounds = new(0, 0, 0, 16, 16, 16);
        bool denseUniform =
            loaded.TryGetUniformRegion(denseBounds, out VoxelVolumeStore.VoxelSample denseSample);

        bool correct =
            sameSamples &&
            loaded.Revision == revision &&
            loaded.BaseGeometryRegionCount == store.BaseGeometryRegionCount &&
            loaded.BaseMaterialRegionCount == store.BaseMaterialRegionCount &&
            loaded.BaseDenseBrickCount == store.BaseDenseBrickCount &&
            loaded.BaseDenseBytes == 0 &&
            loaded.EditBrickCount == store.EditBrickCount &&
            denseUniform && denseSample.IsSolid && denseSample.MaterialId == 7;

        return (
            correct,
            $"compressedBytes={compressedBytes} revision={loaded.Revision} baseShapes={loaded.BaseGeometryRegionCount} baseBricks={loaded.BaseDenseBrickCount} editBricks={loaded.EditBrickCount} sameSamples={sameSamples}");
    }

    private static (bool Passed, string Detail) TestUniformRegionFastPath()
    {
        VoxelVolumeStore store = new(
            width: 10_000,
            height: 500,
            depth: 10_000,
            voxelSizeMeters: 0.1f);
        store.AddSolidBox(
            Vector3.Zero,
            new Vector3(700f, 2f, 700f),
            materialId: 1);

        VoxelVolumeStore.VoxelBounds plateau = new(5_000, 10, 5_000, 6_000, 14, 6_000);
        bool solidUniform = store.TryGetUniformRegion(plateau, out var solid);

        VoxelVolumeStore.VoxelBounds emptyRegion = new(9_000, 10, 9_000, 9_016, 14, 9_016);
        bool emptyUniform = store.TryGetUniformRegion(emptyRegion, out var empty);

        short[] density = new short[(int)emptyRegion.Volume];
        ushort[] material = new ushort[density.Length];
        store.ReadRegion(emptyRegion, density, material);

        bool filled = true;
        for (int i = 0; i < density.Length; i++)
        {
            if (density[i] >= 0 || material[i] != 0)
            {
                filled = false;
                break;
            }
        }

        bool correct =
            solidUniform && solid.IsSolid && solid.MaterialId == 1 &&
            emptyUniform && !empty.IsSolid && empty.MaterialId == 0 &&
            filled &&
            store.EditBrickCount == 0;

        return (
            correct,
            $"solidUniform={solidUniform} solid=({solid.Density},{solid.MaterialId}) emptyUniform={emptyUniform} empty=({empty.Density},{empty.MaterialId}) emptyBuffer={filled}");
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
        store.CarveSphere(new Vector3(500f, 40f, 500f), 5f);
        store.PaintSphere(new Vector3(500f, 40f, 500f), 1f, materialId: 2);
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
