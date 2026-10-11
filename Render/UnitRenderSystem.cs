using Core.Combat;
using Core.Items;
using Core.Map;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using System;
using System.Numerics;

namespace RimClone.Render;

public sealed class UnitRenderSystem
{
    private const int CircleSegments = 16;
    private const int VisionConeSegments = 20;
    private const int VisionRingSegments = 48;

    private readonly VertexArray _vertices =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _bodyOutlines =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _statusIcons =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _hitVolumeFills =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _visionArea =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _selection =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _aiming =
        new VertexArray(
            PrimitiveType.Lines);

    private bool[] _aimTargetDrawn = Array.Empty<bool>();

    private readonly VertexArray _visionDebug =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _aiDebug =
        new VertexArray(
            PrimitiveType.Lines);

    public void Draw(
        RenderWindow window,
        UnitSimulation simulation,
        WorldMap worldMap,
        View cameraView,
        float tilePixelSize,
        UnitId selectedUnit,
        bool showVisionDebug,
        bool showAiDebug = true,
        bool hideUnseenTargets = false)
    {
        if (tilePixelSize <= 0f)
            return;

        float screenScale =
            MathF.Max(
                0.001f,
                window.Size.X / MathF.Max(1f, cameraView.Size.X));

        float pixelsPerWorldUnit =
            tilePixelSize * screenScale;

        // Keep aim details quiet at mass-combat zoom; unit markers stay readable.
        bool showUnitDetails =
            pixelsPerWorldUnit >= 8f;

        _vertices.Clear();
        _bodyOutlines.Clear();
        _statusIcons.Clear();
        _hitVolumeFills.Clear();
        _visionArea.Clear();
        _selection.Clear();
        _aiming.Clear();
        _visionDebug.Clear();
        _aiDebug.Clear();

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

        Vector3[] headNormals = units.HeadNormal;
        Span<HitVolume> hitVolumeBuffer =
            stackalloc HitVolume[UnitHitSystem.MaxHitVolumeCount];

        if (showUnitDetails)
        {
            if (_aimTargetDrawn.Length < units.Capacity)
                Array.Resize(ref _aimTargetDrawn, units.Capacity);

            Array.Clear(_aimTargetDrawn);
        }

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        Vector3[] positions =
            units.Position;

        float[] widths =
            units.Width;

        float[] lengths =
            units.Length;

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

            // A shooter can be outside the camera while its target is visible.
            // Aim state is simulation data, so collect it before view culling.
            if (showUnitDetails &&
                simulation.Health.OverallHitPoints[unitIndex] > 0f)
            {
                AppendAimProgress(
                    _aiming,
                    simulation,
                    unitIndex,
                    tilePixelSize,
                    _aimTargetDrawn);
            }

            if (hideUnseenTargets &&
                hasSelection &&
                unitIndex != selectedIndex)
            {
                VisionState state =
                    simulation.Vision.Evaluate(
                        units,
                        worldMap,
                        selectedIndex,
                        unitIndex,
                        simulation.Bodies).State;

                if (state != VisionState.Visible)
                    continue;
            }

            bool rootVisible =
                unitPosition.X >= minWorldX &&
                unitPosition.X <= maxWorldX &&
                unitPosition.Y >= minWorldY &&
                unitPosition.Y <= maxWorldY;

            // Body parts extend less than two world units from the root.
            // This padded root cull rejects distant units before visiting their parts.
            if (!rootVisible)
                continue;

            float screenX = unitPosition.X * tilePixelSize;
            float screenY = unitPosition.Y * tilePixelSize;
            float hitPoints = simulation.Health.OverallHitPoints[unitIndex];
            float maxHitPoints = simulation.Health.OverallMaxHitPoints[unitIndex];
            bool isAlive = hitPoints > 0f;
            float healthRatio = maxHitPoints > 0f ? hitPoints / maxHitPoints : 0f;
            float suppression = simulation.Suppression.Value[unitIndex];
            UnitSuppressionState suppressionState = simulation.Suppression.GetState(unitIndex);
            float markerRadius = GetMarkerRadius(
                widths[unitIndex],
                lengths[unitIndex],
                tilePixelSize,
                screenScale);

            bool inspectHitVolumes =
                hasSelection && selectedIndex == unitIndex;

            if (inspectHitVolumes)
            {
                // Inspection view is an upright body diagram packed INSIDE
                // the selected unit's circle, not the hit volumes' world offsets.
                float inspectionRadius = MathF.Max(
                    markerRadius,
                    9f / MathF.Max(0.001f, screenScale));

                AppendUnitMarker(
                    _vertices,
                    _bodyOutlines,
                    _statusIcons,
                    screenX,
                    screenY,
                    inspectionRadius,
                    headNormals[unitIndex],
                    units.FactionTag[unitIndex],
                    isAlive,
                    healthRatio,
                    suppression,
                    suppressionState,
                    screenScale);

                int volumeCount = UnitHitSystem.GetHitVolumes(
                    units,
                    unitIndex,
                    hitVolumeBuffer,
                    simulation.Bodies);

                for (int volumeIndex = 0; volumeIndex < volumeCount; volumeIndex++)
                {
                    AppendPackedHitVolume(
                        _hitVolumeFills,
                        _bodyOutlines,
                        hitVolumeBuffer[volumeIndex],
                        volumeIndex,
                        screenX,
                        screenY,
                        inspectionRadius,
                        screenScale);
                }
            }
            else
            {
                AppendUnitMarker(
                    _vertices,
                    _bodyOutlines,
                    _statusIcons,
                    screenX,
                    screenY,
                    markerRadius,
                    headNormals[unitIndex],
                    units.FactionTag[unitIndex],
                    isAlive,
                    healthRatio,
                    suppression,
                    suppressionState,
                    screenScale);
            }

            if (!isAlive)
                continue;

            if (showAiDebug &&
                simulation.AI.Enabled &&
                hasSelection &&
                selectedIndex == unitIndex)
            {
                AppendAiDebug(
                    _aiDebug,
                    simulation,
                    unitIndex,
                    tilePixelSize);

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
            }
        }

        if (hasSelection &&
            showVisionDebug)
        {
            AppendVisionDebug(
                _visionArea,
                _visionDebug,
                simulation,
                worldMap,
                selectedIndex,
                tilePixelSize);
        }

        if (_visionArea.VertexCount > 0)
        {
            window.Draw(
                _visionArea);
        }

