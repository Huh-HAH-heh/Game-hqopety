using System;

namespace Core.Unit;

/// <summary>
/// Pure utility formulas currently used by the hybrid decision layer.
/// These formulas intentionally preserve the existing behavior; they are
/// centralized here before the available-action scoring model is expanded.
/// </summary>
public static class UnitTacticalUtility
{
    private const float MoveDistanceForMaximumUtility = 24f;
    private const float MoveOrderBaseUtility = 0.50f;
    private const float MoveOrderDistanceBonus = 0.30f;
    private const float SuppressionMovePenalty = 0.12f;
    private const float LowHealthMovePenalty = 0.10f;

    public static float EvaluateMoveOrder(
        float distance,
        bool suppressedOrPanicked,
        bool lowHealth)
    {
        if (!float.IsFinite(distance))
            distance = 0f;

        float utility = MoveOrderBaseUtility +
            MoveOrderDistanceBonus *
            Math.Clamp(distance / MoveDistanceForMaximumUtility, 0f, 1f);

        if (suppressedOrPanicked)
            utility -= SuppressionMovePenalty;

        if (lowHealth)
            utility -= LowHealthMovePenalty;

        return MathF.Max(0f, utility);
    }

    public static float EvaluateVisibleCombat(float distance, bool hasVisibleTarget)
    {
        if (!hasVisibleTarget || !float.IsFinite(distance))
            return 0f;

        if (distance <= 6f)
            return 0.96f;

        if (distance <= 12f)
            return 0.84f;

        if (distance <= 24f)
            return 0.68f;

        return 0.54f;
    }

    public static float EvaluateProneFiring(
        float distanceToTarget,
        bool exposed,
        float healthRatio)
    {
        if (!float.IsFinite(distanceToTarget))
            distanceToTarget = 0f;

        if (!float.IsFinite(healthRatio))
            healthRatio = 0f;

        float distanceAdvantage = Math.Clamp(
            (distanceToTarget - 9f) / 25f,
            0f,
            1f);
        float exposureAdvantage = exposed ? 1f : 0.2f;

        return Math.Clamp(
            distanceAdvantage * 0.65f +
            exposureAdvantage * 0.25f +
            Math.Clamp(healthRatio, 0f, 1f) * 0.10f,
            0f,
            1f);
    }
}
