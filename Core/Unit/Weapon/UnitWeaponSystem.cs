using System;
using System.Numerics;
using Core.Combat;
using Core.Items;
using Core.Map;

namespace Core.Unit;

public sealed class UnitWeaponSystem
{
    private readonly UnitHitRegionStore? _hitRegions;
    private readonly UnitBodyStore? _bodies;
    private readonly UnitSkillStore? _skills;
    private readonly UnitHealthStore? _health;

    public UnitWeaponSystem(
        UnitHitRegionStore? hitRegions = null,
        UnitBodyStore? bodies = null,
        UnitSkillStore? skills = null,
        UnitHealthStore? health = null)
    {
        _hitRegions = hitRegions;
        _bodies = bodies;
        _skills = skills;
        _health = health;
    }

    public event Action<UnitId, Vector3, ushort>? ShotFired;

    public long TotalShotsFired { get; private set; }
    public long TotalRoundsConsumed { get; private set; }
    public long TotalProjectilesSpawned { get; private set; }
    public int ShotsFiredThisFrame { get; private set; }

    public void BeginMetricsFrame()
    {
        ShotsFiredThisFrame = 0;
    }

    public void Update(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        float deltaTime,
        UnitHealthStore? health = null,
        VisionSystem? vision = null,
        WorldMap? worldMap = null)
    {
        weapons.UpdateTimers(deltaTime);
        weapons.AdvanceBurstTimer(deltaTime);

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];

            if (health != null && health.OverallHitPoints[unit] <= 0f)
                continue;

