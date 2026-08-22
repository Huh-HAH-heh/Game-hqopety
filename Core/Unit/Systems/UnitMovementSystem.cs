using Core.Map;
using Core.Unit.Components;
using Core.Unit.Systems; // Для доступа к HealthSystem.GetSpeedModifier
using System;

namespace Core.Unit
{
    public sealed class UnitMovementSystem
    {
        // Принимаем целевую SpatialCoord вместо старого TileCoord
        public bool TryStartMove(
     UnitStore units,
     int unitId,
     SpatialCoord targetCell, // Содержит микро-координаты X, Y, Z
     UnitSpatialGrid spatialGrid,
     WorldMap map)
        {
            // 1. БАЗОВЫЕ ПРОВЕРКИ ВАЛИДНОСТИ ЮНИТА
            if (unitId < 0 || unitId >= units.Count)
                return false;

            if (units.HealthMasks[unitId] == 0)
                return false;

            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            // Если муравей уже идет — повторно запустить движение нельзя
            if (movement.State == MovementState.Moving)
                return false;

            // 2. РАСЧЕТ ДЕЛЬТЫ (Берем чистые микро-координаты без SpatialMath)
            SpatialCoord currentCell = position.Spatial;

            int dx = Math.Abs(targetCell.X - currentCell.X);
            int dy = Math.Abs(targetCell.Y - currentCell.Y);
            int dz = Math.Abs(targetCell.Z - currentCell.Z);

            // Разрешено перемещение строго в соседнюю микро-ячейку (включая диагонали)
            if (dx > 1 || dy > 1 || dz > 1)
                return false;

            // Юнит пытается шагнуть сам под себя
            if (dx == 0 && dy == 0 && dz == 0)
                return false;

            // 3. ПРОВЕРКА ПРАВИЛ ФИЗИКИ И ВАЛИДАЦИИ ВЫСОТ
            // Теперь вызываем CanStep на каждый шаг, передавая чистые int координаты.
            // Метод проверит, существует ли ячейка, нет ли там стены и не слишком ли высоко.
            if (!MovementRules.CanStep(map, currentCell.X, currentCell.Y, targetCell.X, targetCell.Y, currentCell.Z))
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            // 4. ПРОВЕРКА ЗАНЯТОСТИ МИКРО-ЯЧЕЙКИ ДРУГИМ МУРАВЬЕМ
            // Пространственная сетка теперь настроена на микро-шаг, 
            // поэтому мы атомарно проверяем: свободна ли микро-точка 5х5 пикселей.
            if (spatialGrid.GetUnitsAt(targetCell).Count > 0)
            {
                movement.State = MovementState.Blocked;
                return false;
            }

            // 5. ИНИЦИАЛИЗАЦИЯ ШАГА
            // Запоминаем ячейки начала и конца движения
            movement.SourceCell = currentCell;
            movement.TargetCell = targetCell;
            movement.ZLevel = currentCell.Z; // Сохраняем текущий этаж для системы обновления

            movement.Progress = 0f;
            movement.State = MovementState.Moving;

            return true;
        }


