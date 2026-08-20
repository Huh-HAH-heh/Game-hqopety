using Core.Map;
using Core.Unit;
using Core.Unit.Systems;
using Core.Items; // Подключаем пространство имен для доступа к EdificeStore

namespace RimClone.Render
{
    public sealed class WorldSimulation
    {
        private readonly UnitPushSystem _unitPushSystem = new UnitPushSystem();
        private readonly UnitCombatSystem _unitCombatSystem = new UnitCombatSystem();
        private readonly CombatEffectSystem _effectSystem = new CombatEffectSystem();
        private SquadMovementSystem _squadMovementSystem;
        private UnitMovementSystem _unitMovementSystem;

        public CombatEffectSystem EffectSystem => _effectSystem;

        public void Initialize(UnitMovementSystem movementSystem, SquadMovementSystem squadSystem)
        {
            _unitMovementSystem = movementSystem;
            _squadMovementSystem = squadSystem;
        }

        /// <summary>
        /// Главный метод обновления симуляции. 
        /// ОБНОВЛЕНО: Добавлен обязательный параметр EdificeStore для учета укрытий.
        /// </summary>
        public void Update(
            UnitStore units,
            SquadStore squads,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            EdificeStore edificeStore, // <-- Добавили сюда
            float microCellPixelSize,
            float deltaTime,
            FireteamRegistry fireteamRegistry)
        {
            // 1. Отряды рассчитывают микро-шаги строя
            _squadMovementSystem.Update(units, squads, spatialGrid, map, deltaTime);


            // 2. Попиксельное скольжение муравьев (LERP)
            _unitMovementSystem.Update(units, squads, spatialGrid, map, deltaTime);

            // 3. Мягкое расталкивание в стиле RimWorld
            _unitPushSystem.Update(units, spatialGrid, deltaTime);

            // 4. ИСПРАВЛЕНО: Боевой ИИ теперь получает ВСЕ необходимые параметры для расчета баллистики промахов
            _unitCombatSystem.Update(
                units,
                spatialGrid,
                map,          // Передаем карту мира
                edificeStore, // Передаем базу построек/укрытий
                _effectSystem,
                microCellPixelSize,
                deltaTime
            );

            // 5. Таймеры жизни эффектов стрельбы и крестиков
            _effectSystem.Update(deltaTime);

            // 6. Медицина: Кровотечение, заживление ран, смерть от кровопотери
            units.UpdateHealthSystems(deltaTime, squads);

            // 7. Очистка погибших из пространственной сетки
            for (int i = 0; i < units.Count; i++)
            {
                if (units.HealthMasks[i] == 0)
                {
                    spatialGrid.Remove(units.Positions[i].Spatial, i);
                }
            }
        }
    }
}
