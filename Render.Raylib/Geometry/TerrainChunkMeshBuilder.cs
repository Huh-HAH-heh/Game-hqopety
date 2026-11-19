using System.Numerics;
using System.Threading.Tasks;
using RimClone.Render.RaylibBackend.Data;

namespace RimClone.Render.RaylibBackend.Geometry;

public readonly record struct Color32(byte R, byte G, byte B, byte A = 255)
{
    public Color32 Darken(float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Color32(
            (byte)(R * amount),
            (byte)(G * amount),
            (byte)(B * amount),
            A);
    }
}

public sealed class TerrainChunkMeshData
{
    public int ChunkX { get; }
    public int ChunkY { get; }
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public Color32[] Colors { get; }
    public int TriangleCount => Positions.Length / 3;
    public BoundingBoxData Bounds { get; }

    internal TerrainChunkMeshData(
        int chunkX,
        int chunkY,
        Vector3[] positions,
        Vector3[] normals,
        Color32[] colors,
        BoundingBoxData bounds)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
        Positions = positions;
        Normals = normals;
        Colors = colors;
        Bounds = bounds;
    }
}

public readonly record struct BoundingBoxData(Vector3 Min, Vector3 Max);

/// <summary>
/// CPU-only terrain meshing. It has no Raylib dependency and can run on worker
/// threads. Uniform regions become a single surface patch; edited regions expand
/// to tile-resolution geometry.
/// </summary>
public static class TerrainChunkMeshBuilder
{
    public const int RegionsPerChunk = 4;
    public const float HorizontalUnitsPerTile = 0.25f;
    public const float VerticalUnitsPerMeter = 2.0f;

    public static TerrainChunkMeshData[] BuildAll(
        WorldTerrainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        int chunksX = DivideRoundUp(
            snapshot.RegionsX,
            RegionsPerChunk);
        int chunksY = DivideRoundUp(
            snapshot.RegionsY,
            RegionsPerChunk);
        TerrainChunkMeshData[] chunks =
            new TerrainChunkMeshData[chunksX * chunksY];

        // Mesh generation is pure CPU work. GPU uploads stay on the render thread.
        Parallel.For(0, chunks.Length, index =>
        {
            int chunkX = index % chunksX;
            int chunkY = index / chunksX;
            chunks[index] = BuildChunk(snapshot, chunkX, chunkY);
        });

        return chunks;
    }

    private static TerrainChunkMeshData BuildChunk(
        WorldTerrainSnapshot snapshot,
        int chunkX,
        int chunkY)
    {
        List<Vector3> positions = new(RegionsPerChunk * RegionsPerChunk * 30);
        List<Vector3> normals = new(positions.Capacity);
        List<Color32> colors = new(positions.Capacity);

        int firstRegionX = chunkX * RegionsPerChunk;
        int firstRegionY = chunkY * RegionsPerChunk;
        int lastRegionX = Math.Min(
            firstRegionX + RegionsPerChunk,
            snapshot.RegionsX);
        int lastRegionY = Math.Min(
            firstRegionY + RegionsPerChunk,
            snapshot.RegionsY);

        float minX = firstRegionX * snapshot.RegionTileSize * HorizontalUnitsPerTile;
        float minZ = firstRegionY * snapshot.RegionTileSize * HorizontalUnitsPerTile;
        float maxX = lastRegionX * snapshot.RegionTileSize * HorizontalUnitsPerTile;
        float maxZ = lastRegionY * snapshot.RegionTileSize * HorizontalUnitsPerTile;
        float maxY = 0f;

        for (int regionY = firstRegionY; regionY < lastRegionY; regionY++)
        {
            for (int regionX = firstRegionX; regionX < lastRegionX; regionX++)
            {
                int regionIndex = snapshot.GetRegionIndex(regionX, regionY);
                if (snapshot.IsUniformRegion(regionIndex))
                {
                    BuildUniformRegion(
                        snapshot,
                        regionX,
                        regionY,
                        regionIndex,
                        positions,
                        normals,
                        colors,
                        ref maxY);
                }
                else
                {
                    BuildDetailedRegion(
                        snapshot,
                        regionX,
                        regionY,
                        positions,
                        normals,
                        colors,
                        ref maxY);
                }
            }
        }

        return new TerrainChunkMeshData(
            chunkX,
            chunkY,
            positions.ToArray(),
            normals.ToArray(),
            colors.ToArray(),
            new BoundingBoxData(
                new Vector3(minX, 0f, minZ),
                new Vector3(maxX, maxY, maxZ)));
    }

