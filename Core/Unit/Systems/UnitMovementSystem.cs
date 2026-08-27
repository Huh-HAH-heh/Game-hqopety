using Core.AI;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Systems.CombatPath;

namespace Core.Unit
{
    public sealed class UnitMovementSystem
    {
        private const int MaxUnitsPerTile = 3;
        private const float WaypointBlockedRetryTime = 0.05f;
        private const float PathBrokenCooldown = 1.0f;
        private const float MovementBlockedCooldown = 0.15f;
        private const float WaypointCompletionCooldown = 0.4f;

        public bool TryStartMove(
            UnitStore units,
            int unitId,
            SpatialCoord targetCell,
            UnitSpatialGrid spatialGrid,
            WorldMap map)
        {
            if (unitId < 0 || unitId >= units.Count)
                return false;

            if (units.HealthMasks[unitId] == 0)
                return false;

            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            if (movement.State == MovementState.Moving)
                return false;

            if (!MovementRules.CanStep(
                map,
                position.Spatial.X,
                position.Spatial.Y,
                targetCell.X,
                targetCell.Y,
                position.Spatial.Z))
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            if (spatialGrid.GetUnitsAt(targetCell).Count >= MaxUnitsPerTile)
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            movement.SourceCell = position.Spatial;
            movement.TargetCell = targetCell;
            movement.ZLevel = position.Spatial.Z;
            movement.Progress = 0f;
            movement.State = MovementState.Moving;

            return true;
        }

        public void Update(
            UnitStore units,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            EdificeStore edifices,
            float deltaTime,
            GroupMovementManager groupMovementManager)
        {
            if (deltaTime <= 0f)
                return;

            for (int unitId = 0; unitId < units.Count; unitId++)
            {
                if (units.MovementCooldowns[unitId] > 0f)
                {
                    units.MovementCooldowns[unitId] -= deltaTime;

                    if (units.MovementCooldowns[unitId] > 0f)
                    {
                        SmoothReturnToLogicalPosition(
                            units,
                            unitId,
                            deltaTime,
                            12f
                        );

                        continue;
                    }
                }

                if (units.MovementCooldowns[unitId] <= 0f &&
                    units.Movement[unitId].State == MovementState.Idle)
                {
                    units.MovementCooldowns[unitId] -= deltaTime;
                }

                if (units.HealthMasks[unitId] == 0)
                {
                    if (units.Movement[unitId].State == MovementState.Moving)
                        Stop(units, unitId);

                    continue;
                }

                ref var movement = ref units.Movement[unitId];
                ref var position = ref units.Positions[unitId];

                if (movement.State != MovementState.Moving)
                {
                    UpdateIdleUnit(
                        units,
                        spatialGrid,
                        map,
                        edifices,
                        groupMovementManager,
                        unitId,
                        ref movement,
                        ref position,
                        deltaTime
                    );
                }

                if (movement.State == MovementState.Moving)
                {
                    UpdateMovingUnit(
                        units,
                        spatialGrid,
                        map,
                        groupMovementManager,
                        unitId,
                        ref movement,
                        ref position,
                        deltaTime
                    );
                }
            }
        }

