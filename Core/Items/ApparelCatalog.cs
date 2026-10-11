namespace Core.Items;

public static class ApparelCatalog
{
    public static readonly ArmorConfig WorkShirt = new()
    {
        Name = "Thermal undersuit", Weight = 0.8f,
        Coverage = ArmorCoverage.Torso | ArmorCoverage.LeftArm | ArmorCoverage.RightArm |
                   ArmorCoverage.LeftHand | ArmorCoverage.RightHand,
        Layer = ArmorLayer.Clothing, DamageAbsorption = 0.08f,
        ArmorDamageCoefficient = 0.08f, EnergyLoss = 70f,
        PenetrationResistance = 8f, ArmorRating = 8f, SharpRating = 8f,
        BluntRating = 5f, HeatRating = 4f, SoftArmor = true,
        WorldColorR = 54, WorldColorG = 94, WorldColorB = 105, WorldRenderPadding = 0.085f
    };

    public static readonly ArmorConfig WorkTrousers = new()
    {
        Name = "Light composite legwear", Weight = 1.0f,
        Coverage = ArmorCoverage.LeftLeg | ArmorCoverage.RightLeg |
                   ArmorCoverage.LeftFoot | ArmorCoverage.RightFoot,
        Layer = ArmorLayer.Clothing, DamageAbsorption = 0.10f,
        ArmorDamageCoefficient = 0.08f, EnergyLoss = 55f,
        PenetrationResistance = 10f, ArmorRating = 10f, SharpRating = 10f,
        BluntRating = 6f, HeatRating = 4f, SoftArmor = true,
        WorldColorR = 55, WorldColorG = 75, WorldColorB = 96, WorldRenderPadding = 0.075f
    };

    public static readonly ArmorConfig CombatVest = new()
    {
        Name = "Light ceramic-fiber vest", Weight = 3.2f,
        Coverage = ArmorCoverage.Torso, Layer = ArmorLayer.Armor,
        DamageAbsorption = 0.42f, ArmorDamageCoefficient = 0.18f,
        HardArmorDamageFactor = 0.24f, EnergyLoss = 190f,
        PenetrationResistance = 62f, ArmorRating = 62f, SharpRating = 62f,
        BluntRating = 28f, HeatRating = 42f, SoftArmor = false,
        WorldColorR = 108, WorldColorG = 128, WorldColorB = 142, WorldRenderPadding = 0.14f
    };

    public static readonly ArmorConfig CombatHelmet = new()
    {
        Name = "Light ballistic helmet", Weight = 1.1f,
        Coverage = ArmorCoverage.Head, Layer = ArmorLayer.Armor,
        DamageAbsorption = 0.36f, ArmorDamageCoefficient = 0.18f,
        HardArmorDamageFactor = 0.22f, EnergyLoss = 170f,
        PenetrationResistance = 64f, ArmorRating = 64f, SharpRating = 64f,
        BluntRating = 27f, HeatRating = 35f, SoftArmor = false,
        WorldColorR = 88, WorldColorG = 103, WorldColorB = 108, WorldRenderPadding = 0.12f
    };

    public static readonly ArmorConfig PoweredExoskeleton = new()
    {
        Name = "Powered mobility frame", Weight = 6.6f,
        Coverage = ArmorCoverage.Head | ArmorCoverage.Torso |
                   ArmorCoverage.LeftArm | ArmorCoverage.RightArm |
                   ArmorCoverage.LeftHand | ArmorCoverage.RightHand |
                   ArmorCoverage.LeftLeg | ArmorCoverage.RightLeg |
                   ArmorCoverage.LeftFoot | ArmorCoverage.RightFoot,
        Layer = ArmorLayer.Base, DamageAbsorption = 0.12f,
        ArmorDamageCoefficient = 0.14f, HardArmorDamageFactor = 0.16f,
        EnergyLoss = 55f, PenetrationResistance = 20f,
        ArmorRating = 20f, SharpRating = 20f, BluntRating = 34f,
        HeatRating = 16f, SoftArmor = false, MovementSpeedMultiplier = 1.12f,
        WorldColorR = 112, WorldColorG = 166, WorldColorB = 186, WorldRenderPadding = 0.18f
    };
}
