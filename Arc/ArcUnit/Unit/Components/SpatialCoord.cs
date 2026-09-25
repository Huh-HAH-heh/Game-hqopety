using System;

namespace Core.Unit.Components
{
    /// <summary>
    /// Монолитная глобальная координата микро-ячейки в мире.
    /// Базовая единица пространства для всех муравьев и микро-объектов.
    /// </summary>
    public struct SpatialCoord : IEquatable<SpatialCoord>
    {
        // Глобальная микро-координата X в мире
        public int X;

        // Глобальная микро-координата Y в мире
        public int Y;

        // Номер этажа / Z-уровень карты
        public int Z;

        public SpatialCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Переопределяем методы сравнения, чтобы UnitSpatialGrid работал со скоростью молнии
        public bool Equals(SpatialCoord other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is SpatialCoord other && Equals(other);
        }

        public override int GetHashCode()
        {
            // Высокопроизводительный хэш-код без аллокаций (быстрое перемешивание битов)
            return HashCode.Combine(X, Y, Z);
        }

        public static bool operator ==(SpatialCoord left, SpatialCoord right) => left.Equals(right);
        public static bool operator !=(SpatialCoord left, SpatialCoord right) => !left.Equals(right);

        public override string ToString()
        {
            return $"MicroCell(X:{X}, Y:{Y}, Z:{Z})";
        }
    }
}
