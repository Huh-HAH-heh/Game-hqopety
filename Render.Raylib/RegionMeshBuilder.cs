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
    /// from 4x4-tile render regions, so small edits do not create individual
    /// draw calls or GPU objects for every render region.
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

        float maximumHeight = 0f;
        int quadCount = CountQuads(
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

                Color topColor = GetSurfaceColor(height);
                Color wallColor = Darken(topColor, 0.57f);

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

                float north = terrain.GetHeightMeters(renderX, renderY - 1);
                float south = terrain.GetHeightMeters(renderX, renderY + 1);
                float west = terrain.GetHeightMeters(renderX - 1, renderY);
                float east = terrain.GetHeightMeters(renderX + 1, renderY);

                if (height > north)
                {
                    AddQuad(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x0, height, z0),
                        new Vector3(x1, height, z0),
                        new Vector3(x1, north, z0),
                        new Vector3(x0, north, z0),
                        -Vector3.UnitZ,
                        wallColor);
                }

                if (height > south)
                {
                    AddQuad(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x1, height, z1),
                        new Vector3(x0, height, z1),
                        new Vector3(x0, south, z1),
                        new Vector3(x1, south, z1),
                        Vector3.UnitZ,
                        wallColor);
                }

                if (height > west)
                {
                    AddQuad(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x0, height, z1),
                        new Vector3(x0, height, z0),
                        new Vector3(x0, west, z0),
                        new Vector3(x0, west, z1),
                        -Vector3.UnitX,
                        wallColor);
                }

                if (height > east)
                {
                    AddQuad(
                        vertices, normals, colors, indices,
                        ref vertexOffset, ref indexOffset,
                        new Vector3(x1, height, z0),
                        new Vector3(x1, height, z1),
                        new Vector3(x1, east, z1),
                        new Vector3(x1, east, z0),
                        Vector3.UnitX,
                        wallColor);
                }
            }
        }

        UploadMesh(ref mesh, false);
        result.Mesh = mesh;
        result.IsUploaded = true;
        return result;
    }

    private static int CountQuads(
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

                if (height > terrain.GetHeightMeters(renderX, renderY - 1))
                    quadCount++;
                if (height > terrain.GetHeightMeters(renderX, renderY + 1))
                    quadCount++;
                if (height > terrain.GetHeightMeters(renderX - 1, renderY))
                    quadCount++;
                if (height > terrain.GetHeightMeters(renderX + 1, renderY))
                    quadCount++;
            }
        }

        return quadCount;
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
        float amount = Math.Clamp(height / 52f, 0f, 1f);
        return new Color(
            (int)(52f + amount * 68f),
            (int)(74f + amount * 69f),
            (int)(52f + amount * 66f),
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
