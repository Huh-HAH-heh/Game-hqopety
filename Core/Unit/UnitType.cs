namespace Core.Unit;

public enum UnitType : ushort
{
    Colonist = 0,
    Greenbob = 1,
    SegmentedMonster = 2,
}

public enum UnitBodyType : byte
{
    Colonist = 0,
    SmallCreature = 1,
    SegmentedCreature = 2,
}

public enum BodyPrimitive : byte
{
    Circle = 0,
    Box = 1,
    Triangle = 2,
    Line = 3
}

public enum BodyPartMotion : byte
{
    StaticRelative = 0,
    FollowParent = 1,
    WalkCycle = 2,
    WalkCycleOpposite = 3,
    WalkCycleProfile = 4,
    WalkCycleProfileOpposite = 5,
    PhysicalTail = 6,
    SpinalChain = 7
}

public enum BodyPartRole : byte
{
    Generic = 0,
    Torso = 1,
    Head = 2,
    UpperArm = 3,
    Forearm = 4,
    Hand = 5,
    Thigh = 6,
    Shin = 7,
    Foot = 8,
    Segment = 9,
    Neck = 10,
    Elbow = 11,
    Knee = 12,
    Muzzle = 14,
    Ear = 15,
    Eye = 16,
    Tail = 18
}

public enum UnitPosture : byte
{
    Standing = 0,
    Lying = 1,
    Crouching = 2
}
