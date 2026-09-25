using System;
namespace Core.Unit;

public readonly struct UnitId : IEquatable<UnitId>
{
    public int Index { get; }
    public uint Generation { get; }

    public bool IsValid =>
        Index >= 0 &&
        Generation != 0;

    public UnitId(
        int index,
        uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool Equals(UnitId other)
    {
        return
            Index == other.Index &&
            Generation == other.Generation;
    }

    public override bool Equals(object? obj)
    {
        return
            obj is UnitId other &&
            Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Index,
            Generation);
    }

    public static bool operator ==(
        UnitId left,
        UnitId right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(
        UnitId left,
        UnitId right)
    {
        return !left.Equals(right);
    }

    public override string ToString()
    {
        return $"Unit[{Index}:{Generation}]";
    }
}
