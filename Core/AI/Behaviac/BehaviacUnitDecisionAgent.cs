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
    InvestigateContact = 14,
    InvestigateAudibleContact = InvestigateContact,
    InvestigateSquadContact = 15
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
    [MemberMetaInfo("Has cover", "Whether a valid cover goal is available.", true)]
    public bool HasCover { get; private set; }
    [MemberMetaInfo("Visible target", "Whether a hostile target is currently visible.", true)]
    public bool HasVisibleTarget { get; private set; }
    [MemberMetaInfo("Recent target", "Whether a visual enemy or last-known target exists in memory.", true)]
    public bool HasRecentTarget { get; private set; }
    [MemberMetaInfo("Active target memory", "Whether the active target slot still holds a usable last-known target aim context.", true)]
    public bool HasActiveTargetMemory { get; private set; }
    [MemberMetaInfo("Alerted", "Threat awareness independent of suppression or panic.", true)]
    public bool IsAlerted { get; private set; }
    [MemberMetaInfo("Audible contact", "Whether an uncertain sound contact is worth investigating.", true)]
    public bool HasInvestigableContact { get; private set; }
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
    [MemberMetaInfo("Last-known enemy utility", "Utility of investigating a remembered visual enemy contact (0 to 1).", true)]
    public float LastKnownTargetUtility { get; private set; }
    [MemberMetaInfo("Investigating context", "Whether the unit is already moving toward a remembered contact.", true)]
    public bool IsInvestigatingContext { get; private set; }
    [MemberMetaInfo("Squad contact", "Whether a delayed, uncertain location report was received from a squad mate.", true)]
    public bool HasSquadContact { get; private set; }
    [MemberMetaInfo("Squad contact utility", "Utility score for investigating the reported area (0 to 1).", true)]
    public float SquadContactUtility { get; private set; }

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
        bool hasInvestigableContact = false,
        bool isInImmediateDanger = false,
        bool isAlerted = false,
        float coverUtility = 0f,
        float flankUtility = 0f,
        float investigationUtility = 0f,
        bool hasSquadContact = false,
        float squadContactUtility = 0f,
        float lastKnownTargetUtility = 0f,
        bool isInvestigatingContext = false,
        bool hasActiveTargetMemory = false)
    {
        IsAlive = isAlive;
        IsSuppressed = isSuppressed;
        HasCover = hasCover;
        HasVisibleTarget = hasVisibleTarget;
        HasRecentTarget = hasRecentTarget;
        HasInvestigableContact = hasInvestigableContact;
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
        LastKnownTargetUtility = float.IsFinite(lastKnownTargetUtility)
            ? System.Math.Clamp(lastKnownTargetUtility, 0f, 1f)
            : 0f;
        IsInvestigatingContext = isInvestigatingContext;
        HasActiveTargetMemory = hasActiveTargetMemory;
        HasSquadContact = hasSquadContact;
        SquadContactUtility = float.IsFinite(squadContactUtility)
            ? System.Math.Clamp(squadContactUtility, 0f, 1f)
            : 0f;
        Intent = BehaviacUnitIntent.None;
    }

    [MethodMetaInfo("ShouldReturnDead", "Check whether the unit is dead.")]
    public bool ShouldReturnDead() => !IsAlive;

    [MethodMetaInfo("ShouldSeekCover", "Check whether suppression cover should take priority.")]
    public bool ShouldSeekCover() =>
        IsAlive && HasCover &&
        (IsInImmediateDanger || (IsSuppressed && CoverUtility > CombatUtility));

    [MethodMetaInfo("ShouldExecuteAttackOrder", "Continue the attack task toward its last known contact only when no hostile is visible.")]
    public bool ShouldExecuteAttackOrder() =>
        IsAlive && HasAttackOrder && !HasVisibleTarget;

    [MethodMetaInfo("ShouldFollowMoveOrder", "Compare movement-task utility with immediate combat utility.")]
    public bool ShouldFollowMoveOrder() =>
        IsAlive && HasMoveOrder && MoveOrderUtility >= CombatUtility;

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

    [MethodMetaInfo("ShouldTakePronePosition", "Prefer a prone firing position when distance and exposure justify it.")]
    public bool ShouldTakePronePosition() =>
        IsAlive && HasVisibleTarget && !IsSuppressed && !IsLowHealth &&
        !IsFlanking &&
        (IsProne ||
         (ProneCombatUtility >= 0.60f &&
          ProneCombatUtility > CombatUtility &&
          (!CanFlank ||
           FlankUtility <= CombatUtility + Core.Unit.UnitTacticalUtility.FlankUtilityAdvantage)));

    [MethodMetaInfo("ShouldEngageVisibleTarget", "Check whether a visible target should be engaged.")]
    public bool ShouldEngageVisibleTarget() =>
        IsAlive && HasVisibleTarget;

    [MethodMetaInfo("ShouldSuppressLastKnownTarget", "Check whether a suppressed target should be fired at.")]
    public bool ShouldSuppressLastKnownTarget() =>
        IsAlive && !HasVisibleTarget && HasActiveTargetMemory && HasSuppressiveWeapon &&
        (!HasInvestigableContact || LastKnownTargetUtility >= InvestigationUtility);

    [MethodMetaInfo("ShouldFlankVisibleTarget", "Check whether the current target should be flanked.")]
    public bool ShouldFlankVisibleTarget() =>
        IsAlive && !IsSuppressed && HasVisibleTarget &&
        (IsFlanking ||
         (CanFlank &&
          FlankUtility > CombatUtility + Core.Unit.UnitTacticalUtility.FlankUtilityAdvantage));

    [MethodMetaInfo("ShouldInvestigateLastKnownTarget", "Check whether last known target position should be investigated.")]
    public bool ShouldInvestigateLastKnownTarget() =>
        IsAlive && !IsInvestigatingContext && HasRecentTarget &&
        (!HasInvestigableContact || LastKnownTargetUtility >= InvestigationUtility);

    [MethodMetaInfo("ShouldInvestigateContact", "Investigate a credible perception contact only when no stronger tactical response is active.")]
    public bool ShouldInvestigateContact() =>
        IsAlive && !IsInvestigatingContext && IsAlerted && HasInvestigableContact && !HasVisibleTarget &&
        (!HasRecentTarget || InvestigationUtility > LastKnownTargetUtility) &&
        !IsSuppressed && !IsLowHealth &&
        InvestigationUtility >= Core.Unit.UnitTacticalUtility.MinimumInvestigationUtility;

    [MethodMetaInfo("ShouldInvestigateAudibleContact", "Compatibility alias for older trees.")]
    public bool ShouldInvestigateAudibleContact() => ShouldInvestigateContact();

    [MethodMetaInfo("ShouldInvestigateSquadContact", "Investigate a delayed and uncertain position reported by a squad mate, without treating it as an identified target.")]
    public bool ShouldInvestigateSquadContact() =>
        IsAlive && !IsInvestigatingContext && HasSquadContact && !HasVisibleTarget && !HasRecentTarget &&
        !IsSuppressed && !IsLowHealth &&
        !HasMoveOrder && !HasStopOrder && !HasAttackOrder &&
        SquadContactUtility >= Core.Unit.UnitTacticalUtility.MinimumSquadContactUtility;

    [MethodMetaInfo("SelectDead", "Return the dead-unit intent.")]
    public EBTStatus SelectDead() =>
        SelectIntent(BehaviacUnitIntent.Dead);

    [MethodMetaInfo("SelectSeekCover", "Return the suppression-cover intent.")]
    public EBTStatus SelectSeekCover() =>
        SelectIntent(BehaviacUnitIntent.SeekCover);

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

    [MethodMetaInfo("SelectInvestigateContact", "Move to an uncertain sensed area without treating it as an identified enemy.")]
    public EBTStatus SelectInvestigateContact() =>
        SelectIntent(BehaviacUnitIntent.InvestigateContact);

    [MethodMetaInfo("SelectInvestigateAudibleContact", "Compatibility alias for older trees.")]
    public EBTStatus SelectInvestigateAudibleContact() => SelectInvestigateContact();

    [MethodMetaInfo("SelectInvestigateSquadContact", "Move to a squad-reported area without identifying or firing at a hidden unit.")]
    public EBTStatus SelectInvestigateSquadContact() =>
        SelectIntent(BehaviacUnitIntent.InvestigateSquadContact);

    [MethodMetaInfo("SelectPatrol", "Return the patrol intent.")]
    public EBTStatus SelectPatrol() =>
        SelectIntent(BehaviacUnitIntent.Patrol);

    private EBTStatus SelectIntent(BehaviacUnitIntent intent)
    {
        Intent = intent;
        return EBTStatus.BT_SUCCESS;
    }
}
