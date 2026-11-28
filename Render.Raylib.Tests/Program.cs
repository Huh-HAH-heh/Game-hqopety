using System;
using System.Numerics;
using System.Threading;
using Core.Map;
using RimClone.Render.Raylib;

internal static class Program
{
    private static int Main()
    {
        int failures = 0;
        Console.WriteLine("=== RimClone Surface Nets and density cut-section tests ===");
        Console.WriteLine($"Runtime: {Environment.Version} | CPU: {Environment.ProcessorCount}");

        Run("Chunk input captures a flat 67^3 density window", TestDensityCapture, ref failures);
        Run("Adjacent chunks sample the same shared halo plane", TestSharedHalo, ref failures);
        Run("Flat procedural density produces a continuous horizontal surface", TestFlatSurface, ref failures);
        Run("Cut plane does not modify or regenerate the terrain surface", TestCutSurface, ref failures);
        Run("Density-derived cut geometry fills solids and preserves tunnel openings", TestCutSurfaceGeometry, ref failures);
        Run("Cut contour uses linear density interpolation", TestCutSurfaceInterpolation, ref failures);
        Run("Enclosed solid chunk exits without creating a surface", TestEnclosedSolid, ref failures);
        Run("Three-dimensional tunnel walls are meshed inside solid terrain", TestTunnelSurface, ref failures);
        Run("Empty chunks exit before Surface Nets", TestEmptyField, ref failures);
        Run("Density capture observes cancellation", TestCaptureCancellation, ref failures);
        Run("Surface normals are finite and normalized", TestNormalQuality, ref failures);

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

    private static (bool Passed, string Detail) TestDensityCapture()
    {
        SyntheticDensityField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);

        bool correct =
            input.CellsPerAxis == 64 &&
            input.SampleSide == 67 &&
            input.SamplePlane == 4489 &&
            input.TotalSamples == 300763 &&
            input.Densities.Length >= input.TotalSamples &&
            input.HasPotentialSurface &&
            input.IsCaptured;

        return (
            correct,
            $"cells={input.CellsPerAxis}^3 samples={input.SampleSide}^3 total={input.TotalSamples} potential={input.HasPotentialSurface}");
    }

    private static (bool Passed, string Detail) TestSharedHalo()
    {
        SyntheticDensityField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput left = Capture(field, 1, 1, 1);
        using VoxelChunkBuildInput right = Capture(field, 2, 1, 1);

        int mismatches = 0;
        for (int z = -1; z <= 65; z++)
        for (int y = -1; y <= 65; y++)
        {
            if (MathF.Abs(
                left.GetDensity(64, y, z) -
                right.GetDensity(0, y, z)) > 1e-6f)
            {
                mismatches++;
            }
        }

        return (mismatches == 0, $"shared density samples mismatches={mismatches}");
    }

    private static (bool Passed, string Detail) TestFlatSurface()
    {
        SyntheticDensityField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, new TerrainPalette());

