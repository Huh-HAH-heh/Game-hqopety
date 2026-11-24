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
        Console.WriteLine("=== RimClone continuous SDF / Surface Nets tests ===");
        Console.WriteLine($"Runtime: {Environment.Version} | CPU: {Environment.ProcessorCount}");

        Run("Step=1 adjacent chunks capture identical shared samples",
            TestSharedDensityHalo, ref failures);
        Run("Surface Nets builds an indexed smooth terrain surface",
            TestSurfaceNetsPlane, ref failures);
        Run("Surface Nets preserves material boundaries",
            TestSurfaceNetsMaterialBoundary, ref failures);
        Run("SDF CUT generates a flat finite cap",
            TestSdfCutCap, ref failures);
        Run("Adjacent Surface Nets chunks share an identical halo boundary",
            TestSurfaceNetsBoundary, ref failures);
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
        SyntheticFlatField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput left = Capture(field, 1, 1, 1, 64);
        using VoxelChunkBuildInput right = Capture(field, 2, 1, 1, 64);

        int side = left.SampleSide;
        int plane = left.SamplePlane;
        int mismatches = 0;

        for (int z = 0; z < side; z++)
        for (int y = 0; y < side; y++)
        {
            float a = left.DensitySamples[65 + y * side + z * plane];
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

    private static (bool Passed, string Detail) TestSurfaceNetsPlane()
    {
        SyntheticFlatField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1, 64);
        TerrainPalette palette = new();
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, palette);

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool normalsValid = HasFiniteUnitNormals(meshes);
            bool liesOnSdfSurface = AllVerticesAtHeight(meshes, 8f);

            // Surface Nets keeps one shared vertex per crossing cell rather than
            // collapsing the terrain into an axis-aligned greedy rectangle.
            bool indexedSurface = vertices > 4 && triangles > 2;
            return (
                indexedSurface && normalsValid && liesOnSdfSurface,
                $"meshes={meshes.Length}, vertices={vertices}, triangles={triangles}, normals={normalsValid}, surfaceY={liesOnSdfSurface}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestSurfaceNetsMaterialBoundary()
    {
        SyntheticFlatField field = new(
            256, 256, 256,
            materialSplit: 96,
            surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1, 64);
        TerrainPalette palette = new();
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, palette);

        try
        {
            HashSet<uint> colors = new();
            foreach (VoxelMeshData mesh in meshes)
            {
                ReadOnlySpan<byte> rgba = mesh.Colors;
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    uint packed = (uint)rgba[i] |
                        ((uint)rgba[i + 1] << 8) |
                        ((uint)rgba[i + 2] << 16) |
                        ((uint)rgba[i + 3] << 24);
                    colors.Add(packed);
                }
            }

            bool splitCorrectly = colors.Count >= 2;
            return (
                splitCorrectly,
                $"meshes={meshes.Length}, vertices={CountVertices(meshes)}, materialColors={colors.Count}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestSdfCutCap()
    {
        const float cutHeightMeters = 5f;
        SyntheticFlatField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(
            field, 1, 0, 1, 64, cutHeightMeters);
        TerrainPalette palette = new();
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, palette);

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool finite = HasFiniteUnitNormals(meshes);
            bool flat = AllVerticesAtHeight(meshes, cutHeightMeters);
            bool upward = AllNormalsPointUp(meshes);

            return (
                vertices > 4 && triangles > 2 && finite && flat && upward,
                $"vertices={vertices}, triangles={triangles}, finiteNormals={finite}, flat={flat}, upward={upward}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestSurfaceNetsBoundary()
    {
        SyntheticFlatField field = new(256, 256, 256, surfaceHeight: 80);
        TerrainPalette palette = new();

        using VoxelChunkBuildInput leftInput = Capture(field, 1, 1, 1, 64);
        using VoxelChunkBuildInput rightInput = Capture(field, 2, 1, 1, 64);
        VoxelMeshData[] left = VoxelChunkMesher.BuildSurfaceData(leftInput, palette);
        VoxelMeshData[] right = VoxelChunkMesher.BuildSurfaceData(rightInput, palette);

        try
        {
            // The shared cell centre is half a voxel inside the left chunk,
            // and is also the right chunk's negative-X halo cell.
            float sharedCellX =
                (2 * 64) * SparseVoxelField.VoxelSizeMeters;
            const float surfaceY = 8f;

            HashSet<int> leftBoundary = CollectBoundaryZ(left, sharedCellX, surfaceY);
            HashSet<int> rightBoundary = CollectBoundaryZ(right, sharedCellX, surfaceY);
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
        SyntheticFlatField field = new(256, 256, 256, empty: true);
        using VoxelChunkBuildInput input = new(field, 1, 1, 1, 64);

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
            if (MathF.Abs(vertex.X - x) <= 1e-5f &&
                MathF.Abs(vertex.Y - y) <= 1e-5f)
                points.Add(BitConverter.SingleToInt32Bits(vertex.Z));
        }

        return points;
    }

    private static bool AllVerticesAtHeight(
        VoxelMeshData[] meshes,
        float height)
    {
        int count = 0;
        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 vertex in mesh.Vertices)
        {
            count++;
            if (!float.IsFinite(vertex.Y) ||
                MathF.Abs(vertex.Y - height) > 1e-5f)
                return false;
        }

        return count > 0;
    }

    private static bool AllNormalsPointUp(VoxelMeshData[] meshes)
    {
        int count = 0;
        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 normal in mesh.Normals)
        {
            count++;
            if (normal.Y < 0.999f)
                return false;
        }

        return count > 0;
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
        int chunkZ,
        int cellsPerAxis = VoxelChunkBuildInput.DefaultCellsPerAxis,
        float cutHeightMeters = float.PositiveInfinity)
    {
        VoxelChunkBuildInput input = new(
            field, chunkX, chunkY, chunkZ, cellsPerAxis, cutHeightMeters);

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
        private readonly int _surfaceHeight;

        public SyntheticFlatField(
            int width,
            int height,
            int depth,
            int materialSplit = int.MaxValue,
            bool empty = false,
            int surfaceHeight = 20)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _materialSplit = materialSplit;
            _empty = empty;
            _surfaceHeight = surfaceHeight;
        }

        public float GetDensity(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth ||
                _empty)
            {
                return -1f;
            }

            // A perfectly flat solid layer from the bottom to the configured surface height.
            return y < _surfaceHeight ? 1f : -1f;
        }

        public ushort GetMaterialId(int x, int y, int z)
        {
            MaterialReadCount++;

            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth ||
                _empty || y >= _surfaceHeight)
            {
                return 0;
            }

            return x < _materialSplit ? (ushort)1 : (ushort)2;
        }
    }
}
