using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

internal sealed class RegionGpuMesh : IDisposable
{
    public Mesh Mesh;
    public bool IsUploaded;
    public long RegionVersion;
    public Vector3 BoundsCenter;
    public float BoundsRadius;

    public void Dispose()
    {
        if (!IsUploaded)
            return;

        UnloadMesh(Mesh);
        Mesh = default;
        IsUploaded = false;
    }
}

internal static class RegionMeshBuilder
{
    /// <summary>
    /// Builds one GPU batch for a 192x192 storage region. Its surface is sampled
    /// from 4x4-tile render regions. Uniform areas are merged into large quads;
    /// detailed areas retain the smaller patch geometry.
    /// </summary>
    public static RegionGpuMesh Build(
        TerrainRenderSnapshot terrain,
        int storageRegionX,
        int storageRegionY)
    {
        int storageIndex =
            storageRegionX + storageRegionY * terrain.StorageRegionsX;
        int patchesPerSide = terrain.RenderRegionsPerStorageRegion;
        int firstRenderX = storageRegionX * patchesPerSide;
        int firstRenderY = storageRegionY * patchesPerSide;

        bool uniform = TryGetUniformSurface(
            terrain,
            firstRenderX,
            firstRenderY,
            patchesPerSide,
            out float uniformHeight,
            out ushort uniformMaterial);

        float maximumHeight = 0f;
        int quadCount = uniform
            ? CountUniformQuads(
                terrain,
                firstRenderX,
                firstRenderY,
                patchesPerSide,
                uniformHeight,
                uniformMaterial,
                ref maximumHeight)
            : CountDetailedQuads(
                terrain,
                firstRenderX,
                firstRenderY,
                patchesPerSide,
                ref maximumHeight);

        RegionGpuMesh result = new()
        {
            RegionVersion = terrain.SourceRegionVersions[storageIndex],
            BoundsCenter = new Vector3(
                (storageRegionX + 0.5f) * terrain.StorageRegionSize,
                maximumHeight * 0.5f,
                (storageRegionY + 0.5f) * terrain.StorageRegionSize),
            BoundsRadius = MathF.Sqrt(
                terrain.StorageRegionSize * terrain.StorageRegionSize * 0.5f +
                maximumHeight * maximumHeight * 0.25f)
        };

        if (quadCount == 0)
            return result;

        int vertexCount = checked(quadCount * 4);
        int triangleCount = checked(quadCount * 2);

        Mesh mesh = new(vertexCount, triangleCount);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocColors();
        mesh.AllocIndices();

        Span<Vector3> vertices = mesh.VerticesAs<Vector3>();
        Span<Vector3> normals = mesh.NormalsAs<Vector3>();
        Span<byte> colors = mesh.ColorsAs<byte>();
        Span<ushort> indices = mesh.IndicesAs<ushort>();

        int vertexOffset = 0;
        int indexOffset = 0;

        if (uniform)
        {
            BuildUniformGeometry(
                terrain,
                firstRenderX,
                firstRenderY,
                patchesPerSide,
                uniformHeight,
                uniformMaterial,
                vertices,
                normals,
                colors,
                indices,
                ref vertexOffset,
                ref indexOffset);
        }
        else
        {
            BuildDetailedGeometry(
                terrain,
                firstRenderX,
                firstRenderY,
                patchesPerSide,
                vertices,
                normals,
                colors,
                indices,
                ref vertexOffset,
                ref indexOffset);
        }

        UploadMesh(ref mesh, false);
        result.Mesh = mesh;
        result.IsUploaded = true;
        return result;
    }

