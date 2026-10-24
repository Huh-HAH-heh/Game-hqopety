using System;

namespace Core.Unit;

/// <summary>Pure tactical utility equations driven by a trainable balance profile.</summary>
public static class UnitTacticalUtility
{
    public const float FlankUtilityAdvantage = 0.08f;
    public const float MinimumInvestigationUtility = 0.24f;
    public const float MinimumSquadContactUtility = 0.30f;

    public static float EvaluateMoveOrder(float distance,bool suppressedOrPanicked,bool lowHealth,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!float.IsFinite(distance)) distance=0f;
        float u=b.MoveOrderBaseUtility+b.MoveOrderDistanceBonus*Math.Clamp(distance/b.MoveDistanceForMaximumUtility,0f,1f);
        if(suppressedOrPanicked) u-=b.SuppressionMovePenalty;
        if(lowHealth) u-=b.LowHealthMovePenalty;
        return MathF.Max(0f,u);
    }
    public static float EvaluateVisibleCombat(float distance,bool hasVisibleTarget,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!hasVisibleTarget||!float.IsFinite(distance)) return 0f;
        if(distance<=b.VisibleCombatCloseDistance) return b.VisibleCombatCloseScore;
        if(distance<=b.VisibleCombatMediumDistance) return b.VisibleCombatMediumScore;
        if(distance<=b.VisibleCombatFarDistance) return b.VisibleCombatFarScore;
        return b.VisibleCombatLongScore;
    }
    public static float EvaluateCover(float suppression,bool hasCover,bool immediateDanger,bool panicked,bool lowHealth,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!hasCover) return 0f;
        if(immediateDanger||panicked) return 1f;
        if(!float.IsFinite(suppression)) suppression=0f;
        float p=Math.Clamp((suppression-b.CoverSuppressedThreshold)/(b.CoverPanickedThreshold-b.CoverSuppressedThreshold),0f,1f);
        return Math.Clamp(b.TacticalCoverBase+p*b.TacticalCoverPressureWeight+(lowHealth?b.TacticalCoverLowHealthBonus:0f),0f,1f);
    }
    public static float EvaluateFlanking(float distance,float effectiveRange,bool feasible,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!feasible||!float.IsFinite(distance)||!float.IsFinite(effectiveRange)||distance<0f||effectiveRange<=0f) return 0f;
        float deficit=Math.Clamp((distance-effectiveRange)/MathF.Max(effectiveRange,1f),0f,1f);
        float advantage=Math.Clamp((distance-b.TacticalFlankDistanceStart)/b.TacticalFlankDistanceSpan,0f,1f);
        return Math.Clamp(b.TacticalFlankBase+deficit*b.TacticalFlankRangeDeficitWeight+advantage*b.TacticalFlankDistanceWeight,0f,1f);
    }
    public static float EvaluateInvestigation(float confidence,float distance,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!float.IsFinite(confidence)||!float.IsFinite(distance)||distance<0f) return 0f;
        float penalty=Math.Clamp(distance/b.InvestigationDistanceForMaxPenalty,0f,1f)*b.InvestigationDistancePenalty;
        return Math.Clamp(b.TacticalInvestigationBase+Math.Clamp(confidence,0f,1f)*b.TacticalInvestigationConfidenceWeight-penalty,0f,1f);
    }
    public static float EvaluateInvestigation(float confidence,float distance,float positionErrorRadius,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!float.IsFinite(positionErrorRadius)||positionErrorRadius<0f) return 0f;
        float penalty=Math.Clamp(positionErrorRadius/b.InvestigationUncertaintyMaxRadius,0f,1f)*b.InvestigationUncertaintyPenalty;
        return Math.Clamp(EvaluateInvestigation(confidence,distance,b)-penalty,0f,1f);
    }
    public static float EvaluateProneFiring(float distanceToTarget,bool exposed,float healthRatio,AiBalanceProfile? balanceProfile=null)
    {
        AiBalanceProfile b=balanceProfile??AiBalanceProfile.Default;
        if(!float.IsFinite(distanceToTarget)) distanceToTarget=0f;
        if(!float.IsFinite(healthRatio)) healthRatio=0f;
        float d=Math.Clamp((distanceToTarget-b.ProneDistanceStart)/b.ProneDistanceSpan,0f,1f);
        float e=exposed?1f:0.2f;
        return Math.Clamp(d*b.ProneDistanceWeight+e*b.ProneExposureWeight+Math.Clamp(healthRatio,0f,1f)*b.ProneHealthWeight,0f,1f);
    }
}
