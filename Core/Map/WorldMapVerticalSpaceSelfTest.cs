using Core.Map;

namespace Core.Map;

public static class WorldMapVerticalSpaceSelfTest
{
    public static bool FindsSubsurfaceTunnelAndWalkableFloor()
    {
        WorldMap map = new WorldMap(regionsX: 1, regionsY: 1);
        const int x = 24;
        const int y = 24;

        map.SetSolidHeight(x, y, 400, materialId: 1);
        map.ClearVoxelRange(x, y, startLayer: 20, endLayerExclusive: 30);

        return map.GetMaterialId(x, y, 25) == 0 &&
            map.FindTopOccupiedLayer(x, y, 25) == 19 &&
            map.IsColumnVolumeEmpty(x, y, 20, 29) &&
            map.IsColumnSolidBetween(x, y, 30, 35) &&
            map.TryFindWalkableSurface(
                x, y,
                minFloorLayer: 15,
                maxFloorLayer: 25,
                clearanceLayers: 10,
                out int floorLayer) &&
            floorLayer == 19;
    }
}
