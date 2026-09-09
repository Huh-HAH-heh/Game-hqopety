using Core.CombatPath;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using Core.Unit.Systems.CombatPath;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems;

public sealed class UnitCombatSystem
{
    private readonly List<int> _nearbyCandidates =
        new List<int>(32);

    private readonly UnitDamageSystem _damageSystem =
        new UnitDamageSystem();

    private float[] _targetScanTimers =
        Array.Empty<float>();

    private float[] _targetLosTimers =
        Array.Empty<float>();

    /*
     * Временный физический радиус юнита
     * для определения высоты центра массы.
     *
     * Не связан с логическими координатами.
     */
    private const float TemporaryUnitRadius = 0.4f;

    private const float TargetScanInterval =
        0.20f;

    private const float TargetLosInterval =
        0.10f;

    private const int MaxTargetScanRadius =
        60;

    private const float MeleeThreatRadius =
        2.5f;

    public void Update(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        EdificeStore edificeStore,
        CombatEffectSystem effectSystem,
        float microCellPixelSize,
        float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        EnsureStorage(units.Count);

        for (
            int i = 0;
            i < units.Count;
            i++)
        {
            if (units.HealthMasks[i] == 0)
                continue;

            if (_targetScanTimers[i] > 0f)
            {
                _targetScanTimers[i] -=
                    deltaTime;

                if (_targetScanTimers[i] < 0f)
                    _targetScanTimers[i] = 0f;
            }

            if (_targetLosTimers[i] > 0f)
            {
                _targetLosTimers[i] -=
                    deltaTime;

                if (_targetLosTimers[i] < 0f)
                    _targetLosTimers[i] = 0f;
            }

            if (units.ShotCooldowns[i] > 0f)
            {
                units.ShotCooldowns[i] -=
                    deltaTime;

                if (units.ShotCooldowns[i] < 0f)
                    units.ShotCooldowns[i] = 0f;
            }

            if (units.WeaponSlot[i] is Weapon activeWeapon)
            {
                if (activeWeapon.currentRecoil > 0f)
                {
                    activeWeapon.currentRecoil -=
                        activeWeapon.BaseStats.RecoilRecovery *
                        deltaTime;

                    if (activeWeapon.currentRecoil < 0f)
                        activeWeapon.currentRecoil = 0f;
                }
            }
        }

        ExecuteRimWorldCombat(
            units,
            spatialGrid,
            map,
            edificeStore,
            effectSystem,
            microCellPixelSize,
            deltaTime);
    }

