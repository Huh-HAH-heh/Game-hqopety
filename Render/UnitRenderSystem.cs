using Core.Combat;
using Core.AI.Behaviac;
using Core.AI.Perception;
using Core.Items;
using Core.Map;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RimClone.Render;

public sealed class UnitRenderSystem : IDisposable
{
    private const int CircleSegments = 16;
    private const float UnitVisualScale = 0.8f;
    private const float UnitDetailFadeStartPixels = 8f;
    private const float UnitDetailFadeEndPixels = 12f;
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

    private readonly VertexArray _soundDebug =
        new VertexArray(
            PrimitiveType.Lines);

    private readonly VertexArray _suppressionDebug =
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
        bool hideUnseenTargets = false,
        HashSet<UnitId>? selectedUnits = null,
        bool showUnitRig = true,
        bool showHearingDebug = false,
        bool showAttackLinks = true,
        bool showSuppressionDebug = false)
    {
        if (tilePixelSize <= 0f)
            return;

        float screenScale =
            MathF.Max(
                0.001f,
                window.Size.X / MathF.Max(1f, cameraView.Size.X));

        float pixelsPerWorldUnit =
            tilePixelSize * screenScale;

        float detailBlend = GetUnitDetailBlend(pixelsPerWorldUnit);
        bool showUnitDetails = detailBlend > 0f;
        bool showRigDetails = showUnitRig;

        _vertices.Clear();
        _bodyOutlines.Clear();
        _statusIcons.Clear();
        _visionArea.Clear();
        _selection.Clear();
        _aiming.Clear();
        _visionDebug.Clear();
        _aiDebug.Clear();
        _soundDebug.Clear();
        _suppressionDebug.Clear();

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

        int[] bodyStarts = units.BodyStart;
        short[] bodyCounts = units.BodyCount;
        UnitBodyStore bodies = simulation.Bodies;

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
            bool aliveForAttackLinks =
                simulation.Health.OverallHitPoints[unitIndex] > 0f;
            if (showUnitDetails && aliveForAttackLinks)
            {
                AppendAimProgress(
                    _aiming,
                    simulation,
                    unitIndex,
                    tilePixelSize,
                    _aimTargetDrawn);
            }

            if (showAttackLinks && aliveForAttackLinks)
            {
                AppendActiveAttackLinks(
                    _aiming,
                    simulation,
                    unitIndex,
                    tilePixelSize);
            }

            if (showSuppressionDebug && aliveForAttackLinks)
                AppendSuppressionDebug(_suppressionDebug, simulation, unitIndex, tilePixelSize);

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

            // Only the gameplay collider contributes to the world-space footprint.
            if (!rootVisible)
                continue;

            float screenX = unitPosition.X * tilePixelSize;
            float screenY = unitPosition.Y * tilePixelSize;
            float hitPoints = simulation.Health.OverallHitPoints[unitIndex];
            bool isAlive = hitPoints > 0f;
            float markerRadius = GetMarkerRadius(
                widths[unitIndex],
                lengths[unitIndex],
                tilePixelSize,
                screenScale);

            int renderedParts = 0;
            if (showRigDetails)
            {
                UnitHitRegionProfile? hitProfile =
                    simulation.HitRegions.GetProfile(unitIndex);

                if (units.Type[unitIndex] == UnitType.Colonist &&
                    hitProfile is UnitAnatomyProfile)
                {
                    renderedParts = AppendGameplayZones(
                        _vertices,
                        _bodyOutlines,
                        units,
                        unitIndex,
                        units.FactionTag[unitIndex],
                        isAlive,
                        tilePixelSize,
                        screenScale);
                }
                else
                {
                    renderedParts = AppendBodyParts(
                        _vertices,
                        _bodyOutlines,
                        bodies,
                        bodyStarts[unitIndex],
                        bodyCounts[unitIndex],
                        units.FactionTag[unitIndex],
                        isAlive,
                        tilePixelSize,
                        screenScale,
                        1f);
                }
            }

            // A marker is a substitute for the rig, never an overlay on it.
            // Rig mode renders anatomy; marker-only mode renders this silhouette.
            float markerOpacity = showUnitRig ? 0f : 1f;
            if (markerOpacity > 0f)
            {
                AppendLowDetailUnitMarker(
                    _vertices,
                    screenX,
                    screenY,
                    markerRadius,
                    units.FactionTag[unitIndex],
                    isAlive,
                    markerOpacity);

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

            bool isSelected = selectedUnits != null
                ? selectedUnits.Contains(units.GetId(unitIndex))
                : hasSelection && selectedIndex == unitIndex;

            if (isSelected)
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
                    UnitVisualScale *
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

        if (showHearingDebug)
        {
            SimulationFrameSnapshot.CaptureHearingDebug(
                simulation, out RenderSoundPulse[] soundPulses,
                out RenderAuditoryContact[] soundContacts);
            AppendSoundDebug(_soundDebug, soundPulses, soundContacts, cameraView, tilePixelSize);
        }

        if (_visionArea.VertexCount > 0)
        {
            window.Draw(
                _visionArea);
        }

        if (_vertices.VertexCount > 0)
            window.Draw(_vertices);


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
        if (_soundDebug.VertexCount > 0)
            window.Draw(_soundDebug);
        if (_suppressionDebug.VertexCount > 0)
            window.Draw(_suppressionDebug);
    }

public void DrawSnapshot(
        RenderWindow window,
        SimulationFrameSnapshot snapshot,
        View cameraView,
        float tilePixelSize,
        int selectedUnitIndex,
        bool showAiDebug,
        HashSet<UnitId>? selectedUnits = null,
        bool showUnitRig = true,
        bool showVisionDebug = false,
        bool showHearingDebug = false,
        bool showAttackLinks = true,
        bool showSuppressionDebug = false)
    {
        if (tilePixelSize <= 0f)
            return;

        float screenScale = MathF.Max(
            0.001f,
            window.Size.X / MathF.Max(1f, cameraView.Size.X));
        float detailBlend = GetUnitDetailBlend(tilePixelSize * screenScale);
        bool showUnitDetails = detailBlend > 0f;
        bool showRigDetails = showUnitRig;

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
        _aiming.Clear();
        _visionArea.Clear();
        _visionDebug.Clear();
        _aiDebug.Clear();
        _soundDebug.Clear();
        _suppressionDebug.Clear();

        RenderUnitState[] units = snapshot.Units;
        RenderBodyPart[] bodyParts = snapshot.BodyParts;
        for (int i = 0; i < units.Length; i++)
        {
            RenderUnitState unit = units[i];
            Vector3 position = unit.Position;
            float x = position.X * tilePixelSize;
            float y = position.Y * tilePixelSize;
            bool isSelected = selectedUnits != null
                ? selectedUnits.Contains(new UnitId(unit.Index, unit.Generation))
                : unit.Index == selectedUnitIndex;

            // I shows the complete intent of every selected pawn: its hostile
            // target, movement/cover goal, and a color-coded intent marker.
            if (showAiDebug && isSelected && unit.HitPoints > 0f)
            {
                Color intentColor = GetIntentColor(unit.AiIntent, unit.AiState);
                Color stateColor = GetAiColor(unit.AiState);

                AppendCircleOutline(
                    _aiDebug,
                    x,
                    y,
                    0.27f * tilePixelSize,
                    intentColor,
                    10);

                if (unit.HasTarget)
                {
                    float targetX = unit.TargetPosition.X * tilePixelSize;
                    float targetY = unit.TargetPosition.Y * tilePixelSize;
                    AppendDebugLine(
                        _aiDebug,
                        x,
                        y,
                        targetX,
                        targetY,
                        new Color(intentColor.R, intentColor.G, intentColor.B, 210));
                    AppendCircleOutline(
                        _aiDebug,
                        targetX,
                        targetY,
                        0.18f * tilePixelSize,
                        intentColor,
                        8);
                }

                if (unit.HasMoveTarget)
                {
                    float moveX = unit.MoveTargetPosition.X * tilePixelSize;
                    float moveY = unit.MoveTargetPosition.Y * tilePixelSize;
                    AppendDebugLine(
                        _aiDebug,
                        x,
                        y,
                        moveX,
                        moveY,
                        new Color(stateColor.R, stateColor.G, stateColor.B, 165));
                    AppendCircleOutline(
                        _aiDebug,
                        moveX,
                        moveY,
                        0.12f * tilePixelSize,
                        stateColor,
                        8);
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
                        stateColor,
                        12);
                    AppendDebugLine(
                        _aiDebug,
                        x,
                        y,
                        goalX,
                        goalY,
                        new Color(stateColor.R, stateColor.G, stateColor.B, 180));
                }
            }

            bool isAlive = unit.HitPoints > 0f;
            if (showAttackLinks && isAlive)
                AppendSnapshotAttackLinks(_aiming, unit, x, y, tilePixelSize);

            bool rootInView =
                position.X >= minX && position.X <= maxX &&
                position.Y >= minY && position.Y <= maxY;

            // At macro zoom, individual limbs have no useful screen-space size.
            // Don't scan every body part just to reject an off-screen unit.
            if (!rootInView &&
                (!showRigDetails ||
                 !IsAnyBodyPartInView(
                    bodyParts,
                    unit.BodyPartStart,
                    unit.BodyPartCount,
                    minX,
                    maxX,
                    minY,
                    maxY)))
            {
                continue;
            }

            if (showSuppressionDebug && isAlive && rootInView)
                AppendSnapshotSuppressionDebug(_suppressionDebug, unit, x, y, tilePixelSize);

            float markerRadius = GetMarkerRadius(
                unit.Width,
                unit.Length,
                tilePixelSize,
                screenScale);

            int renderedParts = 0;
            if (showRigDetails)
            {
                if (unit.Type == UnitType.Colonist &&
                    unit.BodyPartCount == UnitGameplayZoneSystem.ZoneCount)
                {
                    // These are the three gameplay damage zones, not the
                    // informational anatomical rig. Shrink only their visuals
                    // around the root; hit testing continues to use full geometry.
                    int count = Math.Clamp(unit.BodyPartCount, 0, bodyParts.Length -
                        Math.Clamp(unit.BodyPartStart, 0, bodyParts.Length));
                    int start = Math.Clamp(unit.BodyPartStart, 0, bodyParts.Length);
                    for (int partIndex = start; partIndex < start + count; partIndex++)
                    {
                        RenderBodyPart part = bodyParts[partIndex];
                        AppendBodyPart(
                            _vertices,
                            _bodyOutlines,
                            position + (part.Position - position) * UnitVisualScale,
                            part.Scale * UnitVisualScale,
                            part.Rotation,
                            part.Primitive,
                            part.Role,
                            unit.FactionTag,
                            isAlive,
                            tilePixelSize,
                            screenScale);
                        renderedParts++;
                    }
                }
                else
                {
                    renderedParts = AppendBodyParts(
                        _vertices,
                        _bodyOutlines,
                        bodyParts,
                        unit.BodyPartStart,
                        unit.BodyPartCount,
                        unit.FactionTag,
                        isAlive,
                        tilePixelSize,
                        screenScale);
                }
            }

            // Marker-only mode draws the full silhouette. When rig mode is on,
            // the anatomical parts replace the marker completely.
            float markerOpacity = showUnitRig ? 0f : 1f;
            if (markerOpacity > 0f)
            {
                AppendLowDetailUnitMarker(
                    _vertices,
                    x,
                    y,
                    markerRadius,
                    unit.FactionTag,
                    isAlive,
                    markerOpacity);

            }


            if (!isAlive)
                continue;

            if (isSelected)
            {
                float selectionRadius = MathF.Max(unit.Width, unit.Length) * 0.6f;
                AppendSelection(
                    _selection,
                    x,
                    y,
                    MathF.Max(selectionRadius, 0.45f) * UnitVisualScale * tilePixelSize);
            }
        }

        if (showVisionDebug &&
            snapshot.VisionDebugObserver.IsValid &&
            snapshot.VisionDebugObserver.Index == selectedUnitIndex)
        {
            RenderUnitState observer = default;
            bool observerFound = false;
            for (int unitIndex = 0; unitIndex < units.Length; unitIndex++)
            {
                if (units[unitIndex].Index != selectedUnitIndex ||
                    units[unitIndex].Generation != snapshot.VisionDebugObserver.Generation)
                    continue;

                observer = units[unitIndex];
                observerFound = true;
                break;
            }

            if (observerFound)
            {
                AppendSnapshotVisionDebug(
                    _visionArea,
                    _visionDebug,
                    snapshot,
                    observer,
                    snapshot.VisionDebugObserverEye,
                    tilePixelSize);
            }
        }

        if (showHearingDebug)
            AppendSoundDebug(_soundDebug, snapshot.SoundPulses, snapshot.SoundContacts, cameraView, tilePixelSize);

        if (_visionArea.VertexCount > 0)
            window.Draw(_visionArea);

        MeleeHitVisual[] meleeHitVisuals = snapshot.MeleeHitVisuals;
        for (int i = 0; i < meleeHitVisuals.Length; i++)
        {
            MeleeHitVisual hit = meleeHitVisuals[i];
            if (hit.Position.X < minX || hit.Position.X > maxX ||
                hit.Position.Y < minY || hit.Position.Y > maxY)
            {
                continue;
            }

            float hitX = hit.Position.X * tilePixelSize;
            float hitY = hit.Position.Y * tilePixelSize;
            float radius = MathF.Max(2f, tilePixelSize * 0.32f);
            byte alpha = (byte)Math.Clamp(
                (int)(255f * hit.Lifetime / 0.24f),
                0,
                255);
            Color flash = hit.FactionTag switch
            {
                1 => new Color(95, 190, 255, alpha),
                2 => new Color(255, 105, 95, alpha),
                3 => new Color(255, 205, 65, alpha),
                _ => new Color(240, 240, 240, alpha)
            };

            // Brief crossed slash at the anatomical impact point; the flash
            // is published with the immutable frame snapshot, not read live.
            AppendDebugLine(
                _statusIcons,
                hitX - radius, hitY - radius,
                hitX + radius, hitY + radius,
                flash);
            AppendDebugLine(
                _statusIcons,
                hitX - radius, hitY + radius,
                hitX + radius, hitY - radius,
                flash);
        }

        if (showUnitDetails)
        {
            RenderAimRing[] aimRings = snapshot.AimRings;
            for (int i = 0; i < aimRings.Length; i++)
            {
                RenderAimRing aim = aimRings[i];

                // Aim rings outside the camera never contribute visible pixels.
                if (aim.AimPoint.X < minX || aim.AimPoint.X > maxX ||
                    aim.AimPoint.Y < minY || aim.AimPoint.Y > maxY)
                {
                    continue;
                }

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
        if (_bodyOutlines.VertexCount > 0)
            window.Draw(_bodyOutlines);
        if (_statusIcons.VertexCount > 0)
            window.Draw(_statusIcons);
        if (_selection.VertexCount > 0)
            window.Draw(_selection);
        if (_aiming.VertexCount > 0)
            window.Draw(_aiming);
        if (_visionDebug.VertexCount > 0)
            window.Draw(_visionDebug);
        if (_aiDebug.VertexCount > 0)
            window.Draw(_aiDebug);
        if (_soundDebug.VertexCount > 0)
            window.Draw(_soundDebug);
        if (_suppressionDebug.VertexCount > 0)
            window.Draw(_suppressionDebug);
    }

    private static int AppendBodyParts(
        VertexArray fills,
        VertexArray outlines,
        UnitBodyStore bodies,
        int start,
        int count,
        ushort factionTag,
        bool isAlive,
        float tilePixelSize,
        float screenScale,
        float opacity = 1f)
    {
        if (start < 0 || count <= 0 || start >= bodies.Count)
            return 0;

        count = Math.Min(count, bodies.Count - start);
        Vector3[] positions = bodies.WorldPosition;
        Vector3[] scales = bodies.WorldScale;
        float[] rotations = bodies.WorldRotation;
        BodyPrimitive[] primitives = bodies.Primitive;
        BodyPartRole[] roles = bodies.Role;

        int rendered = 0;
        for (int i = start; i < start + count; i++)
        {
            if (roles[i] == BodyPartRole.Internal)
                continue;

            AppendBodyPart(
                fills,
                outlines,
                positions[i],
                scales[i],
                rotations[i],
                primitives[i],
                roles[i],
                factionTag,
                isAlive,
                tilePixelSize,
                screenScale,
                opacity);
            rendered++;
        }

        return rendered;
    }

    private static int AppendBodyParts(
        VertexArray fills,
        VertexArray outlines,
        RenderBodyPart[] bodyParts,
        int start,
        int count,
        ushort factionTag,
        bool isAlive,
        float tilePixelSize,
        float screenScale,
        float opacity = 1f)
    {
        if (start < 0 || count <= 0 || start >= bodyParts.Length)
            return 0;

        count = Math.Min(count, bodyParts.Length - start);
        int rendered = 0;
        for (int i = start; i < start + count; i++)
        {
            RenderBodyPart part = bodyParts[i];
            if (part.Role == BodyPartRole.Internal)
                continue;

            AppendBodyPart(
                fills,
                outlines,
                part.Position,
                part.Scale,
                part.Rotation,
                part.Primitive,
                part.Role,
                factionTag,
                isAlive,
                tilePixelSize,
                screenScale,
                opacity);
            rendered++;
        }

        return rendered;
    }

    private static int AppendGameplayZones(
        VertexArray fills,
        VertexArray outlines,
        UnitStore units,
        int unitIndex,
        ushort factionTag,
        bool isAlive,
        float tilePixelSize,
        float screenScale)
    {
        Vector3 origin = units.Position[unitIndex];
        for (int zoneIndex = 0; zoneIndex < UnitGameplayZoneSystem.ZoneCount; zoneIndex++)
        {
            UnitGameplayZoneGeometry zone = UnitGameplayZoneSystem.GetGeometry(
                units, unitIndex, zoneIndex);
            RenderBodyPart part = UnitGameplayZoneRenderGeometry.CreatePart(zone);
            AppendBodyPart(
                fills,
                outlines,
                origin + (part.Position - origin) * UnitVisualScale,
                part.Scale * UnitVisualScale,
                part.Rotation,
                part.Primitive,
                part.Role,
                factionTag,
                isAlive,
                tilePixelSize,
                screenScale);
        }

        return UnitGameplayZoneSystem.ZoneCount;
    }

    private static int AppendHitRegions(
        VertexArray fills,
        VertexArray outlines,
        UnitStore units,
        UnitHitRegionStore hitRegions,
        int unitIndex,
        ushort factionTag,
        bool isAlive,
        float tilePixelSize,
        float screenScale)
    {
        UnitHitRegionProfile? profile = hitRegions.GetProfile(unitIndex);
        if (profile == null)
            return 0;

        ReadOnlySpan<UnitCircleHitRegion> regions = profile.Regions;
        for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
        {
            UnitCircleHitRegion region = regions[regionIndex];
            UnitHitRegionGeometry geometry = UnitHitSystem.GetRegionGeometry(
                units,
                unitIndex,
                regionIndex,
                hitRegions);
            RenderBodyPart part = UnitHitRegionRenderGeometry.CreatePart(
                units,
                unitIndex,
                region,
                geometry);

            AppendBodyPart(
                fills,
                outlines,
                part.Position,
                part.Scale,
                part.Rotation,
                part.Primitive,
                part.Role,
                factionTag,
                isAlive,
                tilePixelSize,
                screenScale);
        }

        return regions.Length;
    }

    private static void AppendBodyPart(
        VertexArray fills,
        VertexArray outlines,
        Vector3 position,
        Vector3 scale,
        float rotation,
        BodyPrimitive primitive,
        BodyPartRole role,
        ushort factionTag,
        bool isAlive,
        float tilePixelSize,
        float screenScale,
        float opacity = 1f)
    {
        float radiusX = MathF.Abs(scale.X) * tilePixelSize;
        float radiusY = MathF.Abs(scale.Y) * tilePixelSize;
        if (radiusX <= 0f ||
            (primitive != BodyPrimitive.Line && radiusY <= 0f))
            return;

        float centerX = position.X * tilePixelSize;
        float centerY = position.Y * tilePixelSize;

        Color fill = ApplyOpacity(GetBodyPartColor(role, factionTag, isAlive), opacity);
        Color outline = ApplyOpacity(new Color(20, 26, 32, 245), opacity);

        if (primitive == BodyPrimitive.Line)
        {
            float dx = MathF.Cos(rotation) * radiusX;
            float dy = MathF.Sin(rotation) * radiusX;

            AppendDebugLine(
                outlines,
                centerX - dx,
                centerY - dy,
                centerX + dx,
                centerY + dy,
                fill);
            return;
        }

        int segments = MathF.Max(radiusX, radiusY) * screenScale >= 5f
            ? 16
            : 10;

        if (primitive == BodyPrimitive.Box)
        {
            AppendOrientedRect(
                fills,
                centerX,
                centerY,
                radiusY,
                radiusX,
                rotation,
                fill);
        }
        else if (primitive == BodyPrimitive.Triangle)
        {
            AppendTriangleFill(
                fills,
                centerX,
                centerY,
                radiusX,
                radiusY,
                rotation,
                fill);
        }
        else
        {
            AppendEllipse(
                fills,
                centerX,
                centerY,
                radiusX,
                radiusY,
                rotation,
                fill,
                segments);
        }

        // Keep the outline slightly larger than the fill to preserve readability.
        float outlinePad = 0.45f / MathF.Max(0.001f, screenScale);
        if (primitive == BodyPrimitive.Box)
        {
            AppendOrientedRectOutline(
                outlines,
                centerX,
                centerY,
                radiusY + outlinePad,
                radiusX + outlinePad,
                rotation,
                outline);
        }
        else if (primitive == BodyPrimitive.Triangle)
        {
            AppendTriangleOutline(
                outlines,
                centerX,
                centerY,
                radiusX + outlinePad,
                radiusY + outlinePad,
                rotation,
                outline);
        }
        else
        {
            AppendEllipseOutline(
                outlines,
                centerX,
                centerY,
                radiusX + outlinePad,
                radiusY + outlinePad,
                rotation,
                outline,
                segments);
        }
    }

    private static Color GetBodyPartColor(
        BodyPartRole role,
        ushort factionTag,
        bool isAlive)
    {
        Color faction = factionTag switch
        {
            1 => new Color(76, 125, 171, 255),
            2 => new Color(167, 76, 80, 255),
            3 => new Color(186, 146, 49, 255),
            _ => new Color(122, 137, 151, 255)
        };

        Color color = role switch
        {
            BodyPartRole.Head =>
                Blend(faction, new Color(219, 202, 176, 255), 0.58f),

            BodyPartRole.Torso =>
                Shade(faction, 0.92f),

            BodyPartRole.UpperArm or
            BodyPartRole.Forearm or
            BodyPartRole.Thigh or
            BodyPartRole.Shin =>
                Blend(faction, new Color(190, 200, 210, 255), 0.20f),

            BodyPartRole.Hand or
            BodyPartRole.Foot =>
                Blend(faction, new Color(218, 205, 183, 255), 0.42f),

            BodyPartRole.Neck or
            BodyPartRole.Elbow or
            BodyPartRole.Knee =>
                Blend(faction, new Color(185, 195, 205, 255), 0.28f),

            BodyPartRole.Segment =>
                Shade(faction, 0.90f),

            _ =>
                Blend(faction, new Color(188, 199, 208, 255), 0.16f)
        };

        return isAlive
            ? color
            : Blend(color, new Color(91, 96, 102, 255), 0.72f);
    }

    private static Color ApplyOpacity(Color color, float opacity)
    {
        byte alpha = (byte)Math.Clamp(
            (int)MathF.Round(color.A * Math.Clamp(opacity, 0f, 1f)),
            0,
            255);
        return new Color(color.R, color.G, color.B, alpha);
    }

    private static float GetUnitDetailBlend(float pixelsPerWorldUnit)
    {
        float t = Math.Clamp(
            (pixelsPerWorldUnit - UnitDetailFadeStartPixels) /
            (UnitDetailFadeEndPixels - UnitDetailFadeStartPixels),
            0f,
            1f);

        // Smoothstep avoids visible banding at the beginning and end of the fade.
        return t * t * (3f - 2f * t);
    }

    private static float GetCloseMarkerBlend(float screenScale)
    {
        // Fade tactical goal overlays with zoom; posture glyphs intentionally
        // remain independent of zoom so the old pose indication is preserved.
        float t = Math.Clamp((screenScale - 1.25f) / 0.55f, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static bool IsAnyBodyPartInView(
        UnitBodyStore bodies,
        int start,
        int count,
        float minX,
        float maxX,
        float minY,
        float maxY)
    {
        if (start < 0 || count <= 0 || start >= bodies.Count)
            return false;

        count = Math.Min(count, bodies.Count - start);
        Vector3[] positions = bodies.WorldPosition;
        Vector3[] scales = bodies.WorldScale;
        for (int i = start; i < start + count; i++)
        {
            Vector3 scale = scales[i];
            float extent = MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
            if (IsPartInView(positions[i], extent, minX, maxX, minY, maxY))
                return true;
        }

        return false;
    }

    private static bool IsAnyBodyPartInView(
        RenderBodyPart[] bodyParts,
        int start,
        int count,
        float minX,
        float maxX,
        float minY,
        float maxY)
    {
        if (start < 0 || count <= 0 || start >= bodyParts.Length)
            return false;

        count = Math.Min(count, bodyParts.Length - start);
        for (int i = start; i < start + count; i++)
        {
            RenderBodyPart part = bodyParts[i];
            float extent = MathF.Max(
                MathF.Abs(part.Scale.X),
                MathF.Abs(part.Scale.Y));
            if (IsPartInView(part.Position, extent, minX, maxX, minY, maxY))
                return true;
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

    private static void AppendTriangleFill(
        VertexArray vertices,
        float centerX,
        float centerY,
        float halfLength,
        float halfWidth,
        float rotation,
        Color color)
    {
        float dx = MathF.Cos(rotation);
        float dy = MathF.Sin(rotation);
        float px = -dy;
        float py = dx;

        Vector2f tip = new(
            centerX + dx * halfLength,
            centerY + dy * halfLength);
        Vector2f baseA = new(
            centerX - dx * halfLength - px * halfWidth,
            centerY - dy * halfLength - py * halfWidth);
        Vector2f baseB = new(
            centerX - dx * halfLength + px * halfWidth,
            centerY - dy * halfLength + py * halfWidth);

        vertices.Append(new Vertex(tip, color));
        vertices.Append(new Vertex(baseA, color));
        vertices.Append(new Vertex(baseB, color));
    }

    private static void AppendTriangleOutline(
        VertexArray vertices,
        float centerX,
        float centerY,
        float halfLength,
        float halfWidth,
        float rotation,
        Color color)
    {
        float dx = MathF.Cos(rotation);
        float dy = MathF.Sin(rotation);
        float px = -dy;
        float py = dx;

        float tipX = centerX + dx * halfLength;
        float tipY = centerY + dy * halfLength;
        float baseAX = centerX - dx * halfLength - px * halfWidth;
        float baseAY = centerY - dy * halfLength - py * halfWidth;
        float baseBX = centerX - dx * halfLength + px * halfWidth;
        float baseBY = centerY - dy * halfLength + py * halfWidth;

        AppendDebugLine(vertices, tipX, tipY, baseAX, baseAY, color);
        AppendDebugLine(vertices, baseAX, baseAY, baseBX, baseBY, color);
        AppendDebugLine(vertices, baseBX, baseBY, tipX, tipY, color);
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

    private static Color GetItemColor(ItemConfig item) =>
        new Color(item.WorldColorR, item.WorldColorG, item.WorldColorB);

    private static Color GetFactionAccent(ushort factionTag) =>
        factionTag switch
        {
            1 => new Color(107, 158, 198, 125),
            2 => new Color(190, 116, 109, 125),
            3 => new Color(224, 180, 60, 155),
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

            // Melee gameplay remains active, but melee weapon sprites are intentionally hidden.
            if (weapon is MeleeWeaponConfig)
                continue;

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
        return UnitVisualScale * MathF.Max(
            2.0f / MathF.Max(0.001f, screenScale),
            MathF.Max(0.13f, footprint * 0.125f) * tilePixelSize);
    }

    private static void AppendLowDetailUnitMarker(
        VertexArray vertices,
        float x,
        float y,
        float radius,
        ushort factionTag,
        bool isAlive,
        float opacity)
    {
        // Keep the low-detail body marker stable across every posture. Pose is
        // communicated by a separate quiet glyph, never by deforming this circle.
        Color color = ApplyOpacity(
            GetBodyPartColor(BodyPartRole.Torso, factionTag, isAlive),
            opacity);

        const int segments = 16;
        Vector2f center = new(x, y);
        for (int i = 0; i < segments; i++)
        {
            float a0 = i * MathF.Tau / segments;
            float a1 = (i + 1) * MathF.Tau / segments;
            Vector2f p0 = new(
                x + MathF.Cos(a0) * radius,
                y + MathF.Sin(a0) * radius);
            Vector2f p1 = new(
                x + MathF.Cos(a1) * radius,
                y + MathF.Sin(a1) * radius);

            vertices.Append(new Vertex(center, color));
            vertices.Append(new Vertex(p0, color));
            vertices.Append(new Vertex(p1, color));
        }
    }

    private static void AppendPostureMarker(
        VertexArray vertices,
        float x,
        float y,
        float radius,
        UnitPosture posture,
        float screenScale,
        float opacity)
    {
        if (posture == UnitPosture.Standing)
            return;

        float pixel = 1f / MathF.Max(0.001f, screenScale);
        float size = MathF.Max(pixel * 1.5f, MathF.Min(radius * 0.42f, pixel * 3f));
        float centerX = x + radius * 0.82f + size * 0.25f;
        float centerY = y - radius * 0.82f - size * 0.25f;
        Color color = ApplyOpacity(new Color(150, 162, 174, 145), opacity);

        // Abstract, deliberately low-contrast marks: a broken trail for prone,
        // a shallow notch for crouching, and a small diamond for sitting.
        switch (posture)
        {
            case UnitPosture.Lying:
                AppendDebugLine(vertices,
                    centerX - size, centerY - size * 0.35f,
                    centerX - size * 0.48f, centerY - size * 0.35f, color);
                AppendDebugLine(vertices,
                    centerX - size * 0.12f, centerY,
                    centerX + size * 0.38f, centerY, color);
                AppendDebugLine(vertices,
                    centerX + size * 0.58f, centerY + size * 0.35f,
                    centerX + size, centerY + size * 0.35f, color);
                break;

            case UnitPosture.Crouching:
                AppendDebugLine(vertices,
                    centerX - size, centerY - size * 0.25f,
                    centerX - size * 0.12f, centerY + size * 0.4f, color);
                AppendDebugLine(vertices,
                    centerX - size * 0.12f, centerY + size * 0.4f,
                    centerX + size * 0.72f, centerY - size * 0.25f, color);
                break;

            case UnitPosture.Sitting:
                AppendDebugLine(vertices, centerX, centerY - size,
                    centerX + size, centerY, color);
                AppendDebugLine(vertices, centerX + size, centerY,
                    centerX, centerY + size, color);
                AppendDebugLine(vertices, centerX, centerY + size,
                    centerX - size, centerY, color);
                AppendDebugLine(vertices, centerX - size, centerY,
                    centerX, centerY - size, color);
                break;

        }
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
        float screenScale,
        float opacity = 1f)
    {
        // Baseline unit visualization: a plain white disk with a thin dark edge.
        // All gameplay state is deliberately hidden from the marker for this pass.
        float pixel = 1f / MathF.Max(0.001f, screenScale);
        int segments = radius * screenScale < 7f ? 16 : 28;

        AppendEllipse(
            fills, x, y,
            radius + pixel * 0.72f, radius + pixel * 0.72f,
            0f, ApplyOpacity(new Color(16, 21, 27, 255), opacity), segments);

        AppendEllipse(
            fills, x, y,
            radius, radius,
            0f, ApplyOpacity(new Color(255, 255, 255, 255), opacity), segments);
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
                3 => new Color(255, 190, 45, 250),
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

    private static void AppendSnapshotSuppressionDebug(
        VertexArray debug,
        RenderUnitState unit,
        float x,
        float y,
        float tilePixelSize)
    {
        float suppression = unit.Suppression;
        if (suppression <= 0.01f)
            return;

        Color color = unit.SuppressionState switch
        {
            UnitSuppressionState.Panicked => new Color(255, 70, 70, 220),
            UnitSuppressionState.Suppressed => new Color(255, 200, 70, 190),
            _ => new Color(190, 190, 190, 130)
        };
        float radius = (0.45f + MathF.Min(1f, suppression * 0.15f)) * tilePixelSize;
        AppendCircleOutline(debug, x, y, radius, color, 14);
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

    private static Color GetIntentColor(
        BehaviacUnitIntent intent,
        UnitAiState state)
    {
        return intent switch
        {
            BehaviacUnitIntent.Dead => new Color(130, 138, 148, 225),
            BehaviacUnitIntent.SeekCover or BehaviacUnitIntent.RetreatToCover =>
                new Color(80, 175, 255, 230),
            BehaviacUnitIntent.FlankTarget => new Color(230, 100, 255, 230),
            BehaviacUnitIntent.EngageTarget => new Color(255, 125, 85, 230),
            BehaviacUnitIntent.SuppressiveFire => new Color(255, 190, 70, 230),
            BehaviacUnitIntent.InvestigateLastKnownTarget => new Color(245, 220, 95, 230),
            BehaviacUnitIntent.Patrol => new Color(105, 225, 140, 230),
            BehaviacUnitIntent.ProneEngage => new Color(100, 225, 220, 230),
            _ => GetAiColor(state)
        };
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

            UnitAiState.Flank =>
                new Color(
                    235,
                    105,
                    255),

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

            if (check.State == VisionState.Visible)
            {
                int pointCount = UnitHitSystem.GetVisibilityPointCount(
                    units,
                    targetIndex,
                    simulation.HitRegions);
                for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                {
                    VisionCheck pointCheck = simulation.Vision.EvaluateVisibilityPoint(
                        units,
                        worldMap,
                        observerIndex,
                        targetIndex,
                        pointIndex,
                        simulation.Bodies);
                    if (!pointCheck.IsVisible)
                        continue;

                    float visibleX = pointCheck.VisibleTargetPoint.X * tilePixelSize;
                    float visibleY = pointCheck.VisibleTargetPoint.Y * tilePixelSize;
                    Color zoneColor = GetVisibleZoneColor(pointCheck.VisiblePoint);
                    AppendDebugLine(
                        debug,
                        eyeX,
                        eyeY,
                        visibleX,
                        visibleY,
                        new Color(zoneColor.R, zoneColor.G, zoneColor.B, 115));
                    AppendVisibleZoneMarker(
                        debug,
                        visibleX,
                        visibleY,
                        pointCheck.VisiblePoint,
                        tilePixelSize);
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

    private static Color GetVisibleZoneColor(int pointIndex) =>
        pointIndex switch
        {
            0 => new Color(80, 210, 255, 235),  // head
            1 => new Color(95, 230, 135, 235),  // torso
            2 => new Color(255, 205, 85, 235),  // lower body
            _ => new Color(205, 220, 245, 235)
        };

    private static void AppendVisibleZoneMarker(
        VertexArray debug,
        float x,
        float y,
        int pointIndex,
        float tilePixelSize)
    {
        Color color = GetVisibleZoneColor(pointIndex);
        float radius = MathF.Max(1.5f, tilePixelSize * 0.22f);
        AppendCircleOutline(debug, x, y, radius, color, 12);

        float s = MathF.Max(1f, MathF.Min(tilePixelSize * 0.11f, 3f));
        switch (pointIndex)
        {
            case 0: // H = head
                AppendDebugLine(debug, x - s, y - s, x - s, y + s, color);
                AppendDebugLine(debug, x + s, y - s, x + s, y + s, color);
                AppendDebugLine(debug, x - s, y, x + s, y, color);
                break;
            case 1: // T = torso
                AppendDebugLine(debug, x - s, y - s, x + s, y - s, color);
                AppendDebugLine(debug, x, y - s, x, y + s, color);
                break;
            case 2: // L = lower body
                AppendDebugLine(debug, x - s, y - s, x - s, y + s, color);
                AppendDebugLine(debug, x - s, y + s, x + s, y + s, color);
                break;
        }
    }

    private static void AppendSnapshotVisionDebug(
        VertexArray area,
        VertexArray debug,
        SimulationFrameSnapshot snapshot,
        RenderUnitState observer,
        Vector3 observerEye,
        float tilePixelSize)
    {
        float eyeX = observerEye.X * tilePixelSize;
        float eyeY = observerEye.Y * tilePixelSize;
        float rootX = observer.Position.X * tilePixelSize;
        float rootY = observer.Position.Y * tilePixelSize;
        float headAngle = MathF.Atan2(observer.HeadNormal.Y, observer.HeadNormal.X);
        float range = observer.ViewRange * tilePixelSize;

        AppendVisionCone(area, debug, eyeX, eyeY, headAngle,
            observer.FieldOfView, range);
        AppendRangeRing(debug, eyeX, eyeY, range);
        AppendDebugLine(debug, rootX, rootY, eyeX, eyeY,
            new Color(220, 220, 220, 170));
        AppendDebugLine(
            debug,
            eyeX,
            eyeY,
            eyeX + MathF.Cos(headAngle) * MathF.Min(observer.ViewRange, 3f) * tilePixelSize,
            eyeY + MathF.Sin(headAngle) * MathF.Min(observer.ViewRange, 3f) * tilePixelSize,
            new Color(255, 255, 255, 220));

        RenderVisionContact[] contacts = snapshot.VisionContacts;
        for (int i = 0; i < contacts.Length; i++)
        {
            RenderVisionContact contact = contacts[i];
            float x = contact.VisiblePoint.X * tilePixelSize;
            float y = contact.VisiblePoint.Y * tilePixelSize;
            Color color = GetVisibleZoneColor(contact.VisiblePointIndex);
            AppendDebugLine(
                debug, eyeX, eyeY, x, y,
                new Color(color.R, color.G, color.B, 115));
            AppendVisibleZoneMarker(
                debug, x, y, contact.VisiblePointIndex, tilePixelSize);
        }
    }

    private static void AppendActiveAttackLinks(
        VertexArray aiming,
        UnitSimulation simulation,
        int shooterIndex,
        float tilePixelSize)
    {
        UnitStore units = simulation.Units;
        UnitInventoryStore inventory = simulation.Inventory;
        UnitWeaponStore weapons = simulation.Weapons;
        Vector3 origin = units.Position[shooterIndex];

        for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
        {
            int stateIndex = UnitWeaponStore.GetIndex(shooterIndex, (UnitWeaponSlot)slot);
            if (!weapons.AimIndicatorActive[stateIndex] ||
                weapons.AimTimer[stateIndex] <= 0f ||
                !units.TryGetIndex(weapons.AimTarget[stateIndex], out int targetIndex) ||
                simulation.Health.OverallHitPoints[targetIndex] <= 0f)
                continue;

            short inventorySlot = inventory.GetWeaponEquipment(shooterIndex, (UnitWeaponSlot)slot);
            if (inventorySlot < 0 ||
                inventory.GetItem(shooterIndex, inventorySlot) is not RangedWeaponConfig)
                continue;

            Vector3 point = weapons.AimPoint[stateIndex];
            if (point == Vector3.Zero)
                continue;

            AppendDebugLine(
                aiming,
                origin.X * tilePixelSize,
                origin.Y * tilePixelSize,
                point.X * tilePixelSize,
                point.Y * tilePixelSize,
                new Color(255, 125, 95, 105));
        }
    }

    private static void AppendSnapshotAttackLinks(
        VertexArray aiming,
        RenderUnitState unit,
        float x,
        float y,
        float tilePixelSize)
    {
        AppendSnapshotAttackLink(aiming, unit.PrimaryWeapon, x, y, tilePixelSize);
        AppendSnapshotAttackLink(aiming, unit.SecondaryWeapon, x, y, tilePixelSize);
        AppendSnapshotAttackLink(aiming, unit.UtilityWeapon, x, y, tilePixelSize);
    }

    private static void AppendSnapshotAttackLink(
        VertexArray aiming,
        RenderWeaponVisual weapon,
        float x,
        float y,
        float tilePixelSize)
    {
        if (weapon.Config is not RangedWeaponConfig ||
            !weapon.AimIndicatorActive ||
            weapon.AimTimer <= 0f ||
            weapon.AimPoint == Vector3.Zero)
            return;

        AppendDebugLine(
            aiming,
            x,
            y,
            weapon.AimPoint.X * tilePixelSize,
            weapon.AimPoint.Y * tilePixelSize,
            new Color(255, 125, 95, 105));
    }

    private static void AppendSoundDebug(
        VertexArray debug,
        RenderSoundPulse[] pulses,
        RenderAuditoryContact[] contacts,
        View cameraView,
        float tilePixelSize)
    {
        if (tilePixelSize <= 0f)
            return;

        float minX = (cameraView.Center.X - cameraView.Size.X * 0.5f) / tilePixelSize - 2f;
        float maxX = (cameraView.Center.X + cameraView.Size.X * 0.5f) / tilePixelSize + 2f;
        float minY = (cameraView.Center.Y - cameraView.Size.Y * 0.5f) / tilePixelSize - 2f;
        float maxY = (cameraView.Center.Y + cameraView.Size.Y * 0.5f) / tilePixelSize + 2f;

        for (int i = 0; i < pulses.Length; i++)
        {
            RenderSoundPulse pulse = pulses[i];
            float fade = Math.Clamp(1f - pulse.AgeSeconds / 2.25f, 0f, 1f);
            if (fade <= 0f || pulse.AudibleRange <= 0f ||
                pulse.Position.X + pulse.AudibleRange < minX ||
                pulse.Position.X - pulse.AudibleRange > maxX ||
                pulse.Position.Y + pulse.AudibleRange < minY ||
                pulse.Position.Y - pulse.AudibleRange > maxY)
                continue;

            Color baseColor = GetSoundColor(pulse.Type);
            float x = pulse.Position.X * tilePixelSize;
            float y = pulse.Position.Y * tilePixelSize;
            byte rangeAlpha = (byte)Math.Clamp((int)((8f + pulse.Intensity * 12f) * fade), 0, 24);
            AppendCircleOutline(
                debug, x, y, pulse.AudibleRange * tilePixelSize,
                new Color(baseColor.R, baseColor.G, baseColor.B, rangeAlpha), 32);

            byte sourceAlpha = (byte)Math.Clamp((int)((100f + pulse.Intensity * 125f) * fade), 0, 225);
            Color sourceColor = new(baseColor.R, baseColor.G, baseColor.B, sourceAlpha);
            float sourceRadius = tilePixelSize * (0.18f + 0.16f * pulse.Intensity);
            AppendCircleOutline(debug, x, y, sourceRadius, sourceColor, 12);
            AppendDebugLine(debug, x - sourceRadius, y, x + sourceRadius, y, sourceColor);
            AppendDebugLine(debug, x, y - sourceRadius, x, y + sourceRadius, sourceColor);
        }

        for (int i = 0; i < contacts.Length; i++)
        {
            RenderAuditoryContact contact = contacts[i];
            float confidence = Math.Clamp(contact.Confidence, 0f, 1f);
            if (confidence < 0.12f)
                continue;

            Vector3 listener = contact.ObserverPosition;
            Vector3 estimate = contact.EstimatedPosition;
            bool listenerNearView = listener.X >= minX && listener.X <= maxX &&
                listener.Y >= minY && listener.Y <= maxY;
            float visualError = Math.Clamp(contact.ErrorRadius, 0.3f, 24f);
            bool estimateNearView = estimate.X + visualError >= minX && estimate.X - visualError <= maxX &&
                estimate.Y + visualError >= minY && estimate.Y - visualError <= maxY;
            if (!listenerNearView && !estimateNearView)
                continue;

            Color soundColor = GetSoundColor(contact.Type);
            float lx = listener.X * tilePixelSize;
            float ly = listener.Y * tilePixelSize;
            float ex = estimate.X * tilePixelSize;
            float ey = estimate.Y * tilePixelSize;
            byte linkAlpha = (byte)Math.Clamp((int)(28f + 62f * confidence), 0, 100);
            AppendDebugLine(debug, lx, ly, ex, ey,
                new Color(soundColor.R, soundColor.G, soundColor.B, linkAlpha));

            byte listenerAlpha = (byte)Math.Clamp((int)(100f + 120f * confidence), 0, 220);
            AppendCircleOutline(debug, lx, ly, 0.43f * tilePixelSize,
                new Color(80, 220, 255, listenerAlpha), 12);

            byte uncertaintyAlpha = (byte)Math.Clamp((int)(10f + 24f * confidence), 0, 36);
            AppendCircleOutline(debug, ex, ey, visualError * tilePixelSize,
                new Color(soundColor.R, soundColor.G, soundColor.B, uncertaintyAlpha), 32);
            byte estimateAlpha = (byte)Math.Clamp((int)(90f + 135f * confidence), 0, 225);
            Color estimateColor = new(soundColor.R, soundColor.G, soundColor.B, estimateAlpha);
            float markerRadius = 0.31f * tilePixelSize;
            AppendCircleOutline(debug, ex, ey, markerRadius, estimateColor, 12);
            AppendDebugLine(debug, ex - markerRadius, ey, ex + markerRadius, ey, estimateColor);
            AppendDebugLine(debug, ex, ey - markerRadius, ex, ey + markerRadius, estimateColor);
        }
    }

    private static Color GetSoundColor(TacticalStimulusType type) =>
        type switch
        {
            TacticalStimulusType.Footstep => new Color(90, 220, 255, 220),
            TacticalStimulusType.Gunshot => new Color(255, 160, 70, 220),
            TacticalStimulusType.NearMiss => new Color(255, 75, 75, 220),
            TacticalStimulusType.CoverImpact => new Color(215, 220, 230, 220),
            TacticalStimulusType.Explosion => new Color(225, 105, 255, 220),
            _ => new Color(175, 190, 205, 220)
        };

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

    public void Dispose()
    {
        _vertices.Dispose();
        _bodyOutlines.Dispose();
        _statusIcons.Dispose();
        _visionArea.Dispose();
        _selection.Dispose();
        _aiming.Dispose();
        _visionDebug.Dispose();
        _aiDebug.Dispose();
    }
}
