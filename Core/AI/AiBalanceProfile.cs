namespace Core.Unit;

/// <summary>Immutable, trainable Utility AI balance. Defaults retain current formulas.</summary>
public sealed record AiBalanceProfile
{
    public static AiBalanceProfile Default { get; } = new();

    public float MinimumCandidateScore { get; init; } = 0.001f;
    public float MinimumInvestigationUtility { get; init; } = 0.24f;
    public float MinimumSquadContactUtility { get; init; } = 0.30f;
    public float RegularSwitchMargin { get; init; } = 0.10f;
    public float CommittedSwitchMargin { get; init; } = 0.23f;
    public float RegularSwitchDisciplinePenalty { get; init; } = 0.055f;
    public float CommittedSwitchDisciplineRelief { get; init; } = 0.035f;
    public float CommitDurationBaseSeconds { get; init; } = 0.35f;
    public float CommitDurationDisciplineSeconds { get; init; } = 0.55f;
    public float FlankUtilityAdvantage { get; init; } = 0.08f;

    // Autonomous preference weights; player orders and emergency floors are protected.
    public float EngageScale { get; init; } = 1f;
    public float FlankScale { get; init; } = 1f;
    public float CoverScale { get; init; } = 1f;
    public float RetreatScale { get; init; } = 1f;
    public float ProneScale { get; init; } = 1f;
    public float SuppressiveFireScale { get; init; } = 1f;
    public float LastKnownScale { get; init; } = 1f;
    public float InvestigationScale { get; init; } = 1f;
    public float SquadContactScale { get; init; } = 1f;
    public float PatrolScale { get; init; } = 1f;
    public float ProneAdoptionMinimumUtility { get; init; } = 0.60f;
    public float ProneContinuationUtility { get; init; } = 0.62f;

    public float MoveDistanceForMaximumUtility { get; init; } = 24f;
    public float MoveOrderBaseUtility { get; init; } = 0.50f;
    public float MoveOrderDistanceBonus { get; init; } = 0.30f;
    public float SuppressionMovePenalty { get; init; } = 0.12f;
    public float LowHealthMovePenalty { get; init; } = 0.10f;
    public float VisibleCombatCloseDistance { get; init; } = 6f;
    public float VisibleCombatMediumDistance { get; init; } = 12f;
    public float VisibleCombatFarDistance { get; init; } = 24f;
    public float VisibleCombatCloseScore { get; init; } = 0.96f;
    public float VisibleCombatMediumScore { get; init; } = 0.84f;
    public float VisibleCombatFarScore { get; init; } = 0.68f;
    public float VisibleCombatLongScore { get; init; } = 0.54f;

    public float CoverSuppressedThreshold { get; init; } = 1.8f;
    public float CoverPanickedThreshold { get; init; } = 5.8f;
    public float TacticalCoverBase { get; init; } = 0.18f;
    public float TacticalCoverPressureWeight { get; init; } = 0.66f;
    public float TacticalCoverLowHealthBonus { get; init; } = 0.16f;
    public float TacticalFlankBase { get; init; } = 0.32f;
    public float TacticalFlankRangeDeficitWeight { get; init; } = 0.18f;
    public float TacticalFlankDistanceStart { get; init; } = 24f;
    public float TacticalFlankDistanceSpan { get; init; } = 36f;
    public float TacticalFlankDistanceWeight { get; init; } = 0.22f;

    public float InvestigationDistanceForMaxPenalty { get; init; } = 64f;
    public float InvestigationDistancePenalty { get; init; } = 0.35f;
    public float TacticalInvestigationBase { get; init; } = 0.25f;
    public float TacticalInvestigationConfidenceWeight { get; init; } = 0.75f;
    public float InvestigationUncertaintyMaxRadius { get; init; } = 48f;
    public float InvestigationUncertaintyPenalty { get; init; } = 0.20f;

    public float ProneDistanceStart { get; init; } = 9f;
    public float ProneDistanceSpan { get; init; } = 25f;
    public float ProneDistanceWeight { get; init; } = 0.65f;
    public float ProneExposureWeight { get; init; } = 0.25f;
    public float ProneHealthWeight { get; init; } = 0.10f;

    // Low-level tactical affordances: legal positions/distances, not abstract actions.
    public float RangedEngagementRangeFraction { get; init; } = 0.84f;
    public float RangedRangeToleranceMinimum { get; init; } = 1f;
    public float RangedRangeToleranceFraction { get; init; } = 0.03f;
    public float MeleeEngagementRangeFraction { get; init; } = 0.94f;
    public float MeleeMinimumEngagementDistance { get; init; } = 0.20f;
    public float CoverSearchRadius { get; init; } = 8f;
    public float CoverThreatSeparationWeight { get; init; } = 0.12f;
    public float CoverTravelCostWeight { get; init; } = 0.16f;
    public float FlankNearBackoffDistance { get; init; } = 4f;
    public float FlankNearLateralDistance { get; init; } = 6f;
    public float FlankFarBackoffDistance { get; init; } = 6f;
    public float FlankFarLateralDistance { get; init; } = 8f;
    public float FlankNearRangeFraction { get; init; } = 0.90f;
    public float FlankFarRangeFraction { get; init; } = 0.98f;
    public float FlankBackoffProportion { get; init; } = 0.80f;
    public float FlankLateralProportion { get; init; } = 0.60f;
    public float FlankRangeErrorWeight { get; init; } = 0.08f;
    public float FlankTravelCostWeight { get; init; } = 0.012f;
    public float FlankLineOfSightBonus { get; init; } = 0.90f;
}
