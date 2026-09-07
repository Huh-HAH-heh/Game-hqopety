using System;
using Core.Items;
using Core.Map;
using Core.Structs;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class MapRenderSystem
{
    private readonly VertexArray _mapVertices =
        new VertexArray(PrimitiveType.Triangles);

    private readonly VertexArray _heightVertices =
        new VertexArray(PrimitiveType.Triangles);

    private readonly VertexArray _heightDebugVertices =
        new VertexArray(PrimitiveType.Triangles);

    private readonly VertexArray _gridVertices =
        new VertexArray(PrimitiveType.Lines);

    private const byte GridAlpha = 90;
    private const float HeightPixelsPerLevel = 1.5f;
    private const int MaxVisualHeight = 32;
    private const float SideDarkness = 0.60f;

    public MapRenderSystem(float microCellPixelSize)
    {
    }

    public void Draw(
        RenderWindow window,
        WorldMap worldMap,
        EdificeStore edificeStore,
        View cameraView,
        float regionPixelSize,
        float microCellPixelSize,
        float zoomLevel,
        bool showGrid,
        bool showHeightDebug)
    {
        int currentZ = worldMap.CurrentViewZ;

        MapLayer currentLayer =
            worldMap.GetLayer(currentZ);

        if (currentLayer == null)
            return;

        Vector2f center = cameraView.Center;
        Vector2f size = cameraView.Size;

        float screenMinX = center.X - size.X * 0.5f;
        float screenMaxX = center.X + size.X * 0.5f;
        float screenMinY = center.Y - size.Y * 0.5f;
        float screenMaxY = center.Y + size.Y * 0.5f;

        int lodStep = 1;
        bool drawDetails = true;

        if (zoomLevel >= 4.5f)
        {
            lodStep = 3;
            drawDetails = false;
        }
        else if (zoomLevel >= 2.5f)
        {
            lodStep = 2;
            drawDetails = false;
        }

        float currentTilePixelSize =
            microCellPixelSize * lodStep;

        float safeSize =
            MathF.Ceiling(currentTilePixelSize) + 0.35f;

        _mapVertices.Clear();
        _heightVertices.Clear();
        _heightDebugVertices.Clear();

        if (showGrid)
            _gridVertices.Clear();

        int maxCoord = GetMaxCoord();

        int minCellX = Math.Max(
            0,
            (int)(screenMinX / microCellPixelSize) - lodStep);

        int maxCellX = Math.Min(
            maxCoord,
            (int)(screenMaxX / microCellPixelSize) + lodStep);

        int minCellY = Math.Max(
            0,
            (int)(screenMinY / microCellPixelSize) - lodStep);

        int maxCellY = Math.Min(
            maxCoord,
            (int)(screenMaxY / microCellPixelSize) + lodStep);

        minCellX = (minCellX / lodStep) * lodStep;
        minCellY = (minCellY / lodStep) * lodStep;

        for (int y = minCellY; y <= maxCellY; y += lodStep)
        {
            for (int x = minCellX; x <= maxCellX; x += lodStep)
            {
                ref MicroCell cell =
                    ref currentLayer.GetMicroCell(x, y);

                float px = MathF.Floor(
                    x * microCellPixelSize);

                float py = MathF.Floor(
                    y * microCellPixelSize);

                float heightOffset =
                    GetVisualHeight(cell.Height);

                float topY =
                    py - heightOffset;

                if (TryGetEdificeColor(
                        cell,
                        edificeStore,
                        out Color edificeColor))
                {
                    AppendQuad(
                        _mapVertices,
                        px,
                        topY,
                        safeSize,
                        edificeColor);
                }
                else
                {
                    Color floorColor =
                        GetFloorColor(cell);

                    floorColor =
                        ApplyHeightLight(
                            floorColor,
                            cell.Height);

                    AppendQuad(
                        _mapVertices,
                        px,
                        topY,
                        safeSize,
                        floorColor);

                    if (drawDetails &&
                        (cell.Flags & 0x0002) != 0)
                    {
                        AppendQuad(
                            _mapVertices,
                            px,
                            topY,
                            microCellPixelSize + 0.1f,
                            new Color(
                                185,
                                15,
                                25,
                                220));
                    }
                }

                AppendHeightSides(
                    currentLayer,
                    x,
                    y,
                    px,
                    py,
                    safeSize,
                    cell.Height,
                    lodStep,
                    heightOffset,
                    maxCoord);

                if (showGrid)
                {
                    AppendGrid(
                        _gridVertices,
                        px,
                        topY,
                        safeSize);
                }

                if (showHeightDebug)
                {
                    AppendHeightDebug(
                        currentLayer,
                        x,
                        y,
                        px,
                        topY,
                        safeSize,
                        cell.Height,
                        lodStep,
                        _heightDebugVertices,
                        maxCoord);
                }
            }
        }

        // Боковые грани сначала, чтобы верхние поверхности их перекрывали.
        if (_heightVertices.VertexCount > 0)
            window.Draw(_heightVertices);

        if (_mapVertices.VertexCount > 0)
            window.Draw(_mapVertices);

        if (showHeightDebug &&
            _heightDebugVertices.VertexCount > 0)
        {
            window.Draw(_heightDebugVertices);
        }

        if (showGrid &&
            _gridVertices.VertexCount > 0)
        {
            window.Draw(_gridVertices);
        }
    }

    private static void AppendHeightDebug(
        MapLayer layer,
        int x,
        int y,
        float px,
        float topY,
        float size,
        byte currentHeight,
        int lodStep,
        VertexArray vertices,
        int maxCoord)
    {
        int height = (int)currentHeight;
        int maxDifference = 0;

        if (x + lodStep <= maxCoord)
        {
            ref MicroCell east =
                ref layer.GetMicroCell(x + lodStep, y);

            int difference = Math.Abs(
                height - (int)east.Height);

            if (difference > maxDifference)
                maxDifference = difference;
        }

        if (x - lodStep >= 0)
        {
            ref MicroCell west =
                ref layer.GetMicroCell(x - lodStep, y);

            int difference = Math.Abs(
                height - (int)west.Height);

            if (difference > maxDifference)
                maxDifference = difference;
        }

        if (y + lodStep <= maxCoord)
        {
            ref MicroCell south =
                ref layer.GetMicroCell(x, y + lodStep);

            int difference = Math.Abs(
                height - (int)south.Height);

            if (difference > maxDifference)
                maxDifference = difference;
        }

        if (y - lodStep >= 0)
        {
            ref MicroCell north =
                ref layer.GetMicroCell(x, y - lodStep);

            int difference = Math.Abs(
                height - (int)north.Height);

            if (difference > maxDifference)
                maxDifference = difference;
        }

        if (maxDifference == 0)
            return;

        byte alpha;

        if (maxDifference >= 3)
            alpha = 180;
        else if (maxDifference == 2)
            alpha = 120;
        else
            alpha = 80;

        Color color = new Color(
            255,
            170,
            0,
            alpha);

        AppendQuad(
            vertices,
            px,
            topY,
            size,
            color);
    }

    private void AppendHeightSides(
        MapLayer layer,
        int x,
        int y,
        float px,
        float py,
        float size,
        byte height,
        int lodStep,
        float heightOffset,
        int maxCoord)
    {
        int currentHeight = Math.Min(
            (int)height,
            MaxVisualHeight);

        if (currentHeight <= 0)
            return;

        // EAST
        if (x + lodStep <= maxCoord)
        {
            ref MicroCell neighbour =
                ref layer.GetMicroCell(
                    x + lodStep,
                    y);

            int neighbourHeight = Math.Min(
                (int)neighbour.Height,
                MaxVisualHeight);

            if (currentHeight > neighbourHeight)
            {
                float neighbourOffset =
                    neighbourHeight * HeightPixelsPerLevel;

                float visibleHeight =
                    heightOffset - neighbourOffset;

                if (visibleHeight > 0.01f)
                {
                    AppendVerticalSide(
                        _heightVertices,
                        px + size - 0.15f,
                        py - heightOffset,
                        visibleHeight,
                        GetSideColor(GetFloorColor(neighbour)));
                }
            }
        }

        // SOUTH
        if (y + lodStep <= maxCoord)
        {
            ref MicroCell neighbour =
                ref layer.GetMicroCell(
                    x,
                    y + lodStep);

            int neighbourHeight = Math.Min(
                (int)neighbour.Height,
                MaxVisualHeight);

            if (currentHeight > neighbourHeight)
            {
                float neighbourOffset =
                    neighbourHeight * HeightPixelsPerLevel;

                float visibleHeight =
                    heightOffset - neighbourOffset;

                if (visibleHeight > 0.01f)
                {
                    AppendHorizontalSide(
                        _heightVertices,
                        px,
                        py + size - 0.15f,
                        size,
                        visibleHeight,
                        GetSideColor(GetFloorColor(neighbour)));
                }
            }
        }
    }

    private static void AppendVerticalSide(
        VertexArray vertices,
        float x,
        float topY,
        float height,
        Color color)
    {
        float right = x + 0.8f;
        float bottomY = topY + height;

        Vector2f a = new(x, topY);
        Vector2f b = new(right, topY);
        Vector2f c = new(right, bottomY);
        Vector2f d = new(x, bottomY);

        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(b, color));
        vertices.Append(new Vertex(c, color));
        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(c, color));
        vertices.Append(new Vertex(d, color));
    }

    private static void AppendHorizontalSide(
        VertexArray vertices,
        float x,
        float y,
        float width,
        float height,
        Color color)
    {
        float bottomY = y + height;

        Vector2f a = new(x, y);
        Vector2f b = new(x + width, y);
        Vector2f c = new(x + width, bottomY);
        Vector2f d = new(x, bottomY);

        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(b, color));
        vertices.Append(new Vertex(c, color));
        vertices.Append(new Vertex(a, color));
        vertices.Append(new Vertex(c, color));
        vertices.Append(new Vertex(d, color));
    }

    private static Color GetSideColor(Color baseColor)
    {
        return new Color(
            ClampByte(baseColor.R * SideDarkness),
            ClampByte(baseColor.G * SideDarkness),
            ClampByte(baseColor.B * SideDarkness),
            255);
    }

    private static bool TryGetEdificeColor(
        MicroCell cell,
        EdificeStore edificeStore,
        out Color color)
    {
        color = default;

        if (cell.EdificeId == 0 ||
            edificeStore == null)
        {
            return false;
        }

        ushort id = cell.EdificeId;

        if (id >= edificeStore.Instances.Length)
            return false;

        var instance = edificeStore.Instances[id];

        if (instance.ConfigId >= edificeStore.Configs.Length)
            return false;

        var config = edificeStore.Configs[instance.ConfigId];

        if (config == null)
            return false;

        color = new Color(85, 85, 90);

        if (config.Type == EdificeType.Wall)
        {
            color = config.CoverEffectiveness >= 1.0f
                ? new Color(225, 225, 230)
                : new Color(120, 125, 130);
        }
        else if (config.Type == EdificeType.Generator)
        {
            color = new Color(65, 65, 70);
        }

        return true;
    }

    private static Color GetFloorColor(MicroCell cell)
    {
        if (cell.FloorId == 2)
            return new Color(42, 44, 46);

        if (cell.FloorId == 1)
            return new Color(38, 44, 65);

        return new Color(20, 21, 24);
    }

    private static Color ApplyHeightLight(
        Color color,
        byte height)
    {
        int h = Math.Min(
            (int)height,
            MaxVisualHeight);

        float factor =
            1.0f + MathF.Min(
                h * 0.015f,
                0.30f);

        return new Color(
            ClampByte(color.R * factor),
            ClampByte(color.G * factor),
            ClampByte(color.B * factor),
            color.A);
    }

    private static float GetVisualHeight(byte height)
    {
        int h = Math.Min(
            (int)height,
            MaxVisualHeight);

        return h * HeightPixelsPerLevel;
    }

    private static void AppendQuad(
        VertexArray vertices,
        float x,
        float y,
        float size,
        Color color)
    {
        float right = x + size;
        float bottom = y + size;

        Vector2f topLeft = new(x, y);
        Vector2f topRight = new(right, y);
        Vector2f bottomRight = new(right, bottom);
        Vector2f bottomLeft = new(x, bottom);

        vertices.Append(new Vertex(topLeft, color));
        vertices.Append(new Vertex(topRight, color));
        vertices.Append(new Vertex(bottomRight, color));

        vertices.Append(new Vertex(topLeft, color));
        vertices.Append(new Vertex(bottomRight, color));
        vertices.Append(new Vertex(bottomLeft, color));
    }

    private static void AppendGrid(
        VertexArray vertices,
        float x,
        float y,
        float size)
    {
        Color color = new Color(
            50,
            55,
            70,
            GridAlpha);

        float right = x + size;
        float bottom = y + size;

        vertices.Append(new Vertex(new Vector2f(x, y), color));
        vertices.Append(new Vertex(new Vector2f(right, y), color));

        vertices.Append(new Vertex(new Vector2f(right, y), color));
        vertices.Append(new Vertex(new Vector2f(right, bottom), color));

        vertices.Append(new Vertex(new Vector2f(right, bottom), color));
        vertices.Append(new Vertex(new Vector2f(x, bottom), color));

        vertices.Append(new Vertex(new Vector2f(x, bottom), color));
        vertices.Append(new Vertex(new Vector2f(x, y), color));
    }

    private static byte ClampByte(float value)
    {
        if (value <= 0f)
            return 0;

        if (value >= 255f)
            return 255;

        return (byte)value;
    }

    private static int GetMaxCoord()
    {
        return MapRegion.MicroSize * 16 - 1;
    }
}
