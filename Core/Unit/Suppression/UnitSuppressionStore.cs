using System;
using System.Numerics;

namespace Core.Unit;

public enum UnitSuppressionState : byte
{
    Calm = 0,
    Suppressed = 1,
    Panicked = 2
}

public sealed class UnitSuppressionStore
{
    public const float SuppressedThreshold = 1f;
    public const float PanickedThreshold = 2.5f;

    private float[] _value;
    private Vector3[] _source;

    public int Capacity => _value.Length;
    public float[] Value => _value;
    public Vector3[] Source => _source;

    public UnitSuppressionStore(int initialCapacity = 1024)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _value = new float[initialCapacity];
        _source = new Vector3[initialCapacity];
    }

    public void InitializeUnit(int unitIndex)
    {
        EnsureCapacity(unitIndex + 1);
        _value[unitIndex] = 0f;
        _source[unitIndex] = Vector3.Zero;
    }

    public void ClearUnit(int unitIndex)
    {
        if (unitIndex < 0 || unitIndex >= Capacity)
            return;

        _value[unitIndex] = 0f;
        _source[unitIndex] = Vector3.Zero;
    }

    public void Add(
        int unitIndex,
        float amount,
        Vector3 source)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity ||
            amount <= 0f)
            return;

        _value[unitIndex] =
            MathF.Min(
                10f,
                _value[unitIndex] + amount);

        _source[unitIndex] = source;
    }

    public void Update(
        UnitStore units,
        float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];

            _value[unit] =
                MathF.Max(
                    0f,
                    _value[unit] -
                    0.35f * deltaTime);
        }
    }

    public UnitSuppressionState GetState(int unitIndex)
    {
        float value = _value[unitIndex];

        return value >= PanickedThreshold
            ? UnitSuppressionState.Panicked
            : value >= SuppressedThreshold
                ? UnitSuppressionState.Suppressed
                : UnitSuppressionState.Calm;
    }

    public float GetAccuracyMultiplier(int unitIndex)
    {
        float value = _value[unitIndex];

        return 1f +
            MathF.Min(
                2f,
                value * 0.35f);
    }

    public void EnsureCapacity(int required)
    {
        if (required <= Capacity)
            return;

        int newCapacity =
            Math.Max(
                required,
                Capacity * 2);

        Array.Resize(ref _value, newCapacity);
        Array.Resize(ref _source, newCapacity);
    }
}
