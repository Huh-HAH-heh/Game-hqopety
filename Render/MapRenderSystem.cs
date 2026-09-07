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

    private readonly VertexArray _gridVertices =
        new VertexArray(PrimitiveType.Lines);

    private const byte GridAlpha = 90;

    private const float HeightPixelsPerLevel = 1.5f;
    private const int MaxVisualHeight = 32;

    private const float SideDarkness = 0.60f;
    private const float SideWidth = 1.0f;

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
        bool showGrid)
    {
        MapLayer? layer =
            worldMap.GetLayer(
                worldMap.CurrentViewZ);

        if (layer == null ||
            microCellPixelSize <= 0f)
        {
            return;
        }

        Vector2f center =
            cameraView.Center;

        Vector2f viewSize =
            cameraView.Size;

        float screenMinX =
            center.X -
            viewSize.X * 0.5f;

        float screenMaxX =
            center.X +
            viewSize.X * 0.5f;

        float screenMinY =
            center.Y -
            viewSize.Y * 0.5f;

        float screenMaxY =
            center.Y +
            viewSize.Y * 0.5f;

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

        _mapVertices.Clear();
        _heightVertices.Clear();

        if (showGrid)
            _gridVertices.Clear();

        int maxCoord =
            MapRegion.MicroSize * 16 - 1;

        int minCellX =
            Math.Max(
                0,
                (int)MathF.Floor(
                    screenMinX /
                    microCellPixelSize) -
                lodStep);

        int maxCellX =
            Math.Min(
                maxCoord,
                (int)MathF.Ceiling(
                    screenMaxX /
                    microCellPixelSize) +
                lodStep);

        int minCellY =
            Math.Max(
                0,
                (int)MathF.Floor(
                    screenMinY /
                    microCellPixelSize) -
                lodStep);

        int maxCellY =
            Math.Min(
                maxCoord,
                (int)MathF.Ceiling(
                    screenMaxY /
                    microCellPixelSize) +
                lodStep);

        minCellX =
            (minCellX / lodStep) *
            lodStep;

        minCellY =
            (minCellY / lodStep) *
            lodStep;

        for (int y = minCellY;
             y <= maxCellY;
             y += lodStep)
        {
            for (int x = minCellX;
                 x <= maxCellX;
                 x += lodStep)
            {
                ref MicroCell cell =
                    ref layer.GetMicroCell(
                        x,
                        y);

                /*
                 * Точные границы клетки.
                 *
                 * Никаких Floor/Ceil/SafeSize.
                 */
                float left =
                    x *
                    microCellPixelSize;

                float top =
                    y *
                    microCellPixelSize;

                float right =
                    (x + lodStep) *
                    microCellPixelSize;

                float bottom =
                    (y + lodStep) *
                    microCellPixelSize;

                // ====================================================
                // TOP
                // ====================================================

                Color topColor;

                if (TryGetEdificeColor(
                        cell,
                        edificeStore,
                        out Color edificeColor))
                {
                    topColor =
                        edificeColor;
                }
                else
                {
                    topColor =
                        ApplyHeightLight(
                            GetFloorColor(cell),
                            cell.Height);
                }

                AppendQuad(
                    _mapVertices,
                    left,
                    top,
                    right,
                    bottom,
                    topColor);

                // ====================================================
                // HEIGHT
                // ====================================================

                AppendHeightSides(
                    layer,
                    x,
                    y,
                    left,
                    top,
                    right,
                    bottom,
                    cell.Height,
                    lodStep,
                    maxCoord);

                // ====================================================
                // DETAILS
                // ====================================================

                if (drawDetails &&
                    cell.EdificeId == 0 &&
                    (cell.Flags & 0x0002) != 0)
                {
                    AppendQuad(
                        _mapVertices,
                        left,
                        top,
                        right,
                        bottom,
                        new Color(
                            185,
                            15,
                            25,
                            220));
                }

                // ====================================================
                // GRID
                // ====================================================

                if (showGrid)
                {
                    AppendGrid(
                        _gridVertices,
                        left,
                        top,
                        right,
                        bottom);
                }
            }
        }

        // ------------------------------------------------------------
        // HEIGHT SIDES
        // ------------------------------------------------------------

        if (_heightVertices.VertexCount > 0)
        {
            window.Draw(
                _heightVertices);
        }

        // ------------------------------------------------------------
        // TILE TOPS
        // ------------------------------------------------------------

        if (_mapVertices.VertexCount > 0)
        {
            window.Draw(
                _mapVertices);
        }

        // ------------------------------------------------------------
        // GRID
        // ------------------------------------------------------------

        if (showGrid &&
            _gridVertices.VertexCount > 0)
        {
            window.Draw(
                _gridVertices);
        }
    }

    // ================================================================
    // HEIGHT SIDES
    // ================================================================

    private void AppendHeightSides(
        MapLayer layer,
        int x,
        int y,
        float left,
        float top,
        float right,
        float bottom,
        byte height,
        int lodStep,
        int maxCoord)
    {
        int currentHeight =
            Math.Min(
                (int)height,
                MaxVisualHeight);

        if (currentHeight <= 0)
            return;

        // ============================================================
        // EAST
        // ============================================================

        if (x + lodStep <= maxCoord)
        {
            ref MicroCell east =
                ref layer.GetMicroCell(
                    x + lodStep,
                    y);

            int eastHeight =
                Math.Min(
                    (int)east.Height,
                    MaxVisualHeight);

            if (currentHeight > eastHeight)
            {
                float visibleHeight =
                    (currentHeight -
                     eastHeight) *
                    HeightPixelsPerLevel;

                AppendVerticalSide(
                    _heightVertices,
                    right,
                    top,
                    visibleHeight,
                    GetSideColor(
                        GetFloorColor(east)));
            }
        }

        // ============================================================
        // SOUTH
        // ============================================================

        if (y + lodStep <= maxCoord)
        {
            ref MicroCell south =
                ref layer.GetMicroCell(
                    x,
                    y + lodStep);

            int southHeight =
                Math.Min(
                    (int)south.Height,
                    MaxVisualHeight);

            if (currentHeight > southHeight)
            {
                float visibleHeight =
                    (currentHeight -
                     southHeight) *
                    HeightPixelsPerLevel;

                AppendHorizontalSide(
                    _heightVertices,
                    left,
                    bottom,
                    right - left,
                    visibleHeight,
                    GetSideColor(
                        GetFloorColor(south)));
            }
        }
    }

    // ================================================================
    // EAST SIDE
    // ================================================================

    private static void AppendVerticalSide(
        VertexArray vertices,
        float x,
        float y,
        float height,
        Color color)
    {
        if (height <= 0.01f)
            return;

        float sideRight =
            x +
            SideWidth;

        float bottom =
            y +
            height;

        Vector2f a =
            new(
                x,
                y);

        Vector2f b =
            new(
                sideRight,
                y);

        Vector2f c =
            new(
                sideRight,
                bottom);

        Vector2f d =
            new(
                x,
                bottom);

        vertices.Append(
            new Vertex(
                a,
                color));

        vertices.Append(
            new Vertex(
                b,
                color));

        vertices.Append(
            new Vertex(
                c,
                color));

        vertices.Append(
            new Vertex(
                a,
                color));

        vertices.Append(
            new Vertex(
                c,
                color));

        vertices.Append(
            new Vertex(
                d,
                color));
    }

    // ================================================================
    // SOUTH SIDE
    // ================================================================

    private static void AppendHorizontalSide(
        VertexArray vertices,
        float x,
        float y,
        float width,
        float height,
        Color color)
    {
        if (height <= 0.01f)
            return;

        float bottom =
            y +
            height;

        Vector2f a =
            new(
                x,
                y);

        Vector2f b =
            new(
                x + width,
                y);

        Vector2f c =
            new(
                x + width,
                bottom);

        Vector2f d =
            new(
                x,
                bottom);

        vertices.Append(
            new Vertex(
                a,
                color));

        vertices.Append(
            new Vertex(
                b,
                color));

        vertices.Append(
            new Vertex(
                c,
                color));

        vertices.Append(
            new Vertex(
                a,
                color));

        vertices.Append(
            new Vertex(
                c,
                color));

        vertices.Append(
            new Vertex(
                d,
                color));
    }

    // ================================================================
    // EDIFICE
    // ================================================================

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

        ushort id =
            cell.EdificeId;

        if (id >=
            edificeStore.Instances.Length)
        {
            return false;
        }

        var instance =
            edificeStore.Instances[id];

        if (instance.ConfigId >=
            edificeStore.Configs.Length)
        {
            return false;
        }

        var config =
            edificeStore.Configs[
                instance.ConfigId];

        if (config == null)
            return false;

        if (config.Type ==
            EdificeType.Wall)
        {
            color =
                config.CoverEffectiveness >= 1f
                    ? new Color(
                        225,
                        225,
                        230)
                    : new Color(
                        120,
                        125,
                        130);

            return true;
        }

        if (config.Type ==
            EdificeType.Generator)
        {
            color =
                new Color(
                    65,
                    65,
                    70);

            return true;
        }

        color =
            new Color(
                85,
                85,
                90);

        return true;
    }

    // ================================================================
    // FLOOR COLOR
    // ================================================================

    private static Color GetFloorColor(
        MicroCell cell)
    {
        if (cell.FloorId == 2)
        {
            return new Color(
                42,
                44,
                46);
        }

        if (cell.FloorId == 1)
        {
            return new Color(
                38,
                44,
                65);
        }

        return new Color(
            20,
            21,
            24);
    }

    // ================================================================
    // HEIGHT LIGHTING
    // ================================================================

    private static Color ApplyHeightLight(
        Color color,
        byte height)
    {
        int h =
            Math.Min(
                (int)height,
                MaxVisualHeight);

        float factor =
            1f +
            MathF.Min(
                h * 0.015f,
                0.30f);

        return new Color(
            ClampByte(
                color.R * factor),

            ClampByte(
                color.G * factor),

            ClampByte(
                color.B * factor),

            color.A);
    }

    private static Color GetSideColor(
        Color baseColor)
    {
        return new Color(
            ClampByte(
                baseColor.R *
                SideDarkness),

            ClampByte(
                baseColor.G *
                SideDarkness),

            ClampByte(
                baseColor.B *
                SideDarkness),

            255);
    }

    // ================================================================
    // QUAD
    // ================================================================

    private static void AppendQuad(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        Color color)
    {
        Vector2f topLeft =
            new(
                left,
                top);

        Vector2f topRight =
            new(
                right,
                top);

        Vector2f bottomRight =
            new(
                right,
                bottom);

        Vector2f bottomLeft =
            new(
                left,
                bottom);

        vertices.Append(
            new Vertex(
                topLeft,
                color));

        vertices.Append(
            new Vertex(
                topRight,
                color));

        vertices.Append(
            new Vertex(
                bottomRight,
                color));

        vertices.Append(
            new Vertex(
                topLeft,
                color));

        vertices.Append(
            new Vertex(
                bottomRight,
                color));

        vertices.Append(
            new Vertex(
                bottomLeft,
                color));
    }

    // ================================================================
    // GRID
    // ================================================================

    private static void AppendGrid(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom)
    {
        Color color =
            new Color(
                50,
                55,
                70,
                GridAlpha);

        // TOP
        vertices.Append(
            new Vertex(
                new Vector2f(
                    left,
                    top),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    right,
                    top),
                color));

        // RIGHT
        vertices.Append(
            new Vertex(
                new Vector2f(
                    right,
                    top),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    right,
                    bottom),
                color));

        // BOTTOM
        vertices.Append(
            new Vertex(
                new Vector2f(
                    right,
                    bottom),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    left,
                    bottom),
                color));

        // LEFT
        vertices.Append(
            new Vertex(
                new Vector2f(
                    left,
                    bottom),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    left,
                    top),
                color));
    }

    // ================================================================
    // COLOR
    // ================================================================

    private static byte ClampByte(
        float value)
    {
        if (value <= 0f)
            return 0;

        if (value >= 255f)
            return 255;

        return (byte)value;
    }
}