        private void UpdateIdleUnit(
            UnitStore units,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            EdificeStore edifices,
            GroupMovementManager groupMovementManager,
            int unitId,
            ref UnitMovement movement,
            ref UnitPosition position,
            float deltaTime)
        {
            if (!groupMovementManager.HasRouteForUnit(unitId))
            {
                position.RenderX +=
                    (position.Spatial.X - position.RenderX) *
                    6f *
                    deltaTime;

                position.RenderY +=
                    (position.Spatial.Y - position.RenderY) *
                    6f *
                    deltaTime;

                return;
            }

            SpatialCoord waypoint =
                groupMovementManager.GetCurrentWaypoint(unitId);

            if (waypoint == default &&
                position.Spatial != default)
            {
                movement.State = MovementState.Idle;
                return;
            }

            if (units.MovementCooldowns[unitId] < -2.0f)
            {
                ClearRoute(
                    units,
                    groupMovementManager,
                    unitId,
                    WaypointCompletionCooldown
                );

                return;
            }

            if (position.Spatial == waypoint)
            {
                groupMovementManager.AdvanceWaypoint(unitId);

                if (!groupMovementManager.HasRouteForUnit(unitId))
                {
                    movement.State = MovementState.Idle;
                    movement.Progress = 0f;
                    units.MovementCooldowns[unitId] =
                        WaypointCompletionCooldown;
                }

                return;
            }

            SpatialCoord actualTarget =
                ResolveWaypointTarget(
                    waypoint,
                    position.Spatial,
                    spatialGrid,
                    map
                );

            SpatialCoord nextStep =
                CalculateImmediateStep(
                    position.Spatial,
                    actualTarget
                );

            if (nextStep == position.Spatial)
            {
                groupMovementManager.AdvanceWaypoint(unitId);
                return;
            }

            if (spatialGrid.GetUnitsAt(nextStep).Count >= MaxUnitsPerTile)
            {
                units.MovementCooldowns[unitId] =
                    WaypointBlockedRetryTime;

                return;
            }

            bool pathClear =
                VisibilityChecker.HasLineOfSight(
                    map,
                    edifices,
                    position.Spatial.X,
                    position.Spatial.Y,
                    actualTarget.X,
                    actualTarget.Y,
                    position.Spatial.Z
                );

            if (!pathClear)
            {
                units.MovementCooldowns[unitId] =
                    PathBrokenCooldown;

                movement.State = MovementState.Idle;

                ClearRoute(
                    units,
                    groupMovementManager,
                    unitId,
                    PathBrokenCooldown
                );

                return;
            }

            units.MovementCooldowns[unitId] = 0f;

            TryStartMove(
                units,
                unitId,
                nextStep,
                spatialGrid,
                map
            );
        }

        private void UpdateMovingUnit(
            UnitStore units,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            GroupMovementManager groupMovementManager,
            int unitId,
            ref UnitMovement movement,
            ref UnitPosition position,
            float deltaTime)
        {
            float speed =
                CalculateSpeed(
                    units,
                    unitId
                );

            float heightMultiplier =
                MovementRules.GetHeightSpeedMultiplier(
                    map,
                    movement.SourceCell.X,
                    movement.SourceCell.Y,
                    movement.TargetCell.X,
                    movement.TargetCell.Y,
                    movement.ZLevel
                );

            speed *= heightMultiplier;

            movement.Progress +=
                speed * deltaTime;

            if (movement.Progress > 1f)
                movement.Progress = 1f;

            float targetRenderX =
                movement.SourceCell.X +
                (movement.TargetCell.X - movement.SourceCell.X) *
                movement.Progress;

            float targetRenderY =
                movement.SourceCell.Y +
                (movement.TargetCell.Y - movement.SourceCell.Y) *
                movement.Progress;

            position.RenderX +=
                (targetRenderX - position.RenderX) *
                16f *
                deltaTime;

            position.RenderY +=
                (targetRenderY - position.RenderY) *
                16f *
                deltaTime;

            if (movement.Progress < 1f)
                return;

            FinishMove(
                units,
                spatialGrid,
                unitId
            );

            if (!groupMovementManager.HasRouteForUnit(unitId))
            {
                movement.State = MovementState.Idle;
                movement.Progress = 0f;
                return;
            }

            SpatialCoord waypoint =
                groupMovementManager.GetCurrentWaypoint(unitId);

            if (position.Spatial == waypoint)
            {
                groupMovementManager.AdvanceWaypoint(unitId);

                movement.State = MovementState.Idle;
                movement.Progress = 0f;

                if (!groupMovementManager.HasRouteForUnit(unitId))
                {
                    units.MovementCooldowns[unitId] =
                        WaypointCompletionCooldown;
                }

                return;
            }

            SpatialCoord nextStep =
                CalculateImmediateStep(
                    position.Spatial,
                    waypoint
                );

            bool canContinue =
                nextStep != position.Spatial;

            if (canContinue)
            {
                canContinue =
                    spatialGrid
                        .GetUnitsAt(nextStep)
                        .Count < MaxUnitsPerTile;
            }

            if (canContinue)
            {
                canContinue =
                    MovementRules.CanStep(
                        map,
                        position.Spatial.X,
                        position.Spatial.Y,
                        nextStep.X,
                        nextStep.Y,
                        position.Spatial.Z
                    );
            }

            if (canContinue)
            {
                movement.SourceCell =
                    position.Spatial;

                movement.TargetCell =
                    nextStep;

                movement.Progress = 0f;
                movement.State =
                    MovementState.Moving;
            }
            else
            {
                movement.State =
                    MovementState.Idle;

                movement.Progress = 0f;

                units.MovementCooldowns[unitId] =
                    MovementBlockedCooldown;
            }
        }

