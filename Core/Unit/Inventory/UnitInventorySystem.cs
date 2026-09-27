using Core.Items;
using System;

namespace Core.Unit;

public readonly struct UnitDamageResult
{
    public float RawDamage { get; }
    public float FinalDamage { get; }
    public float ArmorAbsorbed { get; }
    public float ArmorDamage { get; }

    public UnitDamageResult(
        float rawDamage,
        float finalDamage,
        float armorAbsorbed,
        float armorDamage)
    {
        RawDamage = rawDamage;
        FinalDamage = finalDamage;
        ArmorAbsorbed = armorAbsorbed;
        ArmorDamage = armorDamage;
    }
}

public sealed class UnitInventorySystem
{
    public int AddItem(
        UnitInventoryStore inventory,
        UnitId unitId,
        ItemConfig item,
        float maxDurability = 0f)
    {
        if (item == null ||
            !unitId.IsValid)
            return -1;

        return inventory.AddItem(
            unitId.Index,
            item,
            maxDurability);
    }

    public bool EquipArmor(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitId unitId,
        int inventorySlot)
    {
        if (!units.TryGetIndex(
                unitId,
                out int unitIndex))
            return false;

        ArmorConfig? armor =
            inventory.GetItem(
                unitIndex,
                inventorySlot) as ArmorConfig;

        if (armor == null)
            return false;

        ArmorCoverage coverage =
            NormalizeCoverage(
                armor);

        if (coverage == ArmorCoverage.None)
            return false;

        // One item is allowed to cover many body zones, but cannot conflict
        // with an existing item on the same zone and armor layer.
        for (int zone = 0;
             zone < UnitInventoryStore.BodyZoneCount;
             zone++)
        {
            ArmorCoverage zoneFlag =
                GetZoneFlag(zone);

            if ((coverage & zoneFlag) == 0)
                continue;

            short occupied =
                inventory.GetBodyEquipment(
                    unitIndex,
                    zone,
                    armor.Layer);

            if (occupied >= 0 &&
                occupied != inventorySlot)
                return false;
        }

        for (int zone = 0;
             zone < UnitInventoryStore.BodyZoneCount;
             zone++)
        {
            ArmorCoverage zoneFlag =
                GetZoneFlag(zone);

            if ((coverage & zoneFlag) == 0)
                continue;

            inventory.SetBodyEquipment(
                unitIndex,
                zone,
                armor.Layer,
                (short)inventorySlot);
        }

        return true;
    }

    public bool EquipWeapon(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitId unitId,
        int inventorySlot,
        UnitWeaponSlot slot)
    {
        if (!units.TryGetIndex(
                unitId,
                out int unitIndex))
            return false;

        WeaponConfig? weapon =
            inventory.GetItem(
                unitIndex,
                inventorySlot) as WeaponConfig;

        if (weapon == null ||
            !CanUseWeaponSlot(
                weapon.Capabilities,
                slot))
            return false;

        short occupied =
            inventory.GetWeaponEquipment(
                unitIndex,
                slot);

        if (occupied >= 0 &&
            occupied != inventorySlot)
            return false;

        inventory.SetWeaponEquipment(
            unitIndex,
            slot,
            (short)inventorySlot);

        return true;
    }

