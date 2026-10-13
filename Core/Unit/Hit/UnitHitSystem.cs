using System;
using System.Numerics;

namespace Core.Unit;

public readonly struct UnitHitRegionGeometry
{
    public Vector3 Center { get; }
    public float Radius { get; }
    public float HalfHeight { get; }

    public UnitHitRegionGeometry(
        Vector3 center,
        float radius,
        float halfHeight)
    {
        Center = center;
        Radius = radius;
        HalfHeight = halfHeight;
    }
}

public readonly struct UnitHitResult
{
    public UnitHealthPartId Part { get; }
    public Vector3 Position { get; }
    public float T { get; }
    public int RegionIndex { get; }
    public int BindingIndex { get; }

    public UnitHitResult(
        UnitHealthPartId part,
        Vector3 position,
        float t,
        int regionIndex = -1,
        int bindingIndex = -1)
    {
        Part = part;
        Position = position;
        T = t;
        RegionIndex = regionIndex;
        BindingIndex = bindingIndex;
    }
}

/// <summary>
/// Shared geometry queries for projectile collision, visibility, and aiming.
/// Canonical anatomy nodes use their live body-store primitive and transform;
/// legacy profiles fall back to vertical cylinders. All geometry comes from the
/// unit's assigned data profile.
/// </summary>
public sealed class UnitHitSystem
{
    public static Vector3 GetEyePosition(
        UnitStore units,
        int unitIndex,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        for (int i = 0; i < profile.RegionCount; i++)
        {
            if (TryGetMarker(profile, i, UnitAnatomyMarkerRole.Eye, out UnitAnatomyMarker marker))
                return GetMarkerWorldPosition(units, unitIndex, i, marker, hitRegions, bodies);
        }

        for (int i = 0; i < profile.RegionCount; i++)
        {
            if (!RegionHasPart(profile, i, UnitHealthPartId.Head))
                continue;

            GetWorldGeometry(units, unitIndex, i, profile,
                out Vector3 center, out _, out float halfHeight, hitRegions, bodies);
            return center + new Vector3(0f, 0f, halfHeight * 0.55f);
        }

        return units.Position[unitIndex] +
            new Vector3(0f, 0f, MathF.Max(0.05f, units.Height[unitIndex]) * 0.8f);
    }

    public static int GetVisibilityPointCount(
        UnitStore units,
        int unitIndex,
        UnitHitRegionStore? hitRegions = null) =>
        CountTargetRegions(GetProfile(units, unitIndex, hitRegions));

