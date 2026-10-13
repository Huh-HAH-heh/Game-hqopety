using System;
using Core.Items;
using Core.Unit;

namespace RimClone.Render;

public readonly record struct RenderHealthPart(
    UnitHealthPartId Part,
    UnitHealthPartKind Kind,
    float HitPoints,
    float MaxHitPoints,
    float BleedRate);

public readonly record struct RenderInventoryItem(
    ItemConfig Config,
    float Durability,
    float MaxDurability,
    bool Equipped);

public sealed class UnitCardDetailsSnapshot
{
    public UnitId Unit { get; init; }
    public RenderHealthPart[] HealthParts { get; init; } = Array.Empty<RenderHealthPart>();
    public RenderInventoryItem[] Inventory { get; init; } = Array.Empty<RenderInventoryItem>();

    public static UnitCardDetailsSnapshot Capture(UnitSimulation simulation, UnitId id)
    {
        UnitStore units = simulation.Units;
        if (!units.TryGetIndex(id, out int unitIndex))
            return new UnitCardDetailsSnapshot { Unit = id };

        UnitHealthStore health = simulation.Health;
        int healthStart = unitIndex * health.MaxParts;
        int healthCount = health.PartCount[unitIndex];
        UnitHealthPartId[] parts = health.Part;
        UnitHealthPartKind[] kinds = health.Kind;
        float[] hitPoints = health.HitPoints;
        float[] maxHitPoints = health.MaxHitPoints;
        float[] bleedRates = health.BleedRate;
        bool[] present = health.Present;

        int visibleHealthCount = 0;
        for (int i = 0; i < healthCount; i++)
        {
            if (present[healthStart + i])
                visibleHealthCount++;
        }

        RenderHealthPart[] healthParts = new RenderHealthPart[visibleHealthCount];
        int healthWrite = 0;
        for (int i = 0; i < healthCount; i++)
        {
            int source = healthStart + i;
            if (!present[source])
                continue;

            healthParts[healthWrite++] = new RenderHealthPart(
                parts[source],
                kinds[source],
                hitPoints[source],
                maxHitPoints[source],
                bleedRates[source]);
        }

        UnitInventoryStore inventory = simulation.Inventory;
        int inventoryStart = unitIndex * UnitInventoryStore.MaxInventorySlots;
        bool[] occupied = inventory.Occupied;
        ItemConfig[] items = inventory.Items;
        int itemCount = 0;

        for (int slot = 0; slot < UnitInventoryStore.MaxInventorySlots; slot++)
        {
            if (occupied[inventoryStart + slot] && items[inventoryStart + slot] != null)
                itemCount++;
        }

        RenderInventoryItem[] inventoryItems = new RenderInventoryItem[itemCount];
        float[] durability = inventory.Durability;
        float[] maxDurability = inventory.MaxDurability;
        int itemWrite = 0;

        for (int slot = 0; slot < UnitInventoryStore.MaxInventorySlots; slot++)
        {
            int itemIndex = inventoryStart + slot;
            if (!occupied[itemIndex] || items[itemIndex] == null)
                continue;

            bool equipped = false;
            for (int weaponSlot = 0; weaponSlot < UnitInventoryStore.WeaponSlotCount; weaponSlot++)
            {
                if (inventory.GetWeaponEquipment(unitIndex, (UnitWeaponSlot)weaponSlot) == slot)
                {
                    equipped = true;
                    break;
                }
            }

            if (!equipped)
            {
                for (int zone = 0; zone < UnitInventoryStore.BodyZoneCount && !equipped; zone++)
                {
                    for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
                    {
                        if (inventory.GetBodyEquipment(unitIndex, zone, (ArmorLayer)layer) == slot)
                        {
                            equipped = true;
                            break;
                        }
                    }
                }
            }

            inventoryItems[itemWrite++] = new RenderInventoryItem(
                items[itemIndex],
                durability[itemIndex],
                maxDurability[itemIndex],
                equipped);
        }

        return new UnitCardDetailsSnapshot
        {
            Unit = id,
            HealthParts = healthParts,
            Inventory = inventoryItems
        };
    }
}
