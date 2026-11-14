using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Lightweight pawn silhouettes for the block-terrain isometric preview.
/// Uses a small set of primitive shapes rather than full anatomical rigs.
/// </summary>
public sealed class IsometricUnitPreviewRenderer : IDisposable
{
    private const int EllipseSegments = 12;
    private readonly VertexArray _vertices = new(PrimitiveType.Triangles);
    private readonly List<PreviewUnit> _units = new(512);

    private float _centerX;
    private float _centerY;
    private float _halfTileSize;
    private float _groundDepthScale;
    private float _verticalScale;
    private float _rotationCosine;
    private float _rotationSine;
    private float _pixelsPerTile;

    private struct PreviewUnit
    {
        public Vector2f Center;
        public Vector2f Forward;
        public Vector2f Side;
        public Color Clothing;
        public float Depth;
        public float Length;
        public float Width;
        public float Scale;
        public bool Selected;
    }

    public void Draw(
        RenderWindow window,
        UnitSimulation simulation,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float azimuthDegrees,
        float elevationDegrees,
        UnitId selectedUnit,
        HashSet<UnitId>? selectedUnits)
    {
        if (tilePixelSize <= 0f)
            return;

        PrepareProjection(
            window, worldMap, cameraView, tilePixelSize,
            azimuthDegrees, elevationDegrees);

        UnitStore units = simulation.Units;
        Vector3[] positions = units.Position;
        Vector3[] facing = units.BodyNormal;
        Vector3[] velocities = units.Velocity;
        float[] widths = units.Width;
        float[] lengths = units.Length;
        float[] hitPoints = simulation.Health.OverallHitPoints;
        ushort[] factions = units.FactionTag;
        ReadOnlySpan<int> active = units.ActiveIndices;

        _units.Clear();
        for (int i = 0; i < active.Length; i++)
        {
            int index = active[i];
            UnitId id = units.GetId(index);
            AddUnit(
                positions[index],
                facing[index],
                velocities[index],
                widths[index],
                lengths[index],
                factions[index],
                hitPoints[index],
                selectedUnits?.Contains(id) == true || id.Equals(selectedUnit),
                cameraView);
        }

        DrawCollected(window);
    }

    public void DrawSnapshot(
        RenderWindow window,
        RenderUnitState[] units,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float azimuthDegrees,
        float elevationDegrees,
        UnitId selectedUnit,
        HashSet<UnitId>? selectedUnits)
    {
        if (tilePixelSize <= 0f || units.Length == 0)
            return;

        PrepareProjection(
            window, worldMap, cameraView, tilePixelSize,
            azimuthDegrees, elevationDegrees);

        _units.Clear();
        for (int i = 0; i < units.Length; i++)
        {
            RenderUnitState unit = units[i];
            UnitId id = new(unit.Index, unit.Generation);
            AddUnit(
                unit.Position,
                unit.BodyNormal,
                unit.Velocity,
                unit.Width,
                unit.Length,
                unit.FactionTag,
                unit.HitPoints,
                selectedUnits?.Contains(id) == true || id.Equals(selectedUnit),
                cameraView);
        }

        DrawCollected(window);
    }

    private void PrepareProjection(
        RenderWindow window,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        float azimuthDegrees,
        float elevationDegrees)
    {
        float tileSize = MathF.Max(1f, tilePixelSize * 2f);
        float elevation = Math.Clamp(
            float.IsFinite(elevationDegrees) ? elevationDegrees : 30f,
            15f,
            90f) * (MathF.PI / 180f);
        float radians = float.IsFinite(azimuthDegrees)
            ? azimuthDegrees * (MathF.PI / 180f)
            : 0f;

        _centerX = worldMap.TileWidth * 0.5f;
        _centerY = worldMap.TileHeight * 0.5f;
        _halfTileSize = tileSize * 0.5f;
        _groundDepthScale = _halfTileSize * MathF.Sin(elevation);
        _verticalScale =
            _halfTileSize * MathF.Cos(elevation) / MathF.Cos(MathF.PI / 6f);
        _rotationCosine = MathF.Cos(radians);
        _rotationSine = MathF.Sin(radians);
        _pixelsPerTile = tileSize * window.Size.X /
            MathF.Max(1f, cameraView.Size.X);
    }