        private SpatialCoord ResolveWaypointTarget(
            SpatialCoord waypoint,
            SpatialCoord current,
            UnitSpatialGrid spatialGrid,
            WorldMap map)
        {
            if (spatialGrid.GetUnitsAt(waypoint).Count < MaxUnitsPerTile)
                return waypoint;

            if (current == waypoint)
                return waypoint;

            return FindNearestFreeTile(
                waypoint,
                spatialGrid,
                map
            );
        }

        private SpatialCoord FindNearestFreeTile(
            SpatialCoord center,
            UnitSpatialGrid spatialGrid,
            WorldMap map)
        {
            int x = 0;
            int y = 0;
            int dx = 0;
            int dy = -1;

            const int maxSteps = 81;

            for (int i = 0; i < maxSteps; i++)
            {
                if (x >= -4 && x <= 4 &&
                    y >= -4 && y <= 4)
                {
                    SpatialCoord testTile =
                        new SpatialCoord(
                            center.X + x,
                            center.Y + y,
                            center.Z
                        );

                    if (spatialGrid.GetUnitsAt(testTile).Count < MaxUnitsPerTile &&
                        MovementRules.CanStep(
                            map,
                            center.X,
                            center.Y,
                            testTile.X,
                            testTile.Y,
                            center.Z))
                    {
                        return testTile;
                    }
                }

                if (x == y ||
                    (x < 0 && x == -y) ||
                    (x > 0 && x == 1 - y))
                {
                    int temp = dx;
                    dx = -dy;
                    dy = temp;
                }

                x += dx;
                y += dy;
            }

            return center;
        }

        private SpatialCoord CalculateImmediateStep(
            SpatialCoord current,
            SpatialCoord target)
        {
            int dx = target.X - current.X;
            int dy = target.Y - current.Y;

            if (dx != 0)
            {
                return new SpatialCoord(
                    current.X + (dx > 0 ? 1 : -1),
                    current.Y,
                    current.Z
                );
            }

            if (dy != 0)
            {
                return new SpatialCoord(
                    current.X,
                    current.Y + (dy > 0 ? 1 : -1),
                    current.Z
                );
            }

            return current;
        }

        private void FinishMove(
            UnitStore units,
            UnitSpatialGrid spatialGrid,
            int unitId)
        {
            ref var movement =
                ref units.Movement[unitId];

            ref var position =
                ref units.Positions[unitId];

            spatialGrid.Remove(
                position.Spatial,
                unitId
            );

            position.Spatial =
                movement.TargetCell;

            spatialGrid.Add(
                position.Spatial,
                unitId
            );

            movement.Progress = 0f;
            movement.State =
                MovementState.Idle;
        }

        private float CalculateSpeed(
            UnitStore units,
            int unitId)
        {
            float mass =
                units.DynamicMass[unitId];

            float massFactor =
                1f - mass / 1000f;

            if (massFactor < 0.1f)
                massFactor = 0.1f;

            return units.Movement[unitId].Speed *
                   massFactor;
        }

        private void SmoothReturnToLogicalPosition(
            UnitStore units,
            int unitId,
            float deltaTime,
            float strength)
        {
            ref var position =
                ref units.Positions[unitId];

            position.RenderX +=
                (position.Spatial.X - position.RenderX) *
                strength *
                deltaTime;

            position.RenderY +=
                (position.Spatial.Y - position.RenderY) *
                strength *
                deltaTime;
        }

        private void ClearRoute(
            UnitStore units,
            GroupMovementManager groupMovementManager,
            int unitId,
            float cooldown)
        {
            while (groupMovementManager.HasRouteForUnit(unitId))
            {
                groupMovementManager.AdvanceWaypoint(unitId);
            }

            units.MovementCooldowns[unitId] =
                cooldown;

            units.Movement[unitId].State =
                MovementState.Idle;

            units.Movement[unitId].Progress =
                0f;
        }

        public void Stop(
            UnitStore units,
            int unitId)
        {
            if (unitId < 0 || unitId >= units.Count)
                return;

            ref var movement =
                ref units.Movement[unitId];

            ref var position =
                ref units.Positions[unitId];

            movement.SourceCell =
                position.Spatial;

            movement.TargetCell =
                position.Spatial;

            movement.Progress = 0f;
            movement.State =
                MovementState.Idle;
        }
    }
}