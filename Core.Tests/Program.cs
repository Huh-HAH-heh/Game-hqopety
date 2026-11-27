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
        Run("Procedural 3D source supports tunnels, batch reads and save/load", TestProceduralBaseSource, ref failures);
        Run("WorldMap reads its base volume through the legacy edit field", TestWorldMapBaseVolume, ref failures);
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

        short[] baseDensity = new short[VoxelVolumeStore.BrickCellCount];
        ushort[] baseMaterial = new ushort[VoxelVolumeStore.BrickCellCount];
        for (int z = 0; z < VoxelVolumeStore.BrickSize; z++)
        for (int y = 0; y < VoxelVolumeStore.BrickSize; y++)
        for (int x = 0; x < VoxelVolumeStore.BrickSize; x++)
        {
            int index = x + VoxelVolumeStore.BrickSize *
                (y + VoxelVolumeStore.BrickSize * z);
            baseDensity[index] = (short)(80 + x);
            baseMaterial[index] = 3;
        }

        for (int bz = 1; bz <= 2; bz++)
        for (int by = 1; by <= 2; by++)
        for (int bx = 687; bx <= 688; bx++)
            store.WriteBaseBrick(bx, by, bz, baseDensity, baseMaterial);

        store.SealBase();

        VoxelVolumeStore.VoxelBounds uniformBounds =
            new(2_000, 10, 2_000, 4_000, 40, 4_000);
        VoxelVolumeStore.VoxelBounds readBounds =
            new(10_992, 16, 10_992, 11_024, 48, 11_024);

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

        Measure("read_region_dense_32cubed", 500, () =>
        {
            store.ReadRegion(readBounds, density, material);
            checksum += density[0] + material[^1];
        });

        Measure("read_density_dense_32cubed", 500, () =>
        {
            store.ReadDensityRegion(readBounds, density);
            checksum += density[0] + density[^1];
        });

        Measure("read_material_dense_32cubed", 500, () =>
        {
            store.ReadMaterialRegion(readBounds, material);
            checksum += material[0] + material[^1];
        });

        Measure("point_sample_primitive", 200_000, () =>
        {
            int x = random.Next(2_000, 4_000);
            int y = random.Next(10, 40);
            int z = random.Next(2_000, 4_000);
            VoxelVolumeStore.VoxelSample sample = store.GetVoxel(x, y, z);
            checksum += sample.Density + sample.MaterialId;
        });

        Measure("point_sample_dense", 100_000, () =>
        {
            int x = random.Next(10_992, 11_024);
            int y = random.Next(16, 48);
            int z = random.Next(10_992, 11_024);
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

    private static (bool Passed, string Detail) TestWorldMapBaseVolume()
    {
        const int layerCount = 200;
        WorldMap world = CreateFlatWorld(layerCount);
        world.InitializeVoxelVolumeBase();

        VoxelVolumeStore.VoxelSample solid = world.VoxelVolume.GetVoxel(8, 10, 8);
        VoxelVolumeStore.VoxelSample air = world.VoxelVolume.GetVoxel(8, 190, 8);
        float legacyDensity = world.Voxels.GetDensity(8, 10, 8);
        ushort legacyMaterial = world.Voxels.GetMaterialId(8, 10, 8);

        // Static geology is authored into the compact base before it is sealed.
        world.CarveStaticVolume(
            x: 1, y: 1, zLevel: 2,
            width: 2, depth: 2, heightInMeters: 2);
        VoxelVolumeStore.VoxelSample baseCarve = world.VoxelVolume.GetVoxel(15, 25, 15);
        ushort baseCarveLegacyMaterial = world.Voxels.GetMaterialId(15, 25, 15);
        world.VoxelVolume.SealBase();

        // Runtime edits still use the compatibility SparseVoxelField overlay.
        world.CarveStaticVolume(
            x: 3, y: 1, zLevel: 2,
            width: 2, depth: 2, heightInMeters: 2);
        ushort overlayMaterial = world.Voxels.GetMaterialId(35, 25, 15);
        VoxelVolumeStore.VoxelSample overlayBase = world.VoxelVolume.GetVoxel(35, 25, 15);

        VoxelVolumeStore.VoxelBounds bounds = new(8, 10, 8, 10, 12, 10);
        short[] density = new short[(int)bounds.Volume];
        ushort[] material = new ushort[density.Length];
        world.VoxelVolume.ReadRegion(bounds, density, material);

        bool batchMatches = true;
        int index = 0;
        for (int z = bounds.MinZ; z < bounds.MaxZ; z++)
        for (int y = bounds.MinY; y < bounds.MaxY; y++)
        for (int x = bounds.MinX; x < bounds.MaxX; x++)
        {
            VoxelVolumeStore.VoxelSample sample = world.VoxelVolume.GetVoxel(x, y, z);
            if (density[index] != sample.Density || material[index] != sample.MaterialId)
                batchMatches = false;
            index++;
        }

        // Height edits modify the sealed volume without replacing its original
        // procedural source snapshot; the save resolver must restore that snapshot.
        world.SetSolidHeight(6, 6, height: 0, materialId: 0);
        VoxelVolumeStore.VoxelSample removedColumn = world.VoxelVolume.GetVoxel(65, 10, 65);

        using MemoryStream stream = new();
        world.SaveVoxelVolume(stream);
        stream.Position = 0;

        WorldMap restored = CreateFlatWorld(layerCount);
        restored.InitializeVoxelVolumeBase();
        restored.LoadVoxelVolume(stream);
        VoxelVolumeStore.VoxelSample restoredSolid = restored.VoxelVolume.GetVoxel(8, 10, 8);
        VoxelVolumeStore.VoxelSample restoredCarve = restored.VoxelVolume.GetVoxel(15, 25, 15);
        VoxelVolumeStore.VoxelSample restoredColumn = restored.VoxelVolume.GetVoxel(65, 10, 65);

        bool correct =
            solid.IsSolid && solid.MaterialId == 1 &&
            !air.IsSolid && air.MaterialId == 0 &&
            legacyDensity > 0f && legacyMaterial == solid.MaterialId &&
            !baseCarve.IsSolid && baseCarve.MaterialId == 0 &&
            baseCarveLegacyMaterial == 0 &&
            overlayMaterial == 0 && overlayBase.IsSolid &&
            !removedColumn.IsSolid &&
            batchMatches &&
            restoredSolid == solid &&
            restoredCarve == baseCarve &&
            restoredColumn == removedColumn &&
            restored.VoxelVolume.IsBaseSealed;

        return (
            correct,
            $"base=({solid.Density},{solid.MaterialId}) legacy=({legacyDensity:0.###},{legacyMaterial}) baseCarve={baseCarve} overlay=({overlayMaterial},{overlayBase.IsSolid}) removedColumn={removedColumn} batch={batchMatches} restored={restoredColumn}");
    }

    private static WorldMap CreateFlatWorld(int layerCount)
    {
        WorldMap world = new(regionsX: 1, regionsY: 1, layerCount: layerCount);
        ushort[] heights = new ushort[checked(world.TileWidth * world.TileHeight)];
        Array.Fill(heights, (ushort)180);
        world.SetSurfaceHeightPatches(heights, patchSize: 1, materialId: 1);
        return world;
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

    private static (bool Passed, string Detail) TestProceduralBaseSource()
    {
        VoxelVolumeStore store = CreateTestStore();
        store.SetBaseSource(new TestProceduralSource(64, 64, 64, 0.1f, seed: 1));
        store.SealBase();

        VoxelVolumeStore.VoxelSample solid = store.GetVoxel(24, 24, 24);
        VoxelVolumeStore.VoxelSample tunnel = store.GetVoxel(32, 32, 24);
        VoxelVolumeStore.VoxelSample tunnelWall = store.GetVoxel(32, 24, 24);

        VoxelVolumeStore.VoxelBounds bounds = new(28, 28, 8, 36, 36, 56);
        short[] density = new short[(int)bounds.Volume];
        ushort[] material = new ushort[density.Length];
        short[] densityOnly = new short[density.Length];
        ushort[] materialOnly = new ushort[density.Length];
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

        store.CarveSphere(new Vector3(2.4f), 0.25f);
        VoxelVolumeStore.VoxelSample carved = store.GetVoxel(24, 24, 24);

        using MemoryStream stream = new();
        store.Save(stream);
        stream.Position = 0;
        VoxelVolumeStore loaded = VoxelVolumeStore.Load(stream, ResolveTestBaseSource);

        VoxelVolumeStore.VoxelSample restoredCarve = loaded.GetVoxel(24, 24, 24);
        VoxelVolumeStore.VoxelSample restoredSolid = loaded.GetVoxel(24, 24, 40);
        VoxelVolumeStore.VoxelSample restoredTunnel = loaded.GetVoxel(32, 32, 24);

        bool correct =
            store.HasBaseSource &&
            solid.IsSolid && solid.MaterialId == 7 &&
            !tunnel.IsSolid && tunnel.MaterialId == 0 &&
            tunnelWall.IsSolid && tunnelWall.MaterialId == 7 &&
            mismatches == 0 &&
            !carved.IsSolid &&
            loaded.HasBaseSource &&
            restoredCarve == carved &&
            restoredSolid.IsSolid && restoredSolid.MaterialId == 7 &&
            !restoredTunnel.IsSolid && restoredTunnel.MaterialId == 0;

        return (
            correct,
            $"solid={solid.IsSolid} tunnel={tunnel.IsSolid} wall={tunnelWall.IsSolid} batchMismatches={mismatches} sourceRestored={loaded.HasBaseSource} editRestored={!restoredCarve.IsSolid}");
    }

    private static IVoxelBaseSource ResolveTestBaseSource(
        string typeId,
        ushort dataVersion,
        int width,
        int height,
        int depth,
        float voxelSizeMeters,
        BinaryReader reader)
    {
        if (typeId != TestProceduralSource.SourceId || dataVersion != 1)
            throw new InvalidDataException("Unknown test voxel source.");
        int seed = reader.ReadInt32();
        return new TestProceduralSource(
            width, height, depth, voxelSizeMeters, seed);
    }

    private sealed class TestProceduralSource : IVoxelBaseSource
    {
        public const string SourceId = "tests.voxel-cavern";

        public string TypeId => SourceId;
        public ushort DataVersion => 1;
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public float VoxelSizeMeters { get; }
        private int Seed { get; }

        public TestProceduralSource(
            int width,
            int height,
            int depth,
            float voxelSizeMeters,
            int seed)
        {
            Width = width;
            Height = height;
            Depth = depth;
            VoxelSizeMeters = voxelSizeMeters;
            Seed = seed;
        }

        public VoxelVolumeStore.VoxelSample Sample(int x, int y, int z)
        {
            bool inMass =
                x >= 8 && x < 56 &&
                y >= 8 && y < 56 &&
                z >= 8 && z < 56;

            int tunnelX = 31 + (Seed & 1);
            bool inTunnel =
                Math.Abs(x - tunnelX) <= 2 &&
                Math.Abs(y - 32) <= 2 &&
                z >= 8 && z < 56;

            bool solid = inMass && !inTunnel;
            return new VoxelVolumeStore.VoxelSample(
                solid ? (short)96 : (short)-96,
                solid ? (ushort)7 : (ushort)0);
        }

        public void WriteState(BinaryWriter writer) =>
            writer.Write(Seed);
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
