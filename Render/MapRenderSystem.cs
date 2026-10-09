using System;
using System.Diagnostics;
using System.Collections.Generic;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class MapRenderSystem : IDisposable
{
    private readonly record struct TerrainChunkKey(int RegionIndex);

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
    private readonly Dictionary<TerrainChunkKey, TerrainChunkMesh> _terrainChunks = new();
    private readonly List<TerrainChunkMesh> _visibleTerrainChunks = new();

    private Vertex[] _terrainVertices = Array.Empty<Vertex>();
    private int _terrainVertexCount;
    private int _buildingTerrainVertexCount;
    private int _cachedTerrainVertexCount;
    private bool _terrainBufferSupportChecked;
    private bool _useTerrainBuffer;
    private bool _terrainShaderChecked;
    private Shader? _terrainLayerShader;

    private float _baseGray = 48f;
    private float _heightContrast = 14f;
    private long _frameNumber;
    private long _chunkCacheTerrainVersion = long.MinValue;
    private int _chunkCacheVisibleMaxLayer = -1;
    private float _chunkCacheTilePixelSize = -1f;

    private const int ExtraCachedTerrainChunks = 64;

    private const string TerrainVertexShaderSource =
        @"void main()
{
    gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
    gl_TexCoord[0] = gl_MultiTexCoord0;
    gl_FrontColor = gl_Color;
}";

    private const string TerrainFragmentShaderSource =
        @"uniform float uVisibleLayer;
uniform float uBaseGray;
uniform float uHeightContrast;
void main()
{
    float surfaceLayer = gl_TexCoord[0].x;
    float voxelLayer = gl_TexCoord[0].y;

    if (voxelLayer < uVisibleLayer)
        discard;

    float heightFraction = clamp(surfaceLayer, 0.0, 499.0) / 499.0;
    float tonalFraction = pow(heightFraction, 1.8);
    float heightRange = clamp(uBaseGray + uHeightContrast * 1.5, 24.0, 72.0);
    float gray = tonalFraction * heightRange;

    gray = clamp(gray, 0.0, 72.0) / 255.0;
    gl_FragColor = vec4(gray, gray, gray, 1.0);
}";

    private readonly VertexArray _gridVertices =
        new VertexArray(PrimitiveType.Lines);

    private readonly VertexArray _waterVertices =
        new VertexArray(PrimitiveType.Triangles);

    private float[] _smoothedChunkHeights = Array.Empty<float>();

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

    public float BaseGray => _baseGray;

    public float HeightContrast => _heightContrast;

    public void SetVisualSettings(
        float baseGray,
        float heightContrast)
    {
        baseGray = Math.Clamp(baseGray, 8f, 96f);
        heightContrast = Math.Clamp(heightContrast, 0f, 32f);

        bool changed =
            _baseGray != baseGray ||
            _heightContrast != heightContrast;

        if (!changed)
            return;

        _baseGray = baseGray;
        _heightContrast = heightContrast;

        // Shader uniforms are live; only the CPU fallback bakes vertex colors.
        if (_terrainLayerShader == null)
            ClearTerrainChunkCache();
    }

    public void ResetVisualSettings()
    {
        SetVisualSettings(48f, 14f);
    }

    public bool UsesTerrainVertexBuffer =>
        _useTerrainBuffer;

    public long TerrainMeshRebuildCount { get; private set; }

    public double LastTerrainBuildMilliseconds { get; private set; }

    private const byte GridAlpha = 90;

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

        // Keep one baked terrain detail level across zoom changes.
        // Existing chunk meshes are reused instead of rebuilt at each LOD threshold.
        const int lodStep = 1;

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

        int minTileY =
            Math.Max(
                0,
                (int)MathF.Floor(screenMinY / tilePixelSize) - lodStep);

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
        // Shader filtering keeps cached terrain geometry valid across Z-slices.
        int layerCacheKey =
            _terrainLayerShader == null
                ? visibleMaxLayer
                : -1;
        int geometryMaxLayer = visibleMaxLayer;

        if (_chunkCacheTerrainVersion != worldMap.TerrainVersion ||
            _chunkCacheVisibleMaxLayer != layerCacheKey ||
            _chunkCacheTilePixelSize != tilePixelSize)
        {
            ClearTerrainChunkCache();
            _chunkCacheTerrainVersion = worldMap.TerrainVersion;
            _chunkCacheVisibleMaxLayer = layerCacheKey;
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
                int regionIndex = regionX + regionY * worldMap.RegionsX;
                TerrainChunkKey key = new TerrainChunkKey(regionIndex);

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
            _terrainLayerShader.SetUniform("uBaseGray", _baseGray);
            _terrainLayerShader.SetUniform("uHeightContrast", _heightContrast);
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
        int rowCount = (maxTileY - minTileY) / lodStep + 1;
        int vertexStride = TerrainRegion.TilesPerSide + 4;
        int requiredSmoothCapacity = vertexStride * vertexStride;

        if (_smoothedChunkHeights.Length < requiredSmoothCapacity)
            Array.Resize(ref _smoothedChunkHeights, requiredSmoothCapacity);

        // Cache each shared corner once per chunk. Without this scratch grid,
        // four neighbouring cells repeatedly sample the same four heights.
        int smoothingLayer =
            _terrainLayerShader != null
                ? 0
                : visibleMaxLayer;

        for (int row = 0; row <= rowCount; row++)
        {
            int y = minTileY + row * lodStep;
            int offset = row * vertexStride;

            for (int column = 0; column <= columnCount; column++)
            {
                int x = minTileX + column * lodStep;

                _smoothedChunkHeights[offset + column] =
                    GetSmoothedSurfaceLayer(
                        worldMap,
                        x,
                        y,
                        smoothingLayer);
            }
        }

        for (int row = 0; row < rowCount; row++)
        {
            int y = minTileY + row * lodStep;
            int topOffset = row * vertexStride;
            int bottomOffset = (row + 1) * vertexStride;

            for (int column = 0; column < columnCount; column++)
            {
                int x = minTileX + column * lodStep;
                int surfaceLayer = worldMap.GetSurfaceLayer(x, y);

                if (surfaceLayer < 0)
                    continue;

                // The generated terrain fills each column from ground to its
                // surface. The CPU fallback still checks the exact selected layer.
                if (_terrainLayerShader == null &&
                    worldMap.GetMaterialId(x, y, visibleMaxLayer) == 0)
                {
                    continue;
                }

                float topLeftHeight =
                    _smoothedChunkHeights[topOffset + column];
                float topRightHeight =
                    _smoothedChunkHeights[topOffset + column + 1];
                float bottomRightHeight =
                    _smoothedChunkHeights[bottomOffset + column + 1];
                float bottomLeftHeight =
                    _smoothedChunkHeights[bottomOffset + column];

                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float right = (x + lodStep) * tilePixelSize;
                float bottom = (y + lodStep) * tilePixelSize;

                AppendTerrainQuad(
                    left,
                    top,
                    right,
                    bottom,
                    topLeftHeight,
                    topRightHeight,
                    bottomRightHeight,
                    bottomLeftHeight,
                    surfaceLayer);
            }
        }

        TerrainChunkMesh mesh = CreateTerrainChunkMesh(_buildingTerrainVertexCount);
        TerrainMeshRebuildCount++;
        return mesh;
    }

    private static float GetSmoothedSurfaceLayer(
        WorldMap worldMap,
        int vertexX,
        int vertexY,
        int visibleMaxLayer)
    {
        float heightSum = 0f;
        int sampleCount = 0;

        for (int y = vertexY - 1; y <= vertexY; y++)
        {
            for (int x = vertexX - 1; x <= vertexX; x++)
            {
                int surfaceLayer = worldMap.GetSurfaceLayer(x, y);

                if (surfaceLayer < visibleMaxLayer)
                    continue;

                heightSum += surfaceLayer;
                sampleCount++;
            }
        }

        return sampleCount > 0
            ? heightSum / sampleCount
            : 0f;
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
        float topLeftHeight,
        float topRightHeight,
        float bottomRightHeight,
        float bottomLeftHeight,
        int voxelLayer)
    {
        EnsureTerrainVertexCapacity(_buildingTerrainVertexCount + 6);

        Color topLeftColor = GetTerrainVertexColor(topLeftHeight);
        Color topRightColor = GetTerrainVertexColor(topRightHeight);
        Color bottomRightColor = GetTerrainVertexColor(bottomRightHeight);
        Color bottomLeftColor = GetTerrainVertexColor(bottomLeftHeight);

        Vector2f topLeft = new Vector2f(left, top);
        Vector2f topRight = new Vector2f(right, top);
        Vector2f bottomRight = new Vector2f(right, bottom);
        Vector2f bottomLeft = new Vector2f(left, bottom);

        Vector2f topLeftData = new Vector2f(topLeftHeight, voxelLayer);
        Vector2f topRightData = new Vector2f(topRightHeight, voxelLayer);
        Vector2f bottomRightData = new Vector2f(bottomRightHeight, voxelLayer);
        Vector2f bottomLeftData = new Vector2f(bottomLeftHeight, voxelLayer);

        _terrainVertices[_buildingTerrainVertexCount++] =
            new Vertex(topLeft, topLeftColor, topLeftData);
        _terrainVertices[_buildingTerrainVertexCount++] =
            new Vertex(topRight, topRightColor, topRightData);
        _terrainVertices[_buildingTerrainVertexCount++] =
            new Vertex(bottomRight, bottomRightColor, bottomRightData);
        _terrainVertices[_buildingTerrainVertexCount++] =
            new Vertex(topLeft, topLeftColor, topLeftData);
        _terrainVertices[_buildingTerrainVertexCount++] =
            new Vertex(bottomRight, bottomRightColor, bottomRightData);
        _terrainVertices[_buildingTerrainVertexCount++] =
            new Vertex(bottomLeft, bottomLeftColor, bottomLeftData);
    }

    private Color GetTerrainVertexColor(float surfaceLayer)
    {
        return _terrainLayerShader != null
            ? Color.White
            : TerrainHeightPalette.GetTerrainColor(
                surfaceLayer,
                _baseGray,
                _heightContrast);
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
            TerrainChunkKey? oldestKey = null;
            long oldestFrame = long.MaxValue;

            foreach (KeyValuePair<TerrainChunkKey, TerrainChunkMesh> entry in _terrainChunks)
            {
                int regionX = entry.Key.RegionIndex % worldMap.RegionsX;
                int regionY = entry.Key.RegionIndex / worldMap.RegionsX;

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

            if (!oldestKey.HasValue)
                break;

            TerrainChunkMesh mesh = _terrainChunks[oldestKey.Value];
            _cachedTerrainVertexCount -= mesh.VertexCount;
            mesh.Dispose();
            _terrainChunks.Remove(oldestKey.Value);
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
                    if (worldMap.Water.GetAmount(x, y, visibleMaxLayer) == 0)
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
        _smoothedChunkHeights = Array.Empty<float>();
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
        Color color = new Color(105, 105, 105, GridAlpha);

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