    private void AddUnit(
        Vector3 position,
        Vector3 bodyNormal,
        Vector3 velocity,
        float width,
        float length,
        ushort factionTag,
        float hitPoints,
        bool selected,
        View cameraView)
    {
        if (hitPoints <= 0f ||
            !float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
        {
            return;
        }

        Vector2f center = ProjectPosition(position);
        float marginX = cameraView.Size.X * 0.58f;
        float marginY = cameraView.Size.Y * 0.58f;
        if (center.X < cameraView.Center.X - marginX ||
            center.X > cameraView.Center.X + marginX ||
            center.Y < cameraView.Center.Y - marginY ||
            center.Y > cameraView.Center.Y + marginY)
        {
            return;
        }

        float facingX = bodyNormal.X;
        float facingY = bodyNormal.Y;
        float facingLengthSquared = facingX * facingX + facingY * facingY;
        if (facingLengthSquared < 0.0001f)
        {
            facingX = velocity.X;
            facingY = velocity.Y;
            facingLengthSquared = facingX * facingX + facingY * facingY;
        }

        if (facingLengthSquared < 0.0001f)
        {
            facingX = 1f;
            facingY = 0f;
        }
        else
        {
            float inverseLength = 1f / MathF.Sqrt(facingLengthSquared);
            facingX *= inverseLength;
            facingY *= inverseLength;
        }

        Vector2f forward = ProjectDirection(facingX, facingY);
        Vector2f side = ProjectDirection(-facingY, facingX);
        float forwardLength = Length(forward);
        float sideLength = Length(side);
        if (forwardLength < 0.0001f || sideLength < 0.0001f)
            return;

        forward = Scale(forward, 1f / forwardLength);
        side = Scale(side, 1f / sideLength);

        float footprintLength = Math.Clamp(MathF.Max(width, length), 0.75f, 1.2f);
        float footprintWidth = Math.Clamp(MathF.Min(width, length), 0.55f, 1.1f);
        float bodyLength = footprintLength * 0.92f;
        float bodyWidth = footprintWidth * 0.44f;
        float projectedScale = MathF.Sqrt(forwardLength * sideLength);

        float dx = position.X - _centerX;
        float dy = position.Y - _centerY;
        float rotatedX = _rotationCosine * dx - _rotationSine * dy;
        float rotatedY = _rotationSine * dx + _rotationCosine * dy;

        _units.Add(new PreviewUnit
        {
            Center = center,
            Forward = forward,
            Side = side,
            Clothing = GetFactionColor(factionTag),
            Depth = rotatedX + rotatedY,
            Length = bodyLength,
            Width = bodyWidth,
            Scale = projectedScale,
            Selected = selected
        });
    }

    private void DrawCollected(RenderWindow window)
    {
        if (_units.Count == 0)
        {
            _vertices.Clear();
            return;
        }

        _units.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
        _vertices.Clear();

        for (int i = 0; i < _units.Count; i++)
            AppendPerson(_vertices, _units[i], _pixelsPerTile);

        if (_vertices.VertexCount > 0)
            window.Draw(_vertices);
    }

    private static void AppendPerson(
        VertexArray vertices,
        PreviewUnit unit,
        float pixelsPerTile)
    {
        Vector2f forward = unit.Forward;
        Vector2f side = unit.Side;
        float bodyLength = unit.Length;
        float bodyWidth = unit.Width;
        float scale = unit.Scale;
        Vector2f center = unit.Center;

        Color outline = new(25, 27, 30, 245);
        Color clothing = unit.Clothing;
        Color clothingShade = Shade(clothing, 0.62f);
        Color clothingLight = Shade(clothing, 1.22f);
        Color skin = new(205, 166, 133, 255);
        Color skinShade = new(142, 104, 80, 255);
        Color boots = new(38, 42, 46, 255);

        Vector2f shadowCenter = new(center.X, center.Y + scale * 0.045f);
        AppendEllipse(
            vertices, shadowCenter,
            Scale(forward, bodyLength * 0.58f),
            Scale(side, bodyWidth * 0.76f),
            new Color(0, 0, 0, 72));

        if (unit.Selected)
        {
            AppendEllipseOutline(
                vertices,
                center,
                Scale(forward, bodyLength * 0.66f),
                Scale(side, bodyWidth * 1.02f),
                MathF.Max(0.025f, scale * 0.035f),
                new Color(255, 238, 115, 240));
        }

        Vector2f hip = BodyPoint(center, forward, side, -bodyLength * 0.14f, 0f);
        Vector2f leftFoot = BodyPoint(center, forward, side, -bodyLength * 0.48f, bodyWidth * 0.34f);
        Vector2f rightFoot = BodyPoint(center, forward, side, -bodyLength * 0.48f, -bodyWidth * 0.34f);

        AppendLimb(vertices, hip, leftFoot, scale, boots, outline);
        AppendLimb(vertices, hip, rightFoot, scale, boots, outline);

        Vector2f leftShoulder = BodyPoint(center, forward, side, bodyLength * 0.20f, bodyWidth * 0.34f);
        Vector2f leftHand = BodyPoint(center, forward, side, bodyLength * 0.02f, bodyWidth * 0.72f);
        Vector2f rightShoulder = BodyPoint(center, forward, side, bodyLength * 0.20f, -bodyWidth * 0.34f);
        Vector2f rightHand = BodyPoint(center, forward, side, bodyLength * 0.02f, -bodyWidth * 0.72f);

        AppendLimb(vertices, leftShoulder, leftHand, scale, clothingShade, outline);
        AppendLimb(vertices, rightShoulder, rightHand, scale, clothingShade, outline);

        Vector2f torsoCenter = BodyPoint(center, forward, side, bodyLength * 0.015f, 0f);
        AppendEllipse(
            vertices,
            torsoCenter,
            Scale(forward, bodyLength * 0.39f),
            Scale(side, bodyWidth * 0.63f),
            outline);
        AppendEllipse(
            vertices,
            torsoCenter,
            Scale(forward, bodyLength * 0.33f),
            Scale(side, bodyWidth * 0.53f),
            clothing);
        AppendEllipse(
            vertices,
            BodyPoint(torsoCenter, forward, side, 0f, bodyWidth * 0.15f),
            Scale(forward, bodyLength * 0.15f),
            Scale(side, bodyWidth * 0.26f),
            clothingLight);

        Vector2f headCenter = BodyPoint(center, forward, side, bodyLength * 0.43f, 0f);
        AppendEllipse(
            vertices,
            BodyPoint(headCenter, forward, side, -bodyLength * 0.035f, 0f),
            Scale(forward, bodyWidth * 0.43f),
            Scale(side, bodyWidth * 0.43f),
            outline);
        AppendEllipse(
            vertices,
            BodyPoint(headCenter, forward, side, -bodyLength * 0.06f, 0f),
            Scale(forward, bodyWidth * 0.34f),
            Scale(side, bodyWidth * 0.35f),
            clothingShade);
        AppendEllipse(
            vertices,
            BodyPoint(headCenter, forward, side, bodyLength * 0.005f, 0f),
            Scale(forward, bodyWidth * 0.29f),
            Scale(side, bodyWidth * 0.30f),
            skin);
        AppendEllipse(
            vertices,
            BodyPoint(headCenter, forward, side, bodyLength * 0.07f, 0f),
            Scale(forward, bodyWidth * 0.055f),
            Scale(side, bodyWidth * 0.055f),
            skinShade);

        if (pixelsPerTile < 3f)
            return;

        AppendEllipse(
            vertices, leftHand,
            Scale(forward, scale * 0.035f),
            Scale(side, scale * 0.035f),
            skin);
        AppendEllipse(
            vertices, rightHand,
            Scale(forward, scale * 0.035f),
            Scale(side, scale * 0.035f),
            skin);
    }

    private static Vector2f BodyPoint(
        Vector2f origin,
        Vector2f forward,
        Vector2f side,
        float alongForward,
        float alongSide) =>
        Add(origin, Add(Scale(forward, alongForward), Scale(side, alongSide)));

    private static void AppendLimb(
        VertexArray vertices,
        Vector2f start,
        Vector2f end,
        float projectedScale,
        Color fill,
        Color outline)
    {
        float thickness = MathF.Max(0.035f, projectedScale * 0.075f);
        AppendCapsule(vertices, start, end, thickness * 1.55f, outline);
        AppendCapsule(vertices, start, end, thickness, fill);
    }

    private static void AppendCapsule(
        VertexArray vertices,
        Vector2f start,
        Vector2f end,
        float thickness,
        Color color)
    {
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.0001f)
            return;

        float half = thickness * 0.5f;
        float offsetX = -dy / length * half;
        float offsetY = dx / length * half;
        Vector2f a = new(start.X + offsetX, start.Y + offsetY);
        Vector2f b = new(end.X + offsetX, end.Y + offsetY);
        Vector2f c = new(end.X - offsetX, end.Y - offsetY);
        Vector2f d = new(start.X - offsetX, start.Y - offsetY);
        AppendQuad(vertices, a, b, c, d, color);
    }

