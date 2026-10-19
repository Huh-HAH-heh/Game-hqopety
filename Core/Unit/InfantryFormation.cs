using System;
using System.Numerics;

namespace Core.Unit;

public enum InfantryFormationType : byte
{
    Line = 0,
    Column = 1,
    Wedge = 2
}

/// <summary>Deterministic formation-slot offsets for player-controlled infantry groups.</summary>
public static class InfantryFormation
{
    public static string GetName(InfantryFormationType formation) => formation switch
    {
        InfantryFormationType.Line => "Line",
        InfantryFormationType.Column => "Column",
        InfantryFormationType.Wedge => "Wedge",
        _ => "Line"
    };

    /// <summary>
    /// Returns an offset where X is left/right across the formation and Y is
    /// distance behind its leading edge. Every index receives a distinct slot.
    /// </summary>
    public static Vector2 GetOffset(
        int index,
        int count,
        InfantryFormationType formation,
        float spacing)
    {
        if (index < 0 || count <= 0 || index >= count)
            return Vector2.Zero;

        spacing = Math.Clamp(spacing, 0.65f, 4f);

        switch (formation)
        {
            case InfantryFormationType.Column:
                return new Vector2(0f, index * spacing);

            case InfantryFormationType.Wedge:
                if (index == 0)
                    return Vector2.Zero;

                int rank = (index + 1) / 2;
                float side = (index & 1) == 1 ? -rank : rank;
                return new Vector2(side * spacing, rank * spacing);

            default:
                int columns = Math.Min(8, Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(count) * 1.7f)));
                int row = index / columns;
                int column = index % columns;
                int rowCount = Math.Min(columns, count - row * columns);
                float lateral = (column - (rowCount - 1) * 0.5f) * spacing;
                return new Vector2(lateral, row * spacing);
        }
    }
}
