using behaviac;

namespace Core.AI.Behaviac;

public enum BehaviacUnitIntent : byte
{
    None = 0,
    Dead = 1,
    SeekCover = 2,
    RetreatToCover = 3,
    FlankTarget = 4,
    EngageTarget = 5,
    SuppressiveFire = 6,
    InvestigateLastKnownTarget = 7,
    Patrol = 8,
    ProneEngage = 9,
    FollowMoveOrder = 10,
    HoldPosition = 11,
    AttackOrder = 12,
    HoldPositionEngage = 13,
    InvestigateAudibleContact = 14,
    Panic = 16
}

/// <summary>
/// Facts-in / intent-out agent for the live tactical decision tree.
/// It selects policies but has no direct access to movement, combat or rendering systems.
/// </summary>
[TypeMetaInfo("RimClone Unit AI", "Tactical facts and actions exposed to Behaviac Designer")]
public sealed class BehaviacUnitDecisionAgent : Agent
{
    [MemberMetaInfo("Alive", "Whether the unit is alive.", true)]
    public bool IsAlive { get; private set; }
    [MemberMetaInfo("Suppressed", "Whether suppression or panic is active.", true)]
    public bool IsSuppressed { get; private set; }
    [MemberMetaInfo("Panicked", "Whether the unit is in a full panic state.", true)]
    public bool IsPanicked { get; private set; }
    [MemberMetaInfo("Has cover", "Whether a valid cover goal is available.", true)]
    public bool HasCover { get; private set; }
    public bool WasSeekingCover { get; private set; }
    public bool WasFollowingMoveOrder { get; private set; }
    [MemberMetaInfo("Visible target", "Whether a hostile target is currently visible.", true)]
    public bool HasVisibleTarget { get; private set; }
    [MemberMetaInfo("Recent target", "Whether the last known target memory is valid.", true)]
    public bool HasRecentTarget { get; private set; }
    [MemberMetaInfo("Alerted", "Threat awareness independent of suppression or panic.", true)]
    public bool IsAlerted { get; private set; }
    [MemberMetaInfo("Audible contact", "Whether an uncertain sound contact is worth investigating.", true)]
    public bool HasAudibleContact { get; private set; }
    [MemberMetaInfo("Immediate danger", "Whether a close bullet pass, stopped bullet or explosion was recently perceived.", true)]
    public bool IsInImmediateDanger { get; private set; }
    [MemberMetaInfo("Low health", "Whether the unit is below the retreat health threshold.", true)]
    public bool IsLowHealth { get; private set; }
    [MemberMetaInfo("Suppressive weapon", "Whether the current loadout supports suppressive fire.", true)]
    public bool HasSuppressiveWeapon { get; private set; }
    [MemberMetaInfo("Can flank", "Whether this unit has been assigned a flank opportunity.", true)]
    public bool CanFlank { get; private set; }
    [MemberMetaInfo("Is flanking", "Whether this unit is already executing a flank maneuver.", true)]
    public bool IsFlanking { get; private set; }

    [MemberMetaInfo("Already prone", "Whether the unit is currently lying down and can continue a crawl.", true)]
    public bool IsProne { get; private set; }

    [MemberMetaInfo("Prone combat utility", "How beneficial a low prone firing position is right now (0 to 1).", true)]
    public float ProneCombatUtility { get; private set; }

    [MemberMetaInfo("Selected intent", "The tactical intent returned by the behavior tree.", true)]
    public BehaviacUnitIntent Intent { get; private set; }

