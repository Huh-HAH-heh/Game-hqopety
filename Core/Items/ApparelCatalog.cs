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
        WorldColorR = 75,
        WorldColorG = 86,
        WorldColorB = 101,
        WorldRenderPadding = 0.075f
    };
}