            for (int slot = 0;
                 slot < UnitInventoryStore.WeaponSlotCount;
                 slot++)
            {
                UnitWeaponSlot weaponSlot =
                    (UnitWeaponSlot)slot;

                int stateIndex =
                    UnitWeaponStore.GetIndex(
                        unit,
                        weaponSlot);

                short inventorySlot =
                    inventory.GetWeaponEquipment(
                        unit,
                        weaponSlot);

                if (inventorySlot < 0)
                {
                    weapons.ClearSlot(unit, weaponSlot);
                    continue;
                }

                RangedWeaponConfig? weapon =
                    inventory.GetItem(
                        unit,
                        inventorySlot) as RangedWeaponConfig;

                if (weapon == null)
                    continue;

                if (weapons.Reloading[stateIndex] &&
                    weapons.ReloadTimer[stateIndex] <= 0f)
                {
                    weapons.FinishReload(
                        unit,
                        weaponSlot,
                        weapon.MagazineSize);
                }

                UnitCombatCondition condition =
                    UnitCombatConditionSystem.Calculate(health ?? _health, unit);

                if (weapon.RequiresTwoHands && !condition.CanUseTwoHandedWeapon)
                {
                    weapons.ClearAim(unit, weaponSlot);
                    if (weapons.BurstRemaining[stateIndex] > 0)
                        weapons.StartBurst(unit, weaponSlot, default, 0, 0f);
                    continue;
                }

                if (!weapons.Reloading[stateIndex] &&
                    weapons.Ammo[stateIndex] <= 0 &&
                    weapons.ReserveAmmo[stateIndex] > 0 &&
                    weapon.MagazineSize > 0)
                {
                    weapons.StartReload(
                        unit,
                        weaponSlot,
                        weapon.ReloadTime * condition.ReloadTimeMultiplier);
                }

                bool burstReady =
                    weapons.CurrentFireMode[stateIndex] == FireMode.Burst &&
                    weapons.BurstRemaining[stateIndex] > 0 &&
                    weapons.BurstTimer[stateIndex] <= 0f &&
                    weapons.Cooldown[stateIndex] <= 0f;

                if (burstReady)
                {
                    UnitId burstTarget = weapons.BurstTarget[stateIndex];

                    if (!IsLivingTarget(units, health, burstTarget))
                    {
                        weapons.StartBurst(unit, weaponSlot, default, 0, 0f);
                        weapons.ClearAim(unit, weaponSlot);
                        continue;
                    }

                    if (!HasCurrentLineOfSight(
                            units,
                            weapons,
                            vision,
                            worldMap,
                            unit,
                            stateIndex))
                    {
                        weapons.StartBurst(unit, weaponSlot, default, 0, 0f);
                        weapons.ClearAim(unit, weaponSlot);
                        continue;
                    }

                    TryFireAt(
                        units,
                        inventory,
                        weapons,
                        projectiles,
                        units.GetId(unit),
                        weaponSlot,
                        burstTarget,
                        aimPoint: weapons.AimPoint[stateIndex]);

                    continue;
                }

                // Automatic fire runs on weapon cooldown ticks, not on the
                // slower AI decision interval. Target selection stays in AI.
                if (weapons.CurrentFireMode[stateIndex] != FireMode.Auto ||
                    weapons.Cooldown[stateIndex] > 0f ||
                    weapons.Reloading[stateIndex] ||
                    weapons.Ammo[stateIndex] <= 0)
                {
                    continue;
                }

                UnitId target = weapons.AimTarget[stateIndex];
                if (!IsLivingTarget(units, health, target))
                {
                    weapons.ClearAim(unit, weaponSlot);
                    continue;
                }

                if (!HasCurrentLineOfSight(
                        units,
                        weapons,
                        vision,
                        worldMap,
                        unit,
                        stateIndex))
                {
                    weapons.ClearAim(unit, weaponSlot);
                    continue;
                }

                TryFireAt(
                    units,
                    inventory,
                    weapons,
                    projectiles,
                    units.GetId(unit),
                    weaponSlot,
                    target,
                    aimPoint: weapons.AimPoint[stateIndex]);
            }
        }
    }

    private static bool IsLivingTarget(
        UnitStore units,
        UnitHealthStore? health,
        UnitId target)
    {
        return units.IsAlive(target) &&
               (health == null || health.OverallHitPoints[target.Index] > 0f);
    }

    private bool HasCurrentLineOfSight(
        UnitStore units,
        UnitWeaponStore weapons,
        VisionSystem? vision,
        WorldMap? worldMap,
        int unit,
        int stateIndex)
    {
        if (weapons.CurrentAimMode[stateIndex] == AimMode.SuppressFire ||
            vision == null ||
            worldMap == null)
        {
            return true;
        }

        Vector3 aimPoint = weapons.AimPoint[stateIndex];
        if (aimPoint == Vector3.Zero)
            return false;

        return vision.HasLineOfSight(
            worldMap,
            GetMuzzlePosition(units, unit),
            aimPoint,
            out _);
    }

    public bool TryFire(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitId shooter,
        UnitWeaponSlot slot,
        Vector3 direction,
        float accuracyMultiplier = 1f,
        float shotRange = 0f,
        float lifetimeOverride = 0f,
        float shotFlightTime = 0f,
        float targetSpeed = 0f)
    {
        if (!units.TryGetIndex(
                shooter,
                out int unitIndex))
        {
            return false;
        }

        // Apply pawn skill and posture to every shot, including auto-fire and
        // continued bursts that are emitted by UnitWeaponSystem.Update rather
        // than an AI decision tick.
        float skillSpread = _skills?.GetShootingSpreadMultiplier(unitIndex) ?? 1f;
        float postureSpread = units.Posture[unitIndex] switch
        {
            UnitPosture.Lying => 0.78f,
            UnitPosture.Crouching => 0.86f,
            UnitPosture.Sitting => 0.92f,
            _ => 1f
        };
        accuracyMultiplier *= skillSpread * postureSpread;

        short inventorySlot =
            inventory.GetWeaponEquipment(
                unitIndex,
                slot);

        if (inventorySlot < 0)
            return false;

        RangedWeaponConfig? weapon =
            inventory.GetItem(
                unitIndex,
                inventorySlot) as RangedWeaponConfig;

        if (weapon == null ||
            weapon.MagazineSize <= 0)
        {
            return false;
        }

        UnitCombatCondition combatCondition =
            UnitCombatConditionSystem.Calculate(_health, unitIndex);
        if (weapon.RequiresTwoHands && !combatCondition.CanUseTwoHandedWeapon)
            return false;

        accuracyMultiplier *= combatCondition.AimSpreadMultiplier;

        int stateIndex =
            UnitWeaponStore.GetIndex(
                unitIndex,
                slot);

        if (weapons.Cooldown[stateIndex] > 0f ||
            weapons.Reloading[stateIndex] ||
            weapons.Ammo[stateIndex] <= 0)
        {
            return false;
        }

        AmmunitionConfig? ammo =
            GetCurrentAmmunition(
                weapon,
                weapons.CurrentAmmoType[stateIndex]);

        if (ammo == null)
            return false;

        float aimDuration = GetAimDuration(
            weapon,
            weapons.CurrentAimMode[stateIndex],
            shotRange);

        float aimProgress =
            aimDuration <= 0f
                ? 1f
                : Math.Clamp(
                    weapons.AimTimer[stateIndex] /
                    aimDuration,
                    0f,
                    1f);

        float spread =
            ShotAccuracy.CalculateSpread(
                weapon.GetCurrentSpread(),
                weapon.SightEfficiency,
                weapon.AimingAccuracy,
                units.Velocity[unitIndex].Length(),
                weapon.MovementSpread,
                weapons.Recoil[stateIndex],
                accuracyMultiplier,
                shotRange,
                weapon.TotalEffectiveRange,
                aimProgress,
                weapons.CurrentAimMode[stateIndex],
                weapon.CircularError,
                targetSpeed,
                shotFlightTime,
                weapon.LeadError) *
            MathF.Max(
                0.1f,
                ammo.SpreadMultiplier);

        float velocity =
            MathF.Max(
                0f,
                ammo.MuzzleVelocity);

        if (velocity <= 0f)
            return false;

        Vector3 muzzle =
            GetMuzzlePosition(
                units,
                unitIndex);

        float range =
            MathF.Max(
                1f,
                weapon.TotalEffectiveRange);

        float lifetime =
            lifetimeOverride > 0f
                ? lifetimeOverride
                : MathF.Max(
                    0.5f,
                    range / velocity * 2f);

        int pelletCount =
            Math.Clamp(
                ammo.PelletCount,
                1,
                32);

        float effectiveDrag =
            ammo.DragCoefficient /
            MathF.Max(
                0.01f,
                ammo.BallisticCoefficient);

        for (int pellet = 0;
             pellet < pelletCount;
             pellet++)
        {
            Vector3 pelletDirection =
                ApplySpread(
                    direction,
                    pelletCount > 1
                        ? spread * 1.35f
                        : spread,
                    weapons.NextRandom(
                        unitIndex,
                        slot),
                    weapons.NextRandom(
                        unitIndex,
                        slot));

            projectiles.Create(
                shooter,
                units.FactionTag[unitIndex],
                muzzle,
                pelletDirection,
                ammo.ProjectileMassKg,
                ammo.ProjectileDiameterM,
                ammo.MuzzleVelocity,
                effectiveDrag,
                ammo.SharpPenetration > 0f
                    ? ammo.SharpPenetration
                    : ammo.Penetration,
                ammo.BluntPenetration > 0f
                    ? ammo.BluntPenetration
                    : ammo.Penetration * 0.75f,
                weapon.BaseDamage *
                ammo.DamageMultiplier,
                lifetime,
                ammo.DamageType,
                ammo.SuppressionFactor *
                weapon.SuppressionFactor,
                weapon.BleedChance > 0f
                    ? weapon.BleedChance
                    : ammo.BleedChance,
                ammo.IsLaser);
        }

        // Show a representative muzzle flash instead of one marker per round.
        if (TotalShotsFired % 5 == 0)
        {
            projectiles.RegisterImpact(
                muzzle,
                ProjectileImpactKind.MuzzleFlash);
        }

        weapons.Ammo[stateIndex]--;

        TotalShotsFired++;
        ShotFired?.Invoke(shooter, units.Position[unitIndex], units.FactionTag[unitIndex]);
        _skills?.GainExperience(unitIndex, UnitSkill.Shooting, 8f);
        TotalRoundsConsumed++;
        TotalProjectilesSpawned += pelletCount;
        ShotsFiredThisFrame++;

        weapons.AddRecoil(
            unitIndex,
            slot,
            weapon.Recoil *
            MathF.Max(
                0.1f,
                ammo.RecoilMultiplier));

        weapons.Cooldown[stateIndex] =
            MathF.Max(
                0f,
                weapon.FireRate);

        return true;
    }

    public bool TryFireAt(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitId shooter,
        UnitWeaponSlot slot,
        UnitId target,
        float accuracyMultiplier = 1f,
        Vector3? aimPoint = null)
    {
        if (!units.TryGetIndex(shooter, out int shooterIndex) ||
            !units.TryGetIndex(target, out int targetIndex))
        {
            return false;
        }

        int stateIndex = UnitWeaponStore.GetIndex(shooterIndex, slot);
        RangedWeaponConfig? weapon = GetRangedWeapon(inventory, shooterIndex, slot);
        if (weapon == null)
            return false;

        bool continuingBurst =
            weapons.CurrentFireMode[stateIndex] == FireMode.Burst &&
            weapons.BurstRemaining[stateIndex] > 0;

        if (continuingBurst)
        {
            target = weapons.BurstTarget[stateIndex];

            if (!units.TryGetIndex(target, out targetIndex))
                return false;
        }

        Vector3 aimOrigin = GetMuzzlePosition(units, shooterIndex);
        Vector3 requestedAimPoint = aimPoint ??
            GetTargetRegionAimPoint(
                units,
                targetIndex,
                aimOrigin,
                weapons.CurrentTargetMode[stateIndex]);

        Vector3 shooterPosition = units.Position[shooterIndex];
        float dx = requestedAimPoint.X - shooterPosition.X;
        float dy = requestedAimPoint.Y - shooterPosition.Y;
        float targetDistance = MathF.Sqrt(dx * dx + dy * dy);

        bool resetAim = weapons.AimTarget[stateIndex] != target;
        weapons.SetAimTarget(
            shooterIndex,
            slot,
            target,
            resetAim,
            requestedAimPoint);

        float aimDuration = GetAimDuration(
            weapon,
            weapons.CurrentAimMode[stateIndex],
            targetDistance);

        if (weapons.AimTimer[stateIndex] < aimDuration)
            return false;

        int previousBurstRemaining = weapons.BurstRemaining[stateIndex];

        bool fired = TryFireAtInternal(
            units,
            inventory,
            weapons,
            projectiles,
            shooter,
            slot,
            target,
            accuracyMultiplier,
            requestedAimPoint);

        if (!fired)
        {
            if (continuingBurst)
            {
                weapons.StartBurst(
                    shooterIndex,
                    slot,
                    default,
                    0,
                    0f);
            }

            return false;
        }

        if (weapons.CurrentFireMode[stateIndex] == FireMode.Burst)
        {
            int remaining = continuingBurst
                ? previousBurstRemaining - 1
                : Math.Max(0, weapon.BurstCount - 1);

            weapons.StartBurst(
                shooterIndex,
                slot,
                remaining > 0 ? target : default,
                remaining,
                remaining > 0
                    ? MathF.Max(weapon.BurstInterval, weapon.FireRate)
                    : 0f);
        }

        weapons.MarkAimComplete(shooterIndex, slot);

        // A burst is one firing action. The next burst must start a new
        // range-dependent warmup instead of inheriting the previous timer.
        if (weapons.CurrentFireMode[stateIndex] == FireMode.Single ||
            (weapons.CurrentFireMode[stateIndex] == FireMode.Burst &&
             weapons.BurstRemaining[stateIndex] == 0))
        {
            weapons.ClearAim(shooterIndex, slot);
        }

        AmmunitionConfig? currentAmmo = GetCurrentAmmunition(
            weapon,
            weapons.CurrentAmmoType[stateIndex]);

        if (CombatDiagnostics.Enabled)
        {
            CombatDiagnostics.WriteLine(
                $"[SHOT] {shooter} faction={units.FactionTag[shooterIndex]} " +
                $"target={target} targetFaction={units.FactionTag[targetIndex]} " +
                $"mode={weapons.CurrentFireMode[stateIndex]} " +
                $"aim={weapons.CurrentAimMode[stateIndex]} " +
                $"targetMode={weapons.CurrentTargetMode[stateIndex]} " +
                $"targetPos={units.Position[targetIndex]} " +
                $"aimPoint={requestedAimPoint} " +
                $"ammo={currentAmmo?.Name ?? "none"}");
        }

        return true;
    }

    public static float GetAimDuration(
        RangedWeaponConfig weapon,
        AimMode mode,
        float distance)
    {
        float configuredTime = MathF.Max(0f, weapon.AimTime);

        return mode switch
        {
            // CE uses 30-240 ticks (0.5-4 seconds), with warmup scaling by range.
            AimMode.AimedShot => MathF.Max(
                configuredTime,
                0.5f + 3.5f * Math.Clamp(MathF.Max(0f, distance) / 100f, 0f, 1f)),

            AimMode.Snapshot => Math.Clamp(
                configuredTime * 0.45f,
                0.18f,
                0.35f),

            AimMode.SuppressFire => Math.Clamp(
                configuredTime * 0.30f,
                0.12f,
                0.22f),

            _ => configuredTime
        };
    }

    private bool TryFireAtInternal(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitId shooter,
        UnitWeaponSlot slot,
        UnitId target,
        float accuracyMultiplier,
        Vector3? aimPoint = null)
    {
        if (!units.TryGetIndex(
                shooter,
                out int shooterIndex) ||
            !units.TryGetIndex(
                target,
                out int targetIndex))
        {
            return false;
        }

        RangedWeaponConfig? weapon =
            GetRangedWeapon(
                inventory,
                shooterIndex,
                slot);

        if (weapon == null)
            return false;

        int stateIndex =
            UnitWeaponStore.GetIndex(
                shooterIndex,
                slot);

        AmmunitionConfig? ammo =
            GetCurrentAmmunition(
                weapon,
                weapons.CurrentAmmoType[stateIndex]);

        if (ammo == null)
            return false;

        Vector3 muzzle =
            GetMuzzlePosition(
                units,
                shooterIndex);

        Vector3 targetPoint = aimPoint ??
            GetTargetRegionAimPoint(
                units,
                targetIndex,
                muzzle,
                weapons.CurrentTargetMode[stateIndex]);

        Vector3 predictedTarget =
            targetPoint;

        BallisticSolution solution =
            default;

        bool solved = false;
        bool suppressing =
            weapons.CurrentAimMode[stateIndex] == AimMode.SuppressFire;

        if (ammo.IsLaser)
        {
            // Lasers aim directly with modest target lead and no ballistic drop.
            float distance = Vector3.Distance(muzzle, targetPoint);
            float speed = MathF.Max(1f, ammo.MuzzleVelocity);
            float flightTime = distance / speed;

            if (!suppressing)
            {
                predictedTarget +=
                    units.Velocity[targetIndex] *
                    Math.Clamp(flightTime, 0f, 0.35f);
            }

            Vector3 laserDelta = predictedTarget - muzzle;
            float laserLength = laserDelta.Length();
            Vector3 laserDirection = laserLength > 0.001f
                ? laserDelta / laserLength
                : Vector3.UnitX;

            solution = new BallisticSolution(
                laserDirection,
                laserLength / speed,
                speed,
                0.5f * ammo.ProjectileMassKg * speed * speed);
            solved = true;
        }
        else
        {
            // Suppressive fire targets the last known point, not the target's
            // current movement vector. Aimed shots and snapshots still lead targets.
            for (int iteration = 0;
                 !suppressing && iteration < 3;
                 iteration++)
            {
                solved =
                    ProjectileBallistics.TrySolve(
                        muzzle,
                        predictedTarget,
                        ammo.ProjectileMassKg,
                        ammo.ProjectileDiameterM,
                        ammo.MuzzleVelocity,
                        ammo.DragCoefficient,
                        ammo.BallisticCoefficient,
                        out solution);

                if (!solved)
                    break;

                predictedTarget =
                    targetPoint +
                    units.Velocity[targetIndex] *
                    solution.TimeOfFlight;
            }

            if (!solved)
            {
                predictedTarget =
                    targetPoint;

                if (!ProjectileBallistics.TrySolve(
                        muzzle,
                        predictedTarget,
                        ammo.ProjectileMassKg,
                        ammo.ProjectileDiameterM,
                        ammo.MuzzleVelocity,
                        ammo.DragCoefficient,
                        ammo.BallisticCoefficient,
                        out solution))
                {
                    solution =
                        new BallisticSolution(
                            Normalize(
                                targetPoint - muzzle),
                            0f,
                            ammo.MuzzleVelocity,
                            0f);
                }
            }
        }

        Vector3 shotDelta =
            predictedTarget - muzzle;

        float targetDistance =
            MathF.Sqrt(
                shotDelta.X * shotDelta.X +
                shotDelta.Y * shotDelta.Y);

        if (targetDistance >
            weapon.TotalEffectiveRange)
        {
            return false;
        }

        return TryFire(
            units,
            inventory,
            weapons,
            projectiles,
            shooter,
            slot,
            solution.Direction,
            accuracyMultiplier,
            targetDistance,
            MathF.Max(
                0.5f,
                solution.TimeOfFlight + 0.25f),
            solution.TimeOfFlight,
            units.Velocity[targetIndex].Length());
    }

    private Vector3 GetTargetRegionAimPoint(
        UnitStore units,
        int targetIndex,
        Vector3 source,
        TargetMode targetMode)
    {
        if (_hitRegions?.GetProfile(targetIndex) is UnitAnatomyProfile)
        {
            // Anatomy only supplies health bindings and display geometry. Aim selection
            // follows the same three procedural zones used by vision and projectile hits.
            Span<Vector3> points = stackalloc Vector3[UnitGameplayZoneSystem.ZoneCount];
            int written = UnitHitSystem.GetAimPoints(
                units, targetIndex, source, points, bodies: null, hitRegions: _hitRegions);

            if (written > 0)
            {
                int preferredZone = targetMode switch
                {
                    TargetMode.Head => (int)UnitGameplayZone.Head,
                    TargetMode.Torso => (int)UnitGameplayZone.Torso,
                    TargetMode.Legs => (int)UnitGameplayZone.LowerBody,
                    _ => (int)UnitGameplayZone.Torso
                };
                return points[Math.Clamp(preferredZone, 0, written - 1)];
            }
        }

        Vector3 position = units.Position[targetIndex];
        Vector3 towardSource = new(source.X - position.X, source.Y - position.Y, 0f);
        float lengthSquared = towardSource.LengthSquared();
        if (lengthSquared > 0.000001f)
            towardSource /= MathF.Sqrt(lengthSquared);
        else
            towardSource = Vector3.UnitX;

        float radius = MathF.Max(0.01f, units.Radius[targetIndex]);
        float height = MathF.Max(0.05f, units.Height[targetIndex]);
        float verticalFraction = targetMode switch
        {
            TargetMode.Head => units.Posture[targetIndex] == UnitPosture.Lying ? 0.22f : 0.84f,
            TargetMode.Legs => units.Posture[targetIndex] == UnitPosture.Lying ? 0.06f : 0.18f,
            TargetMode.Torso => units.Posture[targetIndex] == UnitPosture.Lying ? 0.15f : 0.52f,
            _ => units.Posture[targetIndex] == UnitPosture.Lying ? 0.14f : 0.55f
        };

        return new Vector3(
            position.X + towardSource.X * radius * 0.65f,
            position.Y + towardSource.Y * radius * 0.65f,
            position.Z + height * verticalFraction);
    }

