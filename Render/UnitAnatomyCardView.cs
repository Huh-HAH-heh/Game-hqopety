using System.IO;
using System;
using Core.Unit;
using SFML.Graphics;
using SFML.System;
using NumericsVector3 = System.Numerics.Vector3;

namespace RimClone.Render;

/// <summary>
/// Orthographic inspection of the same body geometry used by the world renderer.
/// It consumes the live frame's body transforms and the anatomy catalog's real
/// internal volumes; it does not synthesize a humanoid illustration.
/// </summary>
public sealed class UnitAnatomyCardView : IDisposable
{
    private const int ProjectionCount = 3;
    private static readonly Color PanelColor = new(21, 28, 37, 245);
    private static readonly Color PanelOutline = new(62, 76, 94, 255);

    private readonly RectangleShape[] _panels = new RectangleShape[ProjectionCount];
    private readonly Text?[] _labels = new Text?[ProjectionCount];
    private readonly Font? _font;
    private readonly CircleShape _circle = new(1f)
    {
        Origin = new Vector2f(1f, 1f),
        OutlineThickness = 1f
    };
    private readonly RectangleShape _box = new(new Vector2f(2f, 2f))
    {
        Origin = new Vector2f(1f, 1f),
        OutlineThickness = 1f
    };
    private readonly Text? _emptyLabel;

    public UnitAnatomyCardView()
    {
        for (int i = 0; i < _panels.Length; i++)
        {
            _panels[i] = new RectangleShape(new Vector2f(0f, 0f))
            {
                FillColor = PanelColor,
                OutlineColor = PanelOutline,
                OutlineThickness = 1f
            };
        }

        _font = LoadFont();
        string[] names = { "СПЕРЕДИ", "СБОКУ", "СВЕРХУ" };
        for (int i = 0; i < _labels.Length; i++)
        {
            if (_font != null)
            {
                _labels[i] = new Text(_font, names[i], 10)
                {
                    FillColor = new Color(181, 197, 215),
                    Style = Text.Styles.Bold
                };
            }
        }

        if (_font != null)
        {
            _emptyLabel = new Text(_font, "Нет данных анатомии", 12)
            {
                FillColor = new Color(173, 185, 199)
            };
        }
    }

    public void Draw(
        RenderWindow window,
        RenderUnitState unit,
        RenderBodyPart[] bodyParts,
        UnitCardDetailsSnapshot? details,
        float x,
        float y,
        float width,
        float height,
        bool showOrgans)
    {
        UnitAnatomyProfile profile = UnitAnatomyCatalog.Get(unit.Type).Anatomy;
        bodyParts = BuildStaticBodyParts(profile, unit.Position);
        int start = 0;
        int count = Math.Min(profile.NodeCount, bodyParts.Length);
        if (count <= 0)
        {
            DrawEmpty(window, x, y, width, height);
            return;
        }

        // The damage anatomy has a stable orientation and never follows the pawn's posture or facing.
        float bodyAngle = 0f;
        

        float margin = 7f;
        float gap = 6f;
        float panelWidth = (width - margin * 2f - gap * 2f) / 3f;
        float panelHeight = height - margin * 2f;

        for (int projection = 0; projection < ProjectionCount; projection++)
        {
            float panelX = x + margin + projection * (panelWidth + gap);
            float panelY = y + margin;
            _panels[projection].Position = new Vector2f(panelX, panelY);
            _panels[projection].Size = new Vector2f(panelWidth, panelHeight);
            window.Draw(_panels[projection]);

            if (_labels[projection] != null)
            {
                _labels[projection]!.Position = new Vector2f(panelX + 7f, panelY + 6f);
                window.Draw(_labels[projection]!);
            }

            float plotX = panelX + 6f;
            float plotY = panelY + 25f;
            float plotWidth = panelWidth - 12f;
            float plotHeight = panelHeight - 32f;

            // The top-down view is a compact square diagram instead of a
            // narrow, stretched strip inside the tall portrait panel.
            if (projection == 2)
            {
                float square = MathF.Min(plotWidth - 2f, MathF.Min(plotHeight - 2f, 124f));
                plotX += (plotWidth - square) * 0.5f;
                plotY += (plotHeight - square) * 0.5f;
                plotWidth = square;
                plotHeight = square;
            }

            DrawProjection(
                window, unit, bodyParts, start, count, profile, details,
                bodyAngle, projection, plotX, plotY, plotWidth, plotHeight,
                showOrgans);
        }
    }

