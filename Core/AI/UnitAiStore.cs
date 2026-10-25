using System;
using System.Numerics;
using Core.AI.Behaviac;

namespace Core.Unit;

public enum UnitAiState : byte
{
    Idle = 0,
    Attack = 1,
    SeekCover = 2,
    Search = 3,
    Dead = 4,
    Flank = 5,
    Injured = 6
}

public enum UnitAiOrderType : byte
{
    None = 0,
    Move = 1,
    Stop = 2,
    Attack = 3,
    StealthMove = 4
}

public enum UnitIntentDecisionReason : byte
{
    InitialSelection,
    HighestUtility,
    RetainedPreviousIntent,
    EmergencyOverride,
    PreviousIntentNoLongerValid,
    Dead
}

/// <summary>
/// Compact, allocation-free utility snapshot for later AI visualization.
/// </summary>
public readonly record struct UnitIntentUtilityBreakdown
{
    public BehaviacUnitIntent SuggestedIntent { get; init; }
    public float SuggestedScore { get; init; }
    public BehaviacUnitIntent PreviousIntent { get; init; }
    public float PreviousScore { get; init; }
    public UnitIntentDecisionReason Reason { get; init; }
    public bool RetainedPreviousIntent { get; init; }

    public float EngageScore { get; init; }
    public float HoldAndEngageScore { get; init; }
    public float CoverScore { get; init; }
    public float RetreatScore { get; init; }
    public float MoveOrderScore { get; init; }
    public float HoldOrderScore { get; init; }
    public float AttackOrderScore { get; init; }
    public float FlankScore { get; init; }
    public float ProneScore { get; init; }
    public float SuppressiveFireScore { get; init; }
    public float LastKnownContactScore { get; init; }
    public float InvestigableContactScore { get; init; }
    public float SquadContactScore { get; init; }
    public float PatrolScore { get; init; }
}

/// <summary>
/// Immutable, already-collected facts for one pawn. Scoring performs no world queries,
/// allocations, navigation, or combat operations. Normalized inputs are in 0..1.
/// </summary>
public readonly record struct UnitIntentUtilityContext
{
    public bool IsAlive { get; init; }
    public bool HasVisibleTarget { get; init; }
    public bool HasRecentTarget { get; init; }
    public bool HasActiveTargetMemory { get; init; }
    public bool HasCover { get; init; }
    public bool IsInImmediateDanger { get; init; }
    public bool IsSuppressed { get; init; }
    public bool IsPanicked { get; init; }
    public bool IsLowHealth { get; init; }
    public bool HasSuppressiveWeapon { get; init; }
    public bool HasRangedWeapon { get; init; }
    public bool CanFlank { get; init; }
    public bool IsFlanking { get; init; }
    public bool IsProne { get; init; }
    public bool IsAlerted { get; init; }
    public bool HasInvestigableContact { get; init; }
    public bool HasSquadContact { get; init; }
    public bool IsInvestigatingContext { get; init; }
    public UnitAiOrderType OrderType { get; init; }

    public float HealthRatio { get; init; }
    public float Pain { get; init; }
    public float Fatigue { get; init; }
    public float Stress { get; init; }
    public float EnergyRatio { get; init; }
    public float HydrationRatio { get; init; }
    public float SuppressionResistance { get; init; }
    public float ShootingSkill { get; init; }
    public float MeleeSkill { get; init; }
    public float Caution { get; init; }
    public float Aggression { get; init; }
    public float Discipline { get; init; }
    public float Alertness { get; init; }

    public float MoveOrderUtility { get; init; }
    public float CombatUtility { get; init; }
    public float CoverUtility { get; init; }
    public float FlankUtility { get; init; }
    public float ProneCombatUtility { get; init; }
    public float LastKnownTargetUtility { get; init; }
    public float InvestigationUtility { get; init; }
    public float SquadContactUtility { get; init; }
}

/// <summary>
/// Utility proposals run beside the existing Behaviac selector during migration.
/// This allows comparison before switching the live decision source in a later stage.
/// </summary>

public sealed class UnitAiStore
{
    private UnitAiState[] _state;
    private BehaviacUnitIntent[] _lastIntent;
    private BehaviacUnitIntent[] _utilityIntent;
    private float[] _utilityIntentCommitRemainingSeconds;
    private UnitIntentUtilityBreakdown[] _utilityIntentBreakdown;
    private UnitIntentUtilityContext[] _utilityIntentContext;
    private UnitAiOrderType[] _playerOrderType;
    private Vector3[] _playerOrderPosition;
    private UnitId[] _playerOrderTarget;
    private bool[] _hasTarget;
    private UnitId[] _target;
    private Vector3[] _lastSeenPosition;
    private Vector3[] _lastSeenAimPoint;
    private float[] _targetMemory;
    private bool[] _hasGoal;
    private Vector3[] _goal;
    private float[] _suppressionCoverRetry;
    private Vector3[] _suppressionRetrySource;

