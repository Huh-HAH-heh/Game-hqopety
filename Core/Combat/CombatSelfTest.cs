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
        RunTest("Pawn stats: RimWorld skills and suppression resistance", TestPawnSkillsAndResistance, ref passed, ref failed);
        RunTest("Posture: prone silhouette and firing", TestPronePostureAndFiring, ref passed, ref failed);
        RunTest("Targeting: prone visibility and aim points follow the posed rig", TestProneTargetPointsFollowRig, ref passed, ref failed);
        RunTest("AI: exposed unit crawls prone toward firing range", TestProneCrawlApproach, ref passed, ref failed);
        RunTest("Accuracy: recoil and movement increase spread", TestAccuracy, ref passed, ref failed);
        RunTest("Navigation: A* routes around an impassable ridge", TestNavigationRoutesAroundWall, ref passed, ref failed);
        RunTest("Navigation: cached routes avoid previous corridors", TestNavigationRouteReuse, ref passed, ref failed);
        RunTest("AI: scouts toward the center when no target is visible", TestAiSearchAdvance, ref passed, ref failed);
        RunTest("Vision: candidate sampling bounds pair work", TestVisionBoundedCandidateSampling, ref passed, ref failed);
        RunTest("Vision: close elevated hostiles are spatially sampled", TestVisionCloseHostilesSpatiallySampled, ref passed, ref failed);
        RunTest("Vision: candidate work scales with living units", TestVisionWorkScalesWithLivingUnits, ref passed, ref failed);
        RunTest("Terrain: uniform voxel regions release dense buffers", TestTerrainRegionCompression, ref passed, ref failed);
        RunTest("Terrain: range cache avoids height-sized allocations", TestTerrainRangeCacheAllocations, ref passed, ref failed);
        RunTest("Terrain: solid height columns stay compact and materialize on edit", TestCompactSolidHeightColumns, ref passed, ref failed);
        RunTest("Water: dry maps do not allocate dense vertical storage", TestSparseWaterLayerStorage, ref passed, ref failed);
        RunTest("Vision: LOS respects 0.1 m terrain layers", TestVisionHeightUnits, ref passed, ref failed);
        RunTest("Hit regions: custom circles drive visibility, hits and armor", TestHitRegionProfilesDriveVisionCollisionAndArmor, ref passed, ref failed);
        RunTest("Anatomy: colonist nodes share stable tissue IDs", TestColonistUsesAnatomyNodes, ref passed, ref failed);
        RunTest("Anatomy: rig edits preserve tissue IDs and organ volumes", TestAnatomyRigEditsPreserveBindings, ref passed, ref failed);
        RunTest("Anatomy: rig editor can add dependent regions", TestAnatomyRigCanAppendDependency, ref passed, ref failed);
        RunTest("Pose: saved postures and walk-cycle motion are applied", TestSavedPosesAndWalkCycleApplied, ref passed, ref failed);
        RunTest("Body rig: 3D rotation projects consistently in all views", TestBodyPartProjectionMath, ref passed, ref failed);
        RunTest("Ballistics: per-segment hits follow the rendered rig", TestProjectileHitsAuthoredRigSegments, ref passed, ref failed);
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
            1.6f,
            Vector3.Zero);

        if (suppression.GetState(0) !=
            UnitSuppressionState.Suppressed)
        {
            return false;
        }

        suppression.Add(
            0,
            3.0f,
            Vector3.Zero);

        return suppression.GetState(0) ==
               UnitSuppressionState.Panicked &&
               suppression.GetAccuracyMultiplier(0) > 1f;
    }

    private static bool TestPawnSkillsAndResistance()
    {
        UnitSkillStore skills = new UnitSkillStore(2);
        skills.InitializeUnit(0, UnitType.Colonist, 1);
        skills.InitializeUnit(1, UnitType.Colonist, 2);

        if (UnitSkillStore.SkillCount != 12)
            return false;

        for (int i = 0; i < UnitSkillStore.SkillCount; i++)
        {
            UnitSkill skill = (UnitSkill)i;
            if (skills.GetLevel(0, skill) > UnitSkillStore.MaxLevel ||
                skills.GetLevel(1, skill) > UnitSkillStore.MaxLevel)
                return false;
        }

        skills.SetSkillLevel(0, UnitSkill.Shooting, 2);
        skills.SetSkillLevel(1, UnitSkill.Shooting, 18);
        if (skills.GetShootingSpreadMultiplier(1) >= skills.GetShootingSpreadMultiplier(0))
            return false;

        skills.SetSkillLevel(1, UnitSkill.Medical, 0);
        skills.SetPassion(1, UnitSkill.Medical, UnitSkillPassion.Major);
        skills.GainExperience(1, UnitSkill.Medical, 600f);
        if (skills.GetLevel(1, UnitSkill.Medical) != 1)
            return false;

        skills.SetSuppressionResistance(0, 0f);
        skills.SetSuppressionResistance(1, 0.8f);
        UnitSuppressionStore suppression = new UnitSuppressionStore(2);
        suppression.BindSkillStore(skills);
        suppression.InitializeUnit(0);
        suppression.InitializeUnit(1);
        suppression.Add(0, 3f, Vector3.Zero);
        suppression.Add(1, 3f, Vector3.Zero);

        return suppression.Value[0] > suppression.Value[1] &&
            skills.GetSuppressionResistance(1) > skills.GetSuppressionResistance(0);
    }

    private static bool TestPronePostureAndFiring()
    {
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX);
        int inventorySlot = simulation.AddInventoryItem(shooter, WeaponCatalog.AssaultRifle);
        if (inventorySlot < 0 ||
            !simulation.EquipWeapon(shooter, inventorySlot, UnitWeaponSlot.Primary))
            return false;

        int bodyStart = simulation.Units.BodyStart[shooter.Index];
        int bodyCount = simulation.Units.BodyCount[shooter.Index];
        int headPart = -1;
        int torsoPart = -1;
        float standingMaxZ = float.NegativeInfinity;
        for (int i = 0; i < bodyCount; i++)
        {
            int partIndex = bodyStart + i;
            string? id = simulation.Bodies.PartId[partIndex];
            if (id == "body.upper") headPart = partIndex;
            if (id == "body.middle") torsoPart = partIndex;
            standingMaxZ = MathF.Max(standingMaxZ, simulation.Bodies.WorldPosition[partIndex].Z);
        }
        if (headPart < 0 || torsoPart < 0)
            return false;

        float standingHeadZ = simulation.Bodies.WorldPosition[headPart].Z;
        float standingTorsoZ = simulation.Bodies.WorldPosition[torsoPart].Z;
        float standingMinX = float.PositiveInfinity;
        float standingMaxX = float.NegativeInfinity;
        for (int i = 0; i < bodyCount; i++)
        {
            float x = simulation.Bodies.WorldPosition[bodyStart + i].X;
            standingMinX = MathF.Min(standingMinX, x);
            standingMaxX = MathF.Max(standingMaxX, x);
        }
        float standingSpanX = standingMaxX - standingMinX;
        simulation.SetPosture(shooter, UnitPosture.Lying);
        WorldMap map = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 16);
        simulation.Update(map, 0.016f);

        float proneMaxZ = float.NegativeInfinity;
        float proneMinX = float.PositiveInfinity;
        float proneMaxX = float.NegativeInfinity;
        for (int i = 0; i < bodyCount; i++)
        {
            Vector3 partPosition = simulation.Bodies.WorldPosition[bodyStart + i];
            proneMaxZ = MathF.Max(proneMaxZ, partPosition.Z);
            proneMinX = MathF.Min(proneMinX, partPosition.X);
            proneMaxX = MathF.Max(proneMaxX, partPosition.X);
        }
        float proneSpanX = proneMaxX - proneMinX;

        int weaponIndex = UnitWeaponStore.GetIndex(shooter.Index, UnitWeaponSlot.Primary);
        simulation.Weapons.Ammo[weaponIndex] = 1;
        bool fired = simulation.FireWeapon(shooter, UnitWeaponSlot.Primary, Vector3.UnitX);

        return simulation.Units.Posture[shooter.Index] == UnitPosture.Lying &&
            simulation.Bodies.WorldPosition[headPart].Z < standingHeadZ * 0.45f &&
            simulation.Bodies.WorldPosition[torsoPart].Z < standingTorsoZ * 0.45f &&
            proneMaxZ < standingMaxZ * 0.5f &&
            proneSpanX > standingSpanX + 0.20f &&
            fired &&
            simulation.Projectiles.ActiveCount == 1;
    }

    private static bool TestProneTargetPointsFollowRig()
    {
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId observer = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(16.5f, 10.5f, 0.1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);

        if (simulation.HitRegions.GetProfile(target.Index) is not UnitAnatomyProfile anatomy)
            return false;

        simulation.SetPosture(target, UnitPosture.Lying);
        simulation.Units.Velocity[target.Index] = new Vector3(0.6f, 0f, 0f);
        new UnitBodySystem().Update(simulation.Units, simulation.Bodies, 0.016f);
        if (simulation.Units.Posture[target.Index] != UnitPosture.Lying)
            return false;

        WorldMap map = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 32);
        simulation.Vision.Update(
            simulation.Units, map, 0.11f, simulation.Health, simulation.Bodies);

        Vector3 eyeMarker = UnitHitSystem.GetEyePosition(
            simulation.Units, observer.Index, simulation.Bodies, simulation.HitRegions);
        if (!UnitHitSystem.TryGetMarkerPoint(
                simulation.Units, observer.Index, UnitAnatomyMarkerRole.Eye,
                out Vector3 actualEyeMarker, simulation.HitRegions, simulation.Bodies) ||
            Vector3.DistanceSquared(eyeMarker, actualEyeMarker) > 0.000001f)
            return false;

        int visiblePointCount = UnitHitSystem.GetVisibilityPointCount(
            simulation.Units, target.Index, simulation.HitRegions);
        if (visiblePointCount < 3)
            return false;

        Vector3 source = simulation.Units.Position[observer.Index];
        float proneHeightLimit = simulation.Units.Position[target.Index].Z +
            simulation.Units.Height[target.Index] * 0.45f;

        for (int i = 0; i < visiblePointCount; i++)
        {
            int regionIndex = UnitHitSystem.GetVisibilityRegionIndex(
                simulation.Units, target.Index, i, simulation.HitRegions);
            UnitHitRegionGeometry geometry = UnitHitSystem.GetRegionGeometry(
                simulation.Units, target.Index, regionIndex,
                simulation.HitRegions, simulation.Bodies);
            Vector3 aimPoint = UnitHitSystem.GetRegionPoint(
                simulation.Units, target.Index, regionIndex, source,
                simulation.HitRegions, UnitAnatomyMarkerRole.Aim, simulation.Bodies);

            // Target points must land on the live posed primitive, not a synthetic
            // standing-height point above the prone silhouette.
            if (!UnitHitSystem.IsPointInsideRegion(
                    simulation.Units, target.Index, regionIndex, aimPoint,
                    tolerance: 0.003f, hitRegions: simulation.HitRegions, bodies: simulation.Bodies) ||
                aimPoint.Z > proneHeightLimit)
            {
                return false;
            }
        }

        // Direct AI combat checks do not always pass the body store explicitly.
        // VisionSystem must retain the live store and return a point on the prone rig.
        VisionCheck check = simulation.Vision.Evaluate(
            simulation.Units, map, observer.Index, target.Index);
        if (!check.IsVisible || check.VisibleTargetPoint.Z > proneHeightLimit)
            return false;

        if ((uint)check.VisiblePoint >= (uint)visiblePointCount)
            return false;

        int visibleRegion = UnitHitSystem.GetVisibilityRegionIndex(
            simulation.Units, target.Index, check.VisiblePoint, simulation.HitRegions);
        return check.VisibleTargetPoint.Z <= proneHeightLimit &&
            UnitHitSystem.IsPointInsideRegion(
                simulation.Units, target.Index, visibleRegion, check.VisibleTargetPoint,
                tolerance: 0.003f, hitRegions: simulation.HitRegions, bodies: simulation.Bodies);
    }

    private static bool TestProneCrawlApproach()
    {
        WorldMap map = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 16);
        for (int y = 0; y < map.TileHeight; y++)
        {
            for (int x = 0; x < map.TileWidth; x++)
                map.SetSolidHeight(x, y, 10, 1);
        }

        UnitSimulation simulation = new UnitSimulation(8, 64);
        _ = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(0.5f, 0.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(35.5f, 24.5f, 1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);
        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 24.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        simulation.Units.ViewRange[target.Index] = 20f;
        simulation.Units.FieldOfView[target.Index] = 360f;
        simulation.Units.ViewRange[shooter.Index] = 40f;
        simulation.Units.FieldOfView[shooter.Index] = 360f;
        simulation.AI.Enabled = true;
        simulation.AI.UseBehaviacDecisionLayer = true;
        simulation.VisionEnabled = true;

        int inventorySlot = simulation.AddInventoryItem(shooter, WeaponCatalog.AssaultRifle);
        if (inventorySlot < 0 ||
            !simulation.EquipWeapon(shooter, inventorySlot, UnitWeaponSlot.Primary))
            return false;

        float startX = simulation.Units.Position[shooter.Index].X;
        float initialDistance = Vector3.Distance(
            simulation.Units.Position[shooter.Index],
            simulation.Units.Position[target.Index]);

        // Seed perception before measuring movement so this test exercises the
        // tree's live prone decision rather than initial candidate warm-up.
        for (int i = 0; i < 3; i++)
            simulation.Vision.Update(
                simulation.Units, map, 0.11f, simulation.Health, simulation.Bodies);

        ReadOnlySpan<int> visible = simulation.Vision.GetVisibleTargets(shooter.Index);
        bool targetVisible = false;
        for (int i = 0; i < visible.Length; i++)
            targetVisible |= visible[i] == target.Index;
        if (!targetVisible)
        {
            VisionCheck direct = simulation.Vision.Evaluate(
                simulation.Units, map, shooter.Index, target.Index, simulation.Bodies);
            Console.Error.WriteLine(
                $"[Prone crawl test] Target was not in perception list; direct LOS={direct.State}, " +
                $"distance={initialDistance:0.0}, visibleCount={visible.Length}.");
            return false;
        }

        for (int tick = 0; tick < 15; tick++)
            simulation.Update(map, 0.1f);

        Vector3 finalPosition = simulation.Units.Position[shooter.Index];
        float finalDistance = Vector3.Distance(finalPosition, simulation.Units.Position[target.Index]);
        bool passed = simulation.Units.Posture[shooter.Index] == UnitPosture.Lying &&
            finalPosition.X > startX + 1f &&
            finalDistance < initialDistance - 1f;
        if (!passed)
        {
            Console.Error.WriteLine(
                $"[Prone crawl test] posture={simulation.Units.Posture[shooter.Index]}, " +
                $"startX={startX:0.00}, final=({finalPosition.X:0.00},{finalPosition.Y:0.00}), " +
                $"dist={initialDistance:0.00}->{finalDistance:0.00}, " +
                $"movementTarget={simulation.Units.HasTarget[shooter.Index]}:" +
                $"{simulation.Units.Target[shooter.Index]}, " +
                $"aiTarget={simulation.AI.Store.HasTarget[shooter.Index]}, " +
                $"aiState={simulation.AI.Store.State[shooter.Index]}, " +
                $"behaviac={simulation.AI.UseBehaviacDecisionLayer}/{simulation.AI.BehaviacRuntimeReady}, " +
                $"engageDecisions={simulation.AI.LastBehaviacEngageDecisions}.");
        }

        return passed;
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

    private static bool TestCompactSolidHeightColumns()
    {
        WorldMap worldMap = new WorldMap(regionsX: 1, regionsY: 1);
        worldMap.GetOrCreateRegion(0, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int y = 0; y < TerrainRegion.TilesPerSide; y++)
        {
            for (int x = 0; x < TerrainRegion.TilesPerSide; x++)
                worldMap.SetSolidHeight(x, y, 100, 1);
        }
        long generationBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        if (generationBytes > 4096 ||
            worldMap.GetMaterialId(5, 5, 0) != 1 ||
            worldMap.GetMaterialId(5, 5, 99) != 1 ||
            worldMap.GetMaterialId(5, 5, 100) != 0 ||
            worldMap.GetLayer(50).GetMaterialId(5, 5) != 1 ||
            worldMap.GetSurfaceLayer(5, 5) != 99)
        {
            return false;
        }

        // Editing a single voxel must first materialize the compact column.
        worldMap.SetMaterialId(5, 5, 50, 2);
        if (worldMap.GetMaterialId(5, 5, 49) != 1 ||
            worldMap.GetMaterialId(5, 5, 50) != 2 ||
            worldMap.GetMaterialId(5, 5, 99) != 1 ||
            worldMap.GetMaterialId(5, 5, 100) != 0)
        {
            return false;
        }

        // Replacing that edited column with a simple height must clear stale upper voxels.
        worldMap.SetSolidHeight(5, 5, 30, 1);
        return worldMap.GetMaterialId(5, 5, 29) == 1 &&
               worldMap.GetMaterialId(5, 5, 30) == 0 &&
               worldMap.GetLayer(30).GetMaterialId(5, 5) == 0 &&
               worldMap.GetSurfaceLayer(5, 5) == 29;
    }

    private static bool TestSparseWaterLayerStorage()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        WaterLayer water = new WaterLayer(768, 768, 320);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        if (allocatedBytes > 16L * 1024 * 1024 ||
            water.AllocatedColumnCount != 0 ||
            water.GetAmount(5, 7, 3) != 0 ||
            water.GetTopLevel(5, 7) != -1)
        {
            return false;
        }

        water.SetAmount(5, 7, 3, 100);
        water.SetAmount(5, 7, 9, 50);
        if (water.AllocatedColumnCount != 1 ||
            water.GetAmount(5, 7, 3) != 100 ||
            water.GetTopLevel(5, 7) != 9)
        {
            return false;
        }

        water.SetAmount(5, 7, 9, 0);
        if (water.GetTopLevel(5, 7) != 3)
            return false;

        water.AddAmount(5, 7, 3, -100);
        if (water.AllocatedColumnCount != 0 || water.GetTopLevel(5, 7) != -1)
            return false;

        water.AddAmount(2, 4, 1, 12);
        water.SetAmount(8, 4, 2, 7);
        water.Clear();

        return water.AllocatedColumnCount == 0 &&
               water.GetAmount(2, 4, 1) == 0 &&
               water.GetAmount(8, 4, 2) == 0 &&
               water.GetTopLevel(2, 4) == -1 &&
               water.GetTopLevel(8, 4) == -1;
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
                new Vector3(100f, -80f, 9f), radius: 0.001f, halfHeight: 0.001f,
                new HitRegionPartBinding(UnitHealthPartId.Torso, maxHitPoints: 60f)));
        UnitSimulation simulation = new UnitSimulation(8, 64);
        UnitId unit = simulation.Spawn(UnitType.Colonist, new Vector3(10f, 10f, 0.1f), hitProfile: profile);
        if (simulation.HitRegions.GetProfile(unit.Index)?.RegionCount != 1)
            return false;

        Span<Vector3> points = stackalloc Vector3[1];
        int visibilityCount = UnitHitSystem.GetVisibilityPoints(
            simulation.Units, unit.Index, new Vector3(7f, 10f, 2f),
            points, simulation.Bodies, simulation.HitRegions);
        float expectedVisionHeight = simulation.Units.Position[unit.Index].Z +
            simulation.Units.Height[unit.Index] * 0.70f;
        if (visibilityCount != 1 || MathF.Abs(points[0].Z - expectedVisionHeight) > 0.001f)
            return false;

        UnitHitSystem hitSystem = new UnitHitSystem();
        float midHeight = simulation.Units.Position[unit.Index].Z +
            simulation.Units.Height[unit.Index] * 0.5f;
        if (!hitSystem.TryHitUnit(
                simulation.Units, unit.Index,
                new Vector3(8f, 10f, midHeight), new Vector3(12f, 10f, midHeight),
                0f, UnitHealthPartId.None, out UnitHitResult torsoHit,
                simulation.Bodies, simulation.HitRegions) ||
            torsoHit.Part != UnitHealthPartId.Torso || torsoHit.RegionIndex != 0)
            return false;

        float aboveCollider = simulation.Units.Position[unit.Index].Z +
            simulation.Units.Height[unit.Index] + 0.1f;
        if (hitSystem.TryHitUnit(
                simulation.Units, unit.Index,
                new Vector3(8f, 10f, aboveCollider), new Vector3(12f, 10f, aboveCollider),
                0f, UnitHealthPartId.None, out _,
                simulation.Bodies, simulation.HitRegions))
            return false;

        UnitDamageResult damage = simulation.ApplyDamage(unit, torsoHit.Part, 20f);
        return damage.ArmorAbsorbed > 0f && damage.FinalDamage < damage.RawDamage;
    }

    private static bool TestColonistUsesAnatomyNodes()
    {
        int stage = 1;
        bool Fail() { Console.Error.WriteLine($"[Anatomy shape test] Failed stage {stage}."); return false; }

        UnitAnatomyProfile profile = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy;
        if (profile.RegionCount < 13 || profile.NodeCount < 13 ||
            profile.BodyParts.Length != profile.NodeCount || !profile.HasSharedShapes ||
            profile.VolumeCount != 8)
            return Fail();

        stage = 2;
        string[] requiredRegions =
        {
            "body.lower", "body.middle", "body.torso.upper",
            "body.arm.left.upper", "body.arm.left.lower",
            "body.arm.right.upper", "body.arm.right.lower",
            "body.leg.left.upper", "body.leg.left.lower",
            "body.leg.right.upper", "body.leg.right.lower",
            "body.neck", "body.upper"
        };
        for (int i = 0; i < requiredRegions.Length; i++)
            if (profile.FindRegion(requiredRegions[i]) < 0)
                return Fail();

        int torso = profile.FindRegion("body.middle");
        int head = profile.FindRegion("body.upper");
        int leg = profile.FindRegion("body.leg.left.upper");
        if (!profile.TryFindPart(UnitHealthPartId.Torso, out int torsoRegion, out _) || torsoRegion != torso ||
            !profile.TryFindPart(UnitHealthPartId.LeftLeg, out int legRegion, out _) || legRegion != leg ||
            !profile.TryFindPart("heart.main", out int heartRegion, out _) || heartRegion != torso ||
            !profile.TryFindPart("brain.main", out int brainRegion, out _) || brainRegion != head ||
            !profile.TryFindPart(UnitHealthPartId.Neck, out int neckRegion, out _) ||
            neckRegion != profile.FindRegion("body.neck"))
            return Fail();

        stage = 3;
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId unit = simulation.Spawn(UnitType.Colonist, new Vector3(10f, 10f, 0.1f));
        int bodyStart = simulation.Units.BodyStart[unit.Index];
        int checkedShapes = 0;

        for (int i = 0; i < profile.RegionCount; i++)
        {
            UnitAnatomyNode node = profile.GetNode(i);
            if (!node.HasShape || profile.IsInternalNode(i))
                continue;

            UnitHitRegionGeometry geometry = UnitHitSystem.GetRegionGeometry(
                simulation.Units, unit.Index, i, simulation.HitRegions, simulation.Bodies);
            int bodyIndex = bodyStart + i;
            Vector3 expectedCenter = simulation.Bodies.WorldPosition[bodyIndex];
            Vector3 expectedScale = Vector3.Abs(simulation.Bodies.WorldScale[bodyIndex]);
            float expectedRadius = MathF.Max(expectedScale.X, expectedScale.Y);
            float expectedHalfHeight = expectedScale.Z;

            if (Vector3.Distance(geometry.Center, expectedCenter) > 0.001f ||
                MathF.Abs(geometry.Radius - expectedRadius) > 0.001f ||
                MathF.Abs(geometry.HalfHeight - expectedHalfHeight) > 0.001f)
            {
                Console.Error.WriteLine(
                    $"[Anatomy shape test] region={node.Id} " +
                    $"geometry={geometry.Center}/{geometry.Radius}/{geometry.HalfHeight}; " +
                    $"rig={expectedCenter}/{expectedRadius}/{expectedHalfHeight}.");
                return Fail();
            }

            checkedShapes++;
        }

        if (checkedShapes < 13)
            return Fail();

        stage = 4;
        int visibilityCount = UnitHitSystem.GetVisibilityPointCount(
            simulation.Units, unit.Index, simulation.HitRegions);
        if (visibilityCount != profile.VisibleRegionCount || visibilityCount < checkedShapes)
            return Fail();

        for (int i = 0; i < visibilityCount; i++)
        {
            int regionIndex = UnitHitSystem.GetVisibilityRegionIndex(
                simulation.Units, unit.Index, i, simulation.HitRegions);
            if (profile.IsInternalNode(regionIndex))
                return Fail();
        }

        return true;
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

    private static bool TestSavedPosesAndWalkCycleApplied()
    {
        IReadOnlyDictionary<string, UnitBodyPoseTransform>? sittingPose =
            UnitBodyPoseSettingsStore.GetTransforms(UnitType.Colonist, UnitPosture.Sitting);
        if (sittingPose == null ||
            !sittingPose.TryGetValue("body.arm.left.upper", out UnitBodyPoseTransform savedArmPose) ||
            savedArmPose.LocalEulerRotation.LengthSquared() <= 0.0001f)
        {
            Console.Error.WriteLine("[Pose test] Checked-in sitting pose is missing its authored arm transform.");
            return false;
        }

        WorldMap map = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 12);
        for (int y = 0; y < map.TileHeight; y++)
        {
            for (int x = 0; x < map.TileWidth; x++)
                map.SetSolidHeight(x, y, 10, 1);
        }

        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId unit = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX);

        int start = simulation.Units.BodyStart[unit.Index];
        int bodyCount = simulation.Units.BodyCount[unit.Index];
        int armPart = -1;
        int movingPart = -1;
        for (int i = 0; i < bodyCount; i++)
        {
            string? id = simulation.Bodies.PartId[start + i];
            if (id == "body.arm.left.upper")
                armPart = start + i;
            if (id == "body.leg.left.upper")
                movingPart = start + i;
        }

        if (armPart < 0 || movingPart < 0)
            return false;

        float baseArmRotation = simulation.Bodies.WorldRotation[armPart];
        simulation.SetPosture(unit, UnitPosture.Sitting);
        simulation.Update(map, 0.016f);
        float sittingRotation = simulation.Bodies.WorldRotation[armPart];
        if (MathF.Abs(sittingRotation - baseArmRotation) < 0.05f)
        {
            Console.Error.WriteLine(
                $"[Pose test] Sitting pose didn't move the arm: base={baseArmRotation:0.000}, sitting={sittingRotation:0.000}.");
            return false;
        }

        simulation.SetPosture(unit, UnitPosture.Standing);
        simulation.Update(map, 0.016f);
        float standingRotation = simulation.Bodies.WorldRotation[armPart];
        if (MathF.Abs(standingRotation - baseArmRotation) > 0.01f)
            return false;

        float legRotation = simulation.Bodies.WorldRotation[movingPart];
        simulation.SetTarget(unit, simulation.Units.Position[unit.Index] + new Vector3(2f, 0f, 0f));
        simulation.Update(map, 0.016f);
        float walkingLegRotation = simulation.Bodies.WorldRotation[movingPart];

        return simulation.Units.Velocity[unit.Index].LengthSquared() > 0.0025f &&
            MathF.Abs(walkingLegRotation - legRotation) > 0.001f;
    }

    private static bool TestProjectileHitsAuthoredRigSegments()
    {
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId target = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(20f, 20f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX);
        UnitHitRegionProfile profile = simulation.HitRegions.GetProfile(target.Index)!;
        if (profile is not UnitAnatomyProfile anatomy)
            return false;

        UnitHitSystem hitSystem = new UnitHitSystem();
        string[] requiredRegions = { "body.middle", "body.upper" };
        for (int i = 0; i < requiredRegions.Length; i++)
        {
            int regionIndex = anatomy.FindRegion(requiredRegions[i]);
            if (regionIndex < 0)
                return false;

            UnitHitRegionGeometry geometry = UnitHitSystem.GetRegionGeometry(
                simulation.Units,
                target.Index,
                regionIndex,
                simulation.HitRegions,
                simulation.Bodies);
            Vector3 start = geometry.Center - new Vector3(0.25f, 0f, 0f);
            Vector3 end = geometry.Center + new Vector3(0.25f, 0f, 0f);

            if (!hitSystem.TryHitUnit(
                    simulation.Units,
                    target.Index,
                    start,
                    end,
                    0.002f,
                    UnitHealthPartId.None,
                    out UnitHitResult hit,
                    simulation.Bodies,
                    simulation.HitRegions) ||
                hit.RegionIndex != regionIndex)
            {
                Console.Error.WriteLine(
                    $"[Rig hit test] Expected {requiredRegions[i]} (region {regionIndex}); " +
                    $"hit={(hit.RegionIndex >= 0 ? anatomy.GetNode(hit.RegionIndex).Id : "none")}.");
                return false;
            }
        }

        Vector3 root = simulation.Units.Position[target.Index];
        float height = simulation.Units.Height[target.Index];
        Vector3 outsideStart = root + new Vector3(0.62f, -0.08f, height * 0.5f);
        Vector3 outsideEnd = root + new Vector3(0.86f, -0.08f, height * 0.5f);
        return !hitSystem.TryHitUnit(
            simulation.Units,
            target.Index,
            outsideStart,
            outsideEnd,
            0.002f,
            UnitHealthPartId.None,
            out _,
            simulation.Bodies,
            simulation.HitRegions);
    }

    private static bool TestBodyPartProjectionMath()
    {
        Vector3 scale = new(0.12f, 0.035f, 0.18f);
        const float yaw = 0.37f;
        Quaternion yawOnly = BodyPartTransformMath.FromEulerRadians(new Vector3(0f, 0f, yaw));
        BodyPartTransformMath.ProjectedShape top =
            BodyPartTransformMath.Project(scale, yawOnly, horizontalAxis: 0, verticalAxis: 1);

        if (MathF.Abs(top.Scale.X - scale.X) > 0.001f ||
            MathF.Abs(top.Scale.Y - scale.Y) > 0.001f ||
            MathF.Abs(top.Rotation - yaw) > 0.001f)
            return false;

        const float pitch = 0.28f;
        Quaternion pitchOnly = BodyPartTransformMath.FromEulerRadians(new Vector3(pitch, 0f, 0f));
        BodyPartTransformMath.ProjectedShape front =
            BodyPartTransformMath.Project(scale, pitchOnly, horizontalAxis: 1, verticalAxis: 2);
        if (!float.IsFinite(front.Rotation) ||
            MathF.Abs(front.Rotation - pitch) > 0.01f)
            return false;

        const float tilt = 0.22f;
        Quaternion tiltOnly = BodyPartTransformMath.FromEulerRadians(new Vector3(0f, tilt, 0f));
        BodyPartTransformMath.ProjectedShape side =
            BodyPartTransformMath.Project(scale, tiltOnly, horizontalAxis: 0, verticalAxis: 2);
        if (!float.IsFinite(side.Rotation) || MathF.Abs(side.Rotation) < 0.1f)
            return false;

        Quaternion combined = BodyPartTransformMath.Compose(
            yawOnly, new Vector3(pitch, tilt, -0.16f));
        for (int view = 0; view < 3; view++)
        {
            int horizontal = view switch { 0 => 1, 1 => 0, _ => 0 };
            int vertical = view switch { 0 => 2, 1 => 2, _ => 1 };
            BodyPartTransformMath.ProjectedShape projected =
                BodyPartTransformMath.Project(scale, combined, horizontal, vertical);
            if (!float.IsFinite(projected.Scale.X) ||
                !float.IsFinite(projected.Scale.Y) ||
                !float.IsFinite(projected.Scale.Z) ||
                !float.IsFinite(projected.Rotation) ||
                projected.Scale.X <= 0f ||
                projected.Scale.Y <= 0f ||
                projected.Scale.Z <= 0f)
                return false;
        }

        return true;
    }

    private static bool TestVisionDetectsRegionInsideRange()
    {
        WorldMap worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 32);
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId observer = simulation.Spawn(
            UnitType.Colonist, new Vector3(10.5f, 10.5f, 0.1f),
            Vector3.UnitX, Vector3.UnitX, factionTag: 1);

        UnitHitRegionProfile offsetProfile = new UnitHitRegionProfile(
            new UnitCircleHitRegion(new Vector3(-30f, 0f, 8f), radius: 0.001f, halfHeight: 0.001f,
                new HitRegionPartBinding(UnitHealthPartId.Torso, maxHitPoints: 60f)));
        UnitId target = simulation.Spawn(
            UnitType.Colonist, new Vector3(11.2f, 10.5f, 0.1f),
            Vector3.UnitX, Vector3.UnitX, factionTag: 2, hitProfile: offsetProfile);

        simulation.Units.ViewRange[observer.Index] = 1f;
        simulation.Units.FieldOfView[observer.Index] = 360f;
        VisionCheck direct = simulation.Vision.Evaluate(
            simulation.Units, worldMap, observer.Index, target.Index, simulation.Bodies);
        if (!direct.IsVisible)
            return false;

        simulation.Vision.Update(simulation.Units, worldMap, 0.11f, simulation.Health, simulation.Bodies);
        ReadOnlySpan<int> visible = simulation.Vision.GetVisibleTargets(observer.Index);
        for (int i = 0; i < visible.Length; i++)
            if (visible[i] == target.Index)
                return true;
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

        float shooterHealthBefore = simulation.Health.OverallHitPoints[shooter.Index];

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

        UnitId lastHitTarget = simulation.Projectiles.LastHitTarget;
        float shooterHealthAfter = simulation.Health.OverallHitPoints[shooter.Index];

        return simulation.Projectiles.TotalHits > 0 &&
               lastHitTarget.Index == target.Index &&
               lastHitTarget.Generation == target.Generation &&
               MathF.Abs(shooterHealthAfter - shooterHealthBefore) < 0.001f;
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
        UnitAnatomyProfile withMarkers = new(
            UnitAnatomyNode.Circle(
                "sensor-core", new Vector3(0f, 0f, 0.70f), 0.35f, 0.25f,
                new[]
                {
                    new HitRegionPartBinding(UnitHealthPartId.None, kind: UnitHealthPartKind.Organ,
                        maxHitPoints: 18f, id: "sensor-core.tissue")
                },
                new UnitAnatomyMarker("eye", UnitAnatomyMarkerRole.Eye, new Vector3(0f, 0f, 3f)),
                new UnitAnatomyMarker("aim", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim, new Vector3(8f, 0f, 2f)),
                new UnitAnatomyMarker("muzzle", UnitAnatomyMarkerRole.Muzzle, new Vector3(9f, 0f, 4f))));
        UnitAnatomyProfile withoutMarkers = new(
            UnitAnatomyNode.Circle(
                "sensor-core", new Vector3(0f, 0f, -12f), 0.001f, 0.001f,
                new[]
                {
                    new HitRegionPartBinding(UnitHealthPartId.None, kind: UnitHealthPartKind.Organ,
                        maxHitPoints: 18f, id: "sensor-core.tissue")
                }));

        UnitSimulation simulation = new UnitSimulation(4, 32);
        Vector3 position = new Vector3(10f, 12f, 0.1f);
        UnitId unit = simulation.Spawn(UnitType.SegmentedMonster, position, hitProfile: withMarkers);
        UnitId comparison = simulation.Spawn(UnitType.SegmentedMonster, position, hitProfile: withoutMarkers);
        Vector3 eyeA = UnitHitSystem.GetEyePosition(simulation.Units, unit.Index, simulation.Bodies, simulation.HitRegions);
        Vector3 eyeB = UnitHitSystem.GetEyePosition(simulation.Units, comparison.Index, simulation.Bodies, simulation.HitRegions);
        Vector3 source = position + new Vector3(10f, 0f, 0f);
        Vector3 aimA = UnitHitSystem.GetRegionPoint(
            simulation.Units, unit.Index, 0, source, simulation.HitRegions, UnitAnatomyMarkerRole.Aim);
        Vector3 aimB = UnitHitSystem.GetRegionPoint(
            simulation.Units, comparison.Index, 0, source, simulation.HitRegions, UnitAnatomyMarkerRole.Aim);

        bool muzzleFound = UnitHitSystem.TryGetMarkerPoint(
            simulation.Units, unit.Index, UnitAnatomyMarkerRole.Muzzle, out Vector3 muzzle, simulation.HitRegions);
        float height = simulation.Units.Height[unit.Index];
        float radius = simulation.Units.Radius[unit.Index];
        Vector3 expectedMuzzle = position + Vector3.UnitX * (radius * 0.65f) + new Vector3(0f, 0f, height * 0.70f);
        return Vector3.Distance(eyeA, eyeB) < 0.001f &&
            Vector3.Distance(aimA, aimB) < 0.001f &&
            muzzleFound && Vector3.Distance(muzzle, expectedMuzzle) < 0.001f;
    }

    private static bool TestGenericAnatomyTissueHealth()
    {
        UnitAnatomyNode first = UnitAnatomyNode.Circle(
            "damage.root", new Vector3(0f, 0f, 0.2f), 0.2f, 0.1f,
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody,
                    kind: UnitHealthPartKind.BodyPart,
                    maxHitPoints: 30f,
                    id: "damage.root.tissue")
            });

        UnitSimulation simulation = new UnitSimulation(4, 32);
        UnitId unit = simulation.Spawn(
            UnitType.SegmentedMonster,
            new Vector3(10f, 12f, 0.1f),
            hitProfile: new UnitAnatomyProfile(first));

        if (!simulation.Health.TryFindRegionPart(unit.Index, 0, 0, out int rootHealthIndex))
            return false;
        simulation.ApplyAnatomyDamage(unit, 0, 0, 10f);
        if (MathF.Abs(simulation.Health.HitPoints[rootHealthIndex] - 20f) > 0.001f)
            return false;

        UnitAnatomyNode second = UnitAnatomyNode.Circle(
            "damage.child", "damage.root", new Vector3(0f, 0f, 0.8f), 0.15f, 0.08f,
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody,
                    kind: UnitHealthPartKind.BodyPart,
                    maxHitPoints: 40f,
                    id: "damage.child.tissue",
                    parentId: "damage.root.tissue")
            });

        if (!simulation.AddAnatomyNode(unit, second) ||
            !simulation.Health.TryFindRegionPart(unit.Index, 0, 0, out int preservedIndex) ||
            !simulation.Health.TryFindRegionPart(unit.Index, 1, 0, out int childIndex))
            return false;

        simulation.ApplyAnatomyDamage(unit, 1, 0, 10f);
        UnitHitRegionGeometry rootGeometry = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, 0, simulation.HitRegions);
        UnitHitRegionGeometry childGeometry = UnitHitSystem.GetRegionGeometry(
            simulation.Units, unit.Index, 1, simulation.HitRegions);

        return MathF.Abs(simulation.Health.HitPoints[preservedIndex] - 20f) < 0.001f &&
            MathF.Abs(simulation.Health.HitPoints[childIndex] - 30f) < 0.001f &&
            simulation.Health.Parent[childIndex] == preservedIndex &&
            Vector3.Distance(rootGeometry.Center, childGeometry.Center) < 0.001f;
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
        {
            Console.Error.WriteLine($"[Armor self-test] Vest wasn't equipped: slot={vestSlot}, item={simulation.Inventory.GetItem(target.Index, vestSlot)?.Name ?? "<none>"}.");
            return false;
        }

        int durabilityIndex =
            target.Index * UnitInventoryStore.MaxInventorySlots + vestSlot;
        float armorDurabilityBefore = simulation.Inventory.Durability[durabilityIndex];
        float healthBefore = simulation.Health.OverallHitPoints[target.Index];

        DamageSystem damageSystem = new DamageSystem();
        UnitHealthSystem healthSystem = new UnitHealthSystem();
        const float initialEnergy = 1729.8f;

        int torsoRegion = UnitAnatomyCatalog.Get(UnitType.Colonist).Anatomy.FindRegion("body.middle");
        if (torsoRegion < 0)
        {
            Console.Error.WriteLine("[Armor self-test] Canonical torso region 'body.middle' was not found.");
            return false;
        }

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
            {
                Console.Error.WriteLine($"[Armor self-test] Rifle hit {i + 1}: applied={result.DamageApplied:0.000}; expected 0 < damage < 10.");
                return false;
            }
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
            Console.Error.WriteLine($"[Armor self-test] Repeated hits: health {healthBefore:0.0}->{healthAfterRifleHits:0.0}, vest durability {armorDurabilityBefore:0.0}->{armorDurabilityAfter:0.0}.");
            return false;
        }

        UnitDamageResult genericHit = simulation.ApplyDamage(
            target,
            UnitHealthPartId.Torso,
            34f);

        bool genericMitigated = genericHit.FinalDamage > 0f &&
            genericHit.FinalDamage < 20f &&
            simulation.Health.OverallHitPoints[target.Index] > 0f;
        if (!genericMitigated)
            Console.Error.WriteLine($"[Armor self-test] Generic hit applied {genericHit.FinalDamage:0.000}; remaining HP={simulation.Health.OverallHitPoints[target.Index]:0.0}.");
        return genericMitigated;
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
        int stage = 1;
        bool Fail() { Console.Error.WriteLine($"[Static rig test] Failed stage {stage}."); return false; }

        UnitBodyStore definitions = new UnitBodyStore(4);
        BodyHandle colonistBody = definitions.CreateBody(UnitBodyType.Colonist);
        if (colonistBody.Count < 13 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Head) < 1 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Torso) < 3 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.UpperArm) < 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Forearm) < 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Thigh) < 2 ||
            CountBodyRole(definitions, colonistBody, BodyPartRole.Shin) < 2)
            return Fail();

        stage = 2;
        for (int part = colonistBody.Start; part < colonistBody.Start + colonistBody.Count; part++)
        {
            int parent = definitions.Parent[part];
            if (parent < -1 || parent >= part)
                return Fail();

            BodyPartRole role = definitions.Role[part];
            if (role is BodyPartRole.UpperArm or BodyPartRole.Forearm or BodyPartRole.Thigh or BodyPartRole.Shin)
            {
                Vector3 scale = definitions.Scale[part];
                if (definitions.Primitive[part] != BodyPrimitive.Circle ||
                    definitions.Pivot[part] != BodyPartPivot.Start ||
                    scale.X > 0.051f || scale.Y > 0.026f)
                    return Fail();
            }
        }

        stage = 3;
        WorldMap worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 32);
        UnitSimulation simulation = new UnitSimulation(8, 128);
        UnitId colonist = simulation.Spawn(UnitType.Colonist, new Vector3(10f, 10f, 0.1f), Vector3.UnitX, Vector3.UnitX);
        UnitId greenbob = simulation.Spawn(UnitType.Greenbob, new Vector3(12f, 10f, 0.1f), Vector3.UnitX, Vector3.UnitX);
        UnitId segmented = simulation.Spawn(UnitType.SegmentedMonster, new Vector3(14f, 10f, 0.1f), Vector3.UnitX, Vector3.UnitX);
        if (simulation.Units.BodyCount[colonist.Index] < 13 ||
            simulation.Units.BodyCount[greenbob.Index] < 1 ||
            simulation.Units.BodyCount[segmented.Index] < 1)
            return Fail();

        stage = 4;
        int start = simulation.Units.BodyStart[colonist.Index];
        int count = simulation.Units.BodyCount[colonist.Index];
        Vector3[] beforePositions = new Vector3[count];
        float[] beforeRotations = new float[count];
        Vector3[] beforeScales = new Vector3[count];
        Vector3 initialUnitPosition = simulation.Units.Position[colonist.Index];
        for (int i = 0; i < count; i++)
        {
            // Store the base rig relative to its pawn, not as a stale world-space pose.
            beforePositions[i] = simulation.Bodies.WorldPosition[start + i] - initialUnitPosition;
            beforeRotations[i] = simulation.Bodies.WorldRotation[start + i];
            beforeScales[i] = simulation.Bodies.WorldScale[start + i];
        }

        // Move the root before a simulation tick: the whole base rig must follow it.
        simulation.Units.Position[colonist.Index] = initialUnitPosition + new Vector3(0.75f, 0.35f, 0f);
        simulation.SetPosture(colonist, UnitPosture.Crouching);
        simulation.Update(worldMap, 0.016f);
        if (!double.IsFinite(simulation.LastBodyUpdateMilliseconds) ||
            simulation.Units.Posture[colonist.Index] != UnitPosture.Crouching)
            return Fail();

        stage = 5;
        Vector3 currentUnitPosition = simulation.Units.Position[colonist.Index];
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            Vector3 relativePosition = simulation.Bodies.WorldPosition[index] - currentUnitPosition;
            if (Vector3.Distance(beforePositions[i], relativePosition) > 0.0001f ||
                MathF.Abs(beforeRotations[i] - simulation.Bodies.WorldRotation[index]) > 0.0001f ||
                Vector3.Distance(beforeScales[i], simulation.Bodies.WorldScale[index]) > 0.0001f)
            {
                Console.Error.WriteLine($"[Static rig test] bodyPart={i} relativeBefore={beforePositions[i]} relativeAfter={relativePosition}.");
                return Fail();
            }
        }
        return true;
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
