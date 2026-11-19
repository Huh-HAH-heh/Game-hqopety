using System.Numerics;
using Core.Map;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace RimClone.Render.Raylib;

internal sealed class RegionGpuMesh : IDisposable
{
    public Mesh[] Meshes = Array.Empty<Mesh>();
    public bool IsUploaded;
    public long RegionVersion;
    public int CutawayLayer;
    public Vector3 BoundsCenter;
    public float BoundsRadius;

    public void Dispose()
    {
        if (!IsUploaded)
            return;

        foreach (Mesh mesh in Meshes)
            UnloadMesh(mesh);

        Meshes = Array.Empty<Mesh>();
        IsUploaded = false;
    }
}

internal static class RegionMeshBuilder
{
    private const int SubChunksPerAxis = 2;
    private const int MaximumQuadsPerMesh = 15_000;
    private const float UnitsPerMeter = 0.1f;

    private const float ContourIntervalMeters = 5f;
    private const float ContourHalfHeight = 0.055f;
    private const float ContourOffset = 0.035f;

    private const int CutHatchLinesPerPatch = 4;
    private const float CutHatchHalfWidth = 0.035f;
    private const float CutHatchLift = 0.045f;

    private static readonly Color BrightContourColor = new(246, 248, 250, 255);
    private static readonly Color DimContourColor = new(168, 173, 181, 255);
    private static readonly Color CutHatchColor = new(205, 211, 219, 255);

    private readonly record struct TerrainQuad(
        Vector3 A,
        Vector3 B,
        Vector3 C,
        Vector3 D,
        Vector3 Normal,
        Color Color,
        bool ReverseWinding);

    /// <summary>
    /// Builds a volumetric mesh for one 192x192 storage region. Each 4x4-tile
    /// render cell is reconstructed from all of its vertical solid/air intervals.
    /// The two-by-two sub-batching keeps 16-bit GPU indices safely bounded.
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
        int chunkSize = patchesPerSide / SubChunksPerAxis;

        RegionGpuMesh result = new()
        {
            RegionVersion = terrain.SourceRegionVersions[storageIndex],
            CutawayLayer = terrain.CutawayEnabled ? terrain.CutawayLayer : -1,
            BoundsCenter = new Vector3(
                (storageRegionX + 0.5f) * terrain.StorageRegionSize,
                terrain.MaximumHeightMeters * 0.5f,
                (storageRegionY + 0.5f) * terrain.StorageRegionSize),
            BoundsRadius = MathF.Sqrt(
                terrain.StorageRegionSize * terrain.StorageRegionSize * 0.5f +
                terrain.MaximumHeightMeters * terrain.MaximumHeightMeters * 0.25f)
        };

        List<Mesh> meshes = new(SubChunksPerAxis * SubChunksPerAxis);

        for (int chunkY = 0; chunkY < SubChunksPerAxis; chunkY++)
        {
            for (int chunkX = 0; chunkX < SubChunksPerAxis; chunkX++)
            {
                BuildChunkMeshes(
                    terrain,
                    firstRenderX + chunkX * chunkSize,
                    firstRenderY + chunkY * chunkSize,
                    chunkSize,
                    meshes);
            }
        }