    private static RenderBodyPart[] BuildStaticBodyParts(
        UnitAnatomyProfile profile,
        NumericsVector3 unitPosition)
    {
        ReadOnlySpan<BodyPartDefinition> definitions = profile.BodyParts;
        int count = definitions.Length;
        var result = new RenderBodyPart[count];
        var positions = new NumericsVector3[count];
        var orientations = new System.Numerics.Quaternion[count];
        var rotations = new float[count];

        for (int i = 0; i < count; i++)
        {
            BodyPartDefinition part = definitions[i];
            int parent = part.ParentIndex;
            NumericsVector3 origin = unitPosition;
            System.Numerics.Quaternion positionOrientation = System.Numerics.Quaternion.Identity;
            System.Numerics.Quaternion parentOrientation = System.Numerics.Quaternion.Identity;
            float parentRotation = 0f;

            if (part.Anchor == BodyPartAnchor.Parent && parent >= 0 && parent < i)
            {
                origin = positions[parent];
                positionOrientation = orientations[parent];
                parentOrientation = orientations[parent];
                parentRotation = rotations[parent];
            }

            NumericsVector3 localEuler = new(
                part.LocalRotationX,
                part.LocalRotationY,
                part.LocalRotation);
            System.Numerics.Quaternion orientation =
                BodyPartTransformMath.Compose(parentOrientation, localEuler);

            float rotation = BodyPartTransformMath.Project(
                part.Scale, orientation, 0, 1).Rotation;
            if (MathF.Abs(localEuler.X) < 0.0001f &&
                MathF.Abs(localEuler.Y) < 0.0001f &&
                MathF.Abs(parentOrientation.X) < 0.0001f &&
                MathF.Abs(parentOrientation.Y) < 0.0001f)
            {
                rotation = parentRotation + localEuler.Z;
            }

            NumericsVector3 joint = origin +
                BodyPartTransformMath.TransformPosition(part.LocalPosition, positionOrientation);
            if (part.Pivot == BodyPartPivot.Start)
            {
                joint += BodyPartTransformMath.TransformPosition(
                    new NumericsVector3(MathF.Abs(part.Scale.X), 0f, 0f),
                    orientation);
            }

            NumericsVector3 projectedScale = BodyPartTransformMath.Project(
                part.Scale, orientation, 0, 1).Scale;
            positions[i] = joint;
            orientations[i] = orientation;
            rotations[i] = rotation;
            result[i] = new RenderBodyPart(
                joint, projectedScale, rotation, part.Primitive, part.Role);
        }

        return result;
    }

