using System;
using Core.Items;

namespace Core.Combat;

public readonly struct ArmorResolution
{
    public float Damage { get; }
    public float Energy { get; }
    public float Penetration { get; }
    public float ArmorDamage { get; }
    public bool Stopped { get; }

    public ArmorResolution(
        float damage,
        float energy,
        float penetration,
        float armorDamage,
        bool stopped)
    {
        Damage = damage;
        Energy = energy;
        Penetration = penetration;
        ArmorDamage = armorDamage;
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
        ArmorConfig armor)
    {
        damage = MathF.Max(0f, damage);
        energy = MathF.Max(0f, energy);
        penetration = MathF.Max(0f, penetration);

        float resistance =
            MathF.Max(
                0f,
                MathF.Max(
                    armor.PenetrationResistance,
                    armor.ArmorRating));

        float energyLoss =
            MathF.Max(0f, armor.EnergyLoss);

        float originalDamage = damage;

        bool deflected =
            penetration > 0f &&
            resistance > 0f &&
            penetration < resistance &&
            (damageType == DamageType.Ballistic ||
             damageType == DamageType.Sharp);

        if (deflected)
        {
            float armorDamage =
                originalDamage *
                Math.Clamp(
                    armor.ArmorDamageCoefficient,
                    0f,
                    1f);

            return new ArmorResolution(
                0f,
                0f,
                0f,
                armorDamage,
                true);
        }

        float residualPenetration =
            resistance <= 0.001f
                ? penetration
                : MathF.Max(
                    0f,
                    penetration - resistance);

        float damageMultiplier =
            resistance <= 0.001f
                ? 1f
                : Math.Clamp(
                    residualPenetration /
                    MathF.Max(
                        penetration,
                        0.001f),
                    0f,
                    1f);

        float absorption =
            Math.Clamp(
                armor.DamageAbsorption,
                0f,
                0.95f);

        float penetrationDamage =
            originalDamage *
            (0.20f +
             0.80f * damageMultiplier);

        damage =
            penetrationDamage *
            (1f - absorption);

        float absorbed =
            MathF.Max(
                0f,
                originalDamage - damage);

        float armorDamage =
            absorbed *
            Math.Clamp(
                armor.ArmorDamageCoefficient,
                0f,
                1f);

        energy =
            MathF.Max(
                0f,
                energy - energyLoss);

        bool stopped =
            residualPenetration <= 0.001f ||
            energy <= 1f;

        if (stopped)
        {
            damage =
                MathF.Min(
                    damage,
                    originalDamage * 0.20f);
        }

        return new ArmorResolution(
            damage,
            energy,
            residualPenetration,
            armorDamage,
            stopped);
    }
}
