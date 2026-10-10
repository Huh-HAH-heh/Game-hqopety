using System;
using Core.Items;
using Core.Combat;

namespace Core.Unit;

public sealed class UnitWeaponStore
{
    private const int DefaultUnitCapacity = 1024;
    private const int MaxAmmoTypes = 8;

    private float[] _cooldown;
    private float[] _reloadTimer;
    private float[] _recoil;
    private int[] _ammo;
    private int[] _reserveAmmo;
    private int[] _reserveAmmoByType;
    private byte[] _ammoType;
    private bool[] _reloading;
    private byte[] _burstRemaining;
    private float[] _burstTimer;
    private FireMode[] _fireMode;
    private AimMode[] _aimMode;
    private TargetMode[] _targetMode;
    private float[] _aimTimer;
    private UnitId[] _aimTarget;
    private UnitId[] _burstTarget;
    private uint[] _randomState;

    public int Capacity =>
        _ammo.Length / UnitInventoryStore.WeaponSlotCount;

    public float[] Cooldown => _cooldown;
    public float[] ReloadTimer => _reloadTimer;
    public float[] Recoil => _recoil;
    public int[] Ammo => _ammo;
    public int[] ReserveAmmo => _reserveAmmo;
    public byte[] CurrentAmmoType => _ammoType;
    public bool[] Reloading => _reloading;
    public byte[] BurstRemaining => _burstRemaining;
    public float[] BurstTimer => _burstTimer;
    public UnitId[] BurstTarget => _burstTarget;
    public FireMode[] CurrentFireMode => _fireMode;
    public AimMode[] CurrentAimMode => _aimMode;
    public TargetMode[] CurrentTargetMode => _targetMode;
    public float[] AimTimer => _aimTimer;
    public UnitId[] AimTarget => _aimTarget;

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
        _reserveAmmoByType = new int[capacity * MaxAmmoTypes];
        _ammoType = new byte[capacity];
        _reloading = new bool[capacity];
        _burstRemaining = new byte[capacity];
        _burstTimer = new float[capacity];
        _burstTarget = new UnitId[capacity];
        _fireMode = new FireMode[capacity];
        _aimMode = new AimMode[capacity];
        _targetMode = new TargetMode[capacity];
        _aimTimer = new float[capacity];
        _aimTarget = new UnitId[capacity];
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
            _ammoType[index] = 0;
            ClearAmmoReserves(index);
            _reloading[index] = false;
            _burstRemaining[index] = 0;
            _burstTimer[index] = 0f;
            _burstTarget[index] = default;
            _fireMode[index] = FireMode.Single;
            _aimMode[index] = AimMode.AimedShot;
            _targetMode[index] = TargetMode.Automatic;
            _aimTimer[index] = 0f;
            _aimTarget[index] = default;
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

    public void CancelActions(int unitIndex)
    {
        if (unitIndex < 0 || unitIndex >= Capacity)
            return;

        int start = unitIndex * UnitInventoryStore.WeaponSlotCount;

        for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
        {
            int index = start + slot;
            _burstRemaining[index] = 0;
            _burstTimer[index] = 0f;
            _burstTarget[index] = default;
            _aimTimer[index] = 0f;
            _aimTarget[index] = default;
        }
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
            weapon is RangedWeaponConfig rangedConfig
                ? Math.Max(0, (int)rangedConfig.MagazineSize)
                : 0;

        _ammo[index] = magazineSize;
        _ammoType[index] = 0;
        ClearAmmoReserves(index);

        if (weapon is RangedWeaponConfig rangedWeapon &&
            rangedWeapon.AmmoSet != null)
        {
            int count =
                Math.Min(
                    MaxAmmoTypes,
                    rangedWeapon.AmmoSet.Count);

            for (int ammoType = 0;
                 ammoType < count;
                 ammoType++)
            {
                SetAmmoReserve(
                    index,
                    ammoType,
                    magazineSize * 3);
            }
        }
        else
        {
            SetAmmoReserve(
                index,
                0,
                magazineSize * 3);
        }

        _reserveAmmo[index] =
            GetAmmoReserve(
                index,
                _ammoType[index]);

        _reloading[index] = false;
        _burstRemaining[index] = 0;
        _burstTimer[index] = 0f;
        _burstTarget[index] = default;
        _aimTimer[index] = 0f;
        _aimTarget[index] = default;

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
        _ammoType[index] = 0;
        ClearAmmoReserves(index);
        _reloading[index] = false;
        _burstRemaining[index] = 0;
        _burstTimer[index] = 0f;
        _burstTarget[index] = default;
        _aimTimer[index] = 0f;
        _aimTarget[index] = default;
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
        SetAmmoReserve(
            index,
            _ammoType[index],
            _reserveAmmo[index]);
        _reloading[index] = false;
    }

