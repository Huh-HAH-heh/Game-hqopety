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
            MathF.Max(
                0.20f,
                height) *
            tilePixelSize;

        float torsoRadius =
            MathF.Max(
                0.045f,
                width * 0.30f) *
            tilePixelSize;

        float headRadius =
            MathF.Max(
                0.055f,
                width * 0.34f) *
            tilePixelSize;

        if (posture == UnitPosture.Standing)
        {
            HumanPose pose =
                BuildStandingHumanPose(
                    x,
                    y,
                    h,
                    width,
                    headNormal);

            Color limbColor =
                new Color(
                    205,
                    205,
                    205);

            float pelvisRadius =
                MathF.Max(
                    0.045f,
                    width * 0.23f) *
                tilePixelSize;

            float abdomenRadius =
                MathF.Max(
                    0.045f,
                    width * 0.27f) *
                tilePixelSize;

            float chestRadius =
                MathF.Max(
                    0.050f,
                    width * 0.30f) *
                tilePixelSize;

            float neckRadius =
                MathF.Max(
                    0.025f,
                    width * 0.14f) *
                tilePixelSize;

            float legRadius =
                MathF.Max(
                    0.022f,
                    width * 0.10f) *
                tilePixelSize;

            float kneeRadius =
                MathF.Max(
                    0.026f,
                    width * 0.115f) *
                tilePixelSize;

            float thighRadius =
                MathF.Max(
                    0.032f,
                    width * 0.145f) *
                tilePixelSize;

            float armRadius =
                MathF.Max(
                    0.022f,
                    width * 0.10f) *
                tilePixelSize;

            float handRadius =
                MathF.Max(
                    0.020f,
                    width * 0.085f) *
                tilePixelSize;

            // Torso remains the bright central mass.
            AppendCircle(
                vertices,
                pose.Pelvis.X,
                pose.Pelvis.Y,
                pelvisRadius,
                color);

            AppendCircle(
                vertices,
                pose.Abdomen.X,
                pose.Abdomen.Y,
                abdomenRadius,
                color);

            AppendCircle(
                vertices,
                pose.Chest.X,
                pose.Chest.Y,
                chestRadius,
                color);

            // Limbs are chains of logical joints. Their links overlap,
            // so animation can move the joints without changing rendering
            // logic.
            AppendLimbChain(
                vertices,
                pose.LeftHip,
                pose.LeftKnee,
                pose.LeftFoot,
                thighRadius,
                kneeRadius,
                legRadius,
                limbColor);

            AppendLimbChain(
                vertices,
                pose.RightHip,
                pose.RightKnee,
                pose.RightFoot,
                thighRadius,
                kneeRadius,
                legRadius,
                limbColor);

            AppendLimbChain(
                vertices,
                pose.LeftShoulder,
                pose.LeftElbow,
                pose.LeftHand,
                armRadius,
                armRadius,
                handRadius,
                limbColor);

            AppendLimbChain(
                vertices,
                pose.RightShoulder,
                pose.RightElbow,
                pose.RightHand,
                armRadius,
                armRadius,
                handRadius,
                limbColor);

            AppendCircle(
                vertices,
                pose.Neck.X,
                pose.Neck.Y,
                neckRadius,
                color);

            Vector2f headOffset =
                headNormal *
                (headRadius * 0.35f);

            AppendCircle(
                vertices,
                pose.Head.X + headOffset.X,
                pose.Head.Y + headOffset.Y,
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

    private static HumanPose BuildStandingHumanPose(
        float x,
        float y,
        float h,
        float width,
        Vector2f facing)
    {
        Vector2f side =
            new Vector2f(
                -facing.Y,
                facing.X);

        // Move the whole visual a little closer to the ground anchor.
        float rootY =
            y + h * 0.015f;

        // A small forward lean. The pelvis stays back, the chest,
        // neck and head progressively move toward the facing direction.
        Vector2f pelvis =
            new Vector2f(
                x,
                rootY - h * 0.45f);

        Vector2f abdomen =
            pelvis +
            facing * (width * 0.075f) *
            h /
            MathF.Max(
                1f,
                h) -
            new Vector2f(
                0f,
                h * 0.12f);

        Vector2f chest =
            abdomen +
            facing * (width * 0.11f) -
            new Vector2f(
                0f,
                h * 0.12f);

        Vector2f neck =
            chest +
            facing * (width * 0.055f) -
            new Vector2f(
                0f,
                h * 0.075f);

        Vector2f head =
            neck +
            facing * (width * 0.075f) -
            new Vector2f(
                0f,
                h * 0.095f);

        float legSide =
            width * 0.11f;

        float hipSide =
            width * 0.10f;

        Vector2f leftHip =
            pelvis +
            side * legSide;

        Vector2f rightHip =
            pelvis -
            side * legSide;

        Vector2f leftKnee =
            new Vector2f(
                leftHip.X,
                rootY - h * 0.27f);

        Vector2f rightKnee =
            new Vector2f(
                rightHip.X,
                rootY - h * 0.27f);

        Vector2f leftFoot =
            new Vector2f(
                leftHip.X,
                rootY);

        Vector2f rightFoot =
            new Vector2f(
                rightHip.X,
                rootY);

        float shoulderSide =
            width * 0.30f;

        float elbowSide =
            width * 0.36f;

        float handSide =
            width * 0.40f;

        Vector2f leftShoulder =
            chest +
            side * shoulderSide +
            facing * (width * 0.02f);

        Vector2f rightShoulder =
            chest -
            side * shoulderSide +
            facing * (width * 0.02f);

        // Elbows and hands hang slightly back from the shoulders,
        // reinforcing the relaxed/slouched posture.
        Vector2f leftElbow =
            new Vector2f(
                x,
                chest.Y + h * 0.055f) +
            side * elbowSide -
            facing * (width * 0.025f);

        Vector2f rightElbow =
            new Vector2f(
                x,
                chest.Y + h * 0.055f) -
            side * elbowSide -
            facing * (width * 0.025f);

        Vector2f leftHand =
            new Vector2f(
                x,
                chest.Y + h * 0.14f) +
            side * handSide -
            facing * (width * 0.055f);

        Vector2f rightHand =
            new Vector2f(
                x,
                chest.Y + h * 0.14f) -
            side * handSide -
            facing * (width * 0.055f);

        return new HumanPose(
            pelvis,
            abdomen,
            chest,
            neck,
            head,
            leftHip,
            leftKnee,
            leftFoot,
            rightHip,
            rightKnee,
            rightFoot,
            leftShoulder,
            leftElbow,
            leftHand,
            rightShoulder,
            rightElbow,
            rightHand);
    }

    private static void AppendLimbChain(
        VertexArray vertices,
        Vector2f start,
        Vector2f joint,
        Vector2f end,
        float startRadius,
        float jointRadius,
        float endRadius,
        Color color)
    {
        AppendCircle(
            vertices,
            start.X,
            start.Y,
            startRadius,
            color);

        AppendCircle(
            vertices,
            Lerp(start, joint, 0.5f).X,
            Lerp(start, joint, 0.5f).Y,
            startRadius * 0.95f,
            color);

        AppendCircle(
            vertices,
            joint.X,
            joint.Y,
            jointRadius,
            color);

        AppendCircle(
            vertices,
            Lerp(joint, end, 0.5f).X,
            Lerp(joint, end, 0.5f).Y,
            endRadius * 0.95f,
            color);

        AppendCircle(
            vertices,
            end.X,
            end.Y,
            endRadius,
            color);
    }

    private static Vector2f Lerp(
        Vector2f a,
        Vector2f b,
        float amount)
    {
        return a +
            (b - a) *
            amount;
    }

    private readonly struct HumanPose
    {
        public readonly Vector2f Pelvis;
        public readonly Vector2f Abdomen;
        public readonly Vector2f Chest;
        public readonly Vector2f Neck;
        public readonly Vector2f Head;

        public readonly Vector2f LeftHip;
        public readonly Vector2f LeftKnee;
        public readonly Vector2f LeftFoot;

        public readonly Vector2f RightHip;
        public readonly Vector2f RightKnee;
        public readonly Vector2f RightFoot;

        public readonly Vector2f LeftShoulder;
        public readonly Vector2f LeftElbow;
        public readonly Vector2f LeftHand;

        public readonly Vector2f RightShoulder;
        public readonly Vector2f RightElbow;
        public readonly Vector2f RightHand;

        public HumanPose(
            Vector2f pelvis,
            Vector2f abdomen,
            Vector2f chest,
            Vector2f neck,
            Vector2f head,
            Vector2f leftHip,
            Vector2f leftKnee,
            Vector2f leftFoot,
            Vector2f rightHip,
            Vector2f rightKnee,
            Vector2f rightFoot,
            Vector2f leftShoulder,
            Vector2f leftElbow,
            Vector2f leftHand,
            Vector2f rightShoulder,
            Vector2f rightElbow,
            Vector2f rightHand)
        {
            Pelvis = pelvis;
            Abdomen = abdomen;
            Chest = chest;
            Neck = neck;
            Head = head;
            LeftHip = leftHip;
            LeftKnee = leftKnee;
            LeftFoot = leftFoot;
            RightHip = rightHip;
            RightKnee = rightKnee;
            RightFoot = rightFoot;
            LeftShoulder = leftShoulder;
            LeftElbow = leftElbow;
            LeftHand = leftHand;
            RightShoulder = rightShoulder;
            RightElbow = rightElbow;
            RightHand = rightHand;
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