    private void DrawProjection(
        RenderWindow window,
        RenderUnitState unit,
        RenderBodyPart[] bodyParts,
        int start,
        int count,
        UnitAnatomyProfile profile,
        UnitCardDetailsSnapshot? details,
        float bodyAngle,
        int projection,
        float plotX,
        float plotY,
        float plotWidth,
        float plotHeight,
        bool showOrgans)
    {
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;

        for (int i = 0; i < count; i++)
        {
            if (i < profile.NodeCount && !IsNodeVisible(profile, i, details))
                continue;

            ProjectedGeometry geometry = ProjectBodyPart(
                bodyParts[start + i], unit.Position, bodyAngle, projection);
            IncludeBounds(geometry, ref minX, ref minY, ref maxX, ref maxY);
        }

        ReadOnlySpan<UnitAnatomyVolume> volumes = profile.Volumes;
        if (showOrgans)
        {
            for (int i = 0; i < volumes.Length; i++)
            {
                if (!TryProjectVolume(
                        volumes[i], unit, bodyParts, start, count,
                        bodyAngle, projection, profile, details,
                        out ProjectedGeometry geometry))
                {
                    continue;
                }

                IncludeBounds(geometry, ref minX, ref minY, ref maxX, ref maxY);
            }
        }

        if (!float.IsFinite(minX) || !float.IsFinite(minY))
            return;

        float spanX = MathF.Max(0.001f, maxX - minX);
        float spanY = MathF.Max(0.001f, maxY - minY);
        float pixelsPerUnit = MathF.Min(
            MathF.Max(1f, plotWidth - 10f) / spanX,
            MathF.Max(1f, plotHeight - 10f) / spanY);
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float screenX = plotX + plotWidth * 0.5f;
        float screenY = plotY + plotHeight * 0.5f;

        for (int i = 0; i < count; i++)
        {
            if (i < profile.NodeCount && !IsNodeVisible(profile, i, details))
                continue;

            RenderBodyPart part = bodyParts[start + i];
            ProjectedGeometry geometry = ProjectBodyPart(
                part, unit.Position, bodyAngle, projection);

            float ratio = i < profile.NodeCount
                ? GetNodeHealthRatio(profile.GetNode(i), details, unit.HealthRatio)
                : unit.HealthRatio;

            DrawGeometry(
                window, geometry, screenX, screenY, centerX, centerY,
                pixelsPerUnit, projection, HealthColor(ratio, 224), false);
        }

        if (!showOrgans)
            return;

        for (int i = 0; i < volumes.Length; i++)
        {
            if (!TryProjectVolume(
                    volumes[i], unit, bodyParts, start, count,
                    bodyAngle, projection, profile, details,
                    out ProjectedGeometry geometry))
            {
                continue;
            }

            DrawGeometry(
                window, geometry, screenX, screenY, centerX, centerY,
                pixelsPerUnit, projection, HealthColor(geometry.HealthRatio, 205), true);
        }
    }

    private void DrawGeometry(
        RenderWindow window,
        ProjectedGeometry geometry,
        float screenCenterX,
        float screenCenterY,
        float worldCenterX,
        float worldCenterY,
        float pixelsPerUnit,
        int projection,
        Color fill,
        bool organ)
    {
        float x = screenCenterX + (geometry.X - worldCenterX) * pixelsPerUnit;
        float y = screenCenterY - (geometry.Y - worldCenterY) * pixelsPerUnit;
        float width = MathF.Max(1.1f, geometry.HalfWidth * pixelsPerUnit);
        float height = MathF.Max(1.1f, geometry.HalfHeight * pixelsPerUnit);

        Color outline = organ
            ? new Color(241, 230, 211, 245)
            : new Color(15, 21, 29, 235);

        if (geometry.Primitive == BodyPrimitive.Circle)
        {
            _circle.Position = new Vector2f(x, y);
            _circle.Scale = new Vector2f(width, height);
            _circle.Rotation = projection == 2 ? geometry.Rotation : 0f;
            _circle.FillColor = fill;
            _circle.OutlineColor = outline;
            _circle.OutlineThickness = MathF.Min(0.5f, 0.8f / MathF.Max(width, height));
            window.Draw(_circle);
        }
        else
        {
            _box.Position = new Vector2f(x, y);
            _box.Scale = new Vector2f(width, height);
            _box.Rotation = projection == 2 ? geometry.Rotation : 0f;
            _box.FillColor = fill;
            _box.OutlineColor = outline;
            _box.OutlineThickness = MathF.Min(0.5f, 0.8f / MathF.Max(width, height));
            window.Draw(_box);
        }
    }