        result.Meshes = meshes.ToArray();
        result.IsUploaded = result.Meshes.Length > 0;
        return result;
    }

    private static void BuildChunkMeshes(
        TerrainRenderSnapshot terrain,
        int firstRenderX,
        int firstRenderY,
        int size,
        List<Mesh> output)
    {
        List<TerrainQuad> quads = new(Math.Min(size * size * 10, MaximumQuadsPerMesh));
        Span<TileRange> rangesBuffer = stackalloc TileRange[terrain.LayerCount];
        Span<TileRange> neighbourBuffer = stackalloc TileRange[terrain.LayerCount];

        int cutLayer = terrain.CutawayEnabled
            ? terrain.CutawayLayer
            : terrain.LayerCount;

        for (int localY = 0; localY < size; localY++)
        {
            int renderY = firstRenderY + localY;

            for (int localX = 0; localX < size; localX++)
            {
                int renderX = firstRenderX + localX;

                AddColumnGeometry(
                    terrain,
                    renderX,
                    renderY,
                    cutLayer,
                    rangesBuffer,
                    neighbourBuffer,
                    quads);
            }
        }

        if (quads.Count > MaximumQuadsPerMesh && size > 4)
        {
            int half = size / 2;
            BuildChunkMeshes(terrain, firstRenderX, firstRenderY, half, output);
            BuildChunkMeshes(terrain, firstRenderX + half, firstRenderY, half, output);
            BuildChunkMeshes(terrain, firstRenderX, firstRenderY + half, half, output);
            BuildChunkMeshes(terrain, firstRenderX + half, firstRenderY + half, half, output);
            return;
        }

        if (quads.Count > MaximumQuadsPerMesh)
        {
            throw new InvalidOperationException(
                "A terrain micro-batch exceeded the 16-bit mesh index limit.");
        }

        if (quads.Count > 0)
            output.Add(Upload(quads));
    }

    private static void AddColumnGeometry(
        TerrainRenderSnapshot terrain,
        int renderX,
        int renderY,
        int cutLayer,
        Span<TileRange> rangesBuffer,
        Span<TileRange> neighbourBuffer,
        List<TerrainQuad> quads)
    {
        int rangeCount = terrain.CopyColumnRanges(
            renderX,
            renderY,
            rangesBuffer);

        if (rangeCount == 0)
            return;

        ReadOnlySpan<TileRange> ranges = rangesBuffer[..rangeCount];

        float x0 = renderX * terrain.RegionSize;
        float x1 = MathF.Min(x0 + terrain.RegionSize, terrain.WorldWidth);
        float z0 = renderY * terrain.RegionSize;
        float z1 = MathF.Min(z0 + terrain.RegionSize, terrain.WorldDepth);

        for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
        {
            TileRange range = ranges[rangeIndex];
            if (range.State != WorldMap.StateSolid ||
                range.MaterialId == 0 ||
                range.StartZ >= cutLayer)
            {
                continue;
            }

            int visibleStart = range.StartZ;
            int visibleEnd = Math.Min(range.EndZ, cutLayer);
            if (visibleEnd <= visibleStart)
                continue;

            bool cutFace = terrain.CutawayEnabled &&
                visibleStart < cutLayer &&
                range.EndZ >= cutLayer;
            bool airAbove = cutFace ||
                rangeIndex == ranges.Length - 1 ||
                ranges[rangeIndex + 1].State == WorldMap.StateEmpty;
            bool airBelow = rangeIndex > 0 &&
                ranges[rangeIndex - 1].State == WorldMap.StateEmpty;

            float bottom = visibleStart * UnitsPerMeter;
            float top = visibleEnd * UnitsPerMeter;

            if (airAbove)
            {
                AddHorizontalFace(
                    quads,
                    x0, x1, z0, z1,
                    top,
                    range.MaterialId,
                    isTop: true,
                    isCutFace: cutFace);

                if (cutFace)
                {
                    AddCutHatching(
                        quads,
                        x0, x1, z0, z1,
                        top,
                        range.MaterialId);
                }
            }

            if (airBelow)
            {
                AddHorizontalFace(
                    quads,
                    x0, x1, z0, z1,
                    bottom,
                    range.MaterialId,
                    isTop: false,
                    isCutFace: false);
            }

            AddExposedSide(
                terrain,
                renderX,
                renderY - 1,
                visibleStart,
                visibleEnd,
                range.MaterialId,
                0,
                x0, x1, z0, z1,
                neighbourBuffer,
                quads);

            AddExposedSide(
                terrain,
                renderX,
                renderY + 1,
                visibleStart,
                visibleEnd,
                range.MaterialId,
                1,
                x0, x1, z0, z1,
                neighbourBuffer,
                quads);

            AddExposedSide(
                terrain,
                renderX - 1,
                renderY,
                visibleStart,
                visibleEnd,
                range.MaterialId,
                2,
                x0, x1, z0, z1,
                neighbourBuffer,
                quads);

            AddExposedSide(
                terrain,
                renderX + 1,
                renderY,
                visibleStart,
                visibleEnd,
                range.MaterialId,
                3,
                x0, x1, z0, z1,
                neighbourBuffer,
                quads);
        }
    }

    private static void AddHorizontalFace(
        List<TerrainQuad> quads,
        float x0,
        float x1,
        float z0,
        float z1,
        float height,
        ushort materialId,
        bool isTop,
        bool isCutFace)
    {
        Color color = isCutFace
            ? GetCutFaceColor(height, materialId)
            : GetSurfaceColor(height, materialId);

        quads.Add(new TerrainQuad(
            new Vector3(x0, height, z0),
            new Vector3(x1, height, z0),
            new Vector3(x1, height, z1),
            new Vector3(x0, height, z1),
            isTop ? Vector3.UnitY : -Vector3.UnitY,
            color,
            ReverseWinding: isTop));
    }

    private static void AddExposedSide(
        TerrainRenderSnapshot terrain,
        int neighbourX,
        int neighbourY,
        int visibleStart,
        int visibleEnd,
        ushort materialId,
        int side,
        float x0,
        float x1,
        float z0,
        float z1,
        Span<TileRange> neighbourBuffer,
        List<TerrainQuad> quads)
    {
        if (neighbourX < 0 || neighbourY < 0 ||
            neighbourX >= terrain.RegionsX ||
            neighbourY >= terrain.RegionsY)
        {
            AddSideSegment(
                quads,
                x0, x1, z0, z1,
                visibleStart,
                visibleEnd,
                materialId,
                side);
            return;
        }

        int neighbourCount = terrain.CopyColumnRanges(
            neighbourX,
            neighbourY,
            neighbourBuffer);
        ReadOnlySpan<TileRange> neighbourRanges =
            neighbourBuffer[..neighbourCount];

        foreach (TileRange neighbourRange in neighbourRanges)
        {
            if (neighbourRange.State != WorldMap.StateEmpty)
                continue;

            int exposedStart = Math.Max(
                visibleStart,
                neighbourRange.StartZ);
            int exposedEnd = Math.Min(
                visibleEnd,
                neighbourRange.EndZ);

            if (exposedEnd <= exposedStart)
                continue;

            AddSideSegment(
                quads,
                x0, x1, z0, z1,
                exposedStart,
                exposedEnd,
                materialId,
                side);
        }
    }

    private static void AddSideSegment(
        List<TerrainQuad> quads,
        float x0,
        float x1,
        float z0,
        float z1,
        int startLayer,
        int endLayer,
        ushort materialId,
        int side)
    {
        float bottom = startLayer * UnitsPerMeter;
        float top = endLayer * UnitsPerMeter;
        Color wallColor = Darken(
            GetSurfaceColor((bottom + top) * 0.5f, materialId),
            0.57f);

        Vector3 a;
        Vector3 b;
        Vector3 c;
        Vector3 d;
        Vector3 normal;

        switch (side)
        {
            case 0: // North (-Z)
                a = new Vector3(x0, top, z0);
                b = new Vector3(x1, top, z0);
                c = new Vector3(x1, bottom, z0);
                d = new Vector3(x0, bottom, z0);
                normal = -Vector3.UnitZ;
                break;

            case 1: // South (+Z)
                a = new Vector3(x1, top, z1);
                b = new Vector3(x0, top, z1);
                c = new Vector3(x0, bottom, z1);
                d = new Vector3(x1, bottom, z1);
                normal = Vector3.UnitZ;
                break;

            case 2: // West (-X)
                a = new Vector3(x0, top, z1);
                b = new Vector3(x0, top, z0);
                c = new Vector3(x0, bottom, z0);
                d = new Vector3(x0, bottom, z1);
                normal = -Vector3.UnitX;
                break;

            default: // East (+X)
                a = new Vector3(x1, top, z0);
                b = new Vector3(x1, top, z1);
                c = new Vector3(x1, bottom, z1);
                d = new Vector3(x1, bottom, z0);
                normal = Vector3.UnitX;
                break;
        }

        quads.Add(new TerrainQuad(
            a, b, c, d,
            normal,
            wallColor,
            ReverseWinding: false));

        AddElevationMarks(
            quads,
            x0, x1, z0, z1,
            startLayer,
            endLayer,
            side);
    }

    private static void AddElevationMarks(
        List<TerrainQuad> quads,
        float x0,
        float x1,
        float z0,
        float z1,
        int startLayer,
        int endLayer,
        int side)
    {
        int firstLevel = (int)MathF.Ceiling(
            startLayer / (5f * WorldMap.HeightUnitsPerMeter));
        int lastLevel = (int)MathF.Floor(
            endLayer / (5f * WorldMap.HeightUnitsPerMeter));

        for (int levelIndex = firstLevel; levelIndex <= lastLevel; levelIndex++)
        {
            float level = levelIndex * 5f;
            float lineBottom = MathF.Max(
                startLayer * UnitsPerMeter,
                level - ContourHalfHeight);
            float lineTop = MathF.Min(
                endLayer * UnitsPerMeter,
                level + ContourHalfHeight);

            if (lineTop <= lineBottom)
                continue;

            Vector3 normal = side switch
            {
                0 => -Vector3.UnitZ,
                1 => Vector3.UnitZ,
                2 => -Vector3.UnitX,
                _ => Vector3.UnitX
            };
            Vector3 offset = normal * ContourOffset;

            Vector3 a;
            Vector3 b;
            Vector3 c;
            Vector3 d;

            switch (side)
            {
                case 0:
                    a = new Vector3(x0, lineTop, z0) + offset;
                    b = new Vector3(x1, lineTop, z0) + offset;
                    c = new Vector3(x1, lineBottom, z0) + offset;
                    d = new Vector3(x0, lineBottom, z0) + offset;
                    break;
                case 1:
                    a = new Vector3(x1, lineTop, z1) + offset;
                    b = new Vector3(x0, lineTop, z1) + offset;
                    c = new Vector3(x0, lineBottom, z1) + offset;
                    d = new Vector3(x1, lineBottom, z1) + offset;
                    break;
                case 2:
                    a = new Vector3(x0, lineTop, z1) + offset;
                    b = new Vector3(x0, lineTop, z0) + offset;
                    c = new Vector3(x0, lineBottom, z0) + offset;
                    d = new Vector3(x0, lineBottom, z1) + offset;
                    break;
                default:
                    a = new Vector3(x1, lineTop, z0) + offset;
                    b = new Vector3(x1, lineTop, z1) + offset;
                    c = new Vector3(x1, lineBottom, z1) + offset;
                    d = new Vector3(x1, lineBottom, z0) + offset;
                    break;
            }

            Color color = (levelIndex & 1) == 0
                ? BrightContourColor
                : DimContourColor;

            quads.Add(new TerrainQuad(
                a, b, c, d,
                normal,
                color,
                ReverseWinding: false));
        }
    }

    private static void AddCutHatching(
        List<TerrainQuad> quads,
        float x0,
        float x1,
        float z0,
        float z1,
        float height,
        ushort materialId)
    {
        float y = height + CutHatchLift;
        const float strokeLength = 1.05f;

        for (int i = 0; i < CutHatchLinesPerPatch; i++)
        {
            float startX = x0 + 0.35f + i * 0.88f;
            float startZ;
            float endX;
            float endZ;

            switch (materialId)
            {
                case 2:
                    startZ = z0 + 0.35f + i * 0.88f;
                    endX = MathF.Min(startX + strokeLength, x1 - 0.15f);
                    endZ = startZ;
                    break;

                case 3:
                    startZ = z1 - 0.35f - i * 0.88f;
                    endX = MathF.Min(startX + strokeLength, x1 - 0.15f);
                    endZ = MathF.Max(startZ - strokeLength, z0 + 0.15f);
                    break;

                default:
                    startZ = z0 + 0.35f;
                    endX = MathF.Min(startX + strokeLength, x1 - 0.15f);
                    endZ = MathF.Min(startZ + strokeLength, z1 - 0.15f);
                    break;
            }

            if (endX <= startX ||
                (materialId == 2 && MathF.Abs(endZ - startZ) > 0.001f) ||
                (materialId == 3 && endZ >= startZ) ||
                (materialId != 2 && materialId != 3 && endZ <= startZ))
            {
                continue;
            }

            Vector2 direction = Vector2.Normalize(new Vector2(
                endX - startX,
                endZ - startZ));
            Vector2 halfWidth = new(
                -direction.Y * CutHatchHalfWidth,
                direction.X * CutHatchHalfWidth);

            Vector3 a = new(
                startX + halfWidth.X, y, startZ + halfWidth.Y);
            Vector3 b = new(
                endX + halfWidth.X, y, endZ + halfWidth.Y);
            Vector3 c = new(
                endX - halfWidth.X, y, endZ - halfWidth.Y);
            Vector3 d = new(
                startX - halfWidth.X, y, startZ - halfWidth.Y);

            quads.Add(new TerrainQuad(
                a, b, c, d,
                Vector3.UnitY,
                CutHatchColor,
                ReverseWinding: true));
        }
    }

    private static Color GetSurfaceColor(float height, ushort materialId)
    {
        float amount = Math.Clamp(height / 50f, 0f, 1f);
        int value = (int)MathF.Round(7f + amount * 34f);

        value += materialId switch
        {
            2 => 9,
            3 => -5,
            _ => 0
        };

        value = Math.Clamp(value, 4, 85);
        return new Color(
            value,
            Math.Min(255, value + 1),
            Math.Min(255, value + 3),
            255);
    }

    private static Color GetCutFaceColor(float height, ushort materialId)
    {
        Color baseColor = GetSurfaceColor(height, materialId);
        int value = Math.Clamp(baseColor.R + 5, 4, 90);

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

    private static Mesh Upload(List<TerrainQuad> quads)
    {
        int vertexCount = checked(quads.Count * 4);
        int triangleCount = checked(quads.Count * 2);

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

        foreach (TerrainQuad quad in quads)
        {
            AddQuad(
                vertices, normals, colors, indices,
                ref vertexOffset, ref indexOffset,
                quad.A, quad.B, quad.C, quad.D,
                quad.Normal,
                quad.Color,
                quad.ReverseWinding);
        }

        UploadMesh(ref mesh, false);
        return mesh;
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
