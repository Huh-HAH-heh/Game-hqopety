using System;
using System.Numerics;
using Core.Combat;
using Core.Items;
using Core.Map;

namespace Core.Unit;

public static class CombatSelfTest
{
    public static bool Run()
    {
        int passed = 0;
        int failed = 0;

        Console.WriteLine();
        Console.WriteLine("========== COMBAT SELF TEST ==========");

        RunTest("Ballistics: trajectory solution", TestBallistics, ref passed, ref failed);
        RunTest("Ballistics: ballistic coefficient", TestBallisticCoefficient, ref passed, ref failed);
        RunTest("Armor: hard armor stops underpenetrating hit", TestArmorDeflection, ref passed, ref failed);
        RunTest("Armor: penetrating hit passes armor", TestArmorPenetration, ref passed, ref failed);
        RunTest("Weapon: fire mode cycle", TestFireModes, ref passed, ref failed);
        RunTest("Weapon: aim mode cycle", TestAimModes, ref passed, ref failed);
        RunTest("Weapon: target mode cycle", TestTargetModes, ref passed, ref failed);
        RunTest("Weapon: ammunition selection", TestAmmunitionSelection, ref passed, ref failed);
        RunTest("Suppression: threshold state", TestSuppression, ref passed, ref failed);
        RunTest("Accuracy: recoil and movement increase spread", TestAccuracy, ref passed, ref failed);
        RunTest("Navigation: A* routes around an impassable ridge", TestNavigationRoutesAroundWall, ref passed, ref failed);
        RunTest("Vision: spatial index prunes distant unit pairs", TestVisionSpatialIndex, ref passed, ref failed);
        RunTest("Weapon: firing consumes one round and emits telemetry", TestWeaponAmmoConsumption, ref passed, ref failed);
        RunTest("Lifecycle: dead units stop moving and firing", TestDeadUnitCleanup, ref passed, ref failed);

        Console.WriteLine("---------------------------------------");
        Console.WriteLine($"RESULT: PASS={passed} FAIL={failed}");
        Console.WriteLine("=======================================");
        Console.WriteLine();

        return failed == 0;
    }