    private void ExecuteRimWorldCombat(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        EdificeStore edificeStore,
        CombatEffectSystem effectSystem,
        float microCellPixelSize,
        float deltaTime)
    {
        for (
            int i = 0;
            i < units.Count;
            i++)
        {
            if (units.HealthMasks[i] == 0)
                continue;

            ref UnitPosition posA =
                ref units.Positions[i];

            ref UnitMovement moveA =
                ref units.Movement[i];

            int currentTargetId =
                units.CurrentTargets[i];

            int currentAttackRange =
                GetAttackRange(
                    units,
                    i);

            // ============================================================
            // EXISTING TARGET
            // ============================================================

            if (currentTargetId != -1)
            {
                bool targetValid =
                    currentTargetId >= 0 &&
                    currentTargetId < units.Count &&
                    units.HealthMasks[currentTargetId] > 0;

                if (!targetValid)
                {
                    units.CurrentTargets[i] = -1;
                    currentTargetId = -1;

                    ResetAiming(
                        units,
                        i);
                }
                else
                {
                    ref UnitPosition targetPos =
                        ref units.Positions[currentTargetId];

                    if (targetPos.Spatial.Z !=
                        posA.Spatial.Z)
                    {
                        units.CurrentTargets[i] = -1;
                        currentTargetId = -1;

                        ResetAiming(
                            units,
                            i);
                    }
                    else
                    {
                        float dx =
                            posA.X -
                            targetPos.X;

                        float dy =
                            posA.Y -
                            targetPos.Y;

                        float distance =
                            MathF.Sqrt(
                                dx * dx +
                                dy * dy);

                        int visionRadius =
                            Math.Min(
                                currentAttackRange + 5,
                                MaxTargetScanRadius);

                        if (distance > visionRadius)
                        {
                            units.CurrentTargets[i] = -1;
                            currentTargetId = -1;

                            ResetAiming(
                                units,
                                i);
                        }
                        else if (_targetLosTimers[i] <= 0f)
                        {
                            bool visible =
                                HasUnitLineOfSight(
                                    units,
                                    map,
                                    edificeStore,
                                    i,
                                    currentTargetId);

                            _targetLosTimers[i] =
                                TargetLosInterval;

                            if (!visible)
                            {
                                units.CurrentTargets[i] = -1;
                                currentTargetId = -1;

                                ResetAiming(
                                    units,
                                    i);
                            }
                        }
                    }
                }
            }

            // ============================================================
            // SEARCH TARGET
            // ============================================================

            if (currentTargetId == -1 &&
                _targetScanTimers[i] <= 0f)
            {
                currentTargetId =
                    FindClosestEnemy(
                        units,
                        spatialGrid,
                        map,
                        edificeStore,
                        i,
                        currentAttackRange);

                units.CurrentTargets[i] =
                    currentTargetId;

                _targetScanTimers[i] =
                    TargetScanInterval *
                    GetScanJitter(i);
            }

            // ============================================================
            // NO TARGET
            // ============================================================

            int finalTarget =
                units.CurrentTargets[i];

            if (finalTarget < 0 ||
                finalTarget >= units.Count)
            {
                ResetAiming(
                    units,
                    i);

                continue;
            }

            if (units.HealthMasks[finalTarget] == 0)
            {
                units.CurrentTargets[i] = -1;

                ResetAiming(
                    units,
                    i);

                continue;
            }

            ref UnitPosition targetPosFinal =
                ref units.Positions[finalTarget];

            float fdx =
                posA.X -
                targetPosFinal.X;

            float fdy =
                posA.Y -
                targetPosFinal.Y;

            float distanceInTiles =
                MathF.Sqrt(
                    fdx * fdx +
                    fdy * fdy);

            if (distanceInTiles >
                currentAttackRange)
            {
                if (units.IsAiming[i])
                    ResetAiming(
                        units,
                        i);

                continue;
            }

            // ============================================================
            // BURST
            // ============================================================

            if (units.RemainingBurstShots[i] > 0)
            {
                if (units.ShotCooldowns[i] <= 0f)
                {
                    PerformAttack(
                        units,
                        i,
                        finalTarget,
                        map,
                        edificeStore,
                        spatialGrid,
                        effectSystem,
                        microCellPixelSize);

                    /*
                     * Burst-логика намеренно не меняется.
                     *
                     * Даже если PerformAttack() вернул false
                     * из-за потерянного LOS, один выстрел
                     * всё равно считается потраченным.
                     */

                    units.RemainingBurstShots[i]--;

                    if (units.RemainingBurstShots[i] <= 0)
                    {
                        units.IsAiming[i] = false;

                        units.LeanOffsetX[i] = 0;
                        units.LeanOffsetY[i] = 0;

                        units.ShotCooldowns[i] =
                            units.WeaponSlot[i] != null
                                ? units.WeaponSlot[i]
                                    .BaseStats
                                    .FireRate
                                : 1.5f;
                    }
                    else
                    {
                        units.ShotCooldowns[i] =
                            0.1f;
                    }
                }

                continue;
            }

            // ============================================================
            // START AIMING
            // ============================================================

            bool canShoot =
                units.WeaponSlot[i] != null;

            if (!units.IsAiming[i] &&
                units.ShotCooldowns[i] <= 0f &&
                canShoot)
            {
                units.IsAiming[i] = true;

                units.AimingTimers[i] =
                    units.WeaponSlot[i]
                        .BaseStats
                        .RecoilRecovery *
                    1.5f;

                if (moveA.State ==
                    MovementState.InCover)
                {
                    units.LeanOffsetX[i] =
                        Math.Sign(
                            targetPosFinal.X -
                            posA.X);

                    units.LeanOffsetY[i] =
                        Math.Sign(
                            targetPosFinal.Y -
                            posA.Y);
                }
                else
                {
                    units.LeanOffsetX[i] = 0;
                    units.LeanOffsetY[i] = 0;
                }

                continue;
            }

            // ============================================================
            // AIMING
            // ============================================================

            if (units.IsAiming[i] &&
                units.RemainingBurstShots[i] <= 0)
            {
                units.AimingTimers[i] -=
                    deltaTime;

                if (moveA.State !=
                    MovementState.InCover)
                {
                    units.LeanOffsetX[i] = 0;
                    units.LeanOffsetY[i] = 0;
                }

                if (units.AimingTimers[i] <= 0f)
                {
                    int burstCount = 1;

                    if (units.WeaponSlot[i] != null)
                    {
                        burstCount =
                            units.WeaponSlot[i]
                                .GetBurstCountForDistance(
                                    distanceInTiles);
                    }

                    units.RemainingBurstShots[i] =
                        burstCount;

                    units.AimingTimers[i] =
                        0f;
                }
            }
        }
    }

