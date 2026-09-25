namespace Core.Unit;

public enum UnitType : ushort
{
    Colonist = 0,
    Greenbob = 1,
    SegmentedMonster = 2
}

public enum UnitBodyType : byte
{
    Colonist = 0,
    SmallCreature = 1,
    SegmentedCreature = 2
}

public enum BodyPrimitive : byte
{
    Circle = 0,
    Box = 1
}

public enum BodyPartMotion : byte
{
    StaticRelative = 0,
    FollowParent = 1
}
