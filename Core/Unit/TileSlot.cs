using System;

namespace Core.Unit;

public enum SlotType : byte
{
    Center,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft
}

public struct TileSlot : IEquatable<TileSlot>
{
    public TileCoord Coord;
    public SlotType Type;
    public byte Height; // Используется для рендера и штрафов скорости

    public TileSlot(TileCoord coord, SlotType type, byte height)
    {
        Coord = coord;
        Type = type;
        Height = height;
    }

    // ИСПРАВЛЕНО: Сравниваем только логическое положение на карте и в отряде
    // Вариант без трогания структуры TileCoord
    public static bool operator ==(TileSlot left, TileSlot right)
    {
        return left.Coord.X == right.Coord.X &&
               left.Coord.Y == right.Coord.Y &&
               left.Coord.Z == right.Coord.Z &&
               left.Type == right.Type;
    }
    public static bool operator !=(TileSlot left, TileSlot right)
    {
        return !(left == right);
    }

    public override bool Equals(object? obj) => obj is TileSlot other && Equals(other);

    // Добавлена явная реализация IEquatable<TileSlot> без аллокаций упаковки (boxing)
    public bool Equals(TileSlot other)
    {
        return Coord.X == other.Coord.X &&
               Coord.Y == other.Coord.Y &&
               Coord.Z == other.Coord.Z &&
               Type == other.Type;
    }
    // ИСПРАВЛЕНО: Исключили Height из хеш-функции
    public override int GetHashCode() => HashCode.Combine(Coord, Type);
}
