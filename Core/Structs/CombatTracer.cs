using SFML.System;
using System;
using System.Collections.Generic;
using System.Text;

namespace Render.RenderNeed
{
    public struct CombatTracer
    {
        public Vector2f StartPixels;  // Откуда стреляли/били (в пикселях)
        public Vector2f EndPixels;    // Куда попали (в пикселях)
        public int ZLevel;            // На каком этаже произошел бой
        public float TimeLeft;        // Сколько секунд осталось жить линии (для плавного затухания)
        public float MaxTime;         // Начальное время жизни (например, 0.4 секунды)
        public bool ShowCross;        // Рисовать ли крестик (true если нанесен урон)
    }
}
