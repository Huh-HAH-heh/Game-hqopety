using System.Text;

namespace Core.Map;

public static class TerrainMetadataSystem
{
    public static string InspectColumn(
        WorldMap worldMap,
        int x,
        int y)
    {
        StringBuilder sb =
            new StringBuilder();

        sb.AppendLine(
            "=== TERRAIN COLUMN ===");

        sb.AppendLine(
            $"Координаты: ({x}, {y})");

        sb.AppendLine(
            $"Высота поверхности: " +
            $"{worldMap.GetSurfaceHeight(x, y):F1} м");

        ReadOnlySpan<TileRange> ranges =
            worldMap.GetTileRanges(
                x,
                y);

        sb.AppendLine(
            $"Диапазонов: {ranges.Length}");

        sb.AppendLine();

        for (int i = 0;
             i < ranges.Length;
             i++)
        {
            ref readonly TileRange range =
                ref ranges[i];

            sb.AppendLine(
                $"Range {i}");

            sb.AppendLine(
                $"  Start Z: " +
                $"{range.StartZ * 0.1f:F1} м");

            sb.AppendLine(
                $"  End Z: " +
                $"{range.EndZ * 0.1f:F1} м");

            sb.AppendLine(
                $"  Thickness: " +
                $"{range.Thickness * 0.1f:F1} м");

            sb.AppendLine(
                $"  Material ID: " +
                $"{range.MaterialId}");

            sb.AppendLine(
                $"  State: " +
                $"{range.State}");

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
