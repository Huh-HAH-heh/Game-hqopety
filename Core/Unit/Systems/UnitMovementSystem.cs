using Core.Map;
using Core.Unit.Components;
using System;

namespace Core.Unit;

public sealed class UnitMovementSystem
{
    private const int MaxUnitsPerTile = 3;
    private const float BlockedCooldown = 0.10f;

    public event Action<int>? MoveInterrupted;

    public void Update(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, float deltaTime)
    {
        if (deltaTime <= 0f || units == null || map == null)
            return;

        for (int unitId = 0; unitId < units.Count; unitId++)
        {
            if (units.HealthMasks[unitId] == 0)
            {
                Stop(units, unitId);
                continue;
            }

            if (units.MovementCooldowns[unitId] > 0f)
            {
                units.MovementCooldowns[unitId] -= deltaTime;

                if (units.MovementCooldowns[unitId] > 0f)
                {
                    ReturnToLogical(units, unitId, deltaTime);
                    continue;
                }

                units.Movement[unitId].State = MovementState.Idle;
            }

            ref UnitMovement movement = ref units.Movement[unitId];
            ref UnitPosition position = ref units.Positions[unitId];

            if (movement.State != MovementState.Moving)
            {
                ReturnToLogical(units, unitId, deltaTime);
                continue;
            }

            UpdateMoving(units, spatialGrid, map, unitId, ref movement, ref position, deltaTime);
        }
    }

    public bool TryStartMove(UnitStore units, int unitId, SpatialCoord target, UnitSpatialGrid spatialGrid, WorldMap map)
    {
        if (!CanUseUnit(units, unitId))
            return false;

        ref UnitMovement movement = ref units.Movement[unitId];
        ref UnitPosition position = ref units.Positions[unitId];

        if (movement.State == MovementState.Moving)
            return false;

        SpatialCoord current = position.Spatial;

        int dx = target.X - current.X;
        int dy = target.Y - current.Y;
        int dz = target.Z - current.Z;

        if (dx < -1 || dx > 1 || dy < -1 || dy > 1 || dz < -1 || dz > 1)
            return false;

        if (dx == 0 && dy == 0 && dz == 0)
            return false;

        if (!MovementRules.CanStep(map, current.X, current.Y, target.X, target.Y, current.Z))
        {
            Interrupt(units, unitId);
            return false;
        }

        if (IsReserved(units, unitId, target, spatialGrid))
        {
            Interrupt(units, unitId);
            return false;
        }

        movement.SourceCell = current;
        movement.TargetCell = target;
        movement.ZLevel = current.Z;
        movement.Progress = 0f;
        movement.State = MovementState.Moving;

        return true;
    }

    private void UpdateMoving(UnitStore units, UnitSpatialGrid spatialGrid, WorldMap map, int unitId, ref UnitMovement movement, ref UnitPosition position, float deltaTime)
    {
        float speed = CalculateSpeed(units, unitId);

        speed *= MovementRules.GetHeightSpeedMultiplier(
            map,
            movement.SourceCell.X,
            movement.SourceCell.Y,
            movement.TargetCell.X,
            movement.TargetCell.Y,
            movement.ZLevel);

        if (speed <= 0f)
        {
            movement.State = MovementState.Blocked;
            movement.Progress = 0f;
            units.MovementCooldowns[unitId] = BlockedCooldown;
            position.RenderX = movement.SourceCell.X;
            position.RenderY = movement.SourceCell.Y;
            return;
        }

        movement.Progress += speed * deltaTime;

        if (movement.Progress > 1f)
            movement.Progress = 1f;

        position.RenderX = movement.SourceCell.X + (movement.TargetCell.X - movement.SourceCell.X) * movement.Progress;
        position.RenderY = movement.SourceCell.Y + (movement.TargetCell.Y - movement.SourceCell.Y) * movement.Progress;

        if (movement.Progress < 1f)
            return;

        position.RenderX = movement.TargetCell.X;
        position.RenderY = movement.TargetCell.Y;

        FinishMove(units, spatialGrid, unitId);

        movement.Progress = 0f;
        movement.State = MovementState.Idle;
    }

    private void FinishMove(UnitStore units, UnitSpatialGrid spatialGrid, int unitId)
    {
        ref UnitMovement movement = ref units.Movement[unitId];
        ref UnitPosition position = ref units.Positions[unitId];

        spatialGrid.Remove(position.Spatial, unitId);
        position.Spatial = movement.TargetCell;
        spatialGrid.Add(position.Spatial, unitId);
    }

    private bool IsReserved(UnitStore units, int unitId, SpatialCoord target, UnitSpatialGrid spatialGrid)
    {
        int occupancy = spatialGrid.GetUnitsAt(target).Count;

        if (occupancy >= MaxUnitsPerTile)
            return true;

        return occupancy + CountMovingInto(units, unitId, target) >= MaxUnitsPerTile;
    }

    private int CountMovingInto(UnitStore units, int ignoredUnitId, SpatialCoord target)
    {
        int count = 0;

        for (int i = 0; i < units.Count; i++)
        {
            if (i == ignoredUnitId || units.HealthMasks[i] == 0)
                continue;

            ref UnitMovement movement = ref units.Movement[i];

            if (movement.State == MovementState.Moving && movement.TargetCell == target)
                count++;
        }

        return count;
    }

    private void Interrupt(UnitStore units, int unitId)
    {
        ref UnitMovement movement = ref units.Movement[unitId];

        movement.State = MovementState.Blocked;
        movement.Progress = 0f;
        units.MovementCooldowns[unitId] = BlockedCooldown;

        MoveInterrupted?.Invoke(unitId);
    }

    private void Stop(UnitStore units, int unitId)
    {
        ref UnitMovement movement = ref units.Movement[unitId];
        ref UnitPosition position = ref units.Positions[unitId];

        movement.SourceCell = position.Spatial;
        movement.TargetCell = position.Spatial;
        movement.Progress = 0f;
        movement.State = MovementState.Idle;

        position.RenderX = position.Spatial.X;
        position.RenderY = position.Spatial.Y;
    }

    private void ReturnToLogical(UnitStore units, int unitId, float deltaTime)
    {
        ref UnitPosition position = ref units.Positions[unitId];

        float t = 8f * deltaTime;

        if (t > 1f)
            t = 1f;

        position.RenderX += (position.Spatial.X - position.RenderX) * t;
        position.RenderY += (position.Spatial.Y - position.RenderY) * t;
    }

    private float CalculateSpeed(UnitStore units, int unitId)
    {
        float factor = 1f - units.DynamicMass[unitId] / 1000f;

        if (factor < 0.1f)
            factor = 0.1f;

        return units.Movement[unitId].Speed * factor;
    }

    private bool CanUseUnit(UnitStore units, int unitId)
    {
        return units != null && unitId >= 0 && unitId < units.Count && units.HealthMasks[unitId] != 0;
    }
}