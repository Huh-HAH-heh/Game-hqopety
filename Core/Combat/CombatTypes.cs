namespace Core.Combat;

public enum DamageType : byte
{
    Ballistic = 0,
    Sharp = 1,
    Blunt = 2,
    Heat = 3,
    Explosive = 4
}

public enum FireMode : byte
{
    Single = 0,
    Burst = 1,
    Auto = 2
}

public enum AimMode : byte
{
    AimedShot = 0,
    Snapshot = 1,
    SuppressFire = 2
}

public enum TargetMode : byte
{
    Automatic = 0,
    Torso = 1,
    Head = 2,
    Legs = 3
}

public enum ArmorClass : byte
{
    Sharp = 0,
    Blunt = 1,
    Heat = 2
}
