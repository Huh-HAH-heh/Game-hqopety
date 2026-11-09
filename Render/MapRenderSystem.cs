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

    private const int MaxTerrainChunksBuiltPerFrame = 16;

    // Retain nearby chunks for smooth camera movement without accumulating
    // every GPU mesh ever visited. The currently visible set is always retained.
    private const int MaxCachedTerrainChunks = 512;
    private const int TerrainChunkCacheReserve = 32;

    private sealed class TerrainChunkMesh : IDisposable
    {
        public VertexBuffer? Buffer;
        public VertexArray? Vertices;
        public int VertexCount;
        public long LastUsedFrame;
        public long RegionTerrainVersion;
        public VertexArray? CutHatch;
        public VertexArray? VoidHatch;
        public VertexArray? CutContour;

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
            CutHatch?.Dispose();
            CutHatch = null;
            VoidHatch?.Dispose();
            VoidHatch = null;
            CutContour?.Dispose();
            CutContour = null;
        }
    }

    // Camera movement rebuilds only chunks entering the view.
    private readonly Dictionary<TerrainChunkKey, TerrainChunkMesh> _terrainChunks = new();
    private readonly List<TerrainChunkMesh> _visibleTerrainChunks = new();
    private readonly List<TerrainChunkMesh> _fallbackTerrainChunks = new();
    private readonly List<PendingTerrainChunk> _pendingTerrainChunks = new();
    private readonly List<TerrainChunkKey> _staleTerrainChunkKeys = new();
    private readonly VertexArray _terrainPreviewVertices =
        new VertexArray(PrimitiveType.Triangles);
    private bool _terrainPreviewValid;
    private int _previewMinTileX;
    private int _previewMaxTileX;
    private int _previewMinTileY;
    private int _previewMaxTileY;
    private int _previewLodStep;
    private int _previewMaxLayer = -1;
    private long _previewTerrainGeometryVersion = long.MinValue;
    private float _previewTilePixelSize = -1f;
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
    private long _highestLayerCacheVersion = long.MinValue;
    private int _highestOccupiedLayerCache = -1;

    private int _displayedMaxLayer = -1;
    private int _displayedLodStep = 1;
    private int _stableMaxLayer = -1;
    private int _stableLodStep = 1;
    private int _fallbackMaxLayer = -1;
    private int _fallbackLodStep = 1;
    private int _transitionTargetMaxLayer = -1;
    private int _transitionTargetLodStep = -1;

    public int DisplayedMaxLayer => Math.Max(0, _displayedMaxLayer);
    public int DisplayedLodStep => _displayedLodStep;
    public int PendingTerrainChunkCount => _pendingTerrainChunks.Count;
    public int TerrainChunksBuiltLastFrame { get; private set; }


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
    private readonly VertexArray _heightStepVertices =
        new VertexArray(PrimitiveType.Lines);
    private readonly VertexArray _blockTransitionVertices =
        new VertexArray(PrimitiveType.Lines);
    private readonly VertexArray _waterVertices =
        new VertexArray(PrimitiveType.Triangles);

    private float[] _smoothedChunkHeights = Array.Empty<float>();
    private int[] _chunkSurfaceLayers = Array.Empty<int>();

    private bool _mapCacheValid;
    private bool _gridCacheValid;
    private bool _cachedShowGrid;
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

        // Cliff faces are CPU-shaded even when the top surface uses a shader.
        // Rebuild them when the grayscale range changes.
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

        LastTerrainBuildMilliseconds = 0d;
    }

    public bool UsesTerrainVertexBuffer =>
        _useTerrainBuffer;

    public long TerrainMeshRebuildCount { get; private set; }

    public double LastTerrainBuildMilliseconds { get; private set; }

    private const byte GridAlpha = 52;
    private const int HeightStepLineThresholdUnits = 5;

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
        GameCamera.GetVisibleWorldHalfExtents(
            cameraView,
            out float halfViewX,
            out float halfViewY);

        float screenMinX = center.X - halfViewX;
        float screenMaxX = center.X + halfViewX;
        float screenMinY = center.Y - halfViewY;
        float screenMaxY = center.Y + halfViewY;

        // Estimate visible cells before building geometry. Preserve full detail when zoomed
        // in, but reduce vertex work when zoomed out.
        double visibleCellEstimate =
            (double)viewSize.X / tilePixelSize * (viewSize.Y / tilePixelSize);
        int requestedLodStep = visibleCellEstimate >= 150_000d
            ? 3
            : visibleCellEstimate >= 45_000d
                ? 2
                : 1;

        // Terrain edits invalidate only the touched XY region. A nav flag or
        // a change in another region must not flush all GPU terrain meshes.
        if (MathF.Abs(_chunkCacheTilePixelSize - tilePixelSize) > 0.001f)
        {
            ClearTerrainChunkCache();
            _chunkCacheTilePixelSize = tilePixelSize;
        }
        _chunkCacheTerrainVersion = worldMap.TerrainVersion;

        // Select the requested Z slice and LOD immediately.
        int maxLayer = requestedMaxLayer;
        int lodStep = requestedLodStep;

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

        // Discrete ramps are rendered as sloped block surfaces in the terrain
        // mesh. The old arrow/line overlay was only a debug marker.
        if (!worldMap.UsesDiscreteBlockTerrain)
        {
            DrawBlockTransitions(
                window,
                worldMap,
                minTileX,
                maxTileX,
                minTileY,
                maxTileY,
                tilePixelSize,
                DisplayedMaxLayer);
        }

        // Auxiliary terrain layers must follow the mesh that is actually visible.
        maxLayer = DisplayedMaxLayer;
        lodStep = DisplayedLodStep;

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
            _cachedTerrainVersion == worldMap.TerrainVersion &&
            _cachedShowGrid == showGrid;

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

        if (!gridCacheMatches)
        {
            _gridVertices.Clear();
            _heightStepVertices.Clear();

            for (int y = minTileY; y <= maxTileY; y += lodStep)
            {
                for (int x = minTileX; x <= maxTileX; x += lodStep)
                {
                    int topLayer = showGrid
                        ? FindVisibleTopLayer(worldMap, x, y, maxLayer)
                        : -1;

                    float left = x * tilePixelSize;
                    float top = y * tilePixelSize;
                    float right = Math.Min(worldMap.TileWidth, x + lodStep) * tilePixelSize;
                    float bottom = Math.Min(worldMap.TileHeight, y + lodStep) * tilePixelSize;

                    if (showGrid && topLayer >= 0)
                    {
                        AppendGrid(
                            _gridVertices,
                            left,
                            top,
                            right,
                            bottom);
                    }

                    AppendHeightStepEdges(
                        _heightStepVertices,
                        worldMap,
                        x,
                        y,
                        lodStep,
                        tilePixelSize,
                        maxLayer);
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
            _cachedShowGrid = showGrid;
        }

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

        if (_heightStepVertices.VertexCount > 0)
            window.Draw(_heightStepVertices);
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
        TerrainChunksBuiltLastFrame = 0;
        if (_transitionTargetMaxLayer != visibleMaxLayer ||
            _transitionTargetLodStep != lodStep)
        {
            _transitionTargetMaxLayer = visibleMaxLayer;
            _transitionTargetLodStep = lodStep;
            _fallbackMaxLayer = _stableMaxLayer;
            _fallbackLodStep = _stableLodStep;
        }

        _visibleTerrainChunks.Clear();
        _fallbackTerrainChunks.Clear();
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

                long regionVersion =
                    worldMap.GetRegionTerrainVersion(regionX, regionY);

                if (!_terrainChunks.TryGetValue(key, out TerrainChunkMesh? mesh) ||
                    mesh.RegionTerrainVersion != regionVersion)
                {
                    if (mesh != null)
                        InvalidateTerrainRegionChunks(regionIndex);

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

        bool previewMatches =
            _terrainPreviewValid &&
            _previewMinTileX == minTileX &&
            _previewMaxTileX == maxTileX &&
            _previewMinTileY == minTileY &&
            _previewMaxTileY == maxTileY &&
            _previewLodStep == lodStep &&
            _previewMaxLayer == visibleMaxLayer &&
            _previewTerrainGeometryVersion == worldMap.TerrainGeometryVersion &&
            MathF.Abs(_previewTilePixelSize - tilePixelSize) <= 0.001f;

        bool previewRebuilt =
            _pendingTerrainChunks.Count > 0 && !previewMatches;

        if (previewRebuilt)
        {
            BuildTerrainPreview(
                worldMap,
                minTileX, maxTileX,
                minTileY, maxTileY,
                lodStep, tilePixelSize, visibleMaxLayer);
        }

        int buildLimit = previewRebuilt
            ? 2
            : MaxTerrainChunksBuiltPerFrame;
        int maximumBuilds = Math.Min(
            buildLimit,
            _pendingTerrainChunks.Count);
        bool buildSliceOverlay =
            _pendingTerrainChunks.Count > 0 &&
            visibleMaxLayer <= GetCachedHighestOccupiedLayer(worldMap);
        int buildCount = 0;
        long buildStarted = Stopwatch.GetTimestamp();

        for (int i = 0; i < maximumBuilds; i++)
        {
            PendingTerrainChunk pending = _pendingTerrainChunks[i];
            TerrainChunkMesh mesh = BuildTerrainChunk(
                worldMap,
                pending.RegionX,
                pending.RegionY,
                lodStep,
                tilePixelSize,
                geometryMaxLayer,
                buildSliceOverlay);

            _terrainChunks.Add(
                new TerrainChunkKey(pending.RegionIndex, lodStep, visibleMaxLayer),
                mesh);
            _cachedTerrainVertexCount += mesh.VertexCount;
            mesh.LastUsedFrame = _frameNumber;
            _visibleTerrainChunks.Add(mesh);
            _terrainVertexCount += mesh.VertexCount;
            buildCount++;

            if (Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds >= 4.0)
                break;
        }

        // Built target chunks are displayed immediately; only missing regions
        // borrow a matching chunk from the previous layer/LOD.
        TerrainChunksBuiltLastFrame = buildCount;
        if (buildCount > 0)
            _pendingTerrainChunks.RemoveRange(0, buildCount);

        for (int i = 0; i < _pendingTerrainChunks.Count; i++)
        {
            PendingTerrainChunk pending = _pendingTerrainChunks[i];
            int regionIndex = pending.RegionIndex;
            TerrainChunkMesh? fallback = null;

            if (_fallbackMaxLayer < 0 ||
                (!_terrainChunks.TryGetValue(
                    new TerrainChunkKey(regionIndex, lodStep, _fallbackMaxLayer),
                    out fallback) &&
                 !_terrainChunks.TryGetValue(
                    new TerrainChunkKey(regionIndex, _fallbackLodStep, _fallbackMaxLayer),
                    out fallback)))
                continue;

            long regionVersion =
                worldMap.GetRegionTerrainVersion(pending.RegionX, pending.RegionY);
            if (fallback.RegionTerrainVersion != regionVersion)
            {
                InvalidateTerrainRegionChunks(regionIndex);
                continue;
            }

            fallback.LastUsedFrame = _frameNumber;
            _fallbackTerrainChunks.Add(fallback);
        }

        _visibleTerrainChunks.AddRange(_fallbackTerrainChunks);
        for (int i = 0; i < _fallbackTerrainChunks.Count; i++)
            _terrainVertexCount += _fallbackTerrainChunks[i].VertexCount;

        _displayedMaxLayer = visibleMaxLayer;
        _displayedLodStep = lodStep;
        if (_pendingTerrainChunks.Count == 0)
        {
            _stableMaxLayer = visibleMaxLayer;
            _stableLodStep = lodStep;
            _fallbackMaxLayer = -1;
            _transitionTargetMaxLayer = -1;
            _transitionTargetLodStep = -1;
        }

        RenderStates terrainStates = RenderStates.Default;

        if (_terrainLayerShader != null)
        {
            _terrainLayerShader.SetUniform("uBaseGray", _baseGray);
            _terrainLayerShader.SetUniform("uHeightContrast", _heightContrast);
            terrainStates = new RenderStates(_terrainLayerShader);
        }

        if (_pendingTerrainChunks.Count > 0 && _terrainPreviewValid)
        {
            window.Draw(_terrainPreviewVertices, terrainStates);
        }

        for (int i = 0; i < _visibleTerrainChunks.Count; i++)
            _visibleTerrainChunks[i].Draw(window, terrainStates);

        for (int i = 0; i < _visibleTerrainChunks.Count; i++)
        {
            TerrainChunkMesh mesh = _visibleTerrainChunks[i];
            if (mesh.CutContour?.VertexCount > 0)
                window.Draw(mesh.CutContour);
            if (mesh.CutHatch?.VertexCount > 0)
                window.Draw(mesh.CutHatch);
            if (mesh.VoidHatch?.VertexCount > 0)
                window.Draw(mesh.VoidHatch);
        }

        EvictExcessTerrainChunks();

        if (TerrainMeshRebuildCount != rebuildsBefore)
        {
            LastTerrainBuildMilliseconds =
                Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        // Keep generated chunk meshes for the lifetime of this terrain version.
        // Zooming away and back therefore reuses existing GPU buffers.
    }

    private int GetCachedHighestOccupiedLayer(WorldMap worldMap)
    {
        if (_highestLayerCacheVersion != worldMap.TerrainGeometryVersion)
        {
            _highestOccupiedLayerCache = worldMap.GetHighestOccupiedLayer();
            _highestLayerCacheVersion = worldMap.TerrainGeometryVersion;
        }

        return _highestOccupiedLayerCache;
    }

    private void InvalidateTerrainRegionChunks(int regionIndex)
    {
        _staleTerrainChunkKeys.Clear();

        foreach (KeyValuePair<TerrainChunkKey, TerrainChunkMesh> entry in _terrainChunks)
        {
            if (entry.Key.RegionIndex == regionIndex)
                _staleTerrainChunkKeys.Add(entry.Key);
        }

        for (int i = 0; i < _staleTerrainChunkKeys.Count; i++)
        {
            TerrainChunkKey key = _staleTerrainChunkKeys[i];
            if (!_terrainChunks.TryGetValue(key, out TerrainChunkMesh? mesh))
                continue;

            _terrainChunks.Remove(key);
            _cachedTerrainVertexCount -= mesh.VertexCount;
            mesh.Dispose();
        }
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
        int visibleMaxLayer,
        bool buildSliceOverlay)
    {
        if (worldMap.UsesDiscreteBlockTerrain)
        {
            return BuildDiscreteBlockTerrainChunk(
                worldMap,
                regionX,
                regionY,
                lodStep,
                tilePixelSize,
                visibleMaxLayer,
                buildSliceOverlay);
        }

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
        if (_chunkSurfaceLayers.Length < requiredSmoothCapacity)
            Array.Resize(ref _chunkSurfaceLayers, requiredSmoothCapacity);

        // Read each source column once. The old path fetched the same four
        // neighbours for every shared corner and queried each cell again.
        for (int row = 0; row <= rowCount + 1; row++)
        {
            int y = minTileY + (row - 1) * lodStep;
            int offset = row * vertexStride;

            for (int column = 0; column <= columnCount + 1; column++)
            {
                int x = minTileX + (column - 1) * lodStep;
                _chunkSurfaceLayers[offset + column] =
                    FindVisibleTopLayer(worldMap, x, y, visibleMaxLayer);
            }
        }

        for (int row = 0; row <= rowCount; row++)
        {
            int offset = row * vertexStride;
            for (int column = 0; column <= columnCount; column++)
            {
                _smoothedChunkHeights[offset + column] =
                    GetSmoothedSurfaceLayer(
                        _chunkSurfaceLayers,
                        vertexStride,
                        row,
                        column);
            }
        }

        for (int row = 0; row < rowCount; row++)
        {
            int y = minTileY + row * lodStep;
            int sampleOffset = (row + 1) * vertexStride;
            int topOffset = row * vertexStride;
            int bottomOffset = (row + 1) * vertexStride;

            for (int column = 0; column < columnCount; column++)
            {
                int x = minTileX + column * lodStep;
                int surfaceLayer =
                    _chunkSurfaceLayers[sampleOffset + column + 1];
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
                float right = Math.Min(worldMap.TileWidth, x + lodStep) * tilePixelSize;
                float bottom = Math.Min(worldMap.TileHeight, y + lodStep) * tilePixelSize;

                AppendTerrainQuad(
                    left, top, right, bottom,
                    topLeftHeight, topRightHeight,
                    bottomRightHeight, bottomLeftHeight,
                    surfaceLayer);

            }
        }

        TerrainChunkMesh mesh = CreateTerrainChunkMesh(_buildingTerrainVertexCount);
        mesh.RegionTerrainVersion =
            worldMap.GetRegionTerrainVersion(regionX, regionY);
        if (buildSliceOverlay)
        {
            BuildChunkSliceOverlay(
                mesh, worldMap,
                minTileX, maxTileX, minTileY, maxTileY,
                lodStep, tilePixelSize, visibleMaxLayer);
        }

        TerrainMeshRebuildCount++;
        return mesh;
    }

    private TerrainChunkMesh BuildDiscreteBlockTerrainChunk(
        WorldMap worldMap,
        int regionX,
        int regionY,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer,
        bool buildSliceOverlay)
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

        // Pass 1: draw one hard, flat top per occupied block column.
        // Ramps use different corner heights so their surface is visibly sloped.
        for (int row = 0; row < rowCount; row++)
        {
            int y = minTileY + row * lodStep;
            for (int column = 0; column < columnCount; column++)
            {
                int x = minTileX + column * lodStep;
                int surfaceLayer = FindVisibleTopLayer(
                    worldMap, x, y, visibleMaxLayer);
                if (surfaceLayer < 0)
                    continue;

                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float right = Math.Min(
                    worldMap.TileWidth, x + lodStep) * tilePixelSize;
                float bottom = Math.Min(
                    worldMap.TileHeight, y + lodStep) * tilePixelSize;

                float high = surfaceLayer + 1f;
                float low = Math.Max(
                    0f,
                    high - WorldMap.HeightUnitsPerMeter);
                float topLeft = high;
                float topRight = high;
                float bottomRight = high;
                float bottomLeft = high;

                if (worldMap.Blocks.GetTransitionType(x, y) ==
                    BlockTransitionType.Ramp)
                {
                    switch (worldMap.Blocks.GetTransitionDirection(x, y))
                    {
                        case BlockDirection.North:
                            bottomLeft = low;
                            bottomRight = low;
                            break;
                        case BlockDirection.East:
                            topLeft = low;
                            bottomLeft = low;
                            break;
                        case BlockDirection.South:
                            topLeft = low;
                            topRight = low;
                            break;
                        case BlockDirection.West:
                            topRight = low;
                            bottomRight = low;
                            break;
                    }
                }

                AppendTerrainQuad(
                    left, top, right, bottom,
                    topLeft, topRight, bottomRight, bottomLeft,
                    surfaceLayer);
            }
        }

        // Pass 2: add filled side faces where one block column rises above
        // its neighbour. These are faces, not contour/edge outlines.
        for (int row = 0; row < rowCount; row++)
        {
            int y = minTileY + row * lodStep;
            for (int column = 0; column < columnCount; column++)
            {
                int x = minTileX + column * lodStep;
                int surfaceLayer = FindVisibleTopLayer(
                    worldMap, x, y, visibleMaxLayer);
                if (surfaceLayer < 0)
                    continue;

                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float right = Math.Min(
                    worldMap.TileWidth, x + lodStep) * tilePixelSize;
                float bottom = Math.Min(
                    worldMap.TileHeight, y + lodStep) * tilePixelSize;

                for (int directionIndex = 0; directionIndex < 4; directionIndex++)
                {
                    BlockDirection direction = (BlockDirection)directionIndex;
                    GetBlockDirectionDelta(direction, out int dx, out int dy);
                    int neighbourX = x + dx * lodStep;
                    int neighbourY = y + dy * lodStep;
                    int neighbourLayer = FindVisibleTopLayer(
                        worldMap, neighbourX, neighbourY, visibleMaxLayer);

                    if (neighbourLayer >= surfaceLayer)
                        continue;

                    // A ramp joins these two surfaces; do not draw a vertical
                    // wall over its upper edge.
                    BlockTransitionType neighbourTransition =
                        worldMap.Blocks.GetTransitionType(neighbourX, neighbourY);
                    BlockDirection reverseDirection =
                        (BlockDirection)(((int)direction + 2) & 3);
                    if (neighbourTransition == BlockTransitionType.Ramp &&
                        worldMap.Blocks.GetTransitionDirection(
                            neighbourX, neighbourY) == reverseDirection)
                    {
                        continue;
                    }

                    float differenceMeters =
                        (surfaceLayer - neighbourLayer) /
                        (float)WorldMap.HeightUnitsPerMeter;
                    float faceDepth = tilePixelSize * Math.Clamp(
                        0.10f + differenceMeters * 0.08f,
                        0.10f,
                        0.42f);

                    float faceLeft = left;
                    float faceTop = top;
                    float faceRight = right;
                    float faceBottom = bottom;
                    switch (direction)
                    {
                        case BlockDirection.North:
                            faceBottom = Math.Min(bottom, top + faceDepth);
                            break;
                        case BlockDirection.East:
                            faceLeft = Math.Max(left, right - faceDepth);
                            break;
                        case BlockDirection.South:
                            faceTop = Math.Max(top, bottom - faceDepth);
                            break;
                        case BlockDirection.West:
                            faceRight = Math.Min(right, left + faceDepth);
                            break;
                    }

                    int faceLayer = Math.Max(
                        0,
                        surfaceLayer - Math.Min(
                            surfaceLayer,
                            WorldMap.HeightUnitsPerMeter));
                    AppendTerrainQuad(
                        faceLeft, faceTop, faceRight, faceBottom,
                        faceLayer, faceLayer, faceLayer, faceLayer,
                        faceLayer);
                }
            }
        }

        TerrainChunkMesh mesh = CreateTerrainChunkMesh(_buildingTerrainVertexCount);
        mesh.RegionTerrainVersion =
            worldMap.GetRegionTerrainVersion(regionX, regionY);

        if (buildSliceOverlay)
        {
            BuildChunkSliceOverlay(
                mesh,
                worldMap,
                minTileX,
                maxTileX,
                minTileY,
                maxTileY,
                lodStep,
                tilePixelSize,
                visibleMaxLayer);
        }

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
                int layer = FindVisibleTopLayer(worldMap, x, y, visibleMaxLayer);
                if (layer < 0)
                    continue;

                heightSum += layer;
                sampleCount++;
            }
        }

        return sampleCount > 0 ? heightSum / sampleCount : 0f;
    }

    private static float GetSmoothedSurfaceLayer(
        int[] samples,
        int stride,
        int row,
        int column)
    {
        int offset = row * stride + column;
        float heightSum = 0f;
        int sampleCount = 0;

        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                int layer = samples[offset + i * stride + j];
                if (layer < 0)
                    continue;

                heightSum += layer;
                sampleCount++;
            }
        }

        return sampleCount > 0 ? heightSum / sampleCount : 0f;
    }

    private void BuildTerrainPreview(
        WorldMap worldMap,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        _terrainPreviewVertices.Clear();

        int previewStep = Math.Clamp(lodStep * 2, 2, 6);
        long viewportCells =
            (long)(maxTileX - minTileX + 1) *
            (maxTileY - minTileY + 1);
        while (previewStep < 32 &&
               viewportCells / ((long)previewStep * previewStep) > 6000)
        {
            previewStep *= 2;
        }

        for (int y = minTileY; y <= maxTileY; y += previewStep)
        {
            for (int x = minTileX; x <= maxTileX; x += previewStep)
            {
                int sampleX = Math.Min(worldMap.MaxTileX, x + previewStep / 2);
                int sampleY = Math.Min(worldMap.MaxTileY, y + previewStep / 2);
                int surfaceLayer =
                    FindVisibleTopLayer(worldMap, sampleX, sampleY, visibleMaxLayer);
                if (surfaceLayer < 0)
                    continue;

                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float right = Math.Min(maxTileX + 1, x + previewStep) * tilePixelSize;
                float bottom = Math.Min(maxTileY + 1, y + previewStep) * tilePixelSize;

                AppendPreviewQuad(
                    _terrainPreviewVertices,
                    left, top, right, bottom,
                    Math.Max(0, FindVisibleTopLayer(worldMap, x, y, visibleMaxLayer)),
                    Math.Max(0, FindVisibleTopLayer(worldMap, x + previewStep, y, visibleMaxLayer)),
                    Math.Max(0, FindVisibleTopLayer(worldMap, x + previewStep, y + previewStep, visibleMaxLayer)),
                    Math.Max(0, FindVisibleTopLayer(worldMap, x, y + previewStep, visibleMaxLayer)),
                    surfaceLayer);

            }
        }

        _terrainPreviewValid = true;
        _previewMinTileX = minTileX;
        _previewMaxTileX = maxTileX;
        _previewMinTileY = minTileY;
        _previewMaxTileY = maxTileY;
        _previewLodStep = lodStep;
        _previewMaxLayer = visibleMaxLayer;
        _previewTerrainGeometryVersion = worldMap.TerrainGeometryVersion;
        _previewTilePixelSize = tilePixelSize;
    }

    private void AppendPreviewQuad(
        VertexArray vertices,
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
        Color tlColor = GetTerrainVertexColor(topLeftHeight);
        Color trColor = GetTerrainVertexColor(topRightHeight);
        Color brColor = GetTerrainVertexColor(bottomRightHeight);
        Color blColor = GetTerrainVertexColor(bottomLeftHeight);

        Vector2f tlData = new(topLeftHeight, voxelLayer);
        Vector2f trData = new(topRightHeight, voxelLayer);
        Vector2f brData = new(bottomRightHeight, voxelLayer);
        Vector2f blData = new(bottomLeftHeight, voxelLayer);

        Vector2f tl = new(left, top);
        Vector2f tr = new(right, top);
        Vector2f br = new(right, bottom);
        Vector2f bl = new(left, bottom);

        vertices.Append(new Vertex(tl, tlColor, tlData));
        vertices.Append(new Vertex(tr, trColor, trData));
        vertices.Append(new Vertex(br, brColor, brData));
        vertices.Append(new Vertex(tl, tlColor, tlData));
        vertices.Append(new Vertex(br, brColor, brData));
        vertices.Append(new Vertex(bl, blColor, blData));
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
        _terrainPreviewValid = false;
        _terrainPreviewVertices.Clear();
        _highestLayerCacheVersion = long.MinValue;

        foreach (TerrainChunkMesh mesh in _terrainChunks.Values)
            mesh.Dispose();

        _terrainChunks.Clear();
        _visibleTerrainChunks.Clear();
        _fallbackTerrainChunks.Clear();
        _pendingTerrainChunks.Clear();
        _displayedMaxLayer = -1;
        _displayedLodStep = 1;
        _stableMaxLayer = -1;
        _stableLodStep = 1;
        _fallbackMaxLayer = -1;
        _fallbackLodStep = 1;
        _transitionTargetMaxLayer = -1;
        _transitionTargetLodStep = -1;
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

    private void BuildChunkSliceOverlay(
        TerrainChunkMesh mesh,
        WorldMap worldMap,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        int lodStep,
        float tilePixelSize,
        int cutLayer)
    {
        VertexArray cutHatch = new(PrimitiveType.Lines);
        VertexArray voidHatch = new(PrimitiveType.Lines);
        VertexArray contour = new(PrimitiveType.Lines);

        Color cutColor = new Color(34, 43, 48, 205);
        Color voidColor = new Color(125, 162, 174, 48);
        Color contourColor = new Color(48, 57, 61, 220);
        bool hasSolidCut = false;

        for (int y = minTileY; y <= maxTileY; y += lodStep)
        {
            for (int x = minTileX; x <= maxTileX; x += lodStep)
            {
                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float right = Math.Min(worldMap.TileWidth, x + lodStep) * tilePixelSize;
                float bottom = Math.Min(worldMap.TileHeight, y + lodStep) * tilePixelSize;

                if (worldMap.GetMaterialId(x, y, cutLayer) != 0)
                {
                    hasSolidCut = true;
                    AppendClippedDiagonalHatch(
                        cutHatch, left, top, right, bottom,
                        cutColor, MathF.Max(7f, tilePixelSize * 0.42f), false);
                }
                else if (worldMap.GetSurfaceLayer(x, y) > cutLayer)
                {
                    float spacing = MathF.Max(14f, tilePixelSize * 0.8f);
                    AppendClippedDiagonalHatch(
                        voidHatch, left, top, right, bottom,
                        voidColor, spacing, false);
                    AppendClippedDiagonalHatch(
                        voidHatch, left, top, right, bottom,
                        voidColor, spacing, true);
                }
            }
        }

        if (hasSolidCut)
        {
            // Smooth cut boundaries across voxel samples instead of drawing each
            // source tile's four-sided border.
            for (int y = minTileY; y <= maxTileY; y += lodStep)
            {
                for (int x = minTileX; x <= maxTileX; x += lodStep)
                {
                    bool tl = worldMap.GetMaterialId(x, y, cutLayer) != 0;
                    bool tr = worldMap.GetMaterialId(x + lodStep, y, cutLayer) != 0;
                    bool br = worldMap.GetMaterialId(x + lodStep, y + lodStep, cutLayer) != 0;
                    bool bl = worldMap.GetMaterialId(x, y + lodStep, cutLayer) != 0;
    
                    AppendMarchingSquaresContour(
                        contour,
                        x * tilePixelSize,
                        y * tilePixelSize,
                        lodStep * tilePixelSize,
                        tl, tr, br, bl,
                        contourColor);
                }
            }
    
    
        }

        if (cutHatch.VertexCount > 0)
            mesh.CutHatch = cutHatch;
        else
            cutHatch.Dispose();

        if (voidHatch.VertexCount > 0)
            mesh.VoidHatch = voidHatch;
        else
            voidHatch.Dispose();

        if (contour.VertexCount > 0)
            mesh.CutContour = contour;
        else
            contour.Dispose();
    }

    private static void AppendClippedDiagonalHatch(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        Color color,
        float spacing,
        bool positiveSlope)
    {
        float min = positiveSlope ? left - bottom : left + top;
        float max = positiveSlope ? right - top : right + bottom;
        int first = (int)MathF.Floor(min / spacing);
        int last = (int)MathF.Ceiling(max / spacing);

        for (int line = first; line <= last; line++)
        {
            float offset = line * spacing;
            int count = 0;
            float x1 = 0f, y1 = 0f, x2 = 0f, y2 = 0f;

            if (positiveSlope)
            {
                TryAppendClippedPoint(left, left - offset, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
                TryAppendClippedPoint(right, right - offset, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
                TryAppendClippedPoint(offset + top, top, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
                TryAppendClippedPoint(offset + bottom, bottom, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
            }
            else
            {
                TryAppendClippedPoint(left, offset - left, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
                TryAppendClippedPoint(right, offset - right, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
                TryAppendClippedPoint(offset - top, top, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
                TryAppendClippedPoint(offset - bottom, bottom, left, top, right, bottom,
                    ref count, ref x1, ref y1, ref x2, ref y2);
            }

            if (count >= 2)
                AppendLine(vertices, x1, y1, x2, y2, color);
        }
    }

    private static void TryAppendClippedPoint(
        float x, float y,
        float left, float top, float right, float bottom,
        ref int count,
        ref float x1, ref float y1, ref float x2, ref float y2)
    {
        if (x < left - 0.001f || x > right + 0.001f ||
            y < top - 0.001f || y > bottom + 0.001f)
            return;

        if ((count > 0 && MathF.Abs(x - x1) < 0.01f && MathF.Abs(y - y1) < 0.01f) ||
            (count > 1 && MathF.Abs(x - x2) < 0.01f && MathF.Abs(y - y2) < 0.01f))
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

    private static void AppendMarchingSquaresContour(
        VertexArray vertices,
        float left,
        float top,
        float step,
        bool tl,
        bool tr,
        bool br,
        bool bl,
        Color color)
    {
        int mask =
            (tl ? 1 : 0) |
            (tr ? 2 : 0) |
            (br ? 4 : 0) |
            (bl ? 8 : 0);
        if (mask is 0 or 15)
            return;

        Vector2f topPoint = new(left + step, top + step * 0.5f);
        Vector2f rightPoint = new(left + step * 1.5f, top + step);
        Vector2f bottomPoint = new(left + step, top + step * 1.5f);
        Vector2f leftPoint = new(left + step * 0.5f, top + step);

        Span<Vector2f> crossings = stackalloc Vector2f[4];
        int count = 0;
        if (tl != tr) crossings[count++] = topPoint;
        if (tr != br) crossings[count++] = rightPoint;
        if (br != bl) crossings[count++] = bottomPoint;
        if (bl != tl) crossings[count++] = leftPoint;

        if (count == 2)
        {
            AppendLine(vertices, crossings[0].X, crossings[0].Y,
                crossings[1].X, crossings[1].Y, color);
        }
        else if (count == 4)
        {
            if (mask == 5)
            {
                AppendLine(vertices, topPoint.X, topPoint.Y, leftPoint.X, leftPoint.Y, color);
                AppendLine(vertices, rightPoint.X, rightPoint.Y, bottomPoint.X, bottomPoint.Y, color);
            }
            else
            {
                AppendLine(vertices, topPoint.X, topPoint.Y, rightPoint.X, rightPoint.Y, color);
                AppendLine(vertices, bottomPoint.X, bottomPoint.Y, leftPoint.X, leftPoint.Y, color);
            }
        }
    }

    private void DrawBlockTransitions(
        RenderWindow window,
        WorldMap worldMap,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        _blockTransitionVertices.Clear();
        if (!worldMap.UsesDiscreteBlockTerrain || tilePixelSize < 2f)
            return;

        Color rampColor = new Color(224, 185, 112, 230);
        Color stairsColor = new Color(128, 185, 210, 230);
        for (int y = minTileY; y <= maxTileY; y++)
        {
            for (int x = minTileX; x <= maxTileX; x++)
            {
                BlockTransitionType type = worldMap.Blocks.GetTransitionType(x, y);
                if (type == BlockTransitionType.None ||
                    worldMap.GetSurfaceLayer(x, y) > visibleMaxLayer)
                {
                    continue;
                }

                BlockDirection direction = worldMap.Blocks.GetTransitionDirection(x, y);
                float left = x * tilePixelSize;
                float top = y * tilePixelSize;
                float centerX = left + tilePixelSize * 0.5f;
                float centerY = top + tilePixelSize * 0.5f;
                GetBlockDirectionDelta(direction, out int dx, out int dy);
                Color color = type == BlockTransitionType.Ramp
                    ? rampColor
                    : stairsColor;

                if (type == BlockTransitionType.Ramp)
                {
                    float endX = centerX + dx * tilePixelSize * 0.28f;
                    float endY = centerY + dy * tilePixelSize * 0.28f;
                    AppendLine(
                        _blockTransitionVertices,
                        centerX - dx * tilePixelSize * 0.22f,
                        centerY - dy * tilePixelSize * 0.22f,
                        endX,
                        endY,
                        color);
                    AppendLine(
                        _blockTransitionVertices,
                        endX,
                        endY,
                        endX - dx * tilePixelSize * 0.13f - dy * tilePixelSize * 0.11f,
                        endY - dy * tilePixelSize * 0.13f + dx * tilePixelSize * 0.11f,
                        color);
                    AppendLine(
                        _blockTransitionVertices,
                        endX,
                        endY,
                        endX - dx * tilePixelSize * 0.13f + dy * tilePixelSize * 0.11f,
                        endY - dy * tilePixelSize * 0.13f - dx * tilePixelSize * 0.11f,
                        color);
                    continue;
                }

                // Three transverse treads form a compact top-down stair glyph.
                for (int tread = 1; tread <= 3; tread++)
                {
                    float progress = tread / 4f - 0.5f;
                    float treadX = centerX + dx * tilePixelSize * progress;
                    float treadY = centerY + dy * tilePixelSize * progress;
                    AppendLine(
                        _blockTransitionVertices,
                        treadX - dy * tilePixelSize * 0.28f,
                        treadY + dx * tilePixelSize * 0.28f,
                        treadX + dy * tilePixelSize * 0.28f,
                        treadY - dx * tilePixelSize * 0.28f,
                        color);
                }
                AppendLine(
                    _blockTransitionVertices,
                    centerX - dx * tilePixelSize * 0.34f,
                    centerY - dy * tilePixelSize * 0.34f,
                    centerX + dx * tilePixelSize * 0.34f,
                    centerY + dy * tilePixelSize * 0.34f,
                    color);
            }
        }

        if (_blockTransitionVertices.VertexCount > 0)
            window.Draw(_blockTransitionVertices);
    }

    private static void AppendHeightStepEdges(
        VertexArray vertices,
        WorldMap worldMap,
        int x,
        int y,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        int eastX = x + lodStep;
        if (eastX <= worldMap.MaxTileX)
        {
            int edgeX = eastX;
            int firstY = y;
            int lastY = Math.Min(worldMap.MaxTileY, y + lodStep - 1);

            // Keep each physical tile edge independent at coarse LODs so a
            // ramp is dashed only on the segment where it actually exists.
            for (int edgeY = firstY; edgeY <= lastY; edgeY++)
            {
                int westLayer = FindVisibleTopLayer(
                    worldMap, edgeX - 1, edgeY, visibleMaxLayer);
                int eastLayer = FindVisibleTopLayer(
                    worldMap, edgeX, edgeY, visibleMaxLayer);
                bool traversable = worldMap.UsesDiscreteBlockTerrain &&
                    worldMap.Blocks.CanTraverse(
                        edgeX - 1, edgeY, edgeX, edgeY);

                AppendHeightStepLine(
                    vertices,
                    edgeX * tilePixelSize,
                    edgeY * tilePixelSize,
                    edgeX * tilePixelSize,
                    (edgeY + 1) * tilePixelSize,
                    westLayer,
                    eastLayer,
                    tilePixelSize,
                    traversable);
            }
        }

        int southY = y + lodStep;
        if (southY <= worldMap.MaxTileY)
        {
            int edgeY = southY;
            int firstX = x;
            int lastX = Math.Min(worldMap.MaxTileX, x + lodStep - 1);

            for (int edgeX = firstX; edgeX <= lastX; edgeX++)
            {
                int northLayer = FindVisibleTopLayer(
                    worldMap, edgeX, edgeY - 1, visibleMaxLayer);
                int southLayer = FindVisibleTopLayer(
                    worldMap, edgeX, edgeY, visibleMaxLayer);
                bool traversable = worldMap.UsesDiscreteBlockTerrain &&
                    worldMap.Blocks.CanTraverse(
                        edgeX, edgeY - 1, edgeX, edgeY);

                AppendHeightStepLine(
                    vertices,
                    edgeX * tilePixelSize,
                    edgeY * tilePixelSize,
                    (edgeX + 1) * tilePixelSize,
                    edgeY * tilePixelSize,
                    northLayer,
                    southLayer,
                    tilePixelSize,
                    traversable);
            }
        }
    }

    private static void AppendHeightStepLine(
        VertexArray vertices,
        float x1,
        float y1,
        float x2,
        float y2,
        int firstLayer,
        int secondLayer,
        float tilePixelSize,
        bool traversable)
    {
        if (firstLayer < 0 && secondLayer < 0)
            return;

        int heightDifference = Math.Abs(firstLayer - secondLayer);
        if (heightDifference < HeightStepLineThresholdUnits)
            return;

        byte alpha = heightDifference >= 30 ? (byte)220 : (byte)165;
        Color color = new Color(245, 247, 250, alpha);

        if (traversable)
        {
            AppendDashedLine(
                vertices,
                x1, y1, x2, y2,
                color,
                MathF.Max(0.45f, tilePixelSize * 0.14f));
            return;
        }

        AppendLine(vertices, x1, y1, x2, y2, color);
    }

    private static void AppendDashedLine(
        VertexArray vertices,
        float x1,
        float y1,
        float x2,
        float y2,
        Color color,
        float dashLength)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f)
            return;

        float dash = MathF.Min(dashLength, length);
        float gap = dashLength * 0.8f;

        for (float start = 0f; start < length; start += dash + gap)
        {
            float end = MathF.Min(length, start + dash);
            float startT = start / length;
            float endT = end / length;

            AppendLine(
                vertices,
                x1 + dx * startT,
                y1 + dy * startT,
                x1 + dx * endT,
                y1 + dy * endT,
                color);
        }
    }

    private static void GetBlockDirectionDelta(
        BlockDirection direction,
        out int dx,
        out int dy)
    {
        dx = direction switch
        {
            BlockDirection.East => 1,
            BlockDirection.West => -1,
            _ => 0
        };
        dy = direction switch
        {
            BlockDirection.South => 1,
            BlockDirection.North => -1,
            _ => 0
        };
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
        _heightStepVertices.Dispose();
        _blockTransitionVertices.Dispose();
        _waterVertices.Dispose();
         _terrainVertices = Array.Empty<Vertex>();
        _smoothedChunkHeights = Array.Empty<float>();
        _chunkSurfaceLayers = Array.Empty<int>();
        _terrainPreviewVertices.Dispose();
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
