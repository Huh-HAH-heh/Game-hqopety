using System;
using System.Numerics;
using Core.Combat;
using Core.Items;

namespace Core.Unit;

public readonly struct DamageEvent
{
    public UnitId Attacker { get; }
    public UnitId Target { get; }
    public UnitHealthPartId Part { get; }
    public Vector3 ImpactPosition { get; }
    public Vector3 Direction { get; }
    public float BaseDamage { get; }
    public float Energy { get; }
    public float InitialEnergy { get; }
    public float Penetration { get; }
    public float BluntPenetration { get; }
    public DamageType DamageType { get; }
    public float BleedChance { get; }

    public DamageEvent(
        UnitId attacker,
        UnitId target,
        UnitHealthPartId part,
        Vector3 impactPosition,
        Vector3 direction,
        float baseDamage,
        float energy,
        float initialEnergy,
        float penetration,
        float bluntPenetration,
        DamageType damageType,
        float bleedChance)
    {
        Attacker = attacker;
        Target = target;
        Part = part;
        ImpactPosition = impactPosition;
        Direction = direction;
        BaseDamage = baseDamage;
        Energy = energy;
        InitialEnergy = initialEnergy;
        Penetration = penetration;
        BluntPenetration = bluntPenetration;
        DamageType = damageType;
        BleedChance = bleedChance;
    }
}

public readonly struct ProjectileDamageResult
{
    public float DamageApplied { get; }
    public float RemainingEnergy { get; }
    public float RemainingPenetration { get; }
    public bool ProjectileStopped { get; }

    public ProjectileDamageResult(
        float damageApplied,
        float remainingEnergy,
        float remainingPenetration,
        bool projectileStopped)
    {
        DamageApplied = damageApplied;
        RemainingEnergy = remainingEnergy;
        RemainingPenetration = remainingPenetration;
        ProjectileStopped = projectileStopped;
    }
}

public sealed class DamageSystem
{
    public ProjectileDamageResult ApplyProjectile(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitHealthStore health,
        UnitHealthSystem healthSystem,
        in DamageEvent damageEvent)
    {
        if (!units.TryGetIndex(
                damageEvent.Target,
                out int targetIndex))
        {
            return new ProjectileDamageResult(
                0f,
                0f,
                0f,
                true);
        }

        float energy =
            MathF.Max(
                0f,
                damageEvent.Energy);

        float penetration =
            MathF.Max(
                0f,
                damageEvent.Penetration);

        float energyRatio =
            damageEvent.InitialEnergy > 0f
                ? Math.Clamp(
                    energy /
                    damageEvent.InitialEnergy,
                    0f,
                    1f)
                : 0f;

        float damage =
            damageEvent.BaseDamage *
            (0.35f +
             0.65f *
             MathF.Sqrt(energyRatio));

        bool stopped = false;

        int zone =
            MapBodyPartToArmorZone(
                damageEvent.Part);

        if (zone >= 0)
        {
            for (int layer =
                     UnitInventoryStore.ArmorLayerCount - 1;
                 layer >= 0;
                 layer--)
            {
                short itemSlot =
                    inventory.GetBodyEquipment(
                        targetIndex,
                        zone,
                        (ArmorLayer)layer);

                if (itemSlot < 0)
                    continue;

                ArmorConfig? armor =
                    inventory.GetItem(
                        targetIndex,
                        itemSlot)
                    as ArmorConfig;

                if (armor == null)
                    continue;

                int durabilityIndex =
                    targetIndex *
                    UnitInventoryStore.MaxInventorySlots +
                    itemSlot;

                if (inventory.Durability[
                        durabilityIndex] <= 0f)
                {
                    continue;
                }

                ArmorResolution resolution =
                    ArmorResolver.ResolveLayer(
                        damageEvent.DamageType,
                        damage,
                        energy,
                        penetration,
                        damageEvent.BluntPenetration,
                        armor);

                damage =
                    resolution.Damage;

                energy =
                    resolution.Energy;

                penetration =
                    resolution.Penetration;

                if (resolution.ArmorDamage > 0f)
                {
                    inventory.Durability[
                        durabilityIndex] =
                        MathF.Max(
                            0f,
                            inventory.Durability[
                                durabilityIndex] -
                            resolution.ArmorDamage);
                }

                if (resolution.BluntImpactDamage > 0f)
                {
                    UnitHealthPartId bluntPart =
                        GetOuterPart(
                            damageEvent.Part);

                    healthSystem.ApplyDamage(
                        units,
                        health,
                        damageEvent.Target,
                        bluntPart,
                        resolution.BluntImpactDamage);
                }

                if (resolution.Stopped)
                {
                    stopped = true;
                    break;
                }
            }
        }

        if (!stopped)
        {
            energy =
                MathF.Max(
                    0f,
                    energy -
                    GetBodyEnergyLoss(
                        damageEvent.Part));

            penetration =
                MathF.Max(
                    0f,
                    penetration -
                    GetBodyPenetrationLoss(
                        damageEvent.Part));

            stopped =
                energy <= 1f ||
                penetration <= 0.01f;
        }

        healthSystem.ApplyDamage(
            units,
            health,
            damageEvent.Target,
            damageEvent.Part,
            damage);

        if (damage > 0f &&
            damageEvent.BleedChance > 0f &&
            damageEvent.DamageType != DamageType.Blunt)
        {
            float bleed =
                damage *
                Math.Clamp(
                    damageEvent.BleedChance,
                    0f,
                    1f) *
                0.05f;

            healthSystem.AddBleed(
                units,
                health,
                damageEvent.Target,
                damageEvent.Part,
                bleed);
        }

        return new ProjectileDamageResult(
            damage,
            energy,
            penetration,
            stopped);
    }

