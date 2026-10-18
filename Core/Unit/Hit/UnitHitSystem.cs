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
        if (bodies != null &&
            profile is UnitAnatomyProfile anatomy &&
            anatomy.HasSharedShapes &&
            TryGetMarkerPoint(
                units, unitIndex, UnitAnatomyMarkerRole.Eye,
                out Vector3 eye, hitRegions, bodies))
        {
            return eye;
        }

        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        float eyeHeight = units.Posture[unitIndex] switch
        {
            UnitPosture.Lying => 0.17f,
            UnitPosture.Crouching => 0.56f,
            UnitPosture.Sitting => 0.68f,
            _ => 0.82f
        };

        return position + new Vector3(0f, 0f, height * eyeHeight);
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
        int visibleCount = CountTargetRegions(profile);
        if ((uint)visibleIndex >= (uint)visibleCount)
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));

        return profile is UnitAnatomyProfile anatomy
            ? anatomy.GetVisibleRegionIndex(visibleIndex)
            : visibleIndex;
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
            !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            return false;

        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if ((uint)regionIndex >= (uint)profile.RegionCount)
            return false;

        if (TryGetBodyShape(
                units, unitIndex, profile, regionIndex, bodies,
                out _, out Vector3 shapeCenter, out Vector3 shapeScale,
                out float shapeRotation, out BodyPrimitive primitive, out float shapeHalfHeight))
        {
            return IsPointInsidePrimitive(
                point, shapeCenter, shapeScale, shapeRotation, primitive,
                shapeHalfHeight, tolerance);
        }

        GetGameplayCollider(units, unitIndex, out Vector3 center, out float radius, out float halfHeight);
        float rx = radius + tolerance;
        float ry = radius + tolerance;
        float rz = halfHeight + tolerance;
        float dx = (point.X - center.X) / rx;
        float dy = (point.Y - center.Y) / ry;
        float dz = (point.Z - center.Z) / rz;
        return dx * dx + dy * dy + dz * dz <= 1f;
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

        // Shared anatomy geometry is the single source of truth for spotting and
        // aiming. Use the live posed primitive centre so prone targets are not
        // represented by an imaginary standing-height point above their silhouette.
        if (bodies != null &&
            profile is UnitAnatomyProfile anatomy &&
            anatomy.HasSharedShapes &&
            !anatomy.IsInternalNode(regionIndex) &&
            TryGetBodyShape(
                units, unitIndex, profile, regionIndex, bodies,
                out _, out Vector3 shapeCenter, out Vector3 shapeScale,
                out float shapeRotation, out BodyPrimitive primitive, out _))
        {
            // Aim at the observer-facing side of the live primitive, not a point
            // floating above the root or hidden behind the target's own silhouette.
            return GetVisibleSurfacePoint(
                source, shapeCenter, shapeScale, shapeRotation, primitive);
        }

        Vector3 position = units.Position[unitIndex];
        Vector3 direction = new(source.X - position.X, source.Y - position.Y, 0f);
        float lengthSquared = direction.LengthSquared();
        direction = lengthSquared > 0.000001f
            ? direction / MathF.Sqrt(lengthSquared)
            : Vector3.UnitX;

        float radius = MathF.Max(0.01f, units.Radius[unitIndex]);
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        float heightFraction = (markerRole & (UnitAnatomyMarkerRole.Eye | UnitAnatomyMarkerRole.Muzzle)) != 0
            ? 0.82f
            : (markerRole & UnitAnatomyMarkerRole.Vision) != 0
                ? 0.70f
                : 0.52f;

        return new Vector3(
            position.X + direction.X * radius * 0.65f,
            position.Y + direction.Y * radius * 0.65f,
            position.Z + height * heightFraction);
    }

    private static Vector3 Rotate2D(Vector3 value, float angle)
    {
        float cosine = MathF.Cos(angle);
        float sine = MathF.Sin(angle);
        return new Vector3(
            value.X * cosine - value.Y * sine,
            value.X * sine + value.Y * cosine,
            value.Z);
    }

    private static Vector3 GetVisibleSurfacePoint(
        Vector3 source,
        Vector3 center,
        Vector3 scale,
        float rotation,
        BodyPrimitive primitive)
    {
        Vector3 towardSource = new(
            source.X - center.X,
            source.Y - center.Y,
            0f);
        float lengthSquared = towardSource.LengthSquared();
        towardSource = lengthSquared > 0.000001f
            ? towardSource / MathF.Sqrt(lengthSquared)
            : Vector3.UnitX;

        Vector3 localDirection = Rotate2D(towardSource, -rotation);
        float radiusX = MathF.Max(0.001f, MathF.Abs(scale.X));
        float radiusY = MathF.Max(0.001f, MathF.Abs(scale.Y));
        float distance = primitive switch
        {
            BodyPrimitive.Circle =>
                1f / MathF.Sqrt(
                    localDirection.X * localDirection.X / (radiusX * radiusX) +
                    localDirection.Y * localDirection.Y / (radiusY * radiusY)),
            BodyPrimitive.Triangle =>
                GetTriangleSurfaceDistance(radiusX, radiusY, localDirection.X, localDirection.Y),
            _ => MathF.Min(
                radiusX / MathF.Max(0.0001f, MathF.Abs(localDirection.X)),
                radiusY / MathF.Max(0.0001f, MathF.Abs(localDirection.Y)))
        };

        const float inset = 0.90f;
        Vector3 offset = Rotate2D(localDirection * (distance * inset), rotation);
        return new Vector3(center.X + offset.X, center.Y + offset.Y, center.Z);
    }

    private static float GetTriangleSurfaceDistance(
        float halfLength,
        float halfWidth,
        float directionX,
        float directionY)
    {
        float distance = halfLength /
            MathF.Max(0.0001f, MathF.Abs(directionX));
        float sideDenominator = MathF.Abs(directionY) +
            halfWidth * directionX / (2f * halfLength);
        if (sideDenominator > 0.0001f)
        {
            float sideDistance = halfWidth / (2f * sideDenominator);
            distance = MathF.Min(distance, sideDistance);
        }

        return distance;
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
        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        float sphereRadius = MathF.Max(MathF.Max(0.01f, units.Radius[unitIndex]), height * 0.5f);
        Vector3 center = position + new Vector3(0f, 0f, sphereRadius);
        float padding = MathF.Max(0f, projectileRadius);
        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);

        // Match a projectile against the actual posed rig first. Previously every
        // anatomical region delegated to one huge unit capsule and then selected a
        // random tissue from the entire body by height, so limbs/torso/head weren't
        // actual hit targets.
        if (bodies != null && profile is UnitAnatomyProfile anatomy &&
            units.BodyCount[unitIndex] > 0)
        {
            int startBody = units.BodyStart[unitIndex];
            int bodyCount = Math.Min(anatomy.NodeCount, units.BodyCount[unitIndex]);
            float bestT = float.PositiveInfinity;
            int bestRegion = -1;
            int bestBinding = -1;
            UnitHealthPartId bestPart = UnitHealthPartId.None;
            Vector3 bestPosition = default;

            for (int regionIndex = 0; regionIndex < bodyCount; regionIndex++)
            {
                if (!TryGetBodyShape(
                        units, unitIndex, profile, regionIndex, bodies,
                        out _, out Vector3 shapeCenter, out Vector3 shapeScale,
                        out float shapeRotation, out BodyPrimitive primitive, out float halfHeight))
                {
                    continue;
                }

                if (!TryIntersectPrimitive(
                        start, end, shapeCenter, shapeScale, shapeRotation,
                        primitive, halfHeight, padding, out float t) ||
                    t >= bestT)
                {
                    continue;
                }

                Vector3 hitPosition = start + (end - start) * t;
                float heightFraction = Math.Clamp(
                    (hitPosition.Z - position.Z) / height, 0f, 1f);

                if (!TrySelectDamageBinding(
                        profile, unitIndex, hitPosition, heightFraction, ignoredPart,
                        out int regionForHit, out int bindingForHit, out UnitHealthPartId part,
                        forcedRegionIndex: regionIndex))
                {
                    continue;
                }

                bestT = t;
                bestRegion = regionForHit;
                bestBinding = bindingForHit;
                bestPart = part;
                bestPosition = hitPosition;
            }

            if (bestRegion >= 0)
            {
                result = new UnitHitResult(bestPart, bestPosition, bestT, bestRegion, bestBinding);
                return true;
            }

            result = default;
            return false;
        }

        // Compatibility fallback for archetypes without a shared authored rig.
        if (!TryIntersectSphere(start, end, center, sphereRadius + padding, out float fallbackT))
        {
            result = default;
            return false;
        }

        Vector3 fallbackPosition = start + (end - start) * fallbackT;
        float fallbackHeightFraction = Math.Clamp(
            (fallbackPosition.Z - position.Z) / height, 0f, 1f);
        if (!TrySelectDamageBinding(
                profile, unitIndex, fallbackPosition, fallbackHeightFraction, ignoredPart,
                out int fallbackRegion, out int fallbackBinding, out UnitHealthPartId fallbackPart))
        {
            result = default;
            return false;
        }

        result = new UnitHitResult(
            fallbackPart, fallbackPosition, fallbackT, fallbackRegion, fallbackBinding);
        return true;
    }

    public static bool TryFindNextAnatomyVolumeHit(
        UnitStore units,
        int unitIndex,
        int parentRegionIndex,
        string entryBindingId,
        Vector3 origin,
        Vector3 direction,
        float minimumDistance,
        int previousVolumeIndex,
        float padding,
        out UnitAnatomyVolumeHit result,
        UnitBodyStore? bodies = null,
        UnitHitRegionStore? hitRegions = null)
    {
        result = default;
        if (unitIndex < 0 || unitIndex >= units.Capacity ||
            !float.IsFinite(minimumDistance) ||
            !float.IsFinite(padding) || padding < 0f ||
            direction.LengthSquared() < 0.000001f ||
            !TryGetBodyShape(
                units, unitIndex, GetProfile(units, unitIndex, hitRegions),
                parentRegionIndex, bodies,
                out int parentBody, out _, out _, out _, out _, out _))
        {
            return false;
        }

        UnitHitRegionProfile profile = GetProfile(units, unitIndex, hitRegions);
        if (profile is not UnitAnatomyProfile anatomy)
            return false;

        UnitAnatomyNode parentNode = anatomy.GetNode(parentRegionIndex);

        Vector3 rayDirection = Vector3.Normalize(direction);
        const float maxDistance = 1.5f;
        Vector3 rayEnd = origin + rayDirection * maxDistance;
        ReadOnlySpan<UnitAnatomyVolume> volumes = anatomy.Volumes;
        float bestDistance = float.PositiveInfinity;

        for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
        {
            UnitAnatomyVolume volume = volumes[volumeIndex];
            if (volumeIndex == previousVolumeIndex ||
                !string.Equals(volume.ParentRegionId, parentNode.Id, StringComparison.Ordinal) ||
                !string.Equals(volume.EntryBindingId, entryBindingId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!anatomy.TryFindPart(volume.TargetBindingId, out int targetRegion, out int targetBinding) ||
                targetRegion != parentRegionIndex)
            {
                continue;
            }

            Quaternion parentOrientation = bodies!.WorldOrientation[parentBody];
            Vector3 center = bodies.WorldPosition[parentBody] +
                Vector3.Transform(volume.LocalCenter, parentOrientation);
            float rotation = bodies.WorldRotation[parentBody] + volume.LocalRotation;

            if (!TryIntersectPrimitive(
                    origin, rayEnd, center, volume.HalfExtents, rotation,
                    volume.Primitive, volume.HalfExtents.Z, padding, out float t))
            {
                continue;
            }

            float distance = t * maxDistance;
            if (distance + 0.0001f < minimumDistance ||
                distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            result = new UnitAnatomyVolumeHit(
                volume, volumeIndex, targetRegion, targetBinding,
                origin + rayDirection * distance, distance);
        }

        return bestDistance < float.PositiveInfinity;
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

    private static bool TrySelectDamageBinding(
        UnitHitRegionProfile profile,
        int unitIndex,
        Vector3 point,
        float heightFraction,
        UnitHealthPartId ignoredPart,
        out int regionIndex,
        out int bindingIndex,
        out UnitHealthPartId part,
        int forcedRegionIndex = -1)
    {
        regionIndex = -1;
        bindingIndex = -1;
        part = UnitHealthPartId.None;
        float totalWeight = 0f;

        int firstRegion = forcedRegionIndex >= 0 ? forcedRegionIndex : 0;
        int lastRegion = forcedRegionIndex >= 0 ? forcedRegionIndex + 1 : profile.RegionCount;
        if (firstRegion < 0 || lastRegion > profile.RegionCount)
            return false;

        for (int r = firstRegion; r < lastRegion; r++)
        {
            ReadOnlySpan<HitRegionPartBinding> bindings = GetBindings(profile, r);
            for (int b = 0; b < bindings.Length; b++)
            {
                HitRegionPartBinding binding = bindings[b];
                if ((ignoredPart != UnitHealthPartId.None && binding.Part == ignoredPart) ||
                    (forcedRegionIndex >= 0 && binding.Kind == UnitHealthPartKind.Organ))
                {
                    continue;
                }

                float likelihood = GetHeightLikelihood(binding.Part, heightFraction);
                if (binding.Kind == UnitHealthPartKind.Organ)
                    likelihood *= 0.16f;
                totalWeight += MathF.Sqrt(MathF.Max(0.01f, binding.Weight)) * likelihood;
            }
        }

        if (totalWeight <= 0f || !float.IsFinite(totalWeight))
            return false;

        uint hash = unchecked((uint)(unitIndex + 1) * 2654435761u);
        hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(point.X) * 2246822519u);
        hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(point.Y) * 3266489917u);
        hash ^= unchecked((uint)BitConverter.SingleToInt32Bits(point.Z) * 668265263u);
        hash ^= hash >> 16;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        float roll = (hash / 4294967296f) * totalWeight;

        for (int r = firstRegion; r < lastRegion; r++)
        {
            ReadOnlySpan<HitRegionPartBinding> bindings = GetBindings(profile, r);
            for (int b = 0; b < bindings.Length; b++)
            {
                HitRegionPartBinding binding = bindings[b];
                if ((ignoredPart != UnitHealthPartId.None && binding.Part == ignoredPart) ||
                    (forcedRegionIndex >= 0 && binding.Kind == UnitHealthPartKind.Organ))
                {
                    continue;
                }

                float likelihood = GetHeightLikelihood(binding.Part, heightFraction);
                if (binding.Kind == UnitHealthPartKind.Organ)
                    likelihood *= 0.16f;

                roll -= MathF.Sqrt(MathF.Max(0.01f, binding.Weight)) * likelihood;
                if (roll > 0f)
                    continue;

                regionIndex = r;
                bindingIndex = b;
                part = binding.Part;
                return true;
            }
        }

        for (int r = lastRegion - 1; r >= firstRegion; r--)
        {
            ReadOnlySpan<HitRegionPartBinding> bindings = GetBindings(profile, r);
            for (int b = bindings.Length - 1; b >= 0; b--)
            {
                if ((ignoredPart != UnitHealthPartId.None && bindings[b].Part == ignoredPart) ||
                    (forcedRegionIndex >= 0 && bindings[b].Kind == UnitHealthPartKind.Organ))
                {
                    continue;
                }

                regionIndex = r;
                bindingIndex = b;
                part = bindings[b].Part;
                return true;
            }
        }

        return false;
    }

    private static float GetHeightLikelihood(UnitHealthPartId part, float height)
    {
        float center;
        float sigma;
        switch (part)
        {
            case UnitHealthPartId.Head: center = 0.86f; sigma = 0.17f; break;
            case UnitHealthPartId.Brain: center = 0.91f; sigma = 0.12f; break;
            case UnitHealthPartId.Neck: center = 0.75f; sigma = 0.16f; break;
            case UnitHealthPartId.Torso: center = 0.52f; sigma = 0.27f; break;
            case UnitHealthPartId.Heart:
            case UnitHealthPartId.LeftLung:
            case UnitHealthPartId.RightLung:
            case UnitHealthPartId.Stomach:
            case UnitHealthPartId.Liver:
            case UnitHealthPartId.LeftKidney:
            case UnitHealthPartId.RightKidney:
                center = 0.49f; sigma = 0.22f; break;
            case UnitHealthPartId.LeftArm:
            case UnitHealthPartId.RightArm:
            case UnitHealthPartId.LeftHand:
            case UnitHealthPartId.RightHand:
                center = 0.54f; sigma = 0.25f; break;
            case UnitHealthPartId.LeftLeg:
            case UnitHealthPartId.RightLeg:
                center = 0.22f; sigma = 0.20f; break;
            case UnitHealthPartId.LeftFoot:
            case UnitHealthPartId.RightFoot:
                center = 0.08f; sigma = 0.15f; break;
            default:
                return 1f;
        }

        float normalized = (height - center) / sigma;
        return MathF.Max(0.015f, MathF.Exp(-0.5f * normalized * normalized));
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
        if (bodies != null &&
            TryGetBodyShape(
                units, unitIndex, profile, regionIndex, bodies,
                out int bodyIndex, out Vector3 shapeCenter, out _, out _, out _, out _))
        {
            // Anatomy markers belong to the rendered node. Transform their local
            // offset through that node's live orientation so eyes and muzzle stay
            // on the flattened head when a unit lies down or crawls.
            Vector3 localOffset = marker.LocalOffset;
            if (units.Posture[unitIndex] == UnitPosture.Lying)
                localOffset.Z *= 0.55f;

            return shapeCenter + BodyPartTransformMath.TransformPosition(
                localOffset,
                bodies.WorldOrientation[bodyIndex]);
        }

        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        Vector3 facing = (marker.Roles &
            (UnitAnatomyMarkerRole.Eye | UnitAnatomyMarkerRole.Muzzle)) != 0
            ? units.HeadNormal[unitIndex]
            : units.BodyNormal[unitIndex];
        float facingLengthSquared = facing.X * facing.X + facing.Y * facing.Y;
        facing = facingLengthSquared > 0.000001f
            ? new Vector3(facing.X / MathF.Sqrt(facingLengthSquared), facing.Y / MathF.Sqrt(facingLengthSquared), 0f)
            : Vector3.UnitX;

        float prone = units.Posture[unitIndex] == UnitPosture.Lying ? 0.17f : 0f;
        float heightFraction = (marker.Roles & UnitAnatomyMarkerRole.Eye) != 0
            ? (prone > 0f ? prone : units.Posture[unitIndex] switch
            {
                UnitPosture.Crouching => 0.56f,
                UnitPosture.Sitting => 0.68f,
                _ => 0.82f
            })
            : (marker.Roles & UnitAnatomyMarkerRole.Muzzle) != 0
                ? (prone > 0f ? prone : units.Posture[unitIndex] switch
                {
                    UnitPosture.Crouching => 0.58f,
                    UnitPosture.Sitting => 0.68f,
                    _ => 0.70f
                })
                : (marker.Roles & UnitAnatomyMarkerRole.Vision) != 0
                    ? (prone > 0f ? prone : units.Posture[unitIndex] switch
                    {
                        UnitPosture.Crouching => 0.56f,
                        UnitPosture.Sitting => 0.68f,
                        _ => 0.70f
                    })
                    : (prone > 0f ? prone : 0.52f);

        float forwardDistance = MathF.Max(0.01f, units.Radius[unitIndex]) * 0.65f;
        return position + facing * forwardDistance + new Vector3(0f, 0f, height * heightFraction);
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

        GetGameplayCollider(units, unitIndex, out center, out radius, out halfHeight);
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
        GetGameplayCollider(units, unitIndex, out center, out radius, out halfHeight);
    }

        private static void GetGameplayCollider(
        UnitStore units,
        int unitIndex,
        out Vector3 center,
        out float radius,
        out float halfHeight)
    {
        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        radius = MathF.Max(MathF.Max(0.01f, units.Radius[unitIndex]), height * 0.5f);
        halfHeight = radius;
        center = position + new Vector3(0f, 0f, radius);
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
            (uint)regionIndex >= (uint)anatomy.NodeCount ||
            anatomy.IsInternalNode(regionIndex))
        {
            return false;
        }

        UnitAnatomyNode node = anatomy.GetNode(regionIndex);
        if (!node.HasShape)
            return false;

        int bodyStart = units.BodyStart[unitIndex];
        int bodyCount = units.BodyCount[unitIndex];
        if (regionIndex >= bodyCount ||
            bodyStart < 0 ||
            bodyStart + regionIndex >= bodies.Count)
        {
            return false;
        }

        bodyIndex = bodyStart + regionIndex;
        center = bodies.WorldPosition[bodyIndex];
        scale = Vector3.Abs(bodies.WorldScale[bodyIndex]);
        rotation = bodies.WorldRotation[bodyIndex];
        primitive = bodies.Primitive[bodyIndex];
        halfHeight = MathF.Max(0.001f, scale.Z);
        return scale.X > 0f && scale.Y > 0f &&
            float.IsFinite(center.X) && float.IsFinite(center.Y) && float.IsFinite(center.Z);
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
        out float t) =>
        TryIntersectPrimitive(
            start, end, center, scale, rotation, primitive,
            halfHeight, padding, out t, out _);

    private static bool IsPointInsidePrimitive(
        Vector3 point,
        Vector3 center,
        Vector3 scale,
        float rotation,
        BodyPrimitive primitive,
        float halfHeight,
        float padding)
    {
        float tolerance = MathF.Max(0f, padding);
        if (MathF.Abs(point.Z - center.Z) > MathF.Abs(halfHeight) + tolerance)
            return false;

        float dx = point.X - center.X;
        float dy = point.Y - center.Y;
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        float localX = dx * cos + dy * sin;
        float localY = -dx * sin + dy * cos;
        float halfX = MathF.Abs(scale.X) + tolerance;
        float halfY = MathF.Abs(scale.Y) + tolerance;

        switch (primitive)
        {
            case BodyPrimitive.Box:
            case BodyPrimitive.Line:
                return MathF.Abs(localX) <= halfX &&
                    MathF.Abs(localY) <= (primitive == BodyPrimitive.Line
                        ? MathF.Max(0.008f, halfY)
                        : halfY);

            case BodyPrimitive.Triangle:
                return PointInTriangle(
                    localX, localY,
                    halfX, 0f,
                    -halfX, -halfY,
                    -halfX, halfY);

            default:
                float x = localX / MathF.Max(0.001f, halfX);
                float y = localY / MathF.Max(0.001f, halfY);
                return x * x + y * y <= 1f;
        }
    }

    private static bool PointInTriangle(
        float px, float py,
        float ax, float ay,
        float bx, float by,
        float cx, float cy)
    {
        float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
        float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
        float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
        bool hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNegative && hasPositive);
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
        out float t,
        out float exitT)
    {
        t = 0f;
        exitT = 0f;
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
        exitT = Math.Clamp(exit, 0f, 1f);
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

        private static bool TryIntersectSphere(
        Vector3 start,
        Vector3 end,
        Vector3 center,
        float radius,
        out float t)
    {
        t = 0f;
        float r = MathF.Max(0.001f, radius);
        Vector3 offset = start - center;
        Vector3 delta = end - start;
        float c = Vector3.Dot(offset, offset) - r * r;
        if (c <= 0f)
            return true;

        float a = Vector3.Dot(delta, delta);
        if (a <= 0.0000001f)
            return false;

        float b = 2f * Vector3.Dot(offset, delta);
        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f)
            return false;

        float root = MathF.Sqrt(discriminant);
        float t0 = (-b - root) / (2f * a);
        float t1 = (-b + root) / (2f * a);
        if (t0 >= 0f && t0 <= 1f)
        {
            t = t0;
            return true;
        }

        if (t1 >= 0f && t1 <= 1f)
        {
            t = t1;
            return true;
        }

        return false;
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