    private static void BuildUniformRegion(
        WorldTerrainSnapshot snapshot,
        int regionX,
        int regionY,
        int regionIndex,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors,
        ref float maxY)
    {
        ushort heightUnits = snapshot.UniformHeightUnits[regionIndex];
        ushort materialId = snapshot.UniformMaterialIds[regionIndex];

        if (heightUnits == 0 || materialId == 0)
            return;

        float height = ToRenderHeight(heightUnits);
        maxY = Math.Max(maxY, height);

        int size = snapshot.RegionTileSize;
        int tileX = regionX * size;
        int tileZ = regionY * size;

        Color32 topColor = GetSurfaceColor(heightUnits, materialId);
        AddTopQuad(
            positions,
            normals,
            colors,
            tileX * HorizontalUnitsPerTile,
            tileZ * HorizontalUnitsPerTile,
            size * HorizontalUnitsPerTile,
            height,
            topColor);

        AddUniformBoundary(
            snapshot, tileX, tileZ, size, heightUnits,
            Boundary.West, materialId, positions, normals, colors);
        AddUniformBoundary(
            snapshot, tileX, tileZ, size, heightUnits,
            Boundary.East, materialId, positions, normals, colors);
        AddUniformBoundary(
            snapshot, tileX, tileZ, size, heightUnits,
            Boundary.North, materialId, positions, normals, colors);
        AddUniformBoundary(
            snapshot, tileX, tileZ, size, heightUnits,
            Boundary.South, materialId, positions, normals, colors);
    }

    private static void AddUniformBoundary(
        WorldTerrainSnapshot snapshot,
        int tileX,
        int tileZ,
        int size,
        ushort heightUnits,
        Boundary boundary,
        ushort materialId,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors)
    {
        int sampleX = boundary switch
        {
            Boundary.West => tileX - 1,
            Boundary.East => tileX + size,
            _ => tileX + size / 2
        };

        int sampleZ = boundary switch
        {
            Boundary.North => tileZ - 1,
            Boundary.South => tileZ + size,
            _ => tileZ + size / 2
        };

        int neighborRegionX = Math.Clamp(
            sampleX / size,
            0,
            snapshot.RegionsX - 1);
        int neighborRegionZ = Math.Clamp(
            sampleZ / size,
            0,
            snapshot.RegionsY - 1);
        bool neighborIsDetailed = false;

        if (sampleX >= 0 && sampleZ >= 0 &&
            sampleX < snapshot.TileWidth &&
            sampleZ < snapshot.TileHeight)
        {
            neighborIsDetailed = !snapshot.IsUniformRegion(
                snapshot.GetRegionIndex(neighborRegionX, neighborRegionZ));
        }

        if (!neighborIsDetailed)
        {
            ushort neighborHeight = snapshot.GetHeightUnitsAtTile(
                sampleX,
                sampleZ);

            if (heightUnits > neighborHeight)
            {
                AddBoundaryWall(
                    positions,
                    normals,
                    colors,
                    tileX,
                    tileZ,
                    size,
                    boundary,
                    ToRenderHeight(heightUnits),
                    ToRenderHeight(neighborHeight),
                    GetSurfaceColor(heightUnits, materialId).Darken(0.62f));
            }

            return;
        }

        Color32 sideColor = GetSurfaceColor(heightUnits, materialId).Darken(0.62f);
        for (int i = 0; i < size; i++)
        {
            int neighborX = boundary switch
            {
                Boundary.West => tileX - 1,
                Boundary.East => tileX + size,
                _ => tileX + i
            };
            int neighborZ = boundary switch
            {
                Boundary.North => tileZ - 1,
                Boundary.South => tileZ + size,
                _ => tileZ + i
            };

            ushort neighborHeight = snapshot.GetHeightUnitsAtTile(
                neighborX,
                neighborZ);

            if (heightUnits <= neighborHeight)
                continue;

            AddBoundaryWall(
                positions,
                normals,
                colors,
                boundary switch
                {
                    Boundary.West => tileX,
                    Boundary.East => tileX + size - 1,
                    _ => tileX + i
                },
                boundary switch
                {
                    Boundary.North => tileZ,
                    Boundary.South => tileZ + size - 1,
                    _ => tileZ + i
                },
                1,
                boundary,
                ToRenderHeight(heightUnits),
                ToRenderHeight(neighborHeight),
                sideColor);
        }
    }

    private static void BuildDetailedRegion(
        WorldTerrainSnapshot snapshot,
        int regionX,
        int regionY,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors,
        ref float maxY)
    {
        int size = snapshot.RegionTileSize;
        int originX = regionX * size;
        int originZ = regionY * size;

        for (int localZ = 0; localZ < size; localZ++)
        {
            int worldZ = originZ + localZ;

            for (int localX = 0; localX < size; localX++)
            {
                int worldX = originX + localX;
                ushort heightUnits = snapshot.GetHeightUnitsAtTile(worldX, worldZ);
                ushort materialId = snapshot.GetMaterialIdAtTile(worldX, worldZ);

                if (heightUnits == 0 || materialId == 0)
                    continue;

                float height = ToRenderHeight(heightUnits);
                maxY = Math.Max(maxY, height);

                Color32 topColor = GetSurfaceColor(heightUnits, materialId);
                AddTopQuad(
                    positions,
                    normals,
                    colors,
                    worldX * HorizontalUnitsPerTile,
                    worldZ * HorizontalUnitsPerTile,
                    HorizontalUnitsPerTile,
                    height,
                    topColor);

                AddDetailedSideIfExposed(
                    snapshot, worldX, worldZ, heightUnits,
                    Boundary.West, topColor, positions, normals, colors);
                AddDetailedSideIfExposed(
                    snapshot, worldX, worldZ, heightUnits,
                    Boundary.East, topColor, positions, normals, colors);
                AddDetailedSideIfExposed(
                    snapshot, worldX, worldZ, heightUnits,
                    Boundary.North, topColor, positions, normals, colors);
                AddDetailedSideIfExposed(
                    snapshot, worldX, worldZ, heightUnits,
                    Boundary.South, topColor, positions, normals, colors);
            }
        }
    }

