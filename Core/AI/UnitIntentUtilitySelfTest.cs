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
