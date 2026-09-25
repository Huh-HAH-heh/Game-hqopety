using Core.Map;

namespace World;

public static class UndergroundGenerator
{
    private const ushort RockMaterialId = 1;

    public static void Generate(
        WorldMap worldMap)
    {
        GenerateTestRoom(
            worldMap);
    }

    private static void GenerateTestRoom(
        WorldMap worldMap)
    {
        const int minX = 22;
        const int maxX = 27;
        const int minY = 22;
        const int maxY = 27;

        const ushort floorZ = 30;
        const ushort ceilingZ = 60;

        for (int y = minY;
             y <= maxY;
             y++)
        {
            for (int x = minX;
                 x <= maxX;
                 x++)
            {
                ushort surfaceZ =
                    worldMap.GetSurfaceHeightUnits(
                        x,
                        y);

                Span<TileRange> ranges =
                    stackalloc TileRange[3];

                int count = 0;

                ranges[count++] =
                    new TileRange
                    {
                        StartZ = 0,
                        EndZ = floorZ,
                        MaterialId = RockMaterialId,
                        State = WorldMap.StateSolid
                    };

                if (ceilingZ < surfaceZ)
                {
                    ranges[count++] =
                        new TileRange
                        {
                            StartZ = floorZ,
                            EndZ = ceilingZ,
                            MaterialId = 0,
                            State = WorldMap.StateEmpty
                        };

                    ranges[count++] =
                        new TileRange
                        {
                            StartZ = ceilingZ,
                            EndZ = surfaceZ,
                            MaterialId = RockMaterialId,
                            State = WorldMap.StateSolid
                        };
                }
                else if (surfaceZ > floorZ)
                {
                    ranges[count++] =
                        new TileRange
                        {
                            StartZ = floorZ,
                            EndZ = surfaceZ,
                            MaterialId = 0,
                            State = WorldMap.StateEmpty
                        };
                }

                worldMap.SetRanges(
                    x,
                    y,
                    ranges[..count]);
            }
        }
    }
}
