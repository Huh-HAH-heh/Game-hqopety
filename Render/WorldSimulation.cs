using Core.AI;
using Core.Items; // Подключаем пространство имен для доступа к EdificeStore
using Core.Map;
using Core.Unit;
using Core.Unit.Systems;

namespace RimClone.Render
{
    public sealed class WorldSimulation
    {// 1. Добавляем приватное поле (задаем стартовые лимиты групп и юнитов, например 16 и 512)
        private readonly GroupMovementManager _groupMovementManager = new GroupMovementManager(16, 512);

        // 2. Открываем публичный доступ для мышки из VectorRenderer
        public GroupMovementManager GroupMovementManager => _groupMovementManager;








        private readonly UnitPushSystem _unitPushSystem = new UnitPushSystem();
        private readonly UnitCombatSystem _unitCombatSystem = new UnitCombatSystem();
        private readonly CombatEffectSystem _effectSystem = new CombatEffectSystem();
        private UnitMovementSystem _unitMovementSystem;
  

        public CombatEffectSystem EffectSystem => _effectSystem;

        public void Initialize(UnitMovementSystem movementSystem)
        {
            _unitMovementSystem = movementSystem;
        }

        /// <summary>
        /// Главный метод обновления симуляции. 
        /// ОБНОВЛЕНО: Добавлен обязательный параметр EdificeStore для учета укрытий.
        /// </summary>
        public void Update(
            UnitStore units,
            
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            EdificeStore edificeStore, // <-- Добавили сюда
            float microCellPixelSize,
            float deltaTime
            )
            
        {
            // 1. Отряды рассчитывают микро-шаги строя


            // 2. Попиксельное скольжение муравьев (LERP)
            _unitMovementSystem.Update(
    units,
    spatialGrid,
    map,
    edificeStore,         // База построек для DDA (была пропущена)
    deltaTime,
    _groupMovementManager

    );            // Время кадра
                  // Менеджер групп (аргумент метода симуляции)



            // 3. Мягкое расталкивание в стиле RimWorld
            _unitPushSystem.Update(units, spatialGrid, deltaTime, map);


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
            units.UpdateHealthSystems(deltaTime);

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
