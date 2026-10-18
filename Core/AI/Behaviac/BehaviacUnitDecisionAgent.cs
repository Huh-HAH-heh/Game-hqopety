using behaviac;

namespace Core.AI.Behaviac;

public enum BehaviacUnitIntent : byte
{
    None = 0,
    Dead = 1,
    SeekCover = 2,
    EngageTarget = 3,
    InvestigateLastKnownTarget = 4,
    Patrol = 5
}

/// <summary>
/// Minimal facts-in / intent-out agent for the first Behaviac integration slice.
/// It has no access to movement, combat or rendering systems.
/// </summary>
[TypeMetaInfo()]
public sealed class BehaviacUnitDecisionAgent : Agent
{
    public bool IsAlive { get; private set; }
    public bool IsSuppressed { get; private set; }
    public bool HasCover { get; private set; }
    public bool HasVisibleTarget { get; private set; }
    public bool HasRecentTarget { get; private set; }

    public BehaviacUnitIntent Intent { get; private set; }

    public void SetFacts(
        bool isAlive,
        bool isSuppressed,
        bool hasCover,
        bool hasVisibleTarget,
        bool hasRecentTarget)
    {
        IsAlive = isAlive;
        IsSuppressed = isSuppressed;
        HasCover = hasCover;
        HasVisibleTarget = hasVisibleTarget;
        HasRecentTarget = hasRecentTarget;
        Intent = BehaviacUnitIntent.None;
    }

    public bool ShouldReturnDead() => !IsAlive;

    public bool ShouldSeekCover() =>
        IsAlive && IsSuppressed && HasCover;

    public bool ShouldEngageVisibleTarget() =>
        IsAlive && HasVisibleTarget;

    public bool ShouldInvestigateLastKnownTarget() =>
        IsAlive && HasRecentTarget;

    public EBTStatus SelectIntent(BehaviacUnitIntent intent)
    {
        Intent = intent;
        return EBTStatus.BT_SUCCESS;
    }
}
