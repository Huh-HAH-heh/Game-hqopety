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
    Flank = 5
}

public enum UnitAiOrderType : byte
{
    None = 0,
    Move = 1,
    Stop = 2,
    Attack = 3
}

public sealed class UnitAiStore
{
    private UnitAiState[] _state;
    private BehaviacUnitIntent[] _lastIntent;
    private UnitAiOrderType[] _playerOrderType;
    private Vector3[] _playerOrderPosition;
    private UnitId[] _playerOrderTarget;
    private UnitId[] _squadSupportTarget;
    private int[] _squadSupportSquadId;
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

    public UnitAiOrderType[] PlayerOrderType =>
        _playerOrderType;

    public Vector3[] PlayerOrderPosition =>
        _playerOrderPosition;

    public UnitId[] PlayerOrderTarget =>
        _playerOrderTarget;

    public UnitId[] SquadSupportTarget =>
        _squadSupportTarget;

    public int[] SquadSupportSquadId =>
        _squadSupportSquadId;

    public bool HasSquadSupportOrder(int unitIndex) =>
        (uint)unitIndex < (uint)Capacity &&
        _squadSupportTarget[unitIndex].IsValid &&
        _squadSupportSquadId[unitIndex] > 0;

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
        _playerOrderType = new UnitAiOrderType[initialUnitCapacity];
        _playerOrderPosition = new Vector3[initialUnitCapacity];
        _playerOrderTarget = new UnitId[initialUnitCapacity];
        _squadSupportTarget = new UnitId[initialUnitCapacity];
        _squadSupportSquadId = new int[initialUnitCapacity];
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
        _playerOrderType[unitIndex] = UnitAiOrderType.None;
        _playerOrderPosition[unitIndex] = Vector3.Zero;
        _playerOrderTarget[unitIndex] = default;
        _squadSupportTarget[unitIndex] = default;
        _squadSupportSquadId[unitIndex] = 0;
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
        _playerOrderType[unitIndex] = UnitAiOrderType.None;
        _playerOrderPosition[unitIndex] = Vector3.Zero;
        _playerOrderTarget[unitIndex] = default;
        _squadSupportTarget[unitIndex] = default;
        _squadSupportSquadId[unitIndex] = 0;
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
        _playerOrderType[unitIndex] = order;
        _playerOrderPosition[unitIndex] = position;
        _playerOrderTarget[unitIndex] = target;
        _squadSupportTarget[unitIndex] = default;
        _squadSupportSquadId[unitIndex] = 0;
    }

    public void SetSquadSupportOrder(
        int unitIndex,
        UnitAiOrderType order,
        Vector3 position,
        UnitId target,
        int squadId)
    {
        SetPlayerOrder(unitIndex, order, position);
        if (!target.IsValid || squadId <= 0)
            return;

        _squadSupportTarget[unitIndex] = target;
        _squadSupportSquadId[unitIndex] = squadId;
    }

    public void ClearPlayerOrder(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return;

        _playerOrderType[unitIndex] = UnitAiOrderType.None;
        _playerOrderPosition[unitIndex] = Vector3.Zero;
        _playerOrderTarget[unitIndex] = default;
        _squadSupportTarget[unitIndex] = default;
        _squadSupportSquadId[unitIndex] = 0;
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

        Array.Resize(ref _playerOrderType, newCapacity);
        Array.Resize(ref _playerOrderPosition, newCapacity);
        Array.Resize(ref _playerOrderTarget, newCapacity);
        Array.Resize(ref _squadSupportTarget, newCapacity);
        Array.Resize(ref _squadSupportSquadId, newCapacity);

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