    private static void AppendEllipse(
        VertexArray vertices,
        Vector2f center,
        Vector2f axisA,
        Vector2f axisB,
        Color color)
    {
        for (int i = 0; i < EllipseSegments; i++)
        {
            float a0 = i * MathF.Tau / EllipseSegments;
            float a1 = (i + 1) * MathF.Tau / EllipseSegments;
            Vector2f p0 = Add(
                center,
                Add(Scale(axisA, MathF.Cos(a0)), Scale(axisB, MathF.Sin(a0))));
            Vector2f p1 = Add(
                center,
                Add(Scale(axisA, MathF.Cos(a1)), Scale(axisB, MathF.Sin(a1))));
            AppendTriangle(vertices, center, p0, p1, color);
        }
    }

    private static void AppendEllipseOutline(
        VertexArray vertices,
        Vector2f center,
        Vector2f axisA,
        Vector2f axisB,
        float thickness,
        Color color)
    {
        Vector2f previous = Add(center, axisA);
        for (int i = 1; i <= EllipseSegments; i++)
        {
            float angle = i * MathF.Tau / EllipseSegments;
            Vector2f next = Add(
                center,
                Add(Scale(axisA, MathF.Cos(angle)), Scale(axisB, MathF.Sin(angle))));
            AppendCapsule(vertices, previous, next, thickness, color);
            previous = next;
        }
    }

