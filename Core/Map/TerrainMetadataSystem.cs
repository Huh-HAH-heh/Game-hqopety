using System.Text;

namespace Core.Map;

public static class TerrainMetadataSystem
{
    public static string InspectColumn(
        WorldMap worldMap,
        int x,
        int y)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("=== TERRAIN VOXEL COLUMN ===");
        sb.AppendLine($"Координаты: ({x}, {y})");
        sb.AppendLine(
            $"Высота поверхности: {worldMap.GetSurfaceHeight(x, y):F1} м");
        sb.AppendLine($"Слоёв: {worldMap.LayerCount}");

        int occupied = 0;

        for (int z = 0; z < worldMap.LayerCount; z++)
        {
            if (worldMap.GetMaterialId(x, y, z) != 0)
                occupied++;
        }

        sb.AppendLine($"Занято ячеек: {occupied}/{worldMap.LayerCount}");
        sb.AppendLine();

        for (int z = 0; z < worldMap.LayerCount; z++)
        {
            ushort materialId =
                worldMap.GetMaterialId(x, y, z);

            sb.AppendLine(
                $"Z={z,2} м | ID={materialId} | " +
                (materialId == 0 ? "пусто" : "материал"));
        }

        return sb.ToString();
    }
}
