using System;
using Core.Items;
using Core.Combat;

namespace Core.Unit;

public sealed class UnitWeaponStore
{
    private const int DefaultUnitCapacity = 1024;

    private float[] _cooldown;
    private float[] _reloadTimer;
    private float[] _recoil;
    private int[] _ammo;
    private int[] _reserveAmmo;
    private bool[] _reloading;
    private byte[] _burstRemaining;
    private float[] _burstTimer;
    private FireMode[] _fireMode;
    private AimMode[] _aimMode;
    private TargetMode[] _targetMode;
    private UnitId[] _burstTarget;
    private uint[] _randomState;

    public int Capacity =>
        _ammo.Length / UnitInventoryStore.WeaponSlotCount;

    public float[] Cooldown => _cooldown;
    public float[] ReloadTimer => _reloadTimer;
    public float[] Recoil => _recoil;
    public int[] Ammo => _ammo;
    public int[] ReserveAmmo => _reserveAmmo;
    public bool[] Reloading => _reloading;
    public byte[] BurstRemaining => _burstRemaining;
    public float[] BurstTimer => _burstTimer;
    public UnitId[] BurstTarget => _burstTarget;
    public FireMode[] CurrentFireMode => _fireMode;
    public AimMode[] CurrentAimMode => _aimMode;
    public TargetMode[] CurrentTargetMode => _targetMode;

