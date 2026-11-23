using System.Diagnostics;
using System.Numerics;
using System.Collections.Concurrent;
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

        Run("Surface Nets chunk borders use identical source samples", TestChunkBorders, ref failures);
        Run("Uniform chunks skip all material samples", TestDensityOnlySkipsMaterials, ref failures);
        Run("Cut-cap chunks meet without gaps and keep finite normals", TestCutCapBorders, ref failures);
        Run("Ambiguous cut saddles stitch across chunk borders", TestAmbiguousCutCapBorders, ref failures);
        Run("Surface boundary normals stay finite and non-zero", TestSurfaceNormalsAtChunkBorders, ref failures);
        Run("Reduced LOD preserves more SDF detail than legacy 40-step LOD",
            TestLodApproximation, ref failures);

        Console.WriteLine();
        RunBenchmark();
        Console.WriteLine();
        RunWorkerThroughputBenchmark();

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

            const int sampleSide = SparseVoxelField.ChunkSize + 4;
            const int samplePlane = sampleSide * sampleSide;
            int mismatches = 0;
            for (int z = 0; z < sampleSide; z++)
            for (int y = 0; y < sampleSide; y++)
            {
                float a = left.DensitySamples[18 + y * sampleSide + z * samplePlane];
                float b = right.DensitySamples[2 + y * sampleSide + z * samplePlane];
                if (MathF.Abs(a - b) > 0.00001f)
                    mismatches++;
            }

            if (mismatches != 0)
                return (false, $"step={step}, shared-border mismatches={mismatches}");
        }

        return (true, "all shared sample planes match for steps 1, 2, 4, 8, 5, 10 and 40");
    }

    private static (bool Passed, string Detail) TestCutCapBorders()
    {
        const int step = 4;
        const float cutHeightMeters = 5f;
        const float voxelSizeMeters = SparseVoxelField.VoxelSizeMeters;
        SyntheticTerrainField field = new(1024, 1024, 1024);
        TerrainPalette palette = new();
        int cutSampleY = (int)MathF.Floor(
            cutHeightMeters / voxelSizeMeters - 0.5f);
        int chunkY = Math.Clamp(
            cutSampleY / (SparseVoxelField.ChunkSize * step),
            0,
            field.Height / (SparseVoxelField.ChunkSize * step) - 1);

        VoxelChunkBuildInput left = Capture(field, 0, chunkY, 0, step);
        VoxelChunkBuildInput right = Capture(field, 1, chunkY, 0, step);
        VoxelMeshData[] leftCap = VoxelChunkMesher.BuildCutCapData(
            left, cutHeightMeters, palette);
        VoxelMeshData[] rightCap = VoxelChunkMesher.BuildCutCapData(
            right, cutHeightMeters, palette);

        try
        {
        float sharedX = (SparseVoxelField.ChunkSize * step + 0.5f) * voxelSizeMeters;
        HashSet<int> leftBoundary = CollectCapBoundaryZ(leftCap, sharedX, cutHeightMeters);
        HashSet<int> rightBoundary = CollectCapBoundaryZ(rightCap, sharedX, cutHeightMeters);

        bool finite = HasFiniteNonZeroNormals(leftCap) &&
            HasFiniteNonZeroNormals(rightCap);
        bool seamMatches = leftBoundary.Count > 0 &&
            leftBoundary.SetEquals(rightBoundary);

        return (
            finite && seamMatches,
            $"leftBoundary={leftBoundary.Count}, rightBoundary={rightBoundary.Count}, " +
            $"seamMatch={seamMatches}, finiteNormals={finite}");
        }
        finally
        {
            ReturnMeshData(leftCap);
            ReturnMeshData(rightCap);
        }
    }

    private static (bool Passed, string Detail) TestAmbiguousCutCapBorders()
    {
        const int step = 4;
        const float cutHeightMeters = 5f;
        SyntheticSaddleField field = new(512, 512, 512, step);
        TerrainPalette palette = new();
        int chunkY = 0;

        VoxelChunkBuildInput left = Capture(field, 0, chunkY, 0, step);
        VoxelChunkBuildInput right = Capture(field, 1, chunkY, 0, step);
        VoxelMeshData[] leftCap = VoxelChunkMesher.BuildCutCapData(
            left, cutHeightMeters, palette);
        VoxelMeshData[] rightCap = VoxelChunkMesher.BuildCutCapData(
            right, cutHeightMeters, palette);

        try
        {
            float sharedX =
                (SparseVoxelField.ChunkSize * step + 0.5f) *
                SparseVoxelField.VoxelSizeMeters;

            HashSet<int> leftBoundary =
                CollectCapBoundaryZ(leftCap, sharedX, cutHeightMeters);
            HashSet<int> rightBoundary =
                CollectCapBoundaryZ(rightCap, sharedX, cutHeightMeters);

            bool seamMatches = leftBoundary.Count > 0 &&
                leftBoundary.SetEquals(rightBoundary);
            bool validNormals = HasFiniteNonZeroNormals(leftCap) &&
                HasFiniteNonZeroNormals(rightCap);

            return (
                seamMatches && validNormals,
                $"saddleBoundaryLeft={leftBoundary.Count}, saddleBoundaryRight={rightBoundary.Count}, " +
                $"seamMatch={seamMatches}, finiteNormals={validNormals}");
        }
        finally
        {
            ReturnMeshData(leftCap);
            ReturnMeshData(rightCap);
        }
    }

    private static (bool Passed, string Detail) TestSurfaceNormalsAtChunkBorders()
    {
        const int step = 4;
        SyntheticTerrainField field = new(512, 512, 512);
        TerrainPalette palette = new();
        int chunkY = GetSurfaceChunkY(step);

        VoxelChunkBuildInput left = Capture(field, 0, chunkY, 0, step);
        VoxelChunkBuildInput right = Capture(field, 1, chunkY, 0, step);
        VoxelMeshData[] leftMeshes =
            VoxelChunkMesher.BuildSurfaceData(left, palette);
        VoxelMeshData[] rightMeshes =
            VoxelChunkMesher.BuildSurfaceData(right, palette);

        try
        {
            bool leftValid = HasFiniteNonZeroNormals(leftMeshes);
            bool rightValid = HasFiniteNonZeroNormals(rightMeshes);

            return (
                leftValid && rightValid,
                $"leftMeshes={leftMeshes.Length}, rightMeshes={rightMeshes.Length}, " +
                $"leftNormals={leftValid}, rightNormals={rightValid}");
        }
        finally
        {
            ReturnMeshData(leftMeshes);
            ReturnMeshData(rightMeshes);
        }
    }

    private static HashSet<int> CollectCapBoundaryZ(
        VoxelMeshData[] meshes,
        float sharedX,
        float cutHeightMeters)
    {
        HashSet<int> points = new();
        float expectedY = cutHeightMeters + 0.006f;
        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 vertex in mesh.Vertices)
        {
            if (MathF.Abs(vertex.X - sharedX) > 0.0001f ||
                MathF.Abs(vertex.Y - expectedY) > 0.0001f)
                continue;

            points.Add((int)MathF.Round(vertex.Z * 10000f));
        }
        return points;
    }

    private static bool HasFiniteNonZeroNormals(VoxelMeshData[] meshes)
    {
        int count = 0;
        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 normal in mesh.Normals)
        {
            count++;
            float lengthSquared = normal.LengthSquared();
            if (!float.IsFinite(normal.X) ||
                !float.IsFinite(normal.Y) ||
                !float.IsFinite(normal.Z) ||
                !float.IsFinite(lengthSquared) ||
                lengthSquared < 0.9f)
                return false;
        }

        return count > 0;
    }

    private static (bool Passed, string Detail) TestDensityOnlySkipsMaterials()
    {
        SyntheticTerrainField emptyField = new(512, 512, 512);
        VoxelChunkBuildInput empty = new(emptyField, 0, 0, 0, 1);
        CaptureDensityAware(emptyField, empty);

        bool emptySkippedMaterials =
            !empty.HasPotentialSurface &&
            emptyField.MaterialReadCount == 0 &&
            empty.IsComplete;

        SyntheticTerrainField surfaceField = new(512, 512, 512);
        VoxelChunkBuildInput surface = new(surfaceField, 0, 3, 0, 1);
        CaptureDensityAware(surfaceField, surface);

        bool surfaceRetainedMaterials =
            surface.HasPotentialSurface &&
            surfaceField.MaterialReadCount == 4096 &&
            surface.IsComplete;

        return (
            emptySkippedMaterials && surfaceRetainedMaterials,
            $"empty: surface={empty.HasPotentialSurface}, materialReads={emptyField.MaterialReadCount}; " +
            $"surface: surface={surface.HasPotentialSurface}, materialReads={surfaceField.MaterialReadCount}");
    }

    private static void CaptureDensityAware(
        SyntheticTerrainField field,
        VoxelChunkBuildInput input)
    {
        while (!input.IsComplete)
        {
            bool captureMaterials =
                input.DensityCaptureComplete && input.HasPotentialSurface;
            input.CaptureNext(field, 128, captureMaterials);
        }
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
            $"max SDF error (m): runtime step2={fineError:0.000}, runtime step4={runtimeCoarseError:0.000}, " +
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

        return maximumError * SparseVoxelField.VoxelSizeMeters;
    }

    private static void RunBenchmark()
    {
        SyntheticTerrainField field = new(1024, 1024, 1024);
        TerrainPalette palette = new();
        Console.WriteLine("=== CPU mesh benchmark (no GPU upload) ===");
        Console.WriteLine("LOD | cell size | capture median/p95 ms | build median/p95 ms | vertices | triangles | alloc MiB | max SDF error (m)");

        foreach (int step in Lods)
        {
            int chunkY = GetSurfaceChunkY(step);
            const int iterations = 9;
            double[] captureTimings = new double[iterations];
            VoxelChunkBuildInput input = null!;

            for (int i = 0; i < iterations; i++)
            {
                Stopwatch captureTimer = Stopwatch.StartNew();
                input = Capture(field, 0, chunkY, 0, step);
                captureTimer.Stop();
                captureTimings[i] = captureTimer.Elapsed.TotalMilliseconds;
            }
            Array.Sort(captureTimings);

            // JIT warm-up is kept outside the measured mesh samples.
            VoxelMeshData[] warmup = VoxelChunkMesher.BuildSurfaceData(input, palette);
            ReturnMeshData(warmup);

            double[] timings = new double[iterations];
            long allocationBefore = GC.GetTotalAllocatedBytes(true);
            VoxelMeshData[] last = Array.Empty<VoxelMeshData>();

            for (int i = 0; i < iterations; i++)
            {
                Stopwatch buildTimer = Stopwatch.StartNew();
                VoxelMeshData[] next = VoxelChunkMesher.BuildSurfaceData(input, palette);
                if (last.Length > 0)
                    ReturnMeshData(last);
                last = next;
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
                $"{captureTimings[iterations / 2],9:0.000}/{captureTimings[iterations - 1],0:0.000} | " +
                $"{timings[iterations / 2],9:0.000}/{timings[iterations - 1],0:0.000} | " +
                $"{vertices,8} | {triangles,9} | " +
                $"{allocated / (1024d * 1024d),9:0.00} | {maximumError:0.000}");

            ReturnMeshData(last);
        }
    }

    private static void RunWorkerThroughputBenchmark()
    {
        const int step = 2;
        const int chunkCount = 8;
        const int iterations = 5;
        SyntheticTerrainField field = new(1024, 1024, 1024);
        TerrainPalette palette = new();
        int chunkY = GetSurfaceChunkY(step);
        VoxelChunkBuildInput[] inputs = new VoxelChunkBuildInput[chunkCount];

        Stopwatch captureTimer = Stopwatch.StartNew();
        for (int i = 0; i < chunkCount; i++)
        {
            int chunkX = i % 4;
            int chunkZ = i / 4;
            inputs[i] = Capture(field, chunkX, chunkY, chunkZ, step);
        }
        captureTimer.Stop();

        // Warm the mesher JIT and both ThreadPool workers before collecting data.
        ReturnMeshData(VoxelChunkMesher.BuildSurfaceData(inputs[0], palette));
        Task warmA = Task.Run(() => BuildAndRelease(inputs[0], palette));
        Task warmB = Task.Run(() => BuildAndRelease(inputs[1], palette));
        Task.WaitAll(warmA, warmB);

        double[] sequentialTimes = new double[iterations];
        double[] oneWorkerTimes = new double[iterations];
        double[] twoWorkerTimes = new double[iterations];
        int sequentialVertices = 0;
        int sequentialTriangles = 0;
        int oneWorkerVertices = 0;
        int oneWorkerTriangles = 0;
        int twoWorkerVertices = 0;
        int twoWorkerTriangles = 0;
        bool equivalent = true;

        for (int run = 0; run < iterations; run++)
        {
            int sequentialV = 0;
            int sequentialT = 0;
            Stopwatch sequentialTimer = Stopwatch.StartNew();
            foreach (VoxelChunkBuildInput input in inputs)
            {
                VoxelMeshData[] meshes =
                    VoxelChunkMesher.BuildSurfaceData(input, palette);
                try
                {
                    sequentialV += meshes.Sum(static mesh => mesh.VertexCount);
                    sequentialT += meshes.Sum(static mesh => mesh.TriangleCount);
                }
                finally
                {
                    ReturnMeshData(meshes);
                }
            }
            sequentialTimer.Stop();
            sequentialTimes[run] = sequentialTimer.Elapsed.TotalMilliseconds;

            int[] oneV = new int[chunkCount];
            int[] oneT = new int[chunkCount];
            Stopwatch oneTimer = Stopwatch.StartNew();
            Task oneWorker = Task.Run(() =>
                BuildQueue(inputs, palette, oneV, oneT, workerCount: 1));
            oneWorker.GetAwaiter().GetResult();
            oneTimer.Stop();
            oneWorkerTimes[run] = oneTimer.Elapsed.TotalMilliseconds;

            int[] twoV = new int[chunkCount];
            int[] twoT = new int[chunkCount];
            Stopwatch twoTimer = Stopwatch.StartNew();
            Task workerA = Task.Run(() =>
                BuildQueue(inputs, palette, twoV, twoT, workerCount: 2, workerIndex: 0));
            Task workerB = Task.Run(() =>
                BuildQueue(inputs, palette, twoV, twoT, workerCount: 2, workerIndex: 1));
            Task.WaitAll(workerA, workerB);
            twoTimer.Stop();
            twoWorkerTimes[run] = twoTimer.Elapsed.TotalMilliseconds;

            sequentialVertices = sequentialV;
            sequentialTriangles = sequentialT;
            oneWorkerVertices = oneV.Sum();
            oneWorkerTriangles = oneT.Sum();
            twoWorkerVertices = twoV.Sum();
            twoWorkerTriangles = twoT.Sum();
            equivalent &=
                sequentialVertices == oneWorkerVertices &&
                sequentialTriangles == oneWorkerTriangles &&
                sequentialVertices == twoWorkerVertices &&
                sequentialTriangles == twoWorkerTriangles;
        }

        Array.Sort(sequentialTimes);
        Array.Sort(oneWorkerTimes);
        Array.Sort(twoWorkerTimes);

        double sequentialMedian = sequentialTimes[iterations / 2];
        double oneMedian = oneWorkerTimes[iterations / 2];
        double twoMedian = twoWorkerTimes[iterations / 2];
        double speedupOne = sequentialMedian / Math.Max(0.001d, oneMedian);
        double speedupTwo = sequentialMedian / Math.Max(0.001d, twoMedian);
        double throughputTwo = chunkCount /
            Math.Max(0.001d, twoMedian / 1000d);

        Console.WriteLine("=== Warm CPU worker throughput benchmark (no GPU upload) ===");
        Console.WriteLine(
            $"chunks={chunkCount} lod={step} captureTotal={captureTimer.Elapsed.TotalMilliseconds:0.000} ms | runs={iterations}");
        Console.WriteLine(
            $"main-thread median/p95={sequentialMedian:0.000}/{sequentialTimes[^1]:0.000} ms");
        Console.WriteLine(
            $"1 worker median/p95={oneMedian:0.000}/{oneWorkerTimes[^1]:0.000} ms | speedup={speedupOne:0.00}x");
        Console.WriteLine(
            $"2 workers median/p95={twoMedian:0.000}/{twoWorkerTimes[^1]:0.000} ms | speedup={speedupTwo:0.00}x | throughput={throughputTwo:0.0} chunks/s");
        Console.WriteLine(
            $"geometry main={sequentialVertices}/{sequentialTriangles} | " +
            $"one={oneWorkerVertices}/{oneWorkerTriangles} | " +
            $"two={twoWorkerVertices}/{twoWorkerTriangles} | identical={equivalent}");

        if (!equivalent)
            throw new InvalidOperationException(
                "Background workers produced geometry different from sequential meshing.");
    }

    private static void BuildQueue(
        VoxelChunkBuildInput[] inputs,
        TerrainPalette palette,
        int[] vertices,
        int[] triangles,
        int workerCount,
        int workerIndex = 0)
    {
        for (int index = workerIndex; index < inputs.Length; index += workerCount)
        {
            VoxelMeshData[] meshes =
                VoxelChunkMesher.BuildSurfaceData(inputs[index], palette);
            try
            {
                vertices[index] = meshes.Sum(static mesh => mesh.VertexCount);
                triangles[index] = meshes.Sum(static mesh => mesh.TriangleCount);
            }
            finally
            {
                ReturnMeshData(meshes);
            }
        }
    }

    private static void BuildAndRelease(
        VoxelChunkBuildInput input,
        TerrainPalette palette)
    {
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, palette);
        ReturnMeshData(meshes);
    }

    private static void ReturnMeshData(VoxelMeshData[] meshes)
    {
        foreach (VoxelMeshData mesh in meshes)
            mesh.ReturnCpuArrays();
    }

    private static VoxelChunkBuildInput Capture(
        IVoxelFieldSource field,
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

    private sealed class SyntheticSaddleField : IVoxelFieldSource
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        private readonly int _step;

        public SyntheticSaddleField(int width, int height, int depth, int step)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _step = step;
        }

        public float GetDensity(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth)
                return -1f;

            int cellX = x / _step;
            int cellZ = z / _step;
            return ((cellX ^ cellZ) & 1) == 0 ? 1f : -1f;
        }

        public ushort GetMaterialId(int x, int y, int z) => 1;
    }

    private sealed class SyntheticTerrainField : IVoxelFieldSource
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int MaterialReadCount { get; private set; }

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

        public ushort GetMaterialId(int x, int y, int z)
        {
            MaterialReadCount++;
            return GetDensity(x, y, z) > 0f ? (ushort)1 : (ushort)0;
        }
    }
}
