using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using System;
using System.Collections.Generic;

namespace Core.Unit.Systems
{
    public sealed class UnitCombatSystem
    {
        private readonly List<int> _nearbyCandidates =
            new List<int>(32);

        private readonly UnitDamageSystem _damageSystem =
            new UnitDamageSystem();

        // ============================================================
        // AI SCAN CACHE
        // ============================================================

        private float[] _targetScanTimers =
            Array.Empty<float>();

        private float[] _targetLosTimers =
            Array.Empty<float>();

        private const float TargetScanInterval = 0.20f;
        private const float TargetLosInterval = 0.10f;

        // Очень важно:
        // Не даём обычному AI каждый кадр сканировать квадрат 300x300.
        // Это именно радиус поиска цели, а не радиус самого оружия.
        private const int MaxTargetScanRadius = 60;

        private const float MeleeThreatRadius = 2.5f;

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

            EnsureStorage(
                units.Count
            );

            // ========================================================
            // 1. ДЕШЕВЫЕ ТАЙМЕРЫ / ОТДАЧА
            // ========================================================

            for (int i = 0;
                 i < units.Count;
                 i++)
            {
                if (units.HealthMasks[i] == 0)
                    continue;

                if (_targetScanTimers[i] > 0f)
                {
                    _targetScanTimers[i] -= deltaTime;

                    if (_targetScanTimers[i] < 0f)
                        _targetScanTimers[i] = 0f;
                }

                if (_targetLosTimers[i] > 0f)
                {
                    _targetLosTimers[i] -= deltaTime;

                    if (_targetLosTimers[i] < 0f)
                        _targetLosTimers[i] = 0f;
                }

                if (units.ShotCooldowns[i] > 0f)
                {
                    units.ShotCooldowns[i] -= deltaTime;

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

            // ========================================================
            // 2. БОЕВОЙ ИИ
            // ========================================================

            ExecuteRimWorldCombat(
                units,
                spatialGrid,
                map,
                edificeStore,
                effectSystem,
                microCellPixelSize,
                deltaTime
            );
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
            for (int i = 0;
                 i < units.Count;
                 i++)
            {
                if (units.HealthMasks[i] == 0)
                    continue;

                ref var posA =
                    ref units.Positions[i];

                ref var moveA =
                    ref units.Movement[i];

                int currentTargetId =
                    units.CurrentTargets[i];

                int currentAttackRange =
                    GetAttackRange(
                        units,
                        i
                    );

                // ====================================================
                // 1. ПРОВЕРЯЕМ СУЩЕСТВУЮЩУЮ ЦЕЛЬ
                // ====================================================

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
                    }
                    else
                    {
                        ref var targetPos =
                            ref units.Positions[currentTargetId];

                        if (targetPos.Spatial.Z != posA.Spatial.Z)
                        {
                            units.CurrentTargets[i] = -1;
                            currentTargetId = -1;
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
                                    dy * dy
                                );

                            int visionRadius =
                                Math.Min(
                                    currentAttackRange + 5,
                                    MaxTargetScanRadius
                                );

                            if (distance > visionRadius)
                            {
                                units.CurrentTargets[i] = -1;
                                currentTargetId = -1;
                            }
                            else if (_targetLosTimers[i] <= 0f)
                            {
                                bool visible =
                                    CombatPath.VisibilityChecker.HasLineOfSight(
                                        map,
                                        edificeStore,
                                        posA.X,
                                        posA.Y,
                                        targetPos.X,
                                        targetPos.Y,
                                        posA.Z
                                    );

                                _targetLosTimers[i] =
                                    TargetLosInterval;

                                if (!visible)
                                {
                                    units.CurrentTargets[i] = -1;
                                    currentTargetId = -1;
                                }
                            }
                        }
                    }
                }

                // ====================================================
                // 2. ИЩЕМ НОВУЮ ЦЕЛЬ ТОЛЬКО КОГДА НУЖНО
                // ====================================================

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
                            currentAttackRange
                        );

                    units.CurrentTargets[i] =
                        currentTargetId;

                    // ==================================================
                    // РАВНОМЕРНО РАСКИДЫВАЕМ НАГРУЗКУ
                    //
                    // Каждый юнит получает немного другой момент
                    // следующего сканирования.
                    // ==================================================

