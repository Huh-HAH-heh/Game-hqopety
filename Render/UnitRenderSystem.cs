using Core.Unit;
using SFML.Graphics;
using SFML.System;
using System;
using System.Numerics;

namespace RimClone.Render;

public sealed class UnitRenderSystem
{
    private const int CircleSegments = 8;

    private readonly VertexArray _vertices =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _selection =
        new VertexArray(
            PrimitiveType.Lines);

    public void Draw(
        RenderWindow window,
        UnitSimulation simulation,
        View cameraView,
        float tilePixelSize,
        UnitId selectedUnit)
    {
        if (tilePixelSize <= 0f)
            return;

        _vertices.Clear();
        _selection.Clear();

        Vector2f center =
            cameraView.Center;

        Vector2f viewSize =
            cameraView.Size;

        float minWorldX =
            (center.X -
             viewSize.X * 0.5f) /
            tilePixelSize -
            2f;

        float maxWorldX =
            (center.X +
             viewSize.X * 0.5f) /
            tilePixelSize +
            2f;

        float minWorldY =
            (center.Y -
             viewSize.Y * 0.5f) /
            tilePixelSize -
            2f;

        float maxWorldY =
            (center.Y +
             viewSize.Y * 0.5f) /
            tilePixelSize +
            2f;

        UnitStore units =
            simulation.Units;

        UnitBodyStore bodies =
            simulation.Bodies;

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        Vector3[] positions =
            units.Position;

        float[] radii =
            units.Radius;

        float[] rotations =
            units.Rotation;

        float[] viewRanges =
            units.ViewRange;

        float[] fieldOfViews =
            units.FieldOfView;

        UnitType[] types =
            units.Type;

        int[] bodyStarts =
            units.BodyStart;

        short[] bodyCounts =
            units.BodyCount;

        Vector3[] partPositions =
            bodies.WorldPosition;

        Vector3[] partScales =
            bodies.Scale;

        float[] partRotations =
            bodies.WorldRotation;

        BodyPrimitive[] primitives =
            bodies.Primitive;

        bool hasSelection =
            simulation.Units.TryGetIndex(
                selectedUnit,
                out int selectedIndex);

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unitIndex =
                active[i];

            Vector3 unitPosition =
                positions[unitIndex];

            if (unitPosition.X < minWorldX ||
                unitPosition.X > maxWorldX ||
                unitPosition.Y < minWorldY ||
                unitPosition.Y > maxWorldY)
            {
                continue;
            }

            Color color =
                GetUnitColor(
                    types[unitIndex]);

            int start =
                bodyStarts[unitIndex];

            int count =
                bodyCounts[unitIndex];

            int end =
                start + count;

            for (int part = start;
                 part < end;
                 part++)
            {
                Vector3 position =
                    partPositions[part];

                Vector3 scale =
                    partScales[part];

                float pixelX =
                    position.X *
                    tilePixelSize;

                float pixelY =
                    position.Y *
                    tilePixelSize;

                if (primitives[part] ==
                    BodyPrimitive.Circle)
                {
                    AppendCircle(
                        _vertices,
                        pixelX,
                        pixelY,
                        MathF.Max(
                            0.06f,
                            scale.X) *
                        tilePixelSize,
                        color);
                }
                else
                {
                    AppendBox(
                        _vertices,
                        pixelX,
                        pixelY,
                        MathF.Max(
                            0.06f,
                            scale.X) *
                        tilePixelSize,
                        MathF.Max(
                            0.06f,
                            scale.Y) *
                        tilePixelSize,
                        partRotations[part],
                        color);
                }
            }

            if (hasSelection &&
                selectedIndex == unitIndex)
            {
                AppendSelection(
                    _selection,
                    unitPosition.X * tilePixelSize,
                    unitPosition.Y * tilePixelSize,
                    MathF.Max(
                        radii[unitIndex],
                        0.45f) *
                    tilePixelSize);

                AppendVisionCone(
                    _selection,
                    unitPosition.X * tilePixelSize,
                    unitPosition.Y * tilePixelSize,
                    rotations[unitIndex],
                    fieldOfViews[unitIndex],
                    viewRanges[unitIndex] *
                    tilePixelSize);
            }
        }

        if (_vertices.VertexCount > 0)
        {
            window.Draw(
                _vertices);
        }