    private Vector2f ProjectPosition(Vector3 position)
    {
        float dx = position.X - _centerX;
        float dy = position.Y - _centerY;
        float rotatedX = _centerX + _rotationCosine * dx - _rotationSine * dy;
        float rotatedY = _centerY + _rotationSine * dx + _rotationCosine * dy;
        return new Vector2f(
            (rotatedX - rotatedY) * _halfTileSize,
            (rotatedX + rotatedY) * _groundDepthScale -
                position.Z * _verticalScale);
    }

    private Vector2f ProjectDirection(float x, float y)
    {
        float rotatedX = _rotationCosine * x - _rotationSine * y;
        float rotatedY = _rotationSine * x + _rotationCosine * y;
        return new Vector2f(
            (rotatedX - rotatedY) * _halfTileSize,
            (rotatedX + rotatedY) * _groundDepthScale);
    }

    private static Vector2f Add(Vector2f a, Vector2f b) =>
        new(a.X + b.X, a.Y + b.Y);

    private static Vector2f Scale(Vector2f value, float scale) =>
        new(value.X * scale, value.Y * scale);

    private static float Length(Vector2f value) =>
        MathF.Sqrt(value.X * value.X + value.Y * value.Y);

    private static Color GetFactionColor(ushort factionTag) =>
        factionTag switch
        {
            1 => new Color(75, 155, 218, 255),
            2 => new Color(202, 82, 75, 255),
            3 => new Color(205, 166, 76, 255),
            _ => new Color(145, 153, 163, 255)
        };

    private static Color Shade(Color color, float amount) =>
        new(
            (byte)Math.Clamp((int)(color.R * amount), 0, 255),
            (byte)Math.Clamp((int)(color.G * amount), 0, 255),
            (byte)Math.Clamp((int)(color.B * amount), 0, 255),
            color.A);

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

    public void Dispose()
    {
        _vertices.Dispose();
    }
}
