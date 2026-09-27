namespace Core.Unit;

public enum UnitHealthPartKind : byte
{
    BodyPart = 0,
    Organ = 1
}

public enum UnitHealthPartId : ushort
{
    None = 0,

    WholeBody,

    Head,
    Brain,

    Torso,
    Heart,
    LeftLung,
    RightLung,
    Stomach,
    Liver,
    LeftKidney,
    RightKidney,

    LeftArm,
    LeftHand,

    RightArm,
    RightHand,

    LeftLeg,
    LeftFoot,

    RightLeg,
    RightFoot
}
