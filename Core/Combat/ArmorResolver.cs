using System;
using Core.Items;

namespace Core.Combat;

public readonly struct ArmorResolution
{
    public float Damage { get; }
    public float Energy { get; }
    public float Penetration { get; }
    public float BluntImpactDamage { get; }
    public float ArmorDamage { get; }
    public bool Deflected { get; }
    public bool Stopped { get; }

    public ArmorResolution(
        float damage,
        float energy,
        float penetration,
        float bluntImpactDamage,
        float armorDamage,
        bool deflected,
        bool stopped)
    {
        Damage = damage;
        Energy = energy;
        Penetration = penetration;
        BluntImpactDamage = bluntImpactDamage;
        ArmorDamage = armorDamage;
        Deflected = deflected;
        Stopped = stopped;
    }
}

public static class ArmorResolver
{
    public static ArmorResolution ResolveLayer(
        DamageType damageType,
        float damage,
        float energy,
        float penetration,
        float bluntPenetration,
        ArmorConfig armor)
    {
        damage = MathF.Max(0f, damage);
        energy = MathF.Max(0f, energy);
        penetration = MathF.Max(0f, penetration);
        bluntPenetration = MathF.Max(0f, bluntPenetration);

        bool sharp =
            damageType == DamageType.Ballistic ||
            damageType == DamageType.Sharp;

        float armorAmount =
            GetArmorAmount(
                damageType,
                armor);

        float originalDamage =
            damage;

        float newPenetration =
            penetration -
            armorAmount;

        bool deflected =
            sharp &&
            armorAmount > penetration;

        float damageMultiplier =
            penetration <= 0.001f
                ? 1f
                : Math.Clamp(
                    newPenetration /
                    penetration,
                    0f,
                    1f);

        if (deflected)
            damageMultiplier = 0f;

        float newDamage =
            originalDamage *
            damageMultiplier;

        float absorbed =
            MathF.Max(
                0f,
                originalDamage - newDamage);

        float armorDamage =
            CalculateArmorDamage(
                damageType,
                armor,
                penetration,
                armorAmount,
                originalDamage,
                newDamage);

        float bluntImpactDamage = 0f;

        if (deflected)
        {
            bluntImpactDamage =
                CalculateDeflectionBluntDamage(
                    bluntPenetration);

            newPenetration = 0f;
            energy = 0f;
        }
        else
        {
            energy =
                MathF.Max(
                    0f,
                    energy -
                    MathF.Max(
                        0f,
                        armor.EnergyLoss));

            newPenetration =
                MathF.Max(
                    0f,
                    newPenetration);

            if (sharp &&
                originalDamage > newDamage &&
                bluntPenetration > 0f)
            {
                bluntImpactDamage =
                    MathF.Min(
                        originalDamage - newDamage,
                        CalculateDeflectionBluntDamage(
                            bluntPenetration) *
                        0.5f);
            }
        }

        bool stopped =
            deflected ||
            newPenetration <= 0.001f ||
            energy <= 1f;

        if (stopped &&
            !deflected)
        {
            newDamage =
                MathF.Min(
                    newDamage,
                    originalDamage * 0.20f);
        }

        return new ArmorResolution(
            newDamage,
            energy,
            newPenetration,
            bluntImpactDamage,
            armorDamage,
            deflected,
            stopped);
    }

    private static float GetArmorAmount(
        DamageType damageType,
        ArmorConfig armor)
    {
        return damageType switch
        {
            DamageType.Blunt =>
                armor.BluntRating > 0f
                    ? armor.BluntRating
                    : MathF.Max(
                        armor.PenetrationResistance,
                        armor.ArmorRating),

            DamageType.Heat =>
                armor.HeatRating > 0f
                    ? armor.HeatRating
                    : armor.ArmorRating,

            _ =>
                armor.SharpRating > 0f
                    ? armor.SharpRating
                    : MathF.Max(
                        armor.PenetrationResistance,
                        armor.ArmorRating)
        };
    }

    private static float CalculateArmorDamage(
        DamageType damageType,
        ArmorConfig armor,
        float penetration,
        float armorAmount,
        float originalDamage,
        float newDamage)
    {
        if (armorAmount <= 0f)
            return 0f;

        if (armor.SoftArmor)
        {
            if (damageType != DamageType.Ballistic &&
                damageType != DamageType.Sharp)
            {
                return 0f;
            }

            float absorbed =
                MathF.Max(
                    originalDamage * 0.20f,
                    originalDamage - newDamage);

            return absorbed *
                Math.Clamp(
                    armor.ArmorDamageCoefficient,
                    0f,
                    1f);
        }

        if (damageType == DamageType.Blunt &&
            penetration / armorAmount < 0.5f)
        {
            return 0f;
        }

        float factor =
            Math.Clamp(
                armor.HardArmorDamageFactor,
                0f,
                1f);

        if (penetration <= 0.001f)
            return 0f;

        float absorbedDamage =
            MathF.Max(
                0f,
                originalDamage - newDamage);

        float matchedPenDamage =
            absorbedDamage *
            MathF.Min(
                1f,
                (penetration * penetration) /
                (armorAmount * armorAmount));

        float overPenDamage =
            newDamage *
            Math.Clamp(
                armorAmount /
                penetration,
                0f,
                1f);

        return
            (matchedPenDamage +
             overPenDamage) *
            factor;
    }

    private static float CalculateDeflectionBluntDamage(
        float bluntPenetration)
    {
        if (bluntPenetration <= 0f)
            return 0f;

        return
            MathF.Pow(
                bluntPenetration *
                10000f,
                1f / 3f) /
            10f;
    }
}