    public void UpdateTimers(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        for (int i = 0; i < _ammo.Length; i++)
        {
            _recoil[i] = MathF.Max(0f, _recoil[i] - deltaTime * 0.75f);
            _aimTimer[i] = MathF.Min(10f, _aimTimer[i] + deltaTime);

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

    public void SetAimTarget(int unitIndex, UnitWeaponSlot slot, UnitId target, bool reset)
    {
        int index = GetIndex(unitIndex, slot);

        if (reset || _aimTarget[index] != target)
        {
            _aimTarget[index] = target;
            _aimTimer[index] = 0f;
        }
    }

    public void ClearAim(int unitIndex, UnitWeaponSlot slot)
    {
        int index = GetIndex(unitIndex, slot);
        _aimTarget[index] = default;
        _aimTimer[index] = 0f;
    }

    public bool SelectAmmunition(
        int unitIndex,
        UnitWeaponSlot slot,
        int ammoType)
    {
        int index =
            GetIndex(
                unitIndex,
                slot);

        if (_reloading[index] ||
            _ammo[index] > 0 ||
            ammoType < 0 ||
            ammoType >= MaxAmmoTypes)
        {
            return false;
        }

        _ammoType[index] =
            (byte)ammoType;

        _reserveAmmo[index] =
            GetAmmoReserve(
                index,
                ammoType);

        _aimTimer[index] = 0f;
        _aimTarget[index] = default;

        return true;
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
        _aimTimer[index] = 0f;
        _aimTarget[index] = default;
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
        int value =
            Math.Max(
                0,
                _reserveAmmo[index] + amount);

        _reserveAmmo[index] = value;

        SetAmmoReserve(
            index,
            _ammoType[index],
            value);
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
        Array.Resize(ref _reserveAmmoByType, newLength * MaxAmmoTypes);
        Array.Resize(ref _ammoType, newLength);
        Array.Resize(ref _reloading, newLength);
        Array.Resize(ref _burstRemaining, newLength);
        Array.Resize(ref _burstTimer, newLength);
        Array.Resize(ref _burstTarget, newLength);
        Array.Resize(ref _fireMode, newLength);
        Array.Resize(ref _aimMode, newLength);
        Array.Resize(ref _targetMode, newLength);
        Array.Resize(ref _aimTimer, newLength);
        Array.Resize(ref _aimTarget, newLength);
        Array.Resize(ref _randomState, newLength);

        for (int i = oldLength; i < newLength; i++)
            _randomState[i] = Seed(i);
    }

    public void AddReserveAmmo(
        int unitIndex,
        UnitWeaponSlot slot,
        int ammoType,
        int amount)
    {
        if (amount <= 0 ||
            ammoType < 0 ||
            ammoType >= MaxAmmoTypes)
        {
            return;
        }

        int index =
            GetIndex(
                unitIndex,
                slot);

        SetAmmoReserve(
            index,
            ammoType,
            GetAmmoReserve(index, ammoType) + amount);

        if (ammoType == _ammoType[index])
        {
            _reserveAmmo[index] =
                GetAmmoReserve(
                    index,
                    ammoType);
        }
    }

    private int GetAmmoReserve(
        int weaponIndex,
        int ammoType)
    {
        return _reserveAmmoByType[
            weaponIndex * MaxAmmoTypes +
            ammoType];
    }

    private void SetAmmoReserve(
        int weaponIndex,
        int ammoType,
        int value)
    {
        _reserveAmmoByType[
            weaponIndex * MaxAmmoTypes +
            ammoType] =
            Math.Max(
                0,
                value);
    }

    private void ClearAmmoReserves(
        int weaponIndex)
    {
        Array.Clear(
            _reserveAmmoByType,
            weaponIndex * MaxAmmoTypes,
            MaxAmmoTypes);
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
