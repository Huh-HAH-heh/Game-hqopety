using System;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitBodyStore
{
    private const int DefaultCapacity = 4096;

    private Vector3[] _localPosition;
    private Vector3[] _worldPosition;
    private Vector3[] _scale;

    private float[] _localRotation;
    private float[] _worldRotation;
    private float[] _followDistance;

    private int[] _parent;
    private BodyPrimitive[] _primitive;
    private BodyPartMotion[] _motion;

    private int _count;

    private int[] _freeStarts = new int[128];
    private short[] _freeCounts = new short[128];
    private int _freeRangeCount;

    public int Count =>
        _count;

    public Vector3[] LocalPosition =>
        _localPosition;

    public Vector3[] WorldPosition =>
        _worldPosition;

    public Vector3[] Scale =>
        _scale;

    public float[] LocalRotation =>
        _localRotation;

    public float[] WorldRotation =>
        _worldRotation;

    public float[] FollowDistance =>
        _followDistance;

    public int[] Parent =>
        _parent;

    public BodyPrimitive[] Primitive =>
        _primitive;

    public BodyPartMotion[] Motion =>
        _motion;

    public UnitBodyStore(
        int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCapacity));
        }

        _localPosition = new Vector3[initialCapacity];
        _worldPosition = new Vector3[initialCapacity];
        _scale = new Vector3[initialCapacity];

        _localRotation = new float[initialCapacity];
        _worldRotation = new float[initialCapacity];
        _followDistance = new float[initialCapacity];

        _parent = new int[initialCapacity];
        _primitive = new BodyPrimitive[initialCapacity];
        _motion = new BodyPartMotion[initialCapacity];
    }

    public BodyHandle CreateBody(
        UnitBodyType bodyType)
    {
        int count =
            bodyType switch
            {
                UnitBodyType.Colonist => 2,
                UnitBodyType.SmallCreature => 1,
                UnitBodyType.SegmentedCreature => 10,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(bodyType),
                    bodyType,
                    "Неизвестный тип тела.")
            };

        int reusedStart =
            TakeFreeRange(count);

        if (reusedStart >= 0)
        {
            return CreateBodyAt(
                bodyType,
                reusedStart);
        }

        return CreateBodyAt(
            bodyType,
            _count);
    }

    public void FreeBody(
        BodyHandle handle)
    {
        if (handle.Count <= 0)
            return;

        EnsureFreeRangeCapacity(
            _freeRangeCount + 1);

        _freeStarts[_freeRangeCount] =
            handle.Start;

        _freeCounts[_freeRangeCount] =
            checked((short)handle.Count);

        _freeRangeCount++;
    }

    private BodyHandle CreateBodyAt(
        UnitBodyType bodyType,
        int start)
    {
        return bodyType switch
        {
            UnitBodyType.Colonist =>
                CreateColonistAt(start),

            UnitBodyType.SmallCreature =>
                CreateSmallCreatureAt(start),

            UnitBodyType.SegmentedCreature =>
                CreateSegmentedCreatureAt(start),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(bodyType),
                    bodyType,
                    "Неизвестный тип тела.")
        };
    }

    public void SetInitialWorldPosition(
        BodyHandle handle,
        Vector3 unitPosition,
        float unitRotation)
    {
        int end =
            handle.Start +
            handle.Count;

        for (int i = handle.Start;
             i < end;
             i++)
        {
            Vector3 local =
                _localPosition[i];

            Vector3 rotated =
                Rotate2D(
                    local,
                    unitRotation);

            _worldPosition[i] =
                unitPosition +
                rotated;

            _worldRotation[i] =
                unitRotation +
                _localRotation[i];
        }
    }

    private BodyHandle CreateColonistAt(
        int start)
    {
        EnsureCapacityAt(start, 2);
        if (start == _count)
            _count = start;

        SetWriteIndex(start);

        AddPart(
            localPosition: Vector3.Zero,
            scale: new Vector3(0.28f, 0.42f, 0.3f),
            localRotation: 0f,
            followDistance: 0f,
            parent: -1,
            primitive: BodyPrimitive.Box,
            motion: BodyPartMotion.StaticRelative);

        AddPart(
            localPosition: new Vector3(0f, 0.42f, 0f),
            scale: new Vector3(0.17f, 0.17f, 0.17f),
            localRotation: 0f,
            followDistance: 0f,
            parent: start,
            primitive: BodyPrimitive.Circle,
            motion: BodyPartMotion.StaticRelative);

        return new BodyHandle(
            start,
            2);
    }

    private BodyHandle CreateSmallCreatureAt(
        int start)
    {
        EnsureCapacityAt(start, 1);
        SetWriteIndex(start);

        AddPart(
            localPosition: Vector3.Zero,
            scale: new Vector3(0.24f, 0.24f, 0.24f),
            localRotation: 0f,
            followDistance: 0f,
            parent: -1,
            primitive: BodyPrimitive.Circle,
            motion: BodyPartMotion.StaticRelative);

        return new BodyHandle(
            start,
            1);
    }

    private BodyHandle CreateSegmentedCreatureAt(
        int start)
    {
        const int segmentCount = 10;

        EnsureCapacityAt(
            start,
            segmentCount);
        SetWriteIndex(start);

        AddPart(
            localPosition: Vector3.Zero,
            scale: new Vector3(0.52f, 0.52f, 0.45f),
            localRotation: 0f,
            followDistance: 0f,
            parent: -1,
            primitive: BodyPrimitive.Circle,
            motion: BodyPartMotion.StaticRelative);

        for (int i = 1;
             i < segmentCount;
             i++)
        {
            float radius =
                0.46f -
                (i - 1) * 0.025f;

            AddPart(
                localPosition: new Vector3(
                    -(i * 0.62f),
                    0f,
                    0f),
                scale: new Vector3(
                    radius,
                    radius,
                    radius),
                localRotation: 0f,
                followDistance: 0.62f,
                parent: start + i - 1,
                primitive: BodyPrimitive.Circle,
                motion: BodyPartMotion.FollowParent);
        }

        return new BodyHandle(
            start,
            segmentCount);
    }

    private int _writeIndex;

    private void SetWriteIndex(
        int start)
    {
        _writeIndex = start;
    }

    private void AddPart(
        Vector3 localPosition,
        Vector3 scale,
        float localRotation,
        float followDistance,
        int parent,
        BodyPrimitive primitive,
        BodyPartMotion motion)
    {
        int index = _writeIndex++;

        if (index >= _count)
        {
            _count = index + 1;
        }

        _localPosition[index] = localPosition;
        _worldPosition[index] = Vector3.Zero;
        _scale[index] = scale;

        _localRotation[index] = localRotation;
        _worldRotation[index] = 0f;
        _followDistance[index] = followDistance;

        _parent[index] = parent;
        _primitive[index] = primitive;
        _motion[index] = motion;
    }

    private void EnsureCapacityAt(
        int start,
        int count)
    {
        int required =
            start +
            count;

        if (required <= _localPosition.Length)
            return;

        int newCapacity =
            _localPosition.Length;

        while (newCapacity < required)
        {
            newCapacity *= 2;
        }

        Array.Resize(
            ref _localPosition,
            newCapacity);

        Array.Resize(
            ref _worldPosition,
            newCapacity);

        Array.Resize(
            ref _scale,
            newCapacity);

        Array.Resize(
            ref _localRotation,
            newCapacity);

        Array.Resize(
            ref _worldRotation,
            newCapacity);

        Array.Resize(
            ref _followDistance,
            newCapacity);

        Array.Resize(
            ref _parent,
            newCapacity);

        Array.Resize(
            ref _primitive,
            newCapacity);

        Array.Resize(
            ref _motion,
            newCapacity);
    }

    private int TakeFreeRange(
        int count)
    {
        for (int i = _freeRangeCount - 1;
             i >= 0;
             i--)
        {
            if (_freeCounts[i] != count)
                continue;

            int start =
                _freeStarts[i];

            int last =
                _freeRangeCount - 1;

            _freeStarts[i] =
                _freeStarts[last];

            _freeCounts[i] =
                _freeCounts[last];

            _freeRangeCount--;

            return start;
        }

        return -1;
    }

    private void EnsureFreeRangeCapacity(
        int required)
    {
        if (required <= _freeStarts.Length)
            return;

        int newCapacity =
            _freeStarts.Length * 2;

        while (newCapacity < required)
        {
            newCapacity *= 2;
        }

        Array.Resize(
            ref _freeStarts,
            newCapacity);

        Array.Resize(
            ref _freeCounts,
            newCapacity);
    }

    private static Vector3 Rotate2D(
        Vector3 value,
        float angle)
    {
        float cos = MathF.Cos(angle);
        float sin = MathF.Sin(angle);

        return new Vector3(
            value.X * cos - value.Y * sin,
            value.X * sin + value.Y * cos,
            value.Z);
    }
}
