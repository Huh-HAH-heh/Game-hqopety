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
        RunTest("Hit regions: custom circles drive visibility, hits and armor", TestHitRegionProfilesDriveVisionCollisionAndArmor, ref passed, ref failed);
        RunTest("Anatomy: colonist nodes share stable tissue IDs", TestColonistUsesAnatomyNodes, ref passed, ref failed);
        RunTest("Anatomy: rig edits preserve tissue IDs and organ volumes", TestAnatomyRigEditsPreserveBindings, ref passed, ref failed);
        RunTest("Anatomy: rig editor can add dependent regions", TestAnatomyRigCanAppendDependency, ref passed, ref failed);
        RunTest("Anatomy: new organs get damageable tissue and stay inside their host", TestAnatomyRigOrganVolumeAndHealth, ref passed, ref failed);
        RunTest("Anatomy: pose adjustments survive profile and body-store rebuilds", TestAnatomyRigPoseOffsets, ref passed, ref failed);
        RunTest("Vision: extended hit region is detected inside range", TestVisionDetectsRegionInsideRange, ref passed, ref failed);
        RunTest("Targeting: aim modes select hit regions", TestTargetModesAimAtHitRegions, ref passed, ref failed);
        RunTest("Targeting: modes rank arbitrary vertical segments", TestTargetModesRankVerticalSegments, ref passed, ref failed);
        RunTest("Damage: projectile events outside hit regions are rejected", TestDamageRequiresHitRegion, ref passed, ref failed);
        RunTest("Vision: target sightings persist between rotating samples", TestVisionSampleMemory, ref passed, ref failed);
        RunTest("Vision: combat target scan skips friendly formations", TestVisionHostilePairsOnly, ref passed, ref failed);
        RunTest("Weapon: firing consumes one round and emits telemetry", TestWeaponAmmoConsumption, ref passed, ref failed);
        RunTest("Projectile: dead units are excluded from collision grid", TestDeadUnitsExcludedFromProjectileGrid, ref passed, ref failed);
        RunTest("Ballistics: close-range projectile can hit a real unit", TestProjectileHitsUnit, ref passed, ref failed);
        RunTest("Combat log: hit and blocked events survive shot spam", TestCombatLogPriorities, ref passed, ref failed);
        RunTest("Lifecycle: dead units stop moving and firing", TestDeadUnitCleanup, ref passed, ref failed);
        RunTest("Anatomy: all built-in and custom body definitions transform correctly", TestAnatomyProfiles, ref passed, ref failed);
        RunTest("Anatomy: named markers drive eye, aim and muzzle points", TestNamedAnatomyMarkers, ref passed, ref failed);
        RunTest("Anatomy: custom segments own health and can be appended live", TestGenericAnatomyTissueHealth, ref passed, ref failed);
        RunTest("Armor: starting colonist armor mitigates repeated rifle hits", TestStartingArmorMitigatesRifleHits, ref passed, ref failed);
        RunTest("High-tech: repeater, laser and railgun profiles differ", TestHighTechWeaponProfiles, ref passed, ref failed);
        RunTest("High-tech: powered exoskeleton boosts speed while intact", TestExoskeletonMovementBonus, ref passed, ref failed);
        RunTest("Equipment: clothing coverage and visual metadata", TestEquipmentVisualMetadata, ref passed, ref failed);

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

                    private static bool TestHitRegionProfilesDriveVisionCollisionAndArmor()
    {
        UnitHitRegionProfile profile = new UnitHitRegionProfile(
            new UnitCircleHitRegion(
                new Vector3(0f, 0f, 0.50f),
                radius: 0.50f,
                halfHeight: 0.25f,
                new HitRegionPartBinding(UnitHealthPartId.Torso, maxHitPoints: 60f)),
            new UnitCircleHitRegion(
                new Vector3(0f, 0f, 1.50f),
                radius: 0.20f,
                halfHeight: 0.20f,
                new HitRegionPartBinding(UnitHealthPartId.Head, maxHitPoints: 40f)),
            new UnitCircleHitRegion(
                new Vector3(1.10f, 0f, 0.50f),
                radius: 0.30f,
                halfHeight: 0.20f,
                new HitRegionPartBinding(UnitHealthPartId.LeftArm, maxHitPoints: 30f)));

        UnitSimulation simulation = new UnitSimulation(8, 64);
        UnitId unit = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10f, 10f, 0.1f),
            hitProfile: profile);

        if (simulation.HitRegions.GetProfile(unit.Index)?.RegionCount != 3)
            return false;

        Span<Vector3> visibilityPoints = stackalloc Vector3[3];
        int visibilityCount = UnitHitSystem.GetVisibilityPoints(
            simulation.Units,
            unit.Index,
            new Vector3(7f, 10f, 2f),
            visibilityPoints,
            simulation.Bodies,
            simulation.HitRegions);

        if (visibilityCount != 3)
            return false;

        UnitHitSystem hitSystem = new UnitHitSystem();

        if (!hitSystem.TryHitUnit(
                simulation.Units,
                unit.Index,
                new Vector3(8f, 10f, 0.6f),
                new Vector3(12f, 10f, 0.6f),
                0f,
                UnitHealthPartId.None,
                out UnitHitResult torsoHit,
                simulation.Bodies,
                simulation.HitRegions) ||
            torsoHit.Part != UnitHealthPartId.Torso ||
            torsoHit.RegionIndex != 0)
        {
            return false;
        }

        if (!hitSystem.TryHitUnit(
                simulation.Units,
                unit.Index,
                new Vector3(8f, 10f, 1.6f),
                new Vector3(12f, 10f, 1.6f),
                0f,
                UnitHealthPartId.None,
                out UnitHitResult headHit,
                simulation.Bodies,
                simulation.HitRegions) ||
            headHit.Part != UnitHealthPartId.Head ||
            headHit.RegionIndex != 1)
        {
            return false;
        }

        if (hitSystem.TryHitUnit(
                simulation.Units,
                unit.Index,
                new Vector3(8f, 10f, 2.4f),
                new Vector3(12f, 10f, 2.4f),
                0f,
                UnitHealthPartId.None,
                out _,
                simulation.Bodies,
                simulation.HitRegions))
        {
            return false;
        }

        UnitDamageResult damage = simulation.ApplyDamage(
            unit,
            torsoHit.Part,
            20f);

        return damage.ArmorAbsorbed > 0f &&
            damage.FinalDamage < damage.RawDamage;
    }

    private static bool TestColonistUsesAnatomyNodes()
    {
        UnitAnatomyProfile profile = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        if (profile.RegionCount != 13 || profile.NodeCount != 13 || !profile.HasSharedShapes)
            return false;

        int lowerTorso = profile.FindRegion("body.lower");
        int torso = profile.FindRegion("body.middle");
        int upperTorso = profile.FindRegion("body.torso.upper");
        int leftArmUpper = profile.FindRegion("body.arm.left.upper");
        int leftArmLower = profile.FindRegion("body.arm.left.lower");
        int rightArmUpper = profile.FindRegion("body.arm.right.upper");
        int rightArmLower = profile.FindRegion("body.arm.right.lower");
        int leftLeg = profile.FindRegion("body.leg.left.upper");
        int leftShin = profile.FindRegion("body.leg.left.lower");
        int rightLeg = profile.FindRegion("body.leg.right.upper");
        int rightShin = profile.FindRegion("body.leg.right.lower");
        int neck = profile.FindRegion("body.neck");
        int head = profile.FindRegion("body.upper");

        if (lowerTorso < 0 || torso < 0 || upperTorso < 0 ||
            leftArmUpper < 0 || leftArmLower < 0 ||
            rightArmUpper < 0 || rightArmLower < 0 ||
            leftLeg < 0 || leftShin < 0 || rightLeg < 0 || rightShin < 0 ||
            neck < 0 || head < 0 ||
            !profile.TryFindPart(UnitHealthPartId.Torso, out int torsoBindingRegion, out _) ||
            torsoBindingRegion != torso ||
            !profile.TryFindPart(UnitHealthPartId.LeftLeg, out int legBindingRegion, out _) ||
            legBindingRegion != leftLeg ||
            !profile.TryFindPart("heart.main", out int heartRegion, out _) ||
            heartRegion != torso ||
            !profile.TryFindPart("brain.main", out int brainRegion, out _) ||
            brainRegion != head ||
            !profile.TryFindPart(UnitHealthPartId.Neck, out int neckBindingRegion, out _) ||
            neckBindingRegion != neck ||
            profile.VolumeCount != 8)
            return false;

        UnitSimulation simulation = new UnitSimulation(4, 32);
        UnitId unit = simulation.Spawn(UnitType.Colonist, new Vector3(10f, 10f, 0.1f));

        Vector3 legsCenter = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, leftLeg, simulation.HitRegions, simulation.Bodies).Center;
        Vector3 torsoCenter = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, torso, simulation.HitRegions, simulation.Bodies).Center;
        Vector3 headCenter = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, head, simulation.HitRegions, simulation.Bodies).Center;

        int bodyStart = simulation.Units.BodyStart[unit.Index];
        bool sharedGeometry =
            Vector3.Distance(legsCenter, simulation.Bodies.WorldPosition[bodyStart + leftLeg]) < 0.001f &&
            Vector3.Distance(torsoCenter, simulation.Bodies.WorldPosition[bodyStart + torso]) < 0.001f &&
            Vector3.Distance(headCenter, simulation.Bodies.WorldPosition[bodyStart + head]) < 0.001f;

        return sharedGeometry &&
            legsCenter.Z < torsoCenter.Z &&
            torsoCenter.Z < headCenter.Z &&
            UnitHitSystem.GetVisibilityPointCount(simulation.Units, unit.Index, simulation.HitRegions) == 13;
    }

    private static bool TestAnatomyRigEditsPreserveBindings()
    {
        UnitAnatomyProfile original = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        BodyPartDefinition[] parts = original.BodyParts.ToArray();
        if (parts.Length != original.NodeCount || parts.Length < 3)
            return false;

        BodyPartDefinition unchanged = parts[2];
        parts[2] = new BodyPartDefinition(
            unchanged.LocalPosition + new Vector3(0f, 0.012f, 0f),
            unchanged.Scale * 1.02f,
            unchanged.Primitive,
            parentIndex: unchanged.ParentIndex,
            localRotation: unchanged.LocalRotation,
            motion: unchanged.Motion,
            followDistance: unchanged.FollowDistance,
            anchor: unchanged.Anchor,
            role: unchanged.Role,
            pivot: unchanged.Pivot);

        UnitAnatomyProfile edited = original.WithBodyParts(parts);
        if (edited.NodeCount != original.NodeCount ||
            edited.VolumeCount != original.VolumeCount ||
            edited.GetNode(2).Shape.LocalPosition == unchanged.LocalPosition ||
            original.GetNode(2).Shape.LocalPosition != unchanged.LocalPosition)
            return false;

        for (int i = 0; i < original.NodeCount; i++)
        {
            UnitAnatomyNode before = original.GetNode(i);
            UnitAnatomyNode after = edited.GetNode(i);
            if (before.Id != after.Id || before.ParentId != after.ParentId ||
                before.Tissues.Length != after.Tissues.Length)
                return false;

            ReadOnlySpan<HitRegionPartBinding> oldTissues = before.Tissues;
            ReadOnlySpan<HitRegionPartBinding> newTissues = after.Tissues;
            for (int j = 0; j < oldTissues.Length; j++)
            {
                if (oldTissues[j].Id != newTissues[j].Id ||
                    oldTissues[j].Part != newTissues[j].Part ||
                    oldTissues[j].Kind != newTissues[j].Kind)
                    return false;
            }
        }

        ReadOnlySpan<UnitAnatomyVolume> oldVolumes = original.Volumes;
        ReadOnlySpan<UnitAnatomyVolume> newVolumes = edited.Volumes;
        for (int i = 0; i < oldVolumes.Length; i++)
        {
            if (oldVolumes[i].Id != newVolumes[i].Id ||
                oldVolumes[i].TargetBindingId != newVolumes[i].TargetBindingId ||
                oldVolumes[i].ParentRegionId != newVolumes[i].ParentRegionId)
                return false;
        }

        return true;
    }

    private static bool TestAnatomyRigPoseOffsets()
    {
        UnitAnatomyProfile original = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        ReadOnlySpan<UnitAnatomyNode> nodes = original.Nodes;
        string[] ids = new string[original.NodeCount];
        BodyPartDefinition[] parts = original.BodyParts.ToArray();
        int armIndex = original.FindRegion("body.arm.left.lower");
        if (armIndex < 0)
            return false;

        for (int i = 0; i < ids.Length; i++)
            ids[i] = nodes[i].Id;

        BodyPartDefinition part = parts[armIndex];
        Vector3 sittingOffset = new(-0.02f, 0.03f, 0.04f);
        Vector3 lyingOffset = new(0.02f, -0.03f, 0.01f);
        parts[armIndex] = new BodyPartDefinition(
            part.LocalPosition,
            part.Scale,
            part.Primitive,
            parentIndex: part.ParentIndex,
            localRotation: part.LocalRotation,
            motion: part.Motion,
            followDistance: part.FollowDistance,
            anchor: part.Anchor,
            role: part.Role,
            pivot: part.Pivot,
            crouchingOffset: new Vector3(0.01f, 0.02f, -0.01f),
            sittingOffset: sittingOffset,
            lyingOffset: lyingOffset,
            crouchingRotationOffset: 0.15f,
            sittingRotationOffset: -0.3f,
            lyingRotationOffset: 0.45f);

        UnitAnatomyProfile edited = original.WithRig(ids, parts);
        BodyPartDefinition result = edited.GetNode(armIndex).Shape;
        if (result.SittingOffset != sittingOffset ||
            result.LyingOffset != lyingOffset ||
            MathF.Abs(result.SittingRotationOffset + 0.3f) > 0.0001f ||
            MathF.Abs(result.CrouchingRotationOffset - 0.15f) > 0.0001f ||
            edited.VolumeCount != original.VolumeCount)
            return false;

        var bodies = new UnitBodyStore(4);
        BodyHandle handle = bodies.CreateBody(parts);
        int storedIndex = handle.Start + armIndex;
        return bodies.SittingOffset[storedIndex] == sittingOffset &&
               bodies.LyingOffset[storedIndex] == lyingOffset &&
               MathF.Abs(bodies.SittingRotationOffset[storedIndex] + 0.3f) < 0.0001f &&
               MathF.Abs(bodies.LyingRotationOffset[storedIndex] - 0.45f) < 0.0001f;
    }

    private static bool TestAnatomyRigOrganVolumeAndHealth()
    {
        UnitAnatomyProfile original = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        ReadOnlySpan<UnitAnatomyNode> originalNodes = original.Nodes;
        BodyPartDefinition[] originalParts = original.BodyParts.ToArray();
        string[] ids = new string[original.NodeCount + 1];
        BodyPartDefinition[] parts = new BodyPartDefinition[originalParts.Length + 1];
        for (int i = 0; i < original.NodeCount; i++)
        {
            ids[i] = originalNodes[i].Id;
            parts[i] = originalParts[i];
        }

        ids[^1] = "custom.organ.host";
        parts[^1] = new BodyPartDefinition(
            new Vector3(0f, 0f, -0.18f),
            new Vector3(0.04f, 0.03f, 0.08f),
            BodyPrimitive.Circle,
            parentIndex: original.FindRegion("body.middle"),
            anchor: BodyPartAnchor.Parent,
            role: BodyPartRole.Generic);

        ReadOnlySpan<UnitAnatomyVolume> oldVolumes = original.Volumes;
        var volumes = new UnitAnatomyVolume[oldVolumes.Length + 1];
        oldVolumes.CopyTo(volumes);
        volumes[^1] = new UnitAnatomyVolume(
            "organ.test",
            "custom.organ.host",
            "custom.organ.host.tissue",
            "organ.test.tissue",
            new Vector3(0.5f, -0.3f, 0.2f),
            new Vector3(0.4f, 0.35f, 0.3f),
            BodyPrimitive.Circle,
            energyLoss: 22f,
            penetrationLoss: 1.5f,
            damageMultiplier: 1.25f,
            bleedMultiplier: 1.4f);

        UnitAnatomyProfile edited = original.WithRig(ids, parts, volumes);
        int hostIndex = edited.FindRegion("custom.organ.host");
        if (hostIndex < 0 || edited.VolumeCount != original.VolumeCount + 1)
            return false;

        ReadOnlySpan<HitRegionPartBinding> tissues = edited.GetTissues(hostIndex);
        bool damageableOrganFound = false;
        for (int i = 0; i < tissues.Length; i++)
        {
            if (tissues[i].Id != "organ.test.tissue")
                continue;

            damageableOrganFound =
                tissues[i].Kind == UnitHealthPartKind.Organ &&
                MathF.Abs(tissues[i].MaxHitPoints - 20f) < 0.001f;
            break;
        }
        if (!damageableOrganFound)
            return false;

        UnitAnatomyVolume organ = edited.Volumes[^1];
        Vector3 hostScale = Vector3.Abs(parts[^1].Scale);
        float cos = MathF.Abs(MathF.Cos(organ.LocalRotation));
        float sin = MathF.Abs(MathF.Sin(organ.LocalRotation));
        float halfX = cos * organ.HalfExtents.X + sin * organ.HalfExtents.Y;
        float halfY = sin * organ.HalfExtents.X + cos * organ.HalfExtents.Y;
        float nx = (MathF.Abs(organ.LocalCenter.X) + halfX) / hostScale.X;
        float ny = (MathF.Abs(organ.LocalCenter.Y) + halfY) / hostScale.Y;
        float nz = (MathF.Abs(organ.LocalCenter.Z) + organ.HalfExtents.Z) / hostScale.Z;
        return MathF.Sqrt(nx * nx + ny * ny + nz * nz) <= 0.93f;
    }

    private static bool TestAnatomyRigCanAppendDependency()
    {
        UnitAnatomyProfile original = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        ReadOnlySpan<UnitAnatomyNode> originalNodes = original.Nodes;
        BodyPartDefinition[] currentParts = original.BodyParts.ToArray();
        string[] ids = new string[original.NodeCount + 1];
        BodyPartDefinition[] parts = new BodyPartDefinition[currentParts.Length + 1];

        for (int i = 0; i < original.NodeCount; i++)
        {
            ids[i] = originalNodes[i].Id;
            parts[i] = currentParts[i];
        }

        int neck = original.FindRegion("body.neck");
        int head = original.FindRegion("body.upper");
        if (neck < 0 || head < 0 || neck >= head)
            return false;

        BodyPartDefinition headPart = parts[head];
        parts[head] = new BodyPartDefinition(
            headPart.LocalPosition,
            headPart.Scale,
            headPart.Primitive,
            parentIndex: neck,
            localRotation: headPart.LocalRotation,
            motion: headPart.Motion,
            followDistance: headPart.FollowDistance,
            anchor: BodyPartAnchor.Parent,
            role: headPart.Role,
            pivot: headPart.Pivot);

        ids[^1] = "custom.test.segment";
        parts[^1] = new BodyPartDefinition(
            new Vector3(0f, 0f, -0.06f),
            new Vector3(0.03f, 0.02f, 0.05f),
            BodyPrimitive.Circle,
            parentIndex: neck,
            anchor: BodyPartAnchor.Parent,
            role: BodyPartRole.Generic);

        UnitAnatomyProfile edited = original.WithRig(ids, parts);
        if (edited.NodeCount != original.NodeCount + 1 ||
            edited.VolumeCount != original.VolumeCount ||
            edited.GetNode(head).ParentId != "body.neck" ||
            edited.GetNode(edited.NodeCount - 1).ParentId != "body.neck" ||
            !edited.TryFindPart("custom.test.segment.tissue", out int customRegion, out _) ||
            customRegion != edited.NodeCount - 1)
            return false;

        for (int i = 0; i < original.VolumeCount; i++)
        {
            UnitAnatomyVolume before = original.Volumes[i];
            UnitAnatomyVolume after = edited.Volumes[i];
            if (before.Id != after.Id ||
                before.ParentRegionId != after.ParentRegionId ||
                before.TargetBindingId != after.TargetBindingId)
                return false;
        }

        return true;
    }