    private static void RunTest(
        string name,
        Func<bool> test,
        ref int passed,
        ref int failed)
    {
        try
        {
            bool result = test();

            if (result)
            {
                passed++;
                Console.WriteLine($"[PASS] {name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"[FAIL] {name}");
            }
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TestBallistics()
    {
        Vector3 start = new Vector3(0f, 0f, 10f);
        Vector3 target = new Vector3(100f, 0f, 10f);

        AmmunitionConfig ammo = WeaponCatalog.Rifle556;

        bool solved =
            ProjectileBallistics.TrySolve(
                start,
                target,
                ammo.ProjectileMassKg,
                ammo.ProjectileDiameterM,
                ammo.MuzzleVelocity,
                ammo.DragCoefficient,
                ammo.BallisticCoefficient,
                out BallisticSolution solution);

        if (!solved)
            return false;

        if (solution.TimeOfFlight <= 0f ||
            solution.ImpactVelocity <= 0f ||
            solution.ImpactVelocity >= ammo.MuzzleVelocity)
        {
            return false;
        }

        float directionLength =
            solution.Direction.Length();

        return MathF.Abs(directionLength - 1f) < 0.001f &&
               solution.ImpactEnergy > 0f;
    }

    private static bool TestBallisticCoefficient()
    {
        AmmunitionConfig ammo = WeaponCatalog.Rifle556;

        Vector3 velocityFast = new Vector3(ammo.MuzzleVelocity, 0f, 0f);
        Vector3 velocitySlow = velocityFast;

        ProjectileBallistics.Integrate(
            ref velocityFast,
            ammo.ProjectileMassKg,
            ammo.ProjectileDiameterM,
            ammo.DragCoefficient,
            2f,
            1f);

        ProjectileBallistics.Integrate(
            ref velocitySlow,
            ammo.ProjectileMassKg,
            ammo.ProjectileDiameterM,
            ammo.DragCoefficient,
            0.5f,
            1f);

        return velocityFast.Length() > velocitySlow.Length();
    }

    private static bool TestArmorDeflection()
    {
        ArmorConfig armor =
            new ArmorConfig
            {
                Name = "Test Hard Armor",
                ArmorRating = 100f,
                SharpRating = 100f,
                PenetrationResistance = 100f,
                SoftArmor = false,
                EnergyLoss = 50f
            };

        ArmorResolution result =
            ArmorResolver.ResolveLayer(
                DamageType.Ballistic,
                100f,
                500f,
                50f,
                30f,
                armor);

        return result.Deflected &&
               result.Stopped &&
               result.Damage <= 0.001f &&
               result.BluntImpactDamage > 0f;
    }

    private static bool TestArmorPenetration()
    {
        ArmorConfig armor =
            new ArmorConfig
            {
                Name = "Test Armor",
                ArmorRating = 50f,
                SharpRating = 50f,
                PenetrationResistance = 50f,
                SoftArmor = false
            };

        ArmorResolution result =
            ArmorResolver.ResolveLayer(
                DamageType.Ballistic,
                100f,
                800f,
                120f,
                60f,
                armor);

        return !result.Deflected &&
               result.Penetration > 0f &&
               result.Damage > 0f;
    }

    private static bool TestFireModes()
    {
        UnitWeaponStore weapons = new UnitWeaponStore(1);
        weapons.InitializeUnit(0);
        weapons.ConfigureSlot(
            0,
            UnitWeaponSlot.Primary,
            WeaponCatalog.AssaultRifle);

        int index =
            UnitWeaponStore.GetIndex(
                0,
                UnitWeaponSlot.Primary);

        if (weapons.CurrentFireMode[index] !=
            FireMode.Auto)
        {
            return false;
        }

        weapons.CycleFireMode(
            0,
            UnitWeaponSlot.Primary);

        if (weapons.CurrentFireMode[index] !=
            FireMode.Single)
        {
            return false;
        }

        weapons.CycleFireMode(
            0,
            UnitWeaponSlot.Primary);

        return weapons.CurrentFireMode[index] ==
               FireMode.Burst;
    }

    private static bool TestAimModes()
    {
        UnitWeaponStore weapons = new UnitWeaponStore(1);
        weapons.InitializeUnit(0);
        weapons.ConfigureSlot(
            0,
            UnitWeaponSlot.Primary,
            WeaponCatalog.AssaultRifle);

        int index =
            UnitWeaponStore.GetIndex(
                0,
                UnitWeaponSlot.Primary);

        weapons.CycleAimMode(
            0,
            UnitWeaponSlot.Primary);

        if (weapons.CurrentAimMode[index] !=
            AimMode.Snapshot)
        {
            return false;
        }

        weapons.CycleAimMode(
            0,
            UnitWeaponSlot.Primary);

        return weapons.CurrentAimMode[index] ==
               AimMode.SuppressFire;
    }

    private static bool TestTargetModes()
    {
        UnitWeaponStore weapons = new UnitWeaponStore(1);
        weapons.InitializeUnit(0);
        weapons.ConfigureSlot(
            0,
            UnitWeaponSlot.Primary,
            WeaponCatalog.AssaultRifle);

        int index =
            UnitWeaponStore.GetIndex(
                0,
                UnitWeaponSlot.Primary);

        weapons.CycleTargetMode(
            0,
            UnitWeaponSlot.Primary);

        if (weapons.CurrentTargetMode[index] !=
            TargetMode.Torso)
        {
            return false;
        }

        weapons.CycleTargetMode(
            0,
            UnitWeaponSlot.Primary);

        if (weapons.CurrentTargetMode[index] !=
            TargetMode.Head)
        {
            return false;
        }

        weapons.CycleTargetMode(
            0,
            UnitWeaponSlot.Primary);

        weapons.CycleTargetMode(
            0,
            UnitWeaponSlot.Primary);

        return weapons.CurrentTargetMode[index] ==
               TargetMode.Automatic;
    }

    private static bool TestAmmunitionSelection()
    {
        UnitWeaponStore weapons = new UnitWeaponStore(1);
        weapons.InitializeUnit(0);
        weapons.ConfigureSlot(
            0,
            UnitWeaponSlot.Primary,
            WeaponCatalog.AssaultRifle);

        int index =
            UnitWeaponStore.GetIndex(
                0,
                UnitWeaponSlot.Primary);

        if (weapons.CurrentAmmoType[index] != 0)
            return false;

        // Selection is intentionally blocked while the magazine still has rounds.
        weapons.Ammo[index] = 0;

        bool selected =
            weapons.SelectAmmunition(
                0,
                UnitWeaponSlot.Primary,
                1);

        return selected &&
               weapons.CurrentAmmoType[index] == 1 &&
               weapons.ReserveAmmo[index] > 0;
    }

    private static bool TestSuppression()
    {
        UnitSuppressionStore suppression =
            new UnitSuppressionStore(1);

        UnitStore units =
            new UnitStore(1);

        suppression.InitializeUnit(0);

        suppression.Add(
            0,
            1.2f,
            Vector3.Zero);

        if (suppression.GetState(0) !=
            UnitSuppressionState.Suppressed)
        {
            return false;
        }

        suppression.Add(
            0,
            1.5f,
            Vector3.Zero);

        return suppression.GetState(0) ==
               UnitSuppressionState.Panicked &&
               suppression.GetAccuracyMultiplier(0) > 1f;
    }

    private static bool TestAccuracy()
    {
        float calm =
            ShotAccuracy.CalculateSpread(
                0.01f,
                1f,
                1f,
                0f,
                0f,
                0f,
                1f,
                50f,
                100f,
                1f,
                AimMode.AimedShot,
                0f,
                0f,
                0f,
                0.01f);

        float stressed =
            ShotAccuracy.CalculateSpread(
                0.01f,
                1f,
                1f,
                3f,
                0.2f,
                1f,
                1.8f,
                80f,
                100f,
                0.25f,
                AimMode.Snapshot,
                0.001f,
                2f,
                0.3f,
                0.01f);

        return stressed > calm &&
               calm > 0f;
    }
    private static bool TestNavigationRoutesAroundWall()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 50);

        int width = worldMap.TileWidth;
        int height = worldMap.TileHeight;
        int wallX = width / 2;
        int centerY = height / 2;
        const ushort floorHeight = 10;
        const ushort wallHeight = 40;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                worldMap.SetSolidHeight(x, y, floorHeight, 1);
        }

        for (int y = 3; y < height - 3; y++)
            worldMap.SetSolidHeight(wallX, y, wallHeight, 2);

        UnitSimulation simulation = new UnitSimulation(4, 64);
        Vector3 start = new Vector3(5.5f, centerY + 0.5f, 1f);
        Vector3 target = new Vector3(width - 5.5f, centerY + 0.5f, 1f);
        UnitId unit = simulation.Spawn(
            UnitType.Colonist,
            start,
            Vector3.UnitX,
            Vector3.UnitX);

        UnitNavigationSystem navigation = simulation.Navigation;
        Vector3 position = start;
        float startY = start.Y;
        bool detoured = false;

        navigation.BeginUpdate(simulation.Units, worldMap);

        for (int step = 0; step < width * height; step++)
        {
            if (!navigation.TryGetWaypoint(
                    simulation.Units,
                    unit.Index,
                    worldMap,
                    position,
                    target,
                    out Vector2 waypoint))
            {
                return false;
            }

            if (MathF.Abs(waypoint.Y - startY) > 0.01f)
                detoured = true;

            if (waypoint == new Vector2(target.X, target.Y))
            {
                if (!detoured || navigation.RoutesBuilt != 1)
                    return false;

                break;
            }

            int cellX = Math.Clamp((int)MathF.Floor(waypoint.X), 0, width - 1);
            int cellY = Math.Clamp((int)MathF.Floor(waypoint.Y), 0, height - 1);

            if (cellX == wallX && cellY >= 3 && cellY < height - 3)
                return false;

            position = new Vector3(
                waypoint.X,
                waypoint.Y,
                worldMap.GetSurfaceHeight(cellX, cellY));
        }

        // Seal the previously open gaps; A* must report that the goal is unreachable.
        for (int y = 0; y < height; y++)
            worldMap.SetSolidHeight(wallX, y, wallHeight, 2);

        UnitNavigationSystem blockedNavigation = new UnitNavigationSystem();
        blockedNavigation.BeginUpdate(simulation.Units, worldMap);

        bool foundBlockedRoute = blockedNavigation.TryGetWaypoint(
            simulation.Units,
            unit.Index,
            worldMap,
            start,
            target,
            out _);

        return !foundBlockedRoute && blockedNavigation.RoutesFailed == 1;
    }

