using System;

namespace Core.Unit;

/// <summary>
/// Stable, per-pawn behavioral tendencies. Generated once from the pawn identity,
/// not rerolled on each decision or simulation tick. These values bias choices;
/// they never override critical safety rules or explicit state constraints.
/// </summary>
public sealed class UnitAiPersonalityStore
{
    private float[] _caution;
    private float[] _aggression;
    private float[] _discipline;

    public int Capacity => _caution.Length;
    public float[] Caution => _caution;
    public float[] Aggression => _aggression;
    public float[] Discipline => _discipline;

    public UnitAiPersonalityStore(int initialCapacity = 1024)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _caution = new float[initialCapacity];
        _aggression = new float[initialCapacity];
        _discipline = new float[initialCapacity];
        Array.Fill(_caution, 0.5f);
        Array.Fill(_aggression, 0.5f);
        Array.Fill(_discipline, 0.5f);
    }

    public void InitializeUnit(int unitIndex, uint generation, ushort factionTag)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));

        EnsureCapacity(unitIndex + 1);
        uint state = unchecked(
            0x9E3779B9u ^
            (uint)(unitIndex + 1) * 2654435761u ^
            generation * 2246822519u ^
            (uint)factionTag * 3266489917u);
        if (state == 0u)
            state = 0xA341316Cu;

        _caution[unitIndex] = NextTrait(ref state);
        _aggression[unitIndex] = NextTrait(ref state);
        _discipline[unitIndex] = NextTrait(ref state);
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)Capacity)
            return;

        _caution[unitIndex] = 0.5f;
        _aggression[unitIndex] = 0.5f;
        _discipline[unitIndex] = 0.5f;
    }

    public void EnsureCapacity(int required)
    {
        if (required <= Capacity)
            return;

        int newCapacity = Math.Max(required, Capacity * 2);
        int previousCapacity = Capacity;
        Array.Resize(ref _caution, newCapacity);
        Array.Resize(ref _aggression, newCapacity);
        Array.Resize(ref _discipline, newCapacity);
        Array.Fill(_caution, 0.5f, previousCapacity, newCapacity - previousCapacity);
        Array.Fill(_aggression, 0.5f, previousCapacity, newCapacity - previousCapacity);
        Array.Fill(_discipline, 0.5f, previousCapacity, newCapacity - previousCapacity);
    }

    private static float NextTrait(ref uint state) =>
        0.20f + ToUnitFloat(Next(ref state)) * 0.60f;

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static float ToUnitFloat(uint value) =>
        (value & 0x00FFFFFFu) / 16777216f;
}