    [MemberMetaInfo("Has move order", "Whether the player assigned a movement task.", true)]
    public bool HasMoveOrder { get; private set; }
    [MemberMetaInfo("Has stop order", "Whether the player assigned a hold-position task.", true)]
    public bool HasStopOrder { get; private set; }
    [MemberMetaInfo("Has attack order", "Whether the player assigned an attack-target task.", true)]
    public bool HasAttackOrder { get; private set; }
    [MemberMetaInfo("Move-order utility", "Utility score for continuing the player's move task (0 to 1).", true)]
    public float MoveOrderUtility { get; private set; }
    [MemberMetaInfo("Combat utility", "Utility score for responding to current hostile contact (0 to 1).", true)]
    public float CombatUtility { get; private set; }
    [MemberMetaInfo("Cover utility", "Utility of the available cover action (0 to 1).", true)]
    public float CoverUtility { get; private set; }
    [MemberMetaInfo("Flank utility", "Utility of the available flank maneuver (0 to 1).", true)]
    public float FlankUtility { get; private set; }
    [MemberMetaInfo("Investigation utility", "Utility of investigating an uncertain sound contact (0 to 1).", true)]
    public float InvestigationUtility { get; private set; }
    public void SetFacts(
        bool isAlive,
        bool isSuppressed,
        bool hasCover,
        bool hasVisibleTarget,
        bool hasRecentTarget,
        bool isLowHealth,
        bool hasSuppressiveWeapon,
        bool canFlank,
        bool isFlanking,
        float proneCombatUtility = 0f,
        bool isProne = false,
        bool hasMoveOrder = false,
        bool hasStopOrder = false,
        bool hasAttackOrder = false,
        float moveOrderUtility = 0f,
        float combatUtility = 0f,
        bool hasAudibleContact = false,
        bool isInImmediateDanger = false,
        bool isAlerted = false,
        float coverUtility = 0f,
        float flankUtility = 0f,
        float investigationUtility = 0f,
        bool isPanicked = false,
        bool wasSeekingCover = false,
        bool wasFollowingMoveOrder = false)
    {
        IsAlive = isAlive;
        IsSuppressed = isSuppressed;
        IsPanicked = isPanicked;
        WasSeekingCover = wasSeekingCover;
        WasFollowingMoveOrder = wasFollowingMoveOrder;
        HasCover = hasCover;
        HasVisibleTarget = hasVisibleTarget;
        HasRecentTarget = hasRecentTarget;
        HasAudibleContact = hasAudibleContact;
        IsInImmediateDanger = isInImmediateDanger;
        IsAlerted = isAlerted;
        IsLowHealth = isLowHealth;
        HasSuppressiveWeapon = hasSuppressiveWeapon;
        CanFlank = canFlank;
        IsFlanking = isFlanking;
        IsProne = isProne;
        ProneCombatUtility = float.IsFinite(proneCombatUtility)
            ? System.Math.Clamp(proneCombatUtility, 0f, 1f)
            : 0f;
        HasMoveOrder = hasMoveOrder;
        HasStopOrder = hasStopOrder;
        HasAttackOrder = hasAttackOrder;
        MoveOrderUtility = float.IsFinite(moveOrderUtility)
            ? System.Math.Clamp(moveOrderUtility, 0f, 1f)
            : 0f;
        CombatUtility = float.IsFinite(combatUtility)
            ? System.Math.Clamp(combatUtility, 0f, 1f)
            : 0f;
        CoverUtility = float.IsFinite(coverUtility)
            ? System.Math.Clamp(coverUtility, 0f, 1f)
            : 0f;
        FlankUtility = float.IsFinite(flankUtility)
            ? System.Math.Clamp(flankUtility, 0f, 1f)
            : 0f;
        InvestigationUtility = float.IsFinite(investigationUtility)
            ? System.Math.Clamp(investigationUtility, 0f, 1f)
            : 0f;
        Intent = BehaviacUnitIntent.None;
    }

    [MethodMetaInfo("ShouldReturnDead", "Check whether the unit is dead.")]
    public bool ShouldReturnDead() => !IsAlive;

    [MethodMetaInfo("ShouldSeekCover", "Check whether suppression cover should take priority.")]
    public bool ShouldSeekCover() =>
        IsAlive && HasCover &&
        (IsPanicked || IsInImmediateDanger ||
         (IsSuppressed &&
          (WasSeekingCover
              ? CoverUtility + Core.Unit.UnitTacticalUtility.CoverContinuationHysteresis >= CombatUtility
              : CoverUtility > CombatUtility + Core.Unit.UnitTacticalUtility.CoverSwitchHysteresis)));

