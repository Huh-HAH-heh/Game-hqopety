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
    public const float SuppressedThreshold = 2.0f;
    public const float SuppressionRecoveryThreshold = 1.0f;
    public const float PanickedThreshold = 7.0f;
    public const float PanicRecoveryThreshold = 3.2f;
    public const float AlertnessThreshold = 0.16f;

    private const float SuppressionDecayPerSecond = 0.95f;
    private const float AlertnessDecayPerSecond = 0.18f;
    private const float CoverSuppressionMultiplier = 0.55f;

    private UnitSkillStore? _skills;
    private float[] _value;
    private Vector3[] _source;
    private bool[] _suppressed;
    private bool[] _panicked;
    private float[] _alertness;
    private Vector3[] _alertSource;
    private bool[] _inCover;
    private float[] _immediateDangerTimer;
    private Vector3[] _immediateDangerSource;

    public int Capacity => _value.Length;
    public float[] Value => _value;
    public Vector3[] Source => _source;
    public float[] Alertness => _alertness;
    public Vector3[] AlertSource => _alertSource;
    public bool[] InCover => _inCover;

    public UnitSuppressionStore(int initialCapacity = 1024)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _value = new float[initialCapacity];
        _source = new Vector3[initialCapacity];
        _suppressed = new bool[initialCapacity];
        _panicked = new bool[initialCapacity];
        _alertness = new float[initialCapacity];
        _alertSource = new Vector3[initialCapacity];
        _inCover = new bool[initialCapacity];
        _immediateDangerTimer = new float[initialCapacity];
        _immediateDangerSource = new Vector3[initialCapacity];
    }

    public void BindSkillStore(UnitSkillStore skills) =>
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));

    public void InitializeUnit(int unitIndex)
    {
        EnsureCapacity(unitIndex + 1);
        _value[unitIndex] = 0f;
        _source[unitIndex] = Vector3.Zero;
        _suppressed[unitIndex] = false;
        _panicked[unitIndex] = false;
        _alertness[unitIndex] = 0f;
        _alertSource[unitIndex] = Vector3.Zero;
        _inCover[unitIndex] = false;
        _immediateDangerTimer[unitIndex] = 0f;
        _immediateDangerSource[unitIndex] = Vector3.Zero;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return;

        _value[unitIndex] = 0f;
        _source[unitIndex] = Vector3.Zero;
        _suppressed[unitIndex] = false;
        _panicked[unitIndex] = false;
        _alertness[unitIndex] = 0f;
        _alertSource[unitIndex] = Vector3.Zero;
        _inCover[unitIndex] = false;
        _immediateDangerTimer[unitIndex] = 0f;
        _immediateDangerSource[unitIndex] = Vector3.Zero;
    }

    public void Add(int unitIndex, float amount, Vector3 source)
    {
        if ((uint)unitIndex >= (uint)Capacity ||
            !float.IsFinite(amount) || amount <= 0f)
            return;

        float resistance = GetSuppressionResistance(unitIndex);
        float resistanceMultiplier = Math.Clamp(1f - resistance * 0.80f, 0.18f, 1f);
        float coverMultiplier = _inCover[unitIndex] ? CoverSuppressionMultiplier : 1f;
        _value[unitIndex] = MathF.Min(
            10f,
            _value[unitIndex] + amount * resistanceMultiplier * coverMultiplier);
        _source[unitIndex] = source;
        UpdateResponseState(unitIndex);
    }

    /// <summary>Raises threat awareness without adding suppression or causing panic.</summary>
    public void AddAlertness(int unitIndex, float amount, Vector3 source)
    {
        if ((uint)unitIndex >= (uint)Capacity ||
            !float.IsFinite(amount) || amount <= 0f)
            return;

        _alertness[unitIndex] = Math.Clamp(_alertness[unitIndex] + amount, 0f, 1f);
        _alertSource[unitIndex] = source;
    }

    /// <summary>Refreshes awareness while a threat is continuously visible.</summary>
    public void RefreshAlertness(int unitIndex, float value, Vector3 source)
    {
        if ((uint)unitIndex >= (uint)Capacity || !float.IsFinite(value))
            return;

        float normalized = Math.Clamp(value, 0f, 1f);
        if (normalized >= _alertness[unitIndex])
        {
            _alertness[unitIndex] = normalized;
            _alertSource[unitIndex] = source;
        }
    }

    public float GetAlertness(int unitIndex) =>
        (uint)unitIndex < (uint)Capacity ? _alertness[unitIndex] : 0f;

    public float GetSuppressionResistance(int unitIndex) =>
        (uint)unitIndex < (uint)Capacity
            ? Math.Clamp(_skills?.GetSuppressionResistance(unitIndex) ?? 0f, 0f, 1f)
            : 0f;

    public bool IsAlerted(int unitIndex) =>
        GetAlertness(unitIndex) >= AlertnessThreshold;

    public void SetInCover(int unitIndex, bool inCover)
    {
        if ((uint)unitIndex < (uint)Capacity)
            _inCover[unitIndex] = inCover;
    }

    public void MarkImmediateDanger(int unitIndex, float durationSeconds, Vector3 source)
    {
        if ((uint)unitIndex >= (uint)Capacity ||
            !float.IsFinite(durationSeconds) || durationSeconds <= 0f)
            return;

        _immediateDangerTimer[unitIndex] =
            MathF.Max(_immediateDangerTimer[unitIndex], durationSeconds);
        _immediateDangerSource[unitIndex] = source;
    }

    public bool IsInImmediateDanger(int unitIndex) =>
        (uint)unitIndex < (uint)Capacity && _immediateDangerTimer[unitIndex] > 0f;

    public Vector3 GetImmediateDangerSource(int unitIndex) =>
        (uint)unitIndex < (uint)Capacity ? _immediateDangerSource[unitIndex] : Vector3.Zero;

    public void Update(UnitStore units, float deltaTime)
    {
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            return;

        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            float suppressionDecay = _inCover[unit]
                ? SuppressionDecayPerSecond * 1.6f
                : SuppressionDecayPerSecond;
            _value[unit] = MathF.Max(0f, _value[unit] - suppressionDecay * deltaTime);
            _alertness[unit] = MathF.Max(0f, _alertness[unit] - AlertnessDecayPerSecond * deltaTime);
            _immediateDangerTimer[unit] = MathF.Max(0f, _immediateDangerTimer[unit] - deltaTime);
            if (_immediateDangerTimer[unit] <= 0f)
                _immediateDangerSource[unit] = Vector3.Zero;
            UpdateResponseState(unit);
        }
    }

    public UnitSuppressionState GetState(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return UnitSuppressionState.Calm;

        UpdateResponseState(unitIndex);
        return _panicked[unitIndex]
            ? UnitSuppressionState.Panicked
            : _suppressed[unitIndex]
                ? UnitSuppressionState.Suppressed
                : UnitSuppressionState.Calm;
    }

    public bool IsPanicked(int unitIndex) =>
        GetState(unitIndex) == UnitSuppressionState.Panicked;

    public bool IsSuppressed(int unitIndex) =>
        GetState(unitIndex) is UnitSuppressionState.Suppressed or UnitSuppressionState.Panicked;

    public float GetAccuracyMultiplier(int unitIndex)
    {
        float value = (uint)unitIndex < (uint)Capacity ? _value[unitIndex] : 0f;
        // Suppression degrades accuracy, but should not drown out skill and posture.
        return 1f + MathF.Min(0.40f, value * 0.075f);
    }

    private void UpdateResponseState(int unitIndex)
    {
        float resistance = GetSuppressionResistance(unitIndex);
        float panicEnter = PanickedThreshold + resistance * 1.8f;
        float panicExit = PanicRecoveryThreshold + resistance * 0.45f;

        if (_panicked[unitIndex])
        {
            if (_value[unitIndex] <= panicExit)
                _panicked[unitIndex] = false;
        }
        else if (_value[unitIndex] >= panicEnter)
        {
            _panicked[unitIndex] = true;
        }

        if (_suppressed[unitIndex])
        {
            if (_value[unitIndex] <= SuppressionRecoveryThreshold)
                _suppressed[unitIndex] = false;
        }
        else if (_value[unitIndex] >= SuppressedThreshold)
        {
            _suppressed[unitIndex] = true;
        }
    }

    public void EnsureCapacity(int required)
    {
        if (required <= Capacity)
            return;

        int newCapacity = Math.Max(required, Capacity * 2);
        Array.Resize(ref _value, newCapacity);
        Array.Resize(ref _source, newCapacity);
        Array.Resize(ref _suppressed, newCapacity);
        Array.Resize(ref _panicked, newCapacity);
        Array.Resize(ref _alertness, newCapacity);
        Array.Resize(ref _alertSource, newCapacity);
        Array.Resize(ref _inCover, newCapacity);
        Array.Resize(ref _immediateDangerTimer, newCapacity);
        Array.Resize(ref _immediateDangerSource, newCapacity);
    }
}