private static AmmunitionConfig? GetCurrentAmmunition(
        RangedWeaponConfig weapon,
        int ammoType)
    {
        if (weapon.AmmoSet != null)
        {
            AmmunitionConfig? selected =
                weapon.AmmoSet.Get(
                    ammoType);

            if (selected != null)
                return selected;
        }

        return weapon.DefaultAmmunition;
    }

    private static RangedWeaponConfig? GetRangedWeapon(
        UnitInventoryStore inventory,
        int unitIndex,
        UnitWeaponSlot slot)
    {
        short inventorySlot =
            inventory.GetWeaponEquipment(
                unitIndex,
                slot);

        if (inventorySlot < 0)
            return null;

        return inventory.GetItem(
                unitIndex,
                inventorySlot)
            as RangedWeaponConfig;
    }

    private Vector3 GetMuzzlePosition(
        UnitStore units,
        int unitIndex)
    {
        if (_bodies != null &&
            _hitRegions?.GetProfile(unitIndex) is UnitAnatomyProfile anatomy &&
            anatomy.HasSharedShapes &&
            UnitHitSystem.TryGetMarkerPoint(
                units,
                unitIndex,
                UnitAnatomyMarkerRole.Muzzle,
                out Vector3 muzzle,
                _hitRegions,
                _bodies))
        {
            return muzzle;
        }

        Vector3 head = NormalizeHorizontal(units.HeadNormal[unitIndex]);
        float radius = MathF.Max(0.05f, units.Radius[unitIndex]);
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        float heightFraction = units.Posture[unitIndex] switch
        {
            UnitPosture.Lying => 0.17f,
            UnitPosture.Crouching => 0.58f,
            UnitPosture.Sitting => 0.68f,
            _ => 0.70f
        };

        return units.Position[unitIndex] +
            head * (radius * 0.75f + 0.08f) +
            new Vector3(0f, 0f, height * heightFraction);
    }

