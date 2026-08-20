using System;
using System.Collections.Generic;
using System.Text;

namespace Core.defs
{
    public static class DefDatabase
    {
        public static readonly FloorDef[] Floors =
        {
        new FloorDef
        {
            Id = 0,
            Name = "None",
            MoveSpeed = 1f
        },

        new FloorDef
        {
            Id = 1,
            Name = "Grass",
            MoveSpeed = 1f
        },

        new FloorDef
        {
            Id = 2,
            Name = "Stone",
            MoveSpeed = 0.9f
        }
    };

        public static readonly WallDef[] Walls =
        {
        new WallDef
        {
            Id = 0,
            Name = "None",
            BlocksMovement = false,
            BlocksVision = false
        },

        new WallDef
        {
            Id = 1,
            Name = "Wood Wall"
        },

        new WallDef
        {
            Id = 2,
            Name = "Granite Wall"
        }
    };

        public static readonly RoofDef[] Roofs =
        {
        new RoofDef
        {
            Id = 0,
            Name = "None",
            BlocksLight = false,
            BlocksRain = false
        },

        new RoofDef
        {
            Id = 1,
            Name = "Roof"
        }
    };

        public static readonly SolidDef[] Solids =
        {
        new SolidDef
        {
            Id = 0,
            Name = "Empty"
        },

        new SolidDef
        {
            Id = 1,
            Name = "Granite"
        }
    };
    }
}
