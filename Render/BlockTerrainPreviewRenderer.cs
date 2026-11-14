using System;
using System.Collections.Generic;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Isometric inspection view for the discrete 4 x 4 x 1 m block terrain.
/// Camera elevation changes only projection; terrain data remains fixed.
/// It is intentionally read-only: gameplay keeps its normal XY controls.
/// </summary>
public sealed class BlockTerrainPreviewRenderer : IDisposable
{
    private const int MinimumBlockSize = 1;
    private const int MaximumAdaptiveBlockSize = BlockTerrain.HorizontalBlockSize * 8;

    private struct BlockDrawItem
    {
        public int X;
        public int Y;
        public int Size;
        public float Height;
        public ushort MaterialId;
        public float Depth;
    }

    private readonly VertexArray _blockFaces = new(PrimitiveType.Triangles);
    private readonly List<BlockDrawItem> _visibleBlocks = new(4096);
    private bool _cacheValid;
    private long _cachedTerrainVersion = long.MinValue;
    private int _cachedLodStep;
    private float _cachedTileSize = -1f;
    private Vector2f _cachedCameraCenter;
    private Vector2f _cachedViewSize;
    private float _cachedCameraAzimuthDegrees = float.NaN;
    private float _cachedElevationDegrees = float.NaN;
    private long _cachedHighestHeightTerrainVersion = long.MinValue;
    private float _cachedHighestHeightMeters;
    private float _rotationCosine = 1f;
    private float _rotationSine;

    public Vector2f GetCameraCenter(
        WorldMap worldMap,
        float tilePixelSize,
        float elevationDegrees)
    {
        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float elevationRadians = Math.Clamp(elevationDegrees, 15f, 90f) *
            (MathF.PI / 180f);
        float groundDepthScale =
            tileSize * 0.5f * MathF.Sin(elevationRadians);
        float heightScale =
            tileSize * 0.5f * MathF.Cos(elevationRadians) /
            MathF.Cos(MathF.PI / 6f);
        float centerX = worldMap.TileWidth * 0.5f;
        float centerY = worldMap.TileHeight * 0.5f;
        float highestMeters = GetHighestHeightMeters(worldMap);

        return new Vector2f(
            (centerX - centerY) * tileSize * 0.5f,
            (centerX + centerY) * groundDepthScale -
                highestMeters * heightScale * 0.42f);
    }

