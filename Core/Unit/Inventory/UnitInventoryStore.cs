using Core.Items;
using System;

namespace Core.Unit;

public sealed class UnitInventoryStore
{
    private const int DefaultUnitCapacity = 1024;

    public const int MaxInventorySlots = 24;
    public const int BodyZoneCount = 10;
    public const int ArmorLayerCount = 4;
    public const int WeaponSlotCount = 4;

    private ItemConfig[] _items;
    private float[] _durability;
    private float[] _maxDurability;
    private bool[] _occupied;
    private short[] _itemCount;

    // Each entry stores the local inventory slot containing the equipped item.
    // One clothing/armor item may therefore occupy several covered body zones.
    private short[] _bodyEquipment;
    private short[] _weaponEquipment;

    public int Capacity =>
        _itemCount.Length;

    public ItemConfig[] Items =>
        _items;

    public float[] Durability =>
        _durability;

    public float[] MaxDurability =>
        _maxDurability;

    public bool[] Occupied =>
        _occupied;

    public short[] ItemCount =>
        _itemCount;

    public short[] BodyEquipment =>
        _bodyEquipment;

    public short[] WeaponEquipment =>
        _weaponEquipment;

    public UnitInventoryStore(
        int initialUnitCapacity = DefaultUnitCapacity)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialUnitCapacity));

        _itemCount =
            new short[initialUnitCapacity];

        int itemCapacity =
            initialUnitCapacity *
            MaxInventorySlots;

        _items =
            new ItemConfig[itemCapacity];

        _durability =
            new float[itemCapacity];

        _maxDurability =
            new float[itemCapacity];

        _occupied =
            new bool[itemCapacity];

        _bodyEquipment =
            new short[
                initialUnitCapacity *
                BodyZoneCount *
                ArmorLayerCount];

        _weaponEquipment =
            new short[
                initialUnitCapacity *
                WeaponSlotCount];

        ClearEquipment();
    }

    public void InitializeUnit(
        int unitIndex)
    {
        EnsureUnitCapacity(
            unitIndex + 1);

        int itemStart =
            GetItemStart(unitIndex);

        Array.Clear(
            _items,
            itemStart,
            MaxInventorySlots);

        Array.Clear(
            _durability,
            itemStart,
            MaxInventorySlots);

        Array.Clear(
            _maxDurability,
            itemStart,
            MaxInventorySlots);

        Array.Clear(
            _occupied,
            itemStart,
            MaxInventorySlots);

        _itemCount[unitIndex] = 0;

        int bodyStart =
            GetBodyEquipmentStart(
                unitIndex);

        for (int i = 0;
             i < BodyZoneCount * ArmorLayerCount;
             i++)
        {
            _bodyEquipment[
                bodyStart + i] =
                -1;
        }

        int weaponStart =
            GetWeaponEquipmentStart(
                unitIndex);

        for (int i = 0;
             i < WeaponSlotCount;
             i++)
        {
            _weaponEquipment[
                weaponStart + i] =
                -1;
        }
    }

    public void ClearUnit(
        int unitIndex)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        InitializeUnit(
            unitIndex);
    }

    public int AddItem(
        int unitIndex,
        ItemConfig item,
        float maxDurability = 0f)
    {
        if (item == null)
            return -1;

        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return -1;

        int localSlot = -1;

        int itemStart =
            GetItemStart(unitIndex);

        for (int i = 0;
             i < MaxInventorySlots;
             i++)
        {
            if (_occupied[itemStart + i])
                continue;

            localSlot = i;
            break;
        }

        if (localSlot < 0)
            return -1;

        int slot =
            itemStart +
            localSlot;

        _items[slot] = item;
        _maxDurability[slot] =
            MathF.Max(
                0f,
                maxDurability);

        _durability[slot] =
            _maxDurability[slot];

        _occupied[slot] = true;

        _itemCount[unitIndex] =
            checked((short)(_itemCount[unitIndex] + 1));

        return localSlot;
    }

    public bool RemoveItem(
        int unitIndex,
        int inventorySlot)
    {
        if (!IsValidItemSlot(
                unitIndex,
                inventorySlot))
            return false;

        int index =
            GetItemStart(unitIndex) +
            inventorySlot;

        if (!_occupied[index])
            return false;

        UnequipReferences(
            unitIndex,
            (short)inventorySlot);

        _items[index] = null;
        _durability[index] = 0f;
        _maxDurability[index] = 0f;
        _occupied[index] = false;

        _itemCount[unitIndex] =
            checked((short)(
                Math.Max(
                    0,
                    _itemCount[unitIndex] - 1)));

        // Keep slot indices stable: no compaction.
        return true;
    }

    public ItemConfig? GetItem(
        int unitIndex,
        int inventorySlot)
    {
        if (!IsValidItemSlot(
                unitIndex,
                inventorySlot))
            return null;

        int index =
            GetItemStart(unitIndex) +
            inventorySlot;

        return _occupied[index]
            ? _items[index]
            : null;
    }

    public void SetBodyEquipment(
        int unitIndex,
        int zone,
        ArmorLayer layer,
        short inventorySlot)
    {
        if (!IsValidBodyZone(zone) ||
            unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        _bodyEquipment[
            GetBodyEquipmentStart(unitIndex) +
            zone * ArmorLayerCount +
            (int)layer] =
            inventorySlot;
    }

    public short GetBodyEquipment(
        int unitIndex,
        int zone,
        ArmorLayer layer)
    {
        if (!IsValidBodyZone(zone) ||
            unitIndex < 0 ||
            unitIndex >= Capacity)
            return -1;

        return _bodyEquipment[
            GetBodyEquipmentStart(unitIndex) +
            zone * ArmorLayerCount +
            (int)layer];
    }

    public void SetWeaponEquipment(
        int unitIndex,
        UnitWeaponSlot slot,
        short inventorySlot)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        _weaponEquipment[
            GetWeaponEquipmentStart(unitIndex) +
            (int)slot] =
            inventorySlot;
    }

    public short GetWeaponEquipment(
        int unitIndex,
        UnitWeaponSlot slot)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return -1;

        return _weaponEquipment[
            GetWeaponEquipmentStart(unitIndex) +
            (int)slot];
    }

    public void UnequipBodyZone(
        int unitIndex,
        int zone,
        ArmorLayer layer)
    {
        if (!IsValidBodyZone(zone) ||
            unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        _bodyEquipment[
            GetBodyEquipmentStart(unitIndex) +
            zone * ArmorLayerCount +
            (int)layer] =
            -1;
    }

    public void UnequipWeapon(
        int unitIndex,
        UnitWeaponSlot slot)
    {
        if (unitIndex < 0 ||
            unitIndex >= Capacity)
            return;

        _weaponEquipment[
            GetWeaponEquipmentStart(unitIndex) +
            (int)slot] =
            -1;
    }

    public bool IsValidItemSlot(
        int unitIndex,
        int inventorySlot)
    {
        return unitIndex >= 0 &&
            unitIndex < Capacity &&
            inventorySlot >= 0 &&
            inventorySlot < MaxInventorySlots;
    }

    public void EnsureUnitCapacity(
        int required)
    {
        if (required <= Capacity)
            return;

        int oldCapacity =
            Capacity;

        int newCapacity =
            oldCapacity * 2;

        while (newCapacity < required)
            newCapacity *= 2;

        Array.Resize(
            ref _itemCount,
            newCapacity);

        Array.Resize(
            ref _items,
            newCapacity * MaxInventorySlots);

        Array.Resize(
            ref _durability,
            newCapacity * MaxInventorySlots);

        Array.Resize(
            ref _maxDurability,
            newCapacity * MaxInventorySlots);

        Array.Resize(
            ref _occupied,
            newCapacity * MaxInventorySlots);

        Array.Resize(
            ref _bodyEquipment,
            newCapacity *
            BodyZoneCount *
            ArmorLayerCount);

        Array.Resize(
            ref _weaponEquipment,
            newCapacity *
            WeaponSlotCount);

        int oldBodyCapacity =
            oldCapacity *
            BodyZoneCount *
            ArmorLayerCount;

        for (int i = oldBodyCapacity;
             i < _bodyEquipment.Length;
             i++)
        {
            _bodyEquipment[i] = -1;
        }

        int oldWeaponCapacity =
            oldCapacity *
            WeaponSlotCount;

        for (int i = oldWeaponCapacity;
             i < _weaponEquipment.Length;
             i++)
        {
            _weaponEquipment[i] = -1;
        }
    }

    private void ClearEquipment()
    {
        Array.Fill(
            _bodyEquipment,
            (short)-1);

        Array.Fill(
            _weaponEquipment,
            (short)-1);
    }

    private void UnequipReferences(
        int unitIndex,
        short inventorySlot)
    {
        int bodyStart =
            GetBodyEquipmentStart(unitIndex);

        int bodyCount =
            BodyZoneCount *
            ArmorLayerCount;

        for (int i = 0;
             i < bodyCount;
             i++)
        {
            if (_bodyEquipment[bodyStart + i] ==
                inventorySlot)
            {
                _bodyEquipment[
                    bodyStart + i] =
                    -1;
            }
        }

        int weaponStart =
            GetWeaponEquipmentStart(unitIndex);

        for (int i = 0;
             i < WeaponSlotCount;
             i++)
        {
            if (_weaponEquipment[weaponStart + i] ==
                inventorySlot)
            {
                _weaponEquipment[
                    weaponStart + i] =
                    -1;
            }
        }
    }

    private static bool IsValidBodyZone(
        int zone)
    {
        return zone >= 0 &&
            zone < BodyZoneCount;
    }

    private static int GetItemStart(
        int unitIndex)
    {
        return unitIndex *
            MaxInventorySlots;
    }

    private static int GetBodyEquipmentStart(
        int unitIndex)
    {
        return unitIndex *
            BodyZoneCount *
            ArmorLayerCount;
    }

    private static int GetWeaponEquipmentStart(
        int unitIndex)
    {
        return unitIndex *
            WeaponSlotCount;
    }
}
