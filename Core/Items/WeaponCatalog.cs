using System;

using Core.Combat;

namespace Core.Items;

public sealed class AmmunitionConfig : ItemConfig
{
    public float ProjectileMassKg;
    public float ProjectileDiameterM;
    public float MuzzleVelocity;
    public float DragCoefficient = 0.30f;
    public float BallisticCoefficient = 1f;
    public float Penetration;
    public float SharpPenetration;
    public float BluntPenetration;
    public int PelletCount = 1;
    public float SpreadMultiplier = 1f;
    public float RecoilMultiplier = 1f;
    public float DamageMultiplier = 1f;
    public DamageType DamageType = DamageType.Ballistic;
    public float SuppressionFactor = 1f;
    public float BleedChance;
}

public sealed class AmmunitionSet
{
    public string Name { get; }
    public AmmunitionConfig[] Ammunition { get; }

    public int Count =>
        Ammunition.Length;

    public AmmunitionSet(
        string name,
        params AmmunitionConfig[] ammunition)
    {
        Name = name;
        Ammunition = ammunition;
    }

    public AmmunitionConfig? Get(int index)
    {
        if (index < 0 || index >= Ammunition.Length)
            return null;

        return Ammunition[index];
    }
}

public static class WeaponCatalog
{
    public static readonly AmmunitionConfig Rifle556 =
        new AmmunitionConfig
        {
            Name = "5.56x45mm FMJ",
            Weight = 0.012f,
            ProjectileMassKg = 0.004f,
            ProjectileDiameterM = 0.0057f,
            MuzzleVelocity = 930f,
            DragCoefficient = 0.30f,
            BallisticCoefficient = 1f,
            Penetration = 90f,
            SharpPenetration = 90f,
            BluntPenetration = 70f,
            DamageMultiplier = 1f,
            SuppressionFactor = 1f,
            BleedChance = 0.55f
        };

    public static readonly AmmunitionConfig Rifle556Ap =
        new AmmunitionConfig
        {
            Name = "5.56x45mm AP",
            Weight = 0.012f,
            ProjectileMassKg = 0.004f,
            ProjectileDiameterM = 0.0057f,
            MuzzleVelocity = 900f,
            DragCoefficient = 0.29f,
            BallisticCoefficient = 1.05f,
            Penetration = 115f,
            SharpPenetration = 115f,
            BluntPenetration = 82f,
            DamageMultiplier = 0.92f,
            SuppressionFactor = 0.95f,
            BleedChance = 0.48f
        };

    public static readonly AmmunitionConfig Pistol9mm =
        new AmmunitionConfig
        {
            Name = "9x19mm FMJ",
            Weight = 0.010f,
            ProjectileMassKg = 0.008f,
            ProjectileDiameterM = 0.009f,
            MuzzleVelocity = 380f,
            DragCoefficient = 0.32f,
            BallisticCoefficient = 1f,
            Penetration = 45f,
            SharpPenetration = 45f,
            BluntPenetration = 30f,
            DamageMultiplier = 1f,
            SuppressionFactor = 1f,
            BleedChance = 0.35f
        };

    public static readonly AmmunitionConfig Pistol9mmAp =
        new AmmunitionConfig
        {
            Name = "9x19mm AP",
            Weight = 0.010f,
            ProjectileMassKg = 0.008f,
            ProjectileDiameterM = 0.009f,
            MuzzleVelocity = 365f,
            DragCoefficient = 0.31f,
            BallisticCoefficient = 1.03f,
            Penetration = 65f,
            SharpPenetration = 65f,
            BluntPenetration = 36f,
            DamageMultiplier = 0.90f,
            SuppressionFactor = 0.90f,
            BleedChance = 0.30f
        };

    public static readonly AmmunitionSet Rifle556Set =
        new AmmunitionSet(
            "5.56x45mm",
            Rifle556,
            Rifle556Ap);

    public static readonly AmmunitionSet Pistol9mmSet =
        new AmmunitionSet(
            "9x19mm",
            Pistol9mm,
            Pistol9mmAp);

    public static readonly RangedWeaponConfig AssaultRifle =
        new RangedWeaponConfig
        {
            Name = "Assault Rifle",
            Weight = 4.1f,
            BaseDamage = 34,
            BleedChance = 0.55f,
            FireRate = 0.095f,
            MagazineSize = 30,
            ReloadTime = 2.25f,
            BaseEffectiveRange = 180f,
            BaseAccuracy = 0.012f,
            AimingAccuracy = 1f,
            SightEfficiency = 1f,
            MovementSpread = 0.18f,
            CircularError = 0.001f,
            AimTime = 0.20f,
            Recoil = 0.10f,
            DefaultFireMode = FireMode.Auto,
            BurstCount = 3,
            BurstInterval = 0.09f,
            SuppressionFactor = 1.0f,
            AmmoSet = Rifle556Set,
            DefaultAmmunition = Rifle556
        };

    public static readonly RangedWeaponConfig Pistol =
        new RangedWeaponConfig
        {
            Name = "Pistol",
            Weight = 0.9f,
            BaseDamage = 22,
            BleedChance = 0.35f,
            FireRate = 0.18f,
            MagazineSize = 15,
            ReloadTime = 1.65f,
            BaseEffectiveRange = 60f,
            BaseAccuracy = 0.018f,
            AimingAccuracy = 0.9f,
            SightEfficiency = 0.95f,
            MovementSpread = 0.25f,
            CircularError = 0.0015f,
            AimTime = 0.20f,
            Recoil = 0.07f,
            DefaultFireMode = FireMode.Single,
            BurstCount = 1,
            SuppressionFactor = 0.8f,
            AmmoSet = Pistol9mmSet,
            DefaultAmmunition = Pistol9mm
        };
}
