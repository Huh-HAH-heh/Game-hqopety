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
/// Each configured circle is a vertical cylinder in world space: radius in XY,
/// half-height in Z. All geometry comes from the unit's assigned data profile.
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
        ReadOnlySpan<UnitCircleHitRegion> regions = profile.Regions;

        for (int i = 0; i < regions.Length; i++)
        {
            if (regions[i].TryGetMarker(UnitAnatomyMarkerRole.Eye, out UnitAnatomyMarker marker))
                return GetMarkerWorldPosition(units, unitIndex, i, marker, hitRegions);
        }

        for (int i = 0; i < regions.Length; i++)
        {
            UnitCircleHitRegion region = regions[i];
            foreach (HitRegionPartBinding part in region.Parts)
            {
                if (part.Part != UnitHealthPartId.Head)
                    continue;

                GetWorldGeometry(units, unitIndex, region,
                    out Vector3 center, out _, out float halfHeight);
                return center + new Vector3(0f, 0f, halfHeight * 0.55f);
            }
        }

        return units.Position[unitIndex] +
            new Vector3(0f, 0f, MathF.Max(0.05f, units.Height[unitIndex]) * 0.8f);
    }

    public static int GetVisibilityPointCount(
        UnitStore units,
        int unitIndex,
        UnitHitRegionStore? hitRegions = null) =>
        GetProfile(units, unitIndex, hitRegions).RegionCount;

    public static int GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null) =>
        FillRegionPoints(units, unitIndex, source, points, hitRegions, UnitAnatomyMarkerRole.Vision);

    public static int GetAimPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null) =>
        FillRegionPoints(units, unitIndex, source, points, hitRegions, UnitAnatomyMarkerRole.Aim);

    public static UnitHitRegionGeometry GetRegionGeometry(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        if ((uint)regionIndex >= (uint)profile.RegionCount)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));

        UnitCircleHitRegion region = profile.Regions[regionIndex];
        GetWorldGeometry(
            units,
            unitIndex,
            region,
            out Vector3 center,
            out float radius,
            out float halfHeight);

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
        ReadOnlySpan<UnitCircleHitRegion> regions = profile.Regions;

        for (int i = 0; i < regions.Length; i++)
        {
            if (RegionHasPart(regions[i], part))
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
               RegionHasPart(profile.Regions[regionIndex], part);
    }

    public static bool IsPointInsideRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        UnitHealthPartId part,
        Vector3 point,
        float tolerance = 0f,
        UnitHitRegionStore? hitRegions = null)
    {
        return IsPartInRegion(units, unitIndex, regionIndex, part, hitRegions) &&
               IsPointInsideRegion(units, unitIndex, regionIndex, point, tolerance, hitRegions);
    }

    public static bool IsPointInsideRegion(
        UnitStore units,
        int unitIndex,
        int regionIndex,
        Vector3 point,
        float tolerance = 0f,
        UnitHitRegionStore? hitRegions = null)
    {
        if (!float.IsFinite(tolerance) || tolerance < 0f ||
            !float.IsFinite(point.X) ||
            !float.IsFinite(point.Y) ||
            !float.IsFinite(point.Z))
        {
            return false;
        }

        UnitHitRegionGeometry geometry = GetRegionGeometry(
            units, unitIndex, regionIndex, hitRegions);

        float dx = point.X - geometry.Center.X;
        float dy = point.Y - geometry.Center.Y;
        float radius = geometry.Radius + tolerance;

        return dx * dx + dy * dy <= radius * radius &&
               MathF.Abs(point.Z - geometry.Center.Z) <= geometry.HalfHeight + tolerance;
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
               (uint)bindingIndex < (uint)profile.Regions[regionIndex].Parts.Length;
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
            (uint)bindingIndex < (uint)profile.Regions[regionIndex].Parts.Length)
        {
            binding = profile.Regions[regionIndex].Parts[bindingIndex];
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
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        for (int regionIndex = 0; regionIndex < profile.RegionCount; regionIndex++)
        {
            if (!profile.Regions[regionIndex].TryGetMarker(role, out UnitAnatomyMarker marker))
                continue;

            point = GetMarkerWorldPosition(units, unitIndex, regionIndex, marker, hitRegions);
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
        UnitHitRegionStore? hitRegions = null)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
        {
            point = Vector3.Zero;
            return false;
        }

        foreach (UnitAnatomyMarker marker in profile.Regions[regionIndex].Markers)
        {
            if (!string.Equals(marker.Id, markerId, StringComparison.Ordinal))
                continue;

            point = GetMarkerWorldPosition(units, unitIndex, regionIndex, marker, hitRegions);
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
        UnitHitRegionStore? hitRegions = null)
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
                    hitRegions))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool RegionHasPart(
        in UnitCircleHitRegion region,
        UnitHealthPartId part)
    {
        foreach (HitRegionPartBinding binding in region.Parts)
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
        UnitAnatomyMarkerRole markerRole = UnitAnatomyMarkerRole.Target)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
            throw new ArgumentOutOfRangeException(nameof(regionIndex));

        UnitCircleHitRegion region = profile.Regions[regionIndex];
        GetWorldGeometry(units, unitIndex, region,
            out Vector3 center, out float radius, out float halfHeight);

        UnitAnatomyMarker marker;
        bool hasMarker = markerRole switch
        {
            UnitAnatomyMarkerRole.Vision =>
                region.TryGetMarker(UnitAnatomyMarkerRole.Vision, out marker) ||
                region.TryGetMarker(UnitAnatomyMarkerRole.Target, out marker) ||
                region.TryGetMarker(UnitAnatomyMarkerRole.Aim, out marker),
            UnitAnatomyMarkerRole.Aim =>
                region.TryGetMarker(UnitAnatomyMarkerRole.Aim, out marker) ||
                region.TryGetMarker(UnitAnatomyMarkerRole.Target, out marker) ||
                region.TryGetMarker(UnitAnatomyMarkerRole.Vision, out marker),
            _ =>
                region.TryGetMarker(UnitAnatomyMarkerRole.Target, out marker) ||
                region.TryGetMarker(UnitAnatomyMarkerRole.Aim, out marker) ||
                region.TryGetMarker(UnitAnatomyMarkerRole.Vision, out marker)
        };

        if (hasMarker)
            return GetMarkerWorldPosition(units, unitIndex, regionIndex, marker, hitRegions);

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
        ReadOnlySpan<UnitCircleHitRegion> regions = profile.Regions;

        bool found = false;
        float bestT = 2f;
        UnitHealthPartId bestPart = UnitHealthPartId.None;
        int bestRegion = -1;
        int bestBinding = -1;
        Vector3 bestPosition = default;

        for (int i = 0; i < regions.Length; i++)
        {
            UnitCircleHitRegion region = regions[i];
            GetWorldGeometry(units, unitIndex, region,
                out Vector3 center, out float radius, out float halfHeight);

            if (!TryIntersectCylinder(
                    start,
                    end,
                    center,
                    radius + MathF.Max(0f, projectileRadius),
                    halfHeight + MathF.Max(0f, projectileRadius),
                    out float t))
            {
                continue;
            }

            if (t >= bestT)
                continue;

            Vector3 hitPosition = start + (end - start) * t;
            int bindingIndex = SelectPart(region, hitPosition, unitIndex, i, out UnitHealthPartId part);

            if (ignoredPart != UnitHealthPartId.None && part == ignoredPart)
                continue;

            bestT = t;
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
        UnitAnatomyMarkerRole markerRole)
    {
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        if (points.Length < profile.RegionCount)
            throw new ArgumentException("Insufficient hit-region point buffer.", nameof(points));

        for (int i = 0; i < profile.RegionCount; i++)
            points[i] = GetRegionPoint(units, unitIndex, i, source, hitRegions, markerRole);

        return profile.RegionCount;
    }

    private static int SelectPart(
        in UnitCircleHitRegion region,
        Vector3 point,
        int unitIndex,
        int regionIndex,
        out UnitHealthPartId part)
    {
        ReadOnlySpan<HitRegionPartBinding> parts = region.Parts;
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
        UnitHitRegionStore? hitRegions)
    {
        Vector3 center = GetRegionGeometry(units, unitIndex, regionIndex, hitRegions).Center;
        Vector3 bodyNormal = units.BodyNormal[unitIndex];
        float angle = MathF.Atan2(bodyNormal.Y, bodyNormal.X);
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);
        Vector3 offset = marker.LocalOffset;

        return center + new Vector3(
            offset.X * cos - offset.Y * sin,
            offset.X * sin + offset.Y * cos,
            offset.Z);
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
        in UnitCircleHitRegion region,
        out Vector3 center,
        out float radius,
        out float halfHeight)
    {
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

        center = position + new Vector3(
            offset.X * cos - offset.Y * sin,
            offset.X * sin + offset.Y * cos,
            offset.Z);

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
