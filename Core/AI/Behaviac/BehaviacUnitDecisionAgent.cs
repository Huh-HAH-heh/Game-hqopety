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
[TypeMetaInfo()]
public sealed class BehaviacUnitDecisionAgent : Agent
{
    public bool IsAlive { get; private set; }
    public bool IsSuppressed { get; private set; }
    public bool HasCover { get; private set; }
    public bool HasVisibleTarget { get; private set; }
    public bool HasRecentTarget { get; private set; }
    public bool IsLowHealth { get; private set; }
    public bool HasSuppressiveWeapon { get; private set; }
    public bool CanFlank { get; private set; }
    public bool IsFlanking { get; private set; }

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

    public bool ShouldReturnDead() => !IsAlive;

    public bool ShouldSeekCover() =>
        IsAlive && IsSuppressed && HasCover;

    public bool ShouldRetreatToCover() =>
        IsAlive && IsLowHealth && (HasVisibleTarget || HasRecentTarget);

    public bool ShouldEngageVisibleTarget() =>
        IsAlive && HasVisibleTarget;

    public bool ShouldSuppressLastKnownTarget() =>
        IsAlive && !HasVisibleTarget && HasRecentTarget && HasSuppressiveWeapon;

    public bool ShouldFlankVisibleTarget() =>
        IsAlive && !IsSuppressed && HasVisibleTarget && (CanFlank || IsFlanking);

    public bool ShouldInvestigateLastKnownTarget() =>
        IsAlive && HasRecentTarget;

    public EBTStatus SelectIntent(BehaviacUnitIntent intent)
    {
        Intent = intent;
        return EBTStatus.BT_SUCCESS;
    }
}
