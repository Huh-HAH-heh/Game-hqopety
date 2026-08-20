using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Unit.Squad
{
    public enum SquadFormation : byte
    {
        Square,   // Плотное каре (прямоугольник)
        Line,     // Широкая шеренга в ряд
        Column,   // Динамический шлейф гуськом за Лидером (используется в пути)
        Wedge,    // Штурмовой клин (треугольник острием вперед)
        Diamond   // Тактический алмаз / ромб (для круговой обороны)
    }

}