    private static ProjectedGeometry ProjectBodyPart(
        RenderBodyPart part,
        NumericsVector3 unitPosition,
        float bodyAngle,
        int projection)
    {
        NumericsVector3 delta = part.Position - unitPosition;
        float forward = MathF.Cos(bodyAngle) * delta.X + MathF.Sin(bodyAngle) * delta.Y;
        float lateral = -MathF.Sin(bodyAngle) * delta.X + MathF.Cos(bodyAngle) * delta.Y;
        float relativeRotation = part.Rotation - bodyAngle;

        return Project(
            projection, forward, lateral, delta.Z,
            part.Scale, relativeRotation, part.Primitive, 1f);
    }

    private static bool TryProjectVolume(
        UnitAnatomyVolume volume,
        RenderUnitState unit,
        RenderBodyPart[] bodyParts,
        int start,
        int count,
        float bodyAngle,
        int projection,
        UnitAnatomyProfile profile,
        UnitCardDetailsSnapshot? details,
        out ProjectedGeometry geometry)
    {
        int parentRegion = profile.FindRegion(volume.ParentRegionId);
        if (parentRegion < 0 || parentRegion >= count)
        {
            geometry = default;
            return false;
        }

        int parentIndex = start + parentRegion;
        if ((uint)parentIndex >= (uint)bodyParts.Length ||
            !IsNodeVisible(profile, parentRegion, details))
        {
            geometry = default;
            return false;
        }

        RenderBodyPart parent = bodyParts[parentIndex];
        float rotation = parent.Rotation;
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        NumericsVector3 center = new(
            parent.Position.X + cos * volume.LocalCenter.X - sin * volume.LocalCenter.Y,
            parent.Position.Y + sin * volume.LocalCenter.X + cos * volume.LocalCenter.Y,
            parent.Position.Z + volume.LocalCenter.Z);
        NumericsVector3 delta = center - unit.Position;
        float forward = MathF.Cos(bodyAngle) * delta.X + MathF.Sin(bodyAngle) * delta.Y;
        float lateral = -MathF.Sin(bodyAngle) * delta.X + MathF.Cos(bodyAngle) * delta.Y;
        float ratio = GetHealthRatio(volume.TargetBindingId, details, unit.HealthRatio);

        geometry = Project(
            projection,
            forward,
            lateral,
            delta.Z,
            volume.HalfExtents,
            parent.Rotation + volume.LocalRotation - bodyAngle,
            volume.Primitive,
            ratio);
        return true;
    }

    private static ProjectedGeometry Project(
        int projection,
        float forward,
        float lateral,
        float z,
        NumericsVector3 extents,
        float relativeRotation,
        BodyPrimitive primitive,
        float ratio)
    {
        float sin = MathF.Abs(MathF.Sin(relativeRotation));
        float cos = MathF.Abs(MathF.Cos(relativeRotation));
        float halfWidth;
        float halfHeight;
        float centerX;
        float centerY;
        float screenRotation = -relativeRotation * 180f / MathF.PI;

        switch (projection)
        {
            case 0: // Front: lateral width against vertical height.
                centerX = lateral;
                centerY = z;
                halfWidth = sin * MathF.Abs(extents.X) + cos * MathF.Abs(extents.Y);
                halfHeight = MathF.Abs(extents.Z);
                screenRotation = 0f;
                break;
            case 1: // Side: forward length against vertical height.
                centerX = forward;
                centerY = z;
                halfWidth = cos * MathF.Abs(extents.X) + sin * MathF.Abs(extents.Y);
                halfHeight = MathF.Abs(extents.Z);
                screenRotation = 0f;
                break;
            default: // Top: the actual horizontal body plane.
                centerX = forward;
                centerY = lateral;
                halfWidth = cos * MathF.Abs(extents.X) + sin * MathF.Abs(extents.Y);
                halfHeight = sin * MathF.Abs(extents.X) + cos * MathF.Abs(extents.Y);
                break;
        }

        return new ProjectedGeometry(
            centerX, centerY,
            MathF.Max(0.001f, halfWidth),
            MathF.Max(0.001f, halfHeight),
            screenRotation,
            primitive,
            Math.Clamp(ratio, 0f, 1f));
    }