                    _targetScanTimers[i] =
                        TargetScanInterval *
                        GetScanJitter(i);
                }

                // ====================================================
                // 3. ЕСЛИ ЦЕЛИ НЕТ — СТРЕЛЬБЫ НЕТ
                // ====================================================

                int finalTarget =
                    units.CurrentTargets[i];

                if (finalTarget < 0 ||
                    finalTarget >= units.Count)
                {
                    ResetAiming(
                        units,
                        i,
                        ref posA
                    );

                    continue;
                }

                if (units.HealthMasks[finalTarget] == 0)
                {
                    units.CurrentTargets[i] = -1;

                    ResetAiming(
                        units,
                        i,
                        ref posA
                    );

                    continue;
                }

                ref var targetPosFinal =
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
                        fdy * fdy
                    );

                if (distanceInTiles >
                    currentAttackRange)
                {
                    continue;
                }

                // ====================================================
                // 4. BURST
                // ====================================================

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
                            microCellPixelSize
                        );

                        units.RemainingBurstShots[i]--;

                        if (units.RemainingBurstShots[i] <= 0)
                        {
                            units.IsAiming[i] = false;

                            posA.RenderX =
                                posA.X;

                            posA.RenderY =
                                posA.Y;

                            units.ShotCooldowns[i] =
                                units.WeaponSlot[i] != null
                                    ? units.WeaponSlot[i].BaseStats.FireRate
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

                // ====================================================
                // 5. НАЧАЛО ПРИЦЕЛИВАНИЯ
                // ====================================================

                bool canShoot =
                    units.WeaponSlot[i] != null;

                if (!units.IsAiming[i] &&
                    units.ShotCooldowns[i] <= 0f &&
                    canShoot)
                {
                    units.IsAiming[i] =
                        true;

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
                                posA.X
                            );

                        units.LeanOffsetY[i] =
                            Math.Sign(
                                targetPosFinal.Y -
                                posA.Y
                            );
                    }

                    continue;
                }

                // ====================================================
                // 6. ПРИЦЕЛИВАНИЕ
                // ====================================================

                if (units.IsAiming[i] &&
                    units.RemainingBurstShots[i] <= 0)
                {
                    units.AimingTimers[i] -=
                        deltaTime;

                    if (moveA.State ==
                        MovementState.InCover)
                    {
                        posA.RenderX =
                            posA.X +
                            units.LeanOffsetX[i] *
                            0.4f;

                        posA.RenderY =
                            posA.Y +
                            units.LeanOffsetY[i] *
                            0.4f;
                    }

                    if (units.AimingTimers[i] <= 0f)
                    {
                        int burstCount =
                            1;

                        if (units.WeaponSlot[i] != null)
                        {
                            burstCount =
                                units.WeaponSlot[i]
                                    .GetBurstCountForDistance(
                                        distanceInTiles
                                    );
                        }

                        units.RemainingBurstShots[i] =
                            burstCount;

                        units.AimingTimers[i] =
                            0f;
                    }
                }
            }
        }

        // ============================================================
        // ПОИСК БЛИЖАЙШЕГО ВРАГА
        // ============================================================

        private int FindClosestEnemy(
            UnitStore units,
            UnitSpatialGrid spatialGrid,
            WorldMap map,
            EdificeStore edificeStore,
            int unitId,
            int attackRange)
        {
            ref var posA =
                ref units.Positions[unitId];

            int visionRadius =
                Math.Min(
                    attackRange + 5,
                    MaxTargetScanRadius
                );

            _nearbyCandidates.Clear();

            spatialGrid.GetNearby(
                posA.Spatial,
                visionRadius,
                _nearbyCandidates
            );

            int bestTarget =
                -1;

            float bestDistance =
                float.MaxValue;

            for (int idx = 0;
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

                ref var posB =
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
                    if (!CombatPath.VisibilityChecker.HasLineOfSight(
                        map,
                        edificeStore,
                        posA.X,
                        posA.Y,
                        posB.X,
                        posB.Y,
                        posA.Z))
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

                // LOS делаем только для реально ближайшего кандидата.
                if (distanceSqr >= bestDistance)
                    continue;

                if (!CombatPath.VisibilityChecker.HasLineOfSight(
                    map,
                    edificeStore,
                    posA.X,
                    posA.Y,
                    posB.X,
                    posB.Y,
                    posA.Z))
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

        // ============================================================
        // РАСЧЁТ ДАЛЬНОСТИ
        // ============================================================

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
                    (int)range
                )
            );
        }

        // ============================================================
        // НЕБОЛЬШОЙ ДЕТЕРМИНИРОВАННЫЙ JITTER
        // ============================================================

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

        // ============================================================
        // СБРОС ПРИЦЕЛИВАНИЯ
        // ============================================================

        private void ResetAiming(
            UnitStore units,
            int unitId,
            ref UnitPosition position)
        {
            units.IsAiming[unitId] =
                false;

            units.RemainingBurstShots[unitId] =
                0;

            position.RenderX =
                position.X;

            position.RenderY =
                position.Y;
        }

        // ============================================================
        // ARRAY STORAGE
        // ============================================================

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
                        oldSize * 2
                    )
                );

            Array.Resize(
                ref _targetScanTimers,
                newSize
            );

            Array.Resize(
                ref _targetLosTimers,
                newSize
            );

            for (int i = oldSize;
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

        // ============================================================
        // ATTACK
        // ============================================================

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
                return;

            if (targetId < 0 ||
                targetId >= units.Count)
                return;

            if (units.HealthMasks[attackerId] == 0 ||
                units.HealthMasks[targetId] == 0)
                return;

            ref var posA =
                ref units.Positions[attackerId];

            ref var posB =
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
                CombatPath.CombatMath.CalculateHitChance(
                    map,
                    edificeStore,
                    units,
                    attackerId,
                    posA.X,
                    posA.Y,
                    posB.X,
                    posB.Y,
                    posA.Z
                );

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
                    dy * dy
                );

            SFML.System.Vector2f startPixels =
                new SFML.System.Vector2f(
                    posA.RenderX *
                        microCellPixelSize +
                    microCellPixelSize *
                        0.5f,

                    posA.RenderY *
                        microCellPixelSize +
                    microCellPixelSize *
                        0.5f
                );

            SFML.System.Vector2f endPixels;

            int finalHitUnitId =
                -1;

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
                        posB.RenderX *
                            microCellPixelSize +
                        microCellPixelSize *
                            0.5f,

                        posB.RenderY *
                            microCellPixelSize +
                        microCellPixelSize *
                            0.5f
                    );
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
                        scatterRadius + 1
                    );

                int scatterY =
                    Random.Shared.Next(
                        -scatterRadius,
                        scatterRadius + 1
                    );

                if (scatterX == 0 &&
                    scatterY == 0)
                {
                    scatterX =
                        scatterRadius;
                }

                int maxCoord =
                    (16 * 48) - 1;

                hitX =
                    Math.Clamp(
                        posB.X + scatterX,
                        0,
                        maxCoord
                    );

                hitY =
                    Math.Clamp(
                        posB.Y + scatterY,
                        0,
                        maxCoord
                    );

                endPixels =
                    new SFML.System.Vector2f(
                        hitX *
                            microCellPixelSize +
                        microCellPixelSize *
                            0.5f,

                        hitY *
                            microCellPixelSize +
                        microCellPixelSize *
                            0.5f
                    );

                var unitsInScatterCell =
                    spatialGrid.GetUnitsAt(
                        new SpatialCoord(
                            hitX,
                            hitY,
                            posA.Z
                        )
                    );

                if (unitsInScatterCell.Count > 0)
                {
                    finalHitUnitId =
                        unitsInScatterCell[0];
                }
            }

            float finalCalculatedDamage =
                CombatPath
                    .BulletPhysicsCalculator
                    .CalculateDamageAtDistance(
                        distanceInTiles,
                        effectiveRange,
                        baseDamage
                    );

            byte damageToApply =
                (byte)Math.Clamp(
                    finalCalculatedDamage,
                    1,
                    255
                );

            if (finalHitUnitId != -1)
            {
                _damageSystem.ApplyDamage(
                    units,
                    finalHitUnitId,
                    attackerId,
                    damageToApply,
                    weaponBleedChance,
                    map
                );

                if (effectSystem != null &&
                    microCellPixelSize > 0f)
                {
                    effectSystem.AddTracer(
                        startPixels,
                        endPixels,
                        posA.Z,
                        showCross: true,
                        duration: 0.35f
                    );
                }
            }
            else
            {
                MapLayer layer =
                    map.GetLayer(
                        posA.Z
                    );

                if (layer != null)
                {
                    ref MicroCell cell =
                        ref layer.GetMicroCell(
                            hitX,
                            hitY
                        );

                    if (cell.EdificeId > 0)
                    {
                        ref var edifice =
                            ref edificeStore
                                .Instances[
                                    cell.EdificeId
                                ];

                        if (edificeStore.Configs[
                                edifice.ConfigId] != null)
                        {
                            edifice.HitPoints -=
                                damageToApply;
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
                        duration: 0.35f
                    );
                }
            }
        }
    }
}