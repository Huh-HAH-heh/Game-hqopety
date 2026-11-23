using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Core.Map;
using RimClone.Render.Raylib;

internal static class Program
{
    private static int Main()
    {
        int failures = 0;
        Console.WriteLine("=== RimClone flat voxel / greedy meshing tests ===");
        Console.WriteLine($"Runtime: {Environment.Version} | CPU: {Environment.ProcessorCount}");

        Run("Step=1 adjacent chunks capture identical shared samples",
            TestSharedDensityHalo, ref failures);
        Run("Greedy meshing merges a uniform terrain plane into one quad",
            TestGreedyPlaneReduction, ref failures);
        Run("Greedy meshing splits rectangles at material boundaries",
            TestGreedyMaterialBoundary, ref failures);
        Run("Greedy CUT cap is flat, finite and rectangular",
            TestGreedyCutCap, ref failures);
        Run("Adjacent surface chunks share an identical boundary",
            TestGreedySurfaceBoundary, ref failures);
        Run("Empty chunks skip material capture",
            TestEmptyChunkSkipsMaterials, ref failures);

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
            if (!passed)
                failures++;
        }
        catch (Exception exception)
        {
            failures++;
            Console.WriteLine($"[FAIL] {name}: {exception}");
        }
    }

    private static (bool Passed, string Detail) TestSharedDensityHalo()
    {
        SyntheticFlatField field = new(64, 64, 64);
        VoxelChunkBuildInput left = Capture(field, 1, 1, 1);
        VoxelChunkBuildInput right = Capture(field, 2, 1, 1);

        const int side = VoxelChunkBuildInput.SampleSide;
        const int plane = VoxelChunkBuildInput.SamplePlane;
        int mismatches = 0;

        for (int z = 0; z < side; z++)
        for (int y = 0; y < side; y++)
        {
            float a = left.DensitySamples[17 + y * side + z * plane];
            float b = right.DensitySamples[1 + y * side + z * plane];
            if (BitConverter.SingleToInt32Bits(a) !=
                BitConverter.SingleToInt32Bits(b))
            {
                mismatches++;
            }
        }

        return (
            mismatches == 0,
            $"shared plane mismatches={mismatches}, step=1");
    }

    private static (bool Passed, string Detail) TestGreedyPlaneReduction()
    {
        SyntheticFlatField field = new(64, 64, 64);
        VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        TerrainPalette palette = new();
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, palette);

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool normalsValid = HasFiniteUnitNormals(meshes);

            // A flat 16x16 terrain patch is represented by one rectangle:
            // four unique quad vertices and two indexed triangles.
            bool reduced = vertices == 4 && triangles == 2;
            return (
                reduced && normalsValid,
                $"meshes={meshes.Length}, vertices={vertices}, triangles={triangles}, normals={normalsValid}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestGreedyMaterialBoundary()
    {
        SyntheticFlatField field = new(64, 64, 64, materialSplit: 24);
        VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        TerrainPalette palette = new();
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, palette);

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);

            // Two materials divide the 16x16 plane into two mergeable rectangles.
            bool splitCorrectly = vertices == 8 && triangles == 4;
            return (
                splitCorrectly,
                $"vertices={vertices}, triangles={triangles}, expected=8/4");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestGreedyCutCap()
    {
        const float cutHeightMeters = 1f;
        SyntheticFlatField field = new(64, 64, 64);
        VoxelChunkBuildInput input = Capture(field, 1, 0, 1);
        TerrainPalette palette = new();
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildCutCapData(
            input,
            cutHeightMeters,
            palette);

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool finite = HasFiniteUnitNormals(meshes);
            bool flat = true;

            foreach (VoxelMeshData mesh in meshes)
            foreach (Vector3 vertex in mesh.Vertices)
            {
                if (!float.IsFinite(vertex.X) ||
                    !float.IsFinite(vertex.Y) ||
                    !float.IsFinite(vertex.Z) ||
                    MathF.Abs(vertex.Y - (cutHeightMeters + 0.006f)) > 1e-6f)
                {
                    flat = false;
                }
            }

            return (
                vertices == 4 && triangles == 2 && finite && flat,
                $"vertices={vertices}, triangles={triangles}, finiteNormals={finite}, flat={flat}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestGreedySurfaceBoundary()
    {
        SyntheticFlatField field = new(64, 64, 64);
        TerrainPalette palette = new();

        VoxelMeshData[] left = VoxelChunkMesher.BuildSurfaceData(
            Capture(field, 1, 1, 1), palette);
        VoxelMeshData[] right = VoxelChunkMesher.BuildSurfaceData(
            Capture(field, 2, 1, 1), palette);

        try
        {
            float sharedX =
                (2 * SparseVoxelField.ChunkSize) *
                SparseVoxelField.VoxelSizeMeters;
            const float surfaceY = 2f;

            HashSet<int> leftBoundary = CollectBoundaryZ(left, sharedX, surfaceY);
            HashSet<int> rightBoundary = CollectBoundaryZ(right, sharedX, surfaceY);
            bool matches = leftBoundary.Count > 0 &&
                leftBoundary.SetEquals(rightBoundary);

            return (
                matches,
                $"left={leftBoundary.Count}, right={rightBoundary.Count}, exactMatch={matches}");
        }
        finally
        {
            ReturnMeshData(left);
            ReturnMeshData(right);
        }
    }

    private static (bool Passed, string Detail) TestEmptyChunkSkipsMaterials()
    {
        SyntheticFlatField field = new(64, 64, 64, empty: true);
        VoxelChunkBuildInput input = new(field, 1, 1, 1);

        while (!input.DensityCaptureComplete)
            input.CaptureNext(field, 256, captureMaterials: false);

        bool empty = !input.HasPotentialSurface;
        if (empty)
            input.CaptureNext(field, 1, captureMaterials: false);

        bool passed = empty &&
            input.IsComplete &&
            field.MaterialReadCount == 0;

        return (
            passed,
            $"potentialSurface={input.HasPotentialSurface}, complete={input.IsComplete}, materialReads={field.MaterialReadCount}");
    }

    private static HashSet<int> CollectBoundaryZ(
        VoxelMeshData[] meshes,
        float x,
        float y)
    {
        HashSet<int> points = new();

        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 vertex in mesh.Vertices)
        {
            if (vertex.X == x && vertex.Y == y)
                points.Add(BitConverter.SingleToInt32Bits(vertex.Z));
        }

        return points;
    }

    private static bool HasFiniteUnitNormals(VoxelMeshData[] meshes)
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
                MathF.Abs(lengthSquared - 1f) > 1e-4f)
            {
                return false;
            }
        }

        return count > 0;
    }

    private static int CountVertices(VoxelMeshData[] meshes)
    {
        int count = 0;
        foreach (VoxelMeshData mesh in meshes)
            count += mesh.VertexCount;
        return count;
    }

    private static int CountTriangles(VoxelMeshData[] meshes)
    {
        int count = 0;
        foreach (VoxelMeshData mesh in meshes)
            count += mesh.TriangleCount;
        return count;
    }

    private static VoxelChunkBuildInput Capture(
        IVoxelFieldSource field,
        int chunkX,
        int chunkY,
        int chunkZ)
    {
        VoxelChunkBuildInput input = new(field, chunkX, chunkY, chunkZ);

        while (!input.IsComplete)
            input.CaptureNext(field, 4096);

        return input;
    }

    private static void ReturnMeshData(VoxelMeshData[] meshes)
    {
        foreach (VoxelMeshData mesh in meshes)
            mesh.ReturnCpuArrays();
    }

    private sealed class SyntheticFlatField : IVoxelFieldSource
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int MaterialReadCount { get; private set; }

        private readonly int _materialSplit;
        private readonly bool _empty;

        public SyntheticFlatField(
            int width,
            int height,
            int depth,
            int materialSplit = int.MaxValue,
            bool empty = false)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _materialSplit = materialSplit;
            _empty = empty;
        }

        public float GetDensity(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth ||
                _empty)
            {
                return -1f;
            }

            // A perfectly flat solid layer from the bottom to voxel Y=19.
            return y < 20 ? 1f : -1f;
        }

        public ushort GetMaterialId(int x, int y, int z)
        {
            MaterialReadCount++;

            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth ||
                _empty || y >= 20)
            {
                return 0;
            }

            return x < _materialSplit ? (ushort)1 : (ushort)2;
        }
    }
}
