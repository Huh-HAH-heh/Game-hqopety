using System;
using System.Collections.Generic;
using System.Diagnostics;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Renders the existing gameplay map as a real 1 m voxel volume.
/// Geometry is generated lazily for visible XY chunks; voxel occupancy is read
/// directly from WorldMap's sparse volume tree and column backing.
/// </summary>
public sealed class VoxelTerrainRenderer : IDisposable
{
    private const int SamplesPerChunk = 16;
    private const int MaxCachedChunks = 96;
    private const int MaxChunkBuildsPerFrame = 2;
    private const double MaxChunkBuildMilliseconds = 4.0;

    private readonly record struct ChunkKey(int X, int Y, int LodStep);

    private sealed class ChunkMesh : IDisposable
    {
        public readonly VertexArray Faces = new(PrimitiveType.Triangles);
        public long Revision;
        public long LastUsedFrame;

        public void Dispose() => Faces.Dispose();
    }

    private readonly struct ChunkRequest
    {
        public readonly ChunkKey Key;
        public readonly int StartX;
        public readonly int StartY;
        public readonly int WidthSamples;
        public readonly int HeightSamples;
        public readonly long Revision;
        public readonly float DistanceSquared;

        public ChunkRequest(
            ChunkKey key,
            int startX,
            int startY,
            int widthSamples,
            int heightSamples,
            long revision,
            float distanceSquared)
        {
            Key = key;
            StartX = startX;
            StartY = startY;
            WidthSamples = widthSamples;
            HeightSamples = heightSamples;
            Revision = revision;
            DistanceSquared = distanceSquared;
        }
    }

    private readonly Dictionary<ChunkKey, ChunkMesh> _chunks = new();
    private readonly List<ChunkRequest> _visibleRequests = new();
    private readonly List<ChunkRequest> _pendingBuilds = new();
    private readonly List<ChunkKey> _staleKeys = new();
    private long _frameNumber;

    public int CachedChunkCount => _chunks.Count;
    public int ChunksBuiltLastFrame { get; private set; }
    public int VisibleChunkCount => _visibleRequests.Count;

    public static Vector2f GetCameraCenter(
        WorldMap worldMap,
        float tilePixelSize)
    {
        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float highestMeters = worldMap.LayerCount * 0.1f;
        return Project(
            worldMap.TileWidth * 0.5f,
            worldMap.TileHeight * 0.5f,
            highestMeters * 0.42f,
            tileSize);
    }

