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
    private const float CoverSuppressedThreshold = 1.8f;
    private const float CoverPanickedThreshold = 5.8f;
    private const float InvestigationDistanceForMaxPenalty = 64f;

    public const float FlankUtilityAdvantage = 0.08f;
    public const float MinimumInvestigationUtility = 0.24f;
    public const float MinimumSquadContactUtility = 0.30f;

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

    public static float EvaluateCover(
        float suppression,
        bool hasCover,
        bool immediateDanger,
        bool panicked,
        bool lowHealth)
    {
        if (!hasCover)
            return 0f;

        if (immediateDanger || panicked)
            return 1f;

        if (!float.IsFinite(suppression))
            suppression = 0f;

        float pressure = Math.Clamp(
            (suppression - CoverSuppressedThreshold) /
            (CoverPanickedThreshold - CoverSuppressedThreshold),
            0f,
            1f);

        return Math.Clamp(
            0.18f +
            pressure * 0.66f +
            (lowHealth ? 0.16f : 0f),
            0f,
            1f);
    }

    public static float EvaluateFlanking(
        float distance,
        float effectiveRange,
        bool feasible)
    {
        if (!feasible ||
            !float.IsFinite(distance) ||
            !float.IsFinite(effectiveRange) ||
            distance < 0f ||
            effectiveRange <= 0f)
        {
            return 0f;
        }

        float rangeDeficit = Math.Clamp(
            (distance - effectiveRange) / MathF.Max(effectiveRange, 1f),
            0f,
            1f);
        float distanceAdvantage = Math.Clamp(
            (distance - 24f) / 36f,
            0f,
            1f);

        return Math.Clamp(
            0.32f +
            rangeDeficit * 0.18f +
            distanceAdvantage * 0.22f,
            0f,
            1f);
    }

    public static float EvaluateInvestigation(float confidence, float distance)
    {
        if (!float.IsFinite(confidence) ||
            !float.IsFinite(distance) ||
            distance < 0f)
        {
            return 0f;
        }

        float distancePenalty = Math.Clamp(
            distance / InvestigationDistanceForMaxPenalty,
            0f,
            1f) * 0.35f;

        return Math.Clamp(
            0.25f +
            Math.Clamp(confidence, 0f, 1f) * 0.75f -
            distancePenalty,
            0f,
            1f);
    }

    public static float EvaluateInvestigation(
        float confidence,
        float distance,
        float positionErrorRadius)
    {
        if (!float.IsFinite(positionErrorRadius) || positionErrorRadius < 0f)
            return 0f;

        float uncertaintyPenalty =
            Math.Clamp(positionErrorRadius / 48f, 0f, 1f) * 0.20f;
        return Math.Clamp(
            EvaluateInvestigation(confidence, distance) - uncertaintyPenalty,
            0f,
            1f);
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
