using System;
using Core.AI.Behaviac;

namespace Core.Unit;

public static class UnitIntentUtilityScorer
{
    private const float MinimumCandidateScore = 0.001f;
    private const float MinimumInvestigationScore = 0.24f;
    private const float MinimumSquadContactScore = 0.30f;
    private const float RegularSwitchMargin = 0.10f;
    private const float CommittedSwitchMargin = 0.23f;

    public static UnitIntentUtilityBreakdown Select(
        in UnitIntentUtilityContext context,
        BehaviacUnitIntent previousIntent,
        float commitRemainingSeconds)
    {
        float caution = Clamp01(context.Caution);
        float aggression = Clamp01(context.Aggression);
        float discipline = Clamp01(context.Discipline);
        float health = Clamp01(context.HealthRatio);
        float pain = Clamp01(context.Pain);
        float fatigue = Clamp01(context.Fatigue);
        float stress = Clamp01(context.Stress);
        float energyDeficit = 1f - Clamp01(context.EnergyRatio);
        float hydrationDeficit = 1f - Clamp01(context.HydrationRatio);
        float shooting = Clamp01(context.ShootingSkill);
        float melee = Clamp01(context.MeleeSkill);
        float resistance = Clamp01(context.SuppressionResistance);
        float bodyBurden = Clamp01(
            pain * 0.32f +
            fatigue * 0.24f +
            stress * 0.18f +
            energyDeficit * 0.16f +
            hydrationDeficit * 0.10f);

        if (!context.IsAlive)
        {
            return new UnitIntentUtilityBreakdown
            {
                SuggestedIntent = BehaviacUnitIntent.Dead,
                SuggestedScore = 1f,
                PreviousIntent = previousIntent,
                PreviousScore = 0f,
                Reason = UnitIntentDecisionReason.Dead
            };
        }

        float engage = 0f;
        float holdAndEngage = 0f;
        float cover = 0f;
        float retreat = 0f;
        float moveOrder = 0f;
        float holdOrder = 0f;
        float attackOrder = 0f;
        float flank = 0f;
        float prone = 0f;
        float suppressiveFire = 0f;
        float lastKnown = 0f;
        float investigation = 0f;
        float squad = 0f;

        bool hasMoveOrder = context.OrderType == UnitAiOrderType.Move;
        bool hasStopOrder = context.OrderType == UnitAiOrderType.Stop;
        bool hasAttackOrder = context.OrderType == UnitAiOrderType.Attack;
        bool hasAnyPlayerOrder = context.OrderType != UnitAiOrderType.None;

        if (context.HasVisibleTarget)
        {
            float tacticalSkill = context.HasRangedWeapon ? shooting : melee;
            engage = Clamp01(
                0.36f +
                Clamp01(context.CombatUtility) * 0.43f +
                aggression * 0.09f +
                tacticalSkill * 0.06f +
                resistance * 0.04f -
                caution * 0.045f -
                pain * 0.07f -
                fatigue * 0.045f -
                stress * 0.035f);

            if (hasStopOrder)
            {
                holdAndEngage = Clamp01(
                    0.73f +
                    Clamp01(context.CombatUtility) * 0.12f +
                    discipline * 0.09f -
                    bodyBurden * 0.05f);
                engage = 0f;
            }

            if ((context.CanFlank || context.IsFlanking) &&
                !context.IsSuppressed && !context.IsPanicked && !hasStopOrder)
            {
                // Preserve a committed flank while its target remains visible;
                // CanFlank is intentionally false after the maneuver starts.
                flank = context.IsFlanking
                    ? Clamp01(0.63f + aggression * 0.04f - caution * 0.04f - bodyBurden * 0.05f)
                    : Clamp01(
                        context.FlankUtility +
                        aggression * 0.08f -
                        caution * 0.07f -
                        fatigue * 0.045f -
                        bodyBurden * 0.035f);
            }

            bool canContinueProne = context.IsProne &&
                !context.IsSuppressed && !context.IsPanicked && !context.IsLowHealth &&
                !context.IsFlanking &&
                (!context.CanFlank ||
                 context.FlankUtility <= context.CombatUtility + UnitTacticalUtility.FlankUtilityAdvantage);
            bool canAdoptProne = context.ProneCombatUtility >= 0.60f &&
                context.ProneCombatUtility > context.CombatUtility &&
                !context.IsSuppressed && !context.IsPanicked && !context.IsLowHealth &&
                !context.IsFlanking &&
                (!context.CanFlank ||
                 context.FlankUtility <= context.CombatUtility + UnitTacticalUtility.FlankUtilityAdvantage);

            if (canContinueProne || canAdoptProne)
            {
                prone = Clamp01(
                    MathF.Max(context.ProneCombatUtility, canContinueProne ? 0.62f : 0f) +
                    caution * 0.035f +
                    shooting * 0.025f -
                    bodyBurden * 0.025f);
                if (hasStopOrder)
                    prone = 0f;
            }
        }

        if (context.HasCover &&
            (context.IsInImmediateDanger ||
             context.IsPanicked ||
             (context.IsSuppressed && context.CoverUtility > context.CombatUtility)))
        {
            cover = Clamp01(
                0.57f +
                Clamp01(context.CoverUtility) * 0.22f +
                caution * 0.09f +
                pain * 0.045f +
                bodyBurden * 0.035f);

            if (context.IsInImmediateDanger || context.IsPanicked)
                cover = MathF.Max(cover, 0.96f);
        }

        if (context.IsLowHealth &&
            (context.HasVisibleTarget || context.HasRecentTarget) &&
            (!hasStopOrder || context.HasCover))
        {
            retreat = Clamp01(
                0.60f +
                (1f - health) * 0.19f +
                pain * 0.10f +
                caution * 0.075f +
                (context.HasCover ? 0.04f : 0f) -
                aggression * 0.035f);
        }

        if (hasMoveOrder)
        {
            moveOrder = Clamp01(
                context.MoveOrderUtility +
                discipline * 0.055f -
                bodyBurden * 0.045f);
        }

        if (hasStopOrder && !context.HasVisibleTarget)
            holdOrder = Clamp01(0.72f + discipline * 0.14f);

        if (hasAttackOrder && !context.HasVisibleTarget)
        {
            attackOrder = Clamp01(
                0.68f +
                MathF.Min(0.24f, Clamp01(context.LastKnownTargetUtility) * 0.12f) +
                aggression * 0.025f);
        }

        if (!context.HasVisibleTarget &&
            !hasAnyPlayerOrder &&
            context.HasActiveTargetMemory &&
            context.HasSuppressiveWeapon &&
            (!context.HasInvestigableContact ||
             context.LastKnownTargetUtility >= context.InvestigationUtility))
        {
            suppressiveFire = Clamp01(
                0.20f +
                Clamp01(context.LastKnownTargetUtility) * 0.62f +
                aggression * 0.075f +
                shooting * 0.035f -
                bodyBurden * 0.045f -
                (context.IsSuppressed ? 0.14f : 0f) -
                (context.IsPanicked ? 0.20f : 0f) -
                (context.IsLowHealth ? 0.10f : 0f));
        }

        bool canInvestigate = !context.HasVisibleTarget &&
            !context.IsInvestigatingContext &&
            !hasAnyPlayerOrder;

        if (canInvestigate && context.HasRecentTarget)
        {
            lastKnown = Clamp01(
                0.20f +
                Clamp01(context.LastKnownTargetUtility) * 0.64f +
                aggression * 0.035f +
                caution * 0.015f -
                bodyBurden * 0.035f -
                (context.IsSuppressed ? 0.16f : 0f) -
                (context.IsPanicked ? 0.22f : 0f) -
                (context.IsLowHealth ? 0.14f : 0f));
        }

        if (canInvestigate &&
            context.IsAlerted &&
            context.HasInvestigableContact &&
            !context.IsSuppressed &&
            !context.IsPanicked &&
            !context.IsLowHealth &&
            context.InvestigationUtility >= MinimumInvestigationScore &&
            (!context.HasRecentTarget ||
             context.InvestigationUtility > context.LastKnownTargetUtility))
        {
            investigation = Clamp01(
                0.18f +
                Clamp01(context.InvestigationUtility) * 0.64f +
                Clamp01(context.Alertness) * 0.11f +
                discipline * 0.025f -
                caution * 0.035f -
                fatigue * 0.035f);
        }

        if (canInvestigate &&
            context.HasSquadContact &&
            !context.HasRecentTarget &&
            !context.IsSuppressed &&
            !context.IsPanicked &&
            !context.IsLowHealth &&
            context.SquadContactUtility >= MinimumSquadContactScore)
        {
            squad = Clamp01(
                0.16f +
                Clamp01(context.SquadContactUtility) * 0.62f +
                discipline * 0.075f -
                caution * 0.025f -
                bodyBurden * 0.035f);
        }

        float patrol = hasAnyPlayerOrder ? 0f : Clamp01(0.12f + discipline * 0.025f);

        // Continuing an investigation is a valid incumbent, not a fresh target request.
        if (context.IsInvestigatingContext &&
            IsInvestigationIntent(previousIntent) &&
            !context.HasVisibleTarget &&
            !context.IsInImmediateDanger &&
            !context.IsSuppressed &&
            !context.IsPanicked &&
            !context.IsLowHealth &&
            !hasAnyPlayerOrder)
        {
            float continuationScore = 0.61f + discipline * 0.035f;
            switch (previousIntent)
            {
                case BehaviacUnitIntent.InvestigateLastKnownTarget:
                    lastKnown = continuationScore;
                    break;
                case BehaviacUnitIntent.InvestigateContact:
                    investigation = continuationScore;
                    break;
                case BehaviacUnitIntent.InvestigateSquadContact:
                    squad = continuationScore;
                    break;
            }
        }

        var scores = new UnitIntentUtilityBreakdown
        {
            PreviousIntent = previousIntent,
            EngageScore = engage,
            HoldAndEngageScore = holdAndEngage,
            CoverScore = cover,
            RetreatScore = retreat,
            MoveOrderScore = moveOrder,
            HoldOrderScore = holdOrder,
            AttackOrderScore = attackOrder,
            FlankScore = flank,
            ProneScore = prone,
            SuppressiveFireScore = suppressiveFire,
            LastKnownContactScore = lastKnown,
            InvestigableContactScore = investigation,
            SquadContactScore = squad,
            PatrolScore = patrol
        };

        BehaviacUnitIntent bestIntent = BehaviacUnitIntent.Patrol;
        float bestScore = patrol;
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.EngageTarget, engage);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.HoldPositionEngage, holdAndEngage);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.SeekCover, cover);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.RetreatToCover, retreat);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.FollowMoveOrder, moveOrder);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.HoldPosition, holdOrder);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.AttackOrder, attackOrder);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.FlankTarget, flank);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.ProneEngage, prone);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.SuppressiveFire, suppressiveFire);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.InvestigateLastKnownTarget, lastKnown);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.InvestigateContact, investigation);
        Consider(ref bestIntent, ref bestScore, BehaviacUnitIntent.InvestigateSquadContact, squad);

        float previousScore = ScoreFor(previousIntent, scores);
        UnitIntentDecisionReason reason = previousIntent == BehaviacUnitIntent.None
            ? UnitIntentDecisionReason.InitialSelection
            : UnitIntentDecisionReason.HighestUtility;

        if (bestIntent != previousIntent &&
            previousIntent != BehaviacUnitIntent.None &&
            previousScore >= MinimumCandidateScore)
        {
            bool emergency = IsEmergencyIntent(bestIntent, context);
            float margin = emergency
                ? 0f
                : commitRemainingSeconds > 0f
                    ? CommittedSwitchMargin - discipline * 0.035f
                    : RegularSwitchMargin + discipline * 0.055f;

            if (bestScore < previousScore + margin)
            {
                bestIntent = previousIntent;
                bestScore = previousScore;
                reason = UnitIntentDecisionReason.RetainedPreviousIntent;
            }
            else if (emergency)
            {
                reason = UnitIntentDecisionReason.EmergencyOverride;
            }
        }
        else if (bestIntent != previousIntent &&
                 previousIntent != BehaviacUnitIntent.None &&
                 previousScore < MinimumCandidateScore)
        {
            reason = UnitIntentDecisionReason.PreviousIntentNoLongerValid;
        }

        return scores with
        {
            SuggestedIntent = bestIntent,
            SuggestedScore = bestScore,
            PreviousScore = previousScore,
            Reason = reason,
            RetainedPreviousIntent = reason == UnitIntentDecisionReason.RetainedPreviousIntent
        };
    }

    public static float GetCommitDuration(float discipline) =>
        0.35f + Clamp01(discipline) * 0.55f;

    private static void Consider(
        ref BehaviacUnitIntent bestIntent,
        ref float bestScore,
        BehaviacUnitIntent candidate,
        float score)
    {
        if (score > bestScore)
        {
            bestIntent = candidate;
            bestScore = score;
        }
    }

    private static float ScoreFor(
        BehaviacUnitIntent intent,
        in UnitIntentUtilityBreakdown scores) =>
        intent switch
        {
            BehaviacUnitIntent.Dead => 0f,
            BehaviacUnitIntent.EngageTarget => scores.EngageScore,
            BehaviacUnitIntent.HoldPositionEngage => scores.HoldAndEngageScore,
            BehaviacUnitIntent.SeekCover => scores.CoverScore,
            BehaviacUnitIntent.RetreatToCover => scores.RetreatScore,
            BehaviacUnitIntent.FollowMoveOrder => scores.MoveOrderScore,
            BehaviacUnitIntent.HoldPosition => scores.HoldOrderScore,
            BehaviacUnitIntent.AttackOrder => scores.AttackOrderScore,
            BehaviacUnitIntent.FlankTarget => scores.FlankScore,
            BehaviacUnitIntent.ProneEngage => scores.ProneScore,
            BehaviacUnitIntent.SuppressiveFire => scores.SuppressiveFireScore,
            BehaviacUnitIntent.InvestigateLastKnownTarget => scores.LastKnownContactScore,
            BehaviacUnitIntent.InvestigateContact => scores.InvestigableContactScore,
            BehaviacUnitIntent.InvestigateSquadContact => scores.SquadContactScore,
            BehaviacUnitIntent.Patrol => scores.PatrolScore,
            _ => 0f
        };

    private static bool IsEmergencyIntent(
        BehaviacUnitIntent intent,
        in UnitIntentUtilityContext context) =>
        intent == BehaviacUnitIntent.Dead ||
        (intent == BehaviacUnitIntent.RetreatToCover && context.IsLowHealth) ||
        ((intent is BehaviacUnitIntent.SeekCover or BehaviacUnitIntent.RetreatToCover) &&
         (context.IsInImmediateDanger || context.IsPanicked));

    private static bool IsInvestigationIntent(BehaviacUnitIntent intent) =>
        intent is BehaviacUnitIntent.InvestigateLastKnownTarget or
            BehaviacUnitIntent.InvestigateContact or
            BehaviacUnitIntent.InvestigateSquadContact;

    private static float Clamp01(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
