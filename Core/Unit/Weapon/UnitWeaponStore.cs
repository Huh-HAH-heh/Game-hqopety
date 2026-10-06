using System;
using Core.Items;

namespace Core.Unit;

public sealed class UnitWeaponStore
{
    private const int DefaultUnitCapacity = 1024;

    private float[] _cooldown;
    private float[] _reloadTimer;
    private int[] _ammo;
    private uint[] _randomState;

    public int Capacity =>
        _ammo.Length / UnitInventoryStore.WeaponSlotCount;

    public float[] Cooldown => _cooldown;
    public float[] ReloadTimer => _reloadTimer;
    public int[] Ammo => _ammo;

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
        _ammo = new int[capacity];
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
            _ammo[index] = 0;
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
        _ammo[index] =
            weapon is RangedWeaponConfig ranged
                ? Math.Max(0, ranged.MagazineSize)
                : 0;
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
        _ammo[index] = 0;
    }

    public void UpdateTimers(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        for (int i = 0; i < _ammo.Length; i++)
        {
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
        Array.Resize(ref _ammo, newLength);
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

    private static int GetIndex(
        int unitIndex,
        UnitWeaponSlot slot)
    {
        return
            unitIndex *
            UnitInventoryStore.WeaponSlotCount +
            (int)slot;
    }
}