    [MethodMetaInfo("ShouldPanicFallback", "Interrupt combat when a panicked unit has no valid cover destination.")]
    public bool ShouldPanicFallback() =>
        IsAlive && IsPanicked && !HasCover;

    [MethodMetaInfo("ShouldExecuteAttackOrder", "Continue the attack task toward its last known contact only when no hostile is visible.")]
    public bool ShouldExecuteAttackOrder() =>
        IsAlive && HasAttackOrder && !HasVisibleTarget;

    [MethodMetaInfo("ShouldFollowMoveOrder", "Follow a move order unless a hostile is currently visible; the saved order resumes after contact is lost.")]
    public bool ShouldFollowMoveOrder() =>
        IsAlive && HasMoveOrder && !HasVisibleTarget &&
        (WasFollowingMoveOrder
            ? MoveOrderUtility + Core.Unit.UnitTacticalUtility.MoveOrderSwitchHysteresis >= CombatUtility
            : MoveOrderUtility >= CombatUtility + Core.Unit.UnitTacticalUtility.MoveOrderSwitchHysteresis);

    [MethodMetaInfo("ShouldHoldPosition", "Keep the assigned position when no hostile is visible; cover priorities remain above this branch.")]
    public bool ShouldHoldPosition() =>
        IsAlive && HasStopOrder && !HasVisibleTarget;

    [MethodMetaInfo("ShouldHoldPositionAndEngage", "Fire at a visible hostile without advancing unless a higher-priority cover action is selected.")]
    public bool ShouldHoldPositionAndEngage() =>
        IsAlive && HasStopOrder && HasVisibleTarget;

    [MethodMetaInfo("ShouldRetreatToCover", "Check whether low health requires cover; when holding, only interrupt the position if a cover goal is available.")]
    public bool ShouldRetreatToCover() =>
        IsAlive && IsLowHealth && (HasVisibleTarget || HasRecentTarget) &&
        (!HasStopOrder || HasCover);

    [MethodMetaInfo("ShouldTakePronePosition", "Use prone firing only at long range when the unit is highly exposed and has a strong tactical advantage.")]
    public bool ShouldTakePronePosition()
    {
        // Crawling is a deliberate long-range tactic, not a default combat posture.
        // Require a very high score to enter prone; use a lower threshold to exit cleanly.
        float proneThreshold = IsProne ? 0.90f : 0.96f;
        float combatMargin = IsProne ? 0f : 0.10f;

        return IsAlive && HasVisibleTarget && !IsSuppressed && !IsLowHealth &&
            !IsFlanking &&
            ProneCombatUtility >= proneThreshold &&
            ProneCombatUtility > CombatUtility + combatMargin &&
            (!CanFlank ||
             FlankUtility <= CombatUtility + Core.Unit.UnitTacticalUtility.FlankUtilityAdvantage);
    }

    [MethodMetaInfo("ShouldEngageVisibleTarget", "Check whether a visible target should be engaged.")]
    public bool ShouldEngageVisibleTarget() =>
        IsAlive && HasVisibleTarget;

    [MethodMetaInfo("ShouldSuppressLastKnownTarget", "Check whether a suppressed target should be fired at.")]
    public bool ShouldSuppressLastKnownTarget() =>
        IsAlive && !HasVisibleTarget && HasRecentTarget && HasSuppressiveWeapon;

    [MethodMetaInfo("ShouldFlankVisibleTarget", "Check whether the current target should be flanked.")]
    public bool ShouldFlankVisibleTarget() =>
        IsAlive && !IsSuppressed && HasVisibleTarget &&
        (IsFlanking ||
         (CanFlank &&
          FlankUtility > CombatUtility + Core.Unit.UnitTacticalUtility.FlankUtilityAdvantage));

    [MethodMetaInfo("ShouldInvestigateLastKnownTarget", "Check whether last known target position should be investigated.")]
    public bool ShouldInvestigateLastKnownTarget() =>
        IsAlive && HasRecentTarget;

