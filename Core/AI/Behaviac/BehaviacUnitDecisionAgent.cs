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
    Patrol = 8
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
    [MemberMetaInfo("Recent target", "Whether the last known target memory is valid.", true)]
    public bool HasRecentTarget { get; private set; }
    [MemberMetaInfo("Low health", "Whether the unit is below the retreat health threshold.", true)]
    public bool IsLowHealth { get; private set; }
    [MemberMetaInfo("Suppressive weapon", "Whether the current loadout supports suppressive fire.", true)]
    public bool HasSuppressiveWeapon { get; private set; }
    [MemberMetaInfo("Can flank", "Whether this unit has been assigned a flank opportunity.", true)]
    public bool CanFlank { get; private set; }
    [MemberMetaInfo("Is flanking", "Whether this unit is already executing a flank maneuver.", true)]
    public bool IsFlanking { get; private set; }

    [MemberMetaInfo("Selected intent", "The tactical intent returned by the behavior tree.", true)]
    public BehaviacUnitIntent Intent { get; private set; }

    public void SetFacts(
        bool isAlive,
        bool isSuppressed,
        bool hasCover,
        bool hasVisibleTarget,
        bool hasRecentTarget,
        bool isLowHealth,
        bool hasSuppressiveWeapon,
        bool canFlank,
        bool isFlanking)
    {
        IsAlive = isAlive;
        IsSuppressed = isSuppressed;
        HasCover = hasCover;
        HasVisibleTarget = hasVisibleTarget;
        HasRecentTarget = hasRecentTarget;
        IsLowHealth = isLowHealth;
        HasSuppressiveWeapon = hasSuppressiveWeapon;
        CanFlank = canFlank;
        IsFlanking = isFlanking;
        Intent = BehaviacUnitIntent.None;
    }

    [MethodMetaInfo("ShouldReturnDead", "Check whether the unit is dead.")]
    public bool ShouldReturnDead() => !IsAlive;

    [MethodMetaInfo("ShouldSeekCover", "Check whether suppression cover should take priority.")]
    public bool ShouldSeekCover() =>
        IsAlive && IsSuppressed && HasCover;

    [MethodMetaInfo("ShouldRetreatToCover", "Check whether low health requires retreat.")]
    public bool ShouldRetreatToCover() =>
        IsAlive && IsLowHealth && (HasVisibleTarget || HasRecentTarget);

    [MethodMetaInfo("ShouldEngageVisibleTarget", "Check whether a visible target should be engaged.")]
    public bool ShouldEngageVisibleTarget() =>
        IsAlive && HasVisibleTarget;

    [MethodMetaInfo("ShouldSuppressLastKnownTarget", "Check whether a suppressed target should be fired at.")]
    public bool ShouldSuppressLastKnownTarget() =>
        IsAlive && !HasVisibleTarget && HasRecentTarget && HasSuppressiveWeapon;

    [MethodMetaInfo("ShouldFlankVisibleTarget", "Check whether the current target should be flanked.")]
    public bool ShouldFlankVisibleTarget() =>
        IsAlive && !IsSuppressed && HasVisibleTarget && (CanFlank || IsFlanking);

    [MethodMetaInfo("ShouldInvestigateLastKnownTarget", "Check whether last known target position should be investigated.")]
    public bool ShouldInvestigateLastKnownTarget() =>
        IsAlive && HasRecentTarget;

    [MethodMetaInfo("SelectDead", "Return the dead-unit intent.")]
    public EBTStatus SelectDead() =>
        SelectIntent(BehaviacUnitIntent.Dead);

    [MethodMetaInfo("SelectSeekCover", "Return the suppression-cover intent.")]
    public EBTStatus SelectSeekCover() =>
        SelectIntent(BehaviacUnitIntent.SeekCover);

    [MethodMetaInfo("SelectRetreatToCover", "Return the low-health retreat intent.")]
    public EBTStatus SelectRetreatToCover() =>
        SelectIntent(BehaviacUnitIntent.RetreatToCover);

    [MethodMetaInfo("SelectFlankTarget", "Return the flank-maneuver intent.")]
    public EBTStatus SelectFlankTarget() =>
        SelectIntent(BehaviacUnitIntent.FlankTarget);

    [MethodMetaInfo("SelectEngageTarget", "Return the visible-target combat intent.")]
    public EBTStatus SelectEngageTarget() =>
        SelectIntent(BehaviacUnitIntent.EngageTarget);

    [MethodMetaInfo("SelectSuppressiveFire", "Return the suppressive-fire intent.")]
    public EBTStatus SelectSuppressiveFire() =>
        SelectIntent(BehaviacUnitIntent.SuppressiveFire);

    [MethodMetaInfo("SelectInvestigateLastKnownTarget", "Return the investigate intent.")]
    public EBTStatus SelectInvestigateLastKnownTarget() =>
        SelectIntent(BehaviacUnitIntent.InvestigateLastKnownTarget);

    [MethodMetaInfo("SelectPatrol", "Return the patrol intent.")]
    public EBTStatus SelectPatrol() =>
        SelectIntent(BehaviacUnitIntent.Patrol);

    private EBTStatus SelectIntent(BehaviacUnitIntent intent)
    {
        Intent = intent;
        return EBTStatus.BT_SUCCESS;
    }
}