    private static bool IsNodeVisible(
        UnitAnatomyProfile profile,
        int nodeIndex,
        UnitCardDetailsSnapshot? details)
    {
        if (details == null)
            return true;

        ReadOnlySpan<BodyPartDefinition> parts = profile.BodyParts;
        int current = nodeIndex;
        for (int step = 0; current >= 0 && step < parts.Length; step++)
        {
            if (current >= parts.Length || !IsNodePresent(profile.GetNode(current), details))
                return false;

            current = parts[current].ParentIndex;
        }

        return current < 0;
    }

    private static bool IsNodePresent(
        UnitAnatomyNode node,
        UnitCardDetailsSnapshot details)
    {
        AnatomyCardEntry[] entries = details.AnatomyEntries;
        bool found = false;
        bool anyPresent = false;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i] is not BodyPartCardEntry || entries[i].RegionId != node.Id)
                continue;

            found = true;
            anyPresent |= entries[i].Present;
        }

        return !found || anyPresent;
    }

    private static float GetNodeHealthRatio(
        UnitAnatomyNode node,
        UnitCardDetailsSnapshot? details,
        float fallback)
    {
        if (details == null)
            return fallback;

        AnatomyCardEntry[] entries = details.AnatomyEntries;
        float total = 0f;
        int count = 0;

        for (int i = 0; i < entries.Length; i++)
        {
            AnatomyCardEntry entry = entries[i];
            if (entry is not BodyPartCardEntry || entry.RegionId != node.Id)
                continue;

            total += entry.HealthRatio;
            count++;
        }

        return count > 0 ? total / count : fallback;
    }

    private static float GetHealthRatio(
        string tissueId,
        UnitCardDetailsSnapshot? details,
        float fallback)
    {
        if (details == null)
            return fallback;

        AnatomyCardEntry[] entries = details.AnatomyEntries;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Id == tissueId)
                return entries[i].HealthRatio;
        }

        return fallback;
    }

    private static Color HealthColor(float ratio, byte alpha)
    {
        if (ratio <= 0.15f)
            return new Color(200, 62, 67, alpha);
        if (ratio <= 0.40f)
            return new Color(215, 109, 58, alpha);
        if (ratio <= 0.70f)
            return new Color(211, 177, 64, alpha);

        return new Color(73, 157, 125, alpha);
    }

    private void DrawEmpty(RenderWindow window, float x, float y, float width, float height)
    {
        if (_emptyLabel == null)
            return;

        _emptyLabel.Position = new Vector2f(
            x + MathF.Max(0f, width * 0.5f - 65f),
            y + MathF.Max(0f, height * 0.5f - 8f));
        window.Draw(_emptyLabel);
    }

    private static void IncludeBounds(
        ProjectedGeometry geometry,
        ref float minX,
        ref float minY,
        ref float maxX,
        ref float maxY)
    {
        minX = MathF.Min(minX, geometry.X - geometry.HalfWidth);
        minY = MathF.Min(minY, geometry.Y - geometry.HalfHeight);
        maxX = MathF.Max(maxX, geometry.X + geometry.HalfWidth);
        maxY = MathF.Max(maxY, geometry.Y + geometry.HalfHeight);
    }

    private static Font? LoadFont()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = { "segoeui.ttf", "arial.ttf", "tahoma.ttf" };
        for (int i = 0; i < candidates.Length; i++)
        {
            string path = Path.Combine(folder, candidates[i]);
            if (!File.Exists(path))
                continue;

            try
            {
                return new Font(path);
            }
            catch
            {
                // Try the next installed font.
            }
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _panels.Length; i++)
            _panels[i].Dispose();
        for (int i = 0; i < _labels.Length; i++)
            _labels[i]?.Dispose();

        _circle.Dispose();
        _box.Dispose();
        _emptyLabel?.Dispose();
        _font?.Dispose();
    }

    private readonly record struct ProjectedGeometry(
        float X,
        float Y,
        float HalfWidth,
        float HalfHeight,
        float Rotation,
        BodyPrimitive Primitive,
        float HealthRatio);
}
