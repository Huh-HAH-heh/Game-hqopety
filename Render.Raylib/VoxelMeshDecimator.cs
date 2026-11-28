using System;
using System.Buffers;
using System.Numerics;
using System.Threading;

namespace RimClone.Render.Raylib;

internal readonly record struct VoxelMeshDecimationStats(
    int OriginalVertices,
    int OptimizedVertices,
    int OriginalTriangles,
    int OptimizedTriangles,
    int OriginalIndices,
    int OptimizedIndices,
    long OriginalBytes,
    long OptimizedBytes,
    float MaximumErrorMeters,
    int PreservedBoundaryVertices,
    int Collapses);

/// <summary>
/// Conservative QEM edge collapse. Open boundaries, material edges and
/// non-manifold vertices are locked. Only independent interior collapses
/// satisfying the manifold link condition and local normal/error tests are used.
/// </summary>
internal static class VoxelMeshDecimator
{
    private const float MinimumAreaSquared = 1e-12f;
    private const double MinimumDeterminant = 1e-12d;

    private readonly struct EdgeRef : IComparable<EdgeRef>
    {
        public readonly int A;
        public readonly int B;
        public readonly int Opposite;

        public EdgeRef(int a, int b, int opposite)
        {
            if (a <= b)
            {
                A = a;
                B = b;
            }
            else
            {
                A = b;
                B = a;
            }

            Opposite = opposite;
        }

        public int CompareTo(EdgeRef other)
        {
            int result = A.CompareTo(other.A);
            if (result != 0) return result;
            result = B.CompareTo(other.B);
            return result != 0 ? result : Opposite.CompareTo(other.Opposite);
        }
    }

    private struct EdgeInfo
    {
        public int A;
        public int B;
        public int Opposite0;
        public int Opposite1;
        public int FaceCount;
    }

    private struct Quadric
    {
        public double M00, M01, M02, M03;
        public double M11, M12, M13;
        public double M22, M23;
        public double M33;

        public void AddPlane(Vector3 normal, float d, double weight)
        {
            double x = normal.X;
            double y = normal.Y;
            double z = normal.Z;
            double w = d;

            M00 += weight * x * x;
            M01 += weight * x * y;
            M02 += weight * x * z;
            M03 += weight * x * w;
            M11 += weight * y * y;
            M12 += weight * y * z;
            M13 += weight * y * w;
            M22 += weight * z * z;
            M23 += weight * z * w;
            M33 += weight * w * w;
        }

        public void Add(in Quadric other)
        {
            M00 += other.M00;
            M01 += other.M01;
            M02 += other.M02;
            M03 += other.M03;
            M11 += other.M11;
            M12 += other.M12;
            M13 += other.M13;
            M22 += other.M22;
            M23 += other.M23;
            M33 += other.M33;
        }

        public double Evaluate(Vector3 point)
        {
            double x = point.X;
            double y = point.Y;
            double z = point.Z;
            return
                M00 * x * x + 2d * M01 * x * y + 2d * M02 * x * z +
                2d * M03 * x + M11 * y * y + 2d * M12 * y * z +
                2d * M13 * y + M22 * z * z + 2d * M23 * z + M33;
        }

        public bool TryOptimalPosition(out Vector3 position)
        {
            double a = M00;
            double b = M01;
            double c = M02;
            double d = M11;
            double e = M12;
            double f = M22;

            double rx = -M03;
            double ry = -M13;
            double rz = -M23;

            double determinant =
                a * (d * f - e * e) -
                b * (b * f - c * e) +
                c * (b * e - c * d);

            if (Math.Abs(determinant) <= MinimumDeterminant)
            {
                position = default;
                return false;
            }

            double inverse = 1d / determinant;
            double x = (
                rx * (d * f - e * e) -
                b * (ry * f - e * rz) +
                c * (ry * e - d * rz)) * inverse;
            double y = (
                a * (ry * f - e * rz) -
                rx * (b * f - c * e) +
                c * (b * rz - c * ry)) * inverse;
            double z = (
                a * (d * rz - e * ry) -
                b * (b * rz - c * ry) +
                rx * (b * e - c * d)) * inverse;

            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) ||
                x < -float.MaxValue || x > float.MaxValue ||
                y < -float.MaxValue || y > float.MaxValue ||
                z < -float.MaxValue || z > float.MaxValue)
            {
                position = default;
                return false;
            }