    private static void AddDetailedSideIfExposed(
        WorldTerrainSnapshot snapshot,
        int tileX,
        int tileZ,
        ushort heightUnits,
        Boundary boundary,
        Color32 topColor,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors)
    {
        int neighborX = boundary switch
        {
            Boundary.West => tileX - 1,
            Boundary.East => tileX + 1,
            _ => tileX
        };
        int neighborZ = boundary switch
        {
            Boundary.North => tileZ - 1,
            Boundary.South => tileZ + 1,
            _ => tileZ
        };

        ushort neighborHeight = snapshot.GetHeightUnitsAtTile(
            neighborX,
            neighborZ);

        if (heightUnits <= neighborHeight)
            return;

        AddBoundaryWall(
            positions,
            normals,
            colors,
            tileX,
            tileZ,
            1,
            boundary,
            ToRenderHeight(heightUnits),
            ToRenderHeight(neighborHeight),
            topColor.Darken(0.62f));
    }

    private static void AddTopQuad(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors,
        float x,
        float z,
        float size,
        float height,
        Color32 color)
    {
        Vector3 a = new(x, height, z);
        Vector3 b = new(x, height, z + size);
        Vector3 c = new(x + size, height, z + size);
        Vector3 d = new(x + size, height, z);

        AddQuad(
            positions, normals, colors,
            a, b, c, d,
            Vector3.UnitY,
            color);
    }

    private static void AddBoundaryWall(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors,
        int tileX,
        int tileZ,
        int tileCount,
        Boundary boundary,
        float top,
        float bottom,
        Color32 color)
    {
        if (top <= bottom)
            return;

        float x0 = tileX * HorizontalUnitsPerTile;
        float z0 = tileZ * HorizontalUnitsPerTile;
        float length = tileCount * HorizontalUnitsPerTile;
        float x1 = x0 + length;
        float z1 = z0 + length;

        Vector3 a;
        Vector3 b;
        Vector3 c;
        Vector3 d;
        Vector3 normal;

        switch (boundary)
        {
            case Boundary.West:
                a = new Vector3(x0, top, z1);
                b = new Vector3(x0, top, z0);
                c = new Vector3(x0, bottom, z0);
                d = new Vector3(x0, bottom, z1);
                normal = -Vector3.UnitX;
                break;

            case Boundary.East:
                a = new Vector3(x1, top, z0);
                b = new Vector3(x1, top, z1);
                c = new Vector3(x1, bottom, z1);
                d = new Vector3(x1, bottom, z0);
                normal = Vector3.UnitX;
                break;

            case Boundary.North:
                a = new Vector3(x0, top, z0);
                b = new Vector3(x1, top, z0);
                c = new Vector3(x1, bottom, z0);
                d = new Vector3(x0, bottom, z0);
                normal = -Vector3.UnitZ;
                break;

            default:
                a = new Vector3(x1, top, z1);
                b = new Vector3(x0, top, z1);
                c = new Vector3(x0, bottom, z1);
                d = new Vector3(x1, bottom, z1);
                normal = Vector3.UnitZ;
                break;
        }

        AddQuad(
            positions, normals, colors,
            a, b, c, d,
            normal,
            color);
    }

    private static void AddQuad(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        Vector3 normal,
        Color32 color)
    {
        AddTriangle(positions, normals, colors, a, b, c, normal, color);
        AddTriangle(positions, normals, colors, a, c, d, normal, color);
    }

    private static void AddTriangle(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Color32> colors,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 normal,
        Color32 color)
    {
        positions.Add(a);
        positions.Add(b);
        positions.Add(c);

        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        colors.Add(color);
        colors.Add(color);
        colors.Add(color);
    }

    private static float ToRenderHeight(ushort heightUnits) =>
        heightUnits * 0.1f * VerticalUnitsPerMeter;

    private static Color32 GetSurfaceColor(
        ushort heightUnits,
        ushort materialId)
    {
        float heightMeters = heightUnits * 0.1f;

        if (materialId != 1)
            return new Color32(137, 132, 112);

        if (heightMeters < 8f)
            return new Color32(91, 119, 77);
        if (heightMeters < 18f)
            return new Color32(111, 133, 82);
        if (heightMeters < 30f)
            return new Color32(139, 141, 98);
        if (heightMeters < 40f)
            return new Color32(157, 145, 111);

        return new Color32(174, 166, 151);
    }

    private static int DivideRoundUp(int value, int divisor) =>
        (value + divisor - 1) / divisor;

    private enum Boundary : byte
    {
        West,
        East,
        North,
        South
    }
}
