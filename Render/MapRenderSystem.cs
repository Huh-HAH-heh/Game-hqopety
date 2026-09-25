using Core.Map;
using SFML.Graphics;
using SFML.System;
using System;

namespace RimClone.Render;

public sealed class MapRenderSystem
{
    private readonly VertexArray _mapVertices =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _gridVertices =
        new VertexArray(
            PrimitiveType.Lines);

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
        bool showGrid)
    {
        if (tilePixelSize <= 0f)
            return;

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

        _mapVertices.Clear();

        if (showGrid)
            _gridVertices.Clear();

        int lodStep = 1;

        if (zoomLevel >= 4.5f)
        {
            lodStep = 3;
        }
        else if (zoomLevel >= 2.5f)
        {
            lodStep = 2;
        }

        int minTileX =
            Math.Max(
                0,
                (int)MathF.Floor(
                    screenMinX /
                    tilePixelSize) -
                lodStep);

        int maxTileX =
            Math.Min(
                worldMap.MaxTileX,
                (int)MathF.Ceiling(
                    screenMaxX /
                    tilePixelSize) +
                lodStep);

        int minTileY =
            Math.Max(
                0,
                (int)MathF.Floor(
                    screenMinY /
                    tilePixelSize) -
                lodStep);

        int maxTileY =
            Math.Min(
                worldMap.MaxTileY,
                (int)MathF.Ceiling(
                    screenMaxY /
                    tilePixelSize) +
                lodStep);

        minTileX =
            (minTileX / lodStep) *
            lodStep;

        minTileY =
            (minTileY / lodStep) *
            lodStep;

        for (int y = minTileY;
             y <= maxTileY;
             y += lodStep)
        {
            for (int x = minTileX;
                 x <= maxTileX;
                 x += lodStep)
            {
                ushort height =
                    worldMap.GetSurfaceHeightUnits(
                        x,
                        y);

                if (height == 0)
                    continue;

                float left =
                    x * tilePixelSize;

                float top =
                    y * tilePixelSize;

                float right =
                    (x + lodStep) *
                    tilePixelSize;

                float bottom =
                    (y + lodStep) *
                    tilePixelSize;

                AppendQuad(
                    _mapVertices,
                    left,
                    top,
                    right,
                    bottom,
                    GetTerrainColor(height));

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

        if (_mapVertices.VertexCount > 0)
        {
            window.Draw(
                _mapVertices);
        }

        if (showGrid &&
            _gridVertices.VertexCount > 0)
        {
            window.Draw(
                _gridVertices);
        }
    }

    private static void AppendQuad(
        VertexArray vertices,
        float left,
        float top,
        float right,
        float bottom,
        Color color)
    {
        Vector2f topLeft =
            new(left, top);

        Vector2f topRight =
            new(right, top);

        Vector2f bottomRight =
            new(right, bottom);

        Vector2f bottomLeft =
            new(left, bottom);

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

    private static Color GetTerrainColor(
        ushort heightUnits)
    {
        float h =
            heightUnits * 0.1f;

        const float h0 = 0f;
        const float h1 = 4f;
        const float h2 = 9f;
        const float h3 = 15f;
        const float h4 = 22f;
        const float h5 = 32f;

        Color c0 =
            new Color(8, 9, 10);

        Color c1 =
            new Color(13, 15, 16);

        Color c2 =
            new Color(19, 22, 21);

        Color c3 =
            new Color(27, 30, 27);

        Color c4 =
            new Color(39, 39, 34);

        Color c5 =
            new Color(54, 52, 44);

        if (h <= h1)
        {
            return LerpColor(
                c0,
                c1,
                SmoothStep(
                    h0,
                    h1,
                    h));
        }

        if (h <= h2)
        {
            return LerpColor(
                c1,
                c2,
                SmoothStep(
                    h1,
                    h2,
                    h));
        }

        if (h <= h3)
        {
            return LerpColor(
                c2,
                c3,
                SmoothStep(
                    h2,
                    h3,
                    h));
        }

        if (h <= h4)
        {
            return LerpColor(
                c3,
                c4,
                SmoothStep(
                    h3,
                    h4,
                    h));
        }

        return LerpColor(
            c4,
            c5,
            SmoothStep(
                h4,
                h5,
                h));
    }

    private static Color LerpColor(
        Color a,
        Color b,
        float t)
    {
        t =
            Math.Clamp(
                t,
                0f,
                1f);

        return new Color(
            (byte)(a.R +
                   (b.R - a.R) * t),
            (byte)(a.G +
                   (b.G - a.G) * t),
            (byte)(a.B +
                   (b.B - a.B) * t),
            255);
    }

    private static float SmoothStep(
        float min,
        float max,
        float value)
    {
        float t =
            Math.Clamp(
                (value - min) /
                (max - min),
                0f,
                1f);

        return
            t * t *
            (3f - 2f * t);
    }
}
