namespace Core.Items;

public static class ApparelCatalog
{
    public static readonly ArmorConfig WorkShirt = new()
    {
        Name = "Work shirt",
        Weight = 0.8f,
        Coverage = ArmorCoverage.Torso | ArmorCoverage.LeftArm | ArmorCoverage.RightArm |
                   ArmorCoverage.LeftHand | ArmorCoverage.RightHand,
        Layer = ArmorLayer.Clothing,
        DamageAbsorption = 0.08f,
        ArmorDamageCoefficient = 0.08f,
        EnergyLoss = 70f,
        PenetrationResistance = 8f,
        ArmorRating = 8f,
        SharpRating = 8f,
        BluntRating = 5f,
        HeatRating = 2f,
        SoftArmor = true,
        WorldColorR = 82,
        WorldColorG = 108,
        WorldColorB = 78,
        WorldRenderPadding = 0.085f
    };

    public static readonly ArmorConfig WorkTrousers = new()
    {
        Name = "Work trousers",
        Weight = 1.0f,
        Coverage = ArmorCoverage.LeftLeg | ArmorCoverage.RightLeg |
                   ArmorCoverage.LeftFoot | ArmorCoverage.RightFoot,
        Layer = ArmorLayer.Clothing,
        DamageAbsorption = 0.10f,
        ArmorDamageCoefficient = 0.08f,
        EnergyLoss = 55f,
        PenetrationResistance = 10f,
        ArmorRating = 10f,
        SharpRating = 10f,
        BluntRating = 6f,
        HeatRating = 2f,
        SoftArmor = true,
        WorldColorR = 75,
        WorldColorG = 86,
        WorldColorB = 101,
        WorldRenderPadding = 0.075f
    };

    public static readonly ArmorConfig CombatVest = new()
    {
        Name = "Combat vest",
        Weight = 5.5f,
        Coverage = ArmorCoverage.Torso,
        Layer = ArmorLayer.Armor,
        DamageAbsorption = 0.50f,
        ArmorDamageCoefficient = 0.18f,
        HardArmorDamageFactor = 0.22f,
        EnergyLoss = 260f,
        PenetrationResistance = 68f,
        ArmorRating = 68f,
        SharpRating = 68f,
        BluntRating = 30f,
        HeatRating = 18f,
        SoftArmor = false,
        WorldColorR = 120,
        WorldColorG = 126,
        WorldColorB = 132,
        WorldRenderPadding = 0.14f
    };

    public static readonly ArmorConfig CombatHelmet = new()
    {
        Name = "Combat helmet",
        Weight = 1.4f,
        Coverage = ArmorCoverage.Head,
        Layer = ArmorLayer.Armor,
        DamageAbsorption = 0.40f,
        ArmorDamageCoefficient = 0.18f,
        HardArmorDamageFactor = 0.22f,
        EnergyLoss = 240f,
        PenetrationResistance = 72f,
        ArmorRating = 72f,
        SharpRating = 72f,
        BluntRating = 26f,
        HeatRating = 18f,
        SoftArmor = false,
        WorldColorR = 104,
        WorldColorG = 111,
        WorldColorB = 102,
        WorldRenderPadding = 0.12f
    };
}