        try
        {
            int vertices = CountVertices(meshes);
            int triangles = CountTriangles(meshes);
            bool flat = AllVerticesAtHeight(meshes, 8f);
            bool upward = AllNormalsPointUp(meshes);

            return (
                vertices > 0 && triangles > 0 && flat && upward,
                $"meshes={meshes.Length} vertices={vertices} triangles={triangles} y=8m flat={flat} upward={upward}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestCutSurface()
    {
        const float cutHeightMeters = 5f;
        const float terrainHeightMeters = 8f;
        SyntheticDensityField field = new(256, 256, 256, surfaceHeight: 80);

        using VoxelChunkBuildInput input = new(
            field, 1, 1, 1, cellsPerAxis: 64);
        input.CaptureAll(field);

        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, new TerrainPalette());
        try
        {
            bool originalSurfaceRemains = AllVerticesAtHeight(meshes, terrainHeightMeters);
            bool noGeneratedCutGeometry = !HasUpwardSurfaceAtHeight(meshes, cutHeightMeters);
            return (
                originalSurfaceRemains && noGeneratedCutGeometry,
                $"terrainY={terrainHeightMeters:0.0}m cutY={cutHeightMeters:0.0}m " +
                $"originalSurface={originalSurfaceRemains} generatedCutMesh={HasUpwardSurfaceAtHeight(meshes, cutHeightMeters)} " +
                $"vertices={CountVertices(meshes)}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestCutSurfaceGeometry()
    {
        const int cutLayer = 50;
        SyntheticDensityField field = new(
            256, 256, 256,
            surfaceHeight: 80,
            sectionTunnel: true);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildCutSurfaceData(
            field, cutLayer, new TerrainPalette(), step: 4);

        try
        {
            bool solidCovered = IsCutSurfaceCovered(meshes, 8.05f, 8.05f);
            bool tunnelOpen = !IsCutSurfaceCovered(meshes, 9.45f, 9.45f);
            bool upward = AllNormalsPointUp(meshes);
            int triangles = CountTriangles(meshes);

            return (
                solidCovered && tunnelOpen && upward && triangles > 0,
                $"meshes={meshes.Length} triangles={triangles} " +
                $"solidCovered={solidCovered} tunnelOpen={tunnelOpen} upward={upward}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestCutSurfaceInterpolation()
    {
        LinearCutField field = new(128, 64, 128);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildCutSurfaceData(
            field, layer: 32, palette: new TerrainPalette(), step: 4);

        try
        {
            const float expectedX = 5.025f;
            bool interpolated = false;
            foreach (VoxelMeshData mesh in meshes)
            foreach (Vector3 vertex in mesh.Vertices)
            {
                if (MathF.Abs(vertex.X - expectedX) <= 0.001f)
                {
                    interpolated = true;
                    break;
                }
            }

            return (
                interpolated && CountTriangles(meshes) > 0,
                $"expectedContourX={expectedX:0.000} interpolated={interpolated} " +
                $"meshes={meshes.Length} triangles={CountTriangles(meshes)}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static bool IsCutSurfaceCovered(
        VoxelMeshData[] meshes,
        float x,
        float z)
    {
        foreach (VoxelMeshData mesh in meshes)
        {
            ReadOnlySpan<Vector3> vertices = mesh.Vertices;
            ReadOnlySpan<ushort> indices = mesh.Indices;
            for (int i = 0; i < indices.Length; i += 3)
            {
                int ia = indices[i] - mesh.IndexBase;
                int ib = indices[i + 1] - mesh.IndexBase;
                int ic = indices[i + 2] - mesh.IndexBase;
                if ((uint)ia >= (uint)vertices.Length ||
                    (uint)ib >= (uint)vertices.Length ||
                    (uint)ic >= (uint)vertices.Length)
                    continue;

                if (PointInTriangleXZ(x, z,
                    vertices[ia], vertices[ib], vertices[ic]))
                    return true;
            }
        }

        return false;
    }

    private static bool PointInTriangleXZ(
        float x,
        float z,
        Vector3 a,
        Vector3 b,
        Vector3 c)
    {
        static float Cross(float ax, float az, float bx, float bz, float px, float pz) =>
            (bx - ax) * (pz - az) - (bz - az) * (px - ax);

        float area = Cross(a.X, a.Z, b.X, b.Z, c.X, c.Z);
        const float epsilon = 0.00001f;
        if (MathF.Abs(area) <= epsilon)
            return false;

        float ab = Cross(a.X, a.Z, b.X, b.Z, x, z);
        float bc = Cross(b.X, b.Z, c.X, c.Z, x, z);
        float ca = Cross(c.X, c.Z, a.X, a.Z, x, z);
        bool negative = ab < -epsilon || bc < -epsilon || ca < -epsilon;
        bool positive = ab > epsilon || bc > epsilon || ca > epsilon;
        return !(negative && positive);
    }

    private static (bool Passed, string Detail) TestEnclosedSolid()
    {
        SyntheticDensityField field = new(256, 256, 256, solidAll: true);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, new TerrainPalette());

        try
        {
            bool noSurface = !input.HasPotentialSurface && meshes.Length == 0;
            return (noSurface, $"potential={input.HasPotentialSurface} meshes={meshes.Length}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestTunnelSurface()
    {
        SyntheticDensityField field = new(256, 256, 256, solidAll: true, tunnel: true);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, new TerrainPalette());

        try
        {
            bool hasInteriorSurface = CountVertices(meshes) > 0 &&
                HasVertexNear(meshes, new Vector3(9.0f, 9.0f, 9.0f), 0.8f);
            return (
                input.HasPotentialSurface && hasInteriorSurface,
                $"potential={input.HasPotentialSurface} vertices={CountVertices(meshes)} tunnelWall={hasInteriorSurface}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestEmptyField()
    {
        SyntheticDensityField field = new(256, 256, 256, empty: true);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, new TerrainPalette());

        try
        {
            bool empty = !input.HasPotentialSurface && meshes.Length == 0;
            return (empty, $"potential={input.HasPotentialSurface} meshes={meshes.Length}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static (bool Passed, string Detail) TestCaptureCancellation()
    {
        SyntheticDensityField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput input = new(field, 1, 1, 1);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        try
        {
            input.CaptureAll(cancellation.Token);
            return (false, "capture ignored a pre-cancelled token");
        }
        catch (OperationCanceledException)
        {
            return (true, "capture stopped before sampling");
        }
    }

    private static (bool Passed, string Detail) TestNormalQuality()
    {
        SyntheticDensityField field = new(256, 256, 256, surfaceHeight: 80);
        using VoxelChunkBuildInput input = Capture(field, 1, 1, 1);
        VoxelMeshData[] meshes = VoxelChunkMesher.BuildSurfaceData(input, new TerrainPalette());

        try
        {
            int count = 0;
            foreach (VoxelMeshData mesh in meshes)
            foreach (Vector3 normal in mesh.Normals)
            {
                count++;
                if (!float.IsFinite(normal.X) ||
                    !float.IsFinite(normal.Y) ||
                    !float.IsFinite(normal.Z) ||
                    MathF.Abs(normal.LengthSquared() - 1f) > 0.001f)
                {
                    return (false, $"invalid normal={normal}");
                }
            }

            return (count > 0, $"normals={count} all-finite-unit={count > 0}");
        }
        finally
        {
            ReturnMeshData(meshes);
        }
    }

    private static bool HasUpwardSurfaceAtHeight(VoxelMeshData[] meshes, float height)
    {
        foreach (VoxelMeshData mesh in meshes)
        {
            ReadOnlySpan<Vector3> positions = mesh.Vertices;
            ReadOnlySpan<Vector3> normals = mesh.Normals;
            for (int i = 0; i < positions.Length; i++)
            {
                if (MathF.Abs(positions[i].Y - height) <= 0.001f &&
                    normals[i].Y > 0.9f)
                    return true;
            }
        }

        return false;
    }

    private static bool HasHorizontalTriangleCoveringXZ(
        VoxelMeshData[] meshes,
        float height,
        float x,
        float z)
    {
        foreach (VoxelMeshData mesh in meshes)
        {
            ReadOnlySpan<Vector3> vertices = mesh.Vertices;
            ReadOnlySpan<ushort> indices = mesh.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int ia = indices[i] - mesh.IndexBase;
                int ib = indices[i + 1] - mesh.IndexBase;
                int ic = indices[i + 2] - mesh.IndexBase;
                if ((uint)ia >= (uint)vertices.Length ||
                    (uint)ib >= (uint)vertices.Length ||
                    (uint)ic >= (uint)vertices.Length)
                    continue;

                Vector3 a = vertices[ia];
                Vector3 b = vertices[ib];
                Vector3 c = vertices[ic];
                if (MathF.Abs(a.Y - height) > 0.001f ||
                    MathF.Abs(b.Y - height) > 0.001f ||
                    MathF.Abs(c.Y - height) > 0.001f)
                    continue;

                float ab = (x - b.X) * (a.Z - b.Z) -
                    (a.X - b.X) * (z - b.Z);
                float bc = (x - c.X) * (b.Z - c.Z) -
                    (b.X - c.X) * (z - c.Z);
                float ca = (x - a.X) * (c.Z - a.Z) -
                    (c.X - a.X) * (z - a.Z);

                bool hasNegative = ab < -0.0001f ||
                    bc < -0.0001f || ca < -0.0001f;
                bool hasPositive = ab > 0.0001f ||
                    bc > 0.0001f || ca > 0.0001f;
                if (!(hasNegative && hasPositive))
                    return true;
            }
        }

        return false;
    }

    private static bool HasVertexNear(VoxelMeshData[] meshes, Vector3 point, float radius)
    {
        float radiusSquared = radius * radius;
        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 vertex in mesh.Vertices)
            if (Vector3.DistanceSquared(vertex, point) <= radiusSquared)
                return true;

        return false;
    }

    private static bool AllVerticesAtHeight(VoxelMeshData[] meshes, float height)
    {
        int count = 0;
        foreach (VoxelMeshData mesh in meshes)
        foreach (Vector3 vertex in mesh.Vertices)
        {
            count++;
            if (!float.IsFinite(vertex.Y) || MathF.Abs(vertex.Y - height) > 0.001f)
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
            if (normal.Y < 0.9f)
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
        int chunkZ)
    {
        VoxelChunkBuildInput input = new(source, chunkX, chunkY, chunkZ);
        input.CaptureAll(source);
        return input;
    }

    private static void ReturnMeshData(VoxelMeshData[] meshes)
    {
        foreach (VoxelMeshData mesh in meshes)
            mesh.ReturnCpuArrays();
    }

    private sealed class LinearCutField : IVoxelFieldSource
    {
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        public LinearCutField(int width, int height, int depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
        }

        public float GetDensity(int x, int y, int z)
        {
            if ((uint)x >= (uint)Width ||
                (uint)y >= (uint)Height ||
                (uint)z >= (uint)Depth)
                return -1f;

            return 50.25f - (x + 0.5f);
        }

        public ushort GetMaterialId(int x, int y, int z) =>
            GetDensity(x, y, z) > 0f ? (ushort)1 : (ushort)0;
    }

    private sealed class SyntheticDensityField : IVoxelFieldSource
    {
        private readonly int _surfaceHeight;
        private readonly bool _empty;
        private readonly bool _solidAll;
        private readonly bool _tunnel;
        private readonly bool _sectionTunnel;

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        public SyntheticDensityField(
            int width,
            int height,
            int depth,
            int surfaceHeight = 20,
            bool empty = false,
            bool solidAll = false,
            bool tunnel = false,
            bool sectionTunnel = false)
        {
            Width = width;
            Height = height;
            Depth = depth;
            _surfaceHeight = surfaceHeight;
            _empty = empty;
            _solidAll = solidAll;
            _tunnel = tunnel;
            _sectionTunnel = sectionTunnel;
        }

        public float GetDensity(int x, int y, int z)
        {
            if ((uint)x >= Width || (uint)y >= Height || (uint)z >= Depth || _empty)
                return -1f;

            if (_tunnel &&
                x >= 90 && x < 98 &&
                y >= 90 && y < 94 &&
                z >= 90 && z < 98)
                return -1f;

            if (_solidAll)
                return 1f;

            if (_sectionTunnel &&
                x >= 90 && x < 98 &&
                y >= 43 && y < 58 &&
                z >= 90 && z < 98)
                return -1f;

            return _surfaceHeight - (y + 0.5f);
        }

        public ushort GetMaterialId(int x, int y, int z) =>
            GetDensity(x, y, z) > 0f ? (ushort)1 : (ushort)0;
    }
}