    private int FindClosestEnemy(
        UnitStore units,
        UnitSpatialGrid spatialGrid,
        WorldMap map,
        EdificeStore edificeStore,
        int unitId,
        int attackRange)
    {
        ref UnitPosition posA =
            ref units.Positions[unitId];

        int visionRadius =
            Math.Min(
                attackRange + 5,
                MaxTargetScanRadius);

        _nearbyCandidates.Clear();

        spatialGrid.GetNearby(
            posA.Spatial,
            visionRadius,
            _nearbyCandidates);

        int bestTarget = -1;
        float bestDistance =
            float.MaxValue;

        for (
            int idx = 0;
            idx < _nearbyCandidates.Count;
            idx++)
        {
            int candidateId =
                _nearbyCandidates[idx];

            if (candidateId == unitId)
                continue;

            if (candidateId < 0 ||
                candidateId >= units.Count)
            {
                continue;
            }

            if (units.HealthMasks[candidateId] == 0)
                continue;

            if (units.UnitType[candidateId] ==
                units.UnitType[unitId])
            {
                continue;
            }

            ref UnitPosition posB =
                ref units.Positions[candidateId];

            if (posA.Spatial.Z !=
                posB.Spatial.Z)
            {
                continue;
            }

            float dx =
                posA.X -
                posB.X;

            float dy =
                posA.Y -
                posB.Y;

            float distanceSqr =
                dx * dx +
                dy * dy;

            if (distanceSqr >
                attackRange * attackRange)
            {
                continue;
            }

            if (distanceSqr <
                MeleeThreatRadius *
                MeleeThreatRadius)
            {
                if (!HasUnitLineOfSight(
                        units,
                        map,
                        edificeStore,
                        unitId,
                        candidateId))
                {
                    continue;
                }

                if (distanceSqr <
                    bestDistance)
                {
                    bestDistance =
                        distanceSqr;

                    bestTarget =
                        candidateId;
                }

                continue;
            }

            if (distanceSqr >=
                bestDistance)
            {
                continue;
            }

            if (!HasUnitLineOfSight(
                    units,
                    map,
                    edificeStore,
                    unitId,
                    candidateId))
            {
                continue;
            }

            bestDistance =
                distanceSqr;

            bestTarget =
                candidateId;
        }

        return bestTarget;
    }

    private static bool HasUnitLineOfSight(
        UnitStore units,
        WorldMap map,
        EdificeStore edificeStore,
        int sourceId,
        int targetId)
    {
        if (sourceId < 0 ||
            sourceId >= units.Count ||
            targetId < 0 ||
            targetId >= units.Count)
        {
            return false;
        }

        if (units.HealthMasks[sourceId] == 0 ||
            units.HealthMasks[targetId] == 0)
        {
            return false;
        }

        ref UnitPosition source =
            ref units.Positions[sourceId];

        ref UnitPosition target =
            ref units.Positions[targetId];

        if (source.Spatial.Z !=
            target.Spatial.Z)
        {
            return false;
        }

        MapLayer layer =
            map.GetLayer(
                source.Spatial.Z);

        if (layer == null)
            return false;

        float sourceHeight =
            GetUnitCenterHeight(
                layer,
                source.Spatial);

        float targetHeight =
            GetUnitCenterHeight(
                layer,
                target.Spatial);

        return VisibilityChecker.HasLineOfSight(
            layer,
            edificeStore,
            source.Spatial.X,
            source.Spatial.Y,
            HeightRange.Point(
                sourceHeight),
            target.Spatial.X,
            target.Spatial.Y,
            HeightRange.Point(
                targetHeight));
    }

    private static float GetUnitCenterHeight(
        MapLayer layer,
        SpatialCoord position)
    {
        ref MicroCell cell =
            ref layer.GetMicroCell(
                position.X,
                position.Y);

        return
            cell.Height +
            TemporaryUnitRadius;
    }

