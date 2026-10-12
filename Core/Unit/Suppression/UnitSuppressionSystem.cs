using System;
using System.Numerics;

namespace Core.Unit;

public static class UnitSuppressionSystem
{
    public static void AddNearMiss(
        UnitSuppressionStore suppression,
        int unitIndex,
        Vector3 unitPosition,
        Vector3 segmentStart,
        Vector3 segmentEnd,
        float danger,
        float factor)
    {
        float distance = DistanceToSegment2D(
            unitPosition,
            segmentStart,
            segmentEnd);

        ApplyNearMiss(suppression, unitIndex, distance, segmentEnd, danger, factor);
    }

    public static void AddNearMiss(
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

            float centerDistance = DistanceToSegment2D(
                region.Center,
                segmentStart,
                segmentEnd);
            distance = MathF.Min(distance, MathF.Max(0f, centerDistance - region.Radius));
        }

        ApplyNearMiss(suppression, unitIndex, distance, segmentEnd, danger, factor);
    }

    private static void ApplyNearMiss(
        UnitSuppressionStore suppression,
        int unitIndex,
        float distance,
        Vector3 impactPosition,
        float danger,
        float factor)
    {
        if (distance > 1.5f)
            return;

        float proximity = 1f / (0.25f + distance * distance);
        float amount = danger * factor * proximity * 0.0025f;
        suppression.Add(unitIndex, amount, impactPosition);
    }

    private static float DistanceToSegment2D(
        Vector3 point,
        Vector3 start,
        Vector3 end)
    {
        Vector2 p =
            new Vector2(point.X, point.Y);

        Vector2 a =
            new Vector2(start.X, start.Y);

        Vector2 b =
            new Vector2(end.X, end.Y);

        Vector2 ab = b - a;
        float lengthSquared = ab.LengthSquared();

        if (lengthSquared <= 0.000001f)
            return (p - a).Length();

        float t =
            Vector2.Dot(
                p - a,
                ab) /
            lengthSquared;

        t = Math.Clamp(t, 0f, 1f);

        Vector2 closest =
            a + ab * t;

        return (p - closest).Length();
    }
}