    public void Draw(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float cameraAzimuthDegrees,
        float elevationDegrees,
        bool isCameraRotating)
    {
        if (!worldMap.UsesDiscreteBlockTerrain || tilePixelSize <= 0f)
            return;

        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float halfWidth = tileSize * 0.5f;
        elevationDegrees = Math.Clamp(
            float.IsFinite(elevationDegrees) ? elevationDegrees : 30f,
            15f,
            90f);
        float elevationRadians = elevationDegrees * (MathF.PI / 180f);
        float groundDepthScale =
            tileSize * 0.5f * MathF.Sin(elevationRadians);
        float verticalScale =
            tileSize * 0.5f * MathF.Cos(elevationRadians) /
            MathF.Cos(MathF.PI / 6f);
        float highestMeters = GetHighestHeightMeters(worldMap);
        if (!float.IsFinite(cameraAzimuthDegrees))
            cameraAzimuthDegrees = 0f;
        cameraAzimuthDegrees %= 360f;
        if (cameraAzimuthDegrees < 0f)
            cameraAzimuthDegrees += 360f;
        float rotationRadians = cameraAzimuthDegrees * (MathF.PI / 180f);
        _rotationCosine = MathF.Cos(rotationRadians);
        _rotationSine = MathF.Sin(rotationRadians);

        float pixelsPerTile = tileSize * window.Size.X /
            MathF.Max(1f, cameraView.Size.X);
        // Calm terrain is merged into large 32 m blocks. Height/material changes
        // recursively split patches down to 1 m details; while dragging the camera,
        // keep the previous 4 m minimum detail to reduce rotation-time rebuild work.
        int minimumDetailSize = isCameraRotating
            ? BlockTerrain.HorizontalBlockSize
            : MinimumBlockSize;

        bool cameraMovedOutsideCache =
            MathF.Abs(cameraView.Center.X - _cachedCameraCenter.X) >
                cameraView.Size.X * 0.2f ||
            MathF.Abs(cameraView.Center.Y - _cachedCameraCenter.Y) >
                cameraView.Size.Y * 0.2f;
        // The cached mesh already includes a 70% viewport margin.
        // Rebuild only when zooming out far enough to exceed that margin;
        // zooming in and small wheel steps can safely reuse the existing mesh.
        bool viewSizeChanged =
            cameraView.Size.X > _cachedViewSize.X * 1.25f ||
            cameraView.Size.Y > _cachedViewSize.Y * 1.25f;
        bool rebuildGeometry =
            !_cacheValid ||
            _cachedTerrainVersion != worldMap.TerrainVersion ||
            MathF.Abs(_cachedCameraAzimuthDegrees - cameraAzimuthDegrees) > 0.01f ||
            MathF.Abs(_cachedElevationDegrees - elevationDegrees) > 0.01f ||
            _cachedLodStep != minimumDetailSize ||
            MathF.Abs(_cachedTileSize - tileSize) > 0.001f ||
            cameraMovedOutsideCache ||
            viewSizeChanged;

        if (rebuildGeometry)
        {
            _blockFaces.Clear();

            GetVisibleTileBounds(
                cameraView,
                halfWidth,
                groundDepthScale,
                verticalScale,
                highestMeters,
                worldMap,
                _rotationCosine,
                _rotationSine,
                out int minX,
                out int maxX,
                out int minY,
                out int maxY);

            maxX = Math.Min(worldMap.MaxTileX, maxX);
            maxY = Math.Min(worldMap.MaxTileY, maxY);

            _visibleBlocks.Clear();
            float centerX = worldMap.TileWidth * 0.5f;
            float centerY = worldMap.TileHeight * 0.5f;
            int firstBlockX = minX / MaximumAdaptiveBlockSize * MaximumAdaptiveBlockSize;
            int firstBlockY = minY / MaximumAdaptiveBlockSize * MaximumAdaptiveBlockSize;
            for (int y = firstBlockY; y <= maxY; y += MaximumAdaptiveBlockSize)
            {
                for (int x = firstBlockX; x <= maxX; x += MaximumAdaptiveBlockSize)
                {
                    CollectAdaptiveBlocks(
                        worldMap, x, y, MaximumAdaptiveBlockSize,
                        minimumDetailSize, centerX, centerY);
                }
            }

            _visibleBlocks.Sort(static (left, right) =>
            {
                int depthOrder = left.Depth.CompareTo(right.Depth);
                if (depthOrder != 0)
                    return depthOrder;
                int xOrder = left.X.CompareTo(right.X);
                return xOrder != 0 ? xOrder : left.Y.CompareTo(right.Y);
            });

            bool drawBlockGrid = pixelsPerTile >= 2.4f;
            Color seamColor = new Color(27, 31, 34, 150);
            for (int i = 0; i < _visibleBlocks.Count; i++)
            {
                BlockDrawItem block = _visibleBlocks[i];
                int x = block.X;
                int y = block.Y;
                int size = block.Size;
                float height = block.Height;
                float northZ = height;
                float eastZ = height;
                float southZ = height;
                float westZ = height;

                Vector2f north = ProjectLevelPoint(
                    worldMap, x, y, northZ, tileSize,
                    groundDepthScale, verticalScale);
                Vector2f east = ProjectLevelPoint(
                    worldMap, x + size, y, eastZ, tileSize,
                    groundDepthScale, verticalScale);
                Vector2f south = ProjectLevelPoint(
                    worldMap, x + size, y + size, southZ, tileSize,
                    groundDepthScale, verticalScale);
                Vector2f west = ProjectLevelPoint(
                    worldMap, x, y + size, westZ, tileSize,
                    groundDepthScale, verticalScale);

                AppendVisibleSideFaces(
                    worldMap, x, y, size, minimumDetailSize, tileSize,
                    groundDepthScale, verticalScale,
                    drawBlockGrid,
                    northZ, eastZ, southZ, westZ);

                Color topColor = GetTopColor(block.MaterialId, height);
                AppendTopFace(_blockFaces, north, east, south, west, topColor);

                if (drawBlockGrid)
                {
                    AppendLineAsQuad(_blockFaces, north, east, seamColor);
                    AppendLineAsQuad(_blockFaces, east, south, seamColor);
                    AppendLineAsQuad(_blockFaces, south, west, seamColor);
                    AppendLineAsQuad(_blockFaces, west, north, seamColor);
                }
            }

            _cachedTerrainVersion = worldMap.TerrainVersion;
            _cachedCameraAzimuthDegrees = cameraAzimuthDegrees;
            _cachedElevationDegrees = elevationDegrees;
            _cachedLodStep = minimumDetailSize;
            _cachedTileSize = tileSize;
            _cachedCameraCenter = cameraView.Center;
            _cachedViewSize = cameraView.Size;
            _cacheValid = true;
        }

        window.Draw(_blockFaces);
    }

