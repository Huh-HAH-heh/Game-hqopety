using Core.Combat;

namespace Core.Items;

/// <summary>Starter melee equipment using RimClone's shared armor and anatomy pipeline.</summary>
public static class MeleeWeaponCatalog
{
    public static readonly MeleeWeaponConfig CombatKnife = new()
    {
        Name = "Combat Knife",
        Weight = 0.42f,
        WorldRenderLength = 0.28f,
        WorldRenderWidth = 0.035f,
        WorldColorR = 150,
        WorldColorG = 158,
        WorldColorB = 166,
        BaseDamage = 24,
        BleedChance = 0.62f,
        FireRate = 0.48f,
        MeleeDamageType = DamageType.Sharp,
        Reach = 1.05f,
        ArmorPenetration = 18f,
        BluntPenetration = 7f,
        CriticalChance = 0.08f,
        ParryChance = 0.12f
    };

    public static readonly MeleeWeaponConfig ShockBaton = new()
    {
        Name = "Shock Baton",
        Weight = 1.1f,
        WorldRenderLength = 0.48f,
        WorldRenderWidth = 0.055f,
        WorldColorR = 74,
        WorldColorG = 172,
        WorldColorB = 190,
        BaseDamage = 20,
        BleedChance = 0f,
        FireRate = 0.62f,
        MeleeDamageType = DamageType.Blunt,
        Reach = 1.2f,
        ArmorPenetration = 5f,
        BluntPenetration = 24f,
        CriticalChance = 0.04f,
        ParryChance = 0.23f
    };

    public static readonly MeleeWeaponConfig LongBlade = new()
    {
        Name = "Long Blade",
        Weight = 1.45f,
        WorldRenderLength = 0.68f,
        WorldRenderWidth = 0.045f,
        WorldColorR = 178,
        WorldColorG = 183,
        WorldColorB = 190,
        BaseDamage = 32,
        BleedChance = 0.76f,
        FireRate = 0.78f,
        MeleeDamageType = DamageType.Sharp,
        Reach = 1.48f,
        ArmorPenetration = 25f,
        BluntPenetration = 9f,
        CriticalChance = 0.11f,
        ParryChance = 0.19f
    };
}