    private static bool TryGetUniformSurface(
        TerrainRenderSnapshot terrain,
        int firstRenderX,
        int firstRenderY,
        int patchesPerSide,
        out float height,
        out ushort materialId)
    {
        height = terrain.GetHeightMeters(firstRenderX, firstRenderY);
        materialId = terrain.GetMaterialId(firstRenderX, firstRenderY);

        for (int y = 0; y < patchesPerSide; y++)
        {
            for (int x = 0; x < patchesPerSide; x++)
            {
                int renderX = firstRenderX + x;
                int renderY = firstRenderY + y;

                if (terrain.GetHeightMeters(renderX, renderY) != height ||
                    terrain.GetMaterialId(renderX, renderY) != materialId)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int CountUniformQuads(
        TerrainRenderSnapshot terrain,
        int firstRenderX,
        int firstRenderY,
        int patchesPerSide,
        float height,
        ushort materialId,
        ref float maximumHeight)
    {
        if (height <= 0f || materialId == 0)
            return 0;

        maximumHeight = height;
        int quadCount = 1;
        
        for (int i = 0; i < patchesPerSide; i++)
        {
            float north = terrain.GetHeightMeters(firstRenderX + i, firstRenderY - 1);
            float south = terrain.GetHeightMeters(firstRenderX + i, firstRenderY + patchesPerSide);
            float west = terrain.GetHeightMeters(firstRenderX - 1, firstRenderY + i);
            float east = terrain.GetHeightMeters(firstRenderX + patchesPerSide, firstRenderY + i);

            if (height > north)
                quadCount += 1 + CountElevationLines(north, height);
            if (height > south)
                quadCount += 1 + CountElevationLines(south, height);
            if (height > west)
                quadCount += 1 + CountElevationLines(west, height);
            if (height > east)
                quadCount += 1 + CountElevationLines(east, height);
        }

        return quadCount;
    }

    private static void BuildUniformGeometry(
        TerrainRenderSnapshot terrain,
        int firstRenderX,
        int firstRenderY,
        int patchesPerSide,
        float height,
        ushort materialId,
        Span<Vector3> vertices,
        Span<Vector3> normals,
        Span<byte> colors,
        Span<ushort> indices,
        ref int vertexOffset,
        ref int indexOffset)
    {
        if (height <= 0f || materialId == 0)
            return;

        float x0 = firstRenderX * terrain.RegionSize;
        float x1 = x0 + terrain.StorageRegionSize;
        float z0 = firstRenderY * terrain.RegionSize;
        float z1 = z0 + terrain.StorageRegionSize;
        Color topColor = GetSurfaceColor(height);

        AddQuad(
            vertices, normals, colors, indices,
            ref vertexOffset, ref indexOffset,
            new Vector3(x0, height, z0),
            new Vector3(x1, height, z0),
            new Vector3(x1, height, z1),
            new Vector3(x0, height, z1),
            Vector3.UnitY,
            topColor,
            reverseWinding: true);

        for (int i = 0; i < patchesPerSide; i++)
        {
            float segmentX0 = x0 + i * terrain.RegionSize;
            float segmentX1 = segmentX0 + terrain.RegionSize;
            float segmentZ0 = z0 + i * terrain.RegionSize;
            float segmentZ1 = segmentZ0 + terrain.RegionSize;

            float north = terrain.GetHeightMeters(firstRenderX + i, firstRenderY - 1);
            if (height > north)
            {
                AddWallAndContours(
                    vertices, normals, colors, indices,
                    ref vertexOffset, ref indexOffset,
                    new Vector3(segmentX0, height, z0),
                    new Vector3(segmentX1, height, z0),
                    north,
                    -Vector3.UnitZ);
            }

            float south = terrain.GetHeightMeters(
                firstRenderX + i,
                firstRenderY + patchesPerSide);
            if (height > south)
            {
                AddWallAndContours(
                    vertices, normals, colors, indices,
                    ref vertexOffset, ref indexOffset,
                    new Vector3(segmentX1, height, z1),
                    new Vector3(segmentX0, height, z1),
                    south,
                    Vector3.UnitZ);
            }

            float west = terrain.GetHeightMeters(
                firstRenderX - 1,
                firstRenderY + i);
            if (height > west)
            {
                AddWallAndContours(
                    vertices, normals, colors, indices,
                    ref vertexOffset, ref indexOffset,
                    new Vector3(x0, height, segmentZ1),
                    new Vector3(x0, height, segmentZ0),
                    west,
                    -Vector3.UnitX);
            }

            float east = terrain.GetHeightMeters(
                firstRenderX + patchesPerSide,
                firstRenderY + i);
            if (height > east)
            {
                AddWallAndContours(
                    vertices, normals, colors, indices,
                    ref vertexOffset, ref indexOffset,
                    new Vector3(x1, height, segmentZ0),
                    new Vector3(x1, height, segmentZ1),
                    east,
                    Vector3.UnitX);
            }
        }
    }

    private static int CountDetailedQuads(
        TerrainRenderSnapshot terrain,
        int firstRenderX,
        int firstRenderY,
        int patchesPerSide,
        ref float maximumHeight)
    {
        int quadCount = 0;

        for (int localY = 0; localY < patchesPerSide; localY++)
        {
            int renderY = firstRenderY + localY;

            for (int localX = 0; localX < patchesPerSide; localX++)
            {
                int renderX = firstRenderX + localX;
                float height = terrain.GetHeightMeters(renderX, renderY);

                if (height <= 0f ||
                    terrain.GetMaterialId(renderX, renderY) == 0)
                {
                    continue;
                }

                maximumHeight = MathF.Max(maximumHeight, height);
                quadCount++;

                float north = terrain.GetHeightMeters(renderX, renderY - 1);
                float south = terrain.GetHeightMeters(renderX, renderY + 1);
                float west = terrain.GetHeightMeters(renderX - 1, renderY);
                float east = terrain.GetHeightMeters(renderX + 1, renderY);

                if (height > north)
                    quadCount += 1 + CountElevationLines(north, height);
                if (height > south)
                    quadCount += 1 + CountElevationLines(south, height);
                if (height > west)
                    quadCount += 1 + CountElevationLines(west, height);
                if (height > east)
                    quadCount += 1 + CountElevationLines(east, height);
            }
        }

        return quadCount;
    }

    private const float ContourIntervalMeters = 5f;
    private const float ContourHalfHeight = 0.055f;
    private const float ContourOffset = 0.035f;

    private static readonly Color BrightContourColor = new(246, 248, 250, 255);
    private static readonly Color DimContourColor = new(168, 173, 181, 255);

    private static void BuildDetailedGeometry(
        TerrainRenderSnapshot terrain,
        int firstRenderX,
        int firstRenderY,
        int patchesPerSide,
        Span<Vector3> vertices,
        Span<Vector3> normals,
        Span<byte> colors,
        Span<ushort> indices,
        ref int vertexOffset,
        ref int indexOffset)
    {
        for (int localY = 0; localY < patchesPerSide; localY++)
        {
            int renderY = firstRenderY + localY;

            for (int localX = 0; localX < patchesPerSide; localX++)
            {
                int renderX = firstRenderX + localX;
                float height = terrain.GetHeightMeters(renderX, renderY);
                ushort materialId = terrain.GetMaterialId(renderX, renderY);

                if (height <= 0f || materialId == 0)
                    continue;

                float x0 = renderX * terrain.RegionSize;
                float x1 = MathF.Min(
                    x0 + terrain.RegionSize,
                    terrain.WorldWidth);
                float z0 = renderY * terrain.RegionSize;
                float z1 = MathF.Min(
                    z0 + terrain.RegionSize,
                    terrain.WorldDepth);

                AddQuad(
                    vertices, normals, colors, indices,
                    ref vertexOffset, ref indexOffset,
                    new Vector3(x0, height, z0),
                    new Vector3(x1, height, z0),
                    new Vector3(x1, height, z1),
                    new Vector3(x0, height, z1),
                    Vector3.UnitY,
                    GetSurfaceColor(height),
                    reverseWinding: true);

                float north = terrain.GetHeightMeters(renderX, renderY - 1);
                if (height > north)
                {
                    AddWallAndContours(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x0, height, z0),
                        new Vector3(x1, height, z0),
                        north,
                        -Vector3.UnitZ);
                }

                float south = terrain.GetHeightMeters(renderX, renderY + 1);
                if (height > south)
                {
                    AddWallAndContours(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x1, height, z1),
                        new Vector3(x0, height, z1),
                        south,
                        Vector3.UnitZ);
                }

                float west = terrain.GetHeightMeters(renderX - 1, renderY);
                if (height > west)
                {
                    AddWallAndContours(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x0, height, z1),
                        new Vector3(x0, height, z0),
                        west,
                        -Vector3.UnitX);
                }

                float east = terrain.GetHeightMeters(renderX + 1, renderY);
                if (height > east)
                {
                    AddWallAndContours(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x1, height, z0),
                        new Vector3(x1, height, z1),
                        east,
                        Vector3.UnitX);
                }
            }
        }
    }

    private static int CountElevationLines(float lowerHeight, float upperHeight)
    {
        if (upperHeight <= lowerHeight)
            return 0;

        int firstLevel = (int)MathF.Ceiling(
            (lowerHeight - 0.0001f) / ContourIntervalMeters);
        int lastLevel = (int)MathF.Floor(
            (upperHeight + 0.0001f) / ContourIntervalMeters);

        return Math.Max(0, lastLevel - firstLevel + 1);
    }

    private static void AddWallAndContours(
        Span<Vector3> vertices,
        Span<Vector3> normals,
        Span<byte> colors,
        Span<ushort> indices,
        ref int vertexOffset,
        ref int indexOffset,
        Vector3 upperStart,
        Vector3 upperEnd,
        float lowerHeight,
        Vector3 normal)
    {
        float upperHeight = upperStart.Y;
        Color wallColor = Darken(
            GetSurfaceColor((upperHeight + lowerHeight) * 0.5f),
            0.57f);

        AddQuad(
            vertices, normals, colors, indices,
            ref vertexOffset, ref indexOffset,
            upperStart,
            upperEnd,
            new Vector3(upperEnd.X, lowerHeight, upperEnd.Z),
            new Vector3(upperStart.X, lowerHeight, upperStart.Z),
            normal,
            wallColor);

        AddElevationLines(
            vertices, normals, colors, indices,
            ref vertexOffset, ref indexOffset,
            upperStart,
            upperEnd,
            lowerHeight,
            upperHeight,
            normal);
    }

    private static void AddElevationLines(
        Span<Vector3> vertices,
        Span<Vector3> normals,
        Span<byte> colors,
        Span<ushort> indices,
        ref int vertexOffset,
        ref int indexOffset,
        Vector3 upperStart,
        Vector3 upperEnd,
        float lowerHeight,
        float upperHeight,
        Vector3 normal)
    {
        int firstLevel = (int)MathF.Ceiling(
            (lowerHeight - 0.0001f) / ContourIntervalMeters);
        int lastLevel = (int)MathF.Floor(
            (upperHeight + 0.0001f) / ContourIntervalMeters);

        Vector3 offset = normal * ContourOffset;

        for (int levelIndex = firstLevel; levelIndex <= lastLevel; levelIndex++)
        {
            float level = levelIndex * ContourIntervalMeters;
            float lineBottom = MathF.Max(lowerHeight, level - ContourHalfHeight);
            float lineTop = MathF.Min(upperHeight, level + ContourHalfHeight);

            if (lineTop <= lineBottom)
                continue;

            Vector3 a = new Vector3(upperStart.X, lineTop, upperStart.Z) + offset;
            Vector3 b = new Vector3(upperEnd.X, lineTop, upperEnd.Z) + offset;
            Vector3 c = new Vector3(upperEnd.X, lineBottom, upperEnd.Z) + offset;
            Vector3 d = new Vector3(upperStart.X, lineBottom, upperStart.Z) + offset;

            Color lineColor = (levelIndex & 1) == 0
                ? BrightContourColor
                : DimContourColor;

            AddQuad(
                vertices, normals, colors, indices,
                ref vertexOffset, ref indexOffset,
                a, b, c, d,
                normal,
                lineColor);
        }
    }

    private static void AddQuad(
        Span<Vector3> vertices,
        Span<Vector3> normals,
        Span<byte> colors,
        Span<ushort> indices,
        ref int vertexOffset,
        ref int indexOffset,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector3 normal,
        Color color,
        bool reverseWinding = false)
    {
        int start = vertexOffset;
        vertices[start] = a;
        vertices[start + 1] = b;
        vertices[start + 2] = c;
        vertices[start + 3] = d;

        for (int i = 0; i < 4; i++)
        {
            normals[start + i] = normal;
            int colorOffset = (start + i) * 4;
            colors[colorOffset] = color.R;
            colors[colorOffset + 1] = color.G;
            colors[colorOffset + 2] = color.B;
            colors[colorOffset + 3] = color.A;
        }

        if (reverseWinding)
        {
            indices[indexOffset] = (ushort)start;
            indices[indexOffset + 1] = (ushort)(start + 2);
            indices[indexOffset + 2] = (ushort)(start + 1);
            indices[indexOffset + 3] = (ushort)start;
            indices[indexOffset + 4] = (ushort)(start + 3);
            indices[indexOffset + 5] = (ushort)(start + 2);
        }
        else
        {
            indices[indexOffset] = (ushort)start;
            indices[indexOffset + 1] = (ushort)(start + 1);
            indices[indexOffset + 2] = (ushort)(start + 2);
            indices[indexOffset + 3] = (ushort)start;
            indices[indexOffset + 4] = (ushort)(start + 2);
            indices[indexOffset + 5] = (ushort)(start + 3);
        }

        vertexOffset += 4;
        indexOffset += 6;
    }

    private static Color GetSurfaceColor(float height)
    {
        // Restrained charcoal palette: canyon floors stay almost black,
        // ridges and plateaus gain enough luminance to read from above.
        float amount = Math.Clamp(height / 50f, 0f, 1f);
        int value = (int)MathF.Round(7f + amount * 34f);

        return new Color(
            value,
            Math.Min(255, value + 1),
            Math.Min(255, value + 3),
            255);
    }

    private static Color Darken(Color color, float amount)
    {
        return new Color(
            (int)(color.R * amount),
            (int)(color.G * amount),
            (int)(color.B * amount),
            255);
    }
}

internal static class TerrainFrustum
{
    public static bool IsSphereVisible(
        Camera3D camera,
        Vector3 center,
        float radius,
        int viewportWidth,
        int viewportHeight)
    {
        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, camera.Up));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        Vector3 delta = center - camera.Position;

        float depth = Vector3.Dot(delta, forward);
        const float nearPlane = 1f;
        const float farPlane = 18000f;

        if (depth + radius < nearPlane || depth - radius > farPlane)
            return false;

        float tanVertical = MathF.Tan(camera.FovY * MathF.PI / 360f);
        float aspect = viewportWidth / (float)Math.Max(1, viewportHeight);
        float tanHorizontal = tanVertical * aspect;
        float horizontalDistance = MathF.Abs(Vector3.Dot(delta, right));
        float verticalDistance = MathF.Abs(Vector3.Dot(delta, up));

        float horizontalLimit =
            depth * tanHorizontal +
            radius * MathF.Sqrt(1f + tanHorizontal * tanHorizontal);

        float verticalLimit =
            depth * tanVertical +
            radius * MathF.Sqrt(1f + tanVertical * tanVertical);

        return horizontalDistance <= horizontalLimit &&
               verticalDistance <= verticalLimit;
    }
}