    private void CollectAdaptiveBlocks(
        WorldMap worldMap,
        int x,
        int y,
        int size,
        int minimumDetailSize,
        float centerX,
        float centerY)
    {
        if (x >= worldMap.TileWidth || y >= worldMap.TileHeight)
            return;

        if (TryGetUniformPatch(
                worldMap, x, y, size, minimumDetailSize,
                out int surfaceLevel, out ushort materialId))
        {
            if (surfaceLevel > 0 && materialId != 0)
                AddVisibleBlock(x, y, size, surfaceLevel, materialId, centerX, centerY);
            return;
        }

        if (size <= minimumDetailSize)
        {
            // At the active detail limit use the local surface sample. This is
            // 1 m when idle and 4 m during camera rotation for a cheaper preview.
            surfaceLevel = worldMap.Blocks.GetSurfaceLevel(x, y);
            materialId = worldMap.Blocks.GetMaterialId(x, y);
            if (surfaceLevel > 0 && materialId != 0)
                AddVisibleBlock(x, y, size, surfaceLevel, materialId, centerX, centerY);
            return;
        }

        int half = size >> 1;
        CollectAdaptiveBlocks(worldMap, x, y, half, minimumDetailSize, centerX, centerY);
        CollectAdaptiveBlocks(worldMap, x + half, y, half, minimumDetailSize, centerX, centerY);
        CollectAdaptiveBlocks(worldMap, x, y + half, half, minimumDetailSize, centerX, centerY);
        CollectAdaptiveBlocks(worldMap, x + half, y + half, half, minimumDetailSize, centerX, centerY);
    }

