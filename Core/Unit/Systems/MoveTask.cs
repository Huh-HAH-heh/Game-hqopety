using Core.AI;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents;

namespace Core.Unit.Systems;

public sealed class MoveTask : IAiTask
{
    private readonly PathfindingSystem _pathfinding;
    private readonly UnitMovementSystem _movement;

    private int[] _routeIds = new int[64];
    private int[] _routeIndices = new int[64];

    public AiOpCode OpCode => AiOpCode.MoveToTarget;

    public MoveTask(PathfindingSystem pathfinding, UnitMovementSystem movement)
    {
        _pathfinding = pathfinding;
        _movement = movement;
        _movement.MoveInterrupted += Interrupt;
        ClearRoutes();
    }

    public void Update(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, EdificeStore edifices, int unitId, ref AiCommand command, float deltaTime)
    {
        EnsureCapacity(unitId);

        ref UnitPosition position = ref units.Positions[unitId];
        ref UnitMovement movement = ref units.Movement[unitId];

        SpatialCoord target = new(command.TargetX, command.TargetY, command.TargetZ);

        if (position.Spatial == target)
        {
            ClearRoute(unitId);
            units.CpuPopCommand(unitId);
            return;
        }

        if (movement.State == MovementState.Moving)
            return;

        int routeId = _routeIds[unitId];

        if (routeId < 0)
        {
            MapLayer layer = map.GetLayer(position.Spatial.Z);

            if (layer == null)
                return;

            routeId = _pathfinding.RequestPath(layer, position.Spatial, target);

            if (routeId < 0)
                return;

            _routeIds[unitId] = routeId;
            _routeIndices[unitId] = 0;
            return;
        }

        PathRequestState state = _pathfinding.GetState(routeId);

        if (state == PathRequestState.Pending)
            return;

        if (state != PathRequestState.Completed)
        {
            ClearRoute(unitId);
            return;
        }

        if (!_pathfinding.TryGetRoute(routeId, out SpatialCoord[] path, out int length))
        {
            ClearRoute(unitId);
            return;
        }

        int index = _routeIndices[unitId];

        if (index == 0 && length > 0 && path[0] == position.Spatial)
            index = 1;

        if (index >= length)
        {
            ClearRoute(unitId);
            return;
        }

        if (!_movement.TryStartMove(units, unitId, path[index], spatialGrid, map))
            return;

        _routeIndices[unitId] = index + 1;
    }

    public void Cancel(int unitId)
    {
        ClearRoute(unitId);
    }

    private void Interrupt(int unitId)
    {
        ClearRoute(unitId);
    }

    private void ClearRoute(int unitId)
    {
        EnsureCapacity(unitId);

        int routeId = _routeIds[unitId];

        if (routeId >= 0)
            _pathfinding.Remove(routeId);

        _routeIds[unitId] = -1;
        _routeIndices[unitId] = 0;
    }

    private void ClearRoutes()
    {
        for (int i = 0; i < _routeIds.Length; i++)
            _routeIds[i] = -1;
    }

    private void EnsureCapacity(int unitId)
    {
        if (unitId < _routeIds.Length)
            return;

        int size = _routeIds.Length;

        while (size <= unitId)
            size *= 2;

        Array.Resize(ref _routeIds, size);
        Array.Resize(ref _routeIndices, size);

        for (int i = unitId; i < size; i++)
            _routeIds[i] = -1;
    }
}