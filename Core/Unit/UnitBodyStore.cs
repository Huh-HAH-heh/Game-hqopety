using System;
using System.Numerics;

namespace Core.Unit;

public sealed class UnitBodyStore
{
    private const int DefaultCapacity = 4096;

    private Vector3[] _localPosition;
    private Vector3[] _worldPosition;
    private float[] _tailAngle;
    private bool[] _tailInitialized;
    private Vector3[] _scale;
    private Vector3[] _crouchingOffset;
    private Vector3[] _sittingOffset;
    private Vector3[] _lyingOffset;
    private float[] _crouchingRotationOffset;
    private float[] _sittingRotationOffset;
    private float[] _lyingRotationOffset;

    private float[] _localRotation;
    private float[] _worldRotation;
    private float[] _followDistance;

    private int[] _parent;
    private BodyPrimitive[] _primitive;
    private BodyPartMotion[] _motion;
    private BodyPartAnchor[] _anchor;
    private BodyPartRole[] _role;
    private BodyPartPivot[] _pivot;

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

    public float[] TailAngle =>
        _tailAngle;

    public bool[] TailInitialized =>
        _tailInitialized;

    public Vector3[] Scale =>
        _scale;

    public Vector3[] CrouchingOffset => _crouchingOffset;
    public Vector3[] SittingOffset => _sittingOffset;
    public Vector3[] LyingOffset => _lyingOffset;
    public float[] CrouchingRotationOffset => _crouchingRotationOffset;
    public float[] SittingRotationOffset => _sittingRotationOffset;
    public float[] LyingRotationOffset => _lyingRotationOffset;

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

    public BodyPartPivot[] Pivot =>
        _pivot;


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
        _tailAngle = new float[initialCapacity];
        _tailInitialized = new bool[initialCapacity];
        _scale = new Vector3[initialCapacity];
        _crouchingOffset = new Vector3[initialCapacity];
        _sittingOffset = new Vector3[initialCapacity];
        _lyingOffset = new Vector3[initialCapacity];
        _crouchingRotationOffset = new float[initialCapacity];
        _sittingRotationOffset = new float[initialCapacity];
        _lyingRotationOffset = new float[initialCapacity];

        _localRotation = new float[initialCapacity];
        _worldRotation = new float[initialCapacity];
        _followDistance = new float[initialCapacity];

        _parent = new int[initialCapacity];
        _primitive = new BodyPrimitive[initialCapacity];
        _motion = new BodyPartMotion[initialCapacity];
        _anchor = new BodyPartAnchor[initialCapacity];
        _role = new BodyPartRole[initialCapacity];
        _pivot = new BodyPartPivot[initialCapacity];
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
                part.Role,
                part.Pivot,
                part.CrouchingOffset,
                part.SittingOffset,
                part.LyingOffset,
                part.CrouchingRotationOffset,
                part.SittingRotationOffset,
                part.LyingRotationOffset);
        }

        return new BodyHandle(start, parts.Length);
    }

    /// <summary>Replaces an existing body's transform definitions without reallocating its storage.</summary>
    public void ApplyBodyDefinition(
        BodyHandle handle,
        ReadOnlySpan<BodyPartDefinition> parts)
    {
        if (handle.Count != parts.Length ||
            handle.Start < 0 ||
            handle.Start + handle.Count > _count)
        {
            throw new ArgumentException("Body handle and definition count do not match.", nameof(parts));
        }

        for (int i = 0; i < parts.Length; i++)
        {
            int parent = parts[i].ParentIndex;
            if (parent < -1 || parent >= i)
                throw new ArgumentException("Parent must precede its child.", nameof(parts));
        }

        for (int i = 0; i < parts.Length; i++)
        {
            int index = handle.Start + i;
            BodyPartDefinition part = parts[i];
            _localPosition[index] = part.LocalPosition;
            _scale[index] = part.Scale;
            _localRotation[index] = part.LocalRotation;
            _followDistance[index] = part.FollowDistance;
            _parent[index] = part.ParentIndex < 0 ? -1 : handle.Start + part.ParentIndex;
            _primitive[index] = part.Primitive;
            _motion[index] = part.Motion;
            _anchor[index] = part.Anchor;
            _role[index] = part.Role;
            _pivot[index] = part.Pivot;
            _crouchingOffset[index] = part.CrouchingOffset;
            _sittingOffset[index] = part.SittingOffset;
            _lyingOffset[index] = part.LyingOffset;
            _crouchingRotationOffset[index] = part.CrouchingRotationOffset;
            _sittingRotationOffset[index] = part.SittingRotationOffset;
            _lyingRotationOffset[index] = part.LyingRotationOffset;
            _tailInitialized[index] = false;
        }
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
            float positionRotation = unitRotation;
            float partRotation = unitRotation;

            if (_anchor[i] == BodyPartAnchor.UnitHead)
            {
                // The head may look independently, but its center stays at
                // the front of the torso instead of orbiting around the unit.
                partRotation = headRotation;
            }
            else if (_anchor[i] == BodyPartAnchor.Parent &&
                     parent >= handle.Start)
            {
                origin = _worldPosition[parent];
                positionRotation = _worldRotation[parent];
                partRotation = _worldRotation[parent];
            }

            Vector3 pivot =
                origin + Rotate2D(_localPosition[i], positionRotation);

            partRotation += _localRotation[i];

            if (_pivot[i] == BodyPartPivot.Start)
            {
                pivot += Rotate2D(
                    new Vector3(MathF.Abs(_scale[i].X), 0f, 0f),
                    partRotation);
            }

            _worldPosition[i] = pivot;
            _tailAngle[i] = partRotation;
            _tailInitialized[i] = false;
            _worldRotation[i] = partRotation;
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
        BodyPartRole role,
        BodyPartPivot pivot,
        Vector3 crouchingOffset,
        Vector3 sittingOffset,
        Vector3 lyingOffset,
        float crouchingRotationOffset,
        float sittingRotationOffset,
        float lyingRotationOffset)
    {
        int index = _writeIndex++;

        if (index >= _count)
        {
            _count = index + 1;
        }

        _localPosition[index] = localPosition;
        _worldPosition[index] = Vector3.Zero;
        _tailAngle[index] = 0f;
        _tailInitialized[index] = false;
        _scale[index] = scale;

        _localRotation[index] = localRotation;
        _worldRotation[index] = 0f;
        _followDistance[index] = followDistance;

        _parent[index] = parent;
        _primitive[index] = primitive;
        _motion[index] = motion;
        _anchor[index] = anchor;
        _role[index] = role;
        _pivot[index] = pivot;
        _crouchingOffset[index] = crouchingOffset;
        _sittingOffset[index] = sittingOffset;
        _lyingOffset[index] = lyingOffset;
        _crouchingRotationOffset[index] = crouchingRotationOffset;
        _sittingRotationOffset[index] = sittingRotationOffset;
        _lyingRotationOffset[index] = lyingRotationOffset;
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
            ref _tailAngle,
            newCapacity);

        Array.Resize(
            ref _tailInitialized,
            newCapacity);

        Array.Resize(
            ref _scale,
            newCapacity);
        Array.Resize(ref _crouchingOffset, newCapacity);
        Array.Resize(ref _sittingOffset, newCapacity);
        Array.Resize(ref _lyingOffset, newCapacity);
        Array.Resize(ref _crouchingRotationOffset, newCapacity);
        Array.Resize(ref _sittingRotationOffset, newCapacity);
        Array.Resize(ref _lyingRotationOffset, newCapacity);

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

        Array.Resize(
            ref _pivot,
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
