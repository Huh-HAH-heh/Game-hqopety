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
