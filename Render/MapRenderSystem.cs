using System;
using System.Diagnostics;
using System.Collections.Generic;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class MapRenderSystem : IDisposable
{
    private sealed class TerrainChunkMesh : IDisposable
    {
        public VertexBuffer? Buffer;
        public VertexArray? Vertices;
        public int VertexCount;
        public long LastUsedFrame;

        public void Draw(RenderWindow window, RenderStates states)
        {
            if (Buffer != null)
            {
                Buffer.Draw(window, 0, (uint)VertexCount, states);
            }
            else if (Vertices != null && Vertices.VertexCount > 0)
            {
                window.Draw(Vertices, states);
            }
        }

        public void Dispose()
        {
            Buffer?.Dispose();
            Buffer = null;
            Vertices?.Dispose();
            Vertices = null;
        }
    }

    // Camera movement rebuilds only chunks entering the view.
    private readonly Dictionary<int, TerrainChunkMesh> _terrainChunks = new();
    private readonly List<TerrainChunkMesh> _visibleTerrainChunks = new();

    private Vertex[] _terrainVertices = Array.Empty<Vertex>();
    private int _terrainVertexCount;
    private int _buildingTerrainVertexCount;
    private int _cachedTerrainVertexCount;
    private bool _terrainBufferSupportChecked;
    private bool _useTerrainBuffer;
    private bool _terrainShaderChecked;
    private Shader? _terrainLayerShader;
    private bool _heightMapMode = true;
    private long _frameNumber;
    private long _chunkCacheTerrainVersion = long.MinValue;
    private int _chunkCacheVisibleMaxLayer = -1;
    private int _chunkCacheLodStep = -1;
    private float _chunkCacheTilePixelSize = -1f;

    private const int ExtraCachedTerrainChunks = 8;

    private const string TerrainVertexShaderSource =
        @"uniform float uVisibleLayer;
uniform float uLayerScreenOffset;
uniform float uDisplayMode;
void main()
{
    vec4 position = gl_Vertex;
    if (uDisplayMode < 0.5)
    {
        float cutSurface = min(gl_MultiTexCoord0.x, uVisibleLayer);
        float voxelDepth = max(0.0, cutSurface - gl_MultiTexCoord0.y);
        position.y -= voxelDepth * uLayerScreenOffset;
    }
    gl_Position = gl_ModelViewProjectionMatrix * position;
    gl_TexCoord[0] = gl_MultiTexCoord0;
    gl_FrontColor = gl_Color;
}";

    private const string TerrainFragmentShaderSource =
        @"uniform float uVisibleLayer;
uniform float uDisplayMode;
void main()
{
    if (uDisplayMode > 0.5)
    {
        if (abs(gl_TexCoord[0].x - gl_TexCoord[0].y) > 0.5)
            discard;
    }
    else if (gl_TexCoord[0].y > uVisibleLayer + 0.5)
        discard;
    gl_FragColor = gl_Color;
}";

    private readonly VertexArray _gridVertices =
        new VertexArray(PrimitiveType.Lines);

    private readonly VertexArray _waterVertices =
        new VertexArray(PrimitiveType.Triangles);

    private bool _mapCacheValid;
    private bool _gridCacheValid;
    private bool _waterCacheValid;

    private int _cachedMinTileX;
    private int _cachedMaxTileX;
    private int _cachedMinTileY;
    private int _cachedMaxTileY;
    private int _cachedLodStep;
    private int _cachedVisibleMaxLayer = -1;
    private int _cachedWaterVisibleMaxLayer = -1;
    private long _cachedTerrainVersion;
    private long _cachedWaterVersion;

    private int[] _rowTopLayers = Array.Empty<int>();
    private int[] _rowSurfaceLayers = Array.Empty<int>();

    public int TerrainVertexCount =>
        _terrainVertexCount;

    public int TerrainQuadCount =>
        TerrainVertexCount / 6;

    public int TerrainCachedVertexCount =>
        _cachedTerrainVertexCount;

    public int TerrainScratchCapacity =>
        _terrainVertices.Length;

    public int TerrainChunkCacheCount =>
        _terrainChunks.Count;

    public bool UsesTerrainLayerShader =>
        _terrainLayerShader != null;

    public bool HeightMapMode => _heightMapMode;

    public void ToggleHeightMapMode()
    {
        SetHeightMapMode(!_heightMapMode);
    }

    public void SetHeightMapMode(bool enabled)
    {
        _heightMapMode = enabled;
    }

    public bool UsesTerrainVertexBuffer =>
        _useTerrainBuffer;

    public long TerrainMeshRebuildCount { get; private set; }

    public double LastTerrainBuildMilliseconds { get; private set; }

    private const byte GridAlpha = 90;
    private const float LayerScreenOffset = 1.1f;

    public MapRenderSystem(
        float tilePixelSize)
    {
    }

    public void Draw(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float zoomLevel,
        bool showGrid,
        int visibleMaxLayer = -1)
    {
        if (tilePixelSize <= 0f)
            return;

        if (!_terrainBufferSupportChecked)
        {
            // Draw is reached only after the render window/context was created.
            _useTerrainBuffer = VertexBuffer.Available;
            _terrainBufferSupportChecked = true;
        }

        if (!_terrainShaderChecked)
        {
            _terrainShaderChecked = true;

            if (Shader.IsAvailable)
            {
                try
                {
                    _terrainLayerShader = Shader.FromString(
                        TerrainVertexShaderSource,
                        null,
                        TerrainFragmentShaderSource);
                }
                catch (Exception exception)
                {
                    Console.WriteLine(
                        $"[TerrainShader] Disabled; using CPU layer rebuilds: {exception.Message}");
                }
            }
        }

        int maxLayer =
            visibleMaxLayer < 0
                ? worldMap.LayerCount - 1
                : Math.Clamp(
                    visibleMaxLayer,
                    0,
                    worldMap.LayerCount - 1);

        Vector2f center = cameraView.Center;
        Vector2f viewSize = cameraView.Size;

        float screenMinX = center.X - viewSize.X * 0.5f;
        float screenMaxX = center.X + viewSize.X * 0.5f;
        float screenMinY = center.Y - viewSize.Y * 0.5f;
        float screenMaxY = center.Y + viewSize.Y * 0.5f;

        int lodStep = 1;

        if (zoomLevel >= 4.5f)
            lodStep = 3;
        else if (zoomLevel >= 2.5f)
            lodStep = 2;

        int minTileX =
            Math.Max(
                0,
                (int)MathF.Floor(screenMinX / tilePixelSize) -
                lodStep);

        int maxTileX =
            Math.Min(
                worldMap.MaxTileX,
                (int)MathF.Ceiling(screenMaxX / tilePixelSize) +
                lodStep);

        // In height-map mode surfaces are rendered on the XY plane.
        // Only the volume slice needs extra rows for layers shifted down in screen space.
        float maximumLayerOffset =
            _heightMapMode
                ? 0f
                : maxLayer * LayerScreenOffset;

        int minTileY =
            Math.Max(
                0,
                (int)MathF.Floor(
                    (screenMinY - maximumLayerOffset) /
                    tilePixelSize) - lodStep);

        int maxTileY =
            Math.Min(
                worldMap.MaxTileY,
                (int)MathF.Ceiling(
                    screenMaxY / tilePixelSize) + lodStep);

        minTileX = (minTileX / lodStep) * lodStep;
        minTileY = (minTileY / lodStep) * lodStep;

        bool mapCacheMatches =
            _mapCacheValid &&
            _cachedMinTileX == minTileX &&
            _cachedMaxTileX == maxTileX &&
            _cachedMinTileY == minTileY &&
            _cachedMaxTileY == maxTileY &&
            _cachedLodStep == lodStep &&
            _cachedVisibleMaxLayer == maxLayer &&
            _cachedTerrainVersion == worldMap.TerrainVersion;

        bool gridCacheMatches =
            _gridCacheValid &&
            _cachedMinTileX == minTileX &&
            _cachedMaxTileX == maxTileX &&
            _cachedMinTileY == minTileY &&
            _cachedMaxTileY == maxTileY &&
            _cachedLodStep == lodStep &&
            _cachedVisibleMaxLayer == maxLayer &&
            _cachedTerrainVersion == worldMap.TerrainVersion;

        if (!mapCacheMatches)
        {
            // Water and debug-grid caches still depend on viewport bounds.
            // Terrain geometry is cached independently per XY region.
            _waterCacheValid = false;
            _mapCacheValid = true;
            _cachedMinTileX = minTileX;
            _cachedMaxTileX = maxTileX;
            _cachedMinTileY = minTileY;
            _cachedMaxTileY = maxTileY;
            _cachedLodStep = lodStep;
            _cachedVisibleMaxLayer = maxLayer;
            _cachedTerrainVersion = worldMap.TerrainVersion;
            _gridCacheValid = false;
        }

        if (showGrid && !gridCacheMatches)
        {
            _gridVertices.Clear();

            for (int y = minTileY; y <= maxTileY; y += lodStep)
            {
                for (int x = minTileX; x <= maxTileX; x += lodStep)
                {
                    int topLayer =
                        FindVisibleTopLayer(
                            worldMap,
                            x,
                            y,
                            maxLayer);

                    if (topLayer < 0)
                        continue;

                    float left = x * tilePixelSize;
                    float top = y * tilePixelSize;
                    float right = (x + lodStep) * tilePixelSize;
                    float bottom = (y + lodStep) * tilePixelSize;

                    AppendGrid(
                        _gridVertices,
                        left,
                        top,
                        right,
                        bottom);
                }
            }

            _gridCacheValid = true;
            _cachedMinTileX = minTileX;
            _cachedMaxTileX = maxTileX;
            _cachedMinTileY = minTileY;
            _cachedMaxTileY = maxTileY;
            _cachedLodStep = lodStep;
            _cachedVisibleMaxLayer = maxLayer;
            _cachedTerrainVersion = worldMap.TerrainVersion;
        }

        DrawTerrainChunks(
            window,
            worldMap,
            minTileX,
            maxTileX,
            minTileY,
            maxTileY,
            lodStep,
            tilePixelSize,
            maxLayer);

        DrawWater(
            window,
            worldMap,
            minTileX,
            maxTileX,
            minTileY,
            maxTileY,
            lodStep,
            tilePixelSize,
            maxLayer);

        if (showGrid && _gridVertices.VertexCount > 0)
            window.Draw(_gridVertices);
    }

    private void DrawTerrainChunks(
        RenderWindow window,
        WorldMap worldMap,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        int layerCacheKey =
            _terrainLayerShader != null
                ? -1
                : _heightMapMode
                    ? -2
                    : visibleMaxLayer;

        int geometryMaxLayer =
            _terrainLayerShader != null || _heightMapMode
                ? worldMap.LayerCount - 1
                : visibleMaxLayer;

        if (_chunkCacheTerrainVersion != worldMap.TerrainVersion ||
            _chunkCacheVisibleMaxLayer != layerCacheKey ||
            _chunkCacheLodStep != lodStep ||
            _chunkCacheTilePixelSize != tilePixelSize)
        {
            ClearTerrainChunkCache();
            _chunkCacheTerrainVersion = worldMap.TerrainVersion;
            _chunkCacheVisibleMaxLayer = layerCacheKey;
            _chunkCacheLodStep = lodStep;
            _chunkCacheTilePixelSize = tilePixelSize;
        }

        _frameNumber++;
        _visibleTerrainChunks.Clear();
        _terrainVertexCount = 0;

        if (maxTileX < minTileX || maxTileY < minTileY)
        {
            ClearTerrainChunkCache();
            return;
        }

        int minRegionX = minTileX / TerrainRegion.TilesPerSide;
        int maxRegionX = maxTileX / TerrainRegion.TilesPerSide;
        int minRegionY = minTileY / TerrainRegion.TilesPerSide;
        int maxRegionY = maxTileY / TerrainRegion.TilesPerSide;
        int visibleChunkCount =
            (maxRegionX - minRegionX + 1) *
            (maxRegionY - minRegionY + 1);

        long rebuildsBefore = TerrainMeshRebuildCount;
        long started = Stopwatch.GetTimestamp();

        for (int regionY = minRegionY;
             regionY <= maxRegionY;
             regionY++)
        {
            for (int regionX = minRegionX;
                 regionX <= maxRegionX;
                 regionX++)
            {
                int key = regionX + regionY * worldMap.RegionsX;

                if (!_terrainChunks.TryGetValue(key, out TerrainChunkMesh? mesh))
                {
                    mesh = BuildTerrainChunk(
                        worldMap,
                        regionX,
                        regionY,
                        lodStep,
                        tilePixelSize,
                        geometryMaxLayer);

                    _terrainChunks.Add(key, mesh);
                    _cachedTerrainVertexCount += mesh.VertexCount;
                }

                mesh.LastUsedFrame = _frameNumber;
                _visibleTerrainChunks.Add(mesh);
                _terrainVertexCount += mesh.VertexCount;
            }
        }

        RenderStates terrainStates = RenderStates.Default;

        if (_terrainLayerShader != null)
        {
            _terrainLayerShader.SetUniform("uVisibleLayer", (float)visibleMaxLayer);
            _terrainLayerShader.SetUniform("uLayerScreenOffset", LayerScreenOffset);
            _terrainLayerShader.SetUniform("uDisplayMode", _heightMapMode ? 1f : 0f);
            terrainStates = new RenderStates(_terrainLayerShader);
        }

        for (int i = 0; i < _visibleTerrainChunks.Count; i++)
            _visibleTerrainChunks[i].Draw(window, terrainStates);

        if (TerrainMeshRebuildCount != rebuildsBefore)
        {
            LastTerrainBuildMilliseconds =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        TrimTerrainChunkCache(
            worldMap,
            minRegionX,
            maxRegionX,
            minRegionY,
            maxRegionY,
            visibleChunkCount + ExtraCachedTerrainChunks);
    }

    private TerrainChunkMesh BuildTerrainChunk(
        WorldMap worldMap,
        int regionX,
        int regionY,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        _buildingTerrainVertexCount = 0;

        int minTileX = regionX * TerrainRegion.TilesPerSide;
        int maxTileX = Math.Min(
            worldMap.MaxTileX,
            minTileX + TerrainRegion.TilesPerSide - 1);
        int minTileY = regionY * TerrainRegion.TilesPerSide;
        int maxTileY = Math.Min(
            worldMap.MaxTileY,
            minTileY + TerrainRegion.TilesPerSide - 1);

        int columnCount = (maxTileX - minTileX) / lodStep + 1;

        if (_rowTopLayers.Length < columnCount)
        {
            Array.Resize(ref _rowTopLayers, columnCount);
            Array.Resize(ref _rowSurfaceLayers, columnCount);
        }

        for (int y = minTileY; y <= maxTileY; y += lodStep)
        {
            int rowMaxLayer = -1;

            for (int column = 0; column < columnCount; column++)
            {
                int x = minTileX + column * lodStep;
                int surfaceLayer = FindVisibleTopLayer(
                    worldMap,
                    x,
                    y,
                    worldMap.LayerCount - 1);

                _rowSurfaceLayers[column] = surfaceLayer;

                int topLayer = FindVisibleTopLayer(
                    worldMap,
                    x,
                    y,
                    visibleMaxLayer);

                _rowTopLayers[column] = topLayer;

                if (topLayer > rowMaxLayer)
                    rowMaxLayer = topLayer;
            }

            // Merge adjacent equal-height/equal-material cells inside this region.
            for (int z = 0; z <= rowMaxLayer; z++)
            {
                int column = 0;

                while (column < columnCount)
                {
                    int topLayer = _rowTopLayers[column];

                    if (topLayer < z)
                    {
                        column++;
                        continue;
                    }

                    if (_terrainLayerShader == null &&
                        _heightMapMode &&
                        z != topLayer)
                    {
                        column++;
                        continue;
                    }

                    int x = minTileX + column * lodStep;
                    ushort materialId = worldMap.GetMaterialId(x, y, z);

                    if (materialId == 0)
                    {
                        column++;
                        continue;
                    }

                    int runStart = column;
                    int surfaceLayer = _rowSurfaceLayers[runStart];
                    column++;

                    while (column < columnCount &&
                           _rowTopLayers[column] == topLayer &&
                           _rowSurfaceLayers[column] == surfaceLayer)
                    {
                        int nextX = minTileX + column * lodStep;

                        if (worldMap.GetMaterialId(nextX, y, z) != materialId)
                            break;

                        column++;
                    }

                    float left =
                        (minTileX + runStart * lodStep) * tilePixelSize;
                    float right =
                        (minTileX + column * lodStep) * tilePixelSize;
                    float screenDepth = topLayer - z;
                    float actualDepth = surfaceLayer - z;
                    float top =
                        y * tilePixelSize +
                        screenDepth * LayerScreenOffset;
                    float bottom =
                        (y + lodStep) * tilePixelSize +
                        screenDepth * LayerScreenOffset;

                    AppendTerrainQuad(
                        left,
                        top,
                        right,
                        bottom,
                        TerrainHeightPalette.ShadeDepth(
                            TerrainHeightPalette.GetSurfaceColor(
                                surfaceLayer,
                                worldMap.LayerCount),
                            actualDepth),
                        surfaceLayer,
                        z);
                }
            }
        }

        TerrainChunkMesh mesh = CreateTerrainChunkMesh(_buildingTerrainVertexCount);
        TerrainMeshRebuildCount++;
        return mesh;
    }

    private TerrainChunkMesh CreateTerrainChunkMesh(int vertexCount)
    {
        TerrainChunkMesh mesh = new TerrainChunkMesh
        {
            VertexCount = vertexCount
        };

        if (vertexCount == 0)
            return mesh;

        if (_useTerrainBuffer)
        {
            VertexBuffer buffer = new VertexBuffer(
                (uint)vertexCount,
                PrimitiveType.Triangles,
                VertexBuffer.UsageSpecifier.Dynamic);

            if (buffer.Update(_terrainVertices, (uint)vertexCount, 0))
            {
                mesh.Buffer = buffer;
                return mesh;
            }

            buffer.Dispose();
            _useTerrainBuffer = false;
        }

        VertexArray vertices = new VertexArray(PrimitiveType.Triangles);

        for (int i = 0; i < vertexCount; i++)
            vertices.Append(_terrainVertices[i]);

        mesh.Vertices = vertices;
        return mesh;
    }

    private void AppendTerrainQuad(
        float left,
        float top,
        float right,
        float bottom,
        Color color,
        int surfaceLayer,
        int voxelLayer)
    {
        EnsureTerrainVertexCapacity(_buildingTerrainVertexCount + 6);

        Vector2f topLeft = new Vector2f(left, top);
        Vector2f topRight = new Vector2f(right, top);
        Vector2f bottomRight = new Vector2f(right, bottom);
        Vector2f bottomLeft = new Vector2f(left, bottom);
        Vector2f layerData = new Vector2f(surfaceLayer, voxelLayer);

        _terrainVertices[_buildingTerrainVertexCount++] = new Vertex(topLeft, color, layerData);
        _terrainVertices[_buildingTerrainVertexCount++] = new Vertex(topRight, color, layerData);
        _terrainVertices[_buildingTerrainVertexCount++] = new Vertex(bottomRight, color, layerData);
        _terrainVertices[_buildingTerrainVertexCount++] = new Vertex(topLeft, color, layerData);
        _terrainVertices[_buildingTerrainVertexCount++] = new Vertex(bottomRight, color, layerData);
        _terrainVertices[_buildingTerrainVertexCount++] = new Vertex(bottomLeft, color, layerData);
    }

    private void EnsureTerrainVertexCapacity(int required)
    {
        if (required <= _terrainVertices.Length)
            return;

        int newCapacity = Math.Max(4096, _terrainVertices.Length * 2);

        while (newCapacity < required)
            newCapacity *= 2;

        Array.Resize(ref _terrainVertices, newCapacity);
    }

    private void TrimTerrainChunkCache(
        WorldMap worldMap,
        int minRegionX,
        int maxRegionX,
        int minRegionY,
        int maxRegionY,
        int targetCount)
    {
        while (_terrainChunks.Count > targetCount)
        {
            int oldestKey = -1;
            long oldestFrame = long.MaxValue;

            foreach (KeyValuePair<int, TerrainChunkMesh> entry in _terrainChunks)
            {
                int regionX = entry.Key % worldMap.RegionsX;
                int regionY = entry.Key / worldMap.RegionsX;

                if (regionX >= minRegionX && regionX <= maxRegionX &&
                    regionY >= minRegionY && regionY <= maxRegionY)
                {
                    continue;
                }

                if (entry.Value.LastUsedFrame < oldestFrame)
                {
                    oldestFrame = entry.Value.LastUsedFrame;
                    oldestKey = entry.Key;
                }
            }

            if (oldestKey < 0)
                break;

            TerrainChunkMesh mesh = _terrainChunks[oldestKey];
            _cachedTerrainVertexCount -= mesh.VertexCount;
            mesh.Dispose();
            _terrainChunks.Remove(oldestKey);
        }
    }

    private void ClearTerrainChunkCache()
    {
        foreach (TerrainChunkMesh mesh in _terrainChunks.Values)
            mesh.Dispose();

        _terrainChunks.Clear();
        _visibleTerrainChunks.Clear();
        _cachedTerrainVertexCount = 0;
        _terrainVertexCount = 0;
        _buildingTerrainVertexCount = 0;
    }

    private static int FindVisibleTopLayer(
        WorldMap worldMap,
        int x,
        int y,
        int visibleMaxLayer)
    {
        int topLayer =
            Math.Min(
                worldMap.GetSurfaceLayer(x, y),
                visibleMaxLayer);

        while (topLayer >= 0 &&
               worldMap.GetMaterialId(x, y, topLayer) == 0)
        {
            topLayer--;
        }

        return topLayer;
    }

    private void DrawWater(
        RenderWindow window,
        WorldMap worldMap,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        bool waterCacheMatches =
            _waterCacheValid &&
            _cachedMinTileX == minTileX &&
            _cachedMaxTileX == maxTileX &&
            _cachedMinTileY == minTileY &&
            _cachedMaxTileY == maxTileY &&
            _cachedLodStep == lodStep &&
            _cachedWaterVisibleMaxLayer == visibleMaxLayer &&
            _cachedWaterVersion == worldMap.Water.Version;

        if (!waterCacheMatches)
        {
            _waterVertices.Clear();

            Color waterColor = new Color(25, 45, 55, 190);

            for (int y = minTileY; y <= maxTileY; y += lodStep)
            {
                for (int x = minTileX; x <= maxTileX; x += lodStep)
                {
                    int waterZ = worldMap.Water.GetTopLevel(x, y);

                    if (waterZ < 0 || waterZ > visibleMaxLayer)
                        continue;

                    float left = x * tilePixelSize;
                    float top = y * tilePixelSize;
                    float right = (x + lodStep) * tilePixelSize;
                    float bottom = (y + lodStep) * tilePixelSize;

                    AppendQuad(
                        _waterVertices,
                        left,
                        top,
                        right,
                        bottom,
                        waterColor);
                }
            }

            _waterCacheValid = true;
            _cachedWaterVersion = worldMap.Water.Version;
            _cachedWaterVisibleMaxLayer = visibleMaxLayer;

            _cachedMinTileX = minTileX;
            _cachedMaxTileX = maxTileX;
            _cachedMinTileY = minTileY;
            _cachedMaxTileY = maxTileY;
            _cachedLodStep = lodStep;
        }

        if (_waterVertices.VertexCount > 0)
            window.Draw(_waterVertices);
    }

    public void Dispose()
    {
        ClearTerrainChunkCache();
        _terrainLayerShader?.Dispose();
        _terrainLayerShader = null;
        _gridVertices.Dispose();
        _waterVertices.Dispose();
        _terrainVertices = Array.Empty<Vertex>();
    }

    private static void AppendQuad(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        Color color)
    {
        Vector2f topLeft = new Vector2f(left, top);
        Vector2f topRight = new Vector2f(right, top);
        Vector2f bottomRight = new Vector2f(right, bottom);
        Vector2f bottomLeft = new Vector2f(left, bottom);

        vertices.Append(new Vertex(topLeft, color));
        vertices.Append(new Vertex(topRight, color));
        vertices.Append(new Vertex(bottomRight, color));
        vertices.Append(new Vertex(topLeft, color));
        vertices.Append(new Vertex(bottomRight, color));
        vertices.Append(new Vertex(bottomLeft, color));
    }

    private static void AppendGrid(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom)
    {
        Color color = new Color(50, 55, 70, GridAlpha);

        vertices.Append(new Vertex(new Vector2f(left, top), color));
        vertices.Append(new Vertex(new Vector2f(right, top), color));
        vertices.Append(new Vertex(new Vector2f(right, top), color));
        vertices.Append(new Vertex(new Vector2f(right, bottom), color));
        vertices.Append(new Vertex(new Vector2f(right, bottom), color));
        vertices.Append(new Vertex(new Vector2f(left, bottom), color));
        vertices.Append(new Vertex(new Vector2f(left, bottom), color));
        vertices.Append(new Vertex(new Vector2f(left, top), color));
    }
}