    public int Capacity =>
        _state.Length;

    public UnitAiState[] State =>
        _state;

    public BehaviacUnitIntent[] LastIntent =>
        _lastIntent;

    public BehaviacUnitIntent[] UtilityIntent =>
        _utilityIntent;

    public float[] UtilityIntentCommitRemainingSeconds =>
        _utilityIntentCommitRemainingSeconds;

    public UnitIntentUtilityBreakdown[] UtilityIntentBreakdown =>
        _utilityIntentBreakdown;

    public UnitIntentUtilityContext[] UtilityIntentContext =>
        _utilityIntentContext;

    public UnitAiOrderType[] PlayerOrderType =>
        _playerOrderType;

    public Vector3[] PlayerOrderPosition =>
        _playerOrderPosition;

    public UnitId[] PlayerOrderTarget =>
        _playerOrderTarget;

    public bool[] HasTarget =>
        _hasTarget;

    public UnitId[] Target =>
        _target;

    public Vector3[] LastSeenPosition =>
        _lastSeenPosition;

    public Vector3[] LastSeenAimPoint =>
        _lastSeenAimPoint;

    public float[] TargetMemory =>
        _targetMemory;

    public bool[] HasGoal =>
        _hasGoal;

    public Vector3[] Goal =>
        _goal;

    public float[] SuppressionCoverRetry =>
        _suppressionCoverRetry;

    public Vector3[] SuppressionRetrySource =>
        _suppressionRetrySource;

