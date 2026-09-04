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
        // ============================================================
        // GROUPS
        // ============================================================

        private const int BlueFirstGroupId = 0;
        private const int RedFirstGroupId = 8;

        private const int GroupsPerSide = 8;

        // 8 групп * 50 = 400 юнитов на сторону.
        private const int UnitsPerGroup = 50;

        private const int TotalUnitsPerSide =
            GroupsPerSide *
            UnitsPerGroup;

        // ============================================================
        // FORMATION / SPAWN
        // ============================================================

        private const int SpawnColumnsPerGroup = 10;

        // Размер пространства, занимаемого одной группой.
        private const int GroupBlockWidth = 14;
        private const int GroupBlockHeight = 8;

        public static void Spawn(
            UnitStore unitStore,
            UnitSpatialGrid spatialGrid)
        {
            var inventorySystem =
                new UnitInventorySystem();

            // ========================================================
            // WEAPONS
            // ========================================================

            RangedWeaponConfig ak47 =
                new RangedWeaponConfig
                {
                    Name = "АК-47",
                    Weight = 4.3f,
                    BaseDamage = 25,
                    BleedChance = 0.7f,
                    FireRate = 0.1f,
                    BaseEffectiveRange = 100f,
                    BaseAccuracy = 0.02f
                };

            RangedWeaponConfig svd =
                new RangedWeaponConfig
                {
                    Name = "СВД",
                    Weight = 4.5f,
                    BaseDamage = 40,
                    BleedChance = 0.9f,
                    FireRate = 0.8f,
                    BaseEffectiveRange = 300f,
                    BaseAccuracy = 0.005f
                };

            // ========================================================
            // ARMOR
            // ========================================================

            ArmorConfig heavyVest =
                new ArmorConfig
                {
                    Name = "Бронежилет БЖ-4",
                    ProtectedZone = 1,
                    DamageAbsorption = 0.5f,
                    BleedProtectionChance = 0.8f,
                    Weight = 8.5f
                };

            ArmorConfig helmet =
                new ArmorConfig
                {
                    Name = "Каска",
                    ProtectedZone = 0,
                    DamageAbsorption = 0.4f,
                    BleedProtectionChance = 0.7f,
                    Weight = 1.5f
                };

            Console.WriteLine(
                "[SPAWN] Stress test: " +
                TotalUnitsPerSide +
                " BLUE vs " +
                TotalUnitsPerSide +
                " RED"
            );

            Console.WriteLine(
                "[SPAWN] " +
                GroupsPerSide +
                " groups per side, " +
                UnitsPerGroup +
                " units per group"
            );

            // ========================================================
            // BLUE
            // ========================================================

            int blueOriginX = 100;
            int blueOriginY = 120;

            for (int groupIndex = 0;
                 groupIndex < GroupsPerSide;
                 groupIndex++)
            {
                int groupId =
                    BlueFirstGroupId +
                    groupIndex;

                // Расставляем группы сеткой 4 x 2.
                int blockX =
                    groupIndex % 4;

                int blockY =
                    groupIndex / 4;

                int groupStartX =
                    blueOriginX +
                    blockX *
                    GroupBlockWidth;

                int groupStartY =
                    blueOriginY +
                    blockY *
                    GroupBlockHeight;

                SpawnGroup(
                    unitStore,
                    spatialGrid,
                    inventorySystem,
                    groupId,
                    groupStartX,
                    groupStartY,
                    UnitType.Human,
                    ak47,
                    null,
                    null,
                    "BLUE"
                );
            }

            // ========================================================
            // RED
            // ========================================================

            int redOriginX = 300;
            int redOriginY = 330;

            for (int groupIndex = 0;
                 groupIndex < GroupsPerSide;
                 groupIndex++)
            {
                int groupId =
                    RedFirstGroupId +
                    groupIndex;

                int blockX =
                    groupIndex % 4;

                int blockY =
                    groupIndex / 4;

                int groupStartX =
                    redOriginX +
                    blockX *
                    GroupBlockWidth;

                int groupStartY =
                    redOriginY +
                    blockY *
                    GroupBlockHeight;

                SpawnGroup(
                    unitStore,
                    spatialGrid,
                    inventorySystem,
                    groupId,
                    groupStartX,
                    groupStartY,
                    UnitType.Insect,
                    svd,
                    heavyVest,
                    helmet,
                    "RED"
                );
            }

            Console.WriteLine(
                "[SPAWN] ========================================"
            );

            Console.WriteLine(
                $"[SPAWN] BLUE: {GroupsPerSide} groups × {UnitsPerGroup} = {TotalUnitsPerSide}"
            );

            Console.WriteLine(
                $"[SPAWN] RED : {GroupsPerSide} groups × {UnitsPerGroup} = {TotalUnitsPerSide}"
            );

            Console.WriteLine(
                $"[SPAWN] TOTAL: {TotalUnitsPerSide * 2}"
            );

            Console.WriteLine(
                "[SPAWN] ========================================"
            );
        }

        // ============================================================
        // SPAWN GROUP
        // ============================================================

        private static void SpawnGroup(
            UnitStore unitStore,
            UnitSpatialGrid spatialGrid,
            UnitInventorySystem inventorySystem,
            int groupId,
            int startX,
            int startY,
            UnitType unitType,
            RangedWeaponConfig weapon,
            ArmorConfig armor,
            ArmorConfig helmet,
            string sideName)
        {
            Console.WriteLine(
                $"[SPAWN] {sideName} GROUP {groupId}: " +
                $"start=({startX},{startY}) " +
                $"units={UnitsPerGroup}"
            );

            for (int i = 0;
                 i < UnitsPerGroup;
                 i++)
            {
                int spawnX =
                    startX +
                    (i % SpawnColumnsPerGroup);

                int spawnY =
                    startY +
                    (i / SpawnColumnsPerGroup);

                int unitId =
                    CreateUnitEntity(
                        unitStore,
                        spawnX,
                        spawnY,
                        unitType,
                        spatialGrid
                    );

                // ----------------------------------------------------
                // КЛЮЧЕВОЕ:
                // каждая 50-ка получает свой CurrentGroupId.
                // ----------------------------------------------------

                unitStore.CurrentGroupId[unitId] =
                    groupId;

                inventorySystem.EquipWeapon(
                    unitStore,
                    unitId,
                    weapon
                );

                if (armor != null)
                {
                    inventorySystem.EquipArmor(
                        unitStore,
                        unitId,
                        armor
                    );
                }

                if (helmet != null)
                {
                    inventorySystem.EquipArmor(
                        unitStore,
                        unitId,
                        helmet
                    );
                }

                unitStore.ShotCooldowns[unitId] =
                    0f;

                unitStore.Positions[unitId].RenderX =
                    unitStore.Positions[unitId].Spatial.X;

                unitStore.Positions[unitId].RenderY =
                    unitStore.Positions[unitId].Spatial.Y;
            }

            Console.WriteLine(
                $"[SPAWN] {sideName} GROUP {groupId} READY"
            );
        }

        // ============================================================
        // CREATE UNIT
        // ============================================================

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