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
    private const int CircleSegments = 10;
    private const int CapsuleHalfSegments = 4;
    private const int VisionConeSegments = 20;
    private const int VisionRingSegments = 48;

    private readonly VertexArray _vertices =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _bodyOutlines =
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

        _vertices.Clear();
        _bodyOutlines.Clear();
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

        if (_aimTargetDrawn.Length < units.Capacity)
            Array.Resize(ref _aimTargetDrawn, units.Capacity);

        Array.Clear(_aimTargetDrawn);

        UnitBodyStore bodies =
            simulation.Bodies;

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        Vector3[] positions =
            units.Position;

        float[] widths =
            units.Width;

        float[] lengths =
            units.Length;

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

        BodyPartRole[] bodyRoles =
            bodies.Role;

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
            if (simulation.Health.OverallHitPoints[unitIndex] > 0f)
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
                        unitIndex).State;

                if (state != VisionState.Visible)
                    continue;
            }

            int bodyStart = bodyStarts[unitIndex];
            int bodyCount = bodyCounts[unitIndex];
            bool rootVisible =
                unitPosition.X >= minWorldX &&
                unitPosition.X <= maxWorldX &&
                unitPosition.Y >= minWorldY &&
                unitPosition.Y <= maxWorldY;

            if (!rootVisible &&
                !IsBodyVisible(
                    bodyStart,
                    bodyCount,
                    partPositions,
                    partScales,
                    minWorldX,
                    maxWorldX,
                    minWorldY,
                    maxWorldY))
            {
                continue;
            }

            float screenX = unitPosition.X * tilePixelSize;
            float screenY = unitPosition.Y * tilePixelSize;

            if (simulation.Health.OverallHitPoints[unitIndex] <= 0f)
            {
                AppendDeathMarker(
                    _selection,
                    screenX,
                    screenY,
                    MathF.Max(3f, tilePixelSize * 0.62f));
                continue;
            }

            Color color =
                new Color(
                    238,
                    238,
                    238);

            AppendFactionMarker(
                _selection,
                units.FactionTag[unitIndex],
                unitPosition.X * tilePixelSize,
                unitPosition.Y * tilePixelSize,
                MathF.Max(
                    MathF.Max(
                        widths[unitIndex],
                        lengths[unitIndex]) * 0.72f,
                    0.55f) * tilePixelSize);

            UnitAiState aiState =
                simulation.AI.Store.State[unitIndex];

            if (showAiDebug &&
                simulation.AI.Enabled)
            {
                color = GetAiColor(aiState);

                AppendAiStateMarker(
                    _selection,
                    screenX,
                    screenY,
                    tilePixelSize,
                    color);

                // Full target/goal vectors are useful for the selected unit,
                // but drawing 800 vectors at once hides the battlefield.
                if (hasSelection && selectedIndex == unitIndex)
                {
                    AppendAiDebug(
                        _aiDebug,
                        simulation,
                        unitIndex,
                        tilePixelSize);

                    AppendSuppressionDebug(
                        _aiDebug,
                        simulation,
                        unitIndex,
                        tilePixelSize);
                }
            }

            int start = bodyStarts[unitIndex];
            int count = bodyCounts[unitIndex];
            int end = start + count;
            float screenScale = window.Size.X / MathF.Max(1f, cameraView.Size.X);
            float outline = 0.42f / MathF.Max(1f, tilePixelSize * screenScale);
            Color outlineColor = new Color(20, 22, 26, 238);

            // Outline every part first. Fill batches are layered afterwards,
            // so legs disappear under the torso instead of covering it.
            for (int part = start; part < end; part++)
            {
                Vector3 position = partPositions[part];
                Vector3 scale = partScales[part];
                float radiusX = MathF.Max(0.015f, MathF.Abs(scale.X));
                float radiusY = MathF.Max(0.015f, MathF.Abs(scale.Y));
                float extent = MathF.Sqrt(
                    radiusX * radiusX +
                    radiusY * radiusY) + outline;

                if (!IsPartInView(
                    position,
                    extent,
                    minWorldX,
                    maxWorldX,
                    minWorldY,
                    maxWorldY))
                {
                    continue;
                }

                AppendBodyPart(
                    _bodyOutlines,
                    primitives[part],
                    bodyRoles[part],
                    position.X * tilePixelSize,
                    position.Y * tilePixelSize,
                    radiusX * tilePixelSize,
                    radiusY * tilePixelSize,
                    partRotations[part],
                    outline * tilePixelSize,
                    outlineColor);
            }

            // Back-to-front fill order: legs, torso/body, arms/hands, head.
            for (int layer = 0; layer < 4; layer++)
            {
                for (int part = start; part < end; part++)
                {
                    BodyPartRole role = bodyRoles[part];
                    if (GetBodyLayer(role) != layer)
                        continue;

                    Vector3 position = partPositions[part];
                    Vector3 scale = partScales[part];
                    float radiusX = MathF.Max(0.015f, MathF.Abs(scale.X));
                    float radiusY = MathF.Max(0.015f, MathF.Abs(scale.Y));
                    float extent = MathF.Sqrt(
                        radiusX * radiusX +
                        radiusY * radiusY);

                    if (!IsPartInView(
                        position,
                        extent,
                        minWorldX,
                        maxWorldX,
                        minWorldY,
                        maxWorldY))
                    {
                        continue;
                    }

                    float pixelX = position.X * tilePixelSize;
                    float pixelY = position.Y * tilePixelSize;
                    float rotation = partRotations[part];

                    AppendBodyPart(
                        _vertices,
                        primitives[part],
                        role,
                        pixelX,
                        pixelY,
                        radiusX * tilePixelSize,
                        radiusY * tilePixelSize,
                        rotation,
                        0f,
                        GetBodyPartColor(role, color));

                    if (role == BodyPartRole.Head)
                    {
                        float faceRadius = MathF.Max(
                            0.30f,
                            MathF.Min(radiusX, radiusY) * tilePixelSize * 0.18f);

                        AppendEllipse(
                            _vertices,
                            pixelX + MathF.Cos(rotation) * radiusX * tilePixelSize * 0.48f,
                            pixelY + MathF.Sin(rotation) * radiusX * tilePixelSize * 0.48f,
                            faceRadius,
                            faceRadius,
                            rotation,
                            new Color(28, 30, 34, 255));
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

        if (_bodyOutlines.VertexCount > 0)
        {
            window.Draw(
                _bodyOutlines);
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

    private static void AppendAiStateMarker(
        VertexArray vertices,
        float x,
        float y,
        float tilePixelSize,
        Color color)
    {
        float radius = MathF.Max(1.7f, tilePixelSize * 0.28f);
        AppendCircleOutline(vertices, x, y, radius, color, 8);
        AppendDebugLine(
            vertices,
            x - radius * 0.55f,
            y,
            x + radius * 0.55f,
            y,
            color);
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
                observerIndex);

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
                    targetIndex);

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

            simulation.Vision.GetVisibilityPoints(
                units,
                targetIndex,
                points);

            for (int pointIndex = 0;
                 pointIndex < points.Length;
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

    private static void AppendFactionMarker(
        VertexArray vertices,
        ushort factionTag,
        float centerX,
        float centerY,
        float radius)
    {
        if (factionTag == 0)
            return;

        Color color =
            factionTag == 1
                ? new Color(
                    90,
                    170,
                    255,
                    220)
                : new Color(
                    255,
                    130,
                    90,
                    220);

        AppendCircleOutline(
            vertices,
            centerX,
            centerY,
            radius,
            color,
            10);
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

    private static bool IsBodyVisible(
        int start,
        int count,
        Vector3[] positions,
        Vector3[] scales,
        float minX,
        float maxX,
        float minY,
        float maxY)
    {
        int end = start + count;
        for (int part = start; part < end; part++)
        {
            Vector3 position = positions[part];
            Vector3 scale = scales[part];
            float radiusX = MathF.Max(0.06f, MathF.Abs(scale.X));
            float radiusY = MathF.Max(0.06f, MathF.Abs(scale.Y));
            float extent = MathF.Sqrt(
                radiusX * radiusX +
                radiusY * radiusY);

            if (position.X + extent >= minX &&
                position.X - extent <= maxX &&
                position.Y + extent >= minY &&
                position.Y - extent <= maxY)
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendEllipse(
        VertexArray vertices,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float rotation,
        Color color)
    {
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);

        for (int i = 0; i < CircleSegments; i++)
        {
            float a0 = i * MathF.Tau / CircleSegments;
            float a1 = (i + 1) * MathF.Tau / CircleSegments;
            float x0 = radiusX * MathF.Cos(a0);
            float y0 = radiusY * MathF.Sin(a0);
            float x1 = radiusX * MathF.Cos(a1);
            float y1 = radiusY * MathF.Sin(a1);

            vertices.Append(new Vertex(new Vector2f(centerX, centerY), color));
            vertices.Append(new Vertex(
                new Vector2f(
                    centerX + x0 * cos - y0 * sin,
                    centerY + x0 * sin + y0 * cos),
                color));
            vertices.Append(new Vertex(
                new Vector2f(
                    centerX + x1 * cos - y1 * sin,
                    centerY + x1 * sin + y1 * cos),
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

    private static int GetBodyLayer(BodyPartRole role) =>
        role switch
        {
            BodyPartRole.Thigh or
            BodyPartRole.Shin or
            BodyPartRole.Foot => 0,

            BodyPartRole.Torso or
            BodyPartRole.Segment or
            BodyPartRole.Generic => 1,

            BodyPartRole.UpperArm or
            BodyPartRole.Forearm or
            BodyPartRole.Hand => 2,

            BodyPartRole.Head => 3,
            _ => 1
        };

    private static Color GetBodyPartColor(
        BodyPartRole role,
        Color baseColor)
    {
        return role switch
        {
            BodyPartRole.Torso => Shade(baseColor, 0.83f),
            BodyPartRole.Head => Blend(baseColor, new Color(220, 205, 185), 0.48f),
            BodyPartRole.UpperArm => Shade(baseColor, 0.74f),
            BodyPartRole.Forearm => Shade(baseColor, 0.80f),
            BodyPartRole.Hand => Blend(baseColor, new Color(207, 179, 151), 0.42f),
            BodyPartRole.Thigh => Shade(baseColor, 0.65f),
            BodyPartRole.Shin => Shade(baseColor, 0.58f),
            BodyPartRole.Foot => Shade(baseColor, 0.48f),
            BodyPartRole.Segment => Shade(baseColor, 0.88f),
            _ => baseColor
        };
    }

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

    private static void AppendBodyPart(
        VertexArray vertices,
        BodyPrimitive primitive,
        BodyPartRole role,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float rotation,
        float expansion,
        Color color)
    {
        radiusX = MathF.Max(0.01f, radiusX + expansion);
        radiusY = MathF.Max(0.01f, radiusY + expansion);

        bool capsule =
            role == BodyPartRole.Torso ||
            role == BodyPartRole.UpperArm ||
            role == BodyPartRole.Forearm ||
            role == BodyPartRole.Thigh ||
            role == BodyPartRole.Shin ||
            role == BodyPartRole.Foot ||
            role == BodyPartRole.Segment;

        if (capsule)
        {
            AppendCapsule(
                vertices,
                centerX,
                centerY,
                radiusX,
                radiusY,
                rotation,
                color);
        }
        else if (primitive == BodyPrimitive.Circle ||
                 role == BodyPartRole.Head ||
                 role == BodyPartRole.Hand)
        {
            AppendEllipse(
                vertices,
                centerX,
                centerY,
                radiusX,
                radiusY,
                rotation,
                color);
        }
        else
        {
            AppendBox(
                vertices,
                centerX,
                centerY,
                radiusX,
                radiusY,
                rotation,
                color);
        }
    }

    private static void AppendCapsule(
        VertexArray vertices,
        float centerX,
        float centerY,
        float halfLength,
        float halfWidth,
        float rotation,
        Color color)
    {
        float radius = MathF.Min(halfLength, halfWidth);
        float straightHalfLength = MathF.Max(0f, MathF.Max(halfLength, halfWidth) - radius);
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        float endX = cos * straightHalfLength;
        float endY = sin * straightHalfLength;
        Span<Vector2f> points = stackalloc Vector2f[CapsuleHalfSegments * 2 + 2];
        int pointCount = 0;

        for (int i = 0; i <= CapsuleHalfSegments; i++)
        {
            float angle = rotation - MathF.PI * 0.5f +
                MathF.PI * i / CapsuleHalfSegments;

            points[pointCount++] = new Vector2f(
                centerX + endX + MathF.Cos(angle) * radius,
                centerY + endY + MathF.Sin(angle) * radius);
        }

        for (int i = 0; i <= CapsuleHalfSegments; i++)
        {
            float angle = rotation + MathF.PI * 0.5f +
                MathF.PI * i / CapsuleHalfSegments;

            points[pointCount++] = new Vector2f(
                centerX - endX + MathF.Cos(angle) * radius,
                centerY - endY + MathF.Sin(angle) * radius);
        }

        Vector2f center = new Vector2f(centerX, centerY);

        for (int i = 0; i < pointCount; i++)
        {
            int next = (i + 1) % pointCount;
            vertices.Append(new Vertex(center, color));
            vertices.Append(new Vertex(points[i], color));
            vertices.Append(new Vertex(points[next], color));
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
