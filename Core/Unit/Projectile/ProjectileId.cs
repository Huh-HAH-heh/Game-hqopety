using System;

namespace Core.Unit;

public readonly struct ProjectileId : IEquatable<ProjectileId>
{
    public int Index { get; }
    public uint Generation { get; }

    public bool IsValid =>
        Index >= 0 &&
        Generation != 0;

    public ProjectileId(
        int index,
        uint generation)
    {
        Index = index;
        Generation = generation;
    }

    public bool Equals(
        ProjectileId other)
    {
        return
            Index == other.Index &&
            Generation == other.Generation;
    }

    public override bool Equals(
        object? obj)
    {
        return
            obj is ProjectileId other &&
            Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Index,
            Generation);
    }

    public static bool operator ==(
        ProjectileId left,
        ProjectileId right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(
        ProjectileId left,
        ProjectileId right)
    {
        return !left.Equals(right);
    }
}