    public UnitDamageResult ResolveIncomingDamage(
        UnitHealthPartId part,
        float rawDamage,
        UnitId unitId,
        UnitHealthStore health,
        UnitInventoryStore inventory)
    {
        if (rawDamage <= 0f ||
            !unitId.IsValid)
        {
            return new UnitDamageResult(
                rawDamage,
                0f,
                0f,
                0f);
        }

        int unitIndex =
            unitId.Index;

        int zone =
            MapHealthPartToZone(
                part);

        if (zone < 0)
        {
            return new UnitDamageResult(
                rawDamage,
                rawDamage,
                0f,
                0f);
        }

        float remaining =
            rawDamage;

        float absorbedTotal = 0f;
        float armorDamageTotal = 0f;

        // Outer -> inner: multiple clothing/armor layers can protect the
        // same anatomical location.
        for (int layer =
                 UnitInventoryStore.ArmorLayerCount - 1;
             layer >= 0;
             layer--)
        {
            short itemSlot =
                inventory.GetBodyEquipment(
                    unitIndex,
                    zone,
                    (ArmorLayer)layer);

            if (itemSlot < 0)
                continue;

            ArmorConfig? armor =
                inventory.GetItem(
                    unitIndex,
                    itemSlot) as ArmorConfig;

            if (armor == null)
                continue;

            if (inventory.Durability[
                    unitIndex *
                    UnitInventoryStore.MaxInventorySlots +
                    itemSlot] <= 0f)
                continue;

            float reduction =
                Math.Clamp(
                    armor.DamageAbsorption,
                    0f,
                    0.95f);

            float absorbed =
                remaining *
                reduction;

            remaining -= absorbed;
            absorbedTotal += absorbed;

            float armorDamage =
                absorbed *
                Math.Clamp(
                    armor.ArmorDamageCoefficient,
                    0f,
                    1f);

            int durabilityIndex =
                unitIndex *
                UnitInventoryStore.MaxInventorySlots +
                itemSlot;

            inventory.Durability[
                durabilityIndex] =
                MathF.Max(
                    0f,
                    inventory.Durability[
                        durabilityIndex] -
                    armorDamage);

            armorDamageTotal +=
                armorDamage;

            if (remaining <= 0.001f)
            {
                remaining = 0f;
                break;
            }
        }

        return new UnitDamageResult(
            rawDamage,
            remaining,
            absorbedTotal,
            armorDamageTotal);
    }

    private static ArmorCoverage NormalizeCoverage(
        ArmorConfig armor)
    {
        if (armor.Coverage != ArmorCoverage.None)
            return armor.Coverage;

        return armor.ProtectedZone switch
        {
            0 => ArmorCoverage.Head,
            1 => ArmorCoverage.Torso,
            2 => ArmorCoverage.LeftArm |
                 ArmorCoverage.RightArm |
                 ArmorCoverage.LeftHand |
                 ArmorCoverage.RightHand,
            3 => ArmorCoverage.LeftLeg |
                 ArmorCoverage.RightLeg |
                 ArmorCoverage.LeftFoot |
                 ArmorCoverage.RightFoot,
            _ => ArmorCoverage.None
        };
    }

    private static ArmorCoverage GetZoneFlag(
        int zone)
    {
        return zone switch
        {
            0 => ArmorCoverage.Head,
            1 => ArmorCoverage.Torso,
            2 => ArmorCoverage.LeftArm,
            3 => ArmorCoverage.RightArm,
            4 => ArmorCoverage.LeftHand,
            5 => ArmorCoverage.RightHand,
            6 => ArmorCoverage.LeftLeg,
            7 => ArmorCoverage.RightLeg,
            8 => ArmorCoverage.LeftFoot,
            9 => ArmorCoverage.RightFoot,
            _ => ArmorCoverage.None
        };
    }

    private static int MapHealthPartToZone(
        UnitHealthPartId part)
    {
        return part switch
        {
            UnitHealthPartId.Head =>
                0,

            UnitHealthPartId.Brain =>
                0,

            UnitHealthPartId.Torso =>
                1,

            UnitHealthPartId.Heart =>
                1,

            UnitHealthPartId.LeftLung =>
                1,

            UnitHealthPartId.RightLung =>
                1,

            UnitHealthPartId.Stomach =>
                1,

            UnitHealthPartId.Liver =>
                1,

            UnitHealthPartId.LeftKidney =>
                1,

            UnitHealthPartId.RightKidney =>
                1,

            UnitHealthPartId.LeftArm =>
                2,

            UnitHealthPartId.LeftHand =>
                4,

            UnitHealthPartId.RightArm =>
                3,

            UnitHealthPartId.RightHand =>
                5,

            UnitHealthPartId.LeftLeg =>
                6,

            UnitHealthPartId.LeftFoot =>
                8,

            UnitHealthPartId.RightLeg =>
                7,

            UnitHealthPartId.RightFoot =>
                9,

            _ =>
                -1
        };
    }

    private static bool CanUseWeaponSlot(
        WeaponCapabilities capabilities,
        UnitWeaponSlot slot)
    {
        return slot switch
        {
            UnitWeaponSlot.Primary =>
                capabilities !=
                WeaponCapabilities.None,

            UnitWeaponSlot.Secondary =>
                capabilities !=
                WeaponCapabilities.None,

            UnitWeaponSlot.Melee =>
                (capabilities &
                 WeaponCapabilities.Melee) != 0,

            UnitWeaponSlot.Utility =>
                capabilities !=
                WeaponCapabilities.None,

            _ =>
                false
        };
    }
}