    public void Draw(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize)
    {
        if (!worldMap.UsesDiscreteBlockTerrain || tilePixelSize <= 0f)
            return;

        _frameNumber++;
        ChunksBuiltLastFrame = 0;

        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float pixelsPerTile = tileSize * window.Size.X /
            MathF.Max(1f, cameraView.Size.X);

        int lodStep = pixelsPerTile < 1.25f ? 64
            : pixelsPerTile < 2.5f ? 32
            : pixelsPerTile < 5f ? 16
            : pixelsPerTile < 10f ? 8
            : pixelsPerTile < 20f ? 4
            : pixelsPerTile < 40f ? 2
            : 1;

        float highestMeters = GetHighestHeightMeters(worldMap);
        GetVisibleTileBounds(
            cameraView,
            tileSize,
            highestMeters,
            worldMap,
            out int minX,
            out int maxX,
            out int minY,
            out int maxY);

        int chunkSize = SamplesPerChunk * lodStep;
        int minChunkX = minX / chunkSize;
        int maxChunkX = maxX / chunkSize;
        int minChunkY = minY / chunkSize;
        int maxChunkY = maxY / chunkSize;

        _visibleRequests.Clear();
        _pendingBuilds.Clear();

        float centerX = cameraView.Center.X;
        float centerY = cameraView.Center.Y;

        for (int chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
        {
            int startY = chunkY * chunkSize;
            if (startY >= worldMap.TileHeight)
                continue;

            int heightSamples = Math.Min(
                SamplesPerChunk,
                (worldMap.TileHeight - startY + lodStep - 1) / lodStep);

            for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                int startX = chunkX * chunkSize;
                if (startX >= worldMap.TileWidth)
                    continue;

                int widthSamples = Math.Min(
                    SamplesPerChunk,
                    (worldMap.TileWidth - startX + lodStep - 1) / lodStep);
                if (widthSamples <= 0 || heightSamples <= 0)
                    continue;

                int endX = Math.Min(
                    worldMap.TileWidth,
                    startX + widthSamples * lodStep);
                int endY = Math.Min(
                    worldMap.TileHeight,
                    startY + heightSamples * lodStep);

                ChunkKey key = new(chunkX, chunkY, lodStep);
                long revision = GetChunkRevision(
                    worldMap,
                    startX,
                    startY,
                    endX,
                    endY);

                Vector2f projectedCenter = Project(
                    (startX + endX) * 0.5f,
                    (startY + endY) * 0.5f,
                    highestMeters * 0.45f,
                    tileSize);
                float dx = projectedCenter.X - centerX;
                float dy = projectedCenter.Y - centerY;

                var request = new ChunkRequest(
                    key,
                    startX,
                    startY,
                    widthSamples,
                    heightSamples,
                    revision,
                    dx * dx + dy * dy);

                _visibleRequests.Add(request);

                if (!_chunks.TryGetValue(key, out ChunkMesh? mesh) ||
                    mesh.Revision != revision)
                {
                    _pendingBuilds.Add(request);
                }
                else
                {
                    mesh.LastUsedFrame = _frameNumber;
                }
            }
        }

        _pendingBuilds.Sort(static (a, b) =>
            a.DistanceSquared.CompareTo(b.DistanceSquared));

        long buildStarted = Stopwatch.GetTimestamp();
        for (int i = 0;
             i < _pendingBuilds.Count &&
             ChunksBuiltLastFrame < MaxChunkBuildsPerFrame;
             i++)
        {
            ChunkRequest request = _pendingBuilds[i];
            ChunkMesh rebuilt = BuildChunk(worldMap, request, lodStep, tileSize);
            rebuilt.Revision = request.Revision;
            rebuilt.LastUsedFrame = _frameNumber;

            if (_chunks.Remove(request.Key, out ChunkMesh? previous))
                previous.Dispose();

            _chunks.Add(request.Key, rebuilt);
            ChunksBuiltLastFrame++;

            if (Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds >=
                MaxChunkBuildMilliseconds)
            {
                break;
            }
        }

        // Opaque faces are submitted in isometric depth order. Inside each
        // chunk, cells are built in the same X+Y order.
        _visibleRequests.Sort(static (a, b) =>
        {
            int depthA = a.Key.X + a.Key.Y;
            int depthB = b.Key.X + b.Key.Y;
            int depthCompare = depthA.CompareTo(depthB);
            return depthCompare != 0
                ? depthCompare
                : a.Key.X.CompareTo(b.Key.X);
        });

        for (int i = 0; i < _visibleRequests.Count; i++)
        {
            if (_chunks.TryGetValue(_visibleRequests[i].Key, out ChunkMesh? mesh) &&
                mesh.Faces.VertexCount > 0)
            {
                mesh.LastUsedFrame = _frameNumber;
                window.Draw(mesh.Faces);
            }
        }

        PruneCache();
    }

