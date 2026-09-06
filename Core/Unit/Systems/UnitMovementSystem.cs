using Core.Map;
using Core.Unit.Components;
using System;

namespace Core.Unit;

public sealed class UnitMovementSystem
{
    private const int MaxUnitsPerTile = 3;

    private const float BlockedCooldown = 0.10f;

    /*
     * Максимальное время без продвижения
     * по текущему шагу.
     *
     * Если Progress не меняется дольше этого времени,
     * текущий маршрут считается непригодным.
     */
    private const float StuckTimeout = 0.50f;

    private static readonly int[] DirectionX =
    {
        1,
        -1,
        0,
        0
    };

    private static readonly int[] DirectionY =
    {
        0,
        0,
        1,
        -1
    };

    public event Action<int>? MoveInterrupted;

    public void Update(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        float deltaTime)
    {
        if (deltaTime <= 0f ||
            units == null ||
            map == null)
        {
            return;
        }

        for (int unitId = 0;
             unitId < units.Count;
             unitId++)
        {
            /*
             * Мёртвый юнит останавливается.
             */
            if (units.HealthMasks[unitId] == 0)
            {
                Stop(
                    units,
                    unitId);

                continue;
            }

            /*
             * Краткая пауза после interruption.
             *
             * Приказ при этом НЕ отменяется.
             */
            if (units.MovementCooldowns[unitId] > 0f)
            {
                units.MovementCooldowns[unitId] -=
                    deltaTime;

                if (units.MovementCooldowns[unitId] > 0f)
                {
                    ReturnToLogical(
                        units,
                        unitId,
                        deltaTime);

                    continue;
                }

                units.MovementCooldowns[unitId] =
                    0f;

                units.Movement[unitId].State =
                    MovementState.Idle;
            }

            ref UnitMovement movement =
                ref units.Movement[unitId];

            ref UnitPosition position =
                ref units.Positions[unitId];

            if (movement.State !=
                MovementState.Moving)
            {
                ReturnToLogical(
                    units,
                    unitId,
                    deltaTime);

                continue;
            }

            UpdateMoving(
                units,
                spatialGrid,
                map,
                unitId,
                ref movement,
                ref position,
                deltaTime);
        }
    }

    public bool TryStartMove(
        UnitStore units,
        int unitId,
        SpatialCoord requestedTarget,
        UnitSpatialGrid spatialGrid,
        WorldMap map)
    {
        if (!CanUseUnit(
                units,
                unitId))
        {
            return false;
        }

        ref UnitMovement movement =
            ref units.Movement[unitId];

        ref UnitPosition position =
            ref units.Positions[unitId];

        if (movement.State ==
            MovementState.Moving)
        {
            return false;
        }

        SpatialCoord current =
            position.Spatial;

        int dx =
            requestedTarget.X -
            current.X;

        int dy =
            requestedTarget.Y -
            current.Y;

        int dz =
            requestedTarget.Z -
            current.Z;

        /*
         * Один физический шаг —
         * только соседняя клетка.
         */
        if (dx < -1 || dx > 1 ||
            dy < -1 || dy > 1 ||
            dz < -1 || dz > 1)
        {
            /*
             * Для нормального A* это не должно происходить.
             * Но если произошло — текущий шаг невозможен.
             */
            Interrupt(
                units,
                unitId);

            return false;
        }

        if (dx == 0 &&
            dy == 0 &&
            dz == 0)
        {
            return false;
        }

        /*
         * ============================================================
         * ФИЗИЧЕСКАЯ ПРОХОДИМОСТЬ
         * ============================================================
         *
         * Если карта больше не позволяет перейти,
         * старый маршрут нужно прервать.
         */
        if (!MovementRules.CanStep(
                map,
                current.X,
                current.Y,
                requestedTarget.X,
                requestedTarget.Y,
                current.Z))
        {
            Interrupt(
                units,
                unitId);

            return false;
        }

        SpatialCoord target =
            requestedTarget;

        bool alternativeMove =
            false;

        /*
         * ============================================================
         * ПЕРЕПОЛНЕННЫЙ ТАЙЛ
         * ============================================================
         */

        int occupancy =
            spatialGrid.GetUnitsAt(
                requestedTarget).Count;

        if (occupancy >=
            MaxUnitsPerTile)
        {
            /*
             * Целевая клетка забита.
             *
             * Пытаемся аккуратно выпустить юнита
             * на свободного соседа.
             */
            if (!TryFindAlternativeTile(
                    spatialGrid,
                    map,
                    current,
                    requestedTarget,
                    movement,
                    out target))
            {
                /*
                 * Никкуда выйти нельзя.
                 *
                 * Прерываем текущий маршрут.
                 * CPU должен перестроить его.
                 */
                Interrupt(
                    units,
                    unitId);

                return false;
            }

            /*
             * Это НЕ interruption сейчас.
             *
             * Сначала юнит реально должен дойти
             * до alternative tile.
             */
            alternativeMove =
                true;

            movement.LastAlternativeCell =
                target;

            movement.HasLastAlternativeCell =
                true;
        }
        else
        {
            /*
             * Идём непосредственно в нужный тайл.
             */
            movement.HasLastAlternativeCell =
                false;
        }

        /*
         * Запоминаем, что текущий переход —
         * вынужденный alternative move.
         */
        movement.IsAlternativeMove =
            alternativeMove;

        /*
         * ============================================================
         * ЗАПУСК ШАГА
         * ============================================================
         */

        movement.SourceCell =
            current;

        movement.TargetCell =
            target;

        movement.ZLevel =
            current.Z;

        movement.Progress =
            0f;

        movement.StuckTimer =
            0f;

        movement.State =
            MovementState.Moving;

        return true;
    }

