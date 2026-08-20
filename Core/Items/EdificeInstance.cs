using System;

namespace Core.Items
{
    /// <summary>
    /// Экземпляр конкретного здания, построенного на карте.
    /// Легковесная структура для плоских ECS массивов.
    /// </summary>
    public struct EdificeInstance
    {
        public ushort ConfigId;     // Ссылка на EdificeConfig
        public int HitPoints;       // Текущее ХП конкретного здания
        public int OriginX;         // Глобальная микро-координата X левого верхнего угла
        public int OriginY;         // Глобальная микро-координата Y левого верхнего угла
        public int ZLevel;
        public float InternalTimer; // Кулдаун для стрельбы турели

        // ДИНАМИЧЕСКИЕ СОСТОЯНИЯ ЭКЗЕМПЛЯРА
        public bool IsDoorOpened;   // Текущее состояние шлюза: true = открыт, false = закрыт
    }
}
