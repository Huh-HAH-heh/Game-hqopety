using Core.AI;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents;
using System;

namespace Core.Unit.Systems;

public sealed class MoveTask : IAiTask
{
    private readonly PathfindingSystem _pathfinding;
    private readonly UnitMovementSystem _movement;

    private int[] _routeIds = new int[64];
    private int[] _routeIndices = new int[64];

    /*
     * Цель текущего MoveToTarget.
     */
    private SpatialCoord[] _targets =
        new SpatialCoord[64];

    /*
     * Перестроение маршрута запрошено.
     *
     * true:
     * старый маршрут больше не используем,
     * как только его A* закончит — строим новый
     * от текущей позиции.
     */
    private bool[] _repathPending =
        new bool[64];

    public AiOpCode OpCode =>
        AiOpCode.MoveToTarget;

    public MoveTask(
        PathfindingSystem pathfinding,
        UnitMovementSystem movement)
    {
        _pathfinding = pathfinding;
        _movement = movement;

        _movement.MoveInterrupted += Interrupt;

        ClearRoutes();
    }

    public void Update(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        EdificeStore edifices,
        int unitId,
        ref AiCommand command,
        float deltaTime)
    {
        EnsureCapacity(unitId);

        ref UnitPosition position =
            ref units.Positions[unitId];

        ref UnitMovement movement =
            ref units.Movement[unitId];

        SpatialCoord target =
            new SpatialCoord(
                command.TargetX,
                command.TargetY,
                command.TargetZ);

        _targets[unitId] = target;

        /*
         * Юнит уже пришёл.
         */
        if (position.Spatial == target)
        {
            ClearRoute(unitId);

            units.CpuPopCommand(unitId);

            return;
        }

        /*
         * Сейчас выполняется физический шаг.
         */
        if (movement.State ==
            MovementState.Moving)
        {
            return;
        }

        /*
         * После блокировки выдерживаем cooldown.
         */
        if (units.MovementCooldowns[unitId] > 0f)
        {
            return;
        }

        int routeId =
            _routeIds[unitId];

        /*
         * Нет RouteTask.
         *
         * Строим первый маршрут.
         */
        if (routeId < 0)
        {
            RequestNewRoute(
                units,
                map,
                unitId,
                target);

            return;
        }

        /*
         * Нам нужно перестроение.
         *
         * Старый RouteTask не удаляем.
         *
         * Ждём, пока его текущий A*
         * закончит работу.
         */
        if (_repathPending[unitId])
        {
            PathRequestState repathState =
                _pathfinding.GetState(
                    routeId);

            /*
             * Старый A* ещё работает.
             *
             * Ничего не делаем.
             */
            if (repathState ==
                    PathRequestState.Pending ||
                repathState ==
                    PathRequestState.Processing)
            {
                return;
            }

            /*
             * Старый запрос уже завершился.
             *
             * Теперь можно переиспользовать
             * тот же RouteId для нового A*.
             */
            if (repathState ==
                PathRequestState.Completed ||
                repathState ==
                PathRequestState.Failed)
            {
                MapLayer layer =
                    map.GetLayer(
                        position.Spatial.Z);

                if (layer == null)
                    return;

                bool queued =
                    _pathfinding.RequeuePath(
                        routeId,
                        position.Spatial,
                        target,
                        PathPriority.Normal);

                if (!queued)
                    return;

                _repathPending[unitId] =
                    false;

                _routeIndices[unitId] =
                    0;

                return;
            }

            /*
             * Если RouteTask был отменён/потерян,
             * создаём новую.
             */
            ClearRouteWithoutRemoving(unitId);

            RequestNewRoute(
                units,
                map,
                unitId,
                target);

            return;
        }

        PathRequestState state =
            _pathfinding.GetState(
                routeId);

        /*
         * A* ещё не закончил.
         */
        if (state ==
                PathRequestState.Pending ||
            state ==
                PathRequestState.Processing)
        {
            return;
        }

        /*
         * A* не смог построить маршрут.
         */
        if (state !=
            PathRequestState.Completed)
        {
            ClearRoute(unitId);
            return;
        }

        if (!_pathfinding.TryGetRoute(
                routeId,
                out SpatialCoord[]? path,
                out int length))
        {
            ClearRoute(unitId);
            return;
        }

        int index =
            _routeIndices[unitId];

        /*
         * Нулевая клетка маршрута —
         * текущая позиция юнита.
         */
        if (index == 0 &&
            length > 0 &&
            path[0] == position.Spatial)
        {
            index = 1;
        }

        /*
         * Маршрут закончился.
         */
        if (index >= length)
        {
            ClearRoute(unitId);
            return;
        }

        /*
         * Пытаемся пройти следующий тайл.
         *
         * При блокировке UnitMovementSystem
         * вызовет:
         *
         * MoveInterrupted(unitId)
         */
        if (!_movement.TryStartMove(
                units,
                unitId,
                path[index],
                spatialGrid,
                map))
        {
            return;
        }

        _routeIndices[unitId] =
            index + 1;
    }