    private bool TryFindAlternativeTile(
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        SpatialCoord current,
        SpatialCoord requestedTarget,
        in UnitMovement movement,
        out SpatialCoord alternative)
    {
        alternative =
            current;

        int bestOccupancy =
            int.MaxValue;

        int bestDistance =
            int.MaxValue;

        bool found =
            false;

        /*
         * Только 4 cardinal направления.
         */
        for (int i = 0;
             i < 4;
             i++)
        {
            SpatialCoord candidate =
                new SpatialCoord(
                    current.X + DirectionX[i],
                    current.Y + DirectionY[i],
                    current.Z);

            /*
             * Не возвращаемся мгновенно
             * в предыдущий forced-alternative.
             */
            if (movement.HasLastAlternativeCell &&
                candidate.X ==
                    movement.LastAlternativeCell.X &&
                candidate.Y ==
                    movement.LastAlternativeCell.Y &&
                candidate.Z ==
                    movement.LastAlternativeCell.Z)
            {
                continue;
            }

            /*
             * Кандидат должен быть физически проходим.
             */
            if (!MovementRules.CanStep(
                    map,
                    current.X,
                    current.Y,
                    candidate.X,
                    candidate.Y,
                    current.Z))
            {
                continue;
            }

            /*
             * Не входим четвёртым.
             */
            int occupancy =
                spatialGrid.GetUnitsAt(
                    candidate).Count;

            if (occupancy >=
                MaxUnitsPerTile)
            {
                continue;
            }

            /*
             * Чем ближе к исходной цели,
             * тем предпочтительнее кандидат.
             */
            int distanceToTarget =
                Math.Abs(
                    candidate.X -
                    requestedTarget.X) +

                Math.Abs(
                    candidate.Y -
                    requestedTarget.Y);

            /*
             * Приоритет:
             *
             * 1. меньше юнитов;
             * 2. ближе к настоящей цели.
             */
            if (occupancy < bestOccupancy ||
                (occupancy == bestOccupancy &&
                 distanceToTarget < bestDistance))
            {
                bestOccupancy =
                    occupancy;

                bestDistance =
                    distanceToTarget;

                alternative =
                    candidate;

                found =
                    true;
            }
        }

        return found;
    }

