using System;
using System.Diagnostics;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class MapRenderSystem : IDisposable
{
    // Build terrain in managed memory and upload the finished mesh in bulk.
    private readonly VertexArray _mapVertices =
        new VertexArray(PrimitiveType.Triangles);

    private VertexBuffer? _terrainBuffer;
    private Vertex[] _terrainVertices = Array.Empty<Vertex>();
    private int _terrainVertexCount;
    private bool _terrainBufferSupportChecked;
    private bool _useTerrainBuffer;
    private int _smallMeshRebuilds;

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

    public int TerrainVertexCapacity =>
        _terrainVertices.Length;

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

        float maximumLayerOffset =
            maxLayer * LayerScreenOffset;

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
            // Terrain bounds or layer visibility changed; water uses the same view bounds.
            _waterCacheValid = false;

            BuildTerrainMesh(
                worldMap,
                minTileX,
                maxTileX,
                minTileY,
                maxTileY,
                lodStep,
                tilePixelSize,
                maxLayer);

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

        if (_terrainVertexCount > 0)
        {
            if (_useTerrainBuffer && _terrainBuffer != null)
            {
                _terrainBuffer.Draw(
                    window,
                    0,
                    (uint)_terrainVertexCount,
                    RenderStates.Default);
            }
            else if (!_useTerrainBuffer && _mapVertices.VertexCount > 0)
            {
                window.Draw(_mapVertices);
            }
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
    }

    private void BuildTerrainMesh(
        WorldMap worldMap,
        int minTileX,
        int maxTileX,
        int minTileY,
        int maxTileY,
        int lodStep,
        float tilePixelSize,
        int visibleMaxLayer)
    {
        long started = Stopwatch.GetTimestamp();

        _terrainVertexCount = 0;

        if (!_useTerrainBuffer)
            _mapVertices.Clear();

        int columnCount =
            maxTileX < minTileX
                ? 0
                : (maxTileX - minTileX) / lodStep + 1;

        if (columnCount == 0)
        {
            UploadTerrainMesh();
            LastTerrainBuildMilliseconds = 0d;
            TerrainMeshRebuildCount++;
            return;
        }

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

                int surfaceLayer =
                    FindVisibleTopLayer(
                        worldMap,
                        x,
                        y,
                        worldMap.LayerCount - 1);

                _rowSurfaceLayers[column] = surfaceLayer;

                int topLayer =
                    Math.Min(
                        surfaceLayer,
                        visibleMaxLayer);

                _rowTopLayers[column] = topLayer;

                if (topLayer > rowMaxLayer)
                    rowMaxLayer = topLayer;
            }

            // Group adjacent equal-height/equal-material cells into one
            // wide quad. This preserves the image while avoiding six native
            // VertexArray.Append calls for every individual voxel.
            for (int z = 0; z <= rowMaxLayer; z++)
            {
                int column = 0;

                while (column < columnCount)
                {
                    int topLayer =
                        _rowTopLayers[column];

                    if (topLayer < z)
                    {
                        column++;
                        continue;
                    }

                    int x = minTileX + column * lodStep;

                    ushort materialId =
                        worldMap.GetMaterialId(x, y, z);

                    if (materialId == 0)
                    {
                        column++;
                        continue;
                    }

                    int runStart = column;
                    int surfaceLayer =
                        _rowSurfaceLayers[runStart];

                    column++;

                    while (column < columnCount &&
                           _rowTopLayers[column] == topLayer &&
                           _rowSurfaceLayers[column] == surfaceLayer)
                    {
                        int nextX =
                            minTileX + column * lodStep;

                        if (worldMap.GetMaterialId(
                                nextX,
                                y,
                                z) != materialId)
                        {
                            break;
                        }

                        column++;
                    }

                    float left =
                        (minTileX + runStart * lodStep) *
                        tilePixelSize;

                    float right =
                        (minTileX + column * lodStep) *
                        tilePixelSize;

                    float screenDepth =
                        topLayer - z;

                    float actualDepth =
                        surfaceLayer - z;

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
                        ShadeColor(
                            GetMaterialColor(materialId),
                            actualDepth));
                }
            }
        }

        UploadTerrainMesh();

        LastTerrainBuildMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        TerrainMeshRebuildCount++;
    }

    private void AppendTerrainQuad(
        float left,
        float top,
        float right,
        float bottom,
        Color color)
    {
        EnsureTerrainVertexCapacity(
            _terrainVertexCount + 6);

        Vector2f topLeft = new Vector2f(left, top);
        Vector2f topRight = new Vector2f(right, top);
        Vector2f bottomRight = new Vector2f(right, bottom);
        Vector2f bottomLeft = new Vector2f(left, bottom);

        _terrainVertices[_terrainVertexCount++] = new Vertex(topLeft, color);
        _terrainVertices[_terrainVertexCount++] = new Vertex(topRight, color);
        _terrainVertices[_terrainVertexCount++] = new Vertex(bottomRight, color);
        _terrainVertices[_terrainVertexCount++] = new Vertex(topLeft, color);
        _terrainVertices[_terrainVertexCount++] = new Vertex(bottomRight, color);
        _terrainVertices[_terrainVertexCount++] = new Vertex(bottomLeft, color);
    }

    private void EnsureTerrainVertexCapacity(
        int required)
    {
        if (required <= _terrainVertices.Length)
            return;

        int newCapacity =
            Math.Max(
                4096,
                _terrainVertices.Length * 2);

        while (newCapacity < required)
            newCapacity *= 2;

        Array.Resize(
            ref _terrainVertices,
            newCapacity);
    }

    private void UploadTerrainMesh()
    {
        if (!_useTerrainBuffer)
        {
            _mapVertices.Clear();

            for (int i = 0; i < _terrainVertexCount; i++)
                _mapVertices.Append(_terrainVertices[i]);

            return;
        }

        if (_terrainVertexCount == 0)
        {
            if (_useTerrainBuffer &&
                _terrainBuffer != null &&
                _terrainVertices.Length > 32768)
            {
                _smallMeshRebuilds++;

                if (_smallMeshRebuilds >= 8)
                {
                    _terrainBuffer.Dispose();
                    _terrainBuffer = null;
                    Array.Resize(
                        ref _terrainVertices,
                        4096);
                    _smallMeshRebuilds = 0;
                }
            }

            return;
        }

        uint requiredCapacity =
            (uint)_terrainVertices.Length;

        if (_terrainBuffer == null ||
            _terrainBuffer.VertexCount < requiredCapacity)
        {
            _terrainBuffer?.Dispose();

            _terrainBuffer =
                new VertexBuffer(
                    requiredCapacity,
                    PrimitiveType.Triangles,
                    VertexBuffer.UsageSpecifier.Dynamic);
        }

        if (!_terrainBuffer.Update(
                _terrainVertices,
                (uint)_terrainVertexCount,
                0))
        {
            _terrainBuffer.Dispose();
            _terrainBuffer = null;
            _useTerrainBuffer = false;
            _mapVertices.Clear();

            for (int i = 0; i < _terrainVertexCount; i++)
                _mapVertices.Append(_terrainVertices[i]);

            return;
        }

        // Release unusually large retained buffers after repeated smaller
        // rebuilds; a single transient view must not pin peak memory forever.
        if (_useTerrainBuffer &&
            _terrainBuffer != null &&
            _terrainVertices.Length > 32768 &&
            _terrainVertexCount * 4 < _terrainVertices.Length)
        {
            _smallMeshRebuilds++;

            if (_smallMeshRebuilds >= 8)
            {
                int newCapacity = 4096;

                while (newCapacity < _terrainVertexCount)
                    newCapacity *= 2;

                Array.Resize(
                    ref _terrainVertices,
                    newCapacity);

                _terrainBuffer?.Dispose();
                _terrainBuffer =
                    new VertexBuffer(
                        (uint)newCapacity,
                        PrimitiveType.Triangles,
                        VertexBuffer.UsageSpecifier.Dynamic);

                _terrainBuffer.Update(
                    _terrainVertices,
                    (uint)_terrainVertexCount,
                    0);

                _smallMeshRebuilds = 0;
            }
        }
        else
        {
            _smallMeshRebuilds = 0;
        }
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
        _terrainBuffer?.Dispose();
        _terrainBuffer = null;

        _mapVertices.Dispose();
        _gridVertices.Dispose();
        _waterVertices.Dispose();
    }

    private static Color GetMaterialColor(
        ushort materialId)
    {
        return materialId switch
        {
            1 => new Color(42, 41, 35),
            2 => new Color(34, 36, 33),
            3 => new Color(58, 57, 51),
            4 => new Color(73, 61, 41),
            _ => new Color(45, 47, 46)
        };
    }

    private static Color ShadeColor(
        Color color,
        float depth)
    {
        float factor =
            1f / (1f + MathF.Max(0f, depth) * 0.12f);

        return new Color(
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor),
            color.A);
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