            position = new Vector3((float)x, (float)y, (float)z);
            return true;
        }
    }

    private readonly struct CollapseCandidate : IComparable<CollapseCandidate>
    {
        public readonly int A;
        public readonly int B;
        public readonly Vector3 Position;
        public readonly double Cost;
        public readonly float Error;

        public CollapseCandidate(
            int a, int b, Vector3 position, double cost, float error)
        {
            A = a;
            B = b;
            Position = position;
            Cost = cost;
            Error = error;
        }

        public int CompareTo(CollapseCandidate other) =>
            Cost.CompareTo(other.Cost);
    }

    public static VoxelMeshData[] Simplify(
        VoxelMeshData[] source,
        float maximumErrorMeters,
        out VoxelMeshDecimationStats stats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!float.IsFinite(maximumErrorMeters) || maximumErrorMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumErrorMeters));

        int originalVertices = 0;
        int originalIndices = 0;
        int originalTriangles = 0;
        long originalBytes = 0;
        int optimizedVertices = 0;
        int optimizedIndices = 0;
        int optimizedTriangles = 0;
        long optimizedBytes = 0;
        int preservedBoundaryVertices = 0;
        int collapses = 0;
        float maximumError = 0f;

        VoxelMeshData[] result = new VoxelMeshData[source.Length];

        try
        {
            for (int i = 0; i < source.Length; i++)
            {
            VoxelMeshData mesh = source[i];
            originalVertices += mesh.VertexCount;
            originalIndices += mesh.IndexCount;
            originalTriangles += mesh.TriangleCount;
            originalBytes += EstimateBytes(mesh.VertexCount, mesh.IndexCount);

            if (TrySimplifyPacket(
                    mesh,
                    maximumErrorMeters,
                    out VoxelMeshData? optimized,
                    out int boundaryCount,
                    out int packetCollapses,
                    out float packetError,
                    cancellationToken))
            {
                result[i] = optimized!;
                mesh.ReturnCpuArrays();
                collapses += packetCollapses;
                preservedBoundaryVertices += boundaryCount;
                maximumError = Math.Max(maximumError, packetError);
            }
            else
            {
                result[i] = mesh;
                preservedBoundaryVertices += boundaryCount;
            }

            VoxelMeshData output = result[i];
            optimizedVertices += output.VertexCount;
            optimizedIndices += output.IndexCount;
            optimizedTriangles += output.TriangleCount;
                optimizedBytes += EstimateBytes(output.VertexCount, output.IndexCount);
            }
        }
        catch
        {
            for (int i = 0; i < result.Length; i++)
                if (result[i] != null && !ReferenceEquals(result[i], source[i]))
                    result[i].ReturnCpuArrays();
            throw;
        }

        stats = new VoxelMeshDecimationStats(
            originalVertices,
            optimizedVertices,
            originalTriangles,
            optimizedTriangles,
            originalIndices,
            optimizedIndices,
            originalBytes,
            optimizedBytes,
            maximumError,
            preservedBoundaryVertices,
            collapses);

        return result;
    }

    private static bool TrySimplifyPacket(
        VoxelMeshData source,
        float maximumErrorMeters,
        out VoxelMeshData? optimized,
        out int preservedBoundaryVertices,
        out int collapseCount,
        out float maximumAcceptedError,
        CancellationToken cancellationToken)
    {
        optimized = null;
        preservedBoundaryVertices = 0;
        collapseCount = 0;
        maximumAcceptedError = 0f;

        int vertexCount = source.VertexCount;
        int indexCount = source.IndexCount;
        int triangleCount = indexCount / 3;
        if (vertexCount < 8 || triangleCount < 8 || indexCount % 3 != 0)
            return false;

        Vector3[] positions = ArrayPool<Vector3>.Shared.Rent(vertexCount);
        Vector3[] normals = ArrayPool<Vector3>.Shared.Rent(vertexCount);
        byte[] colors = ArrayPool<byte>.Shared.Rent(checked(vertexCount * 4));
        int[] triangles = ArrayPool<int>.Shared.Rent(indexCount);
        Vector3[] faceNormals = ArrayPool<Vector3>.Shared.Rent(triangleCount);
        float[] facePlaneOffsets = ArrayPool<float>.Shared.Rent(triangleCount);
        EdgeRef[] edgeRefs = ArrayPool<EdgeRef>.Shared.Rent(indexCount);
        EdgeInfo[] edges = ArrayPool<EdgeInfo>.Shared.Rent(indexCount);
        int[] degree = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] neighborOffsets = ArrayPool<int>.Shared.Rent(vertexCount + 1);
        int[] neighborCursor = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] neighbors = ArrayPool<int>.Shared.Rent(checked(indexCount * 2));
        int[] faceCounts = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] faceOffsets = ArrayPool<int>.Shared.Rent(vertexCount + 1);
        int[] faceCursor = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] faceIds = ArrayPool<int>.Shared.Rent(indexCount);
        byte[] boundary = ArrayPool<byte>.Shared.Rent(vertexCount);
        byte[] materialFeature = ArrayPool<byte>.Shared.Rent(vertexCount);
        byte[] blocked = ArrayPool<byte>.Shared.Rent(vertexCount);
        int[] neighborMarks = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] collapseTo = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] remap = ArrayPool<int>.Shared.Rent(vertexCount);
        Quadric[] quadrics = ArrayPool<Quadric>.Shared.Rent(vertexCount);
        CollapseCandidate[] candidates = ArrayPool<CollapseCandidate>.Shared.Rent(indexCount);

        Vector3[]? outputVertices = null;
        Vector3[]? outputNormals = null;
        byte[]? outputColors = null;
        ushort[]? outputIndices = null;
        bool outputTransferred = false;

        try
        {
            source.Vertices.CopyTo(positions);
            source.Normals.CopyTo(normals);
            source.Colors.CopyTo(colors);

            ReadOnlySpan<ushort> sourceIndices = source.Indices;
            for (int i = 0; i < indexCount; i++)
            {
                int local = sourceIndices[i] - source.IndexBase;
                if ((uint)local >= (uint)vertexCount)
                    return false;
                triangles[i] = local;
            }

            Array.Clear(degree, 0, vertexCount);
            Array.Clear(faceCounts, 0, vertexCount);
            Array.Clear(boundary, 0, vertexCount);
            Array.Clear(materialFeature, 0, vertexCount);
            Array.Clear(blocked, 0, vertexCount);
            Array.Clear(neighborMarks, 0, vertexCount);
            Array.Clear(quadrics, 0, vertexCount);
            Array.Fill(collapseTo, -1, 0, vertexCount);

            // Build exact triangle planes and per-vertex face lists.
            for (int face = 0; face < triangleCount; face++)
            {
                if ((face & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int offset = face * 3;
                int a = triangles[offset];
                int b = triangles[offset + 1];
                int c = triangles[offset + 2];

                faceCounts[a]++;
                faceCounts[b]++;
                faceCounts[c]++;

                Vector3 cross = Vector3.Cross(
                    positions[b] - positions[a],
                    positions[c] - positions[a]);
                float lengthSquared = cross.LengthSquared();
                if (lengthSquared <= MinimumAreaSquared)
                {
                    faceNormals[face] = Vector3.Zero;
                    facePlaneOffsets[face] = 0f;
                    continue;
                }

                Vector3 normal = cross / MathF.Sqrt(lengthSquared);
                float planeOffset = -Vector3.Dot(normal, positions[a]);
                faceNormals[face] = normal;
                facePlaneOffsets[face] = planeOffset;

                double weight = Math.Sqrt(lengthSquared) * 0.5d;
                quadrics[a].AddPlane(normal, planeOffset, weight);
                quadrics[b].AddPlane(normal, planeOffset, weight);
                quadrics[c].AddPlane(normal, planeOffset, weight);
            }

            int faceTotal = 0;
            faceOffsets[0] = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                faceTotal += faceCounts[i];
                faceOffsets[i + 1] = faceTotal;
                faceCursor[i] = faceOffsets[i];
            }

            for (int face = 0; face < triangleCount; face++)
            {
                if ((face & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int offset = face * 3;
                faceIds[faceCursor[triangles[offset]]++] = face;
                faceIds[faceCursor[triangles[offset + 1]]++] = face;
                faceIds[faceCursor[triangles[offset + 2]]++] = face;
            }

            // Sort all undirected edges. This uses pooled flat arrays instead of
            // one Dictionary/HashSet allocation per vertex or per edge.
            int edgeRefCount = 0;
            for (int face = 0; face < triangleCount; face++)
            {
                if ((face & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int offset = face * 3;
                int a = triangles[offset];
                int b = triangles[offset + 1];
                int c = triangles[offset + 2];
                edgeRefs[edgeRefCount++] = new EdgeRef(a, b, c);
                edgeRefs[edgeRefCount++] = new EdgeRef(b, c, a);
                edgeRefs[edgeRefCount++] = new EdgeRef(c, a, b);
            }
            Array.Sort(edgeRefs, 0, edgeRefCount);

            int edgeCount = 0;
            int cursor = 0;
            while (cursor < edgeRefCount)
            {
                if ((edgeCount & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                EdgeRef first = edgeRefs[cursor];
                int end = cursor + 1;
                while (end < edgeRefCount &&
                    edgeRefs[end].A == first.A &&
                    edgeRefs[end].B == first.B)
                    end++;

                int count = end - cursor;
                EdgeInfo edge = new()
                {
                    A = first.A,
                    B = first.B,
                    Opposite0 = edgeRefs[cursor].Opposite,
                    Opposite1 = count > 1 ? edgeRefs[cursor + 1].Opposite : -1,
                    FaceCount = count
                };
                edges[edgeCount++] = edge;
                degree[edge.A]++;
                degree[edge.B]++;

                if (count != 2)
                {
                    boundary[edge.A] = 1;
                    boundary[edge.B] = 1;
                }

                if (!SameColor(colors, edge.A, edge.B))
                {
                    materialFeature[edge.A] = 1;
                    materialFeature[edge.B] = 1;
                }

                cursor = end;
            }

            // CSR vertex adjacency.
            neighborOffsets[0] = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                neighborOffsets[i + 1] = checked(neighborOffsets[i] + degree[i]);
                neighborCursor[i] = neighborOffsets[i];
            }

            for (int i = 0; i < edgeCount; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                EdgeInfo edge = edges[i];
                neighbors[neighborCursor[edge.A]++] = edge.B;
                neighbors[neighborCursor[edge.B]++] = edge.A;
            }

            for (int i = 0; i < vertexCount; i++)
                if (boundary[i] != 0 || materialFeature[i] != 0)
                    preservedBoundaryVertices++;

            int candidateCount = 0;
            int stamp = 0;
            for (int i = 0; i < edgeCount; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                EdgeInfo edge = edges[i];
                int a = edge.A;
                int b = edge.B;

                if (edge.FaceCount != 2 ||
                    boundary[a] != 0 || boundary[b] != 0 ||
                    materialFeature[a] != 0 || materialFeature[b] != 0 ||
                    !SameColor(colors, a, b))
                    continue;

                if (!SatisfiesLinkCondition(
                        edge,
                        neighborOffsets,
                        neighbors,
                        neighborMarks,
                        ref stamp))
                    continue;

                Quadric combined = quadrics[a];
                combined.Add(quadrics[b]);
                if (!TryChoosePosition(
                        edge,
                        combined,
                        positions,
                        triangles,
                        faceOffsets,
                        faceIds,
                        faceNormals,
                        facePlaneOffsets,
                        triangleCount,
                        maximumErrorMeters,
                        cancellationToken,
                        out Vector3 position,
                        out float error))
                    continue;

                double cost = Math.Max(0d, combined.Evaluate(position));
                candidates[candidateCount++] = new CollapseCandidate(
                    a, b, position, cost, error);
            }

            if (candidateCount == 0)
                return false;

            Array.Sort(candidates, 0, candidateCount);

            // Collapse only independent one-ring regions. This deliberately
            // sacrifices some reduction to keep all tests based on the original
            // manifold neighbourhood and prevent neighbouring edits interacting.
            for (int i = 0; i < candidateCount; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                CollapseCandidate candidate = candidates[i];
                int a = candidate.A;
                int b = candidate.B;
                if (blocked[a] != 0 || blocked[b] != 0 ||
                    collapseTo[a] >= 0 || collapseTo[b] >= 0)
                    continue;

                collapseTo[b] = a;
                positions[a] = candidate.Position;

                Vector3 blendedNormal = normals[a] + normals[b];
                float normalLengthSquared = blendedNormal.LengthSquared();
                if (normalLengthSquared > 1e-12f)
                    normals[a] = blendedNormal / MathF.Sqrt(normalLengthSquared);

                blocked[a] = 1;
                blocked[b] = 1;
                for (int n = neighborOffsets[a]; n < neighborOffsets[a + 1]; n++)
                    blocked[neighbors[n]] = 1;
                for (int n = neighborOffsets[b]; n < neighborOffsets[b + 1]; n++)
                    blocked[neighbors[n]] = 1;

                collapseCount++;
                maximumAcceptedError = Math.Max(maximumAcceptedError, candidate.Error);
            }

            if (collapseCount == 0)
                return false;

            Array.Fill(remap, -1, 0, vertexCount);
            int optimizedVertexCount = 0;
            int optimizedIndexCount = 0;

            for (int i = 0; i < indexCount; i += 3)
            {
                if ((i & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int a = collapseTo[triangles[i]] >= 0
                    ? collapseTo[triangles[i]]
                    : triangles[i];
                int b = collapseTo[triangles[i + 1]] >= 0
                    ? collapseTo[triangles[i + 1]]
                    : triangles[i + 1];
                int c = collapseTo[triangles[i + 2]] >= 0
                    ? collapseTo[triangles[i + 2]]
                    : triangles[i + 2];

                if (a == b || b == c || c == a)
                    continue;

                if (remap[a] < 0) remap[a] = optimizedVertexCount++;
                if (remap[b] < 0) remap[b] = optimizedVertexCount++;
                if (remap[c] < 0) remap[c] = optimizedVertexCount++;
                optimizedIndexCount += 3;
            }

            if (optimizedIndexCount <= 0 || optimizedIndexCount >= indexCount ||
                optimizedVertexCount <= 0 || optimizedVertexCount >= vertexCount)
                return false;

            outputVertices = ArrayPool<Vector3>.Shared.Rent(optimizedVertexCount);
            outputNormals = ArrayPool<Vector3>.Shared.Rent(optimizedVertexCount);
            outputColors = ArrayPool<byte>.Shared.Rent(checked(optimizedVertexCount * 4));
            outputIndices = ArrayPool<ushort>.Shared.Rent(optimizedIndexCount);

            for (int i = 0; i < vertexCount; i++)
            {
                if ((i & 2047) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int destination = remap[i];
                if (destination < 0)
                    continue;

                outputVertices[destination] = positions[i];
                outputNormals[destination] = normals[i];
                colors.AsSpan(i * 4, 4).CopyTo(outputColors.AsSpan(destination * 4, 4));
            }

            int outputCursor = 0;
            for (int i = 0; i < indexCount; i += 3)
            {
                if ((i & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                int a = collapseTo[triangles[i]] >= 0
                    ? collapseTo[triangles[i]]
                    : triangles[i];
                int b = collapseTo[triangles[i + 1]] >= 0
                    ? collapseTo[triangles[i + 1]]
                    : triangles[i + 1];
                int c = collapseTo[triangles[i + 2]] >= 0
                    ? collapseTo[triangles[i + 2]]
                    : triangles[i + 2];

                if (a == b || b == c || c == a)
                    continue;

                outputIndices[outputCursor++] = checked((ushort)remap[a]);
                outputIndices[outputCursor++] = checked((ushort)remap[b]);
                outputIndices[outputCursor++] = checked((ushort)remap[c]);
            }

            optimized = new VoxelMeshData(
                outputVertices,
                outputNormals,
                outputColors,
                outputIndices,
                optimizedVertexCount,
                optimizedIndexCount);
            outputTransferred = true;
            return true;
        }
        finally
        {
            ArrayPool<Vector3>.Shared.Return(positions, clearArray: false);
            ArrayPool<Vector3>.Shared.Return(normals, clearArray: false);
            ArrayPool<byte>.Shared.Return(colors, clearArray: false);
            ArrayPool<int>.Shared.Return(triangles, clearArray: false);
            ArrayPool<Vector3>.Shared.Return(faceNormals, clearArray: false);
            ArrayPool<float>.Shared.Return(facePlaneOffsets, clearArray: false);
            ArrayPool<EdgeRef>.Shared.Return(edgeRefs, clearArray: false);
            ArrayPool<EdgeInfo>.Shared.Return(edges, clearArray: false);
            ArrayPool<int>.Shared.Return(degree, clearArray: false);
            ArrayPool<int>.Shared.Return(neighborOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(neighborCursor, clearArray: false);
            ArrayPool<int>.Shared.Return(neighbors, clearArray: false);
            ArrayPool<int>.Shared.Return(faceCounts, clearArray: false);
            ArrayPool<int>.Shared.Return(faceOffsets, clearArray: false);
            ArrayPool<int>.Shared.Return(faceCursor, clearArray: false);
            ArrayPool<int>.Shared.Return(faceIds, clearArray: false);
            ArrayPool<byte>.Shared.Return(boundary, clearArray: false);
            ArrayPool<byte>.Shared.Return(materialFeature, clearArray: false);
            ArrayPool<byte>.Shared.Return(blocked, clearArray: false);
            ArrayPool<int>.Shared.Return(neighborMarks, clearArray: false);
            ArrayPool<int>.Shared.Return(collapseTo, clearArray: false);
            ArrayPool<int>.Shared.Return(remap, clearArray: false);
            ArrayPool<Quadric>.Shared.Return(quadrics, clearArray: false);
            ArrayPool<CollapseCandidate>.Shared.Return(candidates, clearArray: false);

            if (!outputTransferred)
            {
                if (outputVertices != null) ArrayPool<Vector3>.Shared.Return(outputVertices, clearArray: false);
                if (outputNormals != null) ArrayPool<Vector3>.Shared.Return(outputNormals, clearArray: false);
                if (outputColors != null) ArrayPool<byte>.Shared.Return(outputColors, clearArray: false);
                if (outputIndices != null) ArrayPool<ushort>.Shared.Return(outputIndices, clearArray: false);
            }
        }
    }

    private static bool SatisfiesLinkCondition(
        EdgeInfo edge,
        int[] neighborOffsets,
        int[] neighbors,
        int[] marks,
        ref int stamp)
    {
        if (++stamp == int.MaxValue)
        {
            Array.Clear(marks);
            stamp = 1;
        }

        for (int i = neighborOffsets[edge.B]; i < neighborOffsets[edge.B + 1]; i++)
            marks[neighbors[i]] = stamp;

        int common = 0;
        bool hasFirst = false;
        bool hasSecond = false;
        for (int i = neighborOffsets[edge.A]; i < neighborOffsets[edge.A + 1]; i++)
        {
            int neighbor = neighbors[i];
            if (neighbor == edge.B || marks[neighbor] != stamp)
                continue;

            common++;
            hasFirst |= neighbor == edge.Opposite0;
            hasSecond |= neighbor == edge.Opposite1;
            if (common > 2)
                return false;
        }

        return common == 2 && hasFirst && hasSecond;
    }

    private static bool TryChoosePosition(
        EdgeInfo edge,
        in Quadric quadric,
        Vector3[] positions,
        int[] triangles,
        int[] faceOffsets,
        int[] faceIds,
        Vector3[] faceNormals,
        float[] facePlaneOffsets,
        int triangleCount,
        float maximumErrorMeters,
        CancellationToken cancellationToken,
        out Vector3 bestPosition,
        out float bestError)
    {
        Span<Vector3> choices = stackalloc Vector3[4];
        int choiceCount = 0;
        if (quadric.TryOptimalPosition(out Vector3 optimal))
            choices[choiceCount++] = optimal;

        Vector3 a = positions[edge.A];
        Vector3 b = positions[edge.B];
        choices[choiceCount++] = (a + b) * 0.5f;
        choices[choiceCount++] = a;
        choices[choiceCount++] = b;

        bestPosition = default;
        bestError = 0f;
        double bestCost = double.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < choiceCount; i++)
        {
            Vector3 candidate = choices[i];
            if (!IsFinite(candidate) ||
                !ValidatePosition(
                    edge.A,
                    edge.B,
                    candidate,
                    positions,
                    triangles,
                    faceOffsets,
                    faceIds,
                    faceNormals,
                    facePlaneOffsets,
                    triangleCount,
                    maximumErrorMeters,
                    cancellationToken,
                    out float error))
                continue;

            double cost = Math.Max(0d, quadric.Evaluate(candidate));
            if (cost >= bestCost)
                continue;

            bestCost = cost;
            bestPosition = candidate;
            bestError = error;
            found = true;
        }

        return found;
    }

    private static bool ValidatePosition(
        int a,
        int b,
        Vector3 position,
        Vector3[] positions,
        int[] triangles,
        int[] faceOffsets,
        int[] faceIds,
        Vector3[] faceNormals,
        float[] facePlaneOffsets,
        int triangleCount,
        float maximumErrorMeters,
        CancellationToken cancellationToken,
        out float maximumError)
    {
        maximumError = 0f;
        float errorLimitSquared = maximumErrorMeters * maximumErrorMeters;

        if (!ValidateIncidentFaces(
                a, b, position, positions, triangles,
                faceOffsets, faceIds, faceNormals, facePlaneOffsets,
                triangleCount, errorLimitSquared, cancellationToken, ref maximumError))
            return false;

        if (!ValidateIncidentFaces(
                b, a, position, positions, triangles,
                faceOffsets, faceIds, faceNormals, facePlaneOffsets,
                triangleCount, errorLimitSquared, cancellationToken, ref maximumError))
            return false;

        return true;
    }

    private static bool ValidateIncidentFaces(
        int vertex,
        int other,
        Vector3 position,
        Vector3[] positions,
        int[] triangles,
        int[] faceOffsets,
        int[] faceIds,
        Vector3[] faceNormals,
        float[] facePlaneOffsets,
        int triangleCount,
        float errorLimitSquared,
        CancellationToken cancellationToken,
        ref float maximumError)
    {
        for (int i = faceOffsets[vertex]; i < faceOffsets[vertex + 1]; i++)
        {
            if ((i & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int face = faceIds[i];
            if ((uint)face >= (uint)triangleCount)
                return false;

            Vector3 oldNormal = faceNormals[face];
            if (oldNormal == Vector3.Zero)
                return false;

            int offset = face * 3;
            int i0 = triangles[offset];
            int i1 = triangles[offset + 1];
            int i2 = triangles[offset + 2];

            bool hasVertex = i0 == vertex || i1 == vertex || i2 == vertex;
            bool hasOther = i0 == other || i1 == other || i2 == other;
            if (!hasVertex)
                continue;

            // The two faces incident to the collapsed edge disappear.
            if (hasOther)
                continue;

            float planeDistance = MathF.Abs(
                Vector3.Dot(oldNormal, position) + facePlaneOffsets[face]);
            float planeDistanceSquared = planeDistance * planeDistance;
            if (planeDistanceSquared > errorLimitSquared)
                return false;

            Vector3 p0 = i0 == vertex ? position : positions[i0];
            Vector3 p1 = i1 == vertex ? position : positions[i1];
            Vector3 p2 = i2 == vertex ? position : positions[i2];
            Vector3 newNormal = Vector3.Cross(p1 - p0, p2 - p0);
            float areaSquared = newNormal.LengthSquared();

            // Reject degenerate or flipped surviving triangles.
            if (areaSquared <= MinimumAreaSquared ||
                Vector3.Dot(newNormal, oldNormal) <= 0f)
                return false;

            maximumError = Math.Max(maximumError, planeDistance);
        }

        return true;
    }

    private static bool SameColor(byte[] colors, int a, int b)
    {
        int ia = a * 4;
        int ib = b * 4;
        return colors[ia] == colors[ib] &&
            colors[ia + 1] == colors[ib + 1] &&
            colors[ia + 2] == colors[ib + 2] &&
            colors[ia + 3] == colors[ib + 3];
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static long EstimateBytes(int vertices, int indices) =>
        (long)vertices * (sizeof(float) * 6 + sizeof(byte) * 4) +
        (long)indices * sizeof(ushort);
}