    public void Cancel(int unitId)
    {
        ClearRoute(unitId);
    }

    /*
     * Движение было прервано.
     *
     * НИЧЕГО НЕ УДАЛЯЕМ.
     *
     * Просто говорим:
     * "когда текущий A* закончит,
     *  перестрой этот же RouteTask
     *  от текущей позиции".
     */
    private void Interrupt(int unitId)
    {
        EnsureCapacity(unitId);

        int routeId =
            _routeIds[unitId];

        if (routeId < 0)
            return;

        _repathPending[unitId] = true;
        _routeIndices[unitId] = 0;
    }

    private void RequestNewRoute(
        UnitStore units,
        WorldMap map,
        int unitId,
        SpatialCoord target)
    {
        ref UnitPosition position =
            ref units.Positions[unitId];

        MapLayer layer =
            map.GetLayer(
                position.Spatial.Z);

        if (layer == null)
            return;

        int routeId =
            _pathfinding.RequestPath(
                layer,
                position.Spatial,
                target,
                PathPriority.Normal);

        if (routeId < 0)
            return;

        _routeIds[unitId] =
            routeId;

        _routeIndices[unitId] =
            0;

        _repathPending[unitId] =
            false;
    }

    /*
     * Полностью убрать связь unit -> route.
     */
    private void ClearRoute(
        int unitId)
    {
        EnsureCapacity(unitId);

        int routeId =
            _routeIds[unitId];

        if (routeId >= 0)
        {
            _pathfinding.Remove(
                routeId);
        }

        ClearRouteWithoutRemoving(
            unitId);
    }

    /*
     * Убрать локальное состояние,
     * но не трогать RouteTask.
     */
    private void ClearRouteWithoutRemoving(
        int unitId)
    {
        _routeIds[unitId] = -1;
        _routeIndices[unitId] = 0;
        _repathPending[unitId] = false;
    }

    private void ClearRoutes()
    {
        for (int i = 0;
             i < _routeIds.Length;
             i++)
        {
            _routeIds[i] = -1;
            _routeIndices[i] = 0;
            _repathPending[i] = false;
        }
    }

    private void EnsureCapacity(
        int unitId)
    {
        if (unitId < _routeIds.Length)
            return;

        int oldSize =
            _routeIds.Length;

        int newSize =
            oldSize;

        while (newSize <= unitId)
            newSize *= 2;

        Array.Resize(
            ref _routeIds,
            newSize);

        Array.Resize(
            ref _routeIndices,
            newSize);

        Array.Resize(
            ref _targets,
            newSize);

        Array.Resize(
            ref _repathPending,
            newSize);

        /*
         * Новые элементы RouteId должны быть -1,
         * потому что 0 — валидный RouteId.
         */
        for (int i = oldSize;
             i < newSize;
             i++)
        {
            _routeIds[i] = -1;
            _routeIndices[i] = 0;
            _repathPending[i] = false;
        }
    }
}