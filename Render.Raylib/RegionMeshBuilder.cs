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
    private const int MaximumQuads = 5;

    public static RegionGpuMesh Build(
        TerrainRenderSnapshot terrain,
        int regionX,
        int regionY)
    {
        int regionIndex = regionX + regionY * terrain.RegionsX;
        float height = terrain.HeightsMeters[regionIndex];

        RegionGpuMesh result = new()
        {
            RegionVersion = terrain.RegionVersions[regionIndex],
            BoundsCenter = new Vector3(
                (regionX + 0.5f) * terrain.RegionSize,
                height * 0.5f,
                (regionY + 0.5f) * terrain.RegionSize),
            BoundsRadius = MathF.Sqrt(
                terrain.RegionSize * terrain.RegionSize * 0.5f +
                height * height * 0.25f)
        };

        ushort materialId = terrain.MaterialIds[regionIndex];
        if (height <= 0f || materialId == 0)
            return result;

        float north = GetHeight(terrain, regionX, regionY - 1);
        float south = GetHeight(terrain, regionX, regionY + 1);
        float west = GetHeight(terrain, regionX - 1, regionY);
        float east = GetHeight(terrain, regionX + 1, regionY);

        int quadCount = 1;
        if (height > north) quadCount++;
        if (height > south) quadCount++;
        if (height > west) quadCount++;
        if (height > east) quadCount++;

        int vertexCount = quadCount * 4;
        int triangleCount = quadCount * 2;

        Mesh mesh = new(vertexCount, triangleCount);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocColors();
        mesh.AllocIndices();

        Span<Vector3> vertices = mesh.VerticesAs<Vector3>();
        Span<Vector3> normals = mesh.NormalsAs<Vector3>();
        Span<byte> colors = mesh.ColorsAs<byte>();
        Span<ushort> indices = mesh.IndicesAs<ushort>();

        float x0 = regionX * terrain.RegionSize;
        float x1 = x0 + terrain.RegionSize;
        float z0 = regionY * terrain.RegionSize;
        float z1 = z0 + terrain.RegionSize;

        Color topColor = GetSurfaceColor(height);
        Color wallColor = Darken(topColor, 0.57f);
        int vertexOffset = 0;
        int indexOffset = 0;

        // Region top. Winding is reversed so its normal points upward (+Y).
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

        // Only the higher region emits a shared vertical edge.
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

        UploadMesh(ref mesh, false);
        result.Mesh = mesh;
        result.IsUploaded = true;
        return result;
    }

    private static float GetHeight(
        TerrainRenderSnapshot terrain,
        int regionX,
        int regionY)
    {
        if (regionX < 0 || regionY < 0 ||
            regionX >= terrain.RegionsX ||
            regionY >= terrain.RegionsY)
        {
            return 0f;
        }

        return terrain.HeightsMeters[
            regionX + regionY * terrain.RegionsX];
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
            indices[indexOffset] = (ushort)(start);
            indices[indexOffset + 1] = (ushort)(start + 2);
            indices[indexOffset + 2] = (ushort)(start + 1);
            indices[indexOffset + 3] = (ushort)(start);
            indices[indexOffset + 4] = (ushort)(start + 3);
            indices[indexOffset + 5] = (ushort)(start + 2);
        }
        else
        {
            indices[indexOffset] = (ushort)(start);
            indices[indexOffset + 1] = (ushort)(start + 1);
            indices[indexOffset + 2] = (ushort)(start + 2);
            indices[indexOffset + 3] = (ushort)(start);
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