    private int GetAttackRange(
        UnitStore units,
        int unitId)
    {
        if (units.WeaponSlot[unitId] == null)
            return 4;

        float range =
            units.WeaponSlot[unitId]
                .TotalEffectiveRange;

        return Math.Min(
            150,
            Math.Max(
                1,
                (int)range));
    }

    private float GetScanJitter(
        int unitId)
    {
        unchecked
        {
            uint x =
                (uint)unitId *
                747796405u;

            x ^= x >> 16;
            x *= 2246822519u;
            x ^= x >> 13;

            float value =
                (x & 0xFFFFu) /
                65536f;

            return
                0.75f +
                value *
                0.75f;
        }
    }

    private void ResetAiming(
        UnitStore units,
        int unitId)
    {
        units.IsAiming[unitId] = false;
        units.RemainingBurstShots[unitId] = 0;
        units.AimingTimers[unitId] = 0f;

        units.LeanOffsetX[unitId] = 0;
        units.LeanOffsetY[unitId] = 0;
    }

    private void EnsureStorage(
        int count)
    {
        if (_targetScanTimers.Length >= count)
            return;

        int oldSize =
            _targetScanTimers.Length;

        int newSize =
            Math.Max(
                count,
                Math.Max(
                    64,
                    oldSize * 2));

        Array.Resize(
            ref _targetScanTimers,
            newSize);

        Array.Resize(
            ref _targetLosTimers,
            newSize);

        for (
            int i = oldSize;
            i < newSize;
            i++)
        {
            _targetScanTimers[i] =
                GetInitialScanOffset(i);

            _targetLosTimers[i] =
                0f;
        }
    }

    private float GetInitialScanOffset(
        int unitId)
    {
        unchecked
        {
            uint x =
                (uint)unitId *
                1597334677u;

            x ^= x >> 16;
            x *= 2246822519u;
            x ^= x >> 13;

            float value =
                (x & 0xFFFFu) /
                65536f;

            return
                value *
                TargetScanInterval;
        }
    }

