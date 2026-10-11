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
    private const int VisionConeSegments = 20;
    private const int VisionRingSegments = 48;

    private readonly VertexArray _vertices =
        new VertexArray(
            PrimitiveType.Triangles);

    private readonly VertexArray _bodyOutlines =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _equipment =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _weaponVisuals =
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
        _equipment.Clear();
        _weaponVisuals.Clear();
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

        UnitInventoryStore inventory =
            simulation.Inventory;

        Vector3[] bodyNormals =
            units.BodyNormal;

        Vector3[] headNormals =
            units.HeadNormal;

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

            UnitAiState aiState =
                simulation.AI.Store.State[unitIndex];

            if (showAiDebug &&
                simulation.AI.Enabled)
            {
                color = GetAiColor(aiState);

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
            float pixelScale = MathF.Max(1f, tilePixelSize * screenScale);
            Color outlineColor = new Color(20, 22, 26, 238);
            float bodyRotation = MathF.Atan2(bodyNormals[unitIndex].Y, bodyNormals[unitIndex].X);

            // Only active anatomy renderer: every part is one circle at its simulated world coordinate.
            for (int part = start; part < end; part++)
            {
                Vector3 position = partPositions[part];
                Vector3 scale = partScales[part];
                BodyPartRole role = bodyRoles[part];
                float radius = GetBodyDotRadius(role, scale, pixelScale);

                if (!IsPartInView(position, radius + 0.12f,
                    minWorldX, maxWorldX, minWorldY, maxWorldY))
                {
                    continue;
                }

                float pixelX = position.X * tilePixelSize;
                float pixelY = position.Y * tilePixelSize;

                AppendCircleOutline(_bodyOutlines, pixelX, pixelY,
                    radius * tilePixelSize + 0.30f, outlineColor, CircleSegments);
                AppendCircle(_vertices, pixelX, pixelY,
                    radius * tilePixelSize, GetBodyPartColor(role, color));

                int zone = GetArmorZoneIndex(
                    role,
                    LocalSide(position, unitPosition, bodyRotation));

                if (zone < 0)
                    continue;

                for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
                {
                    short itemSlot = inventory.GetBodyEquipment(unitIndex, zone, (ArmorLayer)layer);
                    if (itemSlot < 0 ||
                        inventory.GetItem(unitIndex, itemSlot) is not ArmorConfig apparel)
                    {
                        continue;
                    }

                    float padding = MathF.Max(0.045f, apparel.WorldRenderPadding) + layer * 0.035f;
                    AppendCircleOutline(_equipment, pixelX, pixelY,
                        (radius + padding) * tilePixelSize, GetItemColor(apparel), CircleSegments);
                }
            }

            AppendEquippedWeapons(
                _weaponVisuals, inventory, simulation.Weapons, unitIndex,
                unitPosition, headNormals[unitIndex], partPositions, bodyRoles,
                start, count, tilePixelSize);

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

        if (_equipment.VertexCount > 0)
            window.Draw(_equipment);

        if (_weaponVisuals.VertexCount > 0)
            window.Draw(_weaponVisuals);

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

    private static float GetBodyDotRadius(BodyPartRole role, Vector3 scale, float pixelScale)
    {
        float configured = MathF.Min(MathF.Abs(scale.X), MathF.Abs(scale.Y));
        float minPixels = role switch
        {
            BodyPartRole.Torso => 1.7f,
            BodyPartRole.Head => 1.5f,
            BodyPartRole.Generic or BodyPartRole.Segment => 1.5f,
            BodyPartRole.UpperArm or BodyPartRole.Forearm or BodyPartRole.Thigh or BodyPartRole.Shin => 0.72f,
            BodyPartRole.Neck or BodyPartRole.Elbow or BodyPartRole.Knee => 0.52f,
            BodyPartRole.Hand or BodyPartRole.Foot => 0.68f,
            _ => 0.65f
        };
        return MathF.Max(configured, minPixels / pixelScale);
    }

    private static float LocalSide(Vector3 part, Vector3 unit, float bodyRotation) =>
        -(part.X - unit.X) * MathF.Sin(bodyRotation) +
        (part.Y - unit.Y) * MathF.Cos(bodyRotation);

    private static int GetArmorZoneIndex(BodyPartRole role, float side)
    {
        bool left = side >= 0f;
        return role switch
        {
            BodyPartRole.Head => 0,
            BodyPartRole.Torso or BodyPartRole.Neck => 1,
            BodyPartRole.UpperArm or BodyPartRole.Elbow or BodyPartRole.Forearm => left ? 2 : 3,
            BodyPartRole.Hand => left ? 4 : 5,
            BodyPartRole.Thigh or BodyPartRole.Knee or BodyPartRole.Shin => left ? 6 : 7,
            BodyPartRole.Foot => left ? 8 : 9,
            _ => -1
        };
    }

    private static Color GetItemColor(ItemConfig item) =>
        new Color(item.WorldColorR, item.WorldColorG, item.WorldColorB);

    private static void AppendEquippedWeapons(
        VertexArray vertices, UnitInventoryStore inventory, UnitWeaponStore weapons,
        int unitIndex, Vector3 unitPosition, Vector3 headNormal,
        Vector3[] partPositions, BodyPartRole[] roles,
        int bodyStart, int bodyCount, float tilePixelSize)
    {
        float handX = 0f, handY = 0f;
        int handCount = 0;
        for (int part = bodyStart; part < bodyStart + bodyCount; part++)
        {
            if (roles[part] != BodyPartRole.Hand) continue;
            handX += partPositions[part].X;
            handY += partPositions[part].Y;
            handCount++;
        }

        float directionX = headNormal.X, directionY = headNormal.Y;
        float directionLength = MathF.Sqrt(directionX * directionX + directionY * directionY);
        if (directionLength < 0.001f)
        {
            directionX = 1f;
            directionY = 0f;
        }
        else
        {
            directionX /= directionLength;
            directionY /= directionLength;
        }

        if (handCount > 0)
        {
            handX /= handCount;
            handY /= handCount;
        }
        else
        {
            handX = unitPosition.X + directionX * 0.30f;
            handY = unitPosition.Y + directionY * 0.30f;
        }

        for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
        {
            short itemSlot = inventory.GetWeaponEquipment(unitIndex, (UnitWeaponSlot)slot);
            if (itemSlot < 0 || inventory.GetItem(unitIndex, itemSlot) is not WeaponConfig weapon)
                continue;

            float dx = directionX, dy = directionY;
            int stateIndex = UnitWeaponStore.GetIndex(unitIndex, (UnitWeaponSlot)slot);
            Vector3 aimPoint = weapons.AimPoint[stateIndex];
            if (weapons.AimIndicatorActive[stateIndex] && aimPoint != Vector3.Zero)
            {
                dx = aimPoint.X - unitPosition.X;
                dy = aimPoint.Y - unitPosition.Y;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                if (len > 0.001f) { dx /= len; dy /= len; }
            }

            float offset = slot == (int)UnitWeaponSlot.Secondary ? 0.09f : 0f;
            float cx = handX - directionY * offset + directionX * 0.08f;
            float cy = handY + directionX * offset + directionY * 0.08f;
            AppendOrientedRect(
                vertices,
                cx * tilePixelSize,
                cy * tilePixelSize,
                MathF.Max(0.025f, weapon.WorldRenderWidth * 0.5f) * tilePixelSize,
                MathF.Max(0.12f, weapon.WorldRenderLength * 0.5f) * tilePixelSize,
                MathF.Atan2(dy, dx),
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

    private static Color GetBodyPartColor(
        BodyPartRole role,
        Color baseColor) =>
        role switch
        {
            BodyPartRole.Torso => Shade(baseColor, 0.83f),
            BodyPartRole.Neck => Shade(baseColor, 0.90f),
            BodyPartRole.Head => Blend(baseColor, new Color(220, 205, 185), 0.48f),
            BodyPartRole.UpperArm => Shade(baseColor, 0.74f),
            BodyPartRole.Forearm => Shade(baseColor, 0.80f),
            BodyPartRole.Elbow => Shade(baseColor, 0.76f),
            BodyPartRole.Hand => Blend(baseColor, new Color(207, 179, 151), 0.42f),
            BodyPartRole.Thigh => Shade(baseColor, 0.65f),
            BodyPartRole.Knee => Shade(baseColor, 0.60f),
            BodyPartRole.Shin => Shade(baseColor, 0.58f),
            BodyPartRole.Foot => Shade(baseColor, 0.48f),
            BodyPartRole.Segment => Shade(baseColor, 0.88f),
            _ => baseColor
        };

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
