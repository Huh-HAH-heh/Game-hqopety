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

    public readonly struct OcclusionUnit
    {
        public readonly float X, Y, Z, FacingX, FacingY, Width, Length, Height;
        public readonly ushort FactionTag;

        public OcclusionUnit(
            float x, float y, float z, float facingX, float facingY,
            float width, float length, float height, ushort factionTag)
        {
            X = x; Y = y; Z = z;
            FacingX = facingX; FacingY = facingY;
            Width = width; Length = length; Height = height;
            FactionTag = factionTag;
        }
    }

    private sealed class OcclusionBatch : IDisposable
    {
        public float Depth;
        public readonly VertexArray Faces = new(PrimitiveType.Triangles);
        public void Dispose() => Faces.Dispose();
    }

    private readonly struct DepthPrimitive
    {
        public readonly float Depth;
        public readonly Vector2f A, B, C, D;
        public readonly Color Color;
        public readonly int Sequence;

        public DepthPrimitive(float depth, Vector2f a, Vector2f b,
            Vector2f c, Vector2f d, Color color, int sequence)
        {
            Depth = depth;
            A = a; B = b; C = c; D = d;
            Color = color;
            Sequence = sequence;
        }
    }

    private readonly struct OcclusionFace
    {
        public readonly Vector2f A, B, C, D;
        public readonly float Depth, MinX, MinY, MaxX, MaxY;

        public OcclusionFace(Vector2f a, Vector2f b, Vector2f c, Vector2f d, float depth)
        {
            A = a; B = b; C = c; D = d; Depth = depth;
            MinX = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X));
            MinY = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y));
            MaxX = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
            MaxY = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
        }
    }

    private readonly List<BlockDrawItem> _visibleBlocks = new(4096);
    private readonly List<OcclusionBatch> _occlusionBatchPool = new(128);
    private readonly List<OcclusionBatch> _activeOcclusionBatches = new(128);
    private readonly Dictionary<int, OcclusionBatch> _occlusionBatchLookup = new();
    private readonly List<OcclusionFace> _occlusionFaces = new(2048);
    private readonly List<DepthPrimitive> _depthPrimitives = new(32768);
    private int _nextPrimitiveSequence;
    private readonly VertexArray _xrayFill = new(PrimitiveType.Triangles);
    private readonly VertexArray _xrayHatch = new(PrimitiveType.Triangles);
    private float _projectionTileSize, _projectionGroundDepthScale, _projectionVerticalScale;
    private float _depthPlanarScale = 1f;
    private float _depthVerticalScale;
    private int _occlusionBucketSize = 1;
    private int _nextOcclusionBatch;
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

    public void Draw(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float cameraAzimuthDegrees,
        float elevationDegrees)
    {
        if (!worldMap.UsesDiscreteBlockTerrain || tilePixelSize <= 0f)
            return;

        _nextOcclusionBatch = 0;

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
        _projectionTileSize = tileSize;
        _projectionGroundDepthScale = groundDepthScale;
        _projectionVerticalScale = verticalScale;
        _depthPlanarScale =
            MathF.Cos(elevationRadians) / MathF.Cos(MathF.PI / 6f);
        _depthVerticalScale = MathF.Sin(elevationRadians);
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
        // Keep roughly 2-3 screen pixels per smallest terrain patch.
        // Detail depends only on zoom: rotation must not change surface geometry
        // or make walls overlap larger top patches.
        int minimumDetailSize = GetMinimumDetailSize(pixelsPerTile);

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
            BeginOcclusionBatchBuild(minimumDetailSize);

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
            int firstBlockX = minX / MaximumAdaptiveBlockSize * MaximumAdaptiveBlockSize;
            int firstBlockY = minY / MaximumAdaptiveBlockSize * MaximumAdaptiveBlockSize;
            for (int y = firstBlockY; y <= maxY; y += MaximumAdaptiveBlockSize)
            {
                for (int x = firstBlockX; x <= maxX; x += MaximumAdaptiveBlockSize)
                {
                    CollectAdaptiveBlocks(
                        worldMap, x, y, MaximumAdaptiveBlockSize,
                        minimumDetailSize);
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

                // Fine 1 m detail is kept in the terrain silhouette and shading.
                // Drawing a full outline around every tiny patch creates many
                // duplicate edge quads, so the visible grid starts at the 4 m block.
                bool drawBlockOutline = drawBlockGrid;

                // Top faces and vertical faces must share the same depth queue.
                // Drawing every top face first lets distant walls overwrite nearer terrain.
                Color topColor = GetTopColor(block.MaterialId, height);
                QueueDepthQuad(block.Depth, north, east, south, west, topColor);

                if (drawBlockOutline)
                {
                    // Draw the 4 m construction grid even when LOD has split
                    // the top into 1 m cells. Each grid edge is emitted once.
                    if (y % BlockTerrain.HorizontalBlockSize == 0)
                        QueueDepthLine(GetCameraDepth(x + size * 0.5f, y, height), north, east, seamColor);
                    if (x % BlockTerrain.HorizontalBlockSize == 0)
                        QueueDepthLine(GetCameraDepth(x, y + size * 0.5f, height), west, north, seamColor);

                    if (x + size >= worldMap.TileWidth)
                        QueueDepthLine(GetCameraDepth(x + size, y + size * 0.5f, height), east, south, seamColor);
                    if (y + size >= worldMap.TileHeight)
                        QueueDepthLine(GetCameraDepth(x + size * 0.5f, y + size, height), south, west, seamColor);
                }

                // Emit the top patch first, then its camera-facing wall
                // segments. Both enter the same depth queue in back-to-front order.
                AppendVisibleSideFaces(
                    worldMap,
                    x, y, size,
                    tileSize,
                    groundDepthScale,
                    verticalScale,
                    drawBlockOutline);
            }

            FlushDepthPrimitives();
            _activeOcclusionBatches.Sort(static (left, right) =>
                left.Depth.CompareTo(right.Depth));
            _occlusionFaces.Sort(static (left, right) =>
                left.Depth.CompareTo(right.Depth));

            _cachedTerrainVersion = worldMap.TerrainVersion;
            _cachedCameraAzimuthDegrees = cameraAzimuthDegrees;
            _cachedElevationDegrees = elevationDegrees;
            _cachedLodStep = minimumDetailSize;
            _cachedTileSize = tileSize;
            _cachedCameraCenter = cameraView.Center;
            _cachedViewSize = cameraView.Size;
            _cacheValid = true;
        }

        // All terrain faces are queued by camera depth and consumed together
        // with units through DrawOccludersBeforeDepth / DrawRemainingOccluders.
    }

    public bool HasOccludersBeforeDepth(float depth) =>
        _nextOcclusionBatch < _activeOcclusionBatches.Count &&
        _activeOcclusionBatches[_nextOcclusionBatch].Depth <= depth;

    public void DrawOccludersBeforeDepth(RenderWindow window, float depth)
    {
        while (_nextOcclusionBatch < _activeOcclusionBatches.Count)
        {
            OcclusionBatch batch = _activeOcclusionBatches[_nextOcclusionBatch];
            if (batch.Depth > depth)
                break;

            if (batch.Faces.VertexCount > 0)
                window.Draw(batch.Faces);

            _nextOcclusionBatch++;
        }
    }

    public void DrawRemainingOccluders(RenderWindow window)
    {
        while (_nextOcclusionBatch < _activeOcclusionBatches.Count)
        {
            OcclusionBatch batch = _activeOcclusionBatches[_nextOcclusionBatch++];
            if (batch.Faces.VertexCount > 0)
                window.Draw(batch.Faces);
        }
    }

    private void BeginOcclusionBatchBuild(int bucketSize)
    {
        // Surface samples and wall segments are addressed on the 1 m grid.
        // Keep painter-order bands at that same resolution regardless of LOD.
        _occlusionBucketSize = 1;
        _activeOcclusionBatches.Clear();
        _occlusionBatchLookup.Clear();
        _occlusionFaces.Clear();
        _depthPrimitives.Clear();
        _nextPrimitiveSequence = 0;
        _nextOcclusionBatch = 0;
    }

    private void QueueDepthQuad(
        float depth, Vector2f a, Vector2f b, Vector2f c, Vector2f d, Color color)
    {
        _depthPrimitives.Add(new DepthPrimitive(
            depth, a, b, c, d, color, _nextPrimitiveSequence++));
    }

    private void QueueDepthLine(float depth, Vector2f a, Vector2f b, Color color)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= 0.001f)
            return;

        const float halfThickness = 0.55f;
        float offsetX = -dy / length * halfThickness;
        float offsetY = dx / length * halfThickness;
        QueueDepthQuad(
            depth,
            new Vector2f(a.X + offsetX, a.Y + offsetY),
            new Vector2f(b.X + offsetX, b.Y + offsetY),
            new Vector2f(b.X - offsetX, b.Y - offsetY),
            new Vector2f(a.X - offsetX, a.Y - offsetY),
            color);
    }

    private void FlushDepthPrimitives()
    {
        _depthPrimitives.Sort(static (left, right) =>
        {
            int depthOrder = left.Depth.CompareTo(right.Depth);
            return depthOrder != 0
                ? depthOrder
                : left.Sequence.CompareTo(right.Sequence);
        });

        for (int i = 0; i < _depthPrimitives.Count; i++)
        {
            DepthPrimitive primitive = _depthPrimitives[i];
            AppendQuad(
                GetOcclusionFaces(primitive.Depth),
                primitive.A, primitive.B, primitive.C, primitive.D,
                primitive.Color);
        }
    }

    private VertexArray GetOcclusionFaces(float depth)
    {
        int bucket = (int)MathF.Floor(depth / _occlusionBucketSize);
        if (_occlusionBatchLookup.TryGetValue(bucket, out OcclusionBatch? existing))
            return existing.Faces;

        int poolIndex = _activeOcclusionBatches.Count;
        OcclusionBatch batch;
        if (poolIndex < _occlusionBatchPool.Count)
        {
            batch = _occlusionBatchPool[poolIndex];
            batch.Faces.Clear();
        }
        else
        {
            batch = new OcclusionBatch();
            _occlusionBatchPool.Add(batch);
        }

        batch.Depth = (bucket + 0.5f) * _occlusionBucketSize;
        _activeOcclusionBatches.Add(batch);
        _occlusionBatchLookup.Add(bucket, batch);
        return batch.Faces;
    }

    private float GetCameraDepth(float x, float y, float heightMeters)
    {
        float rotatedX = _rotationCosine * x - _rotationSine * y;
        float rotatedY = _rotationSine * x + _rotationCosine * y;
        float planarDepth = rotatedX + rotatedY;
        return planarDepth * _depthPlanarScale +
            heightMeters * _depthVerticalScale;
    }


    private int FindFirstOcclusionFaceAfterDepth(float depth)
    {
        int low = 0;
        int high = _occlusionFaces.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_occlusionFaces[middle].Depth <= depth)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    public void DrawHiddenUnitHatching(
        RenderWindow window,
        WorldMap worldMap,
        IReadOnlyList<OcclusionUnit> units,
        View cameraView)
    {
        _xrayFill.Clear();
        _xrayHatch.Clear();
        if (units.Count == 0 || _occlusionFaces.Count == 0)
            return;

        float pixelsPerViewUnit = window.Size.X / MathF.Max(1f, cameraView.Size.X);
        float spacing = 8f / MathF.Max(0.01f, pixelsPerViewUnit);
        float thickness = 0.65f / MathF.Max(0.01f, pixelsPerViewUnit);

        Span<Vector2f> corners = stackalloc Vector2f[8];
        Span<Vector2f> hull = stackalloc Vector2f[16];
        Span<Vector2f> overlap = stackalloc Vector2f[24];
        Span<Vector2f> wall = stackalloc Vector2f[4];

        for (int u = 0; u < units.Count; u++)
        {
            OcclusionUnit unit = units[u];
            float facingLength = MathF.Sqrt(unit.FacingX * unit.FacingX + unit.FacingY * unit.FacingY);
            float fx = facingLength > 0.0001f ? unit.FacingX / facingLength : 1f;
            float fy = facingLength > 0.0001f ? unit.FacingY / facingLength : 0f;
            float sx = -fy, sy = fx;
            float halfLength = MathF.Max(0.16f, MathF.Max(unit.Length, unit.Width) * 0.34f);
            float halfWidth = MathF.Max(0.14f, MathF.Min(unit.Length, unit.Width) * 0.42f);
            float bodyHeight = Math.Clamp(
                float.IsFinite(unit.Height) && unit.Height > 0.3f ? unit.Height : 1.6f,
                0.45f, 3.2f);
            float unitDepth = GetCameraDepth(
                unit.X,
                unit.Y,
                unit.Z + bodyHeight * 0.5f);

            int corner = 0;
            for (int zIndex = 0; zIndex < 2; zIndex++)
            {
                float z = unit.Z + (zIndex == 0 ? 0f : bodyHeight);
                for (int along = -1; along <= 1; along += 2)
                for (int across = -1; across <= 1; across += 2)
                {
                    float x = unit.X + fx * halfLength * along + sx * halfWidth * across;
                    float y = unit.Y + fy * halfLength * along + sy * halfWidth * across;
                    corners[corner++] = ProjectLevelPoint(
                        worldMap, x, y, z, _projectionTileSize,
                        _projectionGroundDepthScale, _projectionVerticalScale);
                }
            }

            int hullCount = BuildConvexHull(corners, hull);
            if (hullCount < 3)
                continue;

            GetBounds(hull[..hullCount],
                out float minX, out float minY, out float maxX, out float maxY);
            Color faction = GetOcclusionFactionColor(unit.FactionTag);

            int firstFace = FindFirstOcclusionFaceAfterDepth(unitDepth + 0.025f);
            for (int faceIndex = firstFace; faceIndex < _occlusionFaces.Count; faceIndex++)
            {
                OcclusionFace face = _occlusionFaces[faceIndex];
                if (face.Depth <= unitDepth + 0.025f ||
                    face.MaxX < minX || face.MinX > maxX ||
                    face.MaxY < minY || face.MinY > maxY)
                    continue;

                wall[0] = face.A; wall[1] = face.B; wall[2] = face.C; wall[3] = face.D;
                int overlapCount = ClipConvexPolygon(hull[..hullCount], wall, overlap);
                if (overlapCount < 3)
                    continue;

                AppendPolygonFill(_xrayFill, overlap[..overlapCount],
                    new Color(faction.R, faction.G, faction.B, 48));
                AppendHatch(_xrayHatch, overlap[..overlapCount], spacing, thickness,
                    new Color(faction.R, faction.G, faction.B, 178));
            }
        }

        if (_xrayFill.VertexCount > 0)
            window.Draw(_xrayFill);
        if (_xrayHatch.VertexCount > 0)
            window.Draw(_xrayHatch);
    }

    private static int BuildConvexHull(Span<Vector2f> points, Span<Vector2f> hull)
    {
        for (int i = 1; i < points.Length; i++)
        {
            Vector2f value = points[i];
            int j = i - 1;
            while (j >= 0 && (points[j].X > value.X ||
                (MathF.Abs(points[j].X - value.X) < 0.0001f && points[j].Y > value.Y)))
            { points[j + 1] = points[j]; j--; }
            points[j + 1] = value;
        }

        int count = 0;
        for (int i = 0; i < points.Length; i++)
        {
            while (count >= 2 && Cross(hull[count - 2], hull[count - 1], points[i]) <= 0f)
                count--;
            hull[count++] = points[i];
        }

        int lowerLimit = count + 1;
        for (int i = points.Length - 2; i >= 0; i--)
        {
            while (count >= lowerLimit && Cross(hull[count - 2], hull[count - 1], points[i]) <= 0f)
                count--;
            hull[count++] = points[i];
        }
        return Math.Max(0, count - 1);
    }

    private static int ClipConvexPolygon(
        ReadOnlySpan<Vector2f> subject,
        ReadOnlySpan<Vector2f> clip,
        Span<Vector2f> result)
    {
        Span<Vector2f> input = stackalloc Vector2f[24];
        Span<Vector2f> output = stackalloc Vector2f[24];
        subject.CopyTo(input);
        int inputCount = subject.Length;
        float winding = SignedArea(clip) >= 0f ? 1f : -1f;

        for (int edge = 0; edge < clip.Length && inputCount > 0; edge++)
        {
            Vector2f a = clip[edge], b = clip[(edge + 1) % clip.Length];
            int outputCount = 0;
            Vector2f previous = input[inputCount - 1];
            float previousDistance = Cross(a, b, previous) * winding;
            bool previousInside = previousDistance >= -0.001f;

            for (int i = 0; i < inputCount; i++)
            {
                Vector2f current = input[i];
                float currentDistance = Cross(a, b, current) * winding;
                bool currentInside = currentDistance >= -0.001f;
                if (currentInside != previousInside)
                {
                    float denominator = previousDistance - currentDistance;
                    if (MathF.Abs(denominator) > 0.000001f && outputCount < output.Length)
                    {
                        float t = Math.Clamp(previousDistance / denominator, 0f, 1f);
                        output[outputCount++] = new Vector2f(
                            previous.X + (current.X - previous.X) * t,
                            previous.Y + (current.Y - previous.Y) * t);
                    }
                }
                if (currentInside && outputCount < output.Length)
                    output[outputCount++] = current;
                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }

            Span<Vector2f> swap = input;
            input = output;
            output = swap;
            inputCount = outputCount;
        }

        int count = Math.Min(inputCount, result.Length);
        input[..count].CopyTo(result);
        return count;
    }

    private static void AppendPolygonFill(
        VertexArray vertices, ReadOnlySpan<Vector2f> polygon, Color color)
    {
        for (int i = 1; i < polygon.Length - 1; i++)
        {
            vertices.Append(new Vertex(polygon[0], color));
            vertices.Append(new Vertex(polygon[i], color));
            vertices.Append(new Vertex(polygon[i + 1], color));
        }
    }

    private static void AppendHatch(
        VertexArray vertices, ReadOnlySpan<Vector2f> polygon,
        float spacing, float halfWidth, Color color)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < polygon.Length; i++)
        {
            float value = polygon[i].X + polygon[i].Y;
            min = MathF.Min(min, value); max = MathF.Max(max, value);
        }

        Span<Vector2f> intersections = stackalloc Vector2f[8];
        for (float line = MathF.Ceiling(min / spacing) * spacing; line <= max; line += spacing)
        {
            int count = 0;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2f a = polygon[i], b = polygon[(i + 1) % polygon.Length];
                float da = a.X + a.Y - line, db = b.X + b.Y - line;
                if (MathF.Abs(da) < 0.0001f)
                    AddUniquePoint(intersections, ref count, a);
                if (da * db < -0.000001f)
                {
                    float t = da / (da - db);
                    AddUniquePoint(intersections, ref count, new Vector2f(
                        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                }
            }
            if (count < 2) continue;

            int first = 0, second = 1;
            float longest = DistanceSquared(intersections[0], intersections[1]);
            for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                float length = DistanceSquared(intersections[i], intersections[j]);
                if (length > longest) { longest = length; first = i; second = j; }
            }
            AppendHatchSegment(vertices, intersections[first], intersections[second], halfWidth, color);
        }
    }

    private static void AddUniquePoint(Span<Vector2f> points, ref int count, Vector2f point)
    {
        for (int i = 0; i < count; i++)
            if (DistanceSquared(points[i], point) < 0.0001f) return;
        if (count < points.Length) points[count++] = point;
    }

    private static void AppendHatchSegment(
        VertexArray vertices, Vector2f a, Vector2f b, float halfWidth, Color color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.0001f) return;
        float ox = -dy / length * halfWidth, oy = dx / length * halfWidth;
        AppendQuad(vertices,
            new Vector2f(a.X + ox, a.Y + oy),
            new Vector2f(b.X + ox, b.Y + oy),
            new Vector2f(b.X - ox, b.Y - oy),
            new Vector2f(a.X - ox, a.Y - oy), color);
    }

    private static void GetBounds(
        ReadOnlySpan<Vector2f> points,
        out float minX, out float minY, out float maxX, out float maxY)
    {
        minX = minY = float.MaxValue;
        maxX = maxY = float.MinValue;
        for (int i = 0; i < points.Length; i++)
        {
            minX = MathF.Min(minX, points[i].X);
            minY = MathF.Min(minY, points[i].Y);
            maxX = MathF.Max(maxX, points[i].X);
            maxY = MathF.Max(maxY, points[i].Y);
        }
    }

    private static float SignedArea(ReadOnlySpan<Vector2f> polygon)
    {
        float area = 0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2f a = polygon[i], b = polygon[(i + 1) % polygon.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }

    private static float Cross(Vector2f a, Vector2f b, Vector2f c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static float DistanceSquared(Vector2f a, Vector2f b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static Color GetOcclusionFactionColor(ushort factionTag) =>
        factionTag switch
        {
            1 => new Color(74, 166, 238, 255),
            2 => new Color(232, 98, 92, 255),
            3 => new Color(235, 190, 84, 255),
            _ => new Color(180, 192, 205, 255)
        };

    private static int GetMinimumDetailSize(float pixelsPerTile) =>
        pixelsPerTile switch
        {
            >= 3f => MinimumBlockSize,
            >= 1.5f => 2,
            >= 0.75f => BlockTerrain.HorizontalBlockSize,
            _ => MaximumAdaptiveBlockSize
        };

    private void CollectAdaptiveBlocks(
        WorldMap worldMap,
        int x,
        int y,
        int size,
        int minimumDetailSize)
    {
        if (x >= worldMap.TileWidth || y >= worldMap.TileHeight)
            return;

        if (TryGetUniformPatch(
                worldMap, x, y, size, minimumDetailSize,
                out int surfaceLevel, out ushort materialId))
        {
            if (surfaceLevel > 0 && materialId != 0)
                AddVisibleBlock(x, y, size, surfaceLevel, materialId);
            return;
        }

        if (size <= MinimumBlockSize)
        {
            // Mixed patches are split down to the actual 1 m sample. Picking the
            // maximum value creates false plateaus, cliffs and long parallel walls.
            surfaceLevel = worldMap.Blocks.GetSurfaceLevel(x, y);
            materialId = worldMap.Blocks.GetMaterialId(x, y);
            if (surfaceLevel > 0 && materialId != 0)
                AddVisibleBlock(x, y, 1, surfaceLevel, materialId);
            return;
        }

        int half = size >> 1;
        CollectAdaptiveBlocks(worldMap, x, y, half, minimumDetailSize);
        CollectAdaptiveBlocks(worldMap, x + half, y, half, minimumDetailSize);
        CollectAdaptiveBlocks(worldMap, x, y + half, half, minimumDetailSize);
        CollectAdaptiveBlocks(worldMap, x + half, y + half, half, minimumDetailSize);
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
        if (size > minimumDetailSize ||
            size > BlockTerrain.HorizontalBlockSize ||
            x + size > worldMap.TileWidth ||
            y + size > worldMap.TileHeight)
        {
            return false;
        }

        BlockTerrain blocks = worldMap.Blocks;
        surfaceLevel = blocks.GetSurfaceLevel(x, y);
        materialId = blocks.GetMaterialId(x, y);

        for (int tileY = y; tileY < y + size; tileY++)
        {
            for (int tileX = x; tileX < x + size; tileX++)
            {
                if (blocks.GetSurfaceLevel(tileX, tileY) != surfaceLevel ||
                    blocks.GetMaterialId(tileX, tileY) != materialId ||
                    (size > 1 && blocks.IsTransitionCell(tileX, tileY)))
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
        ushort materialId)
    {
        // Use absolute map coordinates, matching wall and unit depth keys.
        float centerX = x + size * 0.5f;
        float centerY = y + size * 0.5f;
        float rotatedX = _rotationCosine * centerX - _rotationSine * centerY;
        float rotatedY = _rotationSine * centerX + _rotationCosine * centerY;

        _visibleBlocks.Add(new BlockDrawItem
        {
            X = x,
            Y = y,
            Size = size,
            Height = surfaceLevel,
            MaterialId = materialId,
            Depth = (rotatedX + rotatedY) * _depthPlanarScale +
                surfaceLevel * _depthVerticalScale
        });
    }

    private void AppendVisibleSideFaces(
        WorldMap worldMap,
        int patchX,
        int patchY,
        int patchSize,
        float tileSize,
        float groundDepthScale,
        float verticalScale,
        bool drawBlockGrid)
    {
        const float edgeOnThreshold = 0.025f;
        float xDepthDirection = _rotationCosine + _rotationSine;
        float yDepthDirection = _rotationCosine - _rotationSine;
        bool showXSide = MathF.Abs(xDepthDirection) > edgeOnThreshold;
        bool showYSide = MathF.Abs(yDepthDirection) > edgeOnThreshold;

        Color xFacingColor = new Color(55, 61, 65, 255);
        Color yFacingColor = new Color(39, 45, 49, 255);
        BlockTerrain blocks = worldMap.Blocks;
        int endX = Math.Min(worldMap.TileWidth, patchX + patchSize);
        int endY = Math.Min(worldMap.TileHeight, patchY + patchSize);

        // Only the two camera-facing borders of this patch can contribute
        // visible walls. Split each border into exact 1 m adjacency segments.
        for (int offset = 0; offset < patchSize; offset++)
        {
            if (showXSide)
            {
                int segmentY = patchY + offset;
                int segmentX = xDepthDirection > 0f ? endX - 1 : patchX;
                if (segmentY < endY && segmentX >= patchX && segmentX < endX)
                {
                    int height = blocks.GetSurfaceLevel(segmentX, segmentY);
                    if (height > 0 && blocks.GetMaterialId(segmentX, segmentY) != 0)
                    {
                        if (xDepthDirection > 0f)
                        {
                            AppendSourceEdge(
                                worldMap, segmentX, segmentY, 1, 0, 1,
                                endX, segmentY, endX, segmentY + 1f,
                                height, height,
                                tileSize, groundDepthScale, verticalScale,
                                drawBlockGrid, 1, xFacingColor);
                        }
                        else
                        {
                            AppendSourceEdge(
                                worldMap, segmentX, segmentY, -1, 0, 1,
                                patchX, segmentY + 1f, patchX, segmentY,
                                height, height,
                                tileSize, groundDepthScale, verticalScale,
                                drawBlockGrid, 1, xFacingColor);
                        }
                    }
                }
            }

            if (showYSide)
            {
                int segmentX = patchX + offset;
                int segmentY = yDepthDirection > 0f ? endY - 1 : patchY;
                if (segmentX < endX && segmentY >= patchY && segmentY < endY)
                {
                    int height = blocks.GetSurfaceLevel(segmentX, segmentY);
                    if (height > 0 && blocks.GetMaterialId(segmentX, segmentY) != 0)
                    {
                        if (yDepthDirection > 0f)
                        {
                            AppendSourceEdge(
                                worldMap, segmentX, segmentY, 0, 1, 1,
                                segmentX + 1f, endY, segmentX, endY,
                                height, height,
                                tileSize, groundDepthScale, verticalScale,
                                drawBlockGrid, 1, yFacingColor);
                        }
                        else
                        {
                            AppendSourceEdge(
                                worldMap, segmentX, segmentY, 0, -1, 1,
                                segmentX, patchY, segmentX + 1f, patchY,
                                height, height,
                                tileSize, groundDepthScale, verticalScale,
                                drawBlockGrid, 1, yFacingColor);
                        }
                    }
                }
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
        int verticalGroupHeight,
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

        // Keep one full polygon for X-ray clipping, but render the actual wall
        // as 1 m vertical bands. A single 40-50 m quad has a huge depth range;
        // sorting it by its centroid makes it cover terrain that is in front.
        float faceCenterHeight =
            (topHeightA + topHeightB + bottomHeightA + bottomHeightB) * 0.25f;
        float edgeCenterX = (ax + bx) * 0.5f;
        float edgeCenterY = (ay + by) * 0.5f;
        float edgeDepth = GetCameraDepth(
            edgeCenterX,
            edgeCenterY,
            faceCenterHeight);
        _occlusionFaces.Add(new OcclusionFace(
            topA, topB, bottomB, bottomA, edgeDepth));

        float maximumWallHeight = MathF.Max(
            topHeightA - bottomHeightA,
            topHeightB - bottomHeightB);
        int verticalSegments = Math.Max(
            1,
            (int)MathF.Ceiling(maximumWallHeight));

        for (int segment = 0; segment < verticalSegments; segment++)
        {
            float t0 = segment / (float)verticalSegments;
            float t1 = (segment + 1f) / verticalSegments;

            float lowerA = bottomHeightA + (topHeightA - bottomHeightA) * t0;
            float lowerB = bottomHeightB + (topHeightB - bottomHeightB) * t0;
            float upperA = bottomHeightA + (topHeightA - bottomHeightA) * t1;
            float upperB = bottomHeightB + (topHeightB - bottomHeightB) * t1;

            Vector2f bandTopA = ProjectLevelPoint(
                worldMap, ax, ay, upperA, tileSize,
                groundDepthScale, verticalScale);
            Vector2f bandTopB = ProjectLevelPoint(
                worldMap, bx, by, upperB, tileSize,
                groundDepthScale, verticalScale);
            Vector2f bandBottomA = ProjectLevelPoint(
                worldMap, ax, ay, lowerA, tileSize,
                groundDepthScale, verticalScale);
            Vector2f bandBottomB = ProjectLevelPoint(
                worldMap, bx, by, lowerB, tileSize,
                groundDepthScale, verticalScale);

            float bandCenterHeight =
                (upperA + upperB + lowerA + lowerB) * 0.25f;
            float bandDepth = GetCameraDepth(
                edgeCenterX,
                edgeCenterY,
                bandCenterHeight);
            QueueDepthQuad(
                bandDepth,
                bandTopA,
                bandTopB,
                bandBottomB,
                bandBottomA,
                color);
        }

        if (drawBlockGrid)
        {
            float seamStart = MathF.Max(bottomHeightA, bottomHeightB);
            float seamEnd = MathF.Min(topHeightA, topHeightB);
            float groupHeight = Math.Max(1, verticalGroupHeight);
            float firstSeam = (MathF.Floor(seamStart / groupHeight) + 1f) * groupHeight;
            for (float height = firstSeam;
                 height < seamEnd - 0.01f;
                 height += groupHeight)
            {
                QueueDepthLine(
                    GetCameraDepth((ax + bx) * 0.5f, (ay + by) * 0.5f, height),
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
            float localX = (corner & 1) == 0 ? -view.Size.X : view.Size.X;
            float localY = (corner & 2) == 0 ? -view.Size.Y : view.Size.Y;
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
        _xrayFill.Dispose();
        _xrayHatch.Dispose();
        for (int i = 0; i < _occlusionBatchPool.Count; i++)
            _occlusionBatchPool[i].Dispose();

        _occlusionBatchPool.Clear();
        _activeOcclusionBatches.Clear();
        _occlusionBatchLookup.Clear();
    }
}
