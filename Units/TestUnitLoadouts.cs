using Core.Items;
using Core.Combat;

namespace Units;

public readonly struct TestUnitLoadout
{
    public readonly RangedWeaponConfig? Weapon;
    public readonly ArmorConfig? Armor;
    public readonly ArmorConfig? Helmet;

    public TestUnitLoadout(
        RangedWeaponConfig? weapon,
        ArmorConfig? armor,
        ArmorConfig? helmet)
    {
        Weapon = weapon;
        Armor = armor;
        Helmet = helmet;
    }
}

public static class TestUnitLoadouts
{
    public static readonly TestUnitLoadout Blue =
        new TestUnitLoadout(
            weapon: new RangedWeaponConfig
            {
                Name = "АК-47",
                Weight = 4.3f,
                BaseDamage = 25,
                BleedChance = 0.7f,
                FireRate = 0.1f,
                BaseEffectiveRange = 100f,
                BaseAccuracy = 0.02f,
                DefaultFireMode = FireMode.Auto,
                DefaultAmmunition = WeaponCatalog.Rifle556
            },
            armor: null,
            helmet: null);

    public static readonly TestUnitLoadout Red =
        new TestUnitLoadout(
            weapon: new RangedWeaponConfig
            {
                Name = "СВД",
                Weight = 4.5f,
                BaseDamage = 40,
                BleedChance = 0.9f,
                FireRate = 0.8f,
                BaseEffectiveRange = 300f,
                BaseAccuracy = 0.005f,
                DefaultFireMode = FireMode.Single,
                DefaultAmmunition = WeaponCatalog.Rifle556
            },
            armor: new ArmorConfig
            {
                Name = "Бронежилет БЖ-4",
                ProtectedZone = 1,
                DamageAbsorption = 0.35f,
                BleedProtectionChance = 0.8f,
                ArmorRating = 55f,
                PenetrationResistance = 55f,
                EnergyLoss = 250f,
                Weight = 8.5f
            },
            helmet: new ArmorConfig
            {
                Name = "Каска",
                ProtectedZone = 0,
                DamageAbsorption = 0.25f,
                BleedProtectionChance = 0.7f,
                ArmorRating = 35f,
                PenetrationResistance = 35f,
                EnergyLoss = 170f,
                Weight = 1.5f
            });
}