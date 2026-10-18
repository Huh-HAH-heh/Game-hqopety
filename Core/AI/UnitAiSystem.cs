using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Core.AI.Behaviac;
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
        Math.Max(1, Math.Min(2, Environment.ProcessorCount - 2));
    private static readonly ParallelOptions AiPlanningOptions = new()
    {
        MaxDegreeOfParallelism = MaxAiPlanningParallelism
    };

    private int[] _activeUnitScratch = Array.Empty<int>();
    private int[] _nearestVisibleEnemyByUnit = Array.Empty<int>();
    private int[] _searchPatrolLegByUnit = Array.Empty<int>();
    private Vector3[] _suppressionCoverByUnit = Array.Empty<Vector3>();
    private bool[] _hasSuppressionCoverByUnit = Array.Empty<bool>();
    private BehaviacUnitDecisionAgent?[] _behaviacAgents = Array.Empty<BehaviacUnitDecisionAgent?>();
    private UnitId[] _flankTargetByUnit = Array.Empty<UnitId>();
    private bool[] _hasFlankTargetByUnit = Array.Empty<bool>();
    private bool _behaviacInitialized;

    private readonly UnitWeaponSystem _weaponSystem;
    private readonly UnitSkillStore? _skills;
    private float _thinkTimer;

    public bool Enabled { get; set; }
    public bool LastUpdateRanThinkPass { get; private set; }

    /// <summary>
    /// Uses Behaviac for high-level unit intent selection.
    /// Enabled by default on this experiment branch; set RIMCLONE_USE_BEHAVIAC_AI=0 for legacy AI.
    /// </summary>
    public bool UseBehaviacDecisionLayer { get; set; }

    public UnitAiStore Store { get; }
    public double LastPlanningMilliseconds { get; private set; }

    public bool BehaviacRuntimeReady => _behaviacInitialized;
    public long TotalBehaviacDecisions { get; private set; }
    public int LastBehaviacDecisions { get; private set; }
    public double LastBehaviacDecisionBatchMilliseconds { get; private set; }
    public int LastBehaviacDeadDecisions { get; private set; }
    public int LastBehaviacCoverDecisions { get; private set; }
    public int LastBehaviacRetreatDecisions { get; private set; }
    public int LastBehaviacFlankDecisions { get; private set; }
    public int LastBehaviacEngageDecisions { get; private set; }
    public int LastBehaviacSuppressDecisions { get; private set; }
    public int LastBehaviacInvestigateDecisions { get; private set; }
    public int LastBehaviacPatrolDecisions { get; private set; }

    public UnitAiSystem(
        int initialUnitCapacity = 1024,
        UnitWeaponSystem? weaponSystem = null,
        UnitSkillStore? skills = null)
    {
        Store =
            new UnitAiStore(
                initialUnitCapacity);

        _weaponSystem =
            weaponSystem ?? new UnitWeaponSystem();
        _skills = skills;

        // New branch uses Behaviac by default. Set RIMCLONE_USE_BEHAVIAC_AI=0
        // to compare against the legacy decision path.
        UseBehaviacDecisionLayer = !string.Equals(
            Environment.GetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI"),
            "0",
            StringComparison.Ordinal);
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
        LastUpdateRanThinkPass = false;

        if (!Enabled ||
            deltaTime <= 0f ||
            units.ActiveCount == 0)
        {
            LastPlanningMilliseconds = 0d;
            return;
        }

        _thinkTimer += deltaTime;

        if (_thinkTimer < ThinkInterval)
            return;

        float thinkDelta =
            _thinkTimer;

        _thinkTimer = 0f;
        LastUpdateRanThinkPass = true;

        // Preserve these results between think ticks so the one-second
        // performance logger doesn't usually sample a zero-work render frame.
        LastPlanningMilliseconds = 0d;
        LastBehaviacDecisions = 0;
        LastBehaviacDecisionBatchMilliseconds = 0d;
        LastBehaviacDeadDecisions = 0;
        LastBehaviacCoverDecisions = 0;
        LastBehaviacRetreatDecisions = 0;
        LastBehaviacFlankDecisions = 0;
        LastBehaviacEngageDecisions = 0;
        LastBehaviacSuppressDecisions = 0;
        LastBehaviacInvestigateDecisions = 0;
        LastBehaviacPatrolDecisions = 0;

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

            UnitSuppressionState suppressionState =
                suppression.GetState(unit);

            bool isAlive =
                health.OverallMaxHitPoints[unit] > 0f &&
                health.OverallHitPoints[unit] > 0f;

            BehaviacUnitIntent intent = BehaviacUnitIntent.None;
            if (UseBehaviacDecisionLayer)
            {
                long decisionStarted = Stopwatch.GetTimestamp();
                intent = SelectUnitIntent(
                    units, health, inventory, weapons, vision, unit, suppressionState, isAlive);
                LastBehaviacDecisionBatchMilliseconds +=
                    Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
                RecordBehaviacIntent(intent);
            }

            if (!isAlive ||
                (UseBehaviacDecisionLayer && intent == BehaviacUnitIntent.Dead))
            {
                _hasFlankTargetByUnit[unit] = false;
                _flankTargetByUnit[unit] = default;
                ClearAiming(weapons, unit);
                units.HasTarget[unit] = false;
                Store.ClearGoal(unit);
                Store.State[unit] = UnitAiState.Dead;
                units.Posture[unit] = UnitPosture.Lying;
                continue;
            }

            if (UseBehaviacDecisionLayer &&
                intent != BehaviacUnitIntent.FlankTarget &&
                Store.State[unit] == UnitAiState.Flank)
            {
                // If the tree interrupts a flanking run (lost target, suppression,
                // or a tactical change), cancel the stale destination immediately.
                Store.ClearGoal(unit);
                Store.State[unit] = UnitAiState.Search;
            }

            bool moving = units.Velocity[unit].LengthSquared() > 0.04f;
            if (moving)
                units.Posture[unit] = UnitPosture.Standing;
            else if (suppressionState == UnitSuppressionState.Panicked)
                units.Posture[unit] = UnitPosture.Lying;
            else if (suppressionState == UnitSuppressionState.Suppressed)
                units.Posture[unit] = UnitPosture.Crouching;

            if (suppressionState == UnitSuppressionState.Panicked &&
                Store.HasGoal[unit] &&
                Store.State[unit] != UnitAiState.SeekCover)
            {
                // Don't let a patrol or flank waypoint masquerade as a cover goal
                // when suppression changes the selected behavior.
                Store.ClearGoal(unit);
                units.HasTarget[unit] = false;
            }

            if (suppressionState == UnitSuppressionState.Panicked)
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

                if (Store.HasGoal[unit] &&
                    (!UseBehaviacDecisionLayer ||
                     intent == BehaviacUnitIntent.SeekCover))
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

            // The tree can select Patrol only when there is neither a visible
            // target nor a remembered target. Drop an expired target before the
            // existing patrol/search path runs.
            if (UseBehaviacDecisionLayer &&
                intent == BehaviacUnitIntent.Patrol &&
                Store.HasTarget[unit] &&
                Store.TargetMemory[unit] <= 0f)
            {
                ClearAiming(weapons, unit);
                Store.ClearTarget(unit);
                units.HasTarget[unit] = false;
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

                        int patrolLeg = ++_searchPatrolLegByUnit[unit];
                        if (TryFindSearchWaypoint(
                                units,
                                worldMap,
                                unit,
                                patrolLeg,
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
                        if (!moving && suppressionState == UnitSuppressionState.Calm)
                            units.Posture[unit] = UnitPosture.Sitting;
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

            if (moving)
            {
                units.Posture[unit] = UnitPosture.Standing;
            }
            else if (suppressionState == UnitSuppressionState.Panicked)
            {
                units.Posture[unit] = UnitPosture.Lying;
            }
            else if (suppressionState == UnitSuppressionState.Suppressed)
            {
                units.Posture[unit] = UnitPosture.Crouching;
            }
            else if (targetVisible)
            {
                // A stationary pawn switches to a stable seated firing posture.
                units.Posture[unit] = UnitPosture.Sitting;
            }
            else if (units.Posture[unit] == UnitPosture.Lying)
            {
                // Recover from the prone suppression pose after the threat passes.
                units.Posture[unit] = UnitPosture.Standing;
            }

            if (IsLowHealth(
                    health,
                    unit) &&
                (!UseBehaviacDecisionLayer ||
                 intent == BehaviacUnitIntent.RetreatToCover))
            {
                if (UseBehaviacDecisionLayer &&
                    Store.State[unit] != UnitAiState.SeekCover)
                {
                    Store.ClearGoal(unit);
                }

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

            if (UseBehaviacDecisionLayer &&
                targetVisible &&
                intent == BehaviacUnitIntent.FlankTarget)
            {
                bool targetChanged = !_hasFlankTargetByUnit[unit] ||
                    !_flankTargetByUnit[unit].Equals(target);

                if (Store.State[unit] != UnitAiState.Flank ||
                    !Store.HasGoal[unit] ||
                    targetChanged)
                {
                    Store.ClearGoal(unit);
                    _flankTargetByUnit[unit] = target;
                    _hasFlankTargetByUnit[unit] = true;
                    if (TryFindFlankWaypoint(
                            units,
                            worldMap,
                            unit,
                            target.Index,
                            out Vector3 flankWaypoint))
                    {
                        Store.SetGoal(unit, flankWaypoint);
                    }
                }

                if (Store.HasGoal[unit] &&
                    !ReachedGoal(units.Position[unit], Store.Goal[unit]))
                {
                    ClearAiming(weapons, unit);
                    units.Target[unit] = Store.Goal[unit];
                    units.HasTarget[unit] = true;
                    Store.State[unit] = UnitAiState.Flank;
                    continue;
                }

                // Mark this target as already flanked so the unit doesn't oscillate
                // between a firing position and another flank waypoint every tick.
                Store.ClearGoal(unit);
                Store.State[unit] = UnitAiState.Attack;
                intent = BehaviacUnitIntent.EngageTarget;
            }
            else
            {
                Store.ClearGoal(unit);
            }

            if (targetVisible &&
                (!UseBehaviacDecisionLayer ||
                 intent == BehaviacUnitIntent.EngageTarget))
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
                        GetSpreadMultiplier(units, suppression, unit));

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

            if (Store.TargetMemory[unit] > 0f &&
                (!UseBehaviacDecisionLayer ||
                 intent == BehaviacUnitIntent.SuppressiveFire ||
                 intent == BehaviacUnitIntent.InvestigateLastKnownTarget))
            {
                if ((!UseBehaviacDecisionLayer ||
                     intent == BehaviacUnitIntent.SuppressiveFire) &&
                    HasSuppressFireWeapon(inventory, weapons, unit))
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
                        GetSpreadMultiplier(units, suppression, unit));

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

    private void RecordBehaviacIntent(BehaviacUnitIntent intent)
    {
        LastBehaviacDecisions++;
        TotalBehaviacDecisions++;

        switch (intent)
        {
            case BehaviacUnitIntent.Dead: LastBehaviacDeadDecisions++; break;
            case BehaviacUnitIntent.SeekCover: LastBehaviacCoverDecisions++; break;
            case BehaviacUnitIntent.RetreatToCover: LastBehaviacRetreatDecisions++; break;
            case BehaviacUnitIntent.FlankTarget: LastBehaviacFlankDecisions++; break;
            case BehaviacUnitIntent.EngageTarget: LastBehaviacEngageDecisions++; break;
            case BehaviacUnitIntent.SuppressiveFire: LastBehaviacSuppressDecisions++; break;
            case BehaviacUnitIntent.InvestigateLastKnownTarget: LastBehaviacInvestigateDecisions++; break;
            case BehaviacUnitIntent.Patrol: LastBehaviacPatrolDecisions++; break;
        }
    }

    private BehaviacUnitIntent SelectUnitIntent(
        UnitStore units,
        UnitHealthStore health,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        VisionSystem vision,
        int unit,
        UnitSuppressionState suppressionState,
        bool isAlive)
    {
        if (!_behaviacInitialized)
        {
            BehaviacRuntimeHost.InitializeDefaultBehavior();
            _behaviacInitialized = true;
        }

        if (_behaviacAgents.Length < units.Capacity)
            Array.Resize(ref _behaviacAgents, units.Capacity);

        BehaviacUnitDecisionAgent? agent = _behaviacAgents[unit];
        if (agent is null)
        {
            agent = BehaviacRuntimeHost.CreateAgent<BehaviacUnitDecisionAgent>(
                "rimclone_unit_decision");
            _behaviacAgents[unit] = agent;
        }

        int visibleTargetIndex = _nearestVisibleEnemyByUnit[unit];
        if (Store.HasTarget[unit])
        {
            UnitId currentTarget = Store.Target[unit];
            if (units.IsAlive(currentTarget) &&
                health.OverallHitPoints[currentTarget.Index] > 0f &&
                IsHostile(units, unit, currentTarget.Index) &&
                vision.IsRecentlyVisible(unit, currentTarget.Index, units))
            {
                // Don't change an ongoing maneuver just because the rotating vision
                // sampler found a different enemy in the same tick.
                visibleTargetIndex = currentTarget.Index;
            }
        }

        bool hasVisibleTarget = visibleTargetIndex >= 0;
        bool isSuppressed =
            suppressionState is UnitSuppressionState.Panicked or UnitSuppressionState.Suppressed;
        float healthRatio = health.OverallMaxHitPoints[unit] > 0f
            ? health.OverallHitPoints[unit] / health.OverallMaxHitPoints[unit]
            : 0f;
        bool isLowHealth = healthRatio < LowHealthThreshold;
        bool hasSuppressiveWeapon = HasSuppressFireWeapon(inventory, weapons, unit);
        bool isHealthyEnoughToFlank = !isLowHealth && !isSuppressed;
        bool canFlank = false;

        if (hasVisibleTarget && isHealthyEnoughToFlank && unit % 3 == 0)
        {
            Vector3 delta = units.Position[visibleTargetIndex] - units.Position[unit];
            float distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            bool alreadyFlankedThisTarget = _hasFlankTargetByUnit[unit] &&
                _flankTargetByUnit[unit].Equals(units.GetId(visibleTargetIndex));

            // Keep a small dedicated flanking cohort, but let it commit before
            // frontal units collapse into close range. The 12 m floor makes
            // the maneuver visible in the live mass-combat scenario without
            // allowing already-engaged units to repeatedly circle one target.
            float maxFlankRange = MathF.Min(75f, MathF.Max(25f, units.ViewRange[unit] * 0.85f));
            canFlank = !alreadyFlankedThisTarget &&
                distanceSquared >= 12f * 12f &&
                distanceSquared <= maxFlankRange * maxFlankRange;
        }

        bool isFlanking =
            Store.State[unit] == UnitAiState.Flank &&
            isHealthyEnoughToFlank;

        agent.SetFacts(
            isAlive,
            suppressionState == UnitSuppressionState.Panicked,
            _hasSuppressionCoverByUnit[unit] ||
                (Store.HasGoal[unit] && Store.State[unit] == UnitAiState.SeekCover),
            hasVisibleTarget,
            Store.HasTarget[unit] && Store.TargetMemory[unit] > 0f,
            isLowHealth,
            hasSuppressiveWeapon,
            canFlank,
            isFlanking);

        behaviac.EBTStatus status = BehaviacRuntimeHost.Tick(agent);
        if (status != behaviac.EBTStatus.BT_SUCCESS)
        {
            throw new InvalidOperationException(
                $"Behaviac unit decision failed for unit index {unit}: {status}.");
        }

        return agent.Intent;
    }

    private static bool TryFindFlankWaypoint(
        UnitStore units,
        WorldMap worldMap,
        int unit,
        int targetIndex,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;
        Vector3 position = units.Position[unit];
        Vector3 target = units.Position[targetIndex];
        Vector2 towardTarget = new(target.X - position.X, target.Y - position.Y);
        float lengthSquared = towardTarget.LengthSquared();
        if (lengthSquared < 1f)
            return false;

        Vector2 forward = Vector2.Normalize(towardTarget);
        Vector2 side = new(-forward.Y, forward.X);
        float preferredSide = (unit & 1) == 0 ? 1f : -1f;

        // Try both sides and a shallower side-step if the preferred point is blocked.
        // This reduces stalls near walls while keeping the maneuver deterministic.
        for (int candidate = 0; candidate < 4; candidate++)
        {
            float sideSign = candidate % 2 == 0 ? preferredSide : -preferredSide;
            float backoff = candidate < 2 ? 8f : 16f;
            float lateral = candidate < 2 ? 12f : 18f;
            float targetX = target.X - forward.X * backoff + side.X * lateral * sideSign;
            float targetY = target.Y - forward.Y * backoff + side.Y * lateral * sideSign;
            targetX = Math.Clamp(targetX, 2f, worldMap.MaxTileX - 2f);
            targetY = Math.Clamp(targetY, 2f, worldMap.MaxTileY - 2f);

            int tileX = Math.Clamp((int)MathF.Floor(targetX), 0, worldMap.MaxTileX);
            int tileY = Math.Clamp((int)MathF.Floor(targetY), 0, worldMap.MaxTileY);
            if (worldMap.IsNavigationBlocked(tileX, tileY))
                continue;

            Vector3 candidateWaypoint = new(
                tileX + 0.5f,
                tileY + 0.5f,
                worldMap.GetSurfaceHeight(tileX, tileY));
            Vector2 toWaypoint = new(
                candidateWaypoint.X - position.X,
                candidateWaypoint.Y - position.Y);
            if (toWaypoint.LengthSquared() <= 4f)
                continue;

            waypoint = candidateWaypoint;
            return true;
        }

        return false;
    }

    private static bool TryFindSearchWaypoint(
        UnitStore units,
        WorldMap worldMap,
        int unit,
        int patrolLeg,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;
        Vector3 position = units.Position[unit];

        // Continue through the centre instead of oscillating between centreX +/- 5.
        float direction = units.FactionTag[unit] switch
        {
            1 => 1f,
            2 => -1f,
            _ => position.X < worldMap.TileWidth * 0.5f ? 1f : -1f
        };
        if (position.X <= 8f)
            direction = 1f;
        else if (position.X >= worldMap.MaxTileX - 8f)
            direction = -1f;

        uint seed = unchecked(
            (uint)(unit + 1) * 73856093u ^
            (uint)(patrolLeg + 1) * 19349663u);
        float forwardDistance = 5f + (seed & 7u) * 0.55f;
        float lateralDistance = ((int)((seed >> 4) % 9u) - 4) * 1.15f;
        float desiredX = Math.Clamp(
            position.X + direction * forwardDistance,
            2f,
            worldMap.MaxTileX - 2f);
        float desiredY = Math.Clamp(
            position.Y + lateralDistance,
            2f,
            worldMap.MaxTileY - 2f);

        // A* chooses traversable cells, but the destination retains a stable
        // sub-cell offset. Idle units therefore don't stack on cell centres.
        float fractionX = 0.22f + ((seed >> 8) % 57u) * 0.01f;
        float fractionY = 0.22f + ((seed >> 16) % 57u) * 0.01f;
        int desiredCellX = Math.Clamp((int)MathF.Floor(desiredX), 1, worldMap.MaxTileX - 1);
        int desiredCellY = Math.Clamp((int)MathF.Floor(desiredY), 1, worldMap.MaxTileY - 1);

        float bestDistance = float.PositiveInfinity;
        Vector3 best = Vector3.Zero;
        for (int radius = 0; radius <= 6; radius++)
        {
            for (int offsetY = -radius; offsetY <= radius; offsetY++)
            {
                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    if (Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) != radius)
                        continue;

                    int tileX = desiredCellX + offsetX;
                    int tileY = desiredCellY + offsetY;
                    if (tileX < 1 || tileY < 1 ||
                        tileX >= worldMap.TileWidth - 1 ||
                        tileY >= worldMap.TileHeight - 1 ||
                        worldMap.IsNavigationBlocked(tileX, tileY) ||
                        worldMap.GetSurfaceLayer(tileX, tileY) < 0)
                    {
                        continue;
                    }

                    float x = tileX + fractionX;
                    float y = tileY + fractionY;
                    float dx = x - desiredX;
                    float dy = y - desiredY;
                    float score = dx * dx + dy * dy;
                    if (score >= bestDistance)
                        continue;

                    bestDistance = score;
                    best = new Vector3(x, y, worldMap.GetSurfaceHeight(tileX, tileY));
                }
            }

            if (bestDistance < float.PositiveInfinity)
                break;
        }

        if (!float.IsFinite(bestDistance))
            return false;

        Vector2 delta = new(best.X - position.X, best.Y - position.Y);
        if (delta.LengthSquared() <= 1f)
            return false;

        waypoint = best;
        return true;
    }

    private void EnsurePlanningCapacity(int capacity)
    {
        if (_activeUnitScratch.Length < capacity)
            Array.Resize(ref _activeUnitScratch, capacity);
        if (_nearestVisibleEnemyByUnit.Length < capacity)
            Array.Resize(ref _nearestVisibleEnemyByUnit, capacity);
        if (_searchPatrolLegByUnit.Length < capacity)
            Array.Resize(ref _searchPatrolLegByUnit, capacity);
        if (_suppressionCoverByUnit.Length < capacity)
            Array.Resize(ref _suppressionCoverByUnit, capacity);
        if (_hasSuppressionCoverByUnit.Length < capacity)
            Array.Resize(ref _hasSuppressionCoverByUnit, capacity);
        if (_flankTargetByUnit.Length < capacity)
            Array.Resize(ref _flankTargetByUnit, capacity);
        if (_hasFlankTargetByUnit.Length < capacity)
            Array.Resize(ref _hasFlankTargetByUnit, capacity);
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
        if (state != UnitSuppressionState.Panicked ||
            (Store.HasGoal[unit] && Store.State[unit] == UnitAiState.SeekCover))
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

    private float GetSpreadMultiplier(
        UnitStore units,
        UnitSuppressionStore suppression,
        int unit)
    {
        return Math.Clamp(suppression.GetAccuracyMultiplier(unit), 1f, 1.55f);
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

        // Don't switch patrol legs half a cell before the current target.
        // Let the movement controller reach its fractional sub-cell destination.
        return delta.LengthSquared() <=
               0.08f * 0.08f;
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
