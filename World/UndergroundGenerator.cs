using Core.Map;
using Core.Structs;

namespace World;

public static class UndergroundGenerator
{
    private const int Scale =
        MapRegion.SubDivision;

    public static void Generate(
        WorldMap worldMap)
    {
        MapLayer layer =
            worldMap.GetLayer(-1);

        if (layer == null)
            return;

        GenerateTestRoom(layer);
    }

    private static void GenerateTestRoom(
        MapLayer layer)
    {
        int minMcX =
            22 * Scale;

        int maxMcX =
            27 * Scale;

        int minMcY =
            22 * Scale;

        int maxMcY =
            27 * Scale;

        for (int my = minMcY;
             my <= maxMcY;
             my++)
        {
            for (int mx = minMcX;
                 mx <= maxMcX;
                 mx++)
            {
                ref MicroCell cell =
                    ref layer.GetMicroCell(
                        mx,
                        my);

                cell.EdificeId = 0;
                cell.FloorId = 2;

                // Ровный пол подземелья.
                cell.Height = 3;
            }
        }
    }
}