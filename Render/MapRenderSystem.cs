using System;
using Core.Map;
using Core.Structs;
using Core.Items;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render
{
    public sealed class MapRenderSystem
    {
        // ============================================================
        // BATCHED RENDERING
        // ============================================================

        // Один VertexArray содержит всю видимую карту.
        // Вместо сотен тысяч Draw() будет один Draw().
        private readonly VertexArray _mapVertices =
          new VertexArray(PrimitiveType.Triangles);

        // Отдельный batch для debug-grid.
        private readonly VertexArray _gridVertices =
            new VertexArray(PrimitiveType.Lines);

        private const float GridAlpha = 90f;

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
            int currentZ =
                worldMap.CurrentViewZ;

            MapLayer currentLayer =
                worldMap.GetLayer(currentZ);

            if (currentLayer == null)
                return;

            Vector2f center =
                cameraView.Center;

            Vector2f size =
                cameraView.Size;

            float screenMinX =
                center.X - size.X * 0.5f;

            float screenMaxX =
                center.X + size.X * 0.5f;

            float screenMinY =
                center.Y - size.Y * 0.5f;

            float screenMaxY =
                center.Y + size.Y * 0.5f;

            // ========================================================
            // LOD
            // ========================================================

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
                MathF.Ceiling(
                    currentTilePixelSize) + 0.35f;

            // ========================================================
            // CLEAR PREVIOUS FRAME
            // ========================================================

            _mapVertices.Clear();

            if (showGrid)
                _gridVertices.Clear();

            // ========================================================
            // VISIBLE BOUNDS
            // ========================================================

            int maxCoord =
                (16 * 48) - 1;

            int minCellX =
                Math.Max(
                    0,
                    (int)(screenMinX / microCellPixelSize) -
                    lodStep);

            int maxCellX =
                Math.Min(
                    maxCoord,
                    (int)(screenMaxX / microCellPixelSize) +
                    lodStep);

            int minCellY =
                Math.Max(
                    0,
                    (int)(screenMinY / microCellPixelSize) -
                    lodStep);

            int maxCellY =
                Math.Min(
                    maxCoord,
                    (int)(screenMaxY / microCellPixelSize) +
                    lodStep);

            minCellX =
                (minCellX / lodStep) * lodStep;

            minCellY =
                (minCellY / lodStep) * lodStep;

            // ========================================================
            // BUILD BATCH
            // ========================================================

            for (
                int y = minCellY;
                y <= maxCellY;
                y += lodStep)
            {
                for (
                    int x = minCellX;
                    x <= maxCellX;
                    x += lodStep)
                {
                    ref MicroCell cell =
                        ref currentLayer.GetMicroCell(
                            x,
                            y);

                    float px =
                        MathF.Floor(
                            x * microCellPixelSize);

                    float py =
                        MathF.Floor(
                            y * microCellPixelSize);

                    // ====================================================
                    // WALL / EDIFICE
                    // ====================================================

                    if (cell.EdificeId > 0 &&
                        edificeStore != null)
                    {
                        ushort id =
                            cell.EdificeId;

                        if (id <
                            edificeStore.Instances.Length)
                        {
                            var instance =
                                edificeStore.Instances[id];

                            if (instance.ConfigId <
                                edificeStore.Configs.Length)
                            {
                                var config =
                                    edificeStore.Configs[
                                        instance.ConfigId];

                                if (config != null)
                                {
                                    Color blockColor =
                                        new Color(
                                            85,
                                            85,
                                            90);

                                    if (config.Type ==
                                        EdificeType.Wall)
                                    {
                                        blockColor =
                                            config.CoverEffectiveness >= 1.0f
                                                ? new Color(
                                                    225,
                                                    225,
                                                    230)
                                                : new Color(
                                                    120,
                                                    125,
                                                    130);
                                    }
                                    else if (
                                        config.Type ==
                                        EdificeType.Generator)
                                    {
                                        blockColor =
                                            new Color(
                                                65,
                                                65,
                                                70);
                                    }

                                    AppendQuad(
                                        _mapVertices,
                                        px,
                                        py,
                                        safeSize,
                                        blockColor);

                                    if (showGrid)
                                    {
                                        AppendGrid(
                                            _gridVertices,
                                            px,
                                            py,
                                            safeSize);
                                    }

                                    // Стена закрывает пол.
                                    continue;
                                }
                            }
                        }
                    }

                    // ====================================================
                    // FLOOR
                    // ====================================================

                    Color floorColor;

                    if (cell.FloorId == 2)
                    {
                        floorColor =
                            new Color(
                                42,
                                44,
                                46);
                    }
                    else if (cell.FloorId == 1)
                    {
                        floorColor =
                            new Color(
                                38,
                                44,
                                65);
                    }
                    else
                    {
                        floorColor =
                            new Color(
                                20,
                                21,
                                24);
                    }

                    AppendQuad(
                        _mapVertices,
                        px,
                        py,
                        safeSize,
                        floorColor);

                    // ====================================================
                    // BLOOD
                    // ====================================================

                    if (drawDetails &&
                        (cell.Flags & 0x0002) != 0)
                    {
                        AppendQuad(
                            _mapVertices,
                            px,
                            py,
                            microCellPixelSize + 0.1f,
                            new Color(
                                185,
                                15,
                                25,
                                220));
                    }

                    // ====================================================
                    // DEBUG GRID
                    // ====================================================

                    if (showGrid)
                    {
                        AppendGrid(
                            _gridVertices,
                            px,
                            py,
                            safeSize);
                    }
                }
            }

            // ============================================================
            // ACTUAL GPU DRAWS
            // ============================================================

            // ВСЯ КАРТА:
            // 100 000+ Draw() -> 1 Draw()
            if (_mapVertices.VertexCount > 0)
            {
                window.Draw(_mapVertices);
            }

            // Debug grid:
            // отдельный batch, включается только кнопкой G.
            if (showGrid &&
                _gridVertices.VertexCount > 0)
            {
                window.Draw(_gridVertices);
            }
        }

        // ============================================================
        // QUAD
        // ============================================================

        private static void AppendQuad(
            VertexArray vertices,
            float x,
            float y,
            float size,
            Color color)
        {
            float right = x + size;
            float bottom = y + size;

            Vector2f topLeft =
                new Vector2f(x, y);

            Vector2f topRight =
                new Vector2f(right, y);

            Vector2f bottomRight =
                new Vector2f(right, bottom);

            Vector2f bottomLeft =
                new Vector2f(x, bottom);

            // Triangle 1
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

            // Triangle 2
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

        // ============================================================
        // GRID
        // ============================================================

        private static void AppendGrid(
            VertexArray vertices,
            float x,
            float y,
            float size)
        {
            Color color =
                new Color(
                    50,
                    55,
                    70,
                    (byte)GridAlpha);

            float right =
                x + size;

            float bottom =
                y + size;

            // top
            vertices.Append(
                new Vertex(
                    new Vector2f(x, y),
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(right, y),
                    color));

            // right
            vertices.Append(
                new Vertex(
                    new Vector2f(right, y),
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(right, bottom),
                    color));

            // bottom
            vertices.Append(
                new Vertex(
                    new Vector2f(right, bottom),
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(x, bottom),
                    color));

            // left
            vertices.Append(
                new Vertex(
                    new Vector2f(x, bottom),
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(x, y),
                    color));
        }
    }
}