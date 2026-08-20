using System;
using System.Collections.Generic;
using System.Text;

namespace Core.defs;
public class FloorDef
{
    public ushort Id;

    public string Name;

    public float MoveSpeed;

    public bool IsNatural;
}
public class WallDef
{
    public ushort Id;

    public string Name;

    public float HitPoints;

    public bool BlocksVision;

    public bool BlocksMovement;
}
public class RoofDef
{
    public byte Id;

    public string Name;

    public bool BlocksRain;

    public bool BlocksLight;
}
public class SolidDef
{
    public ushort Id;

    public string Name;

    public float Hardness;

    public float Density;

    public bool Mineable;
}
public class EdificeDef
{
    public int Id;

    public string Name;

    public float HitPoints;

    public bool BlocksMovement;
}