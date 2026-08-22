using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using Core.Unit.Systems;
using System;

namespace Units
{
    public static class TestUnitSpawner
    {
        private const int Scale = MapRegion.SubDivision; // 3

        public static void Spawn(UnitStore unitStore,  UnitSpatialGrid spatialGrid)
        {
            var inventorySystem = new UnitInventorySystem();

            // --- 1. СОЗДАЕМ ОРУЖИЕ И БРОНЮ ---
            // Создаем АК-47 (Идеал одиночными на 100 тайлов/метров)
            // --- 1. СОЗДАЕМ ОРУЖИЕ ---
            // Создаем АК-47 на базе вашего класса RangedWeaponConfig
            RangedWeaponConfig ak47 = new RangedWeaponConfig
            {
                Name = "АК-47",
                Weight = 4.3f,               // Масса автомата
                BaseDamage = 25,             // Базовый урон (у вас тип byte)
                BleedChance = 0.7f,          // Шанс вызвать кровотечение
                FireRate = 0.1f,             // Скорострельность
                BaseEffectiveRange = 100f,   // Идеальная дальность одиночными (вместо BaseRange)
                BaseAccuracy = 0.02f         // Очень точный одиночными (базовый разброс)
            };

            // Создаем СВД на базе вашего класса RangedWeaponConfig
            RangedWeaponConfig svd = new RangedWeaponConfig
            {
                Name = "СВД",
                Weight = 4.5f,
                BaseDamage = 40,             // У СВД урон повыше
                BleedChance = 0.9f,          // Выше шанс кровотечения
                FireRate = 0.8f,             // Стреляет медленнее, чем АК
                BaseEffectiveRange = 300f,   // Идеальная дальность одиночными без прицела
                BaseAccuracy = 0.005f        // Почти идеальный лазер
            };

            // ========================================================
            // ТАКТИЧЕСКИЙ СТРЕСС-ТЕСТ: СТЕНКА НА СТЕНКУ (80 СУЩЕСТВ)
            // ========================================================
            Console.WriteLine("🚀 Инициализация масштабного стресс-теста: 40 vs 40...");

            var heavyVest = new ArmorConfig { Name = "Бронежилет БЖ-4", ProtectedZone = 1, DamageAbsorption = 0.5f, BleedProtectionChance = 0.8f, Weight = 8.5f };
            var helmet = new ArmorConfig { Name = "Каска", ProtectedZone = 0, DamageAbsorption = 0.4f, BleedProtectionChance = 0.7f, Weight = 1.5f };

            // 1. СОЗДАЕМ ОТРЯД №0: СИНИЕ КОЛОНИСТЫ (СВЕРХУ)
         

            // Начальная точка шеренги синих на Севере
            int blueStartCellX = 50;
            int blueStartCellY = 100; // Северная граница

            for (int i = 0; i < 40; i++)
            {
                // Выстраиваем синих в ровную тактическую шеренгу по горизонтали
                int spawnX = blueStartCellX + (i % 20); // 2 ряда по 20 человек
                int spawnY = blueStartCellY + (i / 20);

                int unitId = CreateUnitEntity(unitStore, spawnX, spawnY, UnitType.Human, spatialGrid);
                

                // Выдаем автоматы и пришвартовываем плавный рендер к клетке
                inventorySystem.EquipWeapon(unitStore, unitId, ak47);
                unitStore.ShotCooldowns[unitId] = 0f;
                unitStore.Positions[unitId].RenderX = unitStore.Positions[unitId].Spatial.X;
                unitStore.Positions[unitId].RenderY = unitStore.Positions[unitId].Spatial.Y;
            }

            // Направляем Синих на 500 тайлов вниз (на Юг) и на 20 тайлов ПРАВЕЕ!
            SpatialCoord blueTarget = new SpatialCoord(blueStartCellX + 20, blueStartCellY + 500, 1);
           

            // Начальная точка шеренги жуков на Юге (ровно на 500 клеток ниже синих!)
            int redStartCellX = 50;
            int redStartCellY = blueStartCellY + 30; // 100 + 500 = 600 (Южная граница)

            for (int i = 0; i < 40; i++)
            {
                // Выстраиваем жуков такой же зеркальной шеренгой
                int spawnX = redStartCellX + (i % 20);
                int spawnY = redStartCellY + (i / 20);

                int unitId = CreateUnitEntity(unitStore, spawnX, spawnY, UnitType.Insect, spatialGrid);
                

                // Заряжаем им снайперские СВД, одеваем в броню и швартуем рендер
                inventorySystem.EquipWeapon(unitStore, unitId, svd);
                inventorySystem.EquipArmor(unitStore, unitId, heavyVest);
                inventorySystem.EquipArmor(unitStore, unitId, helmet);
                unitStore.ShotCooldowns[unitId] = 0f;
                unitStore.Positions[unitId].RenderX = unitStore.Positions[unitId].Spatial.X;
                unitStore.Positions[unitId].RenderY = unitStore.Positions[unitId].Spatial.Y;
            }

            // Направляем Красных на 500 тайлов вверх (на Север) и тоже на 20 тайлов ПРАВЕЕ!
            // Они пойдут на перехват синей колонны!
            SpatialCoord redTarget = new SpatialCoord(redStartCellX + 20, redStartCellY - 500, 1);
        
            Console.WriteLine("⚔️ БАТАЛИЯ ЗАПУЩЕНА! Шеренги рождены в легальных границах карты и маршируют навстречу друг другу!");

        }

        private static int CreateUnitEntity(UnitStore unitStore, int mx, int my, UnitType type, UnitSpatialGrid spatialGrid)
        {
            // ИСПРАВЛЕНО: Принудительно передаем mz = 1 вместо 0
            return unitStore.CreateUnit(
                mx: mx, my: my, mz: 1,
                width: 1,
                height: 1,
                mass: 75f, speed: 4.0f,
                type: type,
                spatialGrid: spatialGrid
            );
        }

        
    }
}