    private void PerformAttack(
        UnitStore units,
        int attackerId,
        int targetId,
        WorldMap map,
        EdificeStore edificeStore,
        UnitSpatialGrid spatialGrid,
        CombatEffectSystem effectSystem,
        float microCellPixelSize)
    {
        if (attackerId < 0 ||
            attackerId >= units.Count)
        {
            return;
        }

        if (targetId < 0 ||
            targetId >= units.Count)
        {
            return;
        }

        if (units.HealthMasks[attackerId] == 0 ||
            units.HealthMasks[targetId] == 0)
        {
            return;
        }

        /*
         * Финальная LOS-проверка непосредственно
         * перед выстрелом.
         *
         * Цель могла закрыться стеной/рельефом
         * после последнего cached LOS.
         */

        if (!HasUnitLineOfSight(
                units,
                map,
                edificeStore,
                attackerId,
                targetId))
        {
            return;
        }

        ref UnitPosition posA =
            ref units.Positions[attackerId];

        ref UnitPosition posB =
            ref units.Positions[targetId];

        byte baseDamage =
            25;

        float weaponBleedChance =
            0.7f;

        float effectiveRange =
            2f;

        if (units.WeaponSlot[attackerId]
            is Weapon activeWeapon)
        {
            baseDamage =
                activeWeapon.BaseStats.BaseDamage;

            weaponBleedChance =
                activeWeapon.BaseStats.BleedChance;

            effectiveRange =
                activeWeapon.TotalEffectiveRange;
        }

        float hitChance =
            CombatMath.CalculateHitChance(
                map,
                edificeStore,
                units,
                attackerId,
                posA.X,
                posA.Y,
                posB.X,
                posB.Y,
                posA.Z);

        bool isHit =
            Random.Shared.NextSingle() <=
            hitChance;

        if (units.WeaponSlot[attackerId]
            is Weapon shootingWeapon)
        {
            shootingWeapon.currentRecoil +=
                shootingWeapon.BaseStats.RecoilPerShot;
        }

        float dx =
            posB.X -
            posA.X;

        float dy =
            posB.Y -
            posA.Y;

        float distanceInTiles =
            MathF.Sqrt(
                dx * dx +
                dy * dy);

        // ================================================================
        // VISUAL ATTACK POSITION
        // ================================================================

        float attackerRenderX =
            posA.RenderX +
            units.SeparationOffsetX[attackerId];

        float attackerRenderY =
            posA.RenderY +
            units.SeparationOffsetY[attackerId];

        if (units.IsAiming[attackerId] &&
            units.Movement[attackerId].State ==
                MovementState.InCover)
        {
            attackerRenderX +=
                units.LeanOffsetX[attackerId] *
                0.4f;

            attackerRenderY +=
                units.LeanOffsetY[attackerId] *
                0.4f;
        }

        float targetRenderX =
            posB.RenderX +
            units.SeparationOffsetX[targetId];

        float targetRenderY =
            posB.RenderY +
            units.SeparationOffsetY[targetId];

        SFML.System.Vector2f startPixels =
            new SFML.System.Vector2f(
                attackerRenderX *
                    microCellPixelSize +
                microCellPixelSize *
                    0.5f,

                attackerRenderY *
                    microCellPixelSize +
                microCellPixelSize *
                    0.5f);

        SFML.System.Vector2f endPixels;

        int finalHitUnitId = -1;

        int hitX =
            posB.X;

        int hitY =
            posB.Y;

        if (isHit)
        {
            finalHitUnitId =
                targetId;

            endPixels =
                new SFML.System.Vector2f(
                    targetRenderX *
                        microCellPixelSize +
                    microCellPixelSize *
                        0.5f,

                    targetRenderY *
                        microCellPixelSize +
                    microCellPixelSize *
                        0.5f);
        }
        else
        {
            int scatterRadius =
                distanceInTiles >
                effectiveRange
                    ? 2
                    : 1;

            int scatterX =
                Random.Shared.Next(
                    -scatterRadius,
                    scatterRadius + 1);

            int scatterY =
                Random.Shared.Next(
                    -scatterRadius,
                    scatterRadius + 1);

            if (scatterX == 0 &&
                scatterY == 0)
            {
                scatterX =
                    scatterRadius;
            }

            int maxCoord =
                MapLayer.WidthInRegions *
                MapRegion.MicroSize - 1;

            hitX =
                Math.Clamp(
                    posB.X + scatterX,
                    0,
                    maxCoord);

            hitY =
                Math.Clamp(
                    posB.Y + scatterY,
                    0,
                    maxCoord);

            endPixels =
                new SFML.System.Vector2f(
                    hitX *
                        microCellPixelSize +
                    microCellPixelSize *
                        0.5f,

                    hitY *
                        microCellPixelSize +
                    microCellPixelSize *
                        0.5f);

            var unitsInScatterCell =
                spatialGrid.GetUnitsAt(
                    new SpatialCoord(
                        hitX,
                        hitY,
                        posA.Z));

            if (unitsInScatterCell.Count > 0)
            {
                finalHitUnitId =
                    unitsInScatterCell[0];
            }
        }

        float finalCalculatedDamage =
            BulletPhysicsCalculator
                .CalculateDamageAtDistance(
                    distanceInTiles,
                    effectiveRange,
                    baseDamage);

        byte damageToApply =
            (byte)Math.Clamp(
                finalCalculatedDamage,
                1,
                255);

        if (finalHitUnitId != -1)
        {
            _damageSystem.ApplyDamage(
                units,
                finalHitUnitId,
                attackerId,
                damageToApply,
                weaponBleedChance,
                map);

            if (effectSystem != null &&
                microCellPixelSize > 0f)
            {
                effectSystem.AddTracer(
                    startPixels,
                    endPixels,
                    posA.Z,
                    showCross: true,
                    duration: 0.35f);
            }
        }
        else
        {
            MapLayer layer =
                map.GetLayer(
                    posA.Z);

            if (layer != null)
            {
                ref MicroCell cell =
                    ref layer.GetMicroCell(
                        hitX,
                        hitY);

                if (cell.EdificeId > 0 &&
                    edificeStore != null &&
                    cell.EdificeId <
                        edificeStore.Instances.Length)
                {
                    ref var edifice =
                        ref edificeStore.Instances[
                            cell.EdificeId];

                    if (edifice.ConfigId <
                        edificeStore.Configs.Length)
                    {
                        var config =
                            edificeStore.Configs[
                                edifice.ConfigId];

                        if (config != null)
                        {
                            edifice.HitPoints -=
                                damageToApply;
                        }
                    }
                }
            }

            if (effectSystem != null &&
                microCellPixelSize > 0f)
            {
                effectSystem.AddTracer(
                    startPixels,
                    endPixels,
                    posA.Z,
                    showCross: false,
                    duration: 0.35f);
            }
        }
    }
}