using System;

namespace Core.Items
{
    public enum EdificeType : byte
    {
        Wall,       // Обычная микро-стена (1х1 ячейка)
        Door,       // Дверной шлюз
        Generator,  // Генератор энергии
        Turret      // Автоматическая турель
    }

    public class EdificeConfig
    {
        public ushort TypeId;
        public string Name;
        public EdificeType Type;
        // характеристики каждой постройки 
        public float CoverEffectiveness;
        // Размеры постройки в микро-ячейках
        public byte WidthCells;
        public byte HeightCells;

        public int MaxHitPoints;

        // Кастомные параметры для систем
        public float PowerProduction; // Сколько энергии вырабатывает
        public float AttackRange;     // Радиус стрельбы
        public float ShotCooldown;    // Скорость перезарядки турели
        public byte Damage;           // Урон турели

        // Требования к навыкам (Статические параметры для всех зданий этого типа)
        public byte HackSkill;        // Уровень для взлома
        public byte RequiredSkill;    // Навык для манипулирования (открытия/запуска)


    }
}
