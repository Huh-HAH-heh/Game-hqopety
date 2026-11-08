using System;
using System.Diagnostics;
using System.Collections.Generic;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class MapRenderSystem : IDisposable
{
    private readonly record struct TerrainChunkKey(int RegionIndex, int LodStep, int SliceLayer);

    private readonly record struct PendingTerrainChunk(
        int RegionX,
        int RegionY,
        int RegionIndex);

    private const int MaxTerrainChunksBuiltPerFrame = 4;

    // Retain nearby chunks for smooth camera movement without accumulating
    // every GPU mesh ever visited. The currently visible set is always retained.
    private const int MaxCachedTerrainChunks = 768;
    private const int TerrainChunkCacheReserve = 32;

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
    private readonly List<TerrainChunkMesh> _fallbackTerrainChunks = new();
    private readonly List<PendingTerrainChunk> _pendingTerrainChunks = new();
    private int _pendingCenterRegionX;
    private int _pendingCenterRegionY;

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
    private float _chunkCacheTilePixelSize = -1f;

    private const double ViewChangeSettleMilliseconds = 180d;
    private int _displayedMaxLayer = -1;
    private int _displayedLodStep = 1;
    private int _requestedMaxLayer = -1;
    private int _requestedLodStep = -1;
    private long _viewRequestChangedAt;

    public int DisplayedMaxLayer => Math.Max(0, _displayedMaxLayer);
    public int DisplayedLodStep => _displayedLodStep;


    private const string TerrainVertexShaderSource =
        @"void main()
{
    gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
    gl_TexCoord[0] = gl_MultiTexCoord0;
    gl_FrontColor = gl_Color;
}";

    private const string TerrainFragmentShaderSource =
        @"uniform float uBaseGray;
uniform float uHeightContrast;
void main()
{
    float surfaceLayer = gl_TexCoord[0].x;
    float heightFraction = clamp(surfaceLayer, 0.0, 499.0) / 499.0;
    float tonalFraction = pow(heightFraction, 1.8);
    float heightRange = clamp(uBaseGray + uHeightContrast * 1.5, 24.0, 72.0);
    float gray = tonalFraction * heightRange;

    gray = clamp(gray, 0.0, 72.0) / 255.0;
    gl_FragColor = vec4(gray, gray, gray, 1.0);
}";

    private readonly VertexArray _gridVertices =
        new VertexArray(PrimitiveType.Lines);
    private readonly VertexArray _cutSurfaceHatch = new VertexArray(PrimitiveType.Lines);
    private readonly VertexArray _cutVoidGrid = new VertexArray(PrimitiveType.Lines);
    private bool _sliceOverlayCacheValid;
    private int _sliceMinTileX, _sliceMaxTileX, _sliceMinTileY, _sliceMaxTileY;
    private int _sliceLayer = -1;
    private long _sliceTerrainVersion = long.MinValue;
    private float _sliceTilePixelSize = -1f;

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

    public void InvalidateGraphicsResources()
    {
        // The OS/GPU may invalidate window-owned OpenGL resources while the
        // window is minimized or the graphics context is reactivated. Never
        // keep stale VBO/shader handles after focus has returned.
        ClearTerrainChunkCache();

        _terrainLayerShader?.Dispose();
        _terrainLayerShader = null;
        _terrainShaderChecked = false;
        _terrainBufferSupportChecked = false;
        _useTerrainBuffer = false;

        _chunkCacheTerrainVersion = long.MinValue;
        _chunkCacheTilePixelSize = -1f;

        _mapCacheValid = false;
        _gridCacheValid = false;
        _waterCacheValid = false;
        _sliceOverlayCacheValid = false;
        _cutSurfaceHatch.Clear();
        _cutVoidGrid.Clear();

        LastTerrainBuildMilliseconds = 0d;
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

        int requestedMaxLayer =
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

        // Estimate visible cells before building geometry. Preserve full detail when zoomed
        // in, but reduce vertex work when zoomed out.
        double visibleCellEstimate =
            (double)viewSize.X / tilePixelSize * (viewSize.Y / tilePixelSize);
        int requestedLodStep = visibleCellEstimate >= 150_000d
            ? 3
            : visibleCellEstimate >= 45_000d
                ? 2
                : 1;

        // Terrain/viewport changes are settled before selecting a new slice or LOD.
        // During wheel input the old cached mesh remains on screen; only the final
        // requested state starts building after a short quiet period.
        if (_chunkCacheTerrainVersion != worldMap.TerrainVersion ||
            MathF.Abs(_chunkCacheTilePixelSize - tilePixelSize) > 0.001f)
        {
            ClearTerrainChunkCache();
            _chunkCacheTerrainVersion = worldMap.TerrainVersion;
            _chunkCacheTilePixelSize = tilePixelSize;
        }

        long now = Stopwatch.GetTimestamp();
        if (_requestedMaxLayer != requestedMaxLayer ||
            _requestedLodStep != requestedLodStep)
        {
            _requestedMaxLayer = requestedMaxLayer;
            _requestedLodStep = requestedLodStep;
            _viewRequestChangedAt = now;
        }

        bool viewChangeSettled =
            _displayedMaxLayer < 0 ||
            Stopwatch.GetElapsedTime(_viewRequestChangedAt, now).TotalMilliseconds >=
                ViewChangeSettleMilliseconds;
        int maxLayer = viewChangeSettled
            ? requestedMaxLayer
            : _displayedMaxLayer;
        int lodStep = viewChangeSettled
            ? requestedLodStep
            : _displayedLodStep;

        if (_displayedMaxLayer < 0)
        {
            _displayedMaxLayer = maxLayer;
            _displayedLodStep = lodStep;
        }

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

        UpdateSliceOverlays(
            worldMap, minTileX, maxTileX, minTileY, maxTileY,
            tilePixelSize, maxLayer);
        if (_cutSurfaceHatch.VertexCount > 0)
            window.Draw(_cutSurfaceHatch);
        if (_cutVoidGrid.VertexCount > 0)
            window.Draw(_cutVoidGrid);

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
        // Slice level is part of the chunk key; previously built Z layers remain reusable.
        int geometryMaxLayer = visibleMaxLayer;

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
        long rebuildsBefore = TerrainMeshRebuildCount;
        long started = Stopwatch.GetTimestamp();

        _pendingTerrainChunks.Clear();

        // Draw all cached chunks immediately, but never build the entire missing
        // viewport in one frame. Camera jumps can otherwise synchronously build
        // dozens or hundreds of meshes and cause visible frame-time spikes.
        for (int regionY = minRegionY;
             regionY <= maxRegionY;
             regionY++)
        {
            for (int regionX = minRegionX;
                 regionX <= maxRegionX;
                 regionX++)
            {
                int regionIndex = regionX + regionY * worldMap.RegionsX;
                TerrainChunkKey key = new TerrainChunkKey(regionIndex, lodStep, visibleMaxLayer);

                if (!_terrainChunks.TryGetValue(key, out TerrainChunkMesh? mesh))
                {
                    _pendingTerrainChunks.Add(
                        new PendingTerrainChunk(
                            regionX,
                            regionY,
                            regionIndex));
                    continue;
                }

                mesh.LastUsedFrame = _frameNumber;
                _visibleTerrainChunks.Add(mesh);
                _terrainVertexCount += mesh.VertexCount;
            }
        }

        // When the view is zoomed out, build around its center first so the
        // important area appears immediately while the remaining cache warms.
        _pendingCenterRegionX =
            ((minTileX + maxTileX) / 2) / TerrainRegion.TilesPerSide;
        _pendingCenterRegionY =
            ((minTileY + maxTileY) / 2) / TerrainRegion.TilesPerSide;
        _pendingTerrainChunks.Sort(ComparePendingTerrainChunks);

        int buildCount = Math.Min(
            MaxTerrainChunksBuiltPerFrame,
            _pendingTerrainChunks.Count);

        for (int i = 0; i < buildCount; i++)
        {
            PendingTerrainChunk pending = _pendingTerrainChunks[i];

            TerrainChunkMesh mesh = BuildTerrainChunk(
                worldMap,
                pending.RegionX,
                pending.RegionY,
                lodStep,
                tilePixelSize,
                geometryMaxLayer);

            _terrainChunks.Add(
                new TerrainChunkKey(pending.RegionIndex, lodStep, visibleMaxLayer),
                mesh);
            _cachedTerrainVertexCount += mesh.VertexCount;

            mesh.LastUsedFrame = _frameNumber;
            _visibleTerrainChunks.Add(mesh);
            _terrainVertexCount += mesh.VertexCount;
        }

        bool targetLayerReady = _pendingTerrainChunks.Count <= buildCount;
        if (targetLayerReady)
        {
            _displayedMaxLayer = visibleMaxLayer;
            _displayedLodStep = lodStep;
        }
        else if (_displayedMaxLayer != visibleMaxLayer ||
                 _displayedLodStep != lodStep)
        {
            _fallbackTerrainChunks.Clear();
            _terrainVertexCount = 0;
            for (int regionY = minRegionY; regionY <= maxRegionY; regionY++)
            {
                for (int regionX = minRegionX; regionX <= maxRegionX; regionX++)
                {
                    int regionIndex = regionX + regionY * worldMap.RegionsX;
                    TerrainChunkKey fallbackKey = new(
                        regionIndex, _displayedLodStep, _displayedMaxLayer);
                    if (!_terrainChunks.TryGetValue(fallbackKey, out TerrainChunkMesh? fallback))
                        continue;
                    fallback.LastUsedFrame = _frameNumber;
                    _fallbackTerrainChunks.Add(fallback);
                    _terrainVertexCount += fallback.VertexCount;
                }
            }

            // If a previous complete layer is still cached, display it while the
            // requested layer warms up. Otherwise use the partial target to avoid blank terrain.
            if (_fallbackTerrainChunks.Count > 0)
            {
                _visibleTerrainChunks.Clear();
                _visibleTerrainChunks.AddRange(_fallbackTerrainChunks);
            }
            else
            {
                _displayedMaxLayer = visibleMaxLayer;
                _displayedLodStep = lodStep;
            }
        }

        RenderStates terrainStates = RenderStates.Default;

        if (_terrainLayerShader != null)
        {
            _terrainLayerShader.SetUniform("uBaseGray", _baseGray);
            _terrainLayerShader.SetUniform("uHeightContrast", _heightContrast);
            terrainStates = new RenderStates(_terrainLayerShader);
        }

        for (int i = 0; i < _visibleTerrainChunks.Count; i++)
            _visibleTerrainChunks[i].Draw(window, terrainStates);

        EvictExcessTerrainChunks();

        if (TerrainMeshRebuildCount != rebuildsBefore)
        {
            LastTerrainBuildMilliseconds =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // Keep generated chunk meshes for the lifetime of this terrain version.
        // Zooming away and back therefore reuses existing GPU buffers.
    }

    private void EvictExcessTerrainChunks()
    {
        // Never evict a chunk needed by this frame. A small reserve keeps nearby
        // camera pans cache-hot while stale native VertexBuffers are disposed.
        int maxRetained = Math.Max(
            MaxCachedTerrainChunks,
            _visibleTerrainChunks.Count + TerrainChunkCacheReserve);

        while (_terrainChunks.Count > maxRetained)
        {
            TerrainChunkKey oldestKey = default;
            TerrainChunkMesh? oldestMesh = null;

            foreach (KeyValuePair<TerrainChunkKey, TerrainChunkMesh> entry in _terrainChunks)
            {
                if (oldestMesh != null &&
                    entry.Value.LastUsedFrame >= oldestMesh.LastUsedFrame)
                {
                    continue;
                }

                oldestKey = entry.Key;
                oldestMesh = entry.Value;
            }

            if (oldestMesh == null)
                return;

            _terrainChunks.Remove(oldestKey);
            _cachedTerrainVertexCount -= oldestMesh.VertexCount;
            oldestMesh.Dispose();
        }
    }

    private int ComparePendingTerrainChunks(
        PendingTerrainChunk left,
        PendingTerrainChunk right)
    {
        int leftX = left.RegionX - _pendingCenterRegionX;
        int leftY = left.RegionY - _pendingCenterRegionY;
        int rightX = right.RegionX - _pendingCenterRegionX;
        int rightY = right.RegionY - _pendingCenterRegionY;

        int leftDistance = leftX * leftX + leftY * leftY;
        int rightDistance = rightX * rightX + rightY * rightY;

        int distanceOrder = leftDistance.CompareTo(rightDistance);
        return distanceOrder != 0
            ? distanceOrder
            : left.RegionIndex.CompareTo(right.RegionIndex);
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
        int smoothingLayer = visibleMaxLayer;

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
                int surfaceLayer =
                    FindVisibleTopLayer(worldMap, x, y, visibleMaxLayer);
                if (surfaceLayer < 0)
                    continue;

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
                int surfaceLayer =
                    FindVisibleTopLayer(worldMap, x, y, visibleMaxLayer);
                if (surfaceLayer < 0)
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

    private void ClearTerrainChunkCache()
    {
        foreach (TerrainChunkMesh mesh in _terrainChunks.Values)
            mesh.Dispose();

        _terrainChunks.Clear();
        _visibleTerrainChunks.Clear();
        _fallbackTerrainChunks.Clear();
        _pendingTerrainChunks.Clear();
        _displayedMaxLayer = -1;
        _displayedLodStep = 1;
        _requestedMaxLayer = -1;
        _requestedLodStep = -1;
        _viewRequestChangedAt = 0;
        _sliceOverlayCacheValid = false;
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
        return worldMap.FindTopOccupiedLayer(x, y, visibleMaxLayer);
    }

    private void UpdateSliceOverlays(
        WorldMap worldMap,
        int minTileX, int maxTileX, int minTileY, int maxTileY,
        float tilePixelSize, int cutLayer)
    {
        if (_sliceOverlayCacheValid &&
            _sliceMinTileX == minTileX && _sliceMaxTileX == maxTileX &&
            _sliceMinTileY == minTileY && _sliceMaxTileY == maxTileY &&
            _sliceLayer == cutLayer &&
            _sliceTerrainVersion == worldMap.TerrainVersion &&
            MathF.Abs(_sliceTilePixelSize - tilePixelSize) < 0.001f)
            return;

        _cutSurfaceHatch.Clear();
        _cutVoidGrid.Clear();
        Color cutColor = new Color(27, 35, 42, 190);
        Color voidColor = new Color(122, 163, 179, 58);

        for (int y = minTileY; y <= maxTileY; y++)
        {
            for (int x = minTileX; x <= maxTileX; x++)
            {
                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float right = left + tilePixelSize;
                float bottom = top + tilePixelSize;

                if (worldMap.GetMaterialId(x, y, cutLayer) != 0)
                {
                    AppendHatchedCell(_cutSurfaceHatch, left, top, right, bottom, cutColor);
                    continue;
                }

                int floorLayer = worldMap.FindTopOccupiedLayer(x, y, cutLayer - 1);
                if (floorLayer >= 0 &&
                    worldMap.GetSurfaceLayer(x, y) > cutLayer)
                {
                    AppendCellGrid(_cutVoidGrid, left, top, right, bottom, voidColor);
                }
            }
        }

        _sliceMinTileX = minTileX;
        _sliceMaxTileX = maxTileX;
        _sliceMinTileY = minTileY;
        _sliceMaxTileY = maxTileY;
        _sliceLayer = cutLayer;
        _sliceTerrainVersion = worldMap.TerrainVersion;
        _sliceTilePixelSize = tilePixelSize;
        _sliceOverlayCacheValid = true;
    }

    private static void AppendHatchedCell(
        VertexArray vertices, float left, float top, float right, float bottom, Color color)
    {
        float width = right - left;
        float height = bottom - top;
        float spacing = MathF.Max(3f, MathF.Min(width, height) * 0.27f);
        for (float c = spacing; c < width + height; c += spacing)
        {
            int count = 0;
            float x1 = 0f, y1 = 0f, x2 = 0f, y2 = 0f;
            if (c <= height) AddHatchPoint(left, top + c, ref count, ref x1, ref y1, ref x2, ref y2);
            if (c <= width) AddHatchPoint(left + c, top, ref count, ref x1, ref y1, ref x2, ref y2);
            if (c >= width && c <= width + height) AddHatchPoint(right, top + c - width, ref count, ref x1, ref y1, ref x2, ref y2);
            if (c >= height && c <= width + height) AddHatchPoint(left + c - height, bottom, ref count, ref x1, ref y1, ref x2, ref y2);
            if (count >= 2) AppendLine(vertices, x1, y1, x2, y2, color);
        }
        AppendCellGrid(vertices, left, top, right, bottom,
            new Color(color.R, color.G, color.B, 220));
    }

    private static void AddHatchPoint(
        float x, float y, ref int count,
        ref float x1, ref float y1, ref float x2, ref float y2)
    {
        if (count > 0 &&
            (MathF.Abs(x - x1) < 0.01f && MathF.Abs(y - y1) < 0.01f ||
             count > 1 && MathF.Abs(x - x2) < 0.01f && MathF.Abs(y - y2) < 0.01f))
            return;
        if (count == 0)
        {
            x1 = x; y1 = y; count = 1;
        }
        else if (count == 1)
        {
            x2 = x; y2 = y; count = 2;
        }
    }

    private static void AppendCellGrid(
        VertexArray vertices, float left, float top, float right, float bottom, Color color)
    {
        AppendLine(vertices, left, top, right, top, color);
        AppendLine(vertices, right, top, right, bottom, color);
        AppendLine(vertices, right, bottom, left, bottom, color);
        AppendLine(vertices, left, bottom, left, top, color);
    }

    private static void AppendLine(
        VertexArray vertices, float x1, float y1, float x2, float y2, Color color)
    {
        vertices.Append(new Vertex(new Vector2f(x1, y1), color));
        vertices.Append(new Vertex(new Vector2f(x2, y2), color));
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
