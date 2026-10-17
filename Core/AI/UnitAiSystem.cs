using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Core.Combat;
using Core.Items;
using Core.Map;

namespace Core.Unit;

public sealed class UnitAiSystem
{
    private const float ThinkInterval = 0.15f;
    private const float TargetMemorySeconds = 3f;
    private const float LowHealthThreshold = 0.35f;
    private const float CoverSearchRadius = 8f;
    private const float CoverStep = 2f;
    private const int CoverDirectionCount = 8;
    private static readonly int MaxAiPlanningParallelism =
        Math.Max(1, Environment.ProcessorCount - 1);
    private static readonly ParallelOptions AiPlanningOptions = new()
    {
        MaxDegreeOfParallelism = MaxAiPlanningParallelism
    };

    private int[] _activeUnitScratch = Array.Empty<int>();
    private int[] _nearestVisibleEnemyByUnit = Array.Empty<int>();
    private Vector3[] _suppressionCoverByUnit = Array.Empty<Vector3>();
    private bool[] _hasSuppressionCoverByUnit = Array.Empty<bool>();

    private readonly UnitWeaponSystem _weaponSystem;
    private float _thinkTimer;

    public bool Enabled { get; set; }

    public UnitAiStore Store { get; }
    public double LastPlanningMilliseconds { get; private set; }

    public UnitAiSystem(
        int initialUnitCapacity = 1024,
        UnitWeaponSystem? weaponSystem = null)
    {
        Store =
            new UnitAiStore(
                initialUnitCapacity);

        _weaponSystem =
            weaponSystem ?? new UnitWeaponSystem();
    }

