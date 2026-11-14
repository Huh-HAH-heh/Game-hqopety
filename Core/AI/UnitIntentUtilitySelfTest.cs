using Core.AI.Behaviac;

namespace Core.Unit;

/// <summary>
/// Focused checks for tactical Utility AI candidate availability and order priority.
/// </summary>
public static class UnitIntentUtilitySelfTest
{
    public static bool DistantVisibleContactCanChooseSuppressiveFire()
    {
        UnitIntentUtilityContext context = new()
        {
            IsAlive = true,
            HasVisibleTarget = true,
            HasRangedWeapon = true,
            HasSuppressiveWeapon = true,
            HealthRatio = 0.9f,
            EnergyRatio = 1f,
            HydrationRatio = 1f,
            ShootingSkill = 0.9f,
            SuppressionResistance = 0.8f,
            Caution = 0.9f,
            Aggression = 0.2f,
            Discipline = 0.8f,
            CombatUtility = 0.54f
        };

        UnitIntentUtilityBreakdown decision = UnitIntentUtilityScorer.Select(
            in context,
            BehaviacUnitIntent.None,
            0f);

        return decision.SuggestedIntent == BehaviacUnitIntent.SuppressiveFire &&
            decision.SuppressiveFireScore > decision.EngageScore;
    }

    public static bool SuppressiveFireCannotOverridePlayerMoveOrder()
    {
        UnitIntentUtilityContext context = new()
        {
            IsAlive = true,
            HasVisibleTarget = true,
            HasRangedWeapon = true,
            HasSuppressiveWeapon = true,
            OrderType = UnitAiOrderType.Move,
            MoveOrderUtility = 1f,
            HealthRatio = 0.9f,
            EnergyRatio = 1f,
            HydrationRatio = 1f,
            ShootingSkill = 0.9f,
            SuppressionResistance = 0.8f,
            Caution = 0.9f,
            Aggression = 0.2f,
            Discipline = 0.8f,
            CombatUtility = 0.54f
        };

        UnitIntentUtilityBreakdown decision = UnitIntentUtilityScorer.Select(
            in context,
            BehaviacUnitIntent.None,
            0f);

        return decision.SuggestedIntent == BehaviacUnitIntent.FollowMoveOrder &&
            decision.SuppressiveFireScore == 0f;
    }

    public static bool FriendlyFireRiskReducesButDoesNotForbidEngagement()
    {
        UnitIntentUtilityContext safeContext = new()
        {
            IsAlive = true,
            HasVisibleTarget = true,
            HasRangedWeapon = true,
            HealthRatio = 0.95f,
            EnergyRatio = 1f,
            HydrationRatio = 1f,
            ShootingSkill = 0.85f,
            SuppressionResistance = 0.8f,
            Caution = 0.6f,
            Aggression = 0.65f,
            Discipline = 0.75f,
            CombatUtility = 0.72f
        };

        UnitIntentUtilityContext blockedLaneContext = safeContext with
        {
            FriendlyFireRisk = 1f
        };

        UnitIntentUtilityBreakdown safeDecision = UnitIntentUtilityScorer.Select(
            in safeContext,
            BehaviacUnitIntent.None,
            0f);
        UnitIntentUtilityBreakdown riskyDecision = UnitIntentUtilityScorer.Select(
            in blockedLaneContext,
            BehaviacUnitIntent.None,
            0f);

        return riskyDecision.EngageScore < safeDecision.EngageScore &&
               riskyDecision.EngageScore > 0f;
    }

    public static bool InvestigableSoundCanWakeAnIdlePawn()
    {
        UnitIntentUtilityContext context = new()
        {
            IsAlive = true,
            IsAlerted = true,
            HasInvestigableContact = true,
            HealthRatio = 0.9f,
            EnergyRatio = 1f,
            HydrationRatio = 1f,
            Alertness = 0.7f,
            Discipline = 0.5f,
            InvestigationUtility = 0.8f,
            DecisionSample = 0.5f
        };

        UnitIntentUtilityBreakdown decision = UnitIntentUtilityScorer.Select(
            in context,
            BehaviacUnitIntent.None,
            0f);

        return decision.SuggestedIntent == BehaviacUnitIntent.InvestigateContact &&
            decision.SuggestedScore > 0f &&
            decision.SuggestedScore < 0.9f;
    }

    public static bool SuppressedPawnCanChooseAvailableCover()
    {
        UnitIntentUtilityContext context = new()
        {
            IsAlive = true,
            HasVisibleTarget = true,
            HasCover = true,
            IsSuppressed = true,
            HealthRatio = 0.8f,
            EnergyRatio = 1f,
            HydrationRatio = 1f,
            ShootingSkill = 0.6f,
            SuppressionResistance = 0.5f,
            Caution = 0.85f,
            Aggression = 0.35f,
            Discipline = 0.7f,
            CombatUtility = 0.52f,
            CoverUtility = 0.9f
        };

        UnitIntentUtilityBreakdown decision = UnitIntentUtilityScorer.Select(
            in context,
            BehaviacUnitIntent.None,
            0f);

        return decision.SuggestedIntent == BehaviacUnitIntent.SeekCover &&
            decision.CoverScore > decision.EngageScore;
    }
}
