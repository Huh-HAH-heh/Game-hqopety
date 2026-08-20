using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Unit.Components.AiComponents
{
    namespace Core.Unit.Components
    {
        public enum AiOpCode : byte
        {
            Idle = 0,               // Полный покой (нет задач)
            Wander = 1,             // Расслабленное блуждание / Патруль города
            MoveToTarget = 2,       // Движение в конкретную микро-ячейку (работа/склад)
            TakeCover = 3,          // Экстренный уход под обстрел (Защита от вектора угрозы)
            CombatEngage = 4,       // Боевой контакт (Лок цели, прицеливание, стрельба очередями)
            InvestigateSound = 5    // Разведка: поиск источника подозрительного звука
        }

        /// <summary>
        /// Структура команды (аналог регистра команды ЦП). Занимает всего 12 байт.
        /// </summary>
        public struct AiCommand
        {
            public AiOpCode OpCode; // Какую задачу выполнять
            public int TargetX;     // Вспомогательный параметр 1 (координата X цели / врага)
            public int TargetY;     // Вспомогательный параметр 2 (координата Y цели / врага)
            public float Timer;     // Внутренний таймер задачи (например, сколько секунд еще целиться)
        }
    }

}
