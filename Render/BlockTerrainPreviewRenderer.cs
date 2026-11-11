using System;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Isometric inspection view for the discrete 1 m block terrain.
/// It is intentionally read-only: gameplay keeps its normal XY controls.
/// </summary>
public sealed class BlockTerrainPreviewRenderer : IDisposable
{
    private readonly VertexArray _blockFaces = new(PrimitiveType.Triangles);
    private readonly VertexArray _blockSeams = new(PrimitiveType.Lines);
    private bool _cacheValid;
    private long _cachedTerrainVersion = long.MinValue;
    private int _cachedLodStep;
    private float _cachedTileSize = -1f;
    private Vector2f _cachedCameraCenter;
    private Vector2f _cachedViewSize;
    private int _rotationStep;

    public void SetRotation(int quarterTurns)
    {
        int next = quarterTurns & 3;
        if (_rotationStep == next)
            return;

        _rotationStep = next;
        _cacheValid = false;
    }

    public static Vector2f GetCameraCenter(
        WorldMap worldMap,
        float tilePixelSize)
    {
        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float highestMeters = GetHighestHeightMeters(worldMap);
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

        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float halfWidth = tileSize * 0.5f;
        float quarterHeight = tileSize * 0.25f;
        float verticalScale = tileSize * 0.5f;
        float highestMeters = GetHighestHeightMeters(worldMap);

        float pixelsPerTile = tileSize * window.Size.X /
            MathF.Max(1f, cameraView.Size.X);
        // Keep the detailed 1 m mesh for close inspection only.
        // At normal overview zoom, larger patches avoid submitting tens of
        // thousands of individual block faces to the GPU.
        int step = pixelsPerTile < 3.5f ? 16
            : pixelsPerTile < 7f ? 8
            : pixelsPerTile < 14f ? 4
            : pixelsPerTile < 28f ? 2
            : 1;

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
            _cachedLodStep != step ||
            MathF.Abs(_cachedTileSize - tileSize) > 0.001f ||
            cameraMovedOutsideCache ||
            viewSizeChanged;

        if (rebuildGeometry)
        {
            _blockFaces.Clear();
            _blockSeams.Clear();

            GetVisibleTileBounds(
                cameraView,
                halfWidth,
                quarterHeight,
                verticalScale,
                highestMeters,
                worldMap,
                _rotationStep,
                out int minX,
                out int maxX,
                out int minY,
                out int maxY);
    
            minX = minX / step * step;
            minY = minY / step * step;
            maxX = Math.Min(worldMap.MaxTileX, maxX);
            maxY = Math.Min(worldMap.MaxTileY, maxY);
    
            // Traverse diagonals in rotated level-space so the depth order
            // follows the terrain, not the camera.
            int worldWidth = worldMap.TileWidth;
            int worldHeight = worldMap.TileHeight;
            for (int depth = 0; depth <= worldWidth + worldHeight - 2; depth++)
            {
                int firstX;
                int lastX;
                switch (_rotationStep)
                {
                    case 1:
                        firstX = Math.Max(minX, minY - (worldHeight - 1) + depth);
                        lastX = Math.Min(maxX, maxY - (worldHeight - 1) + depth);
                        break;
                    case 2:
                        int oppositeDepth = worldWidth + worldHeight - 2 - depth;
                        firstX = Math.Max(minX, oppositeDepth - maxY);
                        lastX = Math.Min(maxX, oppositeDepth - minY);
                        break;
                    case 3:
                        firstX = Math.Max(minX, minY - depth + worldWidth - 1);
                        lastX = Math.Min(maxX, maxY - depth + worldWidth - 1);
                        break;
                    default:
                        firstX = Math.Max(minX, depth - maxY);
                        lastX = Math.Min(maxX, depth - minY);
                        break;
                }

                firstX = Math.Max(0, (firstX + step - 1) / step * step);
                for (int x = firstX; x <= lastX; x += step)
                {
                    int y = _rotationStep switch
                    {
                        1 => worldHeight - 1 + x - depth,
                        2 => worldWidth + worldHeight - 2 - depth - x,
                        3 => depth - worldWidth + 1 + x,
                        _ => depth - x
                    };
                    if (y < minY || y > maxY || y % step != 0)
                        continue;

                    int surfaceLayer = worldMap.FindTopOccupiedLayer(
                        x, y, worldMap.LayerCount - 1);
                    float baseHeight;
                    BlockDirection rampDirection = BlockDirection.North;
                    bool isRamp;

                    if (step == 1)
                    {
                        if (surfaceLayer < 0)
                            continue;

                        baseHeight = worldMap.Blocks.GetSurfaceLevel(x, y);
                        if (baseHeight <= 0f)
                            baseHeight = (surfaceLayer + 1f) * 0.1f;

                        isRamp = TryGetValidRamp(
                            worldMap,
                            x,
                            y,
                            out rampDirection,
                            out _);
                    }
                    else if (TrySummarizeRampPatch(
                                 worldMap,
                                 x,
                                 y,
                                 step,
                                 out rampDirection,
                                 out baseHeight))
                    {
                        // Several one-metre ramps collapse to one coarse ramp
                        // for this patch. At distant LOD we keep only the
                        // dominant direction, rather than appending every ramp.
                        isRamp = true;
                    }
                    else
                    {
                        if (surfaceLayer < 0)
                            continue;

                        baseHeight = worldMap.Blocks.GetSurfaceLevel(x, y);
                        if (baseHeight <= 0f)
                            baseHeight = (surfaceLayer + 1f) * 0.1f;

                        isRamp = false;
                    }

                    BlockTransitionType transition = isRamp
                        ? BlockTransitionType.Ramp
                        : BlockTransitionType.None;
                    float northZ = baseHeight;
                    float eastZ = baseHeight;
                    float southZ = baseHeight;
                    float westZ = baseHeight;

                    if (isRamp)
                    {
                        ApplyRampSlope(
                            rampDirection,
                            ref northZ,
                            ref eastZ,
                            ref southZ,
                            ref westZ);
                    }

                    Vector2f north = ProjectLevelPoint(worldMap, x, y, northZ, tileSize);
                    Vector2f east = ProjectLevelPoint(worldMap, x + step, y, eastZ, tileSize);
                    Vector2f south = ProjectLevelPoint(worldMap, x + step, y + step, southZ, tileSize);
                    Vector2f west = ProjectLevelPoint(worldMap, x, y + step, westZ, tileSize);

                    // Keep camera-facing sides fixed while selecting the
                    // corresponding source edges after the level turns.
                    AppendVisibleSideFaces(
                        worldMap, x, y, step, tileSize,
                        northZ, eastZ, southZ, westZ);
    
                    ushort materialId = surfaceLayer >= 0
                        ? worldMap.GetMaterialId(x, y, surfaceLayer)
                        : (ushort)0;
                    Color topColor = GetTopColor(materialId, baseHeight, transition);
                    AppendTopFace(_blockFaces, north, east, south, west, topColor);
    
                    // Thin seams expose the individual metre blocks without
                    // covering the terrain with a bright wireframe.
                    Color seamColor = new Color(27, 31, 34, 150);
                    AppendLine(_blockSeams, north, east, seamColor);
                    AppendLine(_blockSeams, east, south, seamColor);
                    AppendLine(_blockSeams, south, west, seamColor);
                    AppendLine(_blockSeams, west, north, seamColor);

                }
        }

            _cachedTerrainVersion = worldMap.TerrainVersion;
            _cachedLodStep = step;
            _cachedTileSize = tileSize;
            _cachedCameraCenter = cameraView.Center;
            _cachedViewSize = cameraView.Size;
            _cacheValid = true;
        }

        window.Draw(_blockFaces);
        if (pixelsPerTile >= 2.4f)
            window.Draw(_blockSeams);
    }

