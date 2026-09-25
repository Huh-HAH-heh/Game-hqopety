using Core.Items;

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
                BaseAccuracy = 0.02f
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
                BaseAccuracy = 0.005f
            },
            armor: new ArmorConfig
            {
                Name = "Бронежилет БЖ-4",
                ProtectedZone = 1,
                DamageAbsorption = 0.5f,
                BleedProtectionChance = 0.8f,
                Weight = 8.5f
            },
            helmet: new ArmorConfig
            {
                Name = "Каска",
                ProtectedZone = 0,
                DamageAbsorption = 0.4f,
                BleedProtectionChance = 0.7f,
                Weight = 1.5f
            });
}