using System;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitStore
{
    private const int DefaultCapacity = 1024;

    private Vector3[] _position;
    private Vector3[] _velocity;
    private Vector3[] _target;

    private float[] _rotation;
    private float[] _moveSpeed;
    private float[] _radius;
    private float[] _viewRange;
    private float[] _fieldOfView;

    private UnitType[] _type;

    private int[] _bodyStart;
    private short[] _bodyCount;

    private bool[] _hasTarget;
    private uint[] _generation;

    private int[] _activeIndices;
    private int[] _activeSlots;
    private int[] _freeIndices;

    private int _activeCount;
    private int _count;
    private int _freeCount;

    public int ActiveCount =>
        _activeCount;

    public int Capacity =>
        _position.Length;

    public Vector3[] Position =>
        _position;

    public Vector3[] Velocity =>
        _velocity;

    public Vector3[] Target =>
        _target;

    public float[] Rotation =>
        _rotation;

    public float[] MoveSpeed =>
        _moveSpeed;

    public float[] Radius =>
        _radius;

    public float[] ViewRange =>
        _viewRange;

    public float[] FieldOfView =>
        _fieldOfView;

    public UnitType[] Type =>
        _type;

    public int[] BodyStart =>
        _bodyStart;

    public short[] BodyCount =>
        _bodyCount;

    public bool[] HasTarget =>
        _hasTarget;

    public ReadOnlySpan<int> ActiveIndices =>
        _activeIndices.AsSpan(
            0,
            _activeCount);

    public UnitStore(
        int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCapacity));
        }

        _position = new Vector3[initialCapacity];
        _velocity = new Vector3[initialCapacity];
        _target = new Vector3[initialCapacity];

        _rotation = new float[initialCapacity];
        _moveSpeed = new float[initialCapacity];
        _radius = new float[initialCapacity];
        _viewRange = new float[initialCapacity];
        _fieldOfView = new float[initialCapacity];

        _type = new UnitType[initialCapacity];

        _bodyStart = new int[initialCapacity];
        _bodyCount = new short[initialCapacity];

        _hasTarget = new bool[initialCapacity];
        _generation = new uint[initialCapacity];

        _activeIndices = new int[initialCapacity];
        _activeSlots = new int[initialCapacity];
        _freeIndices = new int[initialCapacity];

        Array.Fill(
            _activeSlots,
            -1);
    }

    public UnitId Create(
        in UnitDefinition definition,
        Vector3 position,
        float rotation,
        BodyHandle body)
    {
        int index;

        if (_freeCount > 0)
        {
            index =
                _freeIndices[
                    --_freeCount];
        }
        else
        {
            if (_count == Capacity)
            {
                Grow();
            }

            index = _count++;

            if (_generation[index] == 0)
            {
                _generation[index] = 1;
            }
        }

        _position[index] = position;
        _velocity[index] = Vector3.Zero;
        _target[index] = position;

        _rotation[index] = rotation;
        _moveSpeed[index] = definition.MoveSpeed;
        _radius[index] = definition.Radius;
        _viewRange[index] = definition.ViewRange;
        _fieldOfView[index] = definition.FieldOfView;

        _type[index] = definition.Type;

        _bodyStart[index] = body.Start;
        _bodyCount[index] = checked((short)body.Count);

        _hasTarget[index] = false;

        _activeSlots[index] = _activeCount;
        _activeIndices[_activeCount++] = index;

        return new UnitId(
            index,
            _generation[index]);
    }

    public bool Destroy(
        UnitId id)
    {
        if (!TryGetIndex(
                id,
                out int index))
        {
            return false;
        }

        int slot =
            _activeSlots[index];

        int lastSlot =
            _activeCount - 1;

        int movedIndex =
            _activeIndices[lastSlot];

        _activeIndices[slot] = movedIndex;
        _activeSlots[movedIndex] = slot;

        _activeCount--;
        _activeSlots[index] = -1;

        _generation[index] =
            NextGeneration(
                _generation[index]);

        _freeIndices[_freeCount++] = index;

        _hasTarget[index] = false;
        _velocity[index] = Vector3.Zero;

        return true;
    }

    public bool IsAlive(
        UnitId id)
    {
        return TryGetIndex(
            id,
            out _);
    }

    public bool TryGetIndex(
        UnitId id,
        out int index)
    {
        index = id.Index;

        if (index < 0 ||
            index >= _count ||
            id.Generation == 0 ||
            _generation[index] != id.Generation ||
            _activeSlots[index] < 0)
        {
            index = -1;
            return false;
        }

        return true;
    }

    public void SetTarget(
        UnitId id,
        Vector3 target)
    {
        if (!TryGetIndex(
                id,
                out int index))
        {
            return;
        }

        _target[index] = target;
        _hasTarget[index] = true;
    }

    public void ClearTarget(
        UnitId id)
    {
        if (!TryGetIndex(
                id,
                out int index))
        {
            return;
        }

        _hasTarget[index] = false;
        _velocity[index] = Vector3.Zero;
    }

    private void Grow()
    {
        int oldCapacity =
            Capacity;

        int newCapacity =
            oldCapacity * 2;

        Array.Resize(
            ref _position,
            newCapacity);

        Array.Resize(
            ref _velocity,
            newCapacity);

        Array.Resize(
            ref _target,
            newCapacity);

        Array.Resize(
            ref _rotation,
            newCapacity);

        Array.Resize(
            ref _moveSpeed,
            newCapacity);

        Array.Resize(
            ref _radius,
            newCapacity);

        Array.Resize(
            ref _viewRange,
            newCapacity);

        Array.Resize(
            ref _fieldOfView,
            newCapacity);

        Array.Resize(
            ref _type,
            newCapacity);

        Array.Resize(
            ref _bodyStart,
            newCapacity);

        Array.Resize(
            ref _bodyCount,
            newCapacity);

        Array.Resize(
            ref _hasTarget,
            newCapacity);

        Array.Resize(
            ref _generation,
            newCapacity);

        Array.Resize(
            ref _activeIndices,
            newCapacity);

        Array.Resize(
            ref _activeSlots,
            newCapacity);

        Array.Resize(
            ref _freeIndices,
            newCapacity);

        Array.Fill(
            _activeSlots,
            -1,
            oldCapacity,
            newCapacity - oldCapacity);
    }

    private static uint NextGeneration(
        uint generation)
    {
        if (generation == uint.MaxValue)
        {
            return 1;
        }

        return generation + 1;
    }
}
