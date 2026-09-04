using Core.Items;
using Core.Map;
using Core.Unit;
using Core.Unit.Components;
using Core.Unit.Systems;

namespace RimClone.Render;

public sealed class WorldSimulation
{
    private readonly UnitCombatSystem _unitCombatSystem = new();
    private readonly CombatEffectSystem _effectSystem = new();

    private readonly UnitMovementSystem _movementSystem;

    public CombatEffectSystem EffectSystem => _effectSystem;

    public WorldSimulation(UnitMovementSystem movementSystem)
    {
        _movementSystem = movementSystem;
    }

    public void Update(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, EdificeStore edifices, float microCellPixelSize, float deltaTime)
    {
        _movementSystem.Update(units, spatialGrid, map, deltaTime);

        _unitCombatSystem.Update(
            units,
            spatialGrid,
            map,
            edifices,
            _effectSystem,
            microCellPixelSize,
            deltaTime);

        _effectSystem.Update(deltaTime);
        units.UpdateHealthSystems(deltaTime);

        for (int i = 0; i < units.Count; i++)
        {
            if (units.HealthMasks[i] == 0)
                spatialGrid.Remove(units.Positions[i].Spatial, i);
        }
    }
}