    private static bool TryGetUniformPatch(
        WorldMap worldMap,
        int x,
        int y,
        int size,
        int minimumDetailSize,
        out int surfaceLevel,
        out ushort materialId)
    {
        surfaceLevel = 0;
        materialId = 0;
        if (x + size > worldMap.TileWidth || y + size > worldMap.TileHeight)
            return false;

        BlockTerrain blocks = worldMap.Blocks;
        surfaceLevel = blocks.GetSurfaceLevel(x, y);
        materialId = blocks.GetMaterialId(x, y);

        for (int tileY = y; tileY < y + size; tileY++)
        {
            for (int tileX = x; tileX < x + size; tileX++)
            {
                if (blocks.GetSurfaceLevel(tileX, tileY) != surfaceLevel ||
                    blocks.GetMaterialId(tileX, tileY) != materialId ||
                    (size > minimumDetailSize && blocks.IsTransitionCell(tileX, tileY)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void AddVisibleBlock(
        int x,
        int y,
        int size,
        int surfaceLevel,
        ushort materialId,
        float centerX,
        float centerY)
    {
        float localX = x + size * 0.5f - centerX;
        float localY = y + size * 0.5f - centerY;
        float rotatedX = _rotationCosine * localX - _rotationSine * localY;
        float rotatedY = _rotationSine * localX + _rotationCosine * localY;

        _visibleBlocks.Add(new BlockDrawItem
        {
            X = x,
            Y = y,
            Size = size,
            Height = surfaceLevel,
            MaterialId = materialId,
            Depth = rotatedX + rotatedY
        });
    }

    private void AppendVisibleSideFaces(
        WorldMap worldMap,
        int x,
        int y,
        int blockSize,
        int edgeSegmentSize,
        float tileSize,
        float groundDepthScale,
        float verticalScale,
        bool drawBlockGrid,
        float northZ,
        float eastZ,
        float southZ,
        float westZ)
    {
        Color xFacingColor = new Color(55, 61, 65, 255);
        Color yFacingColor = new Color(39, 45, 49, 255);

        for (int offset = 0; offset < blockSize; offset += edgeSegmentSize)
        {
            int segmentSize = Math.Min(edgeSegmentSize, blockSize - offset);

            if (_rotationCosine + _rotationSine >= 0f)
            {
                int segmentX = x + blockSize - segmentSize;
                int segmentY = y + offset;
                AppendSourceEdge(worldMap, segmentX, segmentY, 1, 0, segmentSize,
                    x + blockSize, segmentY, x + blockSize, segmentY + segmentSize,
                    eastZ, southZ, tileSize,
                    groundDepthScale, verticalScale, drawBlockGrid, xFacingColor);
            }
            else
            {
                int segmentX = x;
                int segmentY = y + offset;
                AppendSourceEdge(worldMap, segmentX, segmentY, -1, 0, segmentSize,
                    x, segmentY + segmentSize, x, segmentY,
                    westZ, northZ, tileSize,
                    groundDepthScale, verticalScale, drawBlockGrid, xFacingColor);
            }

            if (_rotationCosine - _rotationSine >= 0f)
            {
                int segmentX = x + offset;
                int segmentY = y + blockSize - segmentSize;
                AppendSourceEdge(worldMap, segmentX, segmentY, 0, 1, segmentSize,
                    segmentX + segmentSize, y + blockSize, segmentX, y + blockSize,
                    southZ, westZ, tileSize,
                    groundDepthScale, verticalScale, drawBlockGrid, yFacingColor);
            }
            else
            {
                int segmentX = x + offset;
                int segmentY = y;
                AppendSourceEdge(worldMap, segmentX, segmentY, 0, -1, segmentSize,
                    segmentX, y, segmentX + segmentSize, y,
                    northZ, eastZ, tileSize,
                    groundDepthScale, verticalScale, drawBlockGrid, yFacingColor);
            }
        }
    }

    private void AppendSourceEdge(
        WorldMap worldMap,
        int x,
        int y,
        int neighbourDx,
        int neighbourDy,
        int step,
        float ax,
        float ay,
        float bx,
        float by,
        float topHeightA,
        float topHeightB,
        float tileSize,
        float groundDepthScale,
        float verticalScale,
        bool drawBlockGrid,
        Color color)
    {
        int neighbourX = x + neighbourDx * step;
        int neighbourY = y + neighbourDy * step;
        int neighbourLevel = worldMap.Blocks.GetSurfaceLevel(neighbourX, neighbourY);
        float neighbourHeight = neighbourLevel > 0 ? neighbourLevel : 0f;

        float bottomHeightA = MathF.Min(topHeightA, neighbourHeight);
        float bottomHeightB = MathF.Min(topHeightB, neighbourHeight);
        if (topHeightA - bottomHeightA < 0.01f &&
            topHeightB - bottomHeightB < 0.01f)
            return;

        Vector2f topA = ProjectLevelPoint(
            worldMap, ax, ay, topHeightA, tileSize,
            groundDepthScale, verticalScale);
        Vector2f topB = ProjectLevelPoint(
            worldMap, bx, by, topHeightB, tileSize,
            groundDepthScale, verticalScale);
        Vector2f bottomA = ProjectLevelPoint(
            worldMap, ax, ay, bottomHeightA, tileSize,
            groundDepthScale, verticalScale);
        Vector2f bottomB = ProjectLevelPoint(
            worldMap, bx, by, bottomHeightB, tileSize,
            groundDepthScale, verticalScale);
        AppendQuad(_blockFaces, topA, topB, bottomB, bottomA, color);

        if (drawBlockGrid)
        {
            float seamStart = MathF.Max(bottomHeightA, bottomHeightB);
            float seamEnd = MathF.Min(topHeightA, topHeightB);
            for (float height = MathF.Floor(seamStart) + 1f;
                 height < seamEnd - 0.01f;
                 height += 1f)
            {
                AppendLineAsQuad(
                    _blockFaces,
                    ProjectLevelPoint(
                        worldMap, ax, ay, height, tileSize,
                        groundDepthScale, verticalScale),
                    ProjectLevelPoint(
                        worldMap, bx, by, height, tileSize,
                        groundDepthScale, verticalScale),
                    new Color(18, 22, 25, 190));
            }
        }
    }

    private static void AppendTopFace(
        VertexArray vertices,
        Vector2f north,
        Vector2f east,
        Vector2f south,
        Vector2f west,
        Color color)
    {
        AppendTriangle(vertices, north, east, south, color);
        AppendTriangle(vertices, north, south, west, color);
    }

    private static void AppendQuad(
        VertexArray vertices,
        Vector2f a,
        Vector2f b,
        Vector2f c,
        Vector2f d,
        Color color)
    {
        AppendTriangle(vertices, a, b, c, color);
        AppendTriangle(vertices, a, c, d, color);
    }

    private static void AppendTriangle(
        VertexArray vertices,
        Vector2f a,
        Vector2f b,
        Vector2f c,
        Color color)
    {
        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(b, color));
        vertices.Append(new Vertex(c, color));
    }

    private static void AppendLineAsQuad(
        VertexArray vertices,
        Vector2f a,
        Vector2f b,
        Color color)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f)
            return;

        const float halfThickness = 0.55f;
        float offsetX = -dy / length * halfThickness;
        float offsetY = dx / length * halfThickness;

        AppendQuad(
            vertices,
            new Vector2f(a.X + offsetX, a.Y + offsetY),
            new Vector2f(b.X + offsetX, b.Y + offsetY),
            new Vector2f(b.X - offsetX, b.Y - offsetY),
            new Vector2f(a.X - offsetX, a.Y - offsetY),
            color);
    }

    private static Color GetTopColor(
        ushort materialId,
        float heightMeters)
    {
        int shade = Math.Clamp(103 + (int)(heightMeters * 0.35f), 92, 132);
        int materialTint = materialId == 1 ? 0 : (materialId % 3) * 5;
        return new Color(
            (byte)Math.Clamp(shade + materialTint, 0, 255),
            (byte)Math.Clamp(shade + 3, 0, 255),
            (byte)Math.Clamp(shade + 6 - materialTint, 0, 255),
            255);
    }

    private Vector2f ProjectLevelPoint(
        WorldMap worldMap,
        float x,
        float y,
        float heightMeters,
        float tileSize,
        float groundDepthScale,
        float verticalScale)
    {
        float centerX = worldMap.TileWidth * 0.5f;
        float centerY = worldMap.TileHeight * 0.5f;
        float dx = x - centerX;
        float dy = y - centerY;
        float rotatedX =
            centerX + _rotationCosine * dx - _rotationSine * dy;
        float rotatedY =
            centerY + _rotationSine * dx + _rotationCosine * dy;

        return new Vector2f(
            (rotatedX - rotatedY) * tileSize * 0.5f,
            (rotatedX + rotatedY) * groundDepthScale -
                heightMeters * verticalScale);
    }

    private float GetHighestHeightMeters(WorldMap worldMap)
    {
        long terrainVersion = worldMap.TerrainVersion;
        if (_cachedHighestHeightTerrainVersion != terrainVersion)
        {
            int highestLayer = worldMap.GetHighestOccupiedLayer();
            _cachedHighestHeightMeters =
                highestLayer < 0 ? 0f : (highestLayer + 1f) * 0.1f;
            _cachedHighestHeightTerrainVersion = terrainVersion;
        }

        return _cachedHighestHeightMeters;
    }

    private static void GetVisibleTileBounds(
        View view,
        float halfWidth,
        float groundDepthScale,
        float verticalScale,
        float highestMeters,
        WorldMap worldMap,
        float rotationCosine,
        float rotationSine,
        out int minX,
        out int maxX,
        out int minY,
        out int maxY)
    {
        minX = worldMap.MaxTileX;
        minY = worldMap.MaxTileY;
        maxX = 0;
        maxY = 0;

        // Build slightly beyond the viewport. Small camera pans can then reuse
        // this mesh instead of rebuilding thousands of block faces every frame.
        float radians = view.Rotation.Radians;
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);

        for (int corner = 0; corner < 4; corner++)
        {
            float localX = (corner & 1) == 0 ? -view.Size.X * 0.7f : view.Size.X * 0.7f;
            float localY = (corner & 2) == 0 ? -view.Size.Y * 0.7f : view.Size.Y * 0.7f;
            float screenX = view.Center.X + cosine * localX - sine * localY;
            float screenY = view.Center.Y + sine * localX + cosine * localY;

            for (int heightIndex = 0; heightIndex < 2; heightIndex++)
            {
                float baseY = screenY +
                    (heightIndex == 0 ? 0f : highestMeters * verticalScale);
                float projectedX = screenX / halfWidth;
                float projectedY = baseY / groundDepthScale;
                float rotatedX = (projectedX + projectedY) * 0.5f;
                float rotatedY = (projectedY - projectedX) * 0.5f;
                float centerX = worldMap.TileWidth * 0.5f;
                float centerY = worldMap.TileHeight * 0.5f;
                float dx = rotatedX - centerX;
                float dy = rotatedY - centerY;

                // Inverse camera azimuth maps the four screen corners back
                // into the unchanged world-map tile coordinates.
                float x =
                    centerX + rotationCosine * dx + rotationSine * dy;
                float y =
                    centerY - rotationSine * dx + rotationCosine * dy;

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

    public void Dispose()
    {
        _blockFaces.Dispose();
    }
}
