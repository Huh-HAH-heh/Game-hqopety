using System;
using System.IO;
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
        RunTest("Weapon: aim warmup delays first shot", TestWeaponAimWarmup, ref passed, ref failed);
        RunTest("AI: aim survives vision sample gaps but clears on obstruction", TestAimSurvivesVisionSampleGaps, ref passed, ref failed);
        RunTest("Weapon: aim modes have distinct CE warmup times", TestAimModeDurations, ref passed, ref failed);
        RunTest("Weapon: automatic fire follows weapon cadence", TestAutomaticFireCadence, ref passed, ref failed);
        RunTest("Weapon: single fire requires a new aim", TestSingleFireReAims, ref passed, ref failed);
        RunTest("Weapon: burst mode completes the configured burst", TestBurstFireCadence, ref passed, ref failed);
        RunTest("Weapon: suppressive auto fire continues without LOS", TestSuppressiveFireThroughOcclusion, ref passed, ref failed);
        RunTest("Suppression: threshold state", TestSuppression, ref passed, ref failed);
        RunTest("Accuracy: recoil and movement increase spread", TestAccuracy, ref passed, ref failed);
        RunTest("Navigation: A* routes around an impassable ridge", TestNavigationRoutesAroundWall, ref passed, ref failed);
        RunTest("Navigation: cached routes avoid previous corridors", TestNavigationRouteReuse, ref passed, ref failed);
        RunTest("AI: scouts toward the center when no target is visible", TestAiSearchAdvance, ref passed, ref failed);
        RunTest("Vision: candidate sampling bounds pair work", TestVisionBoundedCandidateSampling, ref passed, ref failed);
        RunTest("Vision: close elevated hostiles are spatially sampled", TestVisionCloseHostilesSpatiallySampled, ref passed, ref failed);
        RunTest("Vision: candidate work scales with living units", TestVisionWorkScalesWithLivingUnits, ref passed, ref failed);
        RunTest("Terrain: uniform voxel regions release dense buffers", TestTerrainRegionCompression, ref passed, ref failed);
        RunTest("Terrain: range cache avoids height-sized allocations", TestTerrainRangeCacheAllocations, ref passed, ref failed);
        RunTest("Vision: LOS respects 0.1 m terrain layers", TestVisionHeightUnits, ref passed, ref failed);
        RunTest("Vision: target sightings persist between rotating samples", TestVisionSampleMemory, ref passed, ref failed);
        RunTest("Vision: combat target scan skips friendly formations", TestVisionHostilePairsOnly, ref passed, ref failed);
        RunTest("Weapon: firing consumes one round and emits telemetry", TestWeaponAmmoConsumption, ref passed, ref failed);
        RunTest("Projectile: dead units are excluded from collision grid", TestDeadUnitsExcludedFromProjectileGrid, ref passed, ref failed);
        RunTest("Ballistics: close-range projectile can hit a real unit", TestProjectileHitsUnit, ref passed, ref failed);
        RunTest("Combat log: hit and blocked events survive shot spam", TestCombatLogPriorities, ref passed, ref failed);
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
            AimMode.SuppressFire)
        {
            return false;
        }

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
               AimMode.AimedShot;
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

    private static bool TestWeaponAimWarmup()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(4, 64);

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

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

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);

        bool firstAttemptFired = simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        if (firstAttemptFired ||
            simulation.Projectiles.ActiveCount != 0 ||
            simulation.Weapons.AimTarget[stateIndex] != target)
        {
            return false;
        }

        if (!simulation.Weapons.AimIndicatorActive[stateIndex])
            return false;

        simulation.Update(worldMap, 0.30f);

        bool firedTooEarly = simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        if (firedTooEarly ||
            simulation.TotalShotsFired != 0 ||
            simulation.Projectiles.ActiveCount != 0)
        {
            return false;
        }

        for (int i = 0; i < 12; i++)
        {
            simulation.Update(worldMap, 0.05f);

            if (simulation.TotalShotsFired > 0)
                break;
        }

        // Automatic mode must emit the first round only after the CE-style
        // range-scaled warmup, without needing another AI/target command.
        return simulation.TotalShotsFired == 1 &&
               !simulation.Weapons.AimIndicatorActive[stateIndex];
    }

    private static bool TestAimSurvivesVisionSampleGaps()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(4, 64);

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);
        Vector3 aimPoint = new Vector3(15.5f, 10.5f, 1.5f);

        simulation.Weapons.SetAimTarget(
            shooter.Index,
            UnitWeaponSlot.Primary,
            target,
            reset: true,
            aimPoint);
        simulation.Weapons.UpdateTimers(0.65f);

        UnitAiSystem.ClearAimingIfOccluded(
            simulation.Units,
            simulation.Weapons,
            simulation.Vision,
            worldMap,
            shooter.Index,
            target);

        if (simulation.Weapons.AimTarget[stateIndex] != target ||
            simulation.Weapons.AimTimer[stateIndex] < 0.65f ||
            !simulation.Weapons.AimIndicatorActive[stateIndex])
        {
            return false;
        }

        // A real obstruction must cancel the stored aim, even if the vision
        // candidate rotation was the original reason the target went stale.
        worldMap.SetSolidHeight(10, 10, 30, 1);

        UnitAiSystem.ClearAimingIfOccluded(
            simulation.Units,
            simulation.Weapons,
            simulation.Vision,
            worldMap,
            shooter.Index,
            target);

        return simulation.Weapons.AimTarget[stateIndex] != target &&
               simulation.Weapons.AimTimer[stateIndex] == 0f &&
               !simulation.Weapons.AimIndicatorActive[stateIndex];
    }

    private static bool TestAimModeDurations()
    {
        RangedWeaponConfig weapon = WeaponCatalog.AssaultRifle;

        float suppress = UnitWeaponSystem.GetAimDuration(
            weapon, AimMode.SuppressFire, 20f);
        float snapshot = UnitWeaponSystem.GetAimDuration(
            weapon, AimMode.Snapshot, 20f);
        float aimedNear = UnitWeaponSystem.GetAimDuration(
            weapon, AimMode.AimedShot, 20f);
        float aimedFar = UnitWeaponSystem.GetAimDuration(
            weapon, AimMode.AimedShot, 150f);

        return suppress > 0f &&
               suppress < snapshot &&
               snapshot < aimedNear &&
               aimedNear < aimedFar &&
               aimedFar >= 3.99f;
    }

    private static bool TestAutomaticFireCadence()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(4, 64);
        simulation.VisionEnabled = false;

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

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

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);
        simulation.Weapons.CurrentAimMode[stateIndex] = AimMode.SuppressFire;
        worldMap.SetSolidHeight(10, 10, 30, 1);

        bool startedAim = !simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        long shotsBeforeWarmup = simulation.TotalShotsFired;
        for (int i = 0; i < 18; i++)
            simulation.Update(worldMap, 0.05f);

        return startedAim &&
               shotsBeforeWarmup == 0 &&
               simulation.TotalShotsFired >= 4;
    }

    private static bool TestSingleFireReAims()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(4, 64);
        simulation.VisionEnabled = false;

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

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

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);
        simulation.Weapons.CurrentFireMode[stateIndex] = FireMode.Single;

        if (simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target))
            return false;

        simulation.Update(worldMap, 0.90f);
        worldMap.SetSolidHeight(10, 10, 30, 1);
        bool firstShot = simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        if (!firstShot ||
            simulation.TotalShotsFired != 1 ||
            simulation.Weapons.AimTarget[stateIndex].Generation != 0)
        {
            return false;
        }

        bool repeatedWithoutAim = simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        return !repeatedWithoutAim &&
               simulation.TotalShotsFired == 1 &&
               simulation.Weapons.AimTarget[stateIndex] == target &&
               simulation.Weapons.AimTimer[stateIndex] == 0f;
    }

    private static bool TestBurstFireCadence()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(4, 64);
        simulation.VisionEnabled = false;

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

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

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);
        simulation.Weapons.CurrentFireMode[stateIndex] = FireMode.Burst;
        simulation.Weapons.CurrentAimMode[stateIndex] = AimMode.SuppressFire;

        if (simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target))
            return false;

        worldMap.SetSolidHeight(10, 10, 30, 1);
        simulation.Update(worldMap, 0.90f);

        bool firstShot = simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        if (!firstShot || simulation.TotalShotsFired != 1)
            return false;

        for (int i = 0; i < 4; i++)
            simulation.Update(worldMap, 0.10f);

        if (simulation.TotalShotsFired != 3 ||
            simulation.Weapons.AimTarget[stateIndex].Generation != 0)
        {
            return false;
        }

        // A new burst cannot reuse the previous burst's completed warmup.
        bool startedNewAim = !simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target);

        return startedNewAim &&
               simulation.TotalShotsFired == 3 &&
               simulation.Weapons.AimTarget[stateIndex] == target &&
               simulation.Weapons.AimTimer[stateIndex] == 0f;
    }

    private static bool TestSuppressiveFireThroughOcclusion()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(4, 64);
        simulation.VisionEnabled = false;

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

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

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index,
            UnitWeaponSlot.Primary);
        simulation.Weapons.CurrentAimMode[stateIndex] = AimMode.SuppressFire;

        if (simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target))
            return false;

        // SuppressFire can start with known target position and then keep
        // shooting into the obstacle after the first aim delay.
        worldMap.SetSolidHeight(10, 10, 30, 1);
        for (int i = 0; i < 12; i++)
            simulation.Update(worldMap, 0.05f);

        return simulation.TotalShotsFired > 1;
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
        {
            worldMap.SetSolidHeight(wallX, y, wallHeight, 2);
            worldMap.SetNavigationBlocked(wallX, y, true);
        }

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

        // Flatten the ridge and seal every cell with the explicit navigation-blocker layer.
        // This proves the pathfinder respects non-terrain obstacles as well as height steps.
        for (int y = 0; y < height; y++)
        {
            worldMap.SetSolidHeight(wallX, y, floorHeight, 1);
            worldMap.SetNavigationBlocked(wallX, y, true);
        }

        // Reuse the planner to verify terrain-version changes invalidate its cached grid.
        navigation.BeginUpdate(simulation.Units, worldMap);

        bool foundBlockedRoute = navigation.TryGetWaypoint(
            simulation.Units,
            unit.Index,
            worldMap,
            start,
            target,
            out _);

        return !foundBlockedRoute &&
               navigation.RoutesFailedThisUpdate == 1 &&
               navigation.NavigationGridBuildMilliseconds >= 0d;
    }

    private static bool TestNavigationRouteReuse()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 50);

        const ushort floorHeight = 10;
        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
                worldMap.SetSolidHeight(x, y, floorHeight, 1);
        }

        UnitSimulation simulation = new UnitSimulation(32, 64);
        Vector3 start = new Vector3(5.5f, 24.5f, 1f);
        Vector3 target = new Vector3(42.5f, 24.5f, 1f);

        UnitId first = simulation.Spawn(
            UnitType.Colonist, start, Vector3.UnitX, Vector3.UnitX);
        UnitId second = simulation.Spawn(
            UnitType.Colonist, start, Vector3.UnitX, Vector3.UnitX);

        UnitNavigationSystem navigation = simulation.Navigation;
        navigation.BeginUpdate(simulation.Units, worldMap);

        bool firstFound = navigation.TryGetWaypoint(
            simulation.Units, first.Index, worldMap, start, target,
            out Vector2 firstWaypoint);
        bool secondFound = navigation.TryGetWaypoint(
            simulation.Units, second.Index, worldMap, start, target,
            out Vector2 secondWaypoint);

        bool independentPaths =
            firstFound &&
            secondFound &&
            firstWaypoint != secondWaypoint &&
            navigation.RoutesBuiltThisUpdate == 2 &&
            navigation.RoutesBuiltUsingPriorRoutesThisUpdate == 1 &&
            navigation.RouteTrafficCellsConsideredThisUpdate > 0;

        if (!independentPaths)
            return false;

        // A second tick with unchanged terrain/orders should reuse each unit's
        // stored path without starting more A* searches.
        navigation.BeginUpdate(simulation.Units, worldMap);
        navigation.TryGetWaypoint(
            simulation.Units, first.Index, worldMap, start, target, out _);
        navigation.TryGetWaypoint(
            simulation.Units, second.Index, worldMap, start, target, out _);

        return navigation.RoutesBuiltThisUpdate == 0 &&
               navigation.RouteTrafficCellsConsideredThisUpdate == 0;
    }

    private static bool TestAiSearchAdvance()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 12);

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
                worldMap.SetSolidHeight(x, y, 10, 1);
        }

        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId scout = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(4.5f, 24.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        int index = scout.Index;
        simulation.Units.ViewRange[index] = 2f;
        simulation.Units.FieldOfView[index] = 180f;
        simulation.AI.Enabled = true;
        simulation.VisionEnabled = true;

        float startX = simulation.Units.Position[index].X;
        for (int tick = 0; tick < 20; tick++)
            simulation.Update(worldMap, 0.1f);

        return simulation.Units.Position[index].X > startX + 3f &&
               simulation.AI.Store.State[index] == UnitAiState.Search;
    }

    private static bool TestTerrainRegionCompression()
    {
        TerrainRegion region = new TerrainRegion(
            0,
            0,
            0,
            new TerrainTileRegion?[1],
            0);

        if (region.HasDenseMaterialBuffer ||
            region.HasSparseMaterialBuffer)
        {
            return false;
        }

        region.SetMaterialIdAtIndex(0, 1);

        if (region.HasDenseMaterialBuffer ||
            !region.HasSparseMaterialBuffer ||
            region.GetMaterialIdAtIndex(0) != 1 ||
            region.GetMaterialIdAtIndex(1) != 0)
        {
            return false;
        }

        for (int i = 1; i < TerrainRegion.TotalTiles; i++)
            region.SetMaterialIdAtIndex(i, 1);

        if (region.HasDenseMaterialBuffer ||
            region.HasSparseMaterialBuffer ||
            region.GetMaterialIdAtIndex(0) != 1 ||
            region.GetMaterialIdAtIndex(TerrainRegion.TotalTiles - 1) != 1)
        {
            return false;
        }

        region.SetMaterialIdAtIndex(0, 0);

        if (region.HasDenseMaterialBuffer ||
            !region.HasSparseMaterialBuffer ||
            region.GetMaterialIdAtIndex(0) != 0 ||
            region.GetMaterialIdAtIndex(1) != 1)
        {
            return false;
        }

        // A third material expands the compact base+override representation
        // into dense storage without changing existing cell values.
        region.SetMaterialIdAtIndex(1, 2);

        return region.HasDenseMaterialBuffer &&
               region.GetMaterialIdAtIndex(0) == 0 &&
               region.GetMaterialIdAtIndex(1) == 2 &&
               region.GetMaterialIdAtIndex(2) == 1;
    }

    private static bool TestTerrainRangeCacheAllocations()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: WorldMap.DefaultTerrainLayerCount);

        worldMap.SetSolidHeight(5, 5, 100, 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ReadOnlySpan<TileRange> ranges = worldMap.GetTileRanges(5, 5);
        long allocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - before;

        if (ranges.Length != 2 ||
            ranges[0].StartZ != 0 ||
            ranges[0].EndZ != 100 ||
            ranges[0].MaterialId != 1 ||
            ranges[0].State != WorldMap.StateSolid ||
            ranges[1].StartZ != 100 ||
            ranges[1].EndZ != WorldMap.DefaultTerrainLayerCount ||
            ranges[1].State != WorldMap.StateEmpty ||
            allocatedBytes >= 1024)
        {
            return false;
        }

        before = GC.GetAllocatedBytesForCurrentThread();
        _ = worldMap.GetTileRanges(5, 5);
        return GC.GetAllocatedBytesForCurrentThread() == before;
    }

    private static bool TestVisionBoundedCandidateSampling()
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
                    Vector3.UnitX,
                    factionTag: (ushort)((row * columns + column) % 2 + 1));

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

        return simulation.Vision.LastActiveCandidatesScanned == unitCount * 64 &&
               simulation.Vision.LastCandidatePairs < bruteForcePairs / 4 &&
               simulation.Vision.LastVisibleTargetCount == 0 &&
               simulation.Vision.UpdateCount == 1;
    }

    private static bool TestVisionCloseHostilesSpatiallySampled()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 5,
            regionsY: 5,
            layerCount: 64);
        worldMap.SetSolidHeight(20, 20, 10, 1);
        worldMap.SetSolidHeight(21, 20, 20, 1);
        UnitSimulation simulation = new UnitSimulation(256, 512);

        UnitId observer = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(20.5f, 20.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        simulation.Units.ViewRange[observer.Index] = 10f;
        simulation.Units.FieldOfView[observer.Index] = 360f;

        // The target stands on a 2 m surface next to the observer's 1 m surface.
        // Keep many distant units between them in active-list order: index-only
        // rolling samples miss this visible close target.
        for (int i = 0; i < 198; i++)
        {
            UnitId distant = simulation.Spawn(
                UnitType.Colonist,
                new Vector3(100f + i * 0.5f, 100.5f, 1f),
                factionTag: (ushort)(i % 2 + 1));

            simulation.Units.ViewRange[distant.Index] = 5f;
            simulation.Units.FieldOfView[distant.Index] = 360f;
        }

        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(21.5f, 20.5f, 2f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

        simulation.Units.ViewRange[target.Index] = 10f;
        simulation.Units.FieldOfView[target.Index] = 360f;

        simulation.Vision.Update(
            simulation.Units,
            worldMap,
            0.11f,
            simulation.Health);

        ReadOnlySpan<int> visibleTargets =
            simulation.Vision.GetVisibleTargets(observer.Index);

        for (int i = 0; i < visibleTargets.Length; i++)
        {
            if (visibleTargets[i] == target.Index)
                return true;
        }

        return false;
    }

    private static bool TestVisionWorkScalesWithLivingUnits()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 16);
        UnitSimulation simulation = new UnitSimulation(128, 512);

        for (int i = 0; i < 100; i++)
        {
            UnitId unit = simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    10.5f + (i % 10) * 2f,
                    10.5f + (i / 10) * 2f,
                    0.1f),
                factionTag: (ushort)(i % 2 + 1));

            simulation.Units.ViewRange[unit.Index] = 40f;
            simulation.Units.FieldOfView[unit.Index] = 360f;

            if (i < 75)
                simulation.Health.OverallHitPoints[unit.Index] = 0f;
        }

        simulation.Vision.Update(
            simulation.Units,
            worldMap,
            0.11f,
            simulation.Health);

        // Corpses stay in UnitStore for display, but vision work should only
        // consider the 25 living units: 25 observers x 25 sampled entries.
        return simulation.Units.ActiveCount == 100 &&
               simulation.Vision.LastActiveCandidatesScanned == 25 * 25 &&
               simulation.Vision.LastTargetEvaluations <= 25 * 16 &&
               simulation.Vision.LastVisibleTargetCount > 0;
    }

    private static bool TestVisionHeightUnits()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 100);

        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
                worldMap.SetSolidHeight(x, y, 10, 1);
        }

        // A 6 m obstacle occupies layers 0..59 (each layer is 0.1 m).
        // A ray at 7 m must pass over it; a ray at 2 m must be blocked.
        worldMap.SetSolidHeight(12, 10, 60, 2);

        VisionSystem vision = new VisionSystem();

        bool aboveWallVisible = vision.HasLineOfSight(
            worldMap,
            new Vector3(5.5f, 10.5f, 7f),
            new Vector3(20.5f, 10.5f, 7f),
            out _);

        bool throughWallVisible = vision.HasLineOfSight(
            worldMap,
            new Vector3(5.5f, 10.5f, 2f),
            new Vector3(20.5f, 10.5f, 2f),
            out _);

        return aboveWallVisible && !throughWallVisible;
    }

    private static bool TestVisionSampleMemory()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 64);
        UnitSimulation simulation = new UnitSimulation(16, 64);

        UnitId observer = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(2.5f, 20.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        simulation.Units.ViewRange[observer.Index] = 30f;
        simulation.Units.FieldOfView[observer.Index] = 360f;

        UnitId thirdTarget = default;
        for (int i = 0; i < 12; i++)
        {
            UnitId target = simulation.Spawn(
                UnitType.Colonist,
                new Vector3(10.5f + i, 20.5f, 0.1f),
                -Vector3.UnitX,
                -Vector3.UnitX,
                factionTag: 2);

            simulation.Units.ViewRange[target.Index] = 30f;
            simulation.Units.FieldOfView[target.Index] = 360f;

            if (i == 2)
                thirdTarget = target;
        }

        // Three scans advance the rotating slot past targets 1 and 2 to target 3.
        for (int tick = 0; tick < 4; tick++)
        {
            simulation.Vision.Update(
                simulation.Units,
                worldMap,
                0.11f,
                simulation.Health);
        }

        // The target is not one of the two nearest or the farthest candidates,
        // and may not appear in this tick's compact list. Its verified sighting
        // must nevertheless remain available to the current AI target memory.
        return thirdTarget.Index >= 0 &&
               simulation.Vision.IsRecentlyVisible(
                   observer.Index,
                   thirdTarget.Index,
                   simulation.Units);
    }

    private static bool TestVisionHostilePairsOnly()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 48);
        UnitSimulation simulation = new UnitSimulation(128, 512);

        for (int i = 0; i < 80; i++)
        {
            UnitId ally = simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    10.2f + (i % 10) * 0.35f,
                    10.2f + (i / 10) * 0.35f,
                    0.1f),
                factionTag: 1);

            simulation.Units.ViewRange[ally.Index] = 30f;
            simulation.Units.FieldOfView[ally.Index] = 360f;
        }

        for (int i = 0; i < 4; i++)
        {
            UnitId enemy = simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    12.0f + i * 0.25f,
                    13.7f,
                    0.1f),
                factionTag: 2);

            simulation.Units.ViewRange[enemy.Index] = 30f;
            simulation.Units.FieldOfView[enemy.Index] = 360f;
        }

        simulation.Vision.Update(
            simulation.Units,
            worldMap,
            0.11f,
            simulation.Health);

        // Only the 80x4 hostile pairs in both directions need terrain LOS.
        // 84 units produce 6,972 possible directed non-self pairs without filtering.
        int activeCount = simulation.Units.ActiveCount;
        int bruteForcePairs = activeCount * (activeCount - 1);

        return simulation.Vision.LastCandidatePairs <= 80 * 4 * 2 &&
               simulation.Vision.LastCandidatePairs > 0 &&
               simulation.Vision.LastTargetEvaluations > 0 &&
               simulation.Vision.LastActiveCandidatesScanned > 0 &&
               simulation.Vision.LastActiveCandidatesScanned <= activeCount * 64 &&
               simulation.Vision.LastActiveCandidatesScanned < bruteForcePairs &&
               simulation.Vision.LastTargetEvaluations <= activeCount * 16 &&
               simulation.Vision.LastVisibilityMemoryEntriesScanned <= activeCount * 16 &&
               simulation.Vision.LastVisibilityMemoryEntriesScanned < bruteForcePairs &&
               simulation.Vision.LastLineOfSightChecks > 0 &&
               simulation.Vision.LastVisibleTargetCount > 0;
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

    private static bool TestDeadUnitsExcludedFromProjectileGrid()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 50);
        UnitSimulation simulation = new UnitSimulation(4, 64);

        UnitId dead = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 1f),
            factionTag: 1);
        UnitId living = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(20.5f, 20.5f, 1f),
            factionTag: 2);

        simulation.Health.OverallHitPoints[dead.Index] = 0f;

        UnitSpatialGrid grid = new UnitSpatialGrid();
        grid.Ensure(worldMap, simulation.Units.Capacity);
        grid.Build(simulation.Units, simulation.Health);

        return grid.GetCellHead(10, 10) < 0 &&
               grid.GetCellHead(20, 20) >= 0 &&
               simulation.Units.IsAlive(dead) &&
               simulation.Units.IsAlive(living);
    }

    private static bool TestProjectileHitsUnit()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 50);
        UnitSimulation simulation = new UnitSimulation(4, 64);

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 1f),
            Vector3.UnitY,
            Vector3.UnitY);

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

        // Aim first, then let the weapon's own update fire the first round once
        // the range-scaled warmup elapses. Small steps preserve projectile flight.
        Vector3 visibleAimPoint = simulation.Units.Position[target.Index] +
            new Vector3(0f, 0f, simulation.Units.Height[target.Index] * 0.55f);

        bool startedAiming = !simulation.FireWeaponAt(
            shooter,
            UnitWeaponSlot.Primary,
            target,
            visibleAimPoint);

        if (!startedAiming || simulation.TotalShotsFired != 0)
            return false;

        for (int i = 0; i < 15; i++)
            simulation.Update(worldMap, 0.05f);

        if (simulation.TotalShotsFired != 0)
            return false;

        for (int i = 0; i < 4; i++)
        {
            simulation.Update(worldMap, 0.05f);
            if (simulation.TotalShotsFired > 0)
                break;
        }

        if (simulation.TotalShotsFired != 1)
            return false;

        for (int i = 0; i < 4 && simulation.Projectiles.TotalHits == 0; i++)
            simulation.Update(worldMap, 0.05f);

        return simulation.Projectiles.TotalHits > 0;
    }

    private static bool TestCombatLogPriorities()
    {
        bool wasEnabled = CombatDiagnostics.Enabled;
        TextWriter originalOut = Console.Out;
        using StringWriter output = new StringWriter();

        try
        {
            Console.SetOut(output);
            CombatDiagnostics.Enabled = true;

            for (int i = 0; i < 12; i++)
                CombatDiagnostics.WriteLine($"[SHOT] test={i}");

            CombatDiagnostics.WriteLine("[HIT] target=Unit[2:1]");
            CombatDiagnostics.WriteLine("[BLOCKED] material=1");

            string log = output.ToString();
            int shotLines = 0;
            int offset = 0;

            while ((offset = log.IndexOf("[SHOT]", offset, StringComparison.Ordinal)) >= 0)
            {
                shotLines++;
                offset += 6;
            }

            return shotLines == 5 &&
                   log.Contains("[HIT]", StringComparison.Ordinal) &&
                   log.Contains("[BLOCKED]", StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            CombatDiagnostics.Enabled = wasEnabled;
        }
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