    private void UpdateMoving(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        int unitId,
        ref UnitMovement movement,
        ref UnitPosition position,
        float deltaTime)
    {
        float speed =
            CalculateSpeed(
                units,
                unitId);

        speed *=
            MovementRules.GetHeightSpeedMultiplier(
                map,
                movement.SourceCell.X,
                movement.SourceCell.Y,
                movement.TargetCell.X,
                movement.TargetCell.Y,
                movement.ZLevel);

        /*
         * Скорость отсутствует.
         *
         * Это не мгновенный interruption.
         * Watchdog сначала ждёт StuckTimeout.
         */
        if (speed <= 0f)
        {
            movement.StuckTimer +=
                deltaTime;

            if (movement.StuckTimer >=
                StuckTimeout)
            {
                Interrupt(
                    units,
                    unitId);
            }

            return;
        }

        float previousProgress =
            movement.Progress;

        movement.Progress +=
            speed *
            deltaTime;

        if (movement.Progress > 1f)
        {
            movement.Progress =
                1f;
        }

        /*
         * ============================================================
         * WATCHDOG
         * ============================================================
         */

        if (movement.Progress >
            previousProgress)
        {
            movement.StuckTimer =
                0f;
        }
        else
        {
            movement.StuckTimer +=
                deltaTime;

            if (movement.StuckTimer >=
                StuckTimeout)
            {
                Interrupt(
                    units,
                    unitId);

                return;
            }
        }

        /*
         * ============================================================
         * ВИЗУАЛЬНОЕ ДВИЖЕНИЕ
         * ============================================================
         */

        position.RenderX =
            movement.SourceCell.X +
            (movement.TargetCell.X -
             movement.SourceCell.X) *
            movement.Progress;

        position.RenderY =
            movement.SourceCell.Y +
            (movement.TargetCell.Y -
             movement.SourceCell.Y) *
            movement.Progress;

        /*
         * Шаг ещё не закончен.
         */
        if (movement.Progress < 1f)
        {
            return;
        }

        /*
         * ============================================================
         * ШАГ ЗАВЕРШЁН
         * ============================================================
         */

        position.RenderX =
            movement.TargetCell.X;

        position.RenderY =
            movement.TargetCell.Y;

        /*
         * Сохраняем тип шага ДО сброса состояния.
         */
        bool wasAlternativeMove =
            movement.IsAlternativeMove;

        FinishMove(
            units,
            spatialGrid,
            unitId);

        movement.Progress =
            0f;

        movement.StuckTimer =
            0f;

        movement.State =
            MovementState.Idle;

        movement.IsAlternativeMove =
            false;

        /*
         * ============================================================
         * FORCED ALTERNATIVE FINISHED
         * ============================================================
         *
         * Теперь, когда юнит ФАКТИЧЕСКИ оказался
         * на альтернативной клетке, старый маршрут
         * больше не соответствует его позиции.
         *
         * Генерируем interruption.
         *
         * Приказ при этом не отменяется.
         *
         * CPU должен построить:
         *
         * currentPosition -> originalOrderTarget
         */
        if (wasAlternativeMove)
        {
            MoveInterrupted?.Invoke(
                unitId);
        }
    }

    private void FinishMove(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        int unitId)
    {
        ref UnitMovement movement =
            ref units.Movement[unitId];

        ref UnitPosition position =
            ref units.Positions[unitId];

        /*
         * Удаляем из старой клетки.
         */
        spatialGrid.Remove(
            position.Spatial,
            unitId);

        /*
         * Переводим логическую позицию.
         */
        position.Spatial =
            movement.TargetCell;

        /*
         * Добавляем в новую клетку.
         */
        spatialGrid.Add(
            position.Spatial,
            unitId);
    }

    private void Interrupt(
        UnitStore units,
        int unitId)
    {
        ref UnitMovement movement =
            ref units.Movement[unitId];

        /*
         * Важно:
         *
         * здесь не меняется приказ.
         * Мы только инвалидируем текущий
         * физический переход.
         */
        movement.State =
            MovementState.Blocked;

        movement.Progress =
            0f;

        movement.StuckTimer =
            0f;

        movement.IsAlternativeMove =
            false;

        units.MovementCooldowns[unitId] =
            BlockedCooldown;

        MoveInterrupted?.Invoke(
            unitId);
    }

    private void Stop(
        UnitStore units,
        int unitId)
    {
        ref UnitMovement movement =
            ref units.Movement[unitId];

        ref UnitPosition position =
            ref units.Positions[unitId];

        movement.SourceCell =
            position.Spatial;

        movement.TargetCell =
            position.Spatial;

        movement.Progress =
            0f;

        movement.StuckTimer =
            0f;

        movement.HasLastAlternativeCell =
            false;

        movement.IsAlternativeMove =
            false;

        movement.State =
            MovementState.Idle;

        position.RenderX =
            position.Spatial.X;

        position.RenderY =
            position.Spatial.Y;
    }

    private void ReturnToLogical(
        UnitStore units,
        int unitId,
        float deltaTime)
    {
        ref UnitPosition position =
            ref units.Positions[unitId];

        float t =
            5f *
            deltaTime;

        if (t > 1f)
        {
            t = 1f;
        }

        position.RenderX +=
            (position.Spatial.X -
             position.RenderX) *
            t;

        position.RenderY +=
            (position.Spatial.Y -
             position.RenderY) *
            t;
    }

    private float CalculateSpeed(
        UnitStore units,
        int unitId)
    {
        float factor =
            1f -
            units.DynamicMass[unitId] /
            1000f;

        if (factor < 0.1f)
        {
            factor =
                0.1f;
        }

        return
            units.Movement[unitId].Speed *
            factor;
    }

    private bool CanUseUnit(
        UnitStore units,
        int unitId)
    {
        return
            units != null &&
            unitId >= 0 &&
            unitId < units.Count &&
            units.HealthMasks[unitId] != 0;
    }
}