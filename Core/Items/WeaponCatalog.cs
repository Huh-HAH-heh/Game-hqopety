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
    public bool IsLaser;
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
            WorldRenderLength = 0.82f,
            WorldRenderWidth = 0.085f,
            WorldColorR = 63,
            WorldColorG = 68,
            WorldColorB = 62,
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
            AimTime = 0.65f,
            Recoil = 0.10f,
            DefaultFireMode = FireMode.Auto,
            BurstCount = 3,
            BurstInterval = 0.09f,
            SuppressionFactor = 1.0f,
            AmmoSet = Rifle556Set,
            DefaultAmmunition = Rifle556
        };

    public static readonly AmmunitionConfig PulseRepeaterRound =
        new AmmunitionConfig
        {
            Name = "6mm high-velocity flechette", Weight = 0.008f,
            ProjectileMassKg = 0.0025f, ProjectileDiameterM = 0.004f,
            MuzzleVelocity = 850f, DragCoefficient = 0.18f, BallisticCoefficient = 1.25f,
            Penetration = 80f, SharpPenetration = 80f, BluntPenetration = 52f,
            DamageMultiplier = 1f, DamageType = DamageType.Ballistic,
            SuppressionFactor = 0.75f, BleedChance = 0.32f
        };

    public static readonly AmmunitionSet PulseRepeaterAmmo =
        new AmmunitionSet("6mm flechette", PulseRepeaterRound);

    public static readonly AmmunitionConfig LaserPulse =
        new AmmunitionConfig
        {
            Name = "Coherent laser pulse", Weight = 0.01f,
            ProjectileMassKg = 0.01f, ProjectileDiameterM = 0.005f,
            MuzzleVelocity = 900f, DragCoefficient = 0f, BallisticCoefficient = 1f,
            Penetration = 130f, SharpPenetration = 130f, BluntPenetration = 0f,
            DamageMultiplier = 1f, DamageType = DamageType.Heat,
            SuppressionFactor = 0.35f, BleedChance = 0f, IsLaser = true
        };

    public static readonly AmmunitionSet LaserPulseAmmo =
        new AmmunitionSet("Laser pulse cells", LaserPulse);

    public static readonly AmmunitionConfig RailSlug =
        new AmmunitionConfig
        {
            Name = "8mm ferromagnetic rail slug", Weight = 0.012f,
            ProjectileMassKg = 0.009f, ProjectileDiameterM = 0.0045f,
            MuzzleVelocity = 1500f, DragCoefficient = 0.02f, BallisticCoefficient = 1.5f,
            Penetration = 170f, SharpPenetration = 170f, BluntPenetration = 120f,
            DamageMultiplier = 1f, DamageType = DamageType.Ballistic,
            SuppressionFactor = 1.2f, BleedChance = 0.42f
        };

    public static readonly AmmunitionSet RailgunAmmo =
        new AmmunitionSet("8mm rail slugs", RailSlug);

    public static readonly RangedWeaponConfig PulseRepeater =
        new RangedWeaponConfig
        {
            Name = "K-90 kinetic repeater", Technology = WeaponTechnology.KineticRepeater,
            Weight = 3.0f, WorldRenderLength = 0.78f, WorldRenderWidth = 0.10f,
            WorldColorR = 70, WorldColorG = 125, WorldColorB = 106,
            BaseDamage = 18, BleedChance = 0.32f, FireRate = 0.075f,
            MagazineSize = 72, ReloadTime = 2.3f, BaseEffectiveRange = 150f,
            BaseAccuracy = 0.012f, AimingAccuracy = 1f, SightEfficiency = 1f,
            MovementSpread = 0.18f, CircularError = 0.001f, AimTime = 0.55f,
            Recoil = 0.07f, DefaultFireMode = FireMode.Auto, BurstCount = 3,
            BurstInterval = 0.08f, SuppressionFactor = 0.75f,
            AmmoSet = PulseRepeaterAmmo, DefaultAmmunition = PulseRepeaterRound
        };

    public static readonly RangedWeaponConfig LaserCarbine =
        new RangedWeaponConfig
        {
            Name = "L-4 laser carbine", Technology = WeaponTechnology.Laser,
            Weight = 2.6f, WorldRenderLength = 0.72f, WorldRenderWidth = 0.07f,
            WorldColorR = 58, WorldColorG = 205, WorldColorB = 238,
            BaseDamage = 25, BleedChance = 0f, FireRate = 0.12f,
            MagazineSize = 40, ReloadTime = 2.8f, BaseEffectiveRange = 160f,
            BaseAccuracy = 0.007f, AimingAccuracy = 1.08f, SightEfficiency = 1.05f,
            MovementSpread = 0.10f, CircularError = 0.0007f, AimTime = 0.72f,
            Recoil = 0.04f, DefaultFireMode = FireMode.Auto, BurstCount = 3,
            BurstInterval = 0.12f, SuppressionFactor = 0.35f,
            AmmoSet = LaserPulseAmmo, DefaultAmmunition = LaserPulse
        };

    public static readonly RangedWeaponConfig Railgun =
        new RangedWeaponConfig
        {
            Name = "R-18 magnetic railgun", Technology = WeaponTechnology.Railgun,
            Weight = 7.2f, WorldRenderLength = 1.02f, WorldRenderWidth = 0.13f,
            WorldColorR = 218, WorldColorG = 160, WorldColorB = 78,
            BaseDamage = 58, BleedChance = 0.42f, FireRate = 1.05f,
            MagazineSize = 5, ReloadTime = 3.6f, BaseEffectiveRange = 230f,
            BaseAccuracy = 0.0045f, AimingAccuracy = 1.12f, SightEfficiency = 1.15f,
            MovementSpread = 0.32f, CircularError = 0.0005f, AimTime = 1.25f,
            Recoil = 0.48f, DefaultFireMode = FireMode.Single, BurstCount = 1,
            SuppressionFactor = 1.2f, AmmoSet = RailgunAmmo, DefaultAmmunition = RailSlug
        };

    public static readonly RangedWeaponConfig Pistol =
        new RangedWeaponConfig
        {
            Name = "Pistol",
            Weight = 0.9f,
            WorldRenderLength = 0.36f,
            WorldRenderWidth = 0.075f,
            WorldColorR = 72,
            WorldColorG = 76,
            WorldColorB = 82,
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
            AimTime = 0.42f,
            Recoil = 0.07f,
            DefaultFireMode = FireMode.Single,
            BurstCount = 1,
            SuppressionFactor = 0.8f,
            AmmoSet = Pistol9mmSet,
            DefaultAmmunition = Pistol9mm
        };
}