        public void Update(
        UnitStore units,
        
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            for (int unitId = 0; unitId < units.Count; unitId++)
            {
                // 1. ПРОВЕРКА СМЕРТИ ЮНИТА
                if (units.HealthMasks[unitId] == 0)
                {
                    if (units.Movement[unitId].State == MovementState.Moving)
                    {
                        Stop(units, unitId);
                    }
                    continue;
                }

                ref var movement = ref units.Movement[unitId];
                ref var position = ref units.Positions[unitId];

                // 2. ЮНИТ СТОИТ
                if (movement.State != MovementState.Moving)
                {
                    // Теперь координаты рендера — это чистые координаты микро-ячейки в мире!
                    // Никаких делений на 8f. 1 микро-ячейка = 1 логическая единица.
                    position.RenderX = position.Spatial.X;
                    position.RenderY = position.Spatial.Y;
                    continue;
                }

                // 3. ЮНИТ ДВИГАЕТСЯ: Расчет скорости
                float speed = CalculateSpeed(units, unitId);

                // Получаем модификатор ландшафта (уже адаптирован под int координаты)
                float heightMultiplier = MovementRules.GetHeightSpeedMultiplier(
                    map,
                    movement.SourceCell.X,
                    movement.SourceCell.Y,
                    movement.TargetCell.X,
                    movement.TargetCell.Y,
                    movement.ZLevel
                );

                // Перемножаем базовую скорость муравья на рельеф. 
                // Костыльное "speed *= 8f" удалено, так как шаг сетки теперь монолитный.
                speed *= heightMultiplier;

                // Накапливаем прогресс шага (от 0.0f до 1.0f)
                movement.Progress += speed * deltaTime;

                // 4. ЮНИТ ЗАВЕРШИЛ ШАГ И ПЕРЕШЕЛ В НОВУЮ МИКРО-ЯЧЕЙКУ
                if (movement.Progress >= 1f)
                {
                    FinishMove(units, spatialGrid, unitId);
                }
                // 5. ИНТЕРПОЛЯЦИЯ ДВИЖЕНИЯ МЕЖДУ КАДРАМИ (Плавное скольжение)
                else
                {
                    // Из исходной микро-точки в целевую микро-точку
                    float srcX = movement.SourceCell.X;
                    float srcY = movement.SourceCell.Y;

                    float dstX = movement.TargetCell.X;
                    float dstY = movement.TargetCell.Y;

                    // Линейная интерполяция (LERP) для идеальной плавности рендеринга муравья в SFML
                    position.RenderX = srcX + (dstX - srcX) * movement.Progress;
                    position.RenderY = srcY + (dstY - srcY) * movement.Progress;
                }
            }
        }


        private void FinishMove(UnitStore units, UnitSpatialGrid spatialGrid, int unitId)
        {
            // Считываем ссылки на компоненты пешки строго через ref
            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            // ========================================================
            // ИСПРАВЛЕНО: Запись события шага идет СТРОГО в плоские корневые 
            // массивы UnitStore по индексу unitId! Никаких вложенных структур и squads!
            // ========================================================
            units.HasJustFinishedMoveStep[unitId] = true;
            units.LastVisitedSourceCell[unitId] = movement.SourceCell;

            // 1. Выписываем муравья из старой микро-позиции пространственной сетки
            spatialGrid.Remove(position.Spatial, unitId);

            // 2. Официально перезаписываем позицию компонента на новую микро-ячейку
            position.Spatial = movement.TargetCell;

            // 3. Регистрируем муравья в новой микро-ячейке пространственной сетки
            spatialGrid.Add(position.Spatial, unitId);

            // Фиксируем точный рендер в центре новой микро-ячейки без микро-дёрганий
            position.RenderX = position.Spatial.X;
            position.RenderY = position.Spatial.Y;

            // Сбрасываем параметры движения для следующего шага
            movement.SourceCell = movement.TargetCell;
            movement.Progress = 0f;
            movement.State = MovementState.Idle; // Муравей пришел и готов получать новые приказы
        }


        private float CalculateSpeed(UnitStore units, int unitId)
        {
            float mass = units.DynamicMass[unitId];
            float massFactor = 1f - mass / 1000f;
            if (massFactor < 0.1f) massFactor = 0.1f;

            // ИСПРАВЛЕНИЕ: Передаем в метод ДВА параметра: маску здоровья И уровень потери крови
            float healthFactor = HealthSystem.GetSpeedModifier(units.HealthMasks[unitId], units.BloodLossLevels[unitId]);

            return units.Movement[unitId].Speed * massFactor * healthFactor;
        }


        public void Stop(UnitStore units, int unitId)
        {
            if (unitId < 0 || unitId >= units.Count)
                return;

            ref var movement = ref units.Movement[unitId];
            ref var position = ref units.Positions[unitId];

            // Принудительно останавливаем муравья в его ТЕКУЩЕЙ микро-позиции
            movement.SourceCell = position.Spatial;
            movement.TargetCell = movement.SourceCell;

            movement.Progress = 0f;
            movement.State = MovementState.Idle;
        }

    }
}
