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
    private BodyPartAnchor[] _anchor;
    private BodyPartRole[] _role;

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

    public BodyPartAnchor[] Anchor =>
        _anchor;

    public BodyPartRole[] Role =>
        _role;

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
        _anchor = new BodyPartAnchor[initialCapacity];
        _role = new BodyPartRole[initialCapacity];
    }

    public BodyHandle CreateBody(
        UnitBodyType bodyType) =>
        CreateBody(UnitBodyCatalog.Get(bodyType));

    public BodyHandle CreateBody(
        ReadOnlySpan<BodyPartDefinition> parts)
    {
        if (parts.Length == 0 || parts.Length > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parts),
                "Тело должно содержать от 1 до 32767 частей.");
        }

        for (int i = 0; i < parts.Length; i++)
        {
            int parent = parts[i].ParentIndex;
            if (parent < -1 || parent >= i)
            {
                throw new ArgumentException(
                    "Родитель должен быть -1 или предшествовать дочерней части.",
                    nameof(parts));
            }
        }

        int start = TakeFreeRange(parts.Length);
        if (start < 0)
            start = _count;

        EnsureCapacityAt(start, parts.Length);
        SetWriteIndex(start);

        for (int i = 0; i < parts.Length; i++)
        {
            BodyPartDefinition part = parts[i];
            AddPart(
                part.LocalPosition,
                part.Scale,
                part.LocalRotation,
                part.FollowDistance,
                part.ParentIndex < 0 ? -1 : start + part.ParentIndex,
                part.Primitive,
                part.Motion,
                part.Anchor,
                part.Role);
        }

        return new BodyHandle(start, parts.Length);
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

    public void SetInitialWorldPosition(
        BodyHandle handle,
        Vector3 unitPosition,
        float unitRotation) =>
        SetInitialWorldPosition(
            handle,
            unitPosition,
            unitRotation,
            unitRotation);

    public void SetInitialWorldPosition(
        BodyHandle handle,
        Vector3 unitPosition,
        float unitRotation,
        float headRotation)
    {
        int end =
            handle.Start +
            handle.Count;

        for (int i = handle.Start; i < end; i++)
        {
            int parent = _parent[i];
            Vector3 origin = unitPosition;
            float rotation = unitRotation;

            if (_anchor[i] == BodyPartAnchor.UnitHead)
            {
                rotation = headRotation;
            }
            else if (_anchor[i] == BodyPartAnchor.Parent &&
                     parent >= handle.Start)
            {
                origin = _worldPosition[parent];
                rotation = _worldRotation[parent];
            }

            _worldPosition[i] =
                origin + Rotate2D(_localPosition[i], rotation);
            _worldRotation[i] = rotation + _localRotation[i];
        }
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
        BodyPartMotion motion,
        BodyPartAnchor anchor,
        BodyPartRole role)
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
        _anchor[index] = anchor;
        _role[index] = role;
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

        Array.Resize(
            ref _anchor,
            newCapacity);

        Array.Resize(
            ref _role,
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
