using System.Diagnostics;
using Core.Map;
using RimClone.Render.Raylib;

internal static class Program
{
    private static readonly int[] Lods = { 1, 2, 4, 8, 5, 10, 40 };

    private static int Main()
    {
        int failures = 0;
        Console.WriteLine("=== RimClone voxel terrain pipeline tests ===");
        Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
        Console.WriteLine($"Processor count: {Environment.ProcessorCount}");

        Run("LOD chunk borders use identical source samples", TestChunkBorders, ref failures);
        Run("Reduced LOD preserves more SDF detail than legacy 40-step LOD",
            TestLodApproximation, ref failures);

        Console.WriteLine();
        RunBenchmark();

        Console.WriteLine();
        Console.WriteLine($"RESULT: {(failures == 0 ? "PASS" : "FAIL")} | failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<(bool Passed, string Detail)> test, ref int failures)
    {
        try
        {
            (bool passed, string detail) = test();
            Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {name}: {detail}");
            if (!passed)
                failures++;
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"[FAIL] {name}: {exception}");
        }
    }

    private static (bool Passed, string Detail) TestChunkBorders()
    {
        SyntheticTerrainField field = new(1024, 1024, 1024);
        foreach (int step in Lods)
        {
            int chunkY = GetSurfaceChunkY(step);
            VoxelChunkBuildInput left = Capture(field, 0, chunkY, 0, step);
            VoxelChunkBuildInput right = Capture(field, 1, chunkY, 0, step);

            int mismatches = 0;
            for (int z = 0; z < 19; z++)
            for (int y = 0; y < 19; y++)
            {
                float a = left.DensitySamples[17 + y * 19 + z * 19 * 19];
                float b = right.DensitySamples[1 + y * 19 + z * 19 * 19];
                if (MathF.Abs(a - b) > 0.00001f)
                    mismatches++;
            }

            if (mismatches != 0)
                return (false, $"step={step}, shared-border mismatches={mismatches}");
        }

        return (true, "all shared sample planes match for steps 1, 2, 4, 8, 5, 10 and 40");
    }

    private static (bool Passed, string Detail) TestLodApproximation()
    {
        SyntheticTerrainField field = new(1024, 1024, 1024);
        double fineError = MeasureSdfError(field, 2);
        double runtimeCoarseError = MeasureSdfError(field, 4);
        double legacyStep8Error = MeasureSdfError(field, 8);
        double legacyCoarseError = MeasureSdfError(field, 40);

        bool passed = runtimeCoarseError < legacyStep8Error &&
            runtimeCoarseError < legacyCoarseError &&
            fineError < runtimeCoarseError;

        return (passed,
            $"max SDF error: runtime step2={fineError:0.000}, runtime step4={runtimeCoarseError:0.000}, " +
            $"legacy step8={legacyStep8Error:0.000}, legacy step40={legacyCoarseError:0.000}");
    }

    private static double MeasureSdfError(SyntheticTerrainField field, int step)
    {
        VoxelChunkBuildInput input = Capture(
            field, 0, GetSurfaceChunkY(step), 0, step);

        double maximumError = 0d;
        for (int z = 1; z <= 31; z++)
        for (int x = 1; x <= 31; x++)
        {
            float worldX = x * 0.5f;
            float worldZ = z * 0.5f;
            const float worldY = 49.5f;
            float exact = field.SurfaceHeight(worldX, worldZ) - 50f;
            float reconstructed = input.SampleDensity(worldX, worldY, worldZ);
            maximumError = Math.Max(maximumError, Math.Abs(exact - reconstructed));
        }

        return maximumError;
    }

    private static void RunBenchmark()
    {
        SyntheticTerrainField field = new(1024, 1024, 1024);
        TerrainPalette palette = new();
        Console.WriteLine("=== CPU mesh benchmark (no GPU upload) ===");
        Console.WriteLine("LOD | cell size | capture ms | build median ms | build p95 ms | vertices | triangles | alloc MiB | max SDF error");

        foreach (int step in Lods)
        {
            int chunkY = GetSurfaceChunkY(step);
            Stopwatch captureTimer = Stopwatch.StartNew();
            VoxelChunkBuildInput input = Capture(
                field, 0, chunkY, 0, step);
            captureTimer.Stop();

            // JIT warm-up is kept outside the measured samples.
            _ = VoxelChunkMesher.BuildSurfaceData(input, palette);

            const int iterations = 9;
            double[] timings = new double[iterations];
            long allocationBefore = GC.GetTotalAllocatedBytes(true);
            VoxelMeshData[] last = Array.Empty<VoxelMeshData>();

            for (int i = 0; i < iterations; i++)
            {
                Stopwatch buildTimer = Stopwatch.StartNew();
                last = VoxelChunkMesher.BuildSurfaceData(input, palette);
                buildTimer.Stop();
                timings[i] = buildTimer.Elapsed.TotalMilliseconds;
            }

            long allocated = GC.GetTotalAllocatedBytes(true) - allocationBefore;
            Array.Sort(timings);
            int vertices = last.Sum(static mesh => mesh.VertexCount);
            int triangles = last.Sum(static mesh => mesh.TriangleCount);
            double maximumError = MeasureSdfError(field, step);

            Console.WriteLine(
                $"{step,3} | {step * 0.1f,9:0.0} m | " +
                $"{captureTimer.Elapsed.TotalMilliseconds,10:0.000} | " +
                $"{timings[iterations / 2],16:0.000} | " +
                $"{timings[iterations - 1],12:0.000} | " +
                $"{vertices,8} | {triangles,9} | " +
                $"{allocated / (1024d * 1024d),9:0.00} | {maximumError:0.000}");
        }
    }

    private static VoxelChunkBuildInput Capture(
        SyntheticTerrainField field,
        int chunkX,
        int chunkY,
        int chunkZ,
        int step)
    {
        VoxelChunkBuildInput input = new(field, chunkX, chunkY, chunkZ, step);
        while (!input.IsComplete)
            input.CaptureNext(field, 4096);
        return input;
    }

    private static int GetSurfaceChunkY(int step) =>
        Math.Clamp(
            (int)MathF.Floor(50f / (SparseVoxelField.ChunkSize * step)),
            0,
            1024 / (SparseVoxelField.ChunkSize * step) - 1);

    private sealed class SyntheticTerrainField : IVoxelFieldSource
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        public SyntheticTerrainField(int width, int height, int depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
        }

        public float SurfaceHeight(float x, float z)
        {
            float dx = x - 5.5f;
            float dz = z - 5.5f;
            float bump = 12f * MathF.Exp(
                -(dx * dx + dz * dz) / (2f * 1.4f * 1.4f));
            return 50f + bump +
                1.25f * MathF.Sin(x * 0.12f) * MathF.Cos(z * 0.11f);
        }

        public float GetDensity(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth)
                return -1f;

            return SurfaceHeight(x, z) - (y + 0.5f);
        }

        public ushort GetMaterialId(int x, int y, int z) =>
            GetDensity(x, y, z) > 0f ? (ushort)1 : (ushort)0;
    }
}
