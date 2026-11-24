using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;
using RimClone.Render.Raylib;

internal static class Program
{
    private static int Main()
    {
        int failures = 0;
        Console.WriteLine("=== RimClone byte-grid / greedy voxel tests ===");
        Console.WriteLine($"Runtime: {Environment.Version} | CPU: {Environment.ProcessorCount}");

        Run("16^3 input uses a flat 18^3 byte grid",
            TestFlatByteGrid, ref failures);
        Run("Adjacent chunks read identical shared halo blocks",
            TestSharedBlockHalo, ref failures);
        Run("Greedy meshing merges a flat block surface into one quad",
            TestGreedyPlane, ref failures);
        Run("Different block IDs keep material faces separate",
            TestGreedyMaterialBoundary, ref failures);
        Run("Voxel cut exposes a flat block cap",
            TestGreedyCutCap, ref failures);
        Run("Fully enclosed solid chunk emits no faces",
            TestEnclosedChunkEarlyOut, ref failures);
        Run("Greedy meshing creates the six internal tunnel walls",
            TestEnclosedTunnel, ref failures);
        Run("Empty chunk exits before greedy meshing",
            TestEmptyChunkEarlyOut, ref failures);
        Run("Greedy mesher writes only axis-aligned constant normals",
            TestAxisNormals, ref failures);

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

    private static (bool Passed, string Detail) TestFlatByteGrid()
    {
        SyntheticBlockField field = new(128, 128, 128, surfaceHeight: 80);
        using VoxelChunkBuildInput input = new(field, 1, 4, 1);
        input.CaptureAll(field);

        bool correct =
            input.CellsPerAxis == 16 &&
            input.SampleSide == 18 &&
            input.SamplePlane == 324 &&
            input.TotalSamples == 5832 &&
            input.Blocks.Length >= input.TotalSamples &&
            input.HasPotentialSurface &&
            input.IsCaptured;

        return (
            correct,
            $"cells={input.CellsPerAxis}^3, samples={input.SampleSide}^3, bytes={input.TotalSamples}, solid={input.HasPotentialSurface}");
    }

    private static (bool Passed, string Detail) TestSharedBlockHalo()
    {
        SyntheticBlockField field = new(128, 128, 128, surfaceHeight: 80);
        using VoxelChunkBuildInput left = Capture(field, 1, 4, 1);
        using VoxelChunkBuildInput right = Capture(field, 2, 4, 1);

        int side = left.SampleSide;
        int plane = left.SamplePlane;
        int mismatches = 0;

        for (int z = 0; z < side; z++)
        for (int y = 0; y < side; y++)
        {
            byte a = left.Blocks[side - 1 + y * side + z * plane];
            byte b = right.Blocks[1 + y * side + z * plane];
            if (a != b)
                mismatches++;
        }

        return (
            mismatches == 0,
            $"shared block plane mismatches={mismatches}");
    }

    private static (bool Passed, string Detail) TestGreedyPlane()
    {
        SyntheticBlockField field = new(128, 128, 128, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 4, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool flat = AllVerticesAtHeight(meshes, 8f);

            bool merged = vertices == 4 && triangles == 2;
            return (
                merged && flat,
                $"meshes={meshes.Length}, vertices={vertices}, triangles={triangles}, topPlane={flat}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestGreedyMaterialBoundary()
    {
        SyntheticBlockField field = new(
            128, 128, 128,
            materialSplit: 24,
            surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 4, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            HashSet<uint> colors = CollectColors(meshes);
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);

            bool split = vertices == 8 && triangles == 4 && colors.Count == 2;
            return (
                split,
                $"vertices={vertices}, triangles={triangles}, blockColors={colors.Count}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestGreedyCutCap()
    {
        const int cutLayer = 50;
        const float cutHeightMeters = cutLayer * SparseVoxelField.VoxelSizeMeters;
        SyntheticBlockField field = new(128, 128, 128, surfaceHeight: 80);

        using VoxelChunkBuildInput input = new(field, 1, 3, 1, cutLayer);
        input.CaptureAll(field);

        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool flat = AllVerticesAtHeight(meshes, cutHeightMeters);
            bool upward = AllNormalsPointUp(meshes);

            return (
                vertices == 4 && triangles == 2 && flat && upward,
                $"vertices={vertices}, triangles={triangles}, flat={flat}, upward={upward}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestEnclosedChunkEarlyOut()
    {
        SyntheticBlockField field = new(128, 128, 128, solidAll: true);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            bool noFaces = meshes.Length == 0;
            return (
                input.HasPotentialSurface && noFaces,
                $"solid={input.HasPotentialSurface}, meshes={meshes.Length}, faces=0");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestEnclosedTunnel()
    {
        SyntheticBlockField field = new(128, 128, 128, solidAll: true, tunnel: true);
        using VoxelChunkBuildInput input = Capture(field, 1, 4, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool internalWall = false;

            foreach (VoxelMeshData mesh in meshes)
            {
                ReadOnlySpan<Vector3> positions = mesh.Vertices;
                ReadOnlySpan<Vector3> normals = mesh.Normals;
                for (int i = 0; i < positions.Length; i++)
                {
                    if (MathF.Abs(positions[i].X - 2f) < 1e-5f &&
                        positions[i].Y >= 6.8f && positions[i].Y <= 7.2f &&
                        positions[i].Z >= 2f && positions[i].Z <= 2.8f &&
                        normals[i] == Vector3.UnitX)
                    {
                        internalWall = true;
                        break;
                    }
                }

                if (internalWall)
                    break;
            }

            return (
                vertices == 24 && triangles == 12 && internalWall,
                $"vertices={vertices}, triangles={triangles}, internalTunnelWall={internalWall}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestEmptyChunkEarlyOut()
    {
        SyntheticBlockField field = new(128, 128, 128, empty: true);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        input.CaptureAll(field);

        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            bool empty = !input.HasPotentialSurface && meshes.Length == 0;
            return (
                empty && field.MaterialReadCount == input.TotalSamples,
                $"potentialSurface={input.HasPotentialSurface}, meshes={meshes.Length}, blockReads={field.MaterialReadCount}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestAxisNormals()
    {
        SyntheticBlockField field = new(128, 128, 128, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 4, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(
            input, new TerrainPalette());

        try
        {
            int count = 0;
            foreach (VoxelMeshData mesh in meshes)
            foreach (Vector3 normal in mesh.Normals)
            {
                count++;
                bool axisAligned =
                    (normal.X == 1f || normal.X == -1f) && normal.Y == 0f && normal.Z == 0f ||
                    (normal.Y == 1f || normal.Y == -1f) && normal.X == 0f && normal.Z == 0f ||
                    (normal.Z == 1f || normal.Z == -1f) && normal.X == 0f && normal.Y == 0f;

                if (!axisAligned)
                    return (false, $"invalid normal={normal}");
            }

            return (count > 0, $"normals={count}, all-axis-aligned={count > 0}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static HashSet<uint> CollectColors(VoxelMeshData[] meshes)
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

        return colors;
    }

    private static bool AllVerticesAtHeight(VoxelMeshData[] meshes, float height)
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
            if (normal != Vector3.UnitY)
                return false;
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
        IVoxelFieldSource source,
        int chunkX,
        int chunkY,
        int chunkZ,
        int cutLayer = int.MinValue)
    {
        VoxelChunkBuildInput input = new(
            source, chunkX, chunkY, chunkZ, cutLayer);
        input.CaptureAll(source);
        return input;
    }

    private static void ReturnMeshData(VoxelMeshData[] meshes)
    {
        foreach (VoxelMeshData mesh in meshes)
            mesh.ReturnCpuArrays();
    }

    private sealed class SyntheticBlockField : IVoxelFieldSource
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int MaterialReadCount { get; private set; }

        private readonly int _materialSplit;
        private readonly bool _empty;
        private readonly bool _solidAll;
        private readonly bool _tunnel;
        private readonly int _surfaceHeight;

        public SyntheticBlockField(
            int width,
            int height,
            int depth,
            int materialSplit = int.MaxValue,
            bool empty = false,
            bool solidAll = false,
            bool tunnel = false,
            int surfaceHeight = 20)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _materialSplit = materialSplit;
            _empty = empty;
            _solidAll = solidAll;
            _tunnel = tunnel;
            _surfaceHeight = surfaceHeight;
        }

        public float GetDensity(int x, int y, int z) =>
            GetMaterialId(x, y, z) == 0 ? -1f : 1f;

        public ushort GetMaterialId(int x, int y, int z)
        {
            MaterialReadCount++;

            if (x < 0 || y < 0 || z < 0 ||
                x >= Width || y >= Height || z >= Depth ||
                _empty ||
                (!_solidAll && y >= _surfaceHeight))
            {
                return 0;
            }

            if (_tunnel &&
                x >= 20 && x < 28 &&
                y >= 68 && y < 72 &&
                z >= 20 && z < 28)
            {
                return 0;
            }

            return x < _materialSplit ? (ushort)1 : (ushort)2;
        }
    }
}