    private static bool TestVisionSpatialIndex()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 10,
            regionsY: 10,
            layerCount: 10);
        UnitSimulation simulation = new UnitSimulation(256, 2048);

        const int columns = 16;
        const int rows = 10;
        const int unitCount = columns * rows;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                Vector3 position = new Vector3(
                    20f + column * 25f,
                    20f + row * 25f,
                    0f);

                UnitId unit = simulation.Spawn(
                    UnitType.Colonist,
                    position,
                    Vector3.UnitX,
                    Vector3.UnitX);

                simulation.Units.ViewRange[unit.Index] = 10f;
                simulation.Units.FieldOfView[unit.Index] = 360f;
            }
        }

        simulation.Vision.Update(
            simulation.Units,
            worldMap,
            0.1f,
            simulation.Health);

        int bruteForcePairs = unitCount * (unitCount - 1);

        return simulation.Vision.LastCandidatePairs < bruteForcePairs / 4 &&
               simulation.Vision.LastVisibleTargetCount == 0 &&
               simulation.Vision.UpdateCount == 1;
    }

    private static bool TestWeaponAmmoConsumption()
    {
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 5.5f, 0f),
            Vector3.UnitX,
            Vector3.UnitX);

        int inventorySlot = simulation.AddInventoryItem(
            shooter,
            WeaponCatalog.AssaultRifle);

        if (inventorySlot < 0 ||
            !simulation.EquipWeapon(
                shooter,
                inventorySlot,
                UnitWeaponSlot.Primary))
        {
            return false;
        }

        int weaponIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);
        simulation.Weapons.Ammo[weaponIndex] = 1;
        simulation.BeginMetricsFrame();

        bool fired = simulation.FireWeapon(
            shooter,
            UnitWeaponSlot.Primary,
            Vector3.UnitX);

        return fired &&
               simulation.Weapons.Ammo[weaponIndex] == 0 &&
               simulation.Projectiles.ActiveCount == 1 &&
               simulation.TotalShotsFired == 1 &&
               simulation.TotalRoundsConsumed == 1 &&
               simulation.Projectiles.ActiveCount > 0 &&
               simulation.TotalProjectilesSpawned == simulation.Projectiles.ActiveCount &&
               simulation.ShotsFiredThisFrame == 1;
    }

    private static bool TestDeadUnitCleanup()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 20);
        UnitSimulation simulation = new UnitSimulation(4, 64);

        UnitId dead = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 5.5f, 0f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitId survivor = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(8.5f, 5.5f, 0f),
            -Vector3.UnitX,
            -Vector3.UnitX);

        simulation.SetTarget(dead, new Vector3(20.5f, 5.5f, 0f));
        simulation.Weapons.StartBurst(
            dead.Index,
            UnitWeaponSlot.Primary,
            survivor,
            3,
            0.1f);
        simulation.AI.Store.RememberTarget(
            survivor.Index,
            dead,
            simulation.Units.Position[dead.Index],
            3f);
        simulation.Weapons.StartBurst(
            survivor.Index,
            UnitWeaponSlot.Primary,
            dead,
            2,
            0.1f);
        simulation.Health.OverallHitPoints[dead.Index] = 0f;

        simulation.Update(worldMap, 1f / 60f);

        if (simulation.Units.HasTarget[dead.Index] ||
            simulation.Units.Velocity[dead.Index] != Vector3.Zero ||
            simulation.AI.Store.State[dead.Index] != UnitAiState.Dead ||
            simulation.Weapons.BurstRemaining[
                UnitWeaponStore.GetIndex(dead.Index, UnitWeaponSlot.Primary)] != 0 ||
            simulation.AI.Store.HasTarget[survivor.Index] ||
            simulation.Weapons.BurstRemaining[
                UnitWeaponStore.GetIndex(survivor.Index, UnitWeaponSlot.Primary)] != 0 ||
            simulation.Weapons.BurstTarget[
                UnitWeaponStore.GetIndex(survivor.Index, UnitWeaponSlot.Primary)] == dead)
        {
            return false;
        }

        if (simulation.FireWeapon(
                dead,
                UnitWeaponSlot.Primary,
                Vector3.UnitX) ||
            simulation.FireWeaponAt(
                survivor,
                UnitWeaponSlot.Primary,
                dead))
        {
            return false;
        }

        if (!simulation.Destroy(dead))
            return false;

        UnitId replacement = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 5.5f, 0f),
            Vector3.UnitX,
            Vector3.UnitX);

        return replacement.Index == dead.Index &&
               replacement.Generation != dead.Generation &&
               !simulation.Units.IsAlive(dead);
    }

}