    private static ChunkMesh BuildChunk(
        WorldMap worldMap,
        ChunkRequest request,
        int lodStep,
        float tileSize)
    {
        int levels = (worldMap.LayerCount + WorldMap.HeightUnitsPerMeter - 1) /
            WorldMap.HeightUnitsPerMeter;
        int strideX = request.WidthSamples + 1;
        int strideY = request.HeightSamples + 1;
        int plane = strideX * strideY;
        ushort[] voxels = new ushort[checked(plane * levels)];

        // One sample per LOD cell plus a one-cell halo on its positive edges.
        // The halo lets chunks hide shared faces without consulting another mesh.
        for (int z = 0; z < levels; z++)
        {
            int sourceLayer = z * WorldMap.HeightUnitsPerMeter;
            int zOffset = z * plane;

            for (int sampleY = 0; sampleY <= request.HeightSamples; sampleY++)
            {
                int cellY = request.StartY + sampleY * lodStep;
                if (cellY >= worldMap.TileHeight)
                    continue;

                int nextY = Math.Min(worldMap.TileHeight, cellY + lodStep);
                int sampleWorldY = cellY + Math.Max(0, nextY - cellY) / 2;

                for (int sampleX = 0; sampleX <= request.WidthSamples; sampleX++)
                {
                    int cellX = request.StartX + sampleX * lodStep;
                    if (cellX >= worldMap.TileWidth)
                        continue;

                    int nextX = Math.Min(worldMap.TileWidth, cellX + lodStep);
                    int sampleWorldX = cellX + Math.Max(0, nextX - cellX) / 2;

                    voxels[zOffset + sampleX + sampleY * strideX] =
                        worldMap.GetMaterialId(sampleWorldX, sampleWorldY, sourceLayer);
                }
            }
        }

        var mesh = new ChunkMesh();
        int lastDepth = request.WidthSamples + request.HeightSamples - 2;

        for (int depth = 0; depth <= lastDepth; depth++)
        {
            int firstX = Math.Max(0, depth - (request.HeightSamples - 1));
            int lastX = Math.Min(request.WidthSamples - 1, depth);

            for (int sampleX = firstX; sampleX <= lastX; sampleX++)
            {
                int sampleY = depth - sampleX;
                int cellX = request.StartX + sampleX * lodStep;
                int cellY = request.StartY + sampleY * lodStep;
                int cellRight = Math.Min(worldMap.TileWidth, cellX + lodStep);
                int cellBottom = Math.Min(worldMap.TileHeight, cellY + lodStep);
                if (cellX >= cellRight || cellY >= cellBottom)
                    continue;

                int cell = sampleX + sampleY * strideX;
                for (int z = 0; z < levels; z++)
                {
                    int offset = z * plane + cell;
                    ushort materialId = voxels[offset];
                    if (materialId == 0)
                        continue;

                    float bottomZ = z;
                    float topZ = Math.Min(
                        z + 1f,
                        worldMap.LayerCount / (float)WorldMap.HeightUnitsPerMeter);

                    ushort above = z + 1 < levels
                        ? voxels[offset + plane]
                        : (ushort)0;
                    ushort east = voxels[offset + 1];
                    ushort south = voxels[offset + strideX];

                    if (east == 0)
                    {
                        AppendEastFace(
                            mesh.Faces,
                            cellX,
                            cellY,
                            cellRight,
                            cellBottom,
                            bottomZ,
                            topZ,
                            GetFaceColor(materialId, z, levels, 0.72f),
                            tileSize);
                    }

                    if (south == 0)
                    {
                        AppendSouthFace(
                            mesh.Faces,
                            cellX,
                            cellY,
                            cellRight,
                            cellBottom,
                            bottomZ,
                            topZ,
                            GetFaceColor(materialId, z, levels, 0.56f),
                            tileSize);
                    }

                    if (above == 0)
                    {
                        AppendTopFace(
                            mesh.Faces,
                            cellX,
                            cellY,
                            cellRight,
                            cellBottom,
                            topZ,
                            GetFaceColor(materialId, z, levels, 1f),
                            tileSize);
                    }
                }
            }
        }

        return mesh;
    }

    private static long GetChunkRevision(
        WorldMap worldMap,
        int startX,
        int startY,
        int endX,
        int endY)
    {
        int firstRegionX = startX / TerrainRegion.TilesPerSide;
        int firstRegionY = startY / TerrainRegion.TilesPerSide;
        int lastRegionX = Math.Max(
            firstRegionX,
            (Math.Max(startX + 1, endX) - 1) / TerrainRegion.TilesPerSide);
        int lastRegionY = Math.Max(
            firstRegionY,
            (Math.Max(startY + 1, endY) - 1) / TerrainRegion.TilesPerSide);

        long revision = 17;
        unchecked
        {
            for (int y = firstRegionY; y <= lastRegionY; y++)
            {
                for (int x = firstRegionX; x <= lastRegionX; x++)
                {
                    revision = revision * 31 +
                        worldMap.GetRegionTerrainVersion(x, y);
                }
            }
        }

        return revision;
    }

    private void PruneCache()
    {
        if (_chunks.Count <= MaxCachedChunks)
            return;

        _staleKeys.Clear();
        foreach (KeyValuePair<ChunkKey, ChunkMesh> entry in _chunks)
        {
            bool visible = false;
            for (int i = 0; i < _visibleRequests.Count; i++)
            {
                if (_visibleRequests[i].Key == entry.Key)
                {
                    visible = true;
                    break;
                }
            }

            if (!visible)
                _staleKeys.Add(entry.Key);
        }

        _staleKeys.Sort((a, b) =>
            _chunks[a].LastUsedFrame.CompareTo(_chunks[b].LastUsedFrame));

        int removeCount = Math.Min(
            _staleKeys.Count,
            _chunks.Count - MaxCachedChunks);

        for (int i = 0; i < removeCount; i++)
        {
            if (_chunks.Remove(_staleKeys[i], out ChunkMesh? mesh))
                mesh.Dispose();
        }
    }

    private static float GetHighestHeightMeters(WorldMap worldMap) =>
        worldMap.LayerCount * 0.1f;

    private static Color GetFaceColor(
        ushort materialId,
        int z,
        int levels,
        float shade)
    {
        float height = levels <= 1 ? 0f : z / (float)(levels - 1);
        int baseShade = 86 + (int)(height * 32f);
        int materialTint = materialId == 1 ? 0 : 3 + materialId % 4 * 2;
        int value = Math.Clamp((int)((baseShade + materialTint) * shade), 0, 255);
        int green = Math.Clamp(value + (shade > 0.9f ? 2 : 0), 0, 255);
        int blue = Math.Clamp(value + (shade > 0.9f ? 4 : -1), 0, 255);
        return new Color((byte)value, (byte)green, (byte)blue, 255);
    }