        if (_selection.VertexCount > 0)
        {
            window.Draw(
                _selection);
        }
    }

    private static Color GetUnitColor(
        UnitType type)
    {
        return type switch
        {
            UnitType.Colonist =>
                new Color(
                    220,
                    224,
                    228),

            UnitType.Greenbob =>
                new Color(
                    150,
                    178,
                    156),

            UnitType.SegmentedMonster =>
                new Color(
                    170,
                    148,
                    142),

            _ =>
                new Color(
                    200,
                    200,
                    200)
        };
    }

    private static void AppendCircle(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radius,
        Color color)
    {
        Vector2f center =
            new Vector2f(
                centerX,
                centerY);

        for (int i = 0;
             i < CircleSegments;
             i++)
        {
            float a0 =
                i *
                MathF.Tau /
                CircleSegments;

            float a1 =
                (i + 1) *
                MathF.Tau /
                CircleSegments;

            vertices.Append(
                new Vertex(
                    center,
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(
                        centerX +
                        MathF.Cos(a0) *
                        radius,
                        centerY +
                        MathF.Sin(a0) *
                        radius),
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(
                        centerX +
                        MathF.Cos(a1) *
                        radius,
                        centerY +
                        MathF.Sin(a1) *
                        radius),
                    color));
        }
    }

    private static void AppendBox(
        VertexArray vertices,
        float centerX,
        float centerY,
        float halfWidth,
        float halfHeight,
        float rotation,
        Color color)
    {
        float cos =
            MathF.Cos(rotation);

        float sin =
            MathF.Sin(rotation);

        Vector2f p0 =
            new Vector2f(
                centerX -
                halfWidth * cos +
                halfHeight * sin,
                centerY -
                halfWidth * sin -
                halfHeight * cos);

        Vector2f p1 =
            new Vector2f(
                centerX +
                halfWidth * cos +
                halfHeight * sin,
                centerY +
                halfWidth * sin -
                halfHeight * cos);

        Vector2f p2 =
            new Vector2f(
                centerX +
                halfWidth * cos -
                halfHeight * sin,
                centerY +
                halfWidth * sin +
                halfHeight * cos);

        Vector2f p3 =
            new Vector2f(
                centerX -
                halfWidth * cos -
                halfHeight * sin,
                centerY -
                halfWidth * sin +
                halfHeight * cos);

        vertices.Append(
            new Vertex(
                p0,
                color));

        vertices.Append(
            new Vertex(
                p1,
                color));

        vertices.Append(
            new Vertex(
                p2,
                color));

        vertices.Append(
            new Vertex(
                p0,
                color));

        vertices.Append(
            new Vertex(
                p2,
                color));

        vertices.Append(
            new Vertex(
                p3,
                color));
    }

    private static void AppendVisionCone(
        VertexArray vertices,
        float centerX,
        float centerY,
        float rotation,
        float fieldOfViewDegrees,
        float range)
    {
        Color color =
            new Color(
                190,
                200,
                210,
                80);

        float halfFov =
            fieldOfViewDegrees *
            MathF.PI /
            360f;

        float left =
            rotation -
            halfFov;

        float right =
            rotation +
            halfFov;

        vertices.Append(
            new Vertex(
                new Vector2f(
                    centerX,
                    centerY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    centerX +
                    MathF.Cos(left) *
                    range,
                    centerY +
                    MathF.Sin(left) *
                    range),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    centerX,
                    centerY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    centerX +
                    MathF.Cos(right) *
                    range,
                    centerY +
                    MathF.Sin(right) *
                    range),
                color));
    }

    private static void AppendSelection(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radius)
    {
        const int segments = 16;

        Color color =
            new Color(
                235,
                235,
                235,
                190);

        for (int i = 0;
             i < segments;
             i++)
        {
            float a0 =
                i *
                MathF.Tau /
                segments;

            float a1 =
                (i + 1) *
                MathF.Tau /
                segments;

            vertices.Append(
                new Vertex(
                    new Vector2f(
                        centerX +
                        MathF.Cos(a0) *
                        radius,
                        centerY +
                        MathF.Sin(a0) *
                        radius),
                    color));

            vertices.Append(
                new Vertex(
                    new Vector2f(
                        centerX +
                        MathF.Cos(a1) *
                        radius,
                        centerY +
                        MathF.Sin(a1) *
                        radius),
                    color));
        }
    }
}