        if (_vertices.VertexCount > 0)
            window.Draw(_vertices);

        if (_hitVolumeFills.VertexCount > 0)
            window.Draw(_hitVolumeFills);

        if (_bodyOutlines.VertexCount > 0)
            window.Draw(_bodyOutlines);

        if (_statusIcons.VertexCount > 0)
            window.Draw(_statusIcons);

        if (_selection.VertexCount > 0)
        {
            window.Draw(
                _selection);
        }

        if (_aiming.VertexCount > 0)
        {
            window.Draw(
                _aiming);
        }

        if (_visionDebug.VertexCount > 0)
        {
            window.Draw(
                _visionDebug);
        }

        if (_aiDebug.VertexCount > 0)
        {
            window.Draw(
                _aiDebug);
        }
    }

public void DrawSnapshot(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        View cameraView,
        float tilePixelSize,
        int selectedUnitIndex,
        bool showAiDebug)
    {
        if (tilePixelSize <= 0f)
            return;

        float screenScale = MathF.Max(
            0.001f,
            window.Size.X / MathF.Max(1f, cameraView.Size.X));
        bool showUnitDetails = tilePixelSize * screenScale >= 8f;

        float centerX = cameraView.Center.X;
        float centerY = cameraView.Center.Y;
        float halfWidth = cameraView.Size.X * 0.5f;
        float halfHeight = cameraView.Size.Y * 0.5f;
        float minX = (centerX - halfWidth) / tilePixelSize - 2f;
        float maxX = (centerX + halfWidth) / tilePixelSize + 2f;
        float minY = (centerY - halfHeight) / tilePixelSize - 2f;
        float maxY = (centerY + halfHeight) / tilePixelSize + 2f;

        _vertices.Clear();
        _bodyOutlines.Clear();
        _selection.Clear();
        _statusIcons.Clear();
        _hitVolumeFills.Clear();
        _aiming.Clear();
        _visionArea.Clear();
        _visionDebug.Clear();
        _aiDebug.Clear();

        RenderUnitState[] units = snapshot.Units;
        for (int i = 0; i < units.Length; i++)
        {
            RenderUnitState unit = units[i];
            Vector3 position = unit.Position;
            float x = position.X * tilePixelSize;
            float y = position.Y * tilePixelSize;

            // Debug geometry is produced from the same immutable frame data
            // as the unit markers; no live AI store reads happen on the render thread.
            if (showAiDebug &&
                unit.Index == selectedUnitIndex &&
                unit.HitPoints > 0f)
            {
                Color debugColor = GetAiColor(unit.AiState);

                if (unit.HasTarget)
                {
                    AppendDebugLine(
                        _aiDebug,
                        x,
                        y,
                        unit.TargetPosition.X * tilePixelSize,
                        unit.TargetPosition.Y * tilePixelSize,
                        new Color(debugColor.R, debugColor.G, debugColor.B, 140));
                }

                if (unit.HasGoal)
                {
                    float goalX = unit.Goal.X * tilePixelSize;
                    float goalY = unit.Goal.Y * tilePixelSize;
                    AppendCircleOutline(
                        _aiDebug,
                        goalX,
                        goalY,
                        0.42f * tilePixelSize,
                        debugColor,
                        12);
                    AppendDebugLine(
                        _aiDebug,
                        x,
                        y,
                        goalX,
                        goalY,
                        new Color(debugColor.R, debugColor.G, debugColor.B, 110));
                }

            }

            if (position.X < minX || position.X > maxX ||
                position.Y < minY || position.Y > maxY)
            {
                continue;
            }

            bool isAlive = unit.HitPoints > 0f;
            float markerRadius = GetMarkerRadius(
                unit.Width,
                unit.Length,
                tilePixelSize,
                screenScale);

            RenderHitVolume[] selectedHitVolumes = snapshot.HitVolumes;
            bool inspectHitVolumes =
                unit.Index == selectedUnitIndex &&
                snapshot.HitVolumeUnitIndex == selectedUnitIndex &&
                selectedHitVolumes.Length > 0;

            if (inspectHitVolumes)
            {
                float inspectionRadius = MathF.Max(
                    markerRadius,
                    9f / MathF.Max(0.001f, screenScale));

                AppendUnitMarker(
                    _vertices,
                    _bodyOutlines,
                    _statusIcons,
                    x,
                    y,
                    inspectionRadius,
                    unit.HeadNormal,
                    unit.FactionTag,
                    isAlive,
                    unit.HealthRatio,
                    unit.Suppression,
                    unit.SuppressionState,
                    screenScale);

                for (int volumeIndex = 0; volumeIndex < selectedHitVolumes.Length; volumeIndex++)
                {
                    RenderHitVolume volume = selectedHitVolumes[volumeIndex];
                    AppendPackedHitVolume(
                        _hitVolumeFills,
                        _bodyOutlines,
                        new HitVolume(
                            volume.Part,
                            volume.Center,
                            volume.Forward,
                            volume.Right,
                            volume.Radii),
                        volumeIndex,
                        x,
                        y,
                        inspectionRadius,
                        screenScale);
                }
            }
            else
            {
                AppendUnitMarker(
                    _vertices,
                    _bodyOutlines,
                    _statusIcons,
                    x,
                    y,
                    markerRadius,
                    unit.HeadNormal,
                    unit.FactionTag,
                    isAlive,
                    unit.HealthRatio,
                    unit.Suppression,
                    unit.SuppressionState,
                    screenScale);
            }

            if (!isAlive)
                continue;

            if (unit.Index == selectedUnitIndex)
            {
                float selectionRadius = MathF.Max(unit.Width, unit.Length) * 0.6f;
                AppendSelection(
                    _selection,
                    x,
                    y,
                    MathF.Max(selectionRadius, 0.45f) * tilePixelSize);
            }
        }

        if (showUnitDetails)
        {
            RenderAimRing[] aimRings = snapshot.AimRings;
            for (int i = 0; i < aimRings.Length; i++)
            {
                RenderAimRing aim = aimRings[i];
                float radius = tilePixelSize * (0.72f - 0.60f * aim.Progress);
                byte alpha = (byte)Math.Clamp(
                    (int)(78f * (1f - aim.Progress) + 12f),
                    0,
                    90);

                AppendCircleOutline(
                    _aiming,
                    aim.AimPoint.X * tilePixelSize,
                    aim.AimPoint.Y * tilePixelSize,
                    MathF.Max(0.10f * tilePixelSize, radius),
                    new Color(205, 218, 232, alpha),
                    24);
            }
        }

        if (_vertices.VertexCount > 0)
            window.Draw(_vertices);
        if (_hitVolumeFills.VertexCount > 0)
            window.Draw(_hitVolumeFills);
        if (_bodyOutlines.VertexCount > 0)
            window.Draw(_bodyOutlines);
        if (_statusIcons.VertexCount > 0)
            window.Draw(_statusIcons);
        if (_selection.VertexCount > 0)
            window.Draw(_selection);
        if (_aiming.VertexCount > 0)
            window.Draw(_aiming);
        if (_aiDebug.VertexCount > 0)
            window.Draw(_aiDebug);
    }

    private static void AppendPackedHitVolume(
        VertexArray fills,
        VertexArray outlines,
        in HitVolume volume,
        int shapeIndex,
        float unitX,
        float unitY,
        float circleRadius,
        float screenScale)
    {
        // Compact upright diagram. All coordinates are fractions of the
        // white marker radius; every part stays comfortably inside that disc.
        float side;
        float offsetX;
        float offsetY;
        float radiusX;
        float radiusY;
        float rotation = 0f;

        switch (shapeIndex)
        {
            case 0: // Torso
                offsetX = 0f; offsetY = 0.02f;
                radiusX = 0.22f; radiusY = 0.25f;
                break;
            case 1: // Neck
                offsetX = 0f; offsetY = -0.29f;
                radiusX = 0.065f; radiusY = 0.065f;
                break;
            case 2: // Head
                offsetX = 0f; offsetY = -0.52f;
                radiusX = 0.14f; radiusY = 0.14f;
                break;
            case 3: // Upper arm, right
                offsetX = 0.22f; offsetY = -0.17f;
                radiusX = 0.12f; radiusY = 0.065f;
                rotation = 0.28f;
                break;
            case 4: // Elbow, right
                offsetX = 0.31f; offsetY = -0.015f;
                radiusX = 0.06f; radiusY = 0.06f;
                break;
            case 5: // Forearm, right
                offsetX = 0.31f; offsetY = 0.105f;
                radiusX = 0.12f; radiusY = 0.055f;
                rotation = -0.15f;
                break;
            case 6: // Hand, right
                offsetX = 0.28f; offsetY = 0.225f;
                radiusX = 0.065f; radiusY = 0.055f;
                break;
            case 7: // Upper arm, left
                offsetX = -0.22f; offsetY = -0.17f;
                radiusX = 0.12f; radiusY = 0.065f;
                rotation = -0.28f;
                break;
            case 8: // Elbow, left
                offsetX = -0.31f; offsetY = -0.015f;
                radiusX = 0.06f; radiusY = 0.06f;
                break;
            case 9: // Forearm, left
                offsetX = -0.31f; offsetY = 0.105f;
                radiusX = 0.12f; radiusY = 0.055f;
                rotation = 0.15f;
                break;
            case 10: // Hand, left
                offsetX = -0.28f; offsetY = 0.225f;
                radiusX = 0.065f; radiusY = 0.055f;
                break;
            case 11: // Thigh, right
                offsetX = 0.115f; offsetY = 0.30f;
                radiusX = 0.075f; radiusY = 0.11f;
                rotation = -0.08f;
                break;
            case 12: // Knee, right
                offsetX = 0.12f; offsetY = 0.42f;
                radiusX = 0.055f; radiusY = 0.055f;
                break;
            case 13: // Shin, right
                offsetX = 0.13f; offsetY = 0.535f;
                radiusX = 0.055f; radiusY = 0.12f;
                rotation = -0.035f;
                break;
            case 14: // Foot, right
                offsetX = 0.145f; offsetY = 0.66f;
                radiusX = 0.08f; radiusY = 0.045f;
                break;
            case 15: // Thigh, left
                offsetX = -0.115f; offsetY = 0.30f;
                radiusX = 0.075f; radiusY = 0.11f;
                rotation = 0.08f;
                break;
            case 16: // Knee, left
                offsetX = -0.12f; offsetY = 0.42f;
                radiusX = 0.055f; radiusY = 0.055f;
                break;
            case 17: // Shin, left
                offsetX = -0.13f; offsetY = 0.535f;
                radiusX = 0.055f; radiusY = 0.12f;
                rotation = 0.035f;
                break;
            case 18: // Foot, left
                offsetX = -0.145f; offsetY = 0.66f;
                radiusX = 0.08f; radiusY = 0.045f;
                break;
            default: // Non-human whole-body collider
                offsetX = 0f; offsetY = 0f;
                radiusX = 0.31f; radiusY = 0.25f;
                break;
        }

        // For a single whole-body collider, use a compact central oval.
        if (volume.Part == UnitHealthPartId.WholeBody)
        {
            offsetX = 0f; offsetY = 0.02f;
            radiusX = 0.30f; radiusY = 0.22f;
            rotation = 0f;
        }

        float centerX = unitX + offsetX * circleRadius;
        float centerY = unitY + offsetY * circleRadius;
        float rx = radiusX * circleRadius;
        float ry = radiusY * circleRadius;
        float outlinePad = 0.55f / MathF.Max(0.001f, screenScale);
        int segments = circleRadius * screenScale >= 12f ? 16 : 10;

        GetHitVolumeColors(
            volume.Part,
            out Color fill,
            out Color outline);

        AppendEllipse(
            fills,
            centerX,
            centerY,
            rx,
            ry,
            rotation,
            fill,
            segments);

        AppendEllipseOutline(
            outlines,
            centerX,
            centerY,
            rx + outlinePad,
            ry + outlinePad,
            rotation,
            outline,
            segments);
    }

    private static void GetHitVolumeColors(
        UnitHealthPartId part,
        out Color fill,
        out Color outline)
    {
        Color solid = part switch
        {
            UnitHealthPartId.Head => new Color(255, 204, 74),
            UnitHealthPartId.Torso => new Color(72, 166, 255),
            UnitHealthPartId.LeftArm => new Color(255, 139, 61),
            UnitHealthPartId.RightArm => new Color(255, 91, 112),
            UnitHealthPartId.LeftHand => new Color(255, 211, 117),
            UnitHealthPartId.RightHand => new Color(255, 157, 184),
            UnitHealthPartId.LeftLeg => new Color(86, 220, 156),
            UnitHealthPartId.RightLeg => new Color(50, 190, 191),
            UnitHealthPartId.LeftFoot => new Color(169, 139, 255),
            UnitHealthPartId.RightFoot => new Color(204, 122, 240),
            _ => new Color(210, 220, 232)
        };

        fill = new Color(solid.R, solid.G, solid.B, 155);
        outline = new Color(solid.R, solid.G, solid.B, 250);
    }

    private static void AppendEllipse(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float rotation,
        Color color,
        int segments)
    {
        Vector2f center = new(centerX, centerY);
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);

        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments;
            float a1 = (i + 1) * MathF.Tau / segments;

            Vector2f p0 = RotateEllipsePoint(
                centerX, centerY, radiusX, radiusY, cos, sin, a0);
            Vector2f p1 = RotateEllipsePoint(
                centerX, centerY, radiusX, radiusY, cos, sin, a1);

            vertices.Append(new Vertex(center, color));
            vertices.Append(new Vertex(p0, color));
            vertices.Append(new Vertex(p1, color));
        }
    }

    private static void AppendEllipseOutline(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float rotation,
        Color color,
        int segments)
    {
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);

        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments;
            float a1 = (i + 1) * MathF.Tau / segments;
            Vector2f p0 = RotateEllipsePoint(
                centerX, centerY, radiusX, radiusY, cos, sin, a0);
            Vector2f p1 = RotateEllipsePoint(
                centerX, centerY, radiusX, radiusY, cos, sin, a1);

            AppendDebugLine(
                vertices,
                p0.X, p0.Y,
                p1.X, p1.Y,
                color);
        }
    }

    private static Vector2f RotateEllipsePoint(
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float cos,
        float sin,
        float angle)
    {
        float localX = MathF.Cos(angle) * radiusX;
        float localY = MathF.Sin(angle) * radiusY;
        return new Vector2f(
            centerX + localX * cos - localY * sin,
            centerY + localX * sin + localY * cos);
    }

    private static void AppendOrientedRectOutline(
        VertexArray vertices,
        float centerX,
        float centerY,
        float halfWidth,
        float halfLength,
        float rotation,
        Color color)
    {
        float dx = MathF.Cos(rotation);
        float dy = MathF.Sin(rotation);
        float px = -dy;
        float py = dx;

        float x0 = centerX - dx * halfLength - px * halfWidth;
        float y0 = centerY - dy * halfLength - py * halfWidth;
        float x1 = centerX + dx * halfLength - px * halfWidth;
        float y1 = centerY + dy * halfLength - py * halfWidth;
        float x2 = centerX + dx * halfLength + px * halfWidth;
        float y2 = centerY + dy * halfLength + py * halfWidth;
        float x3 = centerX - dx * halfLength + px * halfWidth;
        float y3 = centerY - dy * halfLength + py * halfWidth;

        AppendDebugLine(vertices, x0, y0, x1, y1, color);
        AppendDebugLine(vertices, x1, y1, x2, y2, color);
        AppendDebugLine(vertices, x2, y2, x3, y3, color);
        AppendDebugLine(vertices, x3, y3, x0, y0, color);
    }

    private static void AppendSnapshotWeapon(
        VertexArray vertices,
        RenderWeaponVisual visual,
        Vector3 position,
        Vector3 facing,
        float tilePixelSize,
        bool secondary)
    {
        WeaponConfig? weapon = visual.Config;
        if (weapon == null)
            return;

        float dx = facing.X;
        float dy = facing.Y;
        float facingLength = MathF.Sqrt(dx * dx + dy * dy);
        if (facingLength < 0.001f)
        {
            dx = 1f;
            dy = 0f;
        }
        else
        {
            dx /= facingLength;
            dy /= facingLength;
        }

        if (visual.AimIndicatorActive && visual.AimPoint != Vector3.Zero)
        {
            float aimX = visual.AimPoint.X - position.X;
            float aimY = visual.AimPoint.Y - position.Y;
            float aimLength = MathF.Sqrt(aimX * aimX + aimY * aimY);
            if (aimLength > 0.001f)
            {
                dx = aimX / aimLength;
                dy = aimY / aimLength;
            }
        }

        float sideOffset = secondary ? 0.10f : 0f;
        float centerDistance = weapon.WorldRenderLength * 0.43f;
        float centerX = position.X + dx * centerDistance - dy * sideOffset;
        float centerY = position.Y + dy * centerDistance + dx * sideOffset;

        AppendOrientedRect(
            vertices,
            centerX * tilePixelSize,
            centerY * tilePixelSize,
            MathF.Max(0.025f, weapon.WorldRenderWidth * 0.5f) * tilePixelSize,
            MathF.Max(0.12f, weapon.WorldRenderLength * 0.5f) * tilePixelSize,
            MathF.Atan2(dy, dx),
            GetItemColor(weapon));
    }

    private static Color GetItemColor(ItemConfig item) =>
        new Color(item.WorldColorR, item.WorldColorG, item.WorldColorB);

    private static Color GetFactionAccent(ushort factionTag) =>
        factionTag switch
        {
            1 => new Color(107, 158, 198, 125),
            2 => new Color(190, 116, 109, 125),
            _ => new Color(153, 164, 176, 95)
        };

    private static void AppendEquippedApparel(
        VertexArray vertices,
        UnitInventoryStore inventory,
        int unitIndex,
        Vector3 unitPosition,
        float bodyRadius,
        float tilePixelSize)
    {
        bool hasApparel = false;
        bool hasPoweredArmor = false;

        for (int zone = 0; zone < UnitInventoryStore.BodyZoneCount; zone++)
        {
            for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
            {
                short itemSlot = inventory.GetBodyEquipment(
                    unitIndex,
                    zone,
                    (ArmorLayer)layer);

                if (itemSlot < 0 ||
                    inventory.GetItem(unitIndex, itemSlot) is not ArmorConfig apparel)
                {
                    continue;
                }

                hasApparel = true;
                hasPoweredArmor |= apparel.MovementSpeedMultiplier > 1f;
            }
        }

        if (!hasApparel)
            return;

        // One restrained gear accent replaces the stack of differently
        // coloured rings that made units look noisy and inconsistent.
        Color accent = hasPoweredArmor
            ? new Color(105, 185, 202, 190)
            : new Color(161, 171, 181, 150);

        AppendCircleOutline(
            vertices,
            unitPosition.X * tilePixelSize,
            unitPosition.Y * tilePixelSize,
            (bodyRadius + 0.10f) * tilePixelSize,
            accent,
            CircleSegments);
    }

    private static void AppendEquippedWeapons(
        VertexArray vertices,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        int unitIndex,
        Vector3 unitPosition,
        Vector3 facing,
        float tilePixelSize)
    {
        float dx = facing.X;
        float dy = facing.Y;
        float facingLength = MathF.Sqrt(dx * dx + dy * dy);
        if (facingLength < 0.001f)
        {
            dx = 1f;
            dy = 0f;
        }
        else
        {
            dx /= facingLength;
            dy /= facingLength;
        }

        for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
        {
            short itemSlot = inventory.GetWeaponEquipment(unitIndex, (UnitWeaponSlot)slot);
            if (itemSlot < 0 ||
                inventory.GetItem(unitIndex, itemSlot) is not WeaponConfig weapon)
                continue;

            float weaponX = dx;
            float weaponY = dy;
            int stateIndex = UnitWeaponStore.GetIndex(unitIndex, (UnitWeaponSlot)slot);
            Vector3 aimPoint = weapons.AimPoint[stateIndex];

            if (weapons.AimIndicatorActive[stateIndex] && aimPoint != Vector3.Zero)
            {
                weaponX = aimPoint.X - unitPosition.X;
                weaponY = aimPoint.Y - unitPosition.Y;
                float aimLength = MathF.Sqrt(weaponX * weaponX + weaponY * weaponY);
                if (aimLength > 0.001f)
                {
                    weaponX /= aimLength;
                    weaponY /= aimLength;
                }
                else
                {
                    weaponX = dx;
                    weaponY = dy;
                }
            }

            float sideOffset = slot == (int)UnitWeaponSlot.Secondary ? 0.10f : 0f;
            float centerDistance = weapon.WorldRenderLength * 0.43f;
            float centerX = unitPosition.X + weaponX * centerDistance - weaponY * sideOffset;
            float centerY = unitPosition.Y + weaponY * centerDistance + weaponX * sideOffset;

            AppendOrientedRect(
                vertices,
                centerX * tilePixelSize,
                centerY * tilePixelSize,
                MathF.Max(0.025f, weapon.WorldRenderWidth * 0.5f) * tilePixelSize,
                MathF.Max(0.12f, weapon.WorldRenderLength * 0.5f) * tilePixelSize,
                MathF.Atan2(weaponY, weaponX),
                GetItemColor(weapon));
        }
    }

    private static void AppendOrientedRect(
        VertexArray vertices, float centerX, float centerY,
        float halfWidth, float halfLength, float rotation, Color color)
    {
        float dx = MathF.Cos(rotation), dy = MathF.Sin(rotation);
        float px = -dy, py = dx;
        Vector2f p0 = new(centerX - dx * halfLength - px * halfWidth, centerY - dy * halfLength - py * halfWidth);
        Vector2f p1 = new(centerX + dx * halfLength - px * halfWidth, centerY + dy * halfLength - py * halfWidth);
        Vector2f p2 = new(centerX + dx * halfLength + px * halfWidth, centerY + dy * halfLength + py * halfWidth);
        Vector2f p3 = new(centerX - dx * halfLength + px * halfWidth, centerY - dy * halfLength + py * halfWidth);
        vertices.Append(new Vertex(p0, color));
        vertices.Append(new Vertex(p1, color));
        vertices.Append(new Vertex(p2, color));
        vertices.Append(new Vertex(p0, color));
        vertices.Append(new Vertex(p2, color));
        vertices.Append(new Vertex(p3, color));
    }

    private static float GetMarkerRadius(
        float width,
        float length,
        float tilePixelSize,
        float screenScale)
    {
        float footprint = MathF.Max(width, length);
        return MathF.Max(
            2.0f / MathF.Max(0.001f, screenScale),
            MathF.Max(0.13f, footprint * 0.125f) * tilePixelSize);
    }

    private static void AppendUnitMarker(
        VertexArray fills,
        VertexArray outlines,
        VertexArray statusIcons,
        float x,
        float y,
        float radius,
        Vector3 facing,
        ushort factionTag,
        bool isAlive,
        float healthRatio,
        float suppression,
        UnitSuppressionState suppressionState,
        float screenScale)
    {
        // Baseline unit visualization: a plain white disk with a thin dark edge.
        // All gameplay state is deliberately hidden from the marker for this pass.
        float pixel = 1f / MathF.Max(0.001f, screenScale);
        int segments = radius * screenScale < 7f ? 16 : 28;

        AppendEllipse(
            fills, x, y,
            radius + pixel * 0.72f, radius + pixel * 0.72f,
            0f, new Color(16, 21, 27, 255), segments);

        AppendEllipse(
            fills, x, y,
            radius, radius,
            0f, new Color(255, 255, 255, 255), segments);
    }

    private static void AppendFactionHalo(
        VertexArray vertices,
        float x,
        float y,
        float radius,
        ushort factionTag,
        bool isAlive,
        int quality)
    {
        Color color = !isAlive
            ? new Color(137, 143, 150, 165)
            : factionTag switch
            {
                1 => new Color(54, 157, 255, 245),
                2 => new Color(255, 65, 70, 245),
                _ => new Color(171, 183, 196, 190)
            };

        // One clean blue/red upper arc, instead of a dim full halo underneath
        // multiple status rings. The saturated tint carries faction identity.
        int segments = quality < 14 ? 10 : 18;
        AppendArc(
            vertices,
            x,
            y,
            radius,
            MathF.PI * 1.14f,
            MathF.PI * 0.72f,
            color,
            segments);
    }

    private static void AppendArc(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radius,
        float startAngle,
        float sweepAngle,
        Color color,
        int segments)
    {
        if (radius <= 0f || MathF.Abs(sweepAngle) <= 0.001f)
            return;

        int steps = Math.Max(2, segments);
        float step = sweepAngle / steps;
        float a0 = startAngle;

        for (int i = 0; i < steps; i++)
        {
            float a1 = startAngle + step * (i + 1);
            AppendDebugLine(
                vertices,
                centerX + MathF.Cos(a0) * radius,
                centerY + MathF.Sin(a0) * radius,
                centerX + MathF.Cos(a1) * radius,
                centerY + MathF.Sin(a1) * radius,
                color);
            a0 = a1;
        }
    }

    private static void AppendWeaponGlyph(
        VertexArray vertices,
        float x,
        float y,
        float radius,
        Vector3 direction,
        Vector3 bodyFacing,
        float screenScale)
    {
        float dx = direction.X;
        float dy = direction.Y;
        float lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 0.0001f)
        {
            dx = 1f;
            dy = 0f;
        }
        else
        {
            float inverseLength = 1f / MathF.Sqrt(lengthSquared);
            dx *= inverseLength;
            dy *= inverseLength;
        }

        float pixel = 1f / MathF.Max(0.001f, screenScale);

        // Keep the attachment point on the body's right flank even when aim
        // direction differs from the head normal. Y grows downward in SFML.
        float fx = bodyFacing.X;
        float fy = bodyFacing.Y;
        float facingLengthSquared = fx * fx + fy * fy;
        if (facingLengthSquared < 0.0001f)
        {
            fx = dx;
            fy = dy;
        }
        else
        {
            float inverseFacingLength = 1f / MathF.Sqrt(facingLengthSquared);
            fx *= inverseFacingLength;
            fy *= inverseFacingLength;
        }

        float sideX = -fy;
        float sideY = fx;
        float weaponPerpX = -dy;
        float weaponPerpY = dx;

        // A long weapon projects beyond the small body disk. Its base corners
        // have different forward offsets to create the slightly skewed profile.
        float side = radius * 0.62f;
        float baseForward = radius * 0.10f;
        float tipForward = radius * 2.45f + pixel * 3.2f;
        float halfWidth = MathF.Max(pixel * 1.45f, radius * 0.34f);

        float tx = x + dx * tipForward + sideX * side;
        float ty = y + dy * tipForward + sideY * side;

        float baseAForward = baseForward - radius * 0.20f;
        float baseBForward = baseForward + radius * 0.24f;
        float baseAX = x + dx * baseAForward + sideX * side - weaponPerpX * halfWidth;
        float baseAY = y + dy * baseAForward + sideY * side - weaponPerpY * halfWidth;
        float baseBX = x + dx * baseBForward + sideX * side + weaponPerpX * halfWidth;
        float baseBY = y + dy * baseBForward + sideY * side + weaponPerpY * halfWidth;

        AppendTriangle(
            vertices,
            tx, ty,
            baseAX, baseAY,
            baseBX, baseBY,
            new Color(15, 21, 28, 255));

        // Bright inset follows the weapon axis, while the mounting offset remains
        // tied to the body facing. This avoids the weapon sliding to another flank
        // during aim rotation.
        float insetStart = radius * 0.56f;
        float insetTip = tipForward - pixel * 1.1f;
        float insetHalfWidth = halfWidth * 0.40f;
        AppendTriangle(
            vertices,
            x + dx * insetTip + sideX * side,
            y + dy * insetTip + sideY * side,
            x + dx * insetStart + sideX * side - weaponPerpX * (insetHalfWidth * 0.70f),
            y + dy * insetStart + sideY * side - weaponPerpY * (insetHalfWidth * 0.70f),
            x + dx * (insetStart + radius * 0.20f) + sideX * side + weaponPerpX * insetHalfWidth,
            y + dy * (insetStart + radius * 0.20f) + sideY * side + weaponPerpY * insetHalfWidth,
            new Color(225, 235, 243, 255));
    }

    private static void AppendTriangle(
        VertexArray vertices,
        float x0,
        float y0,
        float x1,
        float y1,
        float x2,
        float y2,
        Color color)
    {
        vertices.Append(new Vertex(new Vector2f(x0, y0), color));
        vertices.Append(new Vertex(new Vector2f(x1, y1), color));
        vertices.Append(new Vertex(new Vector2f(x2, y2), color));
    }

    private static void AppendDeathMarker(
        VertexArray vertices,
        float x,
        float y,
        float radius)
    {
        AppendCircleOutline(
            vertices,
            x,
            y,
            radius,
            new Color(90, 90, 90, 245),
            10);

        Color cross = new Color(255, 62, 62, 255);
        AppendDebugLine(vertices, x - radius * 0.58f, y - radius * 0.58f,
            x + radius * 0.58f, y + radius * 0.58f, cross);
        AppendDebugLine(vertices, x - radius * 0.58f, y + radius * 0.58f,
            x + radius * 0.58f, y - radius * 0.58f, cross);
    }

    private static void AppendAimProgress(
        VertexArray aiming,
        UnitSimulation simulation,
        int shooterIndex,
        float tilePixelSize,
        bool[] targetDrawn)
    {
        UnitStore units = simulation.Units;
        UnitInventoryStore inventory = simulation.Inventory;
        UnitWeaponStore weapons = simulation.Weapons;

        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
            int stateIndex = UnitWeaponStore.GetIndex(shooterIndex, weaponSlot);

            if (!weapons.AimIndicatorActive[stateIndex] ||
                weapons.AimTimer[stateIndex] <= 0f)
            {
                continue;
            }

            UnitId combatTarget = weapons.AimTarget[stateIndex];

            if (!units.TryGetIndex(combatTarget, out int targetIndex) ||
                targetDrawn[targetIndex] ||
                simulation.Health.OverallHitPoints[targetIndex] <= 0f)
            {
                continue;
            }

            short inventorySlot = inventory.GetWeaponEquipment(
                shooterIndex,
                weaponSlot);

            if (inventorySlot < 0 ||
                inventory.GetItem(shooterIndex, inventorySlot) is not RangedWeaponConfig weapon)
            {
                continue;
            }

            Vector3 aimPoint = weapons.AimPoint[stateIndex];
            if (aimPoint == Vector3.Zero)
                continue;

            Vector3 shooterPosition = units.Position[shooterIndex];
            float dx = aimPoint.X - shooterPosition.X;
            float dy = aimPoint.Y - shooterPosition.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);

            float duration = UnitWeaponSystem.GetAimDuration(
                weapon,
                weapons.CurrentAimMode[stateIndex],
                distance);

            if (duration <= 0f)
                continue;

            float progress = Math.Clamp(
                weapons.AimTimer[stateIndex] / duration,
                0f,
                1f);

            // One muted ring per target. It shrinks as the active weapon warms
            // up, then fades almost to nothing until the shot clears the state.
            // A restrained RimWorld/CE-style aim bubble: it contracts around
            // the target while the weapon warms up, then disappears on firing.
            float radius = tilePixelSize * (0.72f - 0.60f * progress);
            byte alpha = (byte)Math.Clamp(
                (int)(78f * (1f - progress) + 12f),
                0,
                90);

            AppendCircleOutline(
                aiming,
                aimPoint.X * tilePixelSize,
                aimPoint.Y * tilePixelSize,
                MathF.Max(0.10f * tilePixelSize, radius),
                new Color(205, 218, 232, alpha),
                24);

            // Several shooters aiming at the same pawn should not stack opacity.
            targetDrawn[targetIndex] = true;
            return;
        }
    }

