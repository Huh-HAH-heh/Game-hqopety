using System;

namespace Core.Items;

public sealed class AmmunitionConfig : ItemConfig
{
    public float ProjectileMassKg;
    public float ProjectileDiameterM;
    public float MuzzleVelocity;
    public float DragCoefficient = 0.30f;
    public float Penetration;
    public float DamageMultiplier = 1f;
}

public static class WeaponCatalog
{
    public static readonly AmmunitionConfig Rifle556 =
        new AmmunitionConfig
        {
            Name = "5.56x45mm",
            Weight = 0.012f,
            ProjectileMassKg = 0.004f,
            ProjectileDiameterM = 0.0057f,
            MuzzleVelocity = 930f,
            DragCoefficient = 0.30f,
            Penetration = 90f,
            DamageMultiplier = 1f
        };

    public static readonly AmmunitionConfig Pistol9mm =
        new AmmunitionConfig
        {
            Name = "9x19mm",
            Weight = 0.010f,
            ProjectileMassKg = 0.008f,
            ProjectileDiameterM = 0.009f,
            MuzzleVelocity = 380f,
            DragCoefficient = 0.32f,
            Penetration = 45f,
            DamageMultiplier = 1f
        };

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
            DefaultAmmunition = Pistol9mm
        };
}
