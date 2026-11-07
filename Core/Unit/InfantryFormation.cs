using System;
using System.Numerics;

namespace Core.Unit;

public enum InfantryFormationType : byte
{
    Line = 0,
    Column = 1,
    Wedge = 2,
    Free = 3,
    DispersedLine = 4,
    Perimeter = 5,
    None = 6
}

public enum ColumnMovementVariant : byte
{
    HistoricalSlots = 0
}

public static class ColumnMovementVariantInfo
{
    public static string GetName(ColumnMovementVariant variant) =>
        "Колонна · исторические слоты";
}

/// <summary>
/// Names squad movement profiles. The column comparison can use independent
/// destination slots, weak centre-relative steering, a loose corridor or a linked trail.
/// </summary>
public static class InfantryFormation
{
    private const float GoldenAngle = 2.39996323f;

    public static string GetName(InfantryFormationType formation) => formation switch
    {
        InfantryFormationType.Free => "Группа",
        InfantryFormationType.Line => "Линия",
        InfantryFormationType.Column => "Колонна",
        InfantryFormationType.Wedge => "Клин",
        InfantryFormationType.DispersedLine => "Рассыпная линия",
        InfantryFormationType.Perimeter => "Периметр",
        InfantryFormationType.None => "Без строя",
        _ => "Группа"
    };

    /// <summary>
    /// Returns formation offsets. HistoricalSlots uses these as individual
    /// destination offsets; the other column variants use their own steering model.
    /// </summary>
    public static Vector2 GetOffset(
        int index,
        int count,
        InfantryFormationType formation,
        float spacing)
    {
        if (index < 0 || count <= 0 || index >= count ||
            formation == InfantryFormationType.None)
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

            case InfantryFormationType.DispersedLine:
            {
                int columns = Math.Min(12, Math.Max(2,
                    (int)MathF.Ceiling(MathF.Sqrt(count) * 2f)));
                int row = index / columns;
                int column = index % columns;
                int rowCount = Math.Min(columns, count - row * columns);
                float lateral = (column - (rowCount - 1) * 0.5f) * spacing * 1.35f;
                float depth = row * spacing * 1.2f + ((column & 1) == 0 ? 0f : spacing * 0.12f);
                return new Vector2(lateral, depth);
            }

            case InfantryFormationType.Perimeter:
            {
                float angle = index * (MathF.Tau / Math.Max(1, count));
                float radius = spacing * MathF.Max(1f, MathF.Sqrt(count) * 0.58f);
                return new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            }

            case InfantryFormationType.Free:
            default:
            {
                // Sunflower distribution avoids rigid rows while keeping a stable,
                // deterministic spread for the same membership ordering.
                float angle = index * GoldenAngle;
                float radius = spacing * 0.58f * MathF.Sqrt(index + 0.5f);
                return new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            }

            case InfantryFormationType.Line:
            {
                int columns = Math.Min(8, Math.Max(1,
                    (int)MathF.Ceiling(MathF.Sqrt(count) * 1.7f)));
                int row = index / columns;
                int column = index % columns;
                int rowCount = Math.Min(columns, count - row * columns);
                float lateral = (column - (rowCount - 1) * 0.5f) * spacing;
                return new Vector2(lateral, row * spacing);
            }
        }
    }
}
