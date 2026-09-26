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

        Vector3[] bodyNormals =
            units.BodyNormal;

        Vector3[] headNormals =
            units.HeadNormal;

        float[] widths =
            units.Width;

        float[] lengths =
            units.Length;

        float[] viewRanges =
            units.ViewRange;

        float[] fieldOfViews =
            units.FieldOfView;

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

        UnitType[] types =
            units.Type;

        UnitPosture[] postures =
            units.Posture;

        BodyPrimitive[] primitives =
            bodies.Primitive;

        bool hasSelection =
            units.TryGetIndex(
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
                new Color(
                    238,
                    238,
                    238);

            if (types[unitIndex] ==
                UnitType.Colonist)
            {
                AppendHuman(
                    _vertices,
                    unitPosition,
                    widths[unitIndex],
                    lengths[unitIndex],
                    units.Height[unitIndex],
                    bodyNormals[unitIndex],
                    headNormals[unitIndex],
                    postures[unitIndex],
                    tilePixelSize,
                    color);
            }
            else
            {
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
                                0.03f,
                                scale.X) *
                            tilePixelSize,
                            MathF.Max(
                                0.03f,
                                scale.Y) *
                            tilePixelSize,
                            partRotations[part],
                            color);
                    }
                }
            }

            if (hasSelection &&
                selectedIndex == unitIndex)
            {
                float selectionRadius =
                    MathF.Max(
                        widths[unitIndex],
                        lengths[unitIndex]) * 0.6f;

                AppendSelection(
                    _selection,
                    unitPosition.X *
                    tilePixelSize,
                    unitPosition.Y *
                    tilePixelSize,
                    MathF.Max(
                        selectionRadius,
                        0.45f) *
                    tilePixelSize);

                Vector3 headNormal =
                    NormalizeHorizontal(
                        headNormals[unitIndex]);

                float headAngle =
                    MathF.Atan2(
                        headNormal.Y,
                        headNormal.X);

                AppendVisionCone(
                    _selection,
                    unitPosition.X *
                    tilePixelSize,
                    unitPosition.Y *
                    tilePixelSize,
                    headAngle,
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

    private static void AppendHuman(
        VertexArray vertices,
        Vector3 position,
        float width,
        float length,
        float height,
        Vector3 bodyNormal,
        Vector3 headNormal,
        UnitPosture posture,
        float tilePixelSize,
        Color color)
    {
        bodyNormal =
            NormalizeHorizontal(bodyNormal);

        headNormal =
            NormalizeHorizontal(headNormal);

        float x =
            position.X *
            tilePixelSize;

        float y =
            position.Y *
            tilePixelSize;

        float h =
            MathF.Max(0.20f, height) *
            tilePixelSize;

        float torsoRadius =
            MathF.Max(0.045f, width * 0.30f) *
            tilePixelSize;

        float headRadius =
            MathF.Max(0.055f, width * 0.34f) *
            tilePixelSize;

        if (posture == UnitPosture.Standing)
        {
            // The unit position is the ground/contact anchor.
            // Build one connected silhouette instead of a vertical chain
            // of independent circles. Height controls the whole body,
            // width controls its thickness.
            float pelvisY =
                y - h * 0.24f;

            float waistY =
                y - h * 0.42f;

            float shoulderY =
                y - h * 0.59f;

            float headY =
                y - h * 0.74f;

            float pelvisHalfWidth =
                MathF.Max(
                    0.045f,
                    width * 0.42f) *
                tilePixelSize;

            float waistHalfWidth =
                MathF.Max(
                    0.040f,
                    width * 0.34f) *
                tilePixelSize;

            float shoulderHalfWidth =
                MathF.Max(
                    0.055f,
                    width * 0.48f) *
                tilePixelSize;

            float legRadius =
                MathF.Max(
                    0.025f,
                    width * 0.11f) *
                tilePixelSize;

            float legSpread =
                MathF.Max(
                    legRadius,
                    width * 0.18f) *
                tilePixelSize;

            float leftLegX =
                x - legSpread;

            float rightLegX =
                x + legSpread;

            // Legs are rounded filled segments. Their bottom edge stays
            // on the same ground anchor as the selection ring.
            AppendCapsule(
                vertices,
                new Vector2f(
                    leftLegX,
                    y - legRadius),
                new Vector2f(
                    leftLegX,
                    pelvisY + legRadius),
                legRadius,
                color);

            AppendCapsule(
                vertices,
                new Vector2f(
                    rightLegX,
                    y - legRadius),
                new Vector2f(
                    rightLegX,
                    pelvisY + legRadius),
                legRadius,
                color);

            // One filled torso envelope. The widths at each level make
            // the silhouette react continuously to the unit definition.
            AppendFilledPolygon(
                vertices,
                new Vector2f[]
                {
                    new Vector2f(
                        x - pelvisHalfWidth,
                        pelvisY),
                    new Vector2f(
                        x - waistHalfWidth,
                        waistY),
                    new Vector2f(
                        x - shoulderHalfWidth,
                        shoulderY),
                    new Vector2f(
                        x + shoulderHalfWidth,
                        shoulderY),
                    new Vector2f(
                        x + waistHalfWidth,
                        waistY),
                    new Vector2f(
                        x + pelvisHalfWidth,
                        pelvisY)
                },
                color);

            // Short neck segment overlaps the torso and head so there is
            // no visual gap when the unit becomes taller or wider.
            float neckRadius =
                MathF.Max(
                    0.03f,
                    width * 0.16f) *
                tilePixelSize;

            Vector2f neckCenter =
                new Vector2f(
                    x,
                    headY + headRadius * 0.45f);

            AppendCapsule(
                vertices,
                new Vector2f(
                    x,
                    shoulderY),
                neckCenter,
                neckRadius,
                color);

            Vector2f headOffset =
                new Vector2f(
                    headNormal.X,
                    headNormal.Y) *
                (headRadius * 0.35f);

            AppendCircle(
                vertices,
                x + headOffset.X,
                headY + headOffset.Y,
                headRadius,
                color);
        }
        else
        {
            float bodyLength =
                MathF.Max(
                    0.20f,
                    length) *
                tilePixelSize;

            Vector2f direction =
                new Vector2f(
                    bodyNormal.X,
                    bodyNormal.Y);

            Vector2f center =
                new Vector2f(x, y);

            Vector2f bodyCenter =
                center -
                direction * (bodyLength * 0.15f);

            Vector2f headCenter =
                center +
                direction * (bodyLength * 0.38f);

            AppendCircle(
                vertices,
                bodyCenter.X,
                bodyCenter.Y,
                torsoRadius,
                color);

            AppendCircle(
                vertices,
                headCenter.X,
                headCenter.Y,
                headRadius,
                color);
        }
    }

    private static void AppendLine(
        VertexArray vertices,
        float x0,
        float y0,
        float x1,
        float y1,
        float width,
        Color color)
    {
        Vector2f direction =
            new Vector2f(
                x1 - x0,
                y1 - y0);

        float length =
            MathF.Sqrt(
                direction.X * direction.X +
                direction.Y * direction.Y);

        if (length < 0.0001f)
            return;

        direction /= length;

        Vector2f normal =
            new Vector2f(
                -direction.Y * width,
                direction.X * width);

        Vector2f p0 = new(x0 + normal.X, y0 + normal.Y);
        Vector2f p1 = new(x1 + normal.X, y1 + normal.Y);
        Vector2f p2 = new(x1 - normal.X, y1 - normal.Y);
        Vector2f p3 = new(x0 - normal.X, y0 - normal.Y);

        vertices.Append(new Vertex(p0, color));
        vertices.Append(new Vertex(p1, color));
        vertices.Append(new Vertex(p2, color));

        vertices.Append(new Vertex(p0, color));
        vertices.Append(new Vertex(p2, color));
        vertices.Append(new Vertex(p3, color));
    }

    private static void AppendCapsule(
        VertexArray vertices,
        Vector2f start,
        Vector2f end,
        float radius,
        Color color)
    {
        Vector2f direction =
            end - start;

        float length =
            MathF.Sqrt(
                direction.X * direction.X +
                direction.Y * direction.Y);

        if (length < 0.0001f)
        {
            AppendCircle(
                vertices,
                start.X,
                start.Y,
                radius,
                color);

            return;
        }

        direction /= length;

        Vector2f normal =
            new Vector2f(
                -direction.Y * radius,
                direction.X * radius);

        Vector2f p0 =
            start + normal;

        Vector2f p1 =
            end + normal;

        Vector2f p2 =
            end - normal;

        Vector2f p3 =
            start - normal;

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

        AppendCircle(
            vertices,
            start.X,
            start.Y,
            radius,
            color);

        AppendCircle(
            vertices,
            end.X,
            end.Y,
            radius,
            color);
    }

    private static void AppendFilledPolygon(
        VertexArray vertices,
        Vector2f[] points,
        Color color)
    {
        if (points.Length < 3)
            return;

        Vector2f center =
            new Vector2f(
                0f,
                0f);

        for (int i = 0;
             i < points.Length;
             i++)
        {
            center += points[i];
        }

        center /= points.Length;

        for (int i = 0;
             i < points.Length;
             i++)
        {
            int next =
                (i + 1) %
                points.Length;

            vertices.Append(
                new Vertex(
                    center,
                    color));

            vertices.Append(
                new Vertex(
                    points[i],
                    color));

            vertices.Append(
                new Vertex(
                    points[next],
                    color));
        }
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
        float halfLength,
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
                halfLength * sin,
                centerY -
                halfWidth * sin -
                halfLength * cos);

        Vector2f p1 =
            new Vector2f(
                centerX +
                halfWidth * cos +
                halfLength * sin,
                centerY +
                halfWidth * sin -
                halfLength * cos);

        Vector2f p2 =
            new Vector2f(
                centerX +
                halfWidth * cos -
                halfLength * sin,
                centerY +
                halfWidth * sin +
                halfLength * cos);

        Vector2f p3 =
            new Vector2f(
                centerX -
                halfWidth * cos -
                halfLength * sin,
                centerY -
                halfWidth * sin +
                halfLength * cos);

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
        float direction,
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
            direction -
            halfFov;

        float right =
            direction +
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

    private static Vector3 NormalizeHorizontal(
        Vector3 value)
    {
        value.Z = 0f;

        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared < 0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }
}