    public UnitAiStore(
        int initialUnitCapacity = 1024)
    {
        if (initialUnitCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialUnitCapacity));
        }

        _state = new UnitAiState[initialUnitCapacity];
        _lastIntent = new BehaviacUnitIntent[initialUnitCapacity];
        _utilityIntent = new BehaviacUnitIntent[initialUnitCapacity];
        _utilityIntentCommitRemainingSeconds = new float[initialUnitCapacity];
        _utilityIntentBreakdown = new UnitIntentUtilityBreakdown[initialUnitCapacity];
        _utilityIntentContext = new UnitIntentUtilityContext[initialUnitCapacity];
        _playerOrderType = new UnitAiOrderType[initialUnitCapacity];
        _playerOrderPosition = new Vector3[initialUnitCapacity];
        _playerOrderTarget = new UnitId[initialUnitCapacity];
        _hasTarget = new bool[initialUnitCapacity];
        _target = new UnitId[initialUnitCapacity];
        _lastSeenPosition = new Vector3[initialUnitCapacity];
        _lastSeenAimPoint = new Vector3[initialUnitCapacity];
        _targetMemory = new float[initialUnitCapacity];
        _hasGoal = new bool[initialUnitCapacity];
        _goal = new Vector3[initialUnitCapacity];
        _suppressionCoverRetry = new float[initialUnitCapacity];
        _suppressionRetrySource = new Vector3[initialUnitCapacity];
    }

    public void InitializeUnit(
        int unitIndex)
    {
        EnsureCapacity(unitIndex + 1);

        _state[unitIndex] = UnitAiState.Idle;
        _lastIntent[unitIndex] = BehaviacUnitIntent.None;
        _utilityIntent[unitIndex] = BehaviacUnitIntent.None;
        _utilityIntentCommitRemainingSeconds[unitIndex] = 0f;
        _utilityIntentBreakdown[unitIndex] = default;
        _utilityIntentContext[unitIndex] = default;
        _playerOrderType[unitIndex] = UnitAiOrderType.None;
        _playerOrderPosition[unitIndex] = Vector3.Zero;
        _playerOrderTarget[unitIndex] = default;
        _hasTarget[unitIndex] = false;
        _target[unitIndex] = default;
        _lastSeenPosition[unitIndex] = Vector3.Zero;
        _lastSeenAimPoint[unitIndex] = Vector3.Zero;
        _targetMemory[unitIndex] = 0f;
        _hasGoal[unitIndex] = false;
        _goal[unitIndex] = Vector3.Zero;
        _suppressionCoverRetry[unitIndex] = 0f;
        _suppressionRetrySource[unitIndex] = Vector3.Zero;
    }

    public void ClearUnit(
        int unitIndex)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
        {
            return;
        }

        _state[unitIndex] = UnitAiState.Idle;
        _lastIntent[unitIndex] = BehaviacUnitIntent.None;
        _utilityIntent[unitIndex] = BehaviacUnitIntent.None;
        _utilityIntentCommitRemainingSeconds[unitIndex] = 0f;
        _utilityIntentBreakdown[unitIndex] = default;
        _utilityIntentContext[unitIndex] = default;
        _playerOrderType[unitIndex] = UnitAiOrderType.None;
        _playerOrderPosition[unitIndex] = Vector3.Zero;
        _playerOrderTarget[unitIndex] = default;
        _hasTarget[unitIndex] = false;
        _target[unitIndex] = default;
        _lastSeenPosition[unitIndex] = Vector3.Zero;
        _lastSeenAimPoint[unitIndex] = Vector3.Zero;
        _targetMemory[unitIndex] = 0f;
        _hasGoal[unitIndex] = false;
        _goal[unitIndex] = Vector3.Zero;
        _suppressionCoverRetry[unitIndex] = 0f;
        _suppressionRetrySource[unitIndex] = Vector3.Zero;
    }

    public void SetPlayerOrder(
        int unitIndex,
        UnitAiOrderType order,
        Vector3 position = default,
        UnitId target = default)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));

        EnsureCapacity(unitIndex + 1);

        // Replacing an order must not keep navigating to the previous task's goal
        // or retain its remembered combat target.
        bool replacesTask =
            _playerOrderType[unitIndex] != order ||
            _playerOrderPosition[unitIndex] != position ||
            !_playerOrderTarget[unitIndex].Equals(target);
        if (replacesTask)
            ClearTarget(unitIndex);

        _playerOrderType[unitIndex] = order;
        _playerOrderPosition[unitIndex] = position;
        _playerOrderTarget[unitIndex] = target;
    }

    public void ClearPlayerOrder(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return;

        _playerOrderType[unitIndex] = UnitAiOrderType.None;
        _playerOrderPosition[unitIndex] = Vector3.Zero;
        _playerOrderTarget[unitIndex] = default;
    }

    public void RememberTarget(
        int unitIndex,
        UnitId target,
        Vector3 position,
        float memorySeconds)
    {
        if (!_hasTarget[unitIndex] || !_target[unitIndex].Equals(target))
            _lastSeenAimPoint[unitIndex] = Vector3.Zero;

        _hasTarget[unitIndex] = true;
        _target[unitIndex] = target;
        _lastSeenPosition[unitIndex] = position;
        _targetMemory[unitIndex] = memorySeconds;
    }

    public void RememberAimPoint(int unitIndex, Vector3 point)
    {
        if (float.IsFinite(point.X) &&
            float.IsFinite(point.Y) &&
            float.IsFinite(point.Z))
        {
            _lastSeenAimPoint[unitIndex] = point;
        }
    }

    public void TickMemory(
        int unitIndex,
        float deltaTime)
    {
        if (_targetMemory[unitIndex] <= 0f)
            return;

        _targetMemory[unitIndex] =
            MathF.Max(
                0f,
                _targetMemory[unitIndex] -
                deltaTime);
    }

    public void ClearTarget(
        int unitIndex)
    {
        _hasTarget[unitIndex] = false;
        _target[unitIndex] = default;
        _targetMemory[unitIndex] = 0f;
        _lastSeenAimPoint[unitIndex] = Vector3.Zero;
        _hasGoal[unitIndex] = false;
    }

    public void SetGoal(
        int unitIndex,
        Vector3 goal)
    {
        _goal[unitIndex] = goal;
        _hasGoal[unitIndex] = true;
    }

    public void ClearGoal(
        int unitIndex)
    {
        _hasGoal[unitIndex] = false;
        _goal[unitIndex] = Vector3.Zero;
    }

    public void EnsureCapacity(
        int required)
    {
        if (required <= Capacity)
            return;

        int newCapacity =
            Math.Max(
                required,
                Capacity * 2);

        Array.Resize(
            ref _state,
            newCapacity);

        Array.Resize(
            ref _lastIntent,
            newCapacity);

        Array.Resize(ref _utilityIntent, newCapacity);
        Array.Resize(ref _utilityIntentCommitRemainingSeconds, newCapacity);
        Array.Resize(ref _utilityIntentBreakdown, newCapacity);
        Array.Resize(ref _utilityIntentContext, newCapacity);

        Array.Resize(ref _playerOrderType, newCapacity);
        Array.Resize(ref _playerOrderPosition, newCapacity);
        Array.Resize(ref _playerOrderTarget, newCapacity);

        Array.Resize(
            ref _hasTarget,
            newCapacity);

        Array.Resize(
            ref _target,
            newCapacity);

        Array.Resize(
            ref _lastSeenPosition,
            newCapacity);

        Array.Resize(ref _lastSeenAimPoint, newCapacity);

        Array.Resize(
            ref _targetMemory,
            newCapacity);

        Array.Resize(
            ref _hasGoal,
            newCapacity);

        Array.Resize(
            ref _goal,
            newCapacity);

        Array.Resize(
            ref _suppressionCoverRetry,
            newCapacity);

        Array.Resize(
            ref _suppressionRetrySource,
            newCapacity);
    }
}