    public void Update(
        UnitStore units,
        UnitHealthStore health,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        UnitSuppressionStore suppression,
        VisionSystem vision,
        WorldMap worldMap,
        float deltaTime)
    {
        if (!Enabled ||
            deltaTime <= 0f ||
            units.ActiveCount == 0)
        {
            return;
        }

        _thinkTimer += deltaTime;

        if (_thinkTimer < ThinkInterval)
            return;

        float thinkDelta =
            _thinkTimer;

        _thinkTimer = 0f;

        Store.EnsureCapacity(
            units.Capacity);

        long planningStarted = Stopwatch.GetTimestamp();
        ReadOnlySpan<int> active =
            units.ActiveIndices;

        EnsurePlanningCapacity(units.Capacity);
        active.CopyTo(_activeUnitScratch);
        int activeCount = active.Length;

        if (activeCount >= 32 && MaxAiPlanningParallelism > 1)
        {
            Parallel.For(0, activeCount, AiPlanningOptions, i =>
                PrepareUnitPlan(
                    units, health, suppression, vision, worldMap,
                    _activeUnitScratch[i], thinkDelta));
        }
        else
        {
            for (int i = 0; i < activeCount; i++)
            {
                PrepareUnitPlan(
                    units, health, suppression, vision, worldMap,
                    _activeUnitScratch[i], thinkDelta);
            }
        }

        LastPlanningMilliseconds =
            Stopwatch.GetElapsedTime(planningStarted).TotalMilliseconds;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            Store.TickMemory(
                unit,
                thinkDelta);

            Store.SuppressionCoverRetry[unit] =
                MathF.Max(
                    0f,
                    Store.SuppressionCoverRetry[unit] - thinkDelta);

            if (health.OverallMaxHitPoints[unit] <= 0f ||
                health.OverallHitPoints[unit] <= 0f)
            {
                ClearAiming(weapons, unit);
                units.HasTarget[unit] = false;
                Store.ClearGoal(unit);
                Store.State[unit] = UnitAiState.Dead;
                continue;
            }

            UnitSuppressionState suppressionState =
                suppression.GetState(unit);

            if (suppressionState == UnitSuppressionState.Panicked ||
                suppressionState == UnitSuppressionState.Suppressed)
            {
                Vector3 suppressionSource =
                    suppression.Source[unit];

                Vector3 previousSource =
                    Store.SuppressionRetrySource[unit];

                float sourceDx =
                    suppressionSource.X - previousSource.X;

                float sourceDy =
                    suppressionSource.Y - previousSource.Y;

                bool threatChanged =
                    sourceDx * sourceDx + sourceDy * sourceDy >= 16f;

                // Failure to find cover must not trigger 32 terrain LOS rays
                // every AI tick. Retry once per second unless fire comes from
                // a substantially different position.
                if (!Store.HasGoal[unit] &&
                    (Store.SuppressionCoverRetry[unit] <= 0f ||
                     threatChanged))
                {
                    Store.SuppressionCoverRetry[unit] = 1f;
                    Store.SuppressionRetrySource[unit] =
                        suppressionSource;

                    if (_hasSuppressionCoverByUnit[unit])
                    {
                        Store.SetGoal(
                            unit,
                            _suppressionCoverByUnit[unit]);
                    }
                }

                if (Store.HasGoal[unit])
                {
                    ClearAiming(weapons, unit);
                    units.Target[unit] =
                        Store.Goal[unit];

                    units.HasTarget[unit] = true;

                    Store.State[unit] =
                        UnitAiState.SeekCover;

                    continue;
                }
            }

            int visibleEnemy =
                _nearestVisibleEnemyByUnit[unit];

            bool keepCurrentVisibleTarget = false;

            if (Store.HasTarget[unit])
            {
                UnitId currentTarget = Store.Target[unit];

                if (units.IsAlive(currentTarget) &&
                    health.OverallHitPoints[currentTarget.Index] > 0f &&
                    IsHostile(units, unit, currentTarget.Index) &&
                    vision.IsRecentlyVisible(unit, currentTarget.Index, units))
                {
                    // Keep the current target while it remains in recent LOS memory.
                    // The rotating vision sample must not make every soldier switch
                    // targets just because a different enemy was checked this tick.
                    Store.RememberTarget(
                        unit,
                        currentTarget,
                        units.Position[currentTarget.Index],
                        TargetMemorySeconds);
                    keepCurrentVisibleTarget = true;
                }
            }

            if (!keepCurrentVisibleTarget && visibleEnemy >= 0)
            {
                UnitId targetId = units.GetId(visibleEnemy);
                Store.RememberTarget(
                    unit,
                    targetId,
                    units.Position[visibleEnemy],
                    TargetMemorySeconds);
            }

            if (!Store.HasTarget[unit])
            {
                ClearAiming(weapons, unit);

                // Keep the scenario's existing march order until it is reached.
                // Afterward patrol forward and laterally instead of standing idle
                // forever waiting for an enemy to enter the current line of sight.
                if (!units.HasTarget[unit])
                {
                    if (!Store.HasGoal[unit] ||
                        ReachedGoal(units.Position[unit], Store.Goal[unit]))
                    {
                        Store.ClearGoal(unit);

                        if (TryFindSearchWaypoint(
                                units,
                                worldMap,
                                unit,
                                out Vector3 searchWaypoint))
                        {
                            Store.SetGoal(unit, searchWaypoint);
                        }
                    }

                    if (Store.HasGoal[unit])
                    {
                        units.Target[unit] = Store.Goal[unit];
                        units.HasTarget[unit] = true;
                        Store.State[unit] = UnitAiState.Search;
                    }
                    else
                    {
                        Store.State[unit] = UnitAiState.Idle;
                    }
                }
                else
                {
                    Store.State[unit] = UnitAiState.Search;
                }

                continue;
            }

            UnitId target =
                Store.Target[unit];

            if (!units.IsAlive(target) ||
                health.OverallHitPoints[target.Index] <= 0f)
            {
                ClearAiming(weapons, unit);
                Store.ClearTarget(unit);
                units.HasTarget[unit] = false;
                Store.State[unit] = UnitAiState.Idle;
                continue;
            }

            if (!IsHostile(
                    units,
                    unit,
                    target.Index))
            {
                ClearAiming(weapons, unit);
                Store.ClearTarget(unit);
                units.HasTarget[unit] = false;
                Store.State[unit] = UnitAiState.Idle;
                continue;
            }

            bool targetVisible =
                vision.IsRecentlyVisible(
                    unit,
                    target.Index,
                    units);

            if (!targetVisible)
            {
                // Vision uses a rotating, bounded candidate sample. A target can
                // temporarily leave that sample while remaining physically visible.
                // Do not restart aim on that bookkeeping gap; only cancel when an
                // exact terrain LOS check confirms the shot is actually blocked.
                ClearAimingIfOccluded(
                    units,
                    weapons,
                    vision,
                    worldMap,
                    unit,
                    target);
            }

            bool moving =
                units.Velocity[unit].LengthSquared() >
                0.04f;

            if (targetVisible &&
                !moving &&
                suppressionState != UnitSuppressionState.Panicked)
            {
                units.Posture[unit] =
                    UnitPosture.Crouching;
            }
            else if (moving &&
                     units.Posture[unit] ==
                     UnitPosture.Crouching)
            {
                units.Posture[unit] =
                    UnitPosture.Standing;
            }

            if (IsLowHealth(
                    health,
                    unit))
            {
                if (Store.HasGoal[unit] &&
                    ReachedGoal(
                        units.Position[unit],
                        Store.Goal[unit]))
                {
                    ClearAiming(weapons, unit);
                    units.HasTarget[unit] = false;
                    Store.State[unit] =
                        UnitAiState.SeekCover;
                    continue;
                }

                if (!Store.HasGoal[unit] &&
                    Store.SuppressionCoverRetry[unit] <= 0f)
                {
                    // A failed low-health cover search also costs up to 32 LOS
                    // traces. Keep the pawn responsive, but do not repeat the
                    // same expensive search on every 150 ms AI tick.
                    Store.SuppressionCoverRetry[unit] = 1f;

                    if (TryFindCover(
                            units,
                            vision,
                            worldMap,
                            unit,
                            target.Index,
                            out Vector3 cover))
                    {
                        Store.SetGoal(
                            unit,
                            cover);
                    }
                    else
                    {
                        Store.ClearGoal(unit);
                    }
                }

                if (Store.HasGoal[unit])
                {
                    ClearAiming(weapons, unit);
                    units.Target[unit] =
                        Store.Goal[unit];

                    units.HasTarget[unit] = true;

                    FaceTarget(
                        units,
                        unit,
                        target.Index);

                    Store.State[unit] =
                        UnitAiState.SeekCover;

                    continue;
                }
            }

            Store.ClearGoal(unit);

            if (targetVisible)
            {
                FaceTarget(
                    units,
                    unit,
                    target.Index);

                // Hold position while firing/aiming; do not continue walking
                // toward the previous patrol waypoint through the firing lane.
                units.Target[unit] = units.Position[unit];
                units.HasTarget[unit] = false;

                VisionCheck shotCheck = vision.Evaluate(
                    units,
                    worldMap,
                    unit,
                    target.Index);

                // Visibility lists are refreshed less often than movement. Recheck
                // before pulling the trigger so a stale target cannot cause wall fire.
                if (!shotCheck.IsVisible)
                {
                    // Forget stale LOS immediately. Suppressive weapons may
                    // keep firing at the last known point; the others must stop.
                    vision.ForgetVisibleTarget(unit, target.Index);

                    if (Store.TargetMemory[unit] > 0f &&
                        HasSuppressFireWeapon(inventory, weapons, unit))
                    {
                        ClearNonSuppressiveAiming(weapons, inventory, unit);
                        units.Target[unit] = units.Position[unit];
                        units.HasTarget[unit] = false;
                        Store.State[unit] = UnitAiState.Attack;
                        continue;
                    }

                    ClearAiming(weapons, unit);
                    Store.ClearTarget(unit);
                    units.HasTarget[unit] = false;
                    Store.State[unit] = UnitAiState.Search;
                    continue;
                }

                // Retain the last observed anatomical point; suppressive fire must
                // not reconstruct a target point from unit height after LOS is lost.
                Store.RememberAimPoint(unit, shotCheck.VisibleTargetPoint);

                // Aim independently for each weapon slot. TargetMode selects a
                // visible vertical anatomy layer, so one shared point would make
                // Head/Torso/Legs settings ineffective when the AI supplies it.
                bool fired =
                    TryFireAnyRangedWeapon(
                        units,
                        inventory,
                        weapons,
                        projectiles,
                        unit,
                        target,
                        vision,
                        worldMap,
                        suppression.GetAccuracyMultiplier(unit));

                if (fired)
                {
                    units.HasTarget[unit] =
                        HasSustainedFireWeapon(
                            inventory,
                            weapons,
                            unit);
                }
                else if (!HasRangedWeapon(
                             units,
                             inventory,
                             unit))
                {
                    units.Target[unit] =
                        units.Position[target.Index];

                    units.HasTarget[unit] = true;
                }
                else if (HasSustainedFireWeapon(
                             inventory,
                             weapons,
                             unit) ||
                         HasPendingAim(
                             units,
                             inventory,
                             weapons,
                             unit,
                             target))
                {
                    units.HasTarget[unit] = true;
                }
                else
                {
                    units.HasTarget[unit] = false;
                }

                Store.State[unit] =
                    UnitAiState.Attack;

                continue;
            }

            if (Store.TargetMemory[unit] > 0f)
            {
                if (HasSuppressFireWeapon(inventory, weapons, unit))
                {
                    Vector3 lastSeenAimPoint = Store.LastSeenAimPoint[unit];
                    if (lastSeenAimPoint == Vector3.Zero)
                        lastSeenAimPoint = Store.LastSeenPosition[unit];

                    TryFireSuppressiveWeapons(
                        units,
                        inventory,
                        weapons,
                        projectiles,
                        unit,
                        target,
                        lastSeenAimPoint,
                        suppression.GetAccuracyMultiplier(unit));

                    units.Target[unit] = units.Position[unit];
                    units.HasTarget[unit] = false;
                    Store.State[unit] = UnitAiState.Attack;
                }
                else
                {
                    units.Target[unit] = Store.LastSeenPosition[unit];
                    units.HasTarget[unit] = true;
                    Store.State[unit] = UnitAiState.Search;
                }

                continue;
            }

            // Target memory expired: stop suppressing stale coordinates and resume scouting.
            ClearAiming(weapons, unit);
            Store.ClearTarget(unit);
            Store.ClearGoal(unit);
            units.HasTarget[unit] = false;
            Store.State[unit] = UnitAiState.Search;
        }
    }

    private static bool TryFindSearchWaypoint(
        UnitStore units,
        WorldMap worldMap,
        int unit,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;

        Vector3 position = units.Position[unit];
        float centerX = worldMap.TileWidth * 0.5f;
        float centerY = worldMap.TileHeight * 0.5f;

        float direction = units.FactionTag[unit] switch
        {
            1 => 1f,
            2 => -1f,
            _ => position.X < centerX ? 1f : -1f
        };

        float distanceToCenter = (centerX - position.X) * direction;
        float targetX;

        if (distanceToCenter > 8f)
        {
            targetX = position.X + direction * MathF.Min(10f, distanceToCenter);
        }
        else
        {
            // Sweep across the center line while scanning a distinct lateral lane.
            float forwardPoint = centerX + direction * 5f;
            float reversePoint = centerX - direction * 5f;
            targetX = MathF.Abs(position.X - forwardPoint) <= 2f
                ? reversePoint
                : forwardPoint;
        }

        float laneY = centerY + ((unit % 13) - 6) * 4f;
        float targetY = position.Y + Math.Clamp(laneY - position.Y, -5f, 5f);

        targetX = Math.Clamp(targetX, 2f, worldMap.MaxTileX - 2f);
        targetY = Math.Clamp(targetY, 2f, worldMap.MaxTileY - 2f);

        int tileX = Math.Clamp((int)MathF.Floor(targetX), 0, worldMap.MaxTileX);
        int tileY = Math.Clamp((int)MathF.Floor(targetY), 0, worldMap.MaxTileY);

        waypoint = new Vector3(
            tileX + 0.5f,
            tileY + 0.5f,
            worldMap.GetSurfaceHeight(tileX, tileY));

        Vector2 delta = new Vector2(
            waypoint.X - position.X,
            waypoint.Y - position.Y);

        return delta.LengthSquared() > 1f;
    }

    private void EnsurePlanningCapacity(int capacity)
    {
        if (_activeUnitScratch.Length < capacity)
            Array.Resize(ref _activeUnitScratch, capacity);
        if (_nearestVisibleEnemyByUnit.Length < capacity)
            Array.Resize(ref _nearestVisibleEnemyByUnit, capacity);
        if (_suppressionCoverByUnit.Length < capacity)
            Array.Resize(ref _suppressionCoverByUnit, capacity);
        if (_hasSuppressionCoverByUnit.Length < capacity)
            Array.Resize(ref _hasSuppressionCoverByUnit, capacity);
    }

    private void PrepareUnitPlan(
        UnitStore units,
        UnitHealthStore health,
        UnitSuppressionStore suppression,
        VisionSystem vision,
        WorldMap worldMap,
        int unit,
        float thinkDelta)
    {
        _nearestVisibleEnemyByUnit[unit] = -1;
        _hasSuppressionCoverByUnit[unit] = false;

        if (health.OverallMaxHitPoints[unit] <= 0f ||
            health.OverallHitPoints[unit] <= 0f)
        {
            return;
        }

        _nearestVisibleEnemyByUnit[unit] =
            FindNearestVisibleEnemy(units, health, vision, unit);

        UnitSuppressionState state = suppression.GetState(unit);
        if ((state != UnitSuppressionState.Panicked &&
             state != UnitSuppressionState.Suppressed) ||
            Store.HasGoal[unit])
        {
            return;
        }

        Vector3 threat = suppression.Source[unit];
        Vector3 previousSource = Store.SuppressionRetrySource[unit];
        float sourceDx = threat.X - previousSource.X;
        float sourceDy = threat.Y - previousSource.Y;
        bool threatChanged = sourceDx * sourceDx + sourceDy * sourceDy >= 16f;

        // The sequential phase subtracts thinkDelta before checking the timer.
        // Precompute cover only when that same check would request a new search.
        if (Store.SuppressionCoverRetry[unit] > thinkDelta && !threatChanged)
            return;

        if (TryFindSuppressionCover(
                units, vision, worldMap, unit, threat, out Vector3 cover))
        {
            _suppressionCoverByUnit[unit] = cover;
            _hasSuppressionCoverByUnit[unit] = true;
        }
    }

    private static int FindNearestVisibleEnemy(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        int observer)
    {
        ReadOnlySpan<int> visible =
            vision.GetVisibleTargets(
                observer);

        ushort observerFaction =
            units.FactionTag[observer];

        Vector3 position =
            units.Position[observer];

        int bestTarget = -1;
        float bestDistanceSquared =
            float.PositiveInfinity;

        for (int i = 0;
             i < visible.Length;
             i++)
        {
            int target = visible[i];

            if ((uint)target >= (uint)units.Capacity ||
                health.OverallHitPoints[target] <= 0f ||
                !FactionRules.ShouldAttack(
                    observerFaction,
                    units.FactionTag[target]))
            {
                continue;
            }

            Vector3 delta =
                units.Position[target] -
                position;

            float distanceSquared =
                delta.X * delta.X +
                delta.Y * delta.Y;

            if (distanceSquared >=
                bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared =
                distanceSquared;

            bestTarget =
                target;
        }

        return bestTarget;
    }

    private static bool IsTargetVisible(
        ReadOnlySpan<int> visible,
        int target)
    {
        for (int i = 0;
             i < visible.Length;
             i++)
        {
            if (visible[i] == target)
                return true;
        }

        return false;
    }

    private static bool IsHostile(
        UnitStore units,
        int observer,
        int target)
    {
        return FactionRules.ShouldAttack(
            units.FactionTag[observer],
            units.FactionTag[target]);
    }

    private static bool IsLowHealth(
        UnitHealthStore health,
        int unit)
    {
        float max =
            health.OverallMaxHitPoints[unit];

        if (max <= 0f)
            return true;

        return health.OverallHitPoints[unit] /
               max <
               LowHealthThreshold;
    }

    private static bool HasRangedWeapon(
        UnitStore units,
        UnitInventoryStore inventory,
        int unit)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            short inventorySlot =
                inventory.GetWeaponEquipment(
                    unit,
                    (UnitWeaponSlot)slot);

            if (inventorySlot < 0)
                continue;

            if (inventory.GetItem(
                    unit,
                    inventorySlot)
                is RangedWeaponConfig)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFireAnyRangedWeapon(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        int unit,
        UnitId target,
        VisionSystem vision,
        WorldMap worldMap,
        float accuracyMultiplier)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            if (UnitWeaponStore.GetIndex(
                    unit,
                    (UnitWeaponSlot)slot) < 0)
            {
                continue;
            }

            if (inventory.GetWeaponEquipment(
                    unit,
                    (UnitWeaponSlot)slot) < 0)
            {
                continue;
            }

            if (UnitWeaponStore.GetIndex(
                    unit,
                    (UnitWeaponSlot)slot) < 0)
                continue;

            // UnitWeaponSystem will reject melee/non-ranged slots.
            // Try the equipped slots in priority order.
            UnitWeaponSlot weaponSlot =
                (UnitWeaponSlot)slot;

            int stateIndex = UnitWeaponStore.GetIndex(unit, weaponSlot);
            if (weapons.Cooldown[stateIndex] > 0f)
                continue;

            VisionCheck targetCheck = vision.EvaluateTargetMode(
                units,
                worldMap,
                unit,
                target.Index,
                weapons.CurrentTargetMode[stateIndex]);

            if (!targetCheck.IsVisible)
                continue;

            if (TryFire(
                    units,
                    inventory,
                    weapons,
                    projectiles,
                    unit,
                    weaponSlot,
                    target,
                    targetCheck.VisibleTargetPoint,
                    accuracyMultiplier))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFire(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        int unit,
        UnitWeaponSlot slot,
        UnitId target,
        Vector3 aimPoint,
        float accuracyMultiplier)
    {
        short inventorySlot =
            inventory.GetWeaponEquipment(
                unit,
                slot);

        if (inventorySlot < 0)
            return false;

        if (inventory.GetItem(
                unit,
                inventorySlot) is not RangedWeaponConfig)
        {
            return false;
        }

        return _weaponSystem.TryFireAt(
            units,
            inventory,
            weapons,
            projectiles,
            units.GetId(unit),
            slot,
            target,
            accuracyMultiplier,
            aimPoint);
    }

    private bool TryFireSuppressiveWeapons(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        ProjectileStore projectiles,
        int unit,
        UnitId target,
        Vector3 aimPoint,
        float accuracyMultiplier)
    {
        bool attempted = false;

        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
            short inventorySlot = inventory.GetWeaponEquipment(unit, weaponSlot);

            if (inventorySlot < 0 ||
                inventory.GetItem(unit, inventorySlot) is not RangedWeaponConfig)
            {
                continue;
            }

            int stateIndex = UnitWeaponStore.GetIndex(unit, weaponSlot);
            if (weapons.CurrentAimMode[stateIndex] != AimMode.SuppressFire)
                continue;

            attempted = true;

            // This deliberately updates the stored aim point even while the
            // weapon is on cooldown. Auto fire can then keep suppressing that
            // last-known position without requiring a fresh visible target.
            Vector3 suppressPoint =
                weapons.AimTarget[stateIndex] == target &&
                weapons.AimPoint[stateIndex] != Vector3.Zero
                    ? weapons.AimPoint[stateIndex]
                    : aimPoint;

            _weaponSystem.TryFireAt(
                units,
                inventory,
                weapons,
                projectiles,
                units.GetId(unit),
                weaponSlot,
                target,
                accuracyMultiplier,
                suppressPoint);
        }

        return attempted;
    }

    private static bool HasSuppressFireWeapon(
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        int unit)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
            short inventorySlot = inventory.GetWeaponEquipment(unit, weaponSlot);

            if (inventorySlot < 0 ||
                inventory.GetItem(unit, inventorySlot) is not RangedWeaponConfig)
            {
                continue;
            }

            if (weapons.CurrentAimMode[
                    UnitWeaponStore.GetIndex(unit, weaponSlot)] == AimMode.SuppressFire)
            {
                return true;
            }
        }

        return false;
    }

    internal static void ClearAimingIfOccluded(
        UnitStore units,
        UnitWeaponStore weapons,
        VisionSystem vision,
        WorldMap worldMap,
        int unit,
        UnitId target)
    {
        Vector3 eye = vision.GetEyePosition(units, unit);

        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
            int stateIndex = UnitWeaponStore.GetIndex(unit, weaponSlot);

            if (weapons.AimTarget[stateIndex] != target ||
                weapons.CurrentAimMode[stateIndex] == AimMode.SuppressFire)
            {
                continue;
            }

            Vector3 aimPoint = weapons.AimPoint[stateIndex];
            if (aimPoint == Vector3.Zero ||
                !vision.HasLineOfSight(worldMap, eye, aimPoint, out _))
            {
                weapons.ClearAim(unit, weaponSlot);
            }
        }
    }

    private static void ClearNonSuppressiveAiming(
        UnitWeaponStore weapons,
        UnitInventoryStore inventory,
        int unit)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
            short inventorySlot = inventory.GetWeaponEquipment(unit, weaponSlot);

            if (inventorySlot >= 0 &&
                inventory.GetItem(unit, inventorySlot) is RangedWeaponConfig &&
                weapons.CurrentAimMode[
                    UnitWeaponStore.GetIndex(unit, weaponSlot)] == AimMode.SuppressFire)
            {
                continue;
            }

            weapons.ClearAim(unit, weaponSlot);
        }
    }

    private static bool HasPendingAim(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        int unit,
        UnitId target)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            short inventorySlot =
                inventory.GetWeaponEquipment(
                    unit,
                    (UnitWeaponSlot)slot);

            if (inventorySlot < 0)
                continue;

            RangedWeaponConfig? weapon =
                inventory.GetItem(
                    unit,
                    inventorySlot) as RangedWeaponConfig;

            if (weapon == null)
                continue;

            int stateIndex =
                UnitWeaponStore.GetIndex(
                    unit,
                    (UnitWeaponSlot)slot);

            if (weapons.CurrentAimMode[stateIndex] !=
                Core.Combat.AimMode.AimedShot)
            {
                continue;
            }

            if (weapons.AimTarget[stateIndex] == target &&
                weapons.AimTimer[stateIndex] <
                MathF.Max(
                    0f,
                    weapon.AimTime))
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearAiming(
        UnitWeaponStore weapons,
        int unit)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            weapons.ClearAim(unit, (UnitWeaponSlot)slot);
        }
    }

    private static bool HasSustainedFireWeapon(
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        int unit)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
            short inventorySlot =
                inventory.GetWeaponEquipment(unit, weaponSlot);

            if (inventorySlot < 0 ||
                inventory.GetItem(unit, inventorySlot) is not RangedWeaponConfig)
            {
                continue;
            }

            int stateIndex = UnitWeaponStore.GetIndex(unit, weaponSlot);

            if (weapons.CurrentFireMode[stateIndex] != FireMode.Single ||
                weapons.BurstRemaining[stateIndex] > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void FaceTarget(
        UnitStore units,
        int unit,
        int target)
    {
        Vector3 delta =
            units.Position[target] -
            units.Position[unit];

        delta.Z = 0f;

        float lengthSquared =
            delta.LengthSquared();

        if (lengthSquared < 0.000001f)
            return;

        units.HeadNormal[unit] =
            delta /
            MathF.Sqrt(
                lengthSquared);
    }

    private static bool TryFindSuppressionCover(
        UnitStore units,
        VisionSystem vision,
        WorldMap worldMap,
        int unit,
        Vector3 threat,
        out Vector3 cover)
    {
        cover = Vector3.Zero;

        Vector3 origin =
            units.Position[unit];

        float bestDistanceSquared =
            float.PositiveInfinity;

        for (int direction = 0;
             direction < CoverDirectionCount;
             direction++)
        {
            float angle =
                direction *
                MathF.Tau /
                CoverDirectionCount;

            Vector2 offset =
                new Vector2(
                    MathF.Cos(angle),
                    MathF.Sin(angle));

            for (float radius = CoverStep;
                 radius <= CoverSearchRadius;
                 radius += CoverStep)
            {
                Vector3 candidate =
                    origin +
                    new Vector3(
                        offset.X * radius,
                        offset.Y * radius,
                        0f);

                int tileX =
                    (int)MathF.Floor(candidate.X);

                int tileY =
                    (int)MathF.Floor(candidate.Y);

                if (tileX < 1 ||
                    tileY < 1 ||
                    tileX >= worldMap.TileWidth - 1 ||
                    tileY >= worldMap.TileHeight - 1)
                    continue;

                candidate.Z =
                    worldMap.GetSurfaceHeight(
                        tileX,
                        tileY);

                Vector3 candidateEye =
                    candidate +
                    new Vector3(
                        0f,
                        0f,
                        MathF.Max(
                            0.05f,
                            units.Height[unit]));

                Vector3 threatPoint =
                    threat;

                threatPoint.Z =
                    MathF.Max(
                        threatPoint.Z,
                        units.Height[unit]);

                if (vision.HasLineOfSight(
                        worldMap,
                        candidateEye,
                        threatPoint,
                        out _))
                {
                    continue;
                }

                float distanceSquared =
                    (candidate.X - origin.X) *
                    (candidate.X - origin.X) +
                    (candidate.Y - origin.Y) *
                    (candidate.Y - origin.Y);

                if (distanceSquared >= bestDistanceSquared)
                    continue;

                bestDistanceSquared =
                    distanceSquared;

                cover =
                    candidate;
            }
        }

        return bestDistanceSquared <
               float.PositiveInfinity;
    }

    private static bool ReachedGoal(
        Vector3 position,
        Vector3 goal)
    {
        Vector2 delta =
            new Vector2(
                goal.X - position.X,
                goal.Y - position.Y);

        return delta.LengthSquared() <=
               0.50f * 0.50f;
    }

    private static bool TryFindCover(
        UnitStore units,
        VisionSystem vision,
        WorldMap worldMap,
        int unit,
        int enemy,
        out Vector3 cover)
    {
        cover =
            Vector3.Zero;

        Vector3 origin =
            units.Position[unit];

        Vector3 enemyEye =
            vision.GetEyePosition(
                units,
                enemy);

        float bestDistanceSquared =
            float.PositiveInfinity;

        for (int direction = 0;
             direction < CoverDirectionCount;
             direction++)
        {
            float angle =
                direction *
                MathF.Tau /
                CoverDirectionCount;

            Vector2 offset =
                new Vector2(
                    MathF.Cos(angle),
                    MathF.Sin(angle));

            for (float radius = CoverStep;
                 radius <= CoverSearchRadius;
                 radius += CoverStep)
            {
                Vector3 candidate =
                    origin +
                    new Vector3(
                        offset.X * radius,
                        offset.Y * radius,
                        0f);

                int tileX =
                    (int)MathF.Floor(
                        candidate.X);

                int tileY =
                    (int)MathF.Floor(
                        candidate.Y);

                if (tileX < 1 ||
                    tileY < 1 ||
                    tileX >= worldMap.TileWidth - 1 ||
                    tileY >= worldMap.TileHeight - 1)
                {
                    continue;
                }

                candidate.Z =
                    worldMap.GetSurfaceHeight(
                        tileX,
                        tileY);

                Vector3 candidateEye =
                    candidate +
                    new Vector3(
                        0f,
                        0f,
                        MathF.Max(
                            0.05f,
                            units.Height[unit]));

                if (vision.HasLineOfSight(
                        worldMap,
                        candidateEye,
                        enemyEye,
                        out _))
                {
                    continue;
                }

                float distanceSquared =
                    offset.X * radius *
                    offset.X * radius +
                    offset.Y * radius *
                    offset.Y * radius;

                if (distanceSquared >=
                    bestDistanceSquared)
                {
                    continue;
                }

                bestDistanceSquared =
                    distanceSquared;

                cover =
                    candidate;
            }
        }

        return bestDistanceSquared <
               float.PositiveInfinity;
    }
}