private static void AppendSuppressionDebug(
        VertexArray debug,
        UnitSimulation simulation,
        int unitIndex,
        float tilePixelSize)
    {
        float suppression =
            simulation.Suppression.Value[unitIndex];

        if (suppression <= 0.01f)
            return;

        Vector3 position =
            simulation.Units.Position[unitIndex];

        Color color =
            simulation.Suppression.GetState(unitIndex) switch
            {
                UnitSuppressionState.Panicked =>
                    new Color(255, 70, 70, 220),

                UnitSuppressionState.Suppressed =>
                    new Color(255, 200, 70, 190),

                _ =>
                    new Color(190, 190, 190, 130)
            };

        float radius =
            (0.45f +
             MathF.Min(
                 1.0f,
                 suppression * 0.15f)) *
            tilePixelSize;

        AppendCircleOutline(
            debug,
            position.X * tilePixelSize,
            position.Y * tilePixelSize,
            radius,
            color,
            14);
    }

    private static void AppendAiDebug(
        VertexArray debug,
        UnitSimulation simulation,
        int unitIndex,
        float tilePixelSize)
    {
        UnitStore units =
            simulation.Units;

        Color color =
            GetAiColor(
                simulation.AI.Store.State[unitIndex]);

        float x =
            units.Position[unitIndex].X *
            tilePixelSize;

        float y =
            units.Position[unitIndex].Y *
            tilePixelSize;

        UnitAiStore ai =
            simulation.AI.Store;

        if (ai.HasTarget[unitIndex])
        {
            UnitId target =
                ai.Target[unitIndex];

            if (units.TryGetIndex(
                    target,
                    out int targetIndex))
            {
                AppendDebugLine(
                    debug,
                    x,
                    y,
                    units.Position[targetIndex].X * tilePixelSize,
                    units.Position[targetIndex].Y * tilePixelSize,
                    new Color(
                        color.R,
                        color.G,
                        color.B,
                        140));
            }
        }

        if (ai.HasGoal[unitIndex])
        {
            float goalX =
                ai.Goal[unitIndex].X *
                tilePixelSize;

            float goalY =
                ai.Goal[unitIndex].Y *
                tilePixelSize;

            AppendCircleOutline(
                debug,
                goalX,
                goalY,
                0.42f * tilePixelSize,
                color,
                12);

            AppendDebugLine(
                debug,
                x,
                y,
                goalX,
                goalY,
                new Color(
                    color.R,
                    color.G,
                    color.B,
                    110));
        }
    }

    private static Color GetAiColor(
        UnitAiState state)
    {
        return state switch
        {
            UnitAiState.Attack =>
                new Color(
                    245,
                    75,
                    75),

            UnitAiState.SeekCover =>
                new Color(
                    75,
                    175,
                    255),

            UnitAiState.Search =>
                new Color(
                    245,
                    205,
                    70),

            UnitAiState.Dead =>
                new Color(
                    90,
                    90,
                    90),

            _ =>
                new Color(
                    210,
                    210,
                    210)
        };
    }

    private static void AppendVisionDebug(
        VertexArray area,
        VertexArray debug,
        UnitSimulation simulation,
        WorldMap worldMap,
        int observerIndex,
        float tilePixelSize)
    {
        UnitStore units =
            simulation.Units;

        Vector3 observerEye =
            simulation.Vision.GetEyePosition(
                units,
                observerIndex,
                simulation.Bodies);

        float headAngle =
            MathF.Atan2(
                units.HeadNormal[observerIndex].Y,
                units.HeadNormal[observerIndex].X);

        float range =
            units.ViewRange[observerIndex];

        float fieldOfView =
            units.FieldOfView[observerIndex];

        float centerX =
            units.Position[observerIndex].X *
            tilePixelSize;

        float centerY =
            units.Position[observerIndex].Y *
            tilePixelSize;

        float eyeX =
            observerEye.X *
            tilePixelSize;

        float eyeY =
            observerEye.Y *
            tilePixelSize;

        AppendVisionCone(
            area,
            debug,
            eyeX,
            eyeY,
            headAngle,
            fieldOfView,
            range * tilePixelSize);

        AppendRangeRing(
            debug,
            eyeX,
            eyeY,
            range * tilePixelSize);

        AppendDebugLine(
            debug,
            centerX,
            centerY,
            eyeX,
            eyeY,
            new Color(
                220,
                220,
                220,
                170));

        float directionLength =
            MathF.Min(
                range,
                3f) *
            tilePixelSize;

        AppendDebugLine(
            debug,
            eyeX,
            eyeY,
            eyeX +
            MathF.Cos(headAngle) *
            directionLength,
            eyeY +
            MathF.Sin(headAngle) *
            directionLength,
            new Color(
                255,
                255,
                255,
                220));

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        Span<Vector3> points =
            stackalloc Vector3[
                VisionSystem.VisibilityPointCount];

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int targetIndex =
                active[i];

            if (targetIndex == observerIndex)
                continue;

            VisionCheck check =
                simulation.Vision.Evaluate(
                    units,
                    worldMap,
                    observerIndex,
                    targetIndex,
                    simulation.Bodies);

            Color targetColor =
                GetVisionColor(
                    check.State);

            float targetX =
                check.TargetCenter.X *
                tilePixelSize;

            float targetY =
                check.TargetCenter.Y *
                tilePixelSize;

            AppendCircleOutline(
                debug,
                targetX,
                targetY,
                0.32f * tilePixelSize,
                targetColor,
                12);

            if (check.State ==
                VisionState.OutOfRange ||
                check.State ==
                VisionState.OutsideFieldOfView)
            {
                continue;
            }

            int pointCount = simulation.Vision.GetVisibilityPoints(
                units,
                targetIndex,
                observerEye,
                points,
                simulation.Bodies);

            for (int pointIndex = 0;
                 pointIndex < pointCount;
                 pointIndex++)
            {
                Vector3 point =
                    points[pointIndex];

                bool clear =
                    simulation.Vision.HasLineOfSight(
                        worldMap,
                        observerEye,
                        point,
                        out Vector3 blockingPoint);

                Color pointColor =
                    clear
                        ? new Color(
                            70,
                            220,
                            120,
                            210)
                        : new Color(
                            240,
                            80,
                            80,
                            180);

                float pointX =
                    point.X *
                    tilePixelSize;

                float pointY =
                    point.Y *
                    tilePixelSize;

                AppendCircleOutline(
                    debug,
                    pointX,
                    pointY,
                    0.075f * tilePixelSize,
                    pointColor,
                    8);

                if (clear)
                {
                    AppendDebugLine(
                        debug,
                        eyeX,
                        eyeY,
                        pointX,
                        pointY,
                        pointColor);
                }
                else
                {
                    AppendDebugLine(
                        debug,
                        eyeX,
                        eyeY,
                        blockingPoint.X *
                        tilePixelSize,
                        blockingPoint.Y *
                        tilePixelSize,
                        pointColor);

                    AppendDebugLine(
                        debug,
                        blockingPoint.X *
                        tilePixelSize,
                        blockingPoint.Y *
                        tilePixelSize,
                        pointX,
                        pointY,
                        new Color(
                            240,
                            80,
                            80,
                            70));
                }
            }

            if (check.State ==
                VisionState.HiddenByTerrain)
            {
                AppendCircleOutline(
                    debug,
                    check.BlockingPoint.X *
                    tilePixelSize,
                    check.BlockingPoint.Y *
                    tilePixelSize,
                    0.16f *
                    tilePixelSize,
                    new Color(
                        255,
                        210,
                        70,
                        220),
                    10);
            }
        }
    }

    private static Color GetVisionColor(
        VisionState state)
    {
        return state switch
        {
            VisionState.Visible =>
                new Color(
                    70,
                    220,
                    120,
                    220),

            VisionState.HiddenByTerrain =>
                new Color(
                    240,
                    80,
                    80,
                    220),

            VisionState.OutsideFieldOfView =>
                new Color(
                    235,
                    170,
                    70,
                    220),

            VisionState.OutOfRange =>
                new Color(
                    130,
                    135,
                    145,
                    160),

            _ =>
                new Color(
                    220,
                    220,
                    220,
                    180)
        };
    }

    private static void AppendVisionCone(
        VertexArray area,
        VertexArray debug,
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
                45);

        float halfFov =
            fieldOfViewDegrees *
            MathF.PI /
            360f;

        float start =
            direction -
            halfFov;

        float step =
            fieldOfViewDegrees *
            MathF.PI /
            180f /
            VisionConeSegments;

        for (int i = 0;
             i < VisionConeSegments;
             i++)
        {
            float a0 =
                start +
                step * i;

            float a1 =
                start +
                step * (i + 1);

            area.Append(
                new Vertex(
                    new Vector2f(
                        centerX,
                        centerY),
                    color));

            area.Append(
                new Vertex(
                    new Vector2f(
                        centerX +
                        MathF.Cos(a0) *
                        range,
                        centerY +
                        MathF.Sin(a0) *
                        range),
                    color));

            area.Append(
                new Vertex(
                    new Vector2f(
                        centerX +
                        MathF.Cos(a1) *
                        range,
                        centerY +
                        MathF.Sin(a1) *
                        range),
                    color));
        }

        Color boundary =
            new Color(
                235,
                235,
                235,
                180);

        AppendDebugLine(
            debug,
            centerX,
            centerY,
            centerX +
            MathF.Cos(start) *
            range,
            centerY +
            MathF.Sin(start) *
            range,
            boundary);

        AppendDebugLine(
            debug,
            centerX,
            centerY,
            centerX +
            MathF.Cos(start +
                       step *
                       VisionConeSegments) *
            range,
            centerY +
            MathF.Sin(start +
                       step *
                       VisionConeSegments) *
            range,
            boundary);
    }

    private static void AppendRangeRing(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radius)
    {
        Color color =
            new Color(
                180,
                190,
                200,
                80);

        for (int i = 0;
             i < VisionRingSegments;
             i++)
        {
            float a0 =
                i *
                MathF.Tau /
                VisionRingSegments;

            float a1 =
                (i + 1) *
                MathF.Tau /
                VisionRingSegments;

            AppendDebugLine(
                vertices,
                centerX +
                MathF.Cos(a0) *
                radius,
                centerY +
                MathF.Sin(a0) *
                radius,
                centerX +
                MathF.Cos(a1) *
                radius,
                centerY +
                MathF.Sin(a1) *
                radius,
                color);
        }
    }

    private static void AppendDebugLine(
        VertexArray vertices,
        float x0,
        float y0,
        float x1,
        float y1,
        Color color)
    {
        vertices.Append(
            new Vertex(
                new Vector2f(
                    x0,
                    y0),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    x1,
                    y1),
                color));
    }

    private static void AppendCircleOutline(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radius,
        Color color,
        int segments)
    {
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

            AppendDebugLine(
                vertices,
                centerX +
                MathF.Cos(a0) *
                radius,
                centerY +
                MathF.Sin(a0) *
                radius,
                centerX +
                MathF.Cos(a1) *
                radius,
                centerY +
                MathF.Sin(a1) *
                radius,
                color);
        }
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

    private static bool IsPartInView(
        Vector3 position,
        float extent,
        float minX,
        float maxX,
        float minY,
        float maxY) =>
        position.X + extent >= minX &&
        position.X - extent <= maxX &&
        position.Y + extent >= minY &&
        position.Y - extent <= maxY;


    private static Color Shade(Color color, float amount) =>
        new Color(
            (byte)Math.Clamp((int)(color.R * amount), 0, 255),
            (byte)Math.Clamp((int)(color.G * amount), 0, 255),
            (byte)Math.Clamp((int)(color.B * amount), 0, 255),
            color.A);

    private static Color Blend(Color source, Color tint, float amount) =>
        new Color(
            (byte)Math.Clamp((int)(source.R + (tint.R - source.R) * amount), 0, 255),
            (byte)Math.Clamp((int)(source.G + (tint.G - source.G) * amount), 0, 255),
            (byte)Math.Clamp((int)(source.B + (tint.B - source.B) * amount), 0, 255),
            source.A);

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
