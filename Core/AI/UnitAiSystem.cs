using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Core.AI;
using Core.AI.Behaviac;
using Core.AI.Perception;
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
    private const float ProneCrawlFiringDistance = 9f;
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
    private readonly PerceptionMemoryStore? _perceptionMemory;
    private float _thinkTimer;
    private float _tacticalTimeSeconds;
    private readonly SquadTacticalCoordinator _squadCoordinator = new();

    public bool Enabled { get; set; }
    public bool LastUpdateRanThinkPass { get; private set; }

    /// <summary>
    /// Optional Behaviac high-level intent selection. Disabled by default; set
    /// RIMCLONE_USE_BEHAVIAC_AI=1 to opt in. The legacy AI remains active when disabled.
    /// </summary>
    public bool UseBehaviacDecisionLayer { get; set; }

    public TacticalHearingSystem? Hearing { get; set; }
    public UnitAiStore Store { get; }
    public SquadTacticalCoordinator SquadCoordinator => _squadCoordinator;

    public void SynchronizeSquadMemberships(IReadOnlyList<SquadMembershipSnapshot> squads) =>
        _squadCoordinator.SynchronizeMemberships(squads);

    public void InvalidateSquadUnit(UnitId unit) =>
        _squadCoordinator.InvalidateUnit(unit);
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
    public int LastBehaviacInvestigateDecisions { get; private set; }
    public int LastBehaviacMoveOrderDecisions { get; private set; }
    public int LastBehaviacHoldOrderDecisions { get; private set; }
    public int LastBehaviacAttackOrderDecisions { get; private set; }
    public int LastBehaviacHoldAndEngageDecisions { get; private set; }

    public UnitAiSystem(
        int initialUnitCapacity = 1024,
        UnitWeaponSystem? weaponSystem = null,
        UnitSkillStore? skills = null,
        PerceptionMemoryStore? perceptionMemory = null)
    {
        Store =
            new UnitAiStore(
                initialUnitCapacity);

        _weaponSystem =
            weaponSystem ?? new UnitWeaponSystem();
        _skills = skills;
        _perceptionMemory = perceptionMemory;

        // Keep the Behaviac tree, actions, and tactical utility scores in source,
        // but execute them only when explicitly enabled for an experiment.
        UseBehaviacDecisionLayer = string.Equals(
            Environment.GetEnvironmentVariable("RIMCLONE_USE_BEHAVIAC_AI"),
            "1",
            StringComparison.Ordinal);
    }

    public void InitializeUnit(int unitIndex)
    {
        EnsurePlanningCapacity(unitIndex + 1);
        _searchPatrolLegByUnit[unitIndex] = 0;
        _flankTargetByUnit[unitIndex] = default;
        _hasFlankTargetByUnit[unitIndex] = false;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_searchPatrolLegByUnit.Length)
            return;

        _searchPatrolLegByUnit[unitIndex] = 0;
        _flankTargetByUnit[unitIndex] = default;
        _hasFlankTargetByUnit[unitIndex] = false;
        if ((uint)unitIndex < (uint)_behaviacAgents.Length)
            _behaviacAgents[unitIndex] = null;
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
        LastBehaviacInvestigateDecisions = 0;
        LastBehaviacMoveOrderDecisions = 0;
        LastBehaviacHoldOrderDecisions = 0;
        LastBehaviacAttackOrderDecisions = 0;
        LastBehaviacHoldAndEngageDecisions = 0;

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

        _tacticalTimeSeconds += thinkDelta;
        _squadCoordinator.Advance(_tacticalTimeSeconds);

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

            bool reachedSuppressionCover = Store.State[unit] == UnitAiState.SeekCover &&
                Store.HasGoal[unit] &&
                ReachedGoal(units.Position[unit], Store.Goal[unit]);
            suppression.SetInCover(unit, reachedSuppressionCover);

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
                    units, health, inventory, weapons, vision, suppression, unit, suppressionState, isAlive);
                LastBehaviacDecisionBatchMilliseconds +=
                    Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;

                RecordBehaviacIntent(intent);
            }

            Store.LastIntent[unit] = intent;

            if (!isAlive ||
                (UseBehaviacDecisionLayer && intent == BehaviacUnitIntent.Dead))
            {
                _hasFlankTargetByUnit[unit] = false;
                _flankTargetByUnit[unit] = default;
                ClearAiming(weapons, unit);
                units.HasTarget[unit] = false;
                Store.ClearGoal(unit);
                Store.ClearPlayerOrder(unit);
                Store.State[unit] = UnitAiState.Dead;
                units.Posture[unit] = UnitPosture.Lying;
                continue;
            }

            if (UseBehaviacDecisionLayer &&
                intent == BehaviacUnitIntent.Panic &&
                suppressionState == UnitSuppressionState.Panicked)
            {
                // If no cover can be reached, panic interrupts firing and movement
                // orders; retain target memory so the soldier can recover and re-engage.
                ClearAiming(weapons, unit);
                for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
                    weapons.StartBurst(unit, (UnitWeaponSlot)slot, default, 0, 0f);
                Store.ClearGoal(unit);
                units.Target[unit] = units.Position[unit];
                units.HasTarget[unit] = false;
                units.Posture[unit] = UnitPosture.Lying;
                Store.State[unit] = UnitAiState.Idle;
                continue;
            }

            if (UseBehaviacDecisionLayer &&
                suppressionState != UnitSuppressionState.Panicked &&
                TryExecutePlayerOrderIntent(
                    units, health, weapons, vision, worldMap, unit, intent))
            {
                continue;
            }

            if (UseBehaviacDecisionLayer &&
                suppressionState != UnitSuppressionState.Panicked &&
                intent == BehaviacUnitIntent.SeekCover)
            {
                // A non-panicked but suppressed/low-health unit may still seek
                // cover under a Hold order. Move to the planned cover, then let
                // the tree resume HoldPositionEngage once it is reached.
                if (!Store.HasGoal[unit] && _hasSuppressionCoverByUnit[unit])
                    Store.SetGoal(unit, _suppressionCoverByUnit[unit]);

                if (Store.HasGoal[unit] &&
                    !ReachedGoal(units.Position[unit], Store.Goal[unit]))
                {
                    ClearAiming(weapons, unit);
                    units.Target[unit] = Store.Goal[unit];
                    units.HasTarget[unit] = true;
                    Store.State[unit] = UnitAiState.SeekCover;
                    continue;
                }
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
            if (UseBehaviacDecisionLayer && intent == BehaviacUnitIntent.ProneEngage)
                units.Posture[unit] = UnitPosture.Lying;
            else if (suppressionState == UnitSuppressionState.Panicked)
                units.Posture[unit] = moving ? UnitPosture.Crouching : UnitPosture.Lying;
            else if (moving)
                units.Posture[unit] = UnitPosture.Standing;
            else if (suppressionState == UnitSuppressionState.Suppressed)
                units.Posture[unit] = UnitPosture.Crouching;
            else if (units.Posture[unit] == UnitPosture.Lying)
                units.Posture[unit] = UnitPosture.Standing;

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

            if (UseBehaviacDecisionLayer &&
                intent == BehaviacUnitIntent.InvestigateAudibleContact &&
                TryExecuteAudibleInvestigation(units, weapons, worldMap, unit))
            {
                continue;
            }

            // Attack orders are objectives, not hard target locks. Keep the
            // nearest visible hostile selected so soldiers can switch threats.
            int visibleEnemy = _nearestVisibleEnemyByUnit[unit];

            bool keepCurrentVisibleTarget = false;

            if (Store.HasTarget[unit] &&
                !(Store.PlayerOrderType[unit] == UnitAiOrderType.Attack &&
                  units.IsAlive(Store.PlayerOrderTarget[unit]) &&
                  health.OverallHitPoints[Store.PlayerOrderTarget[unit].Index] > 0f &&
                  IsHostile(units, unit, Store.PlayerOrderTarget[unit].Index)))
            {
                UnitId currentTarget = Store.Target[unit];

                if (ShouldKeepCurrentVisibleTarget(
                        units, health, vision, inventory, unit, currentTarget, visibleEnemy))
                {
                    // Preserve target continuity, except when a nearer close contact
                    // must interrupt a long-range target.
                    Store.RememberTarget(
                        unit,
                        currentTarget,
                        GetObservedTargetPosition(units, unit, currentTarget.Index),
                        TargetMemorySeconds);
                    keepCurrentVisibleTarget = true;
                }
            }

            if (!keepCurrentVisibleTarget && visibleEnemy >= 0)
            {
                UnitId targetId = units.GetId(visibleEnemy);
                if (!Store.HasTarget[unit] ||
                    !Store.Target[unit].Equals(targetId))
                {
                    // A fresh enemy contact starts a new, short search leash.
                    _searchPatrolLegByUnit[unit] = 0;
                }

                Store.RememberTarget(
                    unit,
                    targetId,
                    GetObservedTargetPosition(units, unit, visibleEnemy),
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
                HasFreshVisualContact(unit, target, out _) &&
                vision.IsRecentlyVisible(
                    unit,
                    target.Index,
                    units);
            VisionCheck currentTargetShotCheck = default;
            bool hasCurrentTargetShotCheck = false;
            if (targetVisible)
            {
                // Do not move into a firing ring around a remembered/stale contact.
                // Confirm the target can still actually be seen from this position.
                currentTargetShotCheck = vision.Evaluate(
                    units, worldMap, unit, target.Index);
                hasCurrentTargetShotCheck = true;
                targetVisible = currentTargetShotCheck.IsVisible;
            }

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

            if (UseBehaviacDecisionLayer && intent == BehaviacUnitIntent.ProneEngage)
            {
                // Stay prone while crawling to, holding, or firing from the chosen position.
                units.Posture[unit] = UnitPosture.Lying;
            }
            else if (suppressionState == UnitSuppressionState.Panicked)
            {
                // Keep a low profile while retreating, but do not force a crawl.
                units.Posture[unit] = moving ? UnitPosture.Crouching : UnitPosture.Lying;
            }
            else if (moving)
            {
                units.Posture[unit] = UnitPosture.Standing;
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
                // Recover from a temporary prone posture after the threat passes.
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
                    _hasSuppressionCoverByUnit[unit])
                {
                    // PrepareUnitPlan may have found cover after the retry
                    // cooldown because the threat moved; consume that candidate
                    // even when the cooldown is still active.
                    Store.SetGoal(unit, _suppressionCoverByUnit[unit]);
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
                        Store.SetGoal(unit, cover);
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
                intent == BehaviacUnitIntent.FlankTarget &&
                !_squadCoordinator.TryClaimFlank(units.GetId(unit), target, _tacticalTimeSeconds))
            {
                // One soldier handles the flank; the rest retain their local combat decisions.
                intent = BehaviacUnitIntent.EngageTarget;
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
                            GetMaxEffectiveRange(inventory, unit),
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
            else if (UseBehaviacDecisionLayer &&
                     targetVisible &&
                     intent == BehaviacUnitIntent.ProneEngage)
            {
                // Replace a patrol/cover destination on the first prone-engage
                // tick, but retain the crawl goal on subsequent thinking ticks.
                if (Store.State[unit] != UnitAiState.Attack)
                    Store.ClearGoal(unit);
            }
            else
            {
                Store.ClearGoal(unit);
            }

            if (targetVisible &&
                (!UseBehaviacDecisionLayer ||
                 intent is BehaviacUnitIntent.EngageTarget or BehaviacUnitIntent.ProneEngage or
                     BehaviacUnitIntent.HoldPositionEngage))
            {
                // A prone soldier crawls slowly toward a low firing position at
                // about nine metres. A hold order selects HoldPositionEngage instead,
                // so the crawl never overrides an explicit hold task.
                if (intent == BehaviacUnitIntent.ProneEngage)
                {
                    Vector3 pronePosition = units.Position[unit];
                    Vector3 proneTarget = units.Position[target.Index];
                    Vector2 proneDelta = new(
                        proneTarget.X - pronePosition.X,
                        proneTarget.Y - pronePosition.Y);
                    float proneDistanceSquared = proneDelta.LengthSquared();
                    float crawlDistanceSquared =
                        ProneCrawlFiringDistance * ProneCrawlFiringDistance;

                    if (proneDistanceSquared > crawlDistanceSquared)
                    {
                        if (!Store.HasGoal[unit] ||
                            Store.State[unit] != UnitAiState.Attack ||
                            ReachedGoal(pronePosition, Store.Goal[unit]))
                        {
                            Store.ClearGoal(unit);
                            if (TryFindProneCrawlWaypoint(
                                    units,
                                    worldMap,
                                    unit,
                                    target.Index,
                                    out Vector3 crawlWaypoint))
                            {
                                Store.SetGoal(unit, crawlWaypoint);
                            }
                        }

                        if (Store.HasGoal[unit] &&
                            !ReachedGoal(pronePosition, Store.Goal[unit]))
                        {
                            ClearAiming(weapons, unit);
                            units.Target[unit] = Store.Goal[unit];
                            units.HasTarget[unit] = true;
                            Store.State[unit] = UnitAiState.Attack;
                            continue;
                        }
                    }
                    else
                    {
                        Store.ClearGoal(unit);
                    }
                }

                if (intent != BehaviacUnitIntent.HoldPositionEngage &&
                    HasRangedWeapon(units, inventory, unit))
                {
                    Vector3 position = units.Position[unit];
                    Vector3 targetPosition = units.Position[target.Index];
                    float range = GetMaxEffectiveRange(inventory, unit);
                    if (range > 0.1f)
                    {
                        if (TryGetRangedEngagementMoveTarget(
                                position, targetPosition, range, unit, target.Index,
                                out Vector3 engagementPosition))
                        {
                            units.Target[unit] = engagementPosition;
                            units.HasTarget[unit] = true;
                            ClearAiming(weapons, unit);
                            Store.ClearGoal(unit);
                            Store.State[unit] = UnitAiState.Attack;
                            continue;
                        }
                    }
                }

                // Melee-only units approach the edge of their attack ring instead
                // of chasing the target's center. This gives both fighters a stable
                // contact distance and lets the melee system strike without overlap.
                if (intent != BehaviacUnitIntent.HoldPositionEngage &&
                    !HasRangedWeapon(units, inventory, unit))
                {
                    Vector3 position = units.Position[unit];
                    Vector3 targetPosition = units.Position[target.Index];
                    float dx = targetPosition.X - position.X;
                    float dy = targetPosition.Y - position.Y;
                    float distanceSquared = dx * dx + dy * dy;
                    float attackDistance = MeleeCombatSystem.GetAttackDistance(
                        unit, target.Index, units, inventory);

                    // Already in striking reach? Stop closing and fight from this
                    // side instead of circling the defender for a reserved slot.
                    if (distanceSquared > attackDistance * attackDistance)
                    {
                        float desiredDistance = MathF.Max(0.20f, attackDistance * 0.94f);
                        Vector3 engagementPoint = GetMeleeEngagementPoint(
                            unit, target.Index, targetPosition, desiredDistance);
                        units.Target[unit] = engagementPoint;
                        units.HasTarget[unit] = true;
                        ClearAiming(weapons, unit);
                        Store.ClearGoal(unit);
                        Store.State[unit] = UnitAiState.Attack;
                        continue;
                    }
                }

                FaceTarget(
                    units,
                    unit,
                    target.Index);

                // Hold position while firing/aiming; do not continue walking
                // toward the previous patrol waypoint through the firing lane.
                units.Target[unit] = units.Position[unit];
                units.HasTarget[unit] = false;

                VisionCheck shotCheck = hasCurrentTargetShotCheck
                    ? currentTargetShotCheck
                    : vision.Evaluate(units, worldMap, unit, target.Index);

                // This direct visibility check ran before positioning. Never fire
                // based only on a stale visibility-memory entry.
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
                else if (intent != BehaviacUnitIntent.HoldPositionEngage &&
                         !HasRangedWeapon(
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

            // The simplified Behaviac tree no longer invents targets from stale
            // coordinates. The legacy comparison path keeps its original behavior.
            if (!UseBehaviacDecisionLayer && Store.TargetMemory[unit] > 0f)
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
                        GetSpreadMultiplier(units, suppression, unit));

                    units.Target[unit] = units.Position[unit];
                    units.HasTarget[unit] = false;
                    Store.State[unit] = UnitAiState.Attack;
                }
                else
                {
                    // No direct target is visible. Stop the ordinary combat loop
                    // instead of preserving a firing ring around stale coordinates.
                    units.Target[unit] = units.Position[unit];
                    units.HasTarget[unit] = false;
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

    private bool TryExecutePlayerOrderIntent(
        UnitStore units,
        UnitHealthStore health,
        UnitWeaponStore weapons,
        VisionSystem vision,
        WorldMap worldMap,
        int unit,
        BehaviacUnitIntent intent)
    {
        UnitAiStore store = Store;

        if (intent == BehaviacUnitIntent.FollowMoveOrder)
        {
            Vector3 moveDestination = store.PlayerOrderPosition[unit];
            if (ReachedGoal(units.Position[unit], moveDestination))
            {
                store.SetPlayerOrder(unit, UnitAiOrderType.Stop, units.Position[unit]);
                store.ClearTarget(unit);
                store.ClearGoal(unit);
                ClearAiming(weapons, unit);
                units.Target[unit] = units.Position[unit];
                units.HasTarget[unit] = false;
                store.State[unit] = UnitAiState.Idle;
                return true;
            }

            // Behaviac selected the task; this AI action translates it into the
            // existing navigation goal without bypassing the decision tree.
            store.ClearTarget(unit);
            store.SetGoal(unit, moveDestination);
            ClearAiming(weapons, unit);
            units.Target[unit] = moveDestination;
            units.HasTarget[unit] = true;
            store.State[unit] = UnitAiState.Search;
            return true;
        }

        if (intent == BehaviacUnitIntent.HoldPosition)
        {
            store.ClearTarget(unit);
            store.ClearGoal(unit);
            ClearAiming(weapons, unit);
            units.Target[unit] = units.Position[unit];
            units.HasTarget[unit] = false;
            store.State[unit] = UnitAiState.Idle;
            return true;
        }

        if (intent == BehaviacUnitIntent.HoldPositionEngage)
        {
            // Let the regular combat code aim/fire, but remove movement intent
            // and stale waypoints before it runs.
            store.ClearGoal(unit);
            units.Target[unit] = units.Position[unit];
            units.HasTarget[unit] = false;
            store.State[unit] = UnitAiState.Attack;
            return false;
        }

        if (intent != BehaviacUnitIntent.AttackOrder)
            return false;

        UnitId target = store.PlayerOrderTarget[unit];
        bool hasLiveTarget = target.IsValid &&
            units.IsAlive(target) &&
            health.OverallHitPoints[target.Index] > 0f &&
            IsHostile(units, unit, target.Index);
        if (!hasLiveTarget && target.IsValid)
        {
            // Preserve the order destination even when its current enemy dies
            // or becomes invalid; a nearby visible threat may replace it.
            store.SetPlayerOrder(
                unit,
                UnitAiOrderType.Attack,
                store.PlayerOrderPosition[unit]);
            target = default;
        }

        Vector3 destination = store.PlayerOrderPosition[unit];
        bool targetVisible = hasLiveTarget &&
            HasFreshVisualContact(unit, target, out _) &&
            vision.IsRecentlyVisible(unit, target.Index, units) &&
            vision.Evaluate(units, worldMap, unit, target.Index).IsVisible;
        if (targetVisible)
        {
            destination = units.Position[target.Index];
            store.SetPlayerOrder(unit, UnitAiOrderType.Attack, destination, target);
            store.RememberTarget(unit, target, destination, TargetMemorySeconds);
        }
        // Otherwise keep the last actually observed destination. Do not calculate
        // a stand-off position around a unit that may have moved out of sight.

        if (ReachedGoal(units.Position[unit], destination))
        {
            store.ClearTarget(unit);
            store.ClearGoal(unit);
            ClearAiming(weapons, unit);
            units.Target[unit] = units.Position[unit];
            units.HasTarget[unit] = false;
            store.State[unit] = UnitAiState.Search;
            return true;
        }

        store.ClearTarget(unit);
        store.SetGoal(unit, destination);
        ClearAiming(weapons, unit);
        units.Target[unit] = destination;
        units.HasTarget[unit] = true;
        store.State[unit] = UnitAiState.Search;
        return true;
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
            case BehaviacUnitIntent.EngageTarget:
            case BehaviacUnitIntent.ProneEngage: LastBehaviacEngageDecisions++; break;
            case BehaviacUnitIntent.InvestigateAudibleContact:
                LastBehaviacInvestigateDecisions++;
                break;
            case BehaviacUnitIntent.FollowMoveOrder: LastBehaviacMoveOrderDecisions++; break;
            case BehaviacUnitIntent.HoldPosition: LastBehaviacHoldOrderDecisions++; break;
            case BehaviacUnitIntent.AttackOrder: LastBehaviacAttackOrderDecisions++; break;
            case BehaviacUnitIntent.HoldPositionEngage: LastBehaviacHoldAndEngageDecisions++; break;
        }
    }

    private bool TryExecuteAudibleInvestigation(
        UnitStore units,
        UnitWeaponStore weapons,
        WorldMap worldMap,
        int unit)
    {
        if (Hearing?.TryGetBestInvestigableContact(unit, out TacticalContact contact) != true)
            return false;

        Vector3 estimate = contact.EstimatedPosition;
        float angle = ((contact.ContactId * 2.39996323f) + unit * 0.75487767f) % MathF.Tau;
        float searchOffset = Math.Clamp(contact.PositionErrorRadius * 0.15f, 0.5f, 6f);
        float x = Math.Clamp(estimate.X + MathF.Cos(angle) * searchOffset, 0.5f, worldMap.MaxTileX - 0.5f);
        float y = Math.Clamp(estimate.Y + MathF.Sin(angle) * searchOffset, 0.5f, worldMap.MaxTileY - 0.5f);
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
        Vector3 searchPoint = new(x, y, worldMap.GetSurfaceHeight(tileX, tileY));

        // Audio is only a search location: it never creates an identified combat target.
        ClearAiming(weapons, unit);
        Store.ClearTarget(unit);
        Store.SetGoal(unit, searchPoint);
        units.Target[unit] = searchPoint;
        units.HasTarget[unit] = true;
        Store.State[unit] = UnitAiState.Search;
        Hearing.MarkInvestigated(unit, contact.ContactId);
        return true;
    }

    private BehaviacUnitIntent SelectUnitIntent(
        UnitStore units,
        UnitHealthStore health,
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        VisionSystem vision,
        UnitSuppressionStore suppression,
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

        UnitAiOrderType orderType = Store.PlayerOrderType[unit];
        bool hasAttackOrder = orderType == UnitAiOrderType.Attack;
        UnitId attackTarget = Store.PlayerOrderTarget[unit];
        if (hasAttackOrder &&
            attackTarget.IsValid &&
            (!units.IsAlive(attackTarget) ||
             health.OverallHitPoints[attackTarget.Index] <= 0f ||
             !IsHostile(units, unit, attackTarget.Index)))
        {
            // The original target dying must not cancel the attack task.
            // Keep its last-known position as the objective and let a new
            // visible hostile replace the target below.
            Store.SetPlayerOrder(
                unit,
                UnitAiOrderType.Attack,
                Store.PlayerOrderPosition[unit]);
            attackTarget = default;
        }

        if (orderType == UnitAiOrderType.Move &&
            ReachedGoal(units.Position[unit], Store.PlayerOrderPosition[unit]))
        {
            Store.SetPlayerOrder(unit, UnitAiOrderType.Stop, units.Position[unit]);
            Store.ClearGoal(unit);
            orderType = UnitAiOrderType.Stop;
        }

        int visibleTargetIndex = _nearestVisibleEnemyByUnit[unit];
        if (hasAttackOrder)
        {
            // If the spatial visibility sample temporarily omits the current task
            // target, retain it only while LOS memory still confirms it visible.
            if (visibleTargetIndex < 0 &&
                attackTarget.IsValid &&
                units.IsAlive(attackTarget) &&
                health.OverallHitPoints[attackTarget.Index] > 0f &&
                IsHostile(units, unit, attackTarget.Index) &&
                HasFreshVisualContact(unit, attackTarget, out _) &&
                vision.IsRecentlyVisible(unit, attackTarget.Index, units))
            {
                visibleTargetIndex = attackTarget.Index;
            }

            if (visibleTargetIndex >= 0)
            {
                // Switching target also updates the attack task's last-known
                // location, so it can continue searching if LOS is lost again.
                UnitId nearestTarget = units.GetId(visibleTargetIndex);
                Vector3 targetPosition = GetObservedTargetPosition(units, unit, visibleTargetIndex);
                Store.SetPlayerOrder(
                    unit,
                    UnitAiOrderType.Attack,
                    targetPosition,
                    nearestTarget);
                Store.RememberTarget(
                    unit,
                    nearestTarget,
                    targetPosition,
                    TargetMemorySeconds);
            }
        }

        if (!hasAttackOrder && Store.HasTarget[unit])
        {
            UnitId currentTarget = Store.Target[unit];
            if (ShouldKeepCurrentVisibleTarget(
                    units, health, vision, inventory, unit, currentTarget, visibleTargetIndex))
            {
                // The same close-contact override used by target memory also feeds
                // Behaviac, so it cannot plan around a distant target this tick.
                visibleTargetIndex = currentTarget.Index;
            }
        }

        bool hasVisibleTarget = visibleTargetIndex >= 0;
        Vector3 visibleTargetPosition = hasVisibleTarget
            ? GetObservedTargetPosition(units, unit, visibleTargetIndex)
            : Vector3.Zero;
        if (hasVisibleTarget)
            suppression.RefreshAlertness(unit, 0.82f, visibleTargetPosition);
        bool isAlerted = suppression.IsAlerted(unit);
        bool hasMoveOrder = orderType == UnitAiOrderType.Move;
        bool hasStopOrder = orderType == UnitAiOrderType.Stop;

        float moveOrderUtility = 0f;
        if (hasMoveOrder)
        {
            Vector3 moveDelta = Store.PlayerOrderPosition[unit] - units.Position[unit];
            float moveDistance = MathF.Sqrt(moveDelta.X * moveDelta.X + moveDelta.Y * moveDelta.Y);
            moveOrderUtility = UnitTacticalUtility.EvaluateMoveOrder(
                moveDistance,
                suppressionState is UnitSuppressionState.Panicked or UnitSuppressionState.Suppressed,
                IsLowHealth(health, unit));
        }

        float combatUtility = 0f;
        if (hasVisibleTarget)
        {
            Vector3 enemyDelta = visibleTargetPosition - units.Position[unit];
            float enemyDistance = MathF.Sqrt(enemyDelta.X * enemyDelta.X + enemyDelta.Y * enemyDelta.Y);
            combatUtility = UnitTacticalUtility.EvaluateVisibleCombat(enemyDistance, true);
        }

        bool isSuppressed =
            suppressionState is UnitSuppressionState.Panicked or UnitSuppressionState.Suppressed;
        float healthRatio = health.OverallMaxHitPoints[unit] > 0f
            ? health.OverallHitPoints[unit] / health.OverallMaxHitPoints[unit]
            : 0f;
        bool isLowHealth = healthRatio < LowHealthThreshold;
        bool isHealthyEnoughToFlank = !isLowHealth && !isSuppressed;
        bool canFlank = false;
        float flankDistance = 0f;
        float flankEffectiveRange = 0f;

        // Flanking is a ranged-fire maneuver. A melee-only unit should
        // commit toward contact instead of spending its first approach on a
        // wide side/back waypoint.
        if (hasVisibleTarget &&
            isHealthyEnoughToFlank &&
            HasRangedWeapon(units, inventory, unit) &&
            (unit % 4 == 0 || _squadCoordinator.IsMemberOfAnySquad(units.GetId(unit))))
        {
            Vector3 delta = visibleTargetPosition - units.Position[unit];
            float distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            flankDistance = MathF.Sqrt(distanceSquared);
            bool alreadyFlankedThisTarget = _hasFlankTargetByUnit[unit] &&
                _flankTargetByUnit[unit].Equals(units.GetId(visibleTargetIndex));

            // A flank changes the angle of approach while staying within a useful
            // engagement radius; it does not require the target to be out of weapon range.
            float maxFlankRange = MathF.Min(140f, MathF.Max(45f, units.ViewRange[unit] * 0.90f));
            float effectiveRange = GetMaxEffectiveRange(inventory, unit);
            flankEffectiveRange = effectiveRange;
            float minFlankRange = MathF.Max(
                24f,
                MathF.Min(effectiveRange * 0.45f, maxFlankRange * 0.55f));
            canFlank = !alreadyFlankedThisTarget &&
                effectiveRange > 0.1f &&
                flankDistance >= minFlankRange &&
                flankDistance <= maxFlankRange;
        }

        bool isFlanking =
            Store.State[unit] == UnitAiState.Flank &&
            isHealthyEnoughToFlank &&
            HasRangedWeapon(units, inventory, unit);
        float flankUtility = UnitTacticalUtility.EvaluateFlanking(
            flankDistance,
            flankEffectiveRange,
            canFlank);

        float proneUtility = 0f;
        if (hasVisibleTarget &&
            HasRangedWeapon(units, inventory, unit) &&
            suppressionState == UnitSuppressionState.Calm &&
            !isLowHealth &&
            !canFlank &&
            !isFlanking)
        {
            Vector3 targetDelta = visibleTargetPosition - units.Position[unit];
            float distance = MathF.Sqrt(
                targetDelta.X * targetDelta.X + targetDelta.Y * targetDelta.Y);
            bool exposed = !_hasSuppressionCoverByUnit[unit] &&
                !(Store.HasGoal[unit] && Store.State[unit] == UnitAiState.SeekCover);
            proneUtility = UnitTacticalUtility.EvaluateProneFiring(
                distance,
                exposed,
                healthRatio);
        }

        bool hasCoverGoal = Store.HasGoal[unit] &&
            Store.State[unit] == UnitAiState.SeekCover;
        bool hasCover = suppressionState == UnitSuppressionState.Panicked
            ? _hasSuppressionCoverByUnit[unit] || hasCoverGoal
            : (_hasSuppressionCoverByUnit[unit] &&
               !ReachedGoal(units.Position[unit], _suppressionCoverByUnit[unit])) ||
              (hasCoverGoal && !ReachedGoal(units.Position[unit], Store.Goal[unit]));

        bool hasAudibleContact = false;
        float investigationUtility = 0f;
        if (Hearing != null &&
            Hearing.TryGetBestInvestigableContact(unit, out TacticalContact audibleContact))
        {
            Vector3 contactDelta = audibleContact.EstimatedPosition - units.Position[unit];
            float contactDistance = MathF.Sqrt(
                contactDelta.X * contactDelta.X +
                contactDelta.Y * contactDelta.Y);
            float confidence = audibleContact.GetEffectiveConfidence(
                Hearing.CurrentTimeSeconds,
                2.5f);
            investigationUtility = UnitTacticalUtility.EvaluateInvestigation(
                confidence,
                contactDistance);
            hasAudibleContact =
                investigationUtility >= UnitTacticalUtility.MinimumInvestigationUtility;
        }

        bool hasImmediateDanger = suppression.IsInImmediateDanger(unit);
        float coverUtility = UnitTacticalUtility.EvaluateCover(
            suppression.Value[unit],
            hasCover,
            hasImmediateDanger,
            suppressionState == UnitSuppressionState.Panicked,
            isLowHealth);

        agent.SetFacts(
            isAlive,
            isSuppressed,
            hasCover,
            hasVisibleTarget,
            Store.HasTarget[unit] && Store.TargetMemory[unit] > 0f,
            isLowHealth,
            canFlank,
            isFlanking,
            proneUtility,
            units.Posture[unit] == UnitPosture.Lying,
            hasMoveOrder,
            hasStopOrder,
            hasAttackOrder,
            moveOrderUtility,
            combatUtility,
            hasAudibleContact,
            hasImmediateDanger,
            isAlerted,
            coverUtility,
            flankUtility,
            investigationUtility,
            suppressionState == UnitSuppressionState.Panicked,
            Store.State[unit] == UnitAiState.SeekCover && Store.HasGoal[unit],
            Store.LastIntent[unit] == BehaviacUnitIntent.FollowMoveOrder);

        behaviac.EBTStatus status = BehaviacRuntimeHost.Tick(agent);
        if (status != behaviac.EBTStatus.BT_SUCCESS)
        {
            throw new InvalidOperationException(
                $"Behaviac unit decision failed for unit index {unit}: {status}.");
        }

        return agent.Intent;
    }

    private static bool TryFindProneCrawlWaypoint(
        UnitStore units,
        WorldMap worldMap,
        int unit,
        int targetIndex,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;
        Vector3 position = units.Position[unit];
        Vector3 target = units.Position[targetIndex];
        Vector2 awayFromTarget = new(position.X - target.X, position.Y - target.Y);
        float lengthSquared = awayFromTarget.LengthSquared();
        if (lengthSquared < 0.01f)
            return false;

        Vector2 forward = Vector2.Normalize(awayFromTarget);
        Vector2 side = new(-forward.Y, forward.X);

        // Prefer a direct approach point, then deterministic side steps around
        // blocked terrain. This gives prone units a real crawl destination while
        // letting the shared navigation system find the route there.
        for (int candidate = 0; candidate < 8; candidate++)
        {
            float standoff = candidate < 3 ? ProneCrawlFiringDistance : ProneCrawlFiringDistance + 1.5f;
            float lateral = candidate switch
            {
                0 => 0f,
                1 => 4f,
                2 => -4f,
                3 => 6f,
                4 => -6f,
                5 => 8f,
                6 => -8f,
                _ => 0f
            };

            float targetX = target.X + forward.X * standoff + side.X * lateral;
            float targetY = target.Y + forward.Y * standoff + side.Y * lateral;
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
            if (toWaypoint.LengthSquared() < 1f)
                continue;

            Vector2 fromTarget = new(
                candidateWaypoint.X - target.X,
                candidateWaypoint.Y - target.Y);
            if (fromTarget.LengthSquared() <
                (ProneCrawlFiringDistance - 1f) * (ProneCrawlFiringDistance - 1f))
                continue;

            waypoint = candidateWaypoint;
            return true;
        }

        return false;
    }

    private static bool TryFindFlankWaypoint(
        UnitStore units,
        WorldMap worldMap,
        int unit,
        int targetIndex,
        float effectiveRange,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;
        Vector3 position = units.Position[unit];
        Vector3 target = units.Position[targetIndex];
        Vector2 awayFromTarget = new(position.X - target.X, position.Y - target.Y);
        float lengthSquared = awayFromTarget.LengthSquared();
        float baseAngle = lengthSquared > 0.01f
            ? MathF.Atan2(awayFromTarget.Y, awayFromTarget.X)
            : (unit + 1) * 2.39996323f + (targetIndex + 1) * 0.61803399f;

        float range = float.IsFinite(effectiveRange) && effectiveRange > 0.1f
            ? effectiveRange
            : 32f;
        float currentDistance = MathF.Sqrt(MathF.Max(0f, lengthSquared));
        float minStandoff = MathF.Max(12f, range * 0.45f);
        float maxStandoff = MathF.Max(minStandoff, MathF.Min(140f, range * 0.80f));
        float standoff = Math.Clamp(currentDistance, minStandoff, maxStandoff);
        float preferredSide = (unit & 1) == 0 ? 1f : -1f;

        // Move around the target on a firing ring. Do not send the flanker
        // directly to a close backoff point where they would collide with the enemy.
        for (int candidate = 0; candidate < 8; candidate++)
        {
            float side = candidate % 2 == 0 ? preferredSide : -preferredSide;
            float angleOffset = (candidate switch
            {
                0 or 1 => MathF.PI * 0.35f,
                2 or 3 => MathF.PI * 0.48f,
                4 or 5 => MathF.PI * 0.25f,
                _ => MathF.PI * 0.60f
            }) * side;
            float angle = baseAngle + angleOffset;
            float x = target.X + MathF.Cos(angle) * standoff;
            float y = target.Y + MathF.Sin(angle) * standoff;
            x = Math.Clamp(x, 2f, worldMap.MaxTileX - 2f);
            y = Math.Clamp(y, 2f, worldMap.MaxTileY - 2f);

            int tileX = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
            int tileY = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
            if (worldMap.IsNavigationBlocked(tileX, tileY))
                continue;

            Vector3 candidateWaypoint = new(
                tileX + 0.5f,
                tileY + 0.5f,
                worldMap.GetSurfaceHeight(tileX, tileY));
            Vector2 toWaypoint = new(
                candidateWaypoint.X - position.X,
                candidateWaypoint.Y - position.Y);
            if (toWaypoint.LengthSquared() <= 9f)
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
        if (Store.HasGoal[unit] && Store.State[unit] == UnitAiState.SeekCover)
            return;

        int visibleEnemy = _nearestVisibleEnemyByUnit[unit];
        bool isPanicked = state == UnitSuppressionState.Panicked;
        bool isHeld = Store.PlayerOrderType[unit] == UnitAiOrderType.Stop;
        bool lowHealthHoldNeedsCover = isHeld &&
            IsLowHealth(health, unit) &&
            visibleEnemy >= 0;
        bool suppressedHoldNeedsCover = isHeld &&
            state == UnitSuppressionState.Suppressed;
        if (!isPanicked && !lowHealthHoldNeedsCover && !suppressedHoldNeedsCover)
            return;

        Vector3 threat = suppression.Source[unit];
        if (lowHealthHoldNeedsCover)
            threat = units.Position[visibleEnemy];
        Vector3 previousSource = Store.SuppressionRetrySource[unit];
        float sourceDx = threat.X - previousSource.X;
        float sourceDy = threat.Y - previousSource.Y;
        bool threatChanged = sourceDx * sourceDx + sourceDy * sourceDy >= 16f;

        // The sequential phase subtracts thinkDelta before checking the timer.
        // Precompute cover only when that same check would request a new search.
        if (Store.SuppressionCoverRetry[unit] > thinkDelta && !threatChanged)
            return;

        Vector3 cover;
        bool foundCover = lowHealthHoldNeedsCover
            ? TryFindCover(units, vision, worldMap, unit, visibleEnemy, out cover)
            : TryFindSuppressionCover(units, vision, worldMap, unit, threat, out cover);

        Store.SuppressionRetrySource[unit] = threat;

        if (foundCover)
        {
            _suppressionCoverByUnit[unit] = cover;
            _hasSuppressionCoverByUnit[unit] = true;
        }
        else
        {
            // A failed search is expensive for both suppressed and panicked units.
            // Retry after one second, unless the threat moves substantially.
            Store.SuppressionCoverRetry[unit] = 1f;
        }
    }

    private int FindNearestVisibleEnemy(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        int observer)
    {
        if (_perceptionMemory == null)
            return FindNearestVisibleEnemyFromVision(units, health, vision, observer);
        if (!_perceptionMemory.TryGetMemory(observer, out TacticalContactMemory memory))
            return -1;

        Vector3 observerPosition = units.Position[observer];
        float bestDistanceSquared = float.PositiveInfinity;
        int bestTarget = -1;
        IReadOnlyList<TacticalContact> contacts = memory.Contacts;

        for (int i = 0; i < contacts.Count; i++)
        {
            TacticalContact contact = contacts[i];
            if (contact.Kind != TacticalContactKind.VisualConfirmed ||
                !contact.SubjectUnit.HasValue ||
                !TryGetFreshVisualContact(observer, contact.SubjectUnit.Value, out Vector3 observedPosition) ||
                !units.TryGetIndex(contact.SubjectUnit.Value, out int target) ||
                target == observer ||
                health.OverallHitPoints[target] <= 0f ||
                !IsHostile(units, observer, target))
                continue;

            Vector3 delta = observedPosition - observerPosition;
            float distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            bestTarget = target;
        }

        return bestTarget;
    }

    private static int FindNearestVisibleEnemyFromVision(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        int observer)
    {
        ReadOnlySpan<int> visible = vision.GetVisibleTargets(observer);
        ushort observerFaction = units.FactionTag[observer];
        Vector3 position = units.Position[observer];
        int bestTarget = -1;
        float bestDistanceSquared = float.PositiveInfinity;

        for (int i = 0; i < visible.Length; i++)
        {
            int target = visible[i];
            if ((uint)target >= (uint)units.Capacity ||
                health.OverallHitPoints[target] <= 0f ||
                !FactionRules.ShouldAttack(observerFaction, units.FactionTag[target]))
                continue;

            Vector3 delta = units.Position[target] - position;
            float distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            bestTarget = target;
        }

        return bestTarget;
    }

    private bool HasFreshVisualContact(int observer, UnitId target, out Vector3 estimatedPosition) =>
        _perceptionMemory == null || TryGetFreshVisualContact(observer, target, out estimatedPosition);

    internal bool TryGetFreshVisualContact(int observer, UnitId target, out Vector3 estimatedPosition)
    {
        estimatedPosition = Vector3.Zero;
        if (_perceptionMemory == null ||
            !target.IsValid ||
            !_perceptionMemory.TryGetMemory(observer, out TacticalContactMemory memory) ||
            !memory.TryGetContact(target, out TacticalContact contact) ||
            contact.Kind != TacticalContactKind.VisualConfirmed ||
            contact.SubjectUnit is not UnitId subject ||
            !subject.Equals(target) ||
            contact.LastVisualAtSeconds < 0f)
            return false;

        float visualAge = _perceptionMemory.CurrentTimeSeconds - contact.LastVisualAtSeconds;
        if (!float.IsFinite(visualAge) ||
            visualAge < 0f ||
            visualAge >= TacticalContactMemory.VisualConfirmationGraceSeconds)
            return false;

        estimatedPosition = contact.EstimatedPosition;
        return true;
    }

    private Vector3 GetObservedTargetPosition(UnitStore units, int observer, int targetIndex)
    {
        if (_perceptionMemory == null)
            return units.Position[targetIndex];

        return TryGetFreshVisualContact(observer, units.GetId(targetIndex), out Vector3 estimatedPosition)
            ? estimatedPosition
            : Vector3.Zero;
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

    private bool ShouldKeepCurrentVisibleTarget(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        UnitInventoryStore inventory,
        int observer,
        UnitId currentTarget,
        int newlyVisibleTarget)
    {
        if (!units.IsAlive(currentTarget) ||
            health.OverallHitPoints[currentTarget.Index] <= 0f ||
            !IsHostile(units, observer, currentTarget.Index) ||
            !HasFreshVisualContact(observer, currentTarget, out _) ||
            !vision.IsRecentlyVisible(observer, currentTarget.Index, units))
        {
            return false;
        }

        if (newlyVisibleTarget < 0 || newlyVisibleTarget == currentTarget.Index)
            return true;

        Vector3 observerPosition = units.Position[observer];
        Vector3 newPosition = GetObservedTargetPosition(units, observer, newlyVisibleTarget);
        float newDx = newPosition.X - observerPosition.X;
        float newDy = newPosition.Y - observerPosition.Y;
        float newDistanceSquared = newDx * newDx + newDy * newDy;
        float contactRange = MathF.Max(
            VisionSystem.CloseAwarenessRange,
            MeleeCombatSystem.GetAttackDistance(
                observer, newlyVisibleTarget, units, inventory));

        if (newDistanceSquared > contactRange * contactRange)
            return true;

        Vector3 currentPosition = GetObservedTargetPosition(units, observer, currentTarget.Index);
        float currentDx = currentPosition.X - observerPosition.X;
        float currentDy = currentPosition.Y - observerPosition.Y;
        float currentDistanceSquared = currentDx * currentDx + currentDy * currentDy;

        // A closer hostile within immediate-contact range beats target stickiness.
        return newDistanceSquared >= currentDistanceSquared;
    }

    private static Vector3 GetMeleeEngagementPoint(
        int attacker,
        int target,
        Vector3 targetPosition,
        float distance)
    {
        // Golden-angle distribution avoids all pawns sharing the approach axis.
        const float GoldenAngle = 2.39996323f;
        float angle = attacker * GoldenAngle + target * 0.61803399f;
        return new Vector3(
            targetPosition.X + MathF.Cos(angle) * distance,
            targetPosition.Y + MathF.Sin(angle) * distance,
            targetPosition.Z);
    }

    private static bool TryGetRangedEngagementMoveTarget(
        Vector3 unitPosition,
        Vector3 targetPosition,
        float effectiveRange,
        int unit,
        int targetIndex,
        out Vector3 destination)
    {
        destination = unitPosition;
        if (!float.IsFinite(effectiveRange) || effectiveRange <= 0.1f)
            return false;

        float desiredDistance = MathF.Max(3f, effectiveRange * 0.72f);
        float tooFarDistance = desiredDistance + MathF.Max(1f, effectiveRange * 0.05f);
        float tooCloseDistance = MathF.Max(2f, effectiveRange * 0.63f);

        float dx = targetPosition.X - unitPosition.X;
        float dy = targetPosition.Y - unitPosition.Y;
        float distanceSquared = dx * dx + dy * dy;

        if (distanceSquared <= tooFarDistance * tooFarDistance &&
            distanceSquared >= tooCloseDistance * tooCloseDistance)
        {
            return false;
        }

        float distance;
        if (distanceSquared > 0.0001f)
        {
            distance = MathF.Sqrt(distanceSquared);
            dx /= distance;
            dy /= distance;
        }
        else
        {
            // Stable separation direction for exact overlaps.
            float angle = (unit + 1) * 2.39996323f +
                (targetIndex + 2) * 0.61803399f;
            dx = MathF.Cos(angle);
            dy = MathF.Sin(angle);
        }

        destination = new Vector3(
            targetPosition.X - dx * desiredDistance,
            targetPosition.Y - dy * desiredDistance,
            unitPosition.Z);
        return true;
    }

    private static float GetMaxEffectiveRange(
        UnitInventoryStore inventory,
        int unit)
    {
        float range = 0f;
        for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
        {
            short itemSlot = inventory.GetWeaponEquipment(unit, (UnitWeaponSlot)slot);
            if (itemSlot >= 0 &&
                inventory.GetItem(unit, itemSlot) is RangedWeaponConfig weapon)
            {
                range = MathF.Max(range, weapon.TotalEffectiveRange);
            }
        }

        return range;
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
        return Math.Clamp(suppression.GetAccuracyMultiplier(unit), 1f, 1.40f);
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