    private static bool TryGetValidRamp(
        WorldMap worldMap,
        int x,
        int y,
        out BlockDirection direction,
        out float baseHeight)
    {
        direction = worldMap.Blocks.GetTransitionDirection(x, y);
        baseHeight = worldMap.Blocks.GetSurfaceLevel(x, y);

        if (worldMap.Blocks.GetTransitionType(x, y) != BlockTransitionType.Ramp ||
            baseHeight <= 0f)
        {
            return false;
        }

        GetDirectionDelta(direction, out int dx, out int dy);
        return worldMap.Blocks.GetSurfaceLevel(x + dx, y + dy) ==
               baseHeight + 1f;
    }

    private static void GetDirectionDelta(
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

    private static void ApplyRampSlope(
        BlockDirection direction,
        ref float northZ,
        ref float eastZ,
        ref float southZ,
        ref float westZ)
    {
        switch (direction)
        {
            case BlockDirection.North:
                northZ += 1f;
                eastZ += 1f;
                break;
            case BlockDirection.East:
                eastZ += 1f;
                southZ += 1f;
                break;
            case BlockDirection.South:
                southZ += 1f;
                westZ += 1f;
                break;
            case BlockDirection.West:
                westZ += 1f;
                northZ += 1f;
                break;
        }
    }

    private static bool TrySummarizeRampPatch(
        WorldMap worldMap,
        int startX,
        int startY,
        int step,
        out BlockDirection dominantDirection,
        out float averageBaseHeight)
    {
        Span<int> directionCounts = stackalloc int[4];
        int totalRamps = 0;
        float heightSum = 0f;

        int maxX = Math.Min(worldMap.MaxTileX, startX + step - 1);
        int maxY = Math.Min(worldMap.MaxTileY, startY + step - 1);
        for (int y = startY; y <= maxY; y++)
        {
            for (int x = startX; x <= maxX; x++)
            {
                if (!TryGetValidRamp(
                        worldMap,
                        x,
                        y,
                        out BlockDirection direction,
                        out float baseHeight))
                {
                    continue;
                }

                directionCounts[(int)direction]++;
                totalRamps++;
                heightSum += baseHeight;
            }
        }

        int bestDirection = -1;
        int bestCount = 0;
        for (int direction = 0; direction < directionCounts.Length; direction++)
        {
            if (directionCounts[direction] > bestCount)
            {
                bestCount = directionCounts[direction];
                bestDirection = direction;
            }
        }

        // Mixed patches with no clear majority should not invent a ramp
        // direction. They stay a flat distant-LOD surface instead.
        if (totalRamps == 0 || bestCount * 2 <= totalRamps)
        {
            dominantDirection = BlockDirection.North;
            averageBaseHeight = 0f;
            return false;
        }

        dominantDirection = (BlockDirection)bestDirection;
        averageBaseHeight = heightSum / totalRamps;
        return true;
    }

    private void AppendVisibleSideFaces(
        WorldMap worldMap,
        int x,
        int y,
        int step,
        float tileSize,
        float northZ,
        float eastZ,
        float southZ,
        float westZ)
    {
        Color xFacingColor = new Color(55, 61, 65, 255);
        Color yFacingColor = new Color(39, 45, 49, 255);

        // Each turn changes which original map edge faces the fixed camera.
        switch (_rotationStep)
        {
            case 1:
                AppendSourceEdge(worldMap, x, y, 0, -1, step,
                    x, y, x + step, y, northZ, eastZ, tileSize, xFacingColor);
                AppendSourceEdge(worldMap, x, y, 1, 0, step,
                    x + step, y, x + step, y + step,
                    eastZ, southZ, tileSize, yFacingColor);
                break;
            case 2:
                AppendSourceEdge(worldMap, x, y, -1, 0, step,
                    x, y + step, x, y, westZ, northZ, tileSize, xFacingColor);
                AppendSourceEdge(worldMap, x, y, 0, -1, step,
                    x, y, x + step, y, northZ, eastZ, tileSize, yFacingColor);
                break;
            case 3:
                AppendSourceEdge(worldMap, x, y, 0, 1, step,
                    x + step, y + step, x, y + step,
                    southZ, westZ, tileSize, xFacingColor);
                AppendSourceEdge(worldMap, x, y, -1, 0, step,
                    x, y + step, x, y, westZ, northZ, tileSize, yFacingColor);
                break;
            default:
                AppendSourceEdge(worldMap, x, y, 1, 0, step,
                    x + step, y, x + step, y + step,
                    eastZ, southZ, tileSize, xFacingColor);
                AppendSourceEdge(worldMap, x, y, 0, 1, step,
                    x + step, y + step, x, y + step,
                    southZ, westZ, tileSize, yFacingColor);
                break;
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
        Color color)
    {
        int neighbourX = x + neighbourDx * step;
        int neighbourY = y + neighbourDy * step;
        int neighbourLayer = worldMap.FindTopOccupiedLayer(
            neighbourX, neighbourY, worldMap.LayerCount - 1);
        float neighbourHeight = neighbourLayer < 0
            ? 0f
            : worldMap.Blocks.GetSurfaceLevel(neighbourX, neighbourY);

        float bottomHeightA = MathF.Min(topHeightA, neighbourHeight);
        float bottomHeightB = MathF.Min(topHeightB, neighbourHeight);
        if (topHeightA - bottomHeightA < 0.01f &&
            topHeightB - bottomHeightB < 0.01f)
            return;

        Vector2f topA = ProjectLevelPoint(worldMap, ax, ay, topHeightA, tileSize);
        Vector2f topB = ProjectLevelPoint(worldMap, bx, by, topHeightB, tileSize);
        Vector2f bottomA = ProjectLevelPoint(worldMap, ax, ay, bottomHeightA, tileSize);
        Vector2f bottomB = ProjectLevelPoint(worldMap, bx, by, bottomHeightB, tileSize);
        AppendQuad(_blockFaces, topA, topB, bottomB, bottomA, color);

        float seamStart = MathF.Max(bottomHeightA, bottomHeightB);
        float seamEnd = MathF.Min(topHeightA, topHeightB);
        for (float height = MathF.Floor(seamStart) + 1f;
             height < seamEnd - 0.01f;
             height += 1f)
        {
            AppendLine(
                _blockSeams,
                ProjectLevelPoint(worldMap, ax, ay, height, tileSize),
                ProjectLevelPoint(worldMap, bx, by, height, tileSize),
                new Color(18, 22, 25, 190));
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

    private static void AppendLine(
        VertexArray vertices,
        Vector2f a,
        Vector2f b,
        Color color)
    {
        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(b, color));
    }

    private static Color GetTopColor(
        ushort materialId,
        float heightMeters,
        BlockTransitionType transition)
    {
        if (transition == BlockTransitionType.Ramp)
            return new Color(157, 132, 91, 255);

        int shade = Math.Clamp(103 + (int)(heightMeters * 0.35f), 92, 132);
        int materialTint = materialId == 1 ? 0 : (materialId % 3) * 5;
        return new Color(
            (byte)Math.Clamp(shade + materialTint, 0, 255),
            (byte)Math.Clamp(shade + 3, 0, 255),
            (byte)Math.Clamp(shade + 6 - materialTint, 0, 255),
            255);
    }

    private static Vector2f Project(
        float x,
        float y,
        float heightMeters,
        float tileSize) =>
        new(
            (x - y) * tileSize * 0.5f,
            (x + y) * tileSize * 0.25f - heightMeters * tileSize * 0.5f);

    private Vector2f ProjectLevelPoint(
        WorldMap worldMap,
        float x,
        float y,
        float heightMeters,
        float tileSize)
    {
        RotateLevelPoint(
            x, y,
            worldMap.TileWidth * 0.5f,
            worldMap.TileHeight * 0.5f,
            _rotationStep,
            out float rotatedX,
            out float rotatedY);

        return new Vector2f(
            (rotatedX - rotatedY) * tileSize * 0.5f,
            (rotatedX + rotatedY) * tileSize * 0.25f -
                heightMeters * tileSize * 0.5f);
    }

    private static void RotateLevelPoint(
        float x,
        float y,
        float centerX,
        float centerY,
        int rotationStep,
        out float rotatedX,
        out float rotatedY)
    {
        float dx = x - centerX;
        float dy = y - centerY;
        switch (rotationStep & 3)
        {
            case 1:
                rotatedX = centerX - dy;
                rotatedY = centerY + dx;
                break;
            case 2:
                rotatedX = centerX - dx;
                rotatedY = centerY - dy;
                break;
            case 3:
                rotatedX = centerX + dy;
                rotatedY = centerY - dx;
                break;
            default:
                rotatedX = x;
                rotatedY = y;
                break;
        }
    }

    private static float GetHighestHeightMeters(WorldMap worldMap)
    {
        int highestLayer = worldMap.GetHighestOccupiedLayer();
        return highestLayer < 0 ? 0f : (highestLayer + 1f) * 0.1f;
    }

    private static void GetVisibleTileBounds(
        View view,
        float halfWidth,
        float quarterHeight,
        float verticalScale,
        float highestMeters,
        WorldMap worldMap,
        int rotationStep,
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
                float projectedY = baseY / quarterHeight;
                float rotatedX = (projectedX + projectedY) * 0.5f;
                float rotatedY = (projectedY - projectedX) * 0.5f;
                float centerX = worldMap.TileWidth * 0.5f;
                float centerY = worldMap.TileHeight * 0.5f;
                float dx = rotatedX - centerX;
                float dy = rotatedY - centerY;

                float x;
                float y;
                switch (rotationStep & 3)
                {
                    case 1:
                        x = centerX + dy;
                        y = centerY - dx;
                        break;
                    case 2:
                        x = centerX - dx;
                        y = centerY - dy;
                        break;
                    case 3:
                        x = centerX - dy;
                        y = centerY + dx;
                        break;
                    default:
                        x = rotatedX;
                        y = rotatedY;
                        break;
                }

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
        _blockSeams.Dispose();
    }
}
