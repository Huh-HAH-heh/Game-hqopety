using System;
using System.Numerics;
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

    private readonly UnitWeaponSystem _weaponSystem;
    private float _thinkTimer;

    public bool Enabled { get; set; }

    public UnitAiStore Store { get; }

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

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            Store.TickMemory(
                unit,
                thinkDelta);

            if (health.OverallMaxHitPoints[unit] <= 0f ||
                health.OverallHitPoints[unit] <= 0f)
            {
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
                if (!Store.HasGoal[unit] &&
                    TryFindSuppressionCover(
                        units,
                        vision,
                        worldMap,
                        unit,
                        suppression.Source[unit],
                        out Vector3 suppressionCover))
                {
                    Store.SetGoal(
                        unit,
                        suppressionCover);
                }

                if (Store.HasGoal[unit])
                {
                    units.Target[unit] =
                        Store.Goal[unit];

                    units.HasTarget[unit] = true;

                    Store.State[unit] =
                        UnitAiState.SeekCover;

                    continue;
                }
            }

            int visibleEnemy =
                FindNearestVisibleEnemy(
                    units,
                    health,
                    vision,
                    unit);

            if (visibleEnemy >= 0)
            {
                UnitId targetId =
                    units.GetId(
                        visibleEnemy);

                Store.RememberTarget(
                    unit,
                    targetId,
                    units.Position[visibleEnemy],
                    TargetMemorySeconds);
            }

            if (!Store.HasTarget[unit])
            {
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
                Store.ClearTarget(unit);
                units.HasTarget[unit] = false;
                Store.State[unit] = UnitAiState.Idle;
                continue;
            }

            bool targetVisible =
                IsTargetVisible(
                    vision.GetVisibleTargets(unit),
                    target.Index);

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
                    units.HasTarget[unit] = false;
                    Store.State[unit] =
                        UnitAiState.SeekCover;
                    continue;
                }

                if (!Store.HasGoal[unit])
                {
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

                Vector3 aimPoint = ChooseVisibleAimPoint(
                    units,
                    worldMap,
                    vision,
                    unit,
                    target.Index,
                    shotCheck.VisibleTargetPoint);

                bool fired =
                    TryFireAnyRangedWeapon(
                        units,
                        inventory,
                        weapons,
                        projectiles,
                        unit,
                        target,
                        aimPoint,
                        suppression.GetAccuracyMultiplier(unit));

                if (fired)
                {
                    units.HasTarget[unit] =
                        HasSustainedFireWeapon(
                            inventory,
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
                units.Target[unit] =
                    Store.LastSeenPosition[unit];

                units.HasTarget[unit] = true;
                Store.State[unit] =
                    UnitAiState.Search;
                continue;
            }

            // Target memory expired: discard the stale target and resume scouting.
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

    private static Vector3 ChooseVisibleAimPoint(
        UnitStore units,
        WorldMap worldMap,
        VisionSystem vision,
        int shooter,
        int target,
        Vector3 visiblePoint)
    {
        Vector3 eye = vision.GetEyePosition(units, shooter);
        Vector3 targetPosition = units.Position[target];
        float targetHeight = MathF.Max(0.1f, units.Height[target]);

        // Prefer an interior torso/upper-body point. The outermost vision point
        // sits on the top silhouette and may be outside the ballistic hit volume.
        Span<float> heightFractions = stackalloc float[5]
        {
            0.55f, 0.66f, 0.45f, 0.72f, 0.22f
        };

        for (int i = 0; i < heightFractions.Length; i++)
        {
            Vector3 candidate = targetPosition +
                new Vector3(0f, 0f, targetHeight * heightFractions[i]);

            if (vision.HasLineOfSight(worldMap, eye, candidate, out _))
                return candidate;
        }

        // If only the top silhouette is visible, lower the aim a little while
        // retaining a proven terrain-visible point.
        Vector3 fallback = visiblePoint;
        fallback.Z = MathF.Max(
            targetPosition.Z + targetHeight * 0.66f,
            visiblePoint.Z - targetHeight * 0.08f);

        return vision.HasLineOfSight(worldMap, eye, fallback, out _)
            ? fallback
            : visiblePoint;
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
            int target =
                visible[i];

            if (health.OverallHitPoints[target] <= 0f ||
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
        Vector3 aimPoint,
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

            if (weapons.Cooldown[
                    UnitWeaponStore.GetIndex(
                        unit,
                        weaponSlot)] > 0f)
            {
                continue;
            }

            if (TryFire(
                    units,
                    inventory,
                    weapons,
                    projectiles,
                    unit,
                    weaponSlot,
                    target,
                    aimPoint,
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

    private static bool HasSustainedFireWeapon(
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

            RangedWeaponConfig? weapon =
                inventory.GetItem(
                    unit,
                    inventorySlot) as RangedWeaponConfig;

            if (weapon == null)
                continue;

            if (weapon.DefaultFireMode !=
                Core.Combat.FireMode.Single)
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
