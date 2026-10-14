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
    public int RegionIndex { get; }
    public float HitRadius { get; }
    public int BindingIndex { get; }

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
        float bleedChance,
        int regionIndex = -1,
        float hitRadius = 0f,
        int bindingIndex = -1)
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
        RegionIndex = regionIndex;
        HitRadius = MathF.Max(0f, hitRadius);
        BindingIndex = bindingIndex;
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
        in DamageEvent damageEvent,
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null)
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

        float hitTolerance = damageEvent.HitRadius + 0.01f;
        int regionIndex = damageEvent.RegionIndex;
        int bindingIndex = damageEvent.BindingIndex;
        HitRegionPartBinding anatomyBinding = default;
        bool hasAnatomyBinding = bindingIndex >= 0;

        if (hasAnatomyBinding)
        {
            if (regionIndex < 0 ||
                !UnitHitSystem.IsBindingInRegion(
                    units, targetIndex, regionIndex, bindingIndex, hitRegions) ||
                !UnitHitSystem.IsPointInsideRegion(
                    units, targetIndex, regionIndex, damageEvent.ImpactPosition, hitTolerance, hitRegions) ||
                !UnitHitSystem.TryGetBinding(
                    units, targetIndex, regionIndex, bindingIndex, out anatomyBinding, hitRegions))
            {
                return new ProjectileDamageResult(0f, 0f, 0f, true);
            }
        }
        else
        {
            if (regionIndex < 0)
            {
                regionIndex = damageEvent.Part == UnitHealthPartId.None
                    ? UnitHitSystem.FindRegionAtPoint(
                        units, targetIndex, damageEvent.ImpactPosition, hitTolerance, hitRegions)
                    : UnitHitSystem.FindRegionAtPoint(
                        units, targetIndex, damageEvent.Part,
                        damageEvent.ImpactPosition, hitTolerance, hitRegions);
            }
            else
            {
                bool inside = damageEvent.Part == UnitHealthPartId.None
                    ? UnitHitSystem.IsPointInsideRegion(
                        units, targetIndex, regionIndex,
                        damageEvent.ImpactPosition, hitTolerance, hitRegions)
                    : UnitHitSystem.IsPointInsideRegion(
                        units, targetIndex, regionIndex, damageEvent.Part,
                        damageEvent.ImpactPosition, hitTolerance, hitRegions);

                if (!inside)
                    regionIndex = -1;
            }

            if (regionIndex >= 0)
            {
                bindingIndex = UnitHitSystem.FindBindingInRegion(
                    units, targetIndex, regionIndex, damageEvent.Part, hitRegions);

                hasAnatomyBinding = bindingIndex >= 0 &&
                    UnitHitSystem.TryGetBinding(
                        units, targetIndex, regionIndex, bindingIndex,
                        out anatomyBinding, hitRegions);
            }
        }

        if (regionIndex < 0 || !hasAnatomyBinding)
            return new ProjectileDamageResult(0f, 0f, 0f, true);

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

        int zone = hasAnatomyBinding && anatomyBinding.ArmorZone >= 0
            ? anatomyBinding.ArmorZone
            : MapBodyPartToArmorZone(damageEvent.Part);

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

                    if (hasAnatomyBinding)
                    {
                        healthSystem.ApplyRegionDamage(
                            units, health, damageEvent.Target,
                            regionIndex, bindingIndex,
                            resolution.BluntImpactDamage);
                    }
                    else
                    {
                        healthSystem.ApplyDamage(
                            units, health, damageEvent.Target, bluntPart,
                            resolution.BluntImpactDamage, hitRegions);
                    }
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
            float energyLoss = anatomyBinding.ProjectileEnergyLoss >= 0f
                ? anatomyBinding.ProjectileEnergyLoss
                : GetBodyEnergyLoss(damageEvent.Part);
            float penetrationLoss = anatomyBinding.ProjectilePenetrationLoss >= 0f
                ? anatomyBinding.ProjectilePenetrationLoss
                : GetBodyPenetrationLoss(damageEvent.Part);

            energy = MathF.Max(0f, energy - energyLoss);
            penetration = MathF.Max(0f, penetration - penetrationLoss);

            stopped =
                energy <= 1f ||
                penetration <= 0.01f;
        }

        float totalDamage = damage;
        if (hasAnatomyBinding)
        {
            healthSystem.ApplyRegionDamage(
                units, health, damageEvent.Target,
                regionIndex, bindingIndex, damage);
        }
        else
        {
            healthSystem.ApplyDamage(
                units, health, damageEvent.Target,
                damageEvent.Part, damage, hitRegions);
        }

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

            if (hasAnatomyBinding)
            {
                healthSystem.AddRegionBleed(
                    units, health, damageEvent.Target,
                    regionIndex, bindingIndex, bleed);
            }
            else
            {
                healthSystem.AddBleed(
                    units, health, damageEvent.Target,
                    damageEvent.Part, bleed, hitRegions);
            }
        }

        if (!stopped &&
            damageEvent.DamageType != DamageType.Blunt &&
            hasAnatomyBinding &&
            anatomyBinding.Kind != UnitHealthPartKind.Organ &&
            energy > 1f && penetration > 0.01f &&
            damageEvent.Direction.LengthSquared() > 0.000001f)
        {
            float lastDistance = -1f;
            int lastVolumeIndex = -1;
            while (UnitHitSystem.TryFindNextAnatomyVolumeHit(
                       units, targetIndex, regionIndex, anatomyBinding.Id,
                       damageEvent.ImpactPosition, damageEvent.Direction,
                       lastDistance, lastVolumeIndex, damageEvent.HitRadius,
                       out UnitAnatomyVolumeHit volumeHit, bodies, hitRegions))
            {
                UnitAnatomyVolume volume = volumeHit.Volume;
                float remainingRatio = damageEvent.InitialEnergy > 0f
                    ? Math.Clamp(energy / damageEvent.InitialEnergy, 0f, 1f)
                    : 0f;
                float internalDamage = damageEvent.BaseDamage *
                    (0.35f + 0.65f * MathF.Sqrt(remainingRatio)) *
                    volume.DamageMultiplier;

                if (internalDamage > 0f)
                {
                    healthSystem.ApplyRegionDamage(
                        units, health, damageEvent.Target,
                        volumeHit.RegionIndex, volumeHit.BindingIndex, internalDamage);
                    totalDamage += internalDamage;

                    if (damageEvent.BleedChance > 0f)
                    {
                        float internalBleed = internalDamage * Math.Clamp(
                            damageEvent.BleedChance * volume.BleedMultiplier, 0f, 1f) * 0.05f;
                        healthSystem.AddRegionBleed(
                            units, health, damageEvent.Target,
                            volumeHit.RegionIndex, volumeHit.BindingIndex, internalBleed);
                    }
                }

                energy = MathF.Max(0f, energy - volume.EnergyLoss);
                penetration = MathF.Max(0f, penetration - volume.PenetrationLoss);
                stopped = energy <= 1f || penetration <= 0.01f;
                lastDistance = volumeHit.Distance;
                lastVolumeIndex = volumeHit.VolumeIndex;
                if (stopped)
                    break;
            }
        }

        return new ProjectileDamageResult(
            totalDamage,
            energy,
            penetration,
            stopped);
    }

    // Compatibility defaults for bindings authored before resistance became anatomy data.
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
            UnitHealthPartId.Neck or
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
