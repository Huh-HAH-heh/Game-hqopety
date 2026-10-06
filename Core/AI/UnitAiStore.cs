using System;
using System.Numerics;

namespace Core.Unit;

public enum UnitAiState : byte
{
    Idle = 0,
    Attack = 1,
    SeekCover = 2,
    Search = 3,
    Dead = 4
}

public sealed class UnitAiStore
{
    private UnitAiState[] _state;
    private bool[] _hasTarget;
    private UnitId[] _target;
    private Vector3[] _lastSeenPosition;
    private float[] _targetMemory;
    private bool[] _hasGoal;
    private Vector3[] _goal;

    public int Capacity =>
        _state.Length;

    public UnitAiState[] State =>
        _state;

    public bool[] HasTarget =>
        _hasTarget;

    public UnitId[] Target =>
        _target;

    public Vector3[] LastSeenPosition =>
        _lastSeenPosition;

    public float[] TargetMemory =>
        _targetMemory;

    public bool[] HasGoal =>
        _hasGoal;

    public Vector3[] Goal =>
        _goal;

    public UnitAiStore(
        int initialUnitCapacity = 1024)
    {
        if (initialUnitCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialUnitCapacity));
        }

        _state = new UnitAiState[initialUnitCapacity];
        _hasTarget = new bool[initialUnitCapacity];
        _target = new UnitId[initialUnitCapacity];
        _lastSeenPosition = new Vector3[initialUnitCapacity];
        _targetMemory = new float[initialUnitCapacity];
        _hasGoal = new bool[initialUnitCapacity];
        _goal = new Vector3[initialUnitCapacity];
    }

    public void InitializeUnit(
        int unitIndex)
    {
        EnsureCapacity(unitIndex + 1);

        _state[unitIndex] = UnitAiState.Idle;
        _hasTarget[unitIndex] = false;
        _target[unitIndex] = default;
        _lastSeenPosition[unitIndex] = Vector3.Zero;
        _targetMemory[unitIndex] = 0f;
        _hasGoal[unitIndex] = false;
        _goal[unitIndex] = Vector3.Zero;
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
        _hasTarget[unitIndex] = false;
        _target[unitIndex] = default;
        _lastSeenPosition[unitIndex] = Vector3.Zero;
        _targetMemory[unitIndex] = 0f;
        _hasGoal[unitIndex] = false;
        _goal[unitIndex] = Vector3.Zero;
    }

    public void RememberTarget(
        int unitIndex,
        UnitId target,
        Vector3 position,
        float memorySeconds)
    {
        _hasTarget[unitIndex] = true;
        _target[unitIndex] = target;
        _lastSeenPosition[unitIndex] = position;
        _targetMemory[unitIndex] = memorySeconds;
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
            ref _hasTarget,
            newCapacity);

        Array.Resize(
            ref _target,
            newCapacity);

        Array.Resize(
            ref _lastSeenPosition,
            newCapacity);

        Array.Resize(
            ref _targetMemory,
            newCapacity);

        Array.Resize(
            ref _hasGoal,
            newCapacity);

        Array.Resize(
            ref _goal,
            newCapacity);
    }
}
