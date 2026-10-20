using System;
using System.Numerics;

namespace Core.Unit;

public static class UnitSuppressionSystem
{
    public static bool AddNearMiss(
        UnitSuppressionStore suppression,
        int unitIndex,
        Vector3 unitPosition,
        Vector3 segmentStart,
        Vector3 segmentEnd,
        float danger,
        float factor)
    {
        float distance = DistanceToSegment(
            unitPosition,
            segmentStart,
            segmentEnd);

        Vector3 threatPosition = (segmentStart + segmentEnd) * 0.5f;
        return ApplyNearMiss(suppression, unitIndex, distance, threatPosition, danger, factor);
    }

    public static bool AddNearMiss(
        UnitSuppressionStore suppression,
        UnitStore units,
        UnitHitRegionStore? hitRegions,
        int unitIndex,
        Vector3 segmentStart,
        Vector3 segmentEnd,
        float danger,
        float factor)
    {
        UnitHitRegionProfile profile = hitRegions?.GetProfile(unitIndex) ??
            UnitHitRegionCatalog.Get(UnitCatalog.Get(units.Type[unitIndex]).BodyType);

        float distance = float.PositiveInfinity;
        for (int regionIndex = 0; regionIndex < profile.RegionCount; regionIndex++)
        {
            UnitHitRegionGeometry region = UnitHitSystem.GetRegionGeometry(
                units,
                unitIndex,
                regionIndex,
                hitRegions);

            float centerDistance = DistanceToSegment(
                region.Center,
                segmentStart,
                segmentEnd);
            distance = MathF.Min(distance, MathF.Max(0f, centerDistance - region.Radius));
        }

        return ApplyNearMiss(suppression, unitIndex, distance, segmentEnd, danger, factor);
    }

    private static bool ApplyNearMiss(
        UnitSuppressionStore suppression,
        int unitIndex,
        float distance,
        Vector3 impactPosition,
        float danger,
        float factor)
    {
        if (!float.IsFinite(distance) || distance > 1.5f ||
            !float.IsFinite(danger) || danger <= 0f ||
            !float.IsFinite(factor) || factor <= 0f)
            return false;

        float proximity = 1f / (0.25f + distance * distance);
        float amount = danger * factor * proximity * 0.0014f;
        suppression.Add(unitIndex, amount, impactPosition);

        // Only a physically close projectile causes immediate-danger behavior.
        // Stronger rounds and higher suppression factors keep that reaction active longer.
        float distanceWeight = 1f / (1f + distance * distance);
        float dangerWeight = Math.Clamp(danger / 35f, 0.35f, 1.4f);
        float weaponWeight = Math.Clamp(factor, 0f, 1.25f);
        float dangerDuration = Math.Clamp(
            0.18f + 0.70f * distanceWeight * dangerWeight * weaponWeight,
            0.18f,
            1.0f);

        if (suppression.InCover[unitIndex])
            dangerDuration *= 0.60f;

        suppression.MarkImmediateDanger(unitIndex, dangerDuration, impactPosition);
        return true;
    }

    private static float DistanceToSegment(
        Vector3 point,
        Vector3 start,
        Vector3 end)
    {
        Vector3 segment = end - start;
        float lengthSquared = segment.LengthSquared();

        if (lengthSquared <= 0.000001f)
            return (point - start).Length();

        float t = Math.Clamp(
            Vector3.Dot(point - start, segment) / lengthSquared,
            0f,
            1f);

        Vector3 closest = start + segment * t;
        return (point - closest).Length();
    }
}