private static bool TestVisionDetectsRegionInsideRange()
    {
        WorldMap worldMap = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 32);
        UnitSimulation simulation = new UnitSimulation(4, 64);

        UnitId observer = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitHitRegionProfile offsetTargetProfile = new UnitHitRegionProfile(
            new UnitCircleHitRegion(
                new Vector3(-3f, 0f, 0.5f),
                radius: 0.35f,
                halfHeight: 0.30f,
                new HitRegionPartBinding(UnitHealthPartId.Torso, maxHitPoints: 60f)));
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(14.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 2,
            hitProfile: offsetTargetProfile);

        simulation.Units.ViewRange[observer.Index] = 1f;
        simulation.Units.FieldOfView[observer.Index] = 360f;

        // The root is four world units away; only the configured hit region
        // is within range and it occupies a cell next to the observer.
        VisionCheck direct = simulation.Vision.Evaluate(
            simulation.Units,
            worldMap,
            observer.Index,
            target.Index,
            simulation.Bodies);

        if (!direct.IsVisible)
            return false;

        simulation.Vision.Update(
            simulation.Units,
            worldMap,
            0.11f,
            simulation.Health,
            simulation.Bodies);

        ReadOnlySpan<int> visible = simulation.Vision.GetVisibleTargets(observer.Index);
        for (int i = 0; i < visible.Length; i++)
        {
            if (visible[i] == target.Index)
                return true;
        }

        return false;
    }

    private static bool TestTargetModesAimAtHitRegions()
    {
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX);

        int inventorySlot = simulation.AddInventoryItem(
            shooter,
            WeaponCatalog.AssaultRifle);
        if (inventorySlot < 0 ||
            !simulation.EquipWeapon(shooter, inventorySlot, UnitWeaponSlot.Primary))
        {
            return false;
        }

        int stateIndex = UnitWeaponStore.GetIndex(shooter.Index, UnitWeaponSlot.Primary);
        simulation.Weapons.CurrentTargetMode[stateIndex] = TargetMode.Head;
        simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target);
        float headZ = simulation.Weapons.AimPoint[stateIndex].Z;

        simulation.Weapons.CurrentTargetMode[stateIndex] = TargetMode.Torso;
        simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target);
        float torsoZ = simulation.Weapons.AimPoint[stateIndex].Z;

        simulation.Weapons.CurrentTargetMode[stateIndex] = TargetMode.Legs;
        simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target);
        float legsZ = simulation.Weapons.AimPoint[stateIndex].Z;

        return headZ > torsoZ &&
               torsoZ > legsZ &&
               simulation.Weapons.AimPoint[stateIndex] != Vector3.Zero;
    }

    private static bool TestTargetModesRankVerticalSegments()
    {
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitHitRegionProfile verticalProfile = new UnitHitRegionProfile(
            new UnitCircleHitRegion(new Vector3(0f, 0f, 0.20f), 0.25f, 0.18f,
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, maxHitPoints: 100f)),
            new UnitCircleHitRegion(new Vector3(0f, 0f, 0.60f), 0.25f, 0.18f,
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, maxHitPoints: 100f)),
            new UnitCircleHitRegion(new Vector3(0f, 0f, 1.00f), 0.25f, 0.18f,
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, maxHitPoints: 100f)),
            new UnitCircleHitRegion(new Vector3(0f, 0f, 1.40f), 0.25f, 0.18f,
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, maxHitPoints: 100f)),
            new UnitCircleHitRegion(new Vector3(0f, 0f, 1.80f), 0.25f, 0.18f,
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, maxHitPoints: 100f)));

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(15.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            hitProfile: verticalProfile);

        int inventorySlot = simulation.AddInventoryItem(
            shooter, WeaponCatalog.AssaultRifle);
        if (inventorySlot < 0 ||
            !simulation.EquipWeapon(shooter, inventorySlot, UnitWeaponSlot.Primary))
        {
            return false;
        }

        int stateIndex = UnitWeaponStore.GetIndex(
            shooter.Index, UnitWeaponSlot.Primary);

        simulation.Weapons.CurrentTargetMode[stateIndex] = TargetMode.Head;
        simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target);
        float upperZ = simulation.Weapons.AimPoint[stateIndex].Z;

        simulation.Weapons.CurrentTargetMode[stateIndex] = TargetMode.Torso;
        simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target);
        float middleZ = simulation.Weapons.AimPoint[stateIndex].Z;

        simulation.Weapons.CurrentTargetMode[stateIndex] = TargetMode.Legs;
        simulation.FireWeaponAt(shooter, UnitWeaponSlot.Primary, target);
        float lowerZ = simulation.Weapons.AimPoint[stateIndex].Z;

        return upperZ > middleZ &&
               middleZ > lowerZ &&
               simulation.Weapons.AimPoint[stateIndex] != Vector3.Zero;
    }

    private static bool TestDamageRequiresHitRegion()
    {
        UnitSimulation simulation = new UnitSimulation(2, 32);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10f, 10f, 0.1f));

        float healthBefore = simulation.Health.OverallHitPoints[target.Index];
        DamageSystem damageSystem = new DamageSystem();
        UnitHealthSystem healthSystem = new UnitHealthSystem();

        DamageEvent outside = new DamageEvent(
            default,
            target,
            UnitHealthPartId.Torso,
            new Vector3(10f, 10f, 4f),
            Vector3.UnitX,
            30f,
            1000f,
            1000f,
            100f,
            70f,
            DamageType.Ballistic,
            0f);

        ProjectileDamageResult result = damageSystem.ApplyProjectile(
            simulation.Units,
            simulation.Inventory,
            simulation.Health,
            healthSystem,
            in outside,
            simulation.HitRegions);

        return result.ProjectileStopped &&
               result.DamageApplied == 0f &&
               simulation.Health.OverallHitPoints[target.Index] == healthBefore;
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


    private static bool TestNamedAnatomyMarkers()
    {
        UnitAnatomyProfile profile = new(
            UnitAnatomyNode.Circle(
                "sensor-core",
                new Vector3(0f, 0f, 0.70f),
                0.35f,
                0.25f,
                new[]
                {
                    new HitRegionPartBinding(
                        UnitHealthPartId.None,
                        kind: UnitHealthPartKind.Organ,
                        maxHitPoints: 18f,
                        id: "sensor-core.tissue")
                },
                new UnitAnatomyMarker(
                    "eye",
                    UnitAnatomyMarkerRole.Eye,
                    new Vector3(0f, 0f, 0.10f)),
                new UnitAnatomyMarker(
                    "aim",
                    UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim,
                    new Vector3(0.12f, 0f, 0.04f)),
                new UnitAnatomyMarker(
                    "muzzle",
                    UnitAnatomyMarkerRole.Muzzle,
                    new Vector3(0.30f, 0f, 0.15f))));

        UnitSimulation simulation = new UnitSimulation(4, 32);
        UnitId unit = simulation.Spawn(
            UnitType.SegmentedMonster,
            new Vector3(10f, 12f, 0.1f),
            hitProfile: profile);

        Vector3 center = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, 0, simulation.HitRegions).Center;

        bool eyeFound = UnitHitSystem.TryGetMarkerPoint(
            simulation.Units, unit.Index, UnitAnatomyMarkerRole.Eye,
            out Vector3 eye, simulation.HitRegions);
        bool muzzleFound = UnitHitSystem.TryGetMarkerPoint(
            simulation.Units, unit.Index, UnitAnatomyMarkerRole.Muzzle,
            out Vector3 muzzle, simulation.HitRegions);
        Vector3 aim = UnitHitSystem.GetRegionPoint(
            simulation.Units, unit.Index, 0, center + Vector3.UnitX * 10f,
            simulation.HitRegions, UnitAnatomyMarkerRole.Aim);

        return eyeFound && muzzleFound &&
               Vector3.Distance(eye, center + new Vector3(0f, 0f, 0.10f)) < 0.001f &&
               Vector3.Distance(muzzle, center + new Vector3(0.30f, 0f, 0.15f)) < 0.001f &&
               Vector3.Distance(aim, center + new Vector3(0.12f, 0f, 0.04f)) < 0.001f;
    }

    private static bool TestGenericAnatomyTissueHealth()
    {
        UnitAnatomyNode firstSegment = UnitAnatomyNode.Circle(
            "track-segment-0",
            new Vector3(0f, 0f, 0.25f),
            0.35f,
            0.20f,
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody,
                    kind: UnitHealthPartKind.BodyPart,
                    maxHitPoints: 30f,
                    id: "track-tissue-0")
            },
            new UnitAnatomyMarker(
                "aim",
                UnitAnatomyMarkerRole.Target,
                new Vector3(0f, 0f, 0.02f)));

        UnitAnatomyProfile initial = new(firstSegment);
        UnitSimulation simulation = new UnitSimulation(4, 32);
        UnitId unit = simulation.Spawn(
            UnitType.SegmentedMonster,
            new Vector3(10f, 12f, 0.1f),
            hitProfile: initial);

        if (!simulation.Health.TryFindRegionPart(
                unit.Index, 0, 0, out int firstHealthIndex) ||
            !simulation.ApplyAnatomyDamage(unit, 0, 0, 10f).FinalDamage.Equals(10f) ||
            MathF.Abs(simulation.Health.HitPoints[firstHealthIndex] - 20f) > 0.001f)
        {
            return false;
        }

        UnitAnatomyNode secondSegment = UnitAnatomyNode.Circle(
            "track-segment-1",
            "track-segment-0",
            new Vector3(0f, 0f, 0.85f),
            0.32f,
            0.18f,
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody,
                    kind: UnitHealthPartKind.BodyPart,
                    maxHitPoints: 40f,
                    id: "track-tissue-1",
                    parentId: "track-tissue-0")
            },
            new UnitAnatomyMarker(
                "aim",
                UnitAnatomyMarkerRole.Target));

        if (!simulation.AddAnatomyNode(unit, secondSegment) ||
            !simulation.Health.TryFindRegionPart(
                unit.Index, 0, 0, out int preservedHealthIndex) ||
            !simulation.Health.TryFindRegionPart(
                unit.Index, 1, 0, out int secondHealthIndex))
        {
            return false;
        }

        UnitHitRegionGeometry firstGeometry = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, 0, simulation.HitRegions);
        UnitHitRegionGeometry secondGeometry = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, 1, simulation.HitRegions);

        DamageEvent segmentHit = new DamageEvent(
            default,
            unit,
            UnitHealthPartId.WholeBody,
            secondGeometry.Center,
            Vector3.UnitX,
            10f,
            1000f,
            1000f,
            100f,
            70f,
            DamageType.Ballistic,
            0f,
            regionIndex: 1,
            bindingIndex: 0);

        ProjectileDamageResult hitResult = new DamageSystem().ApplyProjectile(
            simulation.Units,
            simulation.Inventory,
            simulation.Health,
            new UnitHealthSystem(),
            in segmentHit,
            simulation.HitRegions);

        return MathF.Abs(simulation.Health.HitPoints[preservedHealthIndex] - 20f) < 0.001f &&
               MathF.Abs(simulation.Health.HitPoints[secondHealthIndex] - 30f) < 0.001f &&
               hitResult.DamageApplied > 0f &&
               simulation.Health.Parent[secondHealthIndex] == preservedHealthIndex &&
               MathF.Abs(secondGeometry.Center.X - firstGeometry.Center.X) < 0.001f &&
               MathF.Abs(secondGeometry.Center.Y - firstGeometry.Center.Y) < 0.001f &&
               MathF.Abs(secondGeometry.Center.Z - firstGeometry.Center.Z - 0.85f) < 0.001f;
    }

    private static bool TestStartingArmorMitigatesRifleHits()
    {
        UnitSimulation simulation = new UnitSimulation(2, 32);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitX);

        int vestSlot = simulation.Inventory.GetBodyEquipment(
            target.Index,
            1,
            ArmorLayer.Armor);

        if (vestSlot < 0 ||
            simulation.Inventory.GetItem(target.Index, vestSlot) != ApparelCatalog.CombatVest)
            return false;

        int durabilityIndex =
            target.Index * UnitInventoryStore.MaxInventorySlots + vestSlot;
        float armorDurabilityBefore = simulation.Inventory.Durability[durabilityIndex];
        float healthBefore = simulation.Health.OverallHitPoints[target.Index];

        DamageSystem damageSystem = new DamageSystem();
        UnitHealthSystem healthSystem = new UnitHealthSystem();
        const float initialEnergy = 1729.8f;

        int torsoRegion = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy.FindRegion("torso");
        if (torsoRegion < 0)
            return false;

        for (int i = 0; i < 5; i++)
        {
            Vector3 torsoCenter = UnitHitSystem.GetRegionGeometry(
                simulation.Units,
                target.Index,
                torsoRegion,
                simulation.HitRegions,
                simulation.Bodies).Center;

            DamageEvent hit = new DamageEvent(
                default,
                target,
                UnitHealthPartId.Torso,
                torsoCenter,
                Vector3.UnitX,
                34f,
                initialEnergy,
                initialEnergy,
                110f,
                70f,
                DamageType.Ballistic,
                0f,
                regionIndex: torsoRegion);

            ProjectileDamageResult result = damageSystem.ApplyProjectile(
                simulation.Units,
                simulation.Inventory,
                simulation.Health,
                healthSystem,
                in hit,
                simulation.HitRegions);

            if (result.DamageApplied <= 0f || result.DamageApplied >= 10f)
                return false;
        }

        float healthAfterRifleHits =
            simulation.Health.OverallHitPoints[target.Index];
        float armorDurabilityAfter =
            simulation.Inventory.Durability[durabilityIndex];

        if (healthAfterRifleHits <= 0f ||
            healthAfterRifleHits >= healthBefore ||
            armorDurabilityAfter >= armorDurabilityBefore ||
            armorDurabilityAfter <= 0f)
        {
            return false;
        }

        UnitDamageResult genericHit = simulation.ApplyDamage(
            target,
            UnitHealthPartId.Torso,
            34f);

        return genericHit.FinalDamage > 0f &&
               genericHit.FinalDamage < 20f &&
               simulation.Health.OverallHitPoints[target.Index] > 0f;
    }

    private static bool TestHighTechWeaponProfiles()
    {
        RangedWeaponConfig repeater = WeaponCatalog.PulseRepeater;
        RangedWeaponConfig laser = WeaponCatalog.LaserCarbine;
        RangedWeaponConfig railgun = WeaponCatalog.Railgun;

        return repeater.Technology == WeaponTechnology.KineticRepeater &&
               repeater.DefaultFireMode == FireMode.Auto &&
               repeater.FireRate < laser.FireRate &&
               repeater.MagazineSize > laser.MagazineSize &&
               laser.Technology == WeaponTechnology.Laser &&
               laser.DefaultFireMode == FireMode.Auto &&
               laser.DefaultAmmunition?.IsLaser == true &&
               laser.DefaultAmmunition?.DamageType == DamageType.Heat &&
               railgun.Technology == WeaponTechnology.Railgun &&
               railgun.DefaultFireMode == FireMode.Single &&
               railgun.FireRate > 1f &&
               railgun.MagazineSize < repeater.MagazineSize &&
               railgun.DefaultAmmunition!.Penetration > laser.DefaultAmmunition!.Penetration &&
               railgun.Recoil > repeater.Recoil &&
               repeater.WorldColorG != laser.WorldColorG &&
               laser.WorldColorB > railgun.WorldColorB;
    }

    private static bool TestExoskeletonMovementBonus()
    {
        UnitSimulation simulation = new UnitSimulation(2, 32);
        UnitId unit = simulation.Spawn(UnitType.Colonist, Vector3.Zero, Vector3.UnitX, Vector3.UnitX);
        float baseSpeed = UnitCatalog.Get(UnitType.Colonist).MoveSpeed;
        float expectedSpeed = baseSpeed * ApparelCatalog.PoweredExoskeleton.MovementSpeedMultiplier;
        int frameSlot = simulation.Inventory.GetBodyEquipment(unit.Index, 6, ArmorLayer.Base);

        if (frameSlot < 0 ||
            simulation.Inventory.GetItem(unit.Index, frameSlot) != ApparelCatalog.PoweredExoskeleton ||
            MathF.Abs(simulation.Units.MoveSpeed[unit.Index] - expectedSpeed) > 0.001f)
            return false;

        simulation.Inventory.Durability[
            unit.Index * UnitInventoryStore.MaxInventorySlots + frameSlot] = 0f;
        simulation.Update(new WorldMap(1, 1, 32), 1f / 60f);
        return MathF.Abs(simulation.Units.MoveSpeed[unit.Index] - baseSpeed) < 0.001f;
    }

    private static bool TestEquipmentVisualMetadata()
    {
        if (WeaponCatalog.AssaultRifle.WorldRenderLength <= WeaponCatalog.Pistol.WorldRenderLength ||
            ApparelCatalog.WorkShirt.WorldRenderPadding <= 0f ||
            ApparelCatalog.WorkTrousers.WorldRenderPadding <= 0f ||
            ApparelCatalog.CombatVest.WorldRenderPadding <= 0f ||
            ApparelCatalog.CombatHelmet.WorldRenderPadding <= 0f ||
            ApparelCatalog.PoweredExoskeleton.WorldRenderPadding <= 0f)
            return false;

        UnitSimulation simulation = new UnitSimulation(2, 32);
        UnitId unit = simulation.Spawn(
            UnitType.Colonist,
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitX);

        int shirt = simulation.Inventory.GetBodyEquipment(
            unit.Index, 1, ArmorLayer.Clothing);
        int trousers = simulation.Inventory.GetBodyEquipment(
            unit.Index, 6, ArmorLayer.Clothing);
        int vest = simulation.Inventory.GetBodyEquipment(
            unit.Index, 1, ArmorLayer.Armor);
        int helmet = simulation.Inventory.GetBodyEquipment(
            unit.Index, 0, ArmorLayer.Armor);
        int exoskeleton = simulation.Inventory.GetBodyEquipment(
            unit.Index, 6, ArmorLayer.Base);

        if (shirt < 0 || trousers < 0 || vest < 0 || helmet < 0 || exoskeleton < 0)
            return false;

        int itemStart = unit.Index * UnitInventoryStore.MaxInventorySlots;

        return simulation.Inventory.GetItem(unit.Index, shirt) == ApparelCatalog.WorkShirt &&
               simulation.Inventory.GetItem(unit.Index, trousers) == ApparelCatalog.WorkTrousers &&
               simulation.Inventory.GetItem(unit.Index, vest) == ApparelCatalog.CombatVest &&
               simulation.Inventory.GetItem(unit.Index, helmet) == ApparelCatalog.CombatHelmet &&
               simulation.Inventory.GetItem(unit.Index, exoskeleton) == ApparelCatalog.PoweredExoskeleton &&
               simulation.Inventory.Durability[itemStart + exoskeleton] > 0f &&
               simulation.Inventory.Durability[itemStart + shirt] > 0f &&
               simulation.Inventory.Durability[itemStart + trousers] > 0f &&
               simulation.Inventory.Durability[itemStart + vest] > 0f &&
               simulation.Inventory.Durability[itemStart + helmet] > 0f;
    }

        private static bool TestAnatomyProfiles()
    {
        UnitBodyStore definitions = new UnitBodyStore(4);
        BodyHandle colonistBody = definitions.CreateBody(UnitBodyType.Colonist);

        if (colonistBody.Count != 13 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Head) != 1 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Torso) != 3 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.UpperArm) != 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Forearm) != 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Thigh) != 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Shin) != 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Neck) != 1 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Hand) != 0 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Foot) != 0 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Elbow) != 0 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Knee) != 0)
        {
            return false;
        }

        for (int part = colonistBody.Start;
             part < colonistBody.Start + colonistBody.Count;
             part++)
        {
            int parent = definitions.Parent[part];
            if (parent < -1 || parent >= part)
                return false;

            BodyPartRole role = definitions.Role[part];
            if (role is BodyPartRole.UpperArm or BodyPartRole.Forearm or
                BodyPartRole.Thigh or BodyPartRole.Shin)
            {
                Vector3 scale = definitions.Scale[part];
                if (definitions.Primitive[part] != BodyPrimitive.Circle ||
                    definitions.Pivot[part] != BodyPartPivot.Start ||
                    scale.X > 0.051f ||
                    scale.Y > 0.026f)
                {
                    return false;
                }
            }
        }

        definitions.SetInitialWorldPosition(
            colonistBody,
            Vector3.Zero,
            0f,
            MathF.PI * 0.5f);

        int definitionHead = FindBodyRole(
            definitions,
            colonistBody,
            BodyPartRole.Head);

        Vector3 headPosition = definitions.WorldPosition[definitionHead];
        Vector3[] initialPositions = definitions.WorldPosition;
        int bodyStartForPose = colonistBody.Start;
        Vector3 torsoPosition = initialPositions[bodyStartForPose];
        Vector3 leftArmPosition = initialPositions[bodyStartForPose + 3];
        Vector3 leftForearmPosition = initialPositions[bodyStartForPose + 4];
        Vector3 rightArmPosition = initialPositions[bodyStartForPose + 5];
        Vector3 rightForearmPosition = initialPositions[bodyStartForPose + 6];
        Vector3 leftLegPosition = initialPositions[bodyStartForPose + 7];
        Vector3 leftShinPosition = initialPositions[bodyStartForPose + 8];
        Vector3 rightLegPosition = initialPositions[bodyStartForPose + 9];
        Vector3 rightShinPosition = initialPositions[bodyStartForPose + 10];

        // The middle torso is the root; left/right limb chains mirror over Y=0.
        if (MathF.Abs(torsoPosition.X) > 0.001f ||
            MathF.Abs(torsoPosition.Y) > 0.001f ||
            MathF.Abs(headPosition.X - 0.155f) > 0.001f ||
            MathF.Abs(headPosition.Y) > 0.001f ||
            MathF.Abs(rightArmPosition.X - leftArmPosition.X) > 0.001f ||
            MathF.Abs(rightArmPosition.Y + leftArmPosition.Y) > 0.001f ||
            MathF.Abs(rightForearmPosition.X - leftForearmPosition.X) > 0.001f ||
            MathF.Abs(rightForearmPosition.Y + leftForearmPosition.Y) > 0.001f ||
            MathF.Abs(rightLegPosition.X - leftLegPosition.X) > 0.001f ||
            MathF.Abs(rightLegPosition.Y + leftLegPosition.Y) > 0.001f ||
            MathF.Abs(rightShinPosition.X - leftShinPosition.X) > 0.001f ||
            MathF.Abs(rightShinPosition.Y + leftShinPosition.Y) > 0.001f)
        {
            return false;
        }

        UnitSimulation simulation = new UnitSimulation(8, 128);
        UnitId colonist = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10f, 10f, 0f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitId greenbob = simulation.Spawn(
            UnitType.Greenbob,
            new Vector3(12f, 10f, 0f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitId segmented = simulation.Spawn(
            UnitType.SegmentedMonster,
            new Vector3(14f, 10f, 0f),
            Vector3.UnitX,
            Vector3.UnitX);

        BodyPartDefinition[] customParts =
            BodyDefinitionExamples.CreateMultiLimbCreature();

        UnitId custom = simulation.SpawnCustom(
            UnitType.Colonist,
            new Vector3(16f, 10f, 0f),
            customParts);

        if (simulation.Units.BodyCount[colonist.Index] != 13 ||
            simulation.Units.BodyCount[greenbob.Index] != 10 ||
            simulation.Units.BodyCount[segmented.Index] != 10 ||
            simulation.Units.BodyCount[custom.Index] != customParts.Length)
        {
            return false;
        }

        simulation.Units.Velocity[colonist.Index] =
            new Vector3(1f, 0f, 0f);

        UnitBodySystem bodySystem = new UnitBodySystem();
        bodySystem.Update(
            simulation.Units,
            simulation.Bodies,
            0.1f);

        int start = simulation.Units.BodyStart[colonist.Index];
        short count = simulation.Units.BodyCount[colonist.Index];
        int leftThigh = FindBodyRole(
            simulation.Bodies,
            new BodyHandle(start, count),
            BodyPartRole.Thigh,
            BodyPartMotion.WalkCycle);
        int rightThigh = FindBodyRole(
            simulation.Bodies,
            new BodyHandle(start, count),
            BodyPartRole.Thigh,
            BodyPartMotion.WalkCycleOpposite);

        float swing = MathF.Sin(1.1f) * 0.32f;
        bool gaitCorrect =
            leftThigh >= 0 &&
            rightThigh >= 0 &&
            MathF.Abs(
                simulation.Bodies.WorldRotation[leftThigh] -
                (MathF.PI - 0.22f + swing)) < 0.001f &&
            MathF.Abs(
                simulation.Bodies.WorldRotation[rightThigh] -
                (-MathF.PI + 0.22f - swing)) < 0.001f;

        if (!gaitCorrect)
        {
            return false;
        }

        BodyHandle colonistHandle = new BodyHandle(start, count);
        BodyHandle greenbobHandle = new BodyHandle(
            simulation.Units.BodyStart[greenbob.Index],
            simulation.Units.BodyCount[greenbob.Index]);

        if (!BodyLinksConnected(simulation.Bodies, colonistHandle) ||
            !BodyLinksConnected(simulation.Bodies, greenbobHandle))
        {
            return false;
        }

        // Independent head aim must rotate the head, not move it around the pawn.
        simulation.SetHeadNormal(colonist, Vector3.UnitY);
        bodySystem.Update(simulation.Units, simulation.Bodies, 0f);
        int head = FindBodyRole(
            simulation.Bodies,
            new BodyHandle(start, count),
            BodyPartRole.Head);
        Vector3 aimedHeadPosition = simulation.Bodies.WorldPosition[head];
        if (MathF.Abs(aimedHeadPosition.X - 10.155f) > 0.002f ||
            MathF.Abs(aimedHeadPosition.Y - 10f) > 0.002f ||
            MathF.Abs(simulation.Bodies.WorldRotation[head] - MathF.PI * 0.5f) > 0.002f)
        {
            return false;
        }

        int walkingArm = FindBodyRole(
            simulation.Bodies,
            new BodyHandle(start, count),
            BodyPartRole.UpperArm,
            BodyPartMotion.WalkCycle);

        if (walkingArm < 0)
        {
            return false;
        }

        float standingArmRotation =
            simulation.Bodies.WorldRotation[walkingArm];

        simulation.SetPosture(colonist, UnitPosture.Crouching);
        bodySystem.Update(simulation.Units, simulation.Bodies, 0f);
        float crouchingArmRotation =
            simulation.Bodies.WorldRotation[walkingArm];

        if (MathF.Abs(crouchingArmRotation) >= MathF.Abs(standingArmRotation) - 0.12f ||
            !BodyLinksConnected(simulation.Bodies, colonistHandle))
        {
            return false;
        }

        simulation.SetPosture(colonist, UnitPosture.Lying);
        bodySystem.Update(simulation.Units, simulation.Bodies, 0f);
        float lyingArmRotation =
            simulation.Bodies.WorldRotation[walkingArm];

        if (MathF.Abs(lyingArmRotation - standingArmRotation) > 0.03f ||
            !BodyLinksConnected(simulation.Bodies, colonistHandle))
        {
            return false;
        }

        // A moving body follows its travel vector while head facing remains independent.
        simulation.SetHeadNormal(colonist, Vector3.UnitX);
        simulation.Units.Velocity[colonist.Index] =
            new Vector3(0f, 1f, 0f);
        simulation.SetPosture(colonist, UnitPosture.Standing);
        bodySystem.Update(simulation.Units, simulation.Bodies, 0f);

        Vector3 bodyNormal = simulation.Units.BodyNormal[colonist.Index];
        Vector3 movedHead = simulation.Bodies.WorldPosition[head];
        return MathF.Abs(bodyNormal.X) < 0.001f &&
               MathF.Abs(bodyNormal.Y - 1f) < 0.001f &&
               MathF.Abs(movedHead.X - 10f) < 0.002f &&
               MathF.Abs(movedHead.Y - 10.155f) < 0.002f &&
               MathF.Abs(simulation.Bodies.WorldRotation[head]) < 0.002f;
    }

    private static bool BodyLinksConnected(
        UnitBodyStore bodies,
        BodyHandle body)
    {
        int end = body.Start + body.Count;

        for (int part = body.Start; part < end; part++)
        {
            int parent = bodies.Parent[part];
            if (parent < body.Start)
                continue;

            BodyPartRole role = bodies.Role[part];
            BodyPartRole parentRole = bodies.Role[parent];
            Vector3 position = bodies.WorldPosition[part];
            Vector3 parentPosition = bodies.WorldPosition[parent];
            float rotation = bodies.WorldRotation[parent];

            if (role is BodyPartRole.Elbow or BodyPartRole.Knee &&
                (parentRole is BodyPartRole.UpperArm or BodyPartRole.Thigh))
            {
                Vector3 endpoint = parentPosition + Axis(
                    rotation,
                    MathF.Abs(bodies.Scale[parent].X));

                if (HorizontalDistance(position, endpoint) > 0.002f)
                    return false;
            }
            else if (role is BodyPartRole.Forearm or BodyPartRole.Shin)
            {
                Vector3 proximal = position - Axis(
                    bodies.WorldRotation[part],
                    MathF.Abs(bodies.Scale[part].X));
                Vector3 parentEndpoint = parentPosition + Axis(
                    bodies.WorldRotation[parent],
                    MathF.Abs(bodies.Scale[parent].X));

                if (HorizontalDistance(proximal, parentEndpoint) > 0.002f)
                    return false;
            }
            else if (role == BodyPartRole.Hand &&
                     (parentRole == BodyPartRole.Forearm ||
                      parentRole == BodyPartRole.UpperArm))
            {
                Vector3 endpoint = parentPosition + Axis(
                    rotation,
                    MathF.Abs(bodies.Scale[parent].X));

                if (HorizontalDistance(position, endpoint) > 0.002f)
                    return false;
            }
            else if (role == BodyPartRole.Foot &&
                     parentRole == BodyPartRole.Thigh)
            {
                Vector3 endpoint = parentPosition + Axis(
                    rotation,
                    MathF.Abs(bodies.Scale[parent].X));

                if (HorizontalDistance(position, endpoint) > 0.002f)
                    return false;
            }
            else if (role == BodyPartRole.Foot &&
                     parentRole == BodyPartRole.Shin)
            {
                Vector3 ankle = parentPosition + Axis(
                    rotation,
                    MathF.Abs(bodies.Scale[parent].X));
                Vector3 footStart = position - Axis(
                    bodies.WorldRotation[part],
                    MathF.Abs(bodies.Scale[part].X));

                if (Vector3.Distance(footStart, ankle) > 0.002f)
                    return false;
            }
        }

        return true;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static Vector3 Axis(float angle, float distance) =>
        new Vector3(
            MathF.Cos(angle) * distance,
            MathF.Sin(angle) * distance,
            0f);

    private static int CountBodyRole(
        UnitBodyStore bodies,
        BodyHandle body,
        BodyPartRole role)
    {
        int result = 0;
        int end = body.Start + body.Count;

        for (int part = body.Start; part < end; part++)
        {
            if (bodies.Role[part] == role)
                result++;
        }

        return result;
    }

    private static int FindBodyRole(
        UnitBodyStore bodies,
        BodyHandle body,
        BodyPartRole role,
        BodyPartMotion motion = BodyPartMotion.StaticRelative)
    {
        int end = body.Start + body.Count;

        for (int part = body.Start; part < end; part++)
        {
            if (bodies.Role[part] == role &&
                bodies.Motion[part] == motion)
            {
                return part;
            }
        }

        return -1;
    }

}