    private static void AppendTopFace(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        float z,
        Color color,
        float tileSize)
    {
        Vector2f north = Project(left, top, z, tileSize);
        Vector2f east = Project(right, top, z, tileSize);
        Vector2f south = Project(right, bottom, z, tileSize);
        Vector2f west = Project(left, bottom, z, tileSize);
        AppendQuad(vertices, north, east, south, west, color);
    }

    private static void AppendEastFace(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        float bottomZ,
        float topZ,
        Color color,
        float tileSize)
    {
        Vector2f topA = Project(right, top, topZ, tileSize);
        Vector2f topB = Project(right, bottom, topZ, tileSize);
        Vector2f bottomB = Project(right, bottom, bottomZ, tileSize);
        Vector2f bottomA = Project(right, top, bottomZ, tileSize);
        AppendQuad(vertices, topA, topB, bottomB, bottomA, color);
    }

    private static void AppendSouthFace(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        float bottomZ,
        float topZ,
        Color color,
        float tileSize)
    {
        Vector2f topA = Project(right, bottom, topZ, tileSize);
        Vector2f topB = Project(left, bottom, topZ, tileSize);
        Vector2f bottomB = Project(left, bottom, bottomZ, tileSize);
        Vector2f bottomA = Project(right, bottom, bottomZ, tileSize);
        AppendQuad(vertices, topA, topB, bottomB, bottomA, color);
    }

    private static void AppendQuad(
        VertexArray vertices,
        Vector2f a,
        Vector2f b,
        Vector2f c,
        Vector2f d,
        Color color)
    {
        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(b, color));
        vertices.Append(new Vertex(c, color));
        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(c, color));
        vertices.Append(new Vertex(d, color));
    }

    private static void GetVisibleTileBounds(
        View view,
        float tileSize,
        float highestMeters,
        WorldMap worldMap,
        out int minX,
        out int maxX,
        out int minY,
        out int maxY)
    {
        minX = worldMap.MaxTileX;
        minY = worldMap.MaxTileY;
        maxX = 0;
        maxY = 0;

        // Cache a generous viewport margin so ordinary panning does not
        // regenerate nearby meshes.
        float left = view.Center.X - view.Size.X * 0.7f;
        float right = view.Center.X + view.Size.X * 0.7f;
        float top = view.Center.Y - view.Size.Y * 0.7f;
        float bottom = view.Center.Y + view.Size.Y * 0.7f;
        float halfWidth = tileSize * 0.5f;
        float quarterHeight = tileSize * 0.25f;
        float verticalScale = tileSize * 0.5f;

        for (int corner = 0; corner < 4; corner++)
        {
            float screenX = (corner & 1) == 0 ? left : right;
            float screenY = (corner & 2) == 0 ? top : bottom;

            for (int heightIndex = 0; heightIndex < 2; heightIndex++)
            {
                float baseY = screenY +
                    (heightIndex == 0 ? 0f : highestMeters * verticalScale);
                float projectedX = screenX / halfWidth;
                float projectedY = baseY / quarterHeight;
                float x = (projectedX + projectedY) * 0.5f;
                float y = (projectedY - projectedX) * 0.5f;

                minX = Math.Min(minX, (int)MathF.Floor(x));
                maxX = Math.Max(maxX, (int)MathF.Ceiling(x));
                minY = Math.Min(minY, (int)MathF.Floor(y));
                maxY = Math.Max(maxY, (int)MathF.Ceiling(y));
            }
        }

        minX = Math.Clamp(minX - 2, 0, worldMap.MaxTileX);
        maxX = Math.Clamp(maxX + 2, 0, worldMap.MaxTileX);
        minY = Math.Clamp(minY - 2, 0, worldMap.MaxTileY);
        maxY = Math.Clamp(maxY + 2, 0, worldMap.MaxTileY);
    }

    private static Vector2f Project(
        float x,
        float y,
        float heightMeters,
        float tileSize) =>
        new(
            (x - y) * tileSize * 0.5f,
            (x + y) * tileSize * 0.25f - heightMeters * tileSize * 0.5f);

    public void Dispose()
    {
        foreach (ChunkMesh mesh in _chunks.Values)
            mesh.Dispose();

        _chunks.Clear();
        _visibleRequests.Clear();
        _pendingBuilds.Clear();
        _staleKeys.Clear();
    }
}