    [MethodMetaInfo("ShouldInvestigateAudibleContact", "Investigate a credible sound only when no stronger tactical response is active.")]
    public bool ShouldInvestigateAudibleContact() =>
        IsAlive && IsAlerted && HasAudibleContact && !HasVisibleTarget && !HasRecentTarget &&
        !IsSuppressed && !IsLowHealth &&
        InvestigationUtility >= Core.Unit.UnitTacticalUtility.MinimumInvestigationUtility;

    [MethodMetaInfo("SelectDead", "Return the dead-unit intent.")]
    public EBTStatus SelectDead() =>
        SelectIntent(BehaviacUnitIntent.Dead);

    [MethodMetaInfo("SelectSeekCover", "Return the suppression-cover intent.")]
    public EBTStatus SelectSeekCover() =>
        SelectIntent(BehaviacUnitIntent.SeekCover);

    [MethodMetaInfo("SelectPanic", "Stop attacking and take a low profile when panic strikes and no cover is reachable.")]
    public EBTStatus SelectPanic() =>
        SelectIntent(BehaviacUnitIntent.Panic);

    [MethodMetaInfo("SelectMoveOrder", "Return the player movement-task intent.")]
    public EBTStatus SelectMoveOrder() =>
        SelectIntent(BehaviacUnitIntent.FollowMoveOrder);

    [MethodMetaInfo("SelectHoldPosition", "Return the hold-position intent.")]
    public EBTStatus SelectHoldPosition() =>
        SelectIntent(BehaviacUnitIntent.HoldPosition);

    [MethodMetaInfo("SelectAttackOrder", "Resume the attack task toward its last known position while no hostile is visible.")]
    public EBTStatus SelectAttackOrder() =>
        SelectIntent(BehaviacUnitIntent.AttackOrder);

    [MethodMetaInfo("SelectHoldPositionEngage", "Return the stationary combat intent.")]
    public EBTStatus SelectHoldPositionEngage() =>
        SelectIntent(BehaviacUnitIntent.HoldPositionEngage);

    [MethodMetaInfo("SelectRetreatToCover", "Return the low-health retreat intent.")]
    public EBTStatus SelectRetreatToCover() =>
        SelectIntent(BehaviacUnitIntent.RetreatToCover);

    [MethodMetaInfo("SelectFlankTarget", "Return the flank-maneuver intent.")]
    public EBTStatus SelectFlankTarget() =>
        SelectIntent(BehaviacUnitIntent.FlankTarget);

    [MethodMetaInfo("SelectProneEngage", "Return the prone firing intent selected by tactical utility.")]
    public EBTStatus SelectProneEngage() =>
        SelectIntent(BehaviacUnitIntent.ProneEngage);

    [MethodMetaInfo("SelectEngageTarget", "Return the visible-target combat intent.")]
    public EBTStatus SelectEngageTarget() =>
        SelectIntent(BehaviacUnitIntent.EngageTarget);

    [MethodMetaInfo("SelectSuppressiveFire", "Return the suppressive-fire intent.")]
    public EBTStatus SelectSuppressiveFire() =>
        SelectIntent(BehaviacUnitIntent.SuppressiveFire);

    [MethodMetaInfo("SelectInvestigateLastKnownTarget", "Return the investigate intent.")]
    public EBTStatus SelectInvestigateLastKnownTarget() =>
        SelectIntent(BehaviacUnitIntent.InvestigateLastKnownTarget);

    [MethodMetaInfo("SelectInvestigateAudibleContact", "Move to an uncertain sound area without treating it as an identified enemy.")]
    public EBTStatus SelectInvestigateAudibleContact() =>
        SelectIntent(BehaviacUnitIntent.InvestigateAudibleContact);

    [MethodMetaInfo("SelectPatrol", "Return the patrol intent.")]
    public EBTStatus SelectPatrol() =>
        SelectIntent(BehaviacUnitIntent.Patrol);

    private EBTStatus SelectIntent(BehaviacUnitIntent intent)
    {
        Intent = intent;
        return EBTStatus.BT_SUCCESS;
    }
}
