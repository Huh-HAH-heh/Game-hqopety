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
        private const int BlueGroupId = 0;
        private const int RedGroupId = 1;

        public static void Spawn(
            UnitStore unitStore,
            UnitSpatialGrid spatialGrid)
        {
            var inventorySystem = new UnitInventorySystem();

            RangedWeaponConfig ak47 = new RangedWeaponConfig
            {
                Name = "АК-47",
                Weight = 4.3f,
                BaseDamage = 25,
                BleedChance = 0.7f,
                FireRate = 0.1f,
                BaseEffectiveRange = 100f,
                BaseAccuracy = 0.02f
            };

            RangedWeaponConfig svd = new RangedWeaponConfig
            {
                Name = "СВД",
                Weight = 4.5f,
                BaseDamage = 40,
                BleedChance = 0.9f,
                FireRate = 0.8f,
                BaseEffectiveRange = 300f,
                BaseAccuracy = 0.005f
            };

            ArmorConfig heavyVest = new ArmorConfig
            {
                Name = "Бронежилет БЖ-4",
                ProtectedZone = 1,
                DamageAbsorption = 0.5f,
                BleedProtectionChance = 0.8f,
                Weight = 8.5f
            };

            ArmorConfig helmet = new ArmorConfig
            {
                Name = "Каска",
                ProtectedZone = 0,
                DamageAbsorption = 0.4f,
                BleedProtectionChance = 0.7f,
                Weight = 1.5f
            };

            Console.WriteLine(
                "[SPAWN] Создание группового ИИ: 40 BLUE vs 40 RED"
            );

            int blueStartCellX = 165;
            int blueStartCellY = 165;

            for (int i = 0; i < 40; i++)
            {
                int spawnX =
                    blueStartCellX +
                    (i % 20);

                int spawnY =
                    blueStartCellY +
                    (i / 20);

                int unitId =
                    CreateUnitEntity(
                        unitStore,
                        spawnX,
                        spawnY,
                        UnitType.Human,
                        spatialGrid
                    );

                unitStore.CurrentGroupId[unitId] =
                    BlueGroupId;

                inventorySystem.EquipWeapon(
                    unitStore,
                    unitId,
                    ak47
                );

                unitStore.ShotCooldowns[unitId] = 0f;

                unitStore.Positions[unitId].RenderX =
                    unitStore.Positions[unitId].Spatial.X;

                unitStore.Positions[unitId].RenderY =
                    unitStore.Positions[unitId].Spatial.Y;

                Console.WriteLine(
                    $"[SPAWN] BLUE unit={unitId} group={BlueGroupId} pos=({spawnX},{spawnY},1)"
                );
            }

            int redStartCellX = 337;
            int redStartCellY = 373;

            for (int i = 0; i < 40; i++)
            {
                int spawnX =
                    redStartCellX +
                    (i % 20);

                int spawnY =
                    redStartCellY +
                    (i / 20);

                int unitId =
                    CreateUnitEntity(
                        unitStore,
                        spawnX,
                        spawnY,
                        UnitType.Insect,
                        spatialGrid
                    );

                unitStore.CurrentGroupId[unitId] =
                    RedGroupId;

                inventorySystem.EquipWeapon(
                    unitStore,
                    unitId,
                    svd
                );

                inventorySystem.EquipArmor(
                    unitStore,
                    unitId,
                    heavyVest
                );

                inventorySystem.EquipArmor(
                    unitStore,
                    unitId,
                    helmet
                );

                unitStore.ShotCooldowns[unitId] = 0f;

                unitStore.Positions[unitId].RenderX =
                    unitStore.Positions[unitId].Spatial.X;

                unitStore.Positions[unitId].RenderY =
                    unitStore.Positions[unitId].Spatial.Y;

                Console.WriteLine(
                    $"[SPAWN] RED unit={unitId} group={RedGroupId} pos=({spawnX},{spawnY},1)"
                );
            }

            Console.WriteLine(
                $"[SPAWN] GROUP {BlueGroupId}: BLUE 40 units"
            );

            Console.WriteLine(
                $"[SPAWN] GROUP {RedGroupId}: RED 40 units"
            );

            Console.WriteLine(
                "[SPAWN] Группы ИИ созданы. Можно выдавать групповые приказы."
            );
        }

        private static int CreateUnitEntity(
            UnitStore unitStore,
            int mx,
            int my,
            UnitType type,
            UnitSpatialGrid spatialGrid)
        {
            return unitStore.CreateUnit(
                mx: mx,
                my: my,
                mz: 1,
                width: 1,
                height: 1,
                mass: 75f,
                speed: 4.0f,
                type: type,
                spatialGrid: spatialGrid
            );
        }
    }
}