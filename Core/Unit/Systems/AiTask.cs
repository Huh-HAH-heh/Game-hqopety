using Core.Items;
using Core.Map;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents;

namespace Core.Unit.Systems;

public interface IAiTask
{
    AiOpCode OpCode { get; }

    void Update(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, EdificeStore edifices, int unitId, ref AiCommand command, float deltaTime);
    void Cancel(int unitId);
}