    public UnitWeaponStore(
        int initialUnitCapacity = DefaultUnitCapacity)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialUnitCapacity));

        int capacity =
            initialUnitCapacity *
            UnitInventoryStore.WeaponSlotCount;

        _cooldown = new float[capacity];
        _reloadTimer = new float[capacity];
        _recoil = new float[capacity];
        _ammo = new int[capacity];
        _reserveAmmo = new int[capacity];
        _reloading = new bool[capacity];
        _burstRemaining = new byte[capacity];
        _burstTimer = new float[capacity];
        _burstTarget = new UnitId[capacity];
        _fireMode = new FireMode[capacity];
        _aimMode = new AimMode[capacity];
        _targetMode = new TargetMode[capacity];
        _randomState = new uint[capacity];

        for (int i = 0; i < capacity; i++)
            _randomState[i] = Seed(i);
    }

    public void InitializeUnit(int unitIndex)
    {
        EnsureUnitCapacity(unitIndex + 1);

        int start =
            unitIndex *
            UnitInventoryStore.WeaponSlotCount;

        for (int i = 0;
             i < UnitInventoryStore.WeaponSlotCount;
             i++)
        {
            int index = start + i;
            _cooldown[index] = 0f;
            _reloadTimer[index] = 0f;
            _recoil[index] = 0f;
            _ammo[index] = 0;
            _reserveAmmo[index] = 0;
            _reloading[index] = false;
            _burstRemaining[index] = 0;
            _burstTimer[index] = 0f;
            _burstTarget[index] = default;
            _fireMode[index] = FireMode.Single;
            _aimMode[index] = AimMode.AimedShot;
            _targetMode[index] = TargetMode.Automatic;
            _randomState[index] = Seed(index);
        }
    }

    public void ClearUnit(int unitIndex)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        InitializeUnit(unitIndex);
    }

    public void ConfigureSlot(
        int unitIndex,
        UnitWeaponSlot slot,
        WeaponConfig weapon)
    {
        EnsureUnitCapacity(unitIndex + 1);

        int index =
            GetIndex(unitIndex, slot);

        _cooldown[index] = 0f;
        _reloadTimer[index] = 0f;
        _recoil[index] = 0f;
        int magazineSize =
            weapon is RangedWeaponConfig ranged
                ? Math.Max(0, (int)ranged.MagazineSize)
                : 0;

        _ammo[index] = magazineSize;
        _reserveAmmo[index] = magazineSize * 3;
        _reloading[index] = false;
        _burstRemaining[index] = 0;
        _burstTimer[index] = 0f;
        _burstTarget[index] = default;

        if (weapon is RangedWeaponConfig ranged)
        {
            _fireMode[index] = ranged.DefaultFireMode;
            _aimMode[index] = ranged.DefaultAimMode;
            _targetMode[index] = ranged.DefaultTargetMode;
        }
        else
        {
            _fireMode[index] = FireMode.Single;
            _aimMode[index] = AimMode.AimedShot;
            _targetMode[index] = TargetMode.Automatic;
        }
    }

    public void ClearSlot(
        int unitIndex,
        UnitWeaponSlot slot)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        int index =
            GetIndex(unitIndex, slot);

        _cooldown[index] = 0f;
        _reloadTimer[index] = 0f;
        _recoil[index] = 0f;
        _ammo[index] = 0;
        _reserveAmmo[index] = 0;
        _reloading[index] = false;
        _burstRemaining[index] = 0;
        _burstTimer[index] = 0f;
        _burstTarget[index] = default;
        _fireMode[index] = FireMode.Single;
        _aimMode[index] = AimMode.AimedShot;
        _targetMode[index] = TargetMode.Automatic;
    }

    public void StartReload(
        int unitIndex,
        UnitWeaponSlot slot,
        float reloadTime)
    {
        int index = GetIndex(unitIndex, slot);

        if (_ammo[index] > 0 ||
            _reloading[index] ||
            _reserveAmmo[index] <= 0 ||
            reloadTime <= 0f)
            return;

        _reloadTimer[index] = reloadTime;
        _reloading[index] = true;
    }

    public void FinishReload(
        int unitIndex,
        UnitWeaponSlot slot,
        int magazineSize)
    {
        int index = GetIndex(unitIndex, slot);

        if (!_reloading[index])
            return;

        if (_reloadTimer[index] > 0f)
            return;

        int missing =
            Math.Max(
                0,
                magazineSize - _ammo[index]);

        int loaded =
            Math.Min(
                missing,
                _reserveAmmo[index]);

        _ammo[index] += loaded;
        _reserveAmmo[index] -= loaded;
        _reloading[index] = false;
    }

    public void UpdateTimers(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        for (int i = 0; i < _ammo.Length; i++)
        {
            _recoil[i] = MathF.Max(0f, _recoil[i] - deltaTime * 0.75f);

            _cooldown[i] =
                MathF.Max(
                    0f,
                    _cooldown[i] - deltaTime);

            _reloadTimer[i] =
                MathF.Max(
                    0f,
                    _reloadTimer[i] - deltaTime);
        }
    }

    public void CycleFireMode(int unitIndex, UnitWeaponSlot slot)
    {
        int index = GetIndex(unitIndex, slot);
        _fireMode[index] = _fireMode[index] switch
        {
            FireMode.Single => FireMode.Burst,
            FireMode.Burst => FireMode.Auto,
            _ => FireMode.Single
        };
        _burstRemaining[index] = 0;
        _burstTimer[index] = 0f;
    }

    public void CycleAimMode(int unitIndex, UnitWeaponSlot slot)
    {
        int index = GetIndex(unitIndex, slot);
        _aimMode[index] = _aimMode[index] switch
        {
            AimMode.AimedShot => AimMode.Snapshot,
            AimMode.Snapshot => AimMode.SuppressFire,
            _ => AimMode.AimedShot
        };
    }

    public void CycleTargetMode(int unitIndex, UnitWeaponSlot slot)
    {
        int index = GetIndex(unitIndex, slot);
        _targetMode[index] = _targetMode[index] switch
        {
            TargetMode.Automatic => TargetMode.Torso,
            TargetMode.Torso => TargetMode.Head,
            TargetMode.Head => TargetMode.Legs,
            _ => TargetMode.Automatic
        };
    }

    public void AddRecoil(
        int unitIndex,
        UnitWeaponSlot slot,
        float amount)
    {
        int index = GetIndex(unitIndex, slot);
        _recoil[index] = MathF.Min(4f, _recoil[index] + MathF.Max(0f, amount));
    }

    public void StartBurst(
        int unitIndex,
        UnitWeaponSlot slot,
        UnitId target,
        int remaining,
        float interval)
    {
        int index = GetIndex(unitIndex, slot);
        _burstTarget[index] = target;
        _burstRemaining[index] = (byte)Math.Clamp(remaining, 0, byte.MaxValue);
        _burstTimer[index] = MathF.Max(0f, interval);
    }

    public void AdvanceBurstTimer(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        for (int i = 0; i < _burstTimer.Length; i++)
            _burstTimer[i] = MathF.Max(0f, _burstTimer[i] - deltaTime);
    }

    public uint NextRandom(
        int unitIndex,
        UnitWeaponSlot slot)
    {
        int index =
            GetIndex(unitIndex, slot);

        uint state =
            _randomState[index];

        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;

        _randomState[index] =
            state == 0
                ? 1u
                : state;

        return _randomState[index];
    }

    public void AddReserveAmmo(
        int unitIndex,
        UnitWeaponSlot slot,
        int amount)
    {
        if (amount <= 0)
            return;

        int index = GetIndex(unitIndex, slot);
        _reserveAmmo[index] =
            Math.Max(
                0,
                _reserveAmmo[index] + amount);
    }

    public void EnsureUnitCapacity(int required)
    {
        if (required <= Capacity)
            return;

        int oldCapacity = Capacity;
        int newCapacity = oldCapacity * 2;

        while (newCapacity < required)
            newCapacity *= 2;

        int oldLength = _ammo.Length;
        int newLength =
            newCapacity *
            UnitInventoryStore.WeaponSlotCount;

        Array.Resize(ref _cooldown, newLength);
        Array.Resize(ref _reloadTimer, newLength);
        Array.Resize(ref _recoil, newLength);
        Array.Resize(ref _ammo, newLength);
        Array.Resize(ref _reserveAmmo, newLength);
        Array.Resize(ref _reloading, newLength);
        Array.Resize(ref _burstRemaining, newLength);
        Array.Resize(ref _burstTimer, newLength);
        Array.Resize(ref _burstTarget, newLength);
        Array.Resize(ref _fireMode, newLength);
        Array.Resize(ref _aimMode, newLength);
        Array.Resize(ref _targetMode, newLength);
        Array.Resize(ref _randomState, newLength);

        for (int i = oldLength; i < newLength; i++)
            _randomState[i] = Seed(i);
    }

    private static uint Seed(int value)
    {
        uint x =
            unchecked(
                (uint)(value + 1) *
                747796405u);

        x ^= x >> 16;
        x *= 2246822519u;
        x ^= x >> 13;

        return x == 0
            ? 1u
            : x;
    }

    public static int GetIndex(
        int unitIndex,
        UnitWeaponSlot slot)
    {
        return
            unitIndex *
            UnitInventoryStore.WeaponSlotCount +
            (int)slot;
    }
}