private static Vector3 ApplySpread(
        Vector3 direction,
        float spread,
        uint randomA,
        uint randomB)
    {
        direction =
            Normalize(
                direction);

        if (spread <= 0f)
            return direction;

        Vector3 right =
            new Vector3(
                -direction.Y,
                direction.X,
                0f);

        right =
            right.LengthSquared() <
            0.000001f
                ? new Vector3(
                    1f,
                    0f,
                    0f)
                : Normalize(
                    right);

        Vector3 up =
            Normalize(
                Vector3.Cross(
                    right,
                    direction));

        float radius =
            MathF.Sqrt(
                ToUnitFloat(
                    randomA)) *
            spread;

        float angle =
            ToUnitFloat(
                randomB) *
            MathF.Tau;

        Vector3 offset =
            right *
            (MathF.Cos(angle) *
            radius) +
            up *
            (MathF.Sin(angle) *
            radius);

        return Normalize(
            direction + offset);
    }

    private static float ToUnitFloat(
        uint value)
    {
        return
            (value & 0x00FFFFFFu) /
            16777215f;
    }

    private static Vector3 NormalizeHorizontal(
        Vector3 value)
    {
        value.Z = 0f;
        return Normalize(value);
    }

    private static Vector3 Normalize(
        Vector3 value)
    {
        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared <
            0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }
}