    public static int GetVisibilityRegionIndex(
        UnitStore units,
        int unitIndex,
        int visibleIndex,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if (profile is UnitAnatomyProfile anatomy)
            return anatomy.GetVisibleRegionIndex(visibleIndex);

        if ((uint)visibleIndex >= (uint)profile.RegionCount)
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));

        return visibleIndex;
    }

    public static int GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null) =>
        FillRegionPoints(units, unitIndex, source, points, hitRegions, UnitAnatomyMarkerRole.Vision, bodies);

    public static int GetAimPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null) =>
        FillRegionPoints(units, unitIndex, source, points, hitRegions, UnitAnatomyMarkerRole.Aim, bodies);

    public static UnitHitRegionGeometry GetRegionGeometry(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        if ((uint)regionIndex >= (uint)profile.RegionCount)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));

        GetWorldGeometry(
            units,
            unitIndex,
            regionIndex,
            profile,
            out Vector3 center,
            out float radius,
            out float halfHeight,
            hitRegions,
            bodies);

        return new UnitHitRegionGeometry(center, radius, halfHeight);
    }

    public static bool IsPartInProfile(
        UnitStore units,
        int unitIndex,
        UnitHealthPartId part,
        UnitHitRegionStore? hitRegions = null)
    {
        if (part == UnitHealthPartId.None)
            return false;

        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        for (int i = 0; i < profile.RegionCount; i++)
        {
            if (RegionHasPart(profile, i, part))
                return true;
        }

        return false;
    }

    public static bool IsPartInRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHealthPartId part,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        return (uint)regionIndex < (uint)profile.RegionCount &&
               RegionHasPart(profile, regionIndex, part);
    }

    public static bool IsPointInsideRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHealthPartId part,
        Vector3 point,
        float tolerance = 0f,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        return IsPartInRegion(units, unitIndex, regionIndex, part, hitRegions) &&
               IsPointInsideRegion(units, unitIndex, regionIndex, point, tolerance, hitRegions, bodies);
    }

    public static bool IsPointInsideRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        Vector3 point,
        float tolerance = 0f,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        if (!float.IsFinite(tolerance) || tolerance < 0f ||
            !float.IsFinite(point.X) ||
            !float.IsFinite(point.Y) ||
            !float.IsFinite(point.Z))
            return false;

        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
            return false;

        if (TryGetBodyShape(
                units, unitIndex, profile, regionIndex, bodies,
                out _, out Vector3 center, out Vector3 scale,
                out float rotation, out BodyPrimitive primitive, out float halfHeight))
        {
            return TryIntersectPrimitive(
                point, point, center, scale, rotation, primitive,
                halfHeight, tolerance, out _);
        }

        UnitHitRegionGeometry geometry = GetRegionGeometry(
            units, unitIndex, regionIndex, hitRegions, bodies);

        float dx = point.X - geometry.Center.X;
        float dy = point.Y - geometry.Center.Y;
        float radius = geometry.Radius + tolerance;

        return dx * dx + dy * dy <= radius * radius &&
               MathF.Abs(point.Z - geometry.Center.Z) <= geometry.HalfHeight + tolerance;
    }

    public static bool TryFindBindingForPart(
        UnitStore units,
        int unitIndex,
        UnitHealthPartId part,
        out int regionIndex,
        out int bindingIndex,
        UnitHitRegionStore? hitRegions = null)
    {
        return TryFindPart(GetProfile(units, unitIndex, hitRegions),
            part, out regionIndex, out bindingIndex);
    }

    public static int FindBindingInRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHealthPartId part,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
            return -1;

        ReadOnlySpan<HitRegionPartBinding> bindings = GetBindings(profile, regionIndex);
        for (int i = 0; i < bindings.Length; i++)
        {
            if (bindings[i].Part == part)
                return i;
        }

        return -1;
    }

    public static bool IsBindingInRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        int bindingIndex,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        return (uint)regionIndex < (uint)profile.RegionCount &&
               (uint)bindingIndex < (uint)GetBindings(profile, regionIndex).Length;
    }

    public static bool TryGetBinding(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        int bindingIndex,
        out HitRegionPartBinding binding,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex < (uint)profile.RegionCount &&
            (uint)bindingIndex < (uint)GetBindings(profile, regionIndex).Length)
        {
            binding = GetBindings(profile, regionIndex)[bindingIndex];
            return true;
        }

        binding = default;
        return false;
    }

    public static bool TryGetMarkerPoint(
        UnitStore units,
        int unitIndex,
        UnitAnatomyMarkerRole role,
        out Vector3 point,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        for (int regionIndex = 0; regionIndex < profile.RegionCount; regionIndex++)
        {
            if (!TryGetMarker(profile, regionIndex, role, out UnitAnatomyMarker marker))
                continue;

            point = GetMarkerWorldPosition(units, unitIndex, regionIndex, marker, hitRegions, bodies);
            return true;
        }

        point = Vector3.Zero;
        return false;
    }

    public static bool TryGetMarkerPoint(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        string markerId,
        out Vector3 point,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
        {
            point = Vector3.Zero;
            return false;
        }

        if (TryGetMarker(profile, regionIndex, markerId, out UnitAnatomyMarker marker))
        {
            point = GetMarkerWorldPosition(units, unitIndex, regionIndex, marker, hitRegions, bodies);
            return true;
        }

        point = Vector3.Zero;
        return false;
    }

    public static int FindRegionAtPoint(
        UnitStore units,
        int unitIndex,
        UnitHealthPartId part,
        Vector3 point,
        float tolerance = 0f,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        for (int i = 0; i < profile.RegionCount; i++)
        {
            if (IsPointInsideRegion(
                    units,
                    unitIndex,
                    i,
                    part,
                    point,
                    tolerance,
                    hitRegions,
                    bodies))
            {
                return i;
            }
        }

        return -1;
    }

    public static int FindRegionAtPoint(
        UnitStore units,
        int unitIndex,
        Vector3 point,
        float tolerance = 0f,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        for (int i = 0; i < profile.RegionCount; i++)
        {
            if (IsPointInsideRegion(
                    units, unitIndex, i, point, tolerance, hitRegions, bodies))
                return i;
        }

        return -1;
    }

    private static bool RegionHasPart(
        UnitHitRegionProfile profile,
        int regionIndex,
        UnitHealthPartId part)
    {
        foreach (HitRegionPartBinding binding in GetBindings(profile, regionIndex))
        {
            if (binding.Part == part)
                return true;
        }

        return false;
    }

    public static Vector3 GetRegionPoint(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        Vector3 source,
        UnitHitRegionStore? hitRegions = null,
        UnitAnatomyMarkerRole markerRole = UnitAnatomyMarkerRole.Target,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));

        GetWorldGeometry(units, unitIndex, regionIndex, profile,
            out Vector3 center, out float radius, out float halfHeight, hitRegions, bodies);

        bool hasMarker = TryGetPreferredMarker(
            profile, regionIndex, markerRole, out UnitAnatomyMarker marker);

        if (hasMarker)
            return GetMarkerWorldPosition(units, unitIndex, regionIndex, marker, hitRegions, bodies);

        Vector2 direction = new Vector2(source.X - center.X, source.Y - center.Y);
        float lengthSquared = direction.LengthSquared();

        if (lengthSquared > 0.000001f)
            direction /= MathF.Sqrt(lengthSquared);
        else
            direction = Vector2.UnitX;

        float vertical = Math.Clamp(
            source.Z - center.Z,
            -halfHeight * 0.65f,
            halfHeight * 0.65f);

        return new Vector3(
            center.X + direction.X * radius * 0.65f,
            center.Y + direction.Y * radius * 0.65f,
            center.Z + vertical);
    }

    public bool TryHitUnit(
        UnitStore units,
        int unitIndex,
        Vector3 start,
        Vector3 end,
        float projectileRadius,
        UnitHealthPartId ignoredPart,
        out UnitHitResult result,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        bool found = false;
        bool bestIsInternal = false;
        float bestT = 2f;
        UnitHealthPartId bestPart = UnitHealthPartId.None;
        int bestRegion = -1;
        int bestBinding = -1;
        Vector3 bestPosition = default;

        for (int i = 0; i < profile.RegionCount; i++)
        {
            float padding = MathF.Max(0f, projectileRadius);
            float t;
            bool intersects;

            if (TryGetBodyShape(
                    units, unitIndex, profile, i, bodies,
                    out _, out Vector3 shapeCenter, out Vector3 scale,
                    out float rotation, out BodyPrimitive primitive, out float shapeHalfHeight))
            {
                intersects = TryIntersectPrimitive(
                    start, end, shapeCenter, scale, rotation, primitive,
                    shapeHalfHeight, padding, out t);
            }
            else
            {
                GetWorldGeometry(units, unitIndex, i, profile,
                    out Vector3 center, out float radius, out float halfHeight,
                    hitRegions, bodies);
                intersects = TryIntersectCylinder(
                    start, end, center, radius + padding, halfHeight + padding, out t);
            }

            bool isInternal = IsInternalNode(profile, i);
            if (!intersects ||
                (found && bestIsInternal && !isInternal) ||
                (found && bestIsInternal == isInternal && t >= bestT))
                continue;

            Vector3 hitPosition = start + (end - start) * t;
            int bindingIndex = SelectPart(profile, i, hitPosition, unitIndex, out UnitHealthPartId part);

            if (ignoredPart != UnitHealthPartId.None && part == ignoredPart)
                continue;

            bestT = t;
            bestIsInternal = isInternal;
            bestPart = part;
            bestRegion = i;
            bestBinding = bindingIndex;
            bestPosition = hitPosition;
            found = true;
        }

        if (!found)
        {
            result = default;
            return false;
        }

        result = new UnitHitResult(bestPart, bestPosition, bestT, bestRegion, bestBinding);
        return true;
    }

    private static int FillRegionPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitHitRegionStore? hitRegions,
        UnitAnatomyMarkerRole markerRole,
        UnitBodyStore? bodies)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        int required = CountTargetRegions(profile);
        if (points.Length < required)
            throw new ArgumentException("Insufficient anatomy target-point buffer.", nameof(points));

        int written = 0;
        for (int visibleIndex = 0; visibleIndex < required; visibleIndex++)
        {
            int regionIndex = profile is UnitAnatomyProfile anatomy
                ? anatomy.GetVisibleRegionIndex(visibleIndex)
                : visibleIndex;

            points[written++] = GetRegionPoint(
                units, unitIndex, regionIndex, source, hitRegions, markerRole, bodies);
        }

        return written;
    }

    private static int SelectPart(
        UnitHitRegionProfile profile,
        int regionIndex,
        Vector3 point,
        int unitIndex,
        out UnitHealthPartId part)
    {
        ReadOnlySpan<HitRegionPartBinding> parts = GetBindings(profile, regionIndex);
        float totalWeight = 0f;

        for (int i = 0; i < parts.Length; i++)
            totalWeight += parts[i].Weight;

        uint hash = unchecked((uint)(unitIndex + 1) * 2654435761u);
        hash ^= unchecked((uint)(regionIndex + 1) * 2246822519u);
        hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(point.X) * 3266489917u);
        hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(point.Y) * 668265263u);
        hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(point.Z) * 374761393u);
        hash ^= hash >> 16;
        hash *= 2246822519u;
        hash ^= hash >> 13;

        float selection = hash / (float)uint.MaxValue * totalWeight;

        for (int i = 0; i < parts.Length; i++)
        {
            selection -= parts[i].Weight;
            if (selection <= 0f)
            {
                part = parts[i].Part;
                return i;
            }
        }

        part = parts[^1].Part;
        return parts.Length - 1;
    }

    private static Vector3 GetMarkerWorldPosition(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitAnatomyMarker marker,
        UnitHitRegionStore? hitRegions,
        UnitBodyStore? bodies)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        GetWorldGeometry(units, unitIndex, regionIndex, profile,
            out Vector3 center, out _, out _, hitRegions, bodies);

        if (bodies != null &&
            TryGetBodyShape(units, unitIndex, GetProfile(units, unitIndex, hitRegions),
                regionIndex, bodies, out _, out _, out _, out float bodyRotation, out _, out _))
        {
            Vector3 offset = marker.LocalOffset;
            float cos = MathF.Cos(bodyRotation);
            float sin = MathF.Sin(bodyRotation);
            return center + new Vector3(
                offset.X * cos - offset.Y * sin,
                offset.X * sin + offset.Y * cos,
                offset.Z);
        }

        Vector3 direction = (marker.Roles &
            (UnitAnatomyMarkerRole.Eye | UnitAnatomyMarkerRole.Muzzle)) != 0
            ? units.HeadNormal[unitIndex]
            : units.BodyNormal[unitIndex];
        float angle = MathF.Atan2(direction.Y, direction.X);
        float fallbackCos = MathF.Cos(angle);
        float fallbackSin = MathF.Sin(angle);
        Vector3 fallbackOffset = marker.LocalOffset;

        return center + new Vector3(
            fallbackOffset.X * fallbackCos - fallbackOffset.Y * fallbackSin,
            fallbackOffset.X * fallbackSin + fallbackOffset.Y * fallbackCos,
            fallbackOffset.Z);
    }

    private static ReadOnlySpan<HitRegionPartBinding> GetBindings(
        UnitHitRegionProfile profile,
        int regionIndex) =>
        profile is UnitAnatomyProfile anatomy
            ? anatomy.GetTissues(regionIndex)
            : profile.Regions[regionIndex].Parts;

    private static bool TryGetMarker(
        UnitHitRegionProfile profile,
        int regionIndex,
        UnitAnatomyMarkerRole role,
        out UnitAnatomyMarker marker)
    {
        if (profile is UnitAnatomyProfile anatomy)
            return anatomy.TryGetMarker(regionIndex, role, out marker);

        return profile.Regions[regionIndex].TryGetMarker(role, out marker);
    }

    private static bool TryGetMarker(
        UnitHitRegionProfile profile,
        int regionIndex,
        string markerId,
        out UnitAnatomyMarker marker)
    {
        if (profile is UnitAnatomyProfile anatomy)
            return anatomy.TryGetMarker(regionIndex, markerId, out marker);

        foreach (UnitAnatomyMarker candidate in profile.Regions[regionIndex].Markers)
        {
            if (!string.Equals(candidate.Id, markerId, StringComparison.Ordinal))
                continue;

            marker = candidate;
            return true;
        }

        marker = default;
        return false;
    }

    private static bool TryGetPreferredMarker(
        UnitHitRegionProfile profile,
        int regionIndex,
        UnitAnatomyMarkerRole role,
        out UnitAnatomyMarker marker)
    {
        UnitAnatomyMarkerRole first = role == UnitAnatomyMarkerRole.Vision
            ? UnitAnatomyMarkerRole.Vision
            : role == UnitAnatomyMarkerRole.Aim
                ? UnitAnatomyMarkerRole.Aim
                : UnitAnatomyMarkerRole.Target;
        UnitAnatomyMarkerRole second = role == UnitAnatomyMarkerRole.Vision
            ? UnitAnatomyMarkerRole.Target
            : role == UnitAnatomyMarkerRole.Aim
                ? UnitAnatomyMarkerRole.Target
                : UnitAnatomyMarkerRole.Aim;
        UnitAnatomyMarkerRole third = role == UnitAnatomyMarkerRole.Vision
            ? UnitAnatomyMarkerRole.Aim
            : role == UnitAnatomyMarkerRole.Aim
                ? UnitAnatomyMarkerRole.Vision
                : UnitAnatomyMarkerRole.Vision;

        return TryGetMarker(profile, regionIndex, first, out marker) ||
               TryGetMarker(profile, regionIndex, second, out marker) ||
               TryGetMarker(profile, regionIndex, third, out marker);
    }

    private static int FindRegion(UnitHitRegionProfile profile, string id)
    {
        if (profile is UnitAnatomyProfile anatomy)
            return anatomy.FindRegion(id);

        return profile.FindRegion(id);
    }

    private static bool TryFindPart(
        UnitHitRegionProfile profile,
        UnitHealthPartId part,
        out int regionIndex,
        out int bindingIndex)
    {
        if (profile is UnitAnatomyProfile anatomy)
            return anatomy.TryFindPart(part, out regionIndex, out bindingIndex);

        return profile.TryFindPart(part, out regionIndex, out bindingIndex);
    }

    private static UnitHitRegionProfile GetProfile(
        UnitStore units,
        int unitIndex,
        UnitHitRegionStore? hitRegions)
    {
        return hitRegions?.GetProfile(unitIndex) ??
            UnitHitRegionCatalog.Get(UnitCatalog.Get(units.Type[unitIndex]).BodyType);
    }

    private static void GetWorldGeometry(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHitRegionProfile profile,
        out Vector3 center,
        out float radius,
        out float halfHeight,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        if (TryGetBodyShape(
                units, unitIndex, profile, regionIndex, bodies,
                out _, out center, out Vector3 scale, out _, out _, out halfHeight))
        {
            radius = MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
            return;
        }

        GetWorldGeometry(
            units, unitIndex, profile.Regions[regionIndex],
            out center, out radius, out halfHeight, hitRegions, bodies);
    }

    private static void GetWorldGeometry(
        UnitStore units,
        int unitIndex,
        in UnitCircleHitRegion region,
        out Vector3 center,
        out float radius,
        out float halfHeight,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
    {
        UnitHitRegionProfile assignedProfile = GetProfile(units, unitIndex, hitRegions);
        int assignedRegionIndex = FindRegion(assignedProfile, region.Id);
        if (TryGetBodyShape(
                units, unitIndex, assignedProfile, assignedRegionIndex, bodies,
                out _, out center, out Vector3 sharedScale, out _, out _, out halfHeight))
        {
            radius = MathF.Max(MathF.Abs(sharedScale.X), MathF.Abs(sharedScale.Y));
            return;
        }

        Vector3 position = units.Position[unitIndex];
        Vector3 factors = region.DimensionCenterFactors;

        Vector3 offset = region.LocalCenter + new Vector3(
            units.Width[unitIndex] * factors.X,
            units.Length[unitIndex] * factors.Y,
            units.Height[unitIndex] * factors.Z);

        Vector3 bodyNormal = units.BodyNormal[unitIndex];
        float angle = MathF.Atan2(bodyNormal.Y, bodyNormal.X);
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        Vector3 rotatedOffset = new Vector3(
            offset.X * cos - offset.Y * sin,
            offset.X * sin + offset.Y * cos,
            offset.Z);

        if (!string.IsNullOrWhiteSpace(region.ParentId))
        {
            UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
            int parentIndex = FindRegion(profile, region.ParentId);

            if (parentIndex >= 0 && parentIndex < profile.RegionCount)
            {
                UnitCircleHitRegion parent = profile.Regions[parentIndex];
                GetWorldGeometry(
                    units, unitIndex, parent,
                    out Vector3 parentCenter, out _, out _, hitRegions, bodies);
                center = parentCenter + rotatedOffset;
            }
            else
            {
                center = position + rotatedOffset;
            }
        }
        else
        {
            center = position + rotatedOffset;
        }

        radius = MathF.Max(
            0.001f,
            region.Radius +
            units.Width[unitIndex] * region.WidthRadiusFactor +
            units.Length[unitIndex] * region.LengthRadiusFactor);

        halfHeight = MathF.Max(
            0.001f,
            region.HalfHeight +
            units.Height[unitIndex] * region.HeightHalfExtentFactor);
    }


    private static int CountTargetRegions(UnitHitRegionProfile profile) =>
        profile is UnitAnatomyProfile anatomy
            ? anatomy.VisibleRegionCount
            : profile.RegionCount;

    private static bool IsInternalNode(UnitHitRegionProfile profile, int index) =>
        profile is UnitAnatomyProfile anatomy && anatomy.IsInternalNode(index);

    private static bool TryGetBodyShape(
        UnitStore units,
        int unitIndex,
        UnitHitRegionProfile profile,
        int regionIndex,
        UnitBodyStore? bodies,
        out int bodyIndex,
        out Vector3 center,
        out Vector3 scale,
        out float rotation,
        out BodyPrimitive primitive,
        out float halfHeight)
    {
        bodyIndex = -1;
        center = default;
        scale = default;
        rotation = 0f;
        primitive = BodyPrimitive.Circle;
        halfHeight = 0f;

        if (bodies == null ||
            profile is not UnitAnatomyProfile anatomy ||
            !anatomy.HasSharedShapes ||
            (uint)regionIndex >= (uint)anatomy.NodeCount ||
            units.BodyCount[unitIndex] != anatomy.NodeCount)
            return false;

        bodyIndex = units.BodyStart[unitIndex] + regionIndex;
        if ((uint)bodyIndex >= (uint)bodies.Count)
            return false;

        center = bodies.WorldPosition[bodyIndex];
        scale = bodies.Scale[bodyIndex];
        rotation = bodies.WorldRotation[bodyIndex];
        primitive = bodies.Primitive[bodyIndex];
        halfHeight = anatomy.GetHitHalfHeight(regionIndex);
        return halfHeight > 0f;
    }

    private static bool TryIntersectPrimitive(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        Vector3 scale,
        float rotation,
        BodyPrimitive primitive,
        float halfHeight,
        float padding,
        out float t)
    {
        t = 0f;
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float x0 = start.X - center.X;
        float y0 = start.Y - center.Y;
        float localX = x0 * cos + y0 * sin;
        float localY = -x0 * sin + y0 * cos;
        float localDx = dx * cos + dy * sin;
        float localDy = -dx * sin + dy * cos;

        float halfX = MathF.Abs(scale.X) + padding;
        float halfY = MathF.Abs(scale.Y) + padding;
        float enter = 0f;
        float exit = 1f;
        bool hitXY;

        switch (primitive)
        {
            case BodyPrimitive.Box:
                hitXY =
                    ClipSlab(localX, localDx, -halfX, halfX, ref enter, ref exit) &&
                    ClipSlab(localY, localDy, -halfY, halfY, ref enter, ref exit);
                break;

            case BodyPrimitive.Triangle:
                hitXY =
                    ClipTriangleEdge(localX, localY, localDx, localDy,
                        halfX, 0f, -halfX, -halfY, ref enter, ref exit) &&
                    ClipTriangleEdge(localX, localY, localDx, localDy,
                        -halfX, -halfY, -halfX, halfY, ref enter, ref exit) &&
                    ClipTriangleEdge(localX, localY, localDx, localDy,
                        -halfX, halfY, halfX, 0f, ref enter, ref exit);
                break;

            case BodyPrimitive.Line:
                halfY = MathF.Max(0.008f, halfY);
                hitXY =
                    ClipSlab(localX, localDx, -halfX, halfX, ref enter, ref exit) &&
                    ClipSlab(localY, localDy, -halfY, halfY, ref enter, ref exit);
                break;

            default:
                hitXY = ClipEllipse(localX, localY, localDx, localDy,
                    halfX, halfY, ref enter, ref exit);
                break;
        }

        if (!hitXY)
            return false;

        float localZ = start.Z - center.Z;
        float dz = end.Z - start.Z;
        float halfZ = MathF.Max(0.001f, halfHeight + padding);
        if (!ClipSlab(localZ, dz, -halfZ, halfZ, ref enter, ref exit) ||
            enter > exit || exit < 0f || enter > 1f)
            return false;

        t = Math.Clamp(enter, 0f, 1f);
        return true;
    }

    private static bool ClipEllipse(
        float x, float y, float dx, float dy,
        float radiusX, float radiusY,
        ref float enter, ref float exit)
    {
        if (radiusX <= 0f || radiusY <= 0f)
            return false;

        float nx = x / radiusX;
        float ny = y / radiusY;
        float ndx = dx / radiusX;
        float ndy = dy / radiusY;
        float a = ndx * ndx + ndy * ndy;
        float b = 2f * (nx * ndx + ny * ndy);
        float c = nx * nx + ny * ny - 1f;

        if (a < 0.000001f)
            return c <= 0f;

        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
            return false;

        float root = MathF.Sqrt(discriminant);
        float t0 = (-b - root) / (2f * a);
        float t1 = (-b + root) / (2f * a);
        if (t0 > t1)
            (t0, t1) = (t1, t0);

        enter = MathF.Max(enter, t0);
        exit = MathF.Min(exit, t1);
        return enter <= exit && exit >= 0f && enter <= 1f;
    }

    private static bool ClipSlab(
        float origin, float delta, float min, float max,
        ref float enter, ref float exit)
    {
        if (MathF.Abs(delta) < 0.000001f)
            return origin >= min && origin <= max;

        float t0 = (min - origin) / delta;
        float t1 = (max - origin) / delta;
        if (t0 > t1)
            (t0, t1) = (t1, t0);

        enter = MathF.Max(enter, t0);
        exit = MathF.Min(exit, t1);
        return enter <= exit;
    }

    private static bool ClipTriangleEdge(
        float x, float y, float dx, float dy,
        float ax, float ay, float bx, float by,
        ref float enter, ref float exit)
    {
        // Vertices are clockwise (+X is forward), so interior is <= 0.
        float edgeX = bx - ax;
        float edgeY = by - ay;
        float a = edgeX * (y - ay) - edgeY * (x - ax);
        float b = edgeX * dy - edgeY * dx;

        if (MathF.Abs(b) < 0.000001f)
            return a <= 0f;

        float crossing = -a / b;
        if (b > 0f)
            exit = MathF.Min(exit, crossing);
        else
            enter = MathF.Max(enter, crossing);

        return enter <= exit;
    }

    private static bool TryIntersectCylinder(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        float radius,
        float halfHeight,
        out float t)
    {
        t = 0f;

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float dz = end.Z - start.Z;
        float ox = start.X - center.X;
        float oy = start.Y - center.Y;

        float horizontalA = dx * dx + dy * dy;
        float horizontalB = 2f * (ox * dx + oy * dy);
        float horizontalC = ox * ox + oy * oy - radius * radius;

        float horizontalEnter = 0f;
        float horizontalExit = 1f;

        if (horizontalA < 0.000001f)
        {
            if (horizontalC > 0f)
                return false;
        }
        else
        {
            float discriminant =
                horizontalB * horizontalB -
                4f * horizontalA * horizontalC;

            if (discriminant < 0f)
                return false;

            float root = MathF.Sqrt(discriminant);
            float t0 = (-horizontalB - root) / (2f * horizontalA);
            float t1 = (-horizontalB + root) / (2f * horizontalA);

            horizontalEnter = MathF.Max(0f, MathF.Min(t0, t1));
            horizontalExit = MathF.Min(1f, MathF.Max(t0, t1));

            if (horizontalEnter > horizontalExit)
                return false;
        }

        float verticalMin = center.Z - halfHeight;
        float verticalMax = center.Z + halfHeight;
        float verticalEnter = 0f;
        float verticalExit = 1f;

        if (MathF.Abs(dz) < 0.000001f)
        {
            if (start.Z < verticalMin || start.Z > verticalMax)
                return false;
        }
        else
        {
            float t0 = (verticalMin - start.Z) / dz;
            float t1 = (verticalMax - start.Z) / dz;

            verticalEnter = MathF.Max(0f, MathF.Min(t0, t1));
            verticalExit = MathF.Min(1f, MathF.Max(t0, t1));

            if (verticalEnter > verticalExit)
                return false;
        }

        float enter = MathF.Max(horizontalEnter, verticalEnter);
        float exit = MathF.Min(horizontalExit, verticalExit);

        if (enter > exit)
            return false;

        t = enter;
        return true;
    }
}