    private static float GetBodyEnergyLoss(
        UnitHealthPartId part)
    {
        return part switch
        {
            UnitHealthPartId.Head => 170f,
            UnitHealthPartId.Torso => 190f,
            UnitHealthPartId.LeftArm or
            UnitHealthPartId.RightArm => 90f,
            UnitHealthPartId.LeftHand or
            UnitHealthPartId.RightHand => 55f,
            UnitHealthPartId.LeftLeg or
            UnitHealthPartId.RightLeg => 105f,
            UnitHealthPartId.LeftFoot or
            UnitHealthPartId.RightFoot => 50f,
            _ => 160f
        };
    }

    private static float GetBodyPenetrationLoss(
        UnitHealthPartId part)
    {
        return part switch
        {
            UnitHealthPartId.Head => 10f,
            UnitHealthPartId.Torso => 12f,
            UnitHealthPartId.LeftArm or
            UnitHealthPartId.RightArm => 6f,
            UnitHealthPartId.LeftHand or
            UnitHealthPartId.RightHand => 4f,
            UnitHealthPartId.LeftLeg or
            UnitHealthPartId.RightLeg => 7f,
            UnitHealthPartId.LeftFoot or
            UnitHealthPartId.RightFoot => 4f,
            _ => 8f
        };
    }

    private static UnitHealthPartId GetOuterPart(
        UnitHealthPartId part)
    {
        return part switch
        {
            UnitHealthPartId.Brain => UnitHealthPartId.Head,
            UnitHealthPartId.Heart or
            UnitHealthPartId.LeftLung or
            UnitHealthPartId.RightLung or
            UnitHealthPartId.Stomach or
            UnitHealthPartId.Liver or
            UnitHealthPartId.LeftKidney or
            UnitHealthPartId.RightKidney =>
                UnitHealthPartId.Torso,
            _ => part
        };
    }

    private static int MapBodyPartToArmorZone(
        UnitHealthPartId part)
    {
        return part switch
        {
            UnitHealthPartId.Head or
            UnitHealthPartId.Brain => 0,

            UnitHealthPartId.Torso or
            UnitHealthPartId.Heart or
            UnitHealthPartId.LeftLung or
            UnitHealthPartId.RightLung or
            UnitHealthPartId.Stomach or
            UnitHealthPartId.Liver or
            UnitHealthPartId.LeftKidney or
            UnitHealthPartId.RightKidney => 1,

            UnitHealthPartId.LeftArm => 2,
            UnitHealthPartId.RightArm => 3,
            UnitHealthPartId.LeftHand => 4,
            UnitHealthPartId.RightHand => 5,
            UnitHealthPartId.LeftLeg => 6,
            UnitHealthPartId.RightLeg => 7,
            UnitHealthPartId.LeftFoot => 8,
            UnitHealthPartId.RightFoot => 9,

            _ => -1
        };
    }
}
