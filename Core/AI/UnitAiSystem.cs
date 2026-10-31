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
    private const int ThinkPhaseBuckets = 9;
    private const float TargetMemorySeconds = 3f;
    private const float LowHealthThreshold = 0.35f;
    private const float CoverStep = 2f;
    private const int CoverDirectionCount = 8;
    private const float RememberedEnemyRefreshInterval = 0.45f;
    private const float RememberedEnemyMemorySeconds = 8f;
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
    private TacticalContact?[] _rememberedEnemyContextByUnit = Array.Empty<TacticalContact?>();
    private TacticalContact?[] _cachedRememberedEnemyContextByUnit = Array.Empty<TacticalContact?>();
    private float[] _nextRememberedEnemyQuerySecondsByUnit = Array.Empty<float>();
    private bool[] _isContextInvestigationGoal = Array.Empty<bool>();
    private UnitId[] _flankTargetByUnit = Array.Empty<UnitId>();
    private bool[] _hasFlankTargetByUnit = Array.Empty<bool>();

    private readonly UnitWeaponSystem _weaponSystem;
    private readonly UnitSkillStore? _skills;
    private readonly PerceptionMemoryStore? _perceptionMemory;
    private readonly UnitMetabolismStore? _metabolism;
    private float[] _unitThinkElapsedSeconds = Array.Empty<float>();
    private float[] _unitThinkDeltaSeconds = Array.Empty<float>();
    private float _tacticalTimeSeconds;
    private readonly SquadTacticalCoordinator _squadCoordinator = new();
    private readonly Dictionary<ushort, AiBalanceProfile> _factionBalanceProfiles = new();
    private readonly Dictionary<ushort, AiStealthProfile> _factionStealthProfiles = new();
    private AiBalanceProfile?[] _unitBalanceProfiles = Array.Empty<AiBalanceProfile?>();
    private AiTacticalGenomePool? _tacticalGenomePool;

    public bool Enabled { get; set; }
    public AiBalanceProfile BalanceProfile { get; set; } = AiBalanceProfile.Default;
    public AiStealthProfile StealthProfile { get; set; } = AiStealthProfile.Default;
    public AiBalanceProfile GetBalanceProfile(ushort factionTag) =>
        _factionBalanceProfiles.TryGetValue(factionTag, out AiBalanceProfile profile) ? profile : BalanceProfile;
    /// <summary>Set between matches, never during a parallel AI update.</summary>
    public void SetBalanceProfileForFaction(ushort factionTag, AiBalanceProfile? profile)
    {
        if (profile == null) _factionBalanceProfiles.Remove(factionTag);
        else _factionBalanceProfiles[factionTag] = profile;
    }

    /// <summary>Faction experiment overrides take precedence over each pawn's inherited genome.</summary>
    public AiBalanceProfile GetBalanceProfile(int unitIndex, ushort factionTag)
    {
        if (_factionBalanceProfiles.TryGetValue(factionTag, out AiBalanceProfile factionProfile))
            return factionProfile;
        if ((uint)unitIndex < (uint)_unitBalanceProfiles.Length &&
            _unitBalanceProfiles[unitIndex] is AiBalanceProfile individualProfile)
            return individualProfile;
        return BalanceProfile;
    }

    public void SetBalanceProfileForUnit(int unitIndex, AiBalanceProfile? profile)
    {
        if (unitIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        EnsurePlanningCapacity(unitIndex + 1);
        _unitBalanceProfiles[unitIndex] = profile;
    }

    /// <summary>Assigns a stable, diverse genome to each subsequently initialized pawn.</summary>
    public void SetTacticalGenomePool(AiTacticalGenomePool? pool)
    {
        AiTacticalGenomePool? sanitized = pool?.Sanitized();
        _tacticalGenomePool = sanitized != null && sanitized.Genomes.Count > 0 ? sanitized : null;
    }

    public AiStealthProfile GetStealthProfile(ushort factionTag) =>
        _factionStealthProfiles.TryGetValue(factionTag, out AiStealthProfile profile)
            ? profile
            : StealthProfile;

    /// <summary>Set between simulation steps; active planning reads this profile in parallel.</summary>
    public void SetStealthProfileForFaction(ushort factionTag, AiStealthProfile? profile)
    {
        if (profile == null) _factionStealthProfiles.Remove(factionTag);
        else _factionStealthProfiles[factionTag] = profile.Sanitized();
    }
    public bool LastUpdateRanThinkPass { get; private set; }

    /// <summary>The utility scorer is the authoritative autonomous intent selector.</summary>
    public bool UseUtilityDecisionLayer => true;
    public bool UtilityDecisionReady => true;

    // Compatibility indicators for older diagnostics; the Behaviac runtime is not used by live pawn decisions.
    public bool UseBehaviacDecisionLayer => false;

    public TacticalHearingSystem? Hearing { get; set; }
    public UnitAiStore Store { get; }
    public UnitAiPersonalityStore Personality { get; }
    public SquadTacticalCoordinator SquadCoordinator => _squadCoordinator;

    public void SynchronizeSquadMemberships(IReadOnlyList<SquadMembershipSnapshot> squads) =>
        _squadCoordinator.SynchronizeMemberships(squads);

    public void InvalidateSquadUnit(UnitId unit) =>
        _squadCoordinator.InvalidateUnit(unit);
    public double LastPlanningMilliseconds { get; private set; }

    public bool BehaviacRuntimeReady => false;
    public long TotalUtilityDecisions { get; private set; }
    public int LastUtilityDecisions { get; private set; }
    public double LastUtilityDecisionBatchMilliseconds { get; private set; }
    public int LastUtilityDeadDecisions { get; private set; }
    public int LastUtilityCoverDecisions { get; private set; }
    public int LastUtilityRetreatDecisions { get; private set; }
    public int LastUtilityFlankDecisions { get; private set; }
    public int LastUtilityEngageDecisions { get; private set; }
    public int LastUtilitySuppressDecisions { get; private set; }
    public int LastUtilityInvestigateDecisions { get; private set; }
    public int LastUtilityPatrolDecisions { get; private set; }
    public int LastUtilityMoveOrderDecisions { get; private set; }
    public int LastUtilityHoldOrderDecisions { get; private set; }
    public int LastUtilityAttackOrderDecisions { get; private set; }
    public int LastUtilityHoldAndEngageDecisions { get; private set; }

    // Compatibility aliases for legacy diagnostics and snapshot consumers.
    public long TotalBehaviacDecisions => TotalUtilityDecisions;
    public int LastBehaviacDecisions => LastUtilityDecisions;
    public double LastBehaviacDecisionBatchMilliseconds => LastUtilityDecisionBatchMilliseconds;
    public int LastBehaviacDeadDecisions => LastUtilityDeadDecisions;
    public int LastBehaviacCoverDecisions => LastUtilityCoverDecisions;
    public int LastBehaviacRetreatDecisions => LastUtilityRetreatDecisions;
    public int LastBehaviacFlankDecisions => LastUtilityFlankDecisions;
    public int LastBehaviacEngageDecisions => LastUtilityEngageDecisions;
    public int LastBehaviacSuppressDecisions => LastUtilitySuppressDecisions;
    public int LastBehaviacInvestigateDecisions => LastUtilityInvestigateDecisions;
    public int LastBehaviacPatrolDecisions => LastUtilityPatrolDecisions;
    public int LastBehaviacMoveOrderDecisions => LastUtilityMoveOrderDecisions;
    public int LastBehaviacHoldOrderDecisions => LastUtilityHoldOrderDecisions;
    public int LastBehaviacAttackOrderDecisions => LastUtilityAttackOrderDecisions;
    public int LastBehaviacHoldAndEngageDecisions => LastUtilityHoldAndEngageDecisions;

    public UnitAiSystem(
        int initialUnitCapacity = 1024,
        UnitWeaponSystem? weaponSystem = null,
        UnitSkillStore? skills = null,
        PerceptionMemoryStore? perceptionMemory = null,
        UnitMetabolismStore? metabolism = null)
    {
        Store =
            new UnitAiStore(
                initialUnitCapacity);

        _weaponSystem =
            weaponSystem ?? new UnitWeaponSystem();
        _skills = skills;
        _perceptionMemory = perceptionMemory;
        _metabolism = metabolism;
        Personality = new UnitAiPersonalityStore(initialUnitCapacity);
        _unitBalanceProfiles = new AiBalanceProfile?[initialUnitCapacity];

    }

    public void InitializeUnit(int unitIndex) =>
        InitializeUnit(unitIndex, generation: 1u, factionTag: 0);

    public void InitializeUnit(int unitIndex, uint generation, ushort factionTag)
    {
        EnsurePlanningCapacity(unitIndex + 1);
        _unitBalanceProfiles[unitIndex] = _tacticalGenomePool?.SelectProfile(unitIndex, generation, factionTag);
        // Spread the first decision across one ThinkInterval; all later decisions
        // stay on this pawn's own ~150 ms cadence instead of a world-wide batch.
        _unitThinkElapsedSeconds[unitIndex] =
            ThinkInterval * (unitIndex % ThinkPhaseBuckets) / ThinkPhaseBuckets;
        _unitThinkDeltaSeconds[unitIndex] = 0f;
        Personality.InitializeUnit(unitIndex, generation, factionTag);
        Store.UtilityIntent[unitIndex] = BehaviacUnitIntent.None;
        Store.UtilityIntentCommitRemainingSeconds[unitIndex] = 0f;
        Store.UtilityIntentBreakdown[unitIndex] = default;
        _searchPatrolLegByUnit[unitIndex] = 0;
        _rememberedEnemyContextByUnit[unitIndex] = null;
        _cachedRememberedEnemyContextByUnit[unitIndex] = null;
        // Stagger the first ambient-memory scan across three AI ticks. Immediate
        // vision, danger reactions and player orders still evaluate on every pawn
        // decision; only the more expensive remembered-contact list scan is spread.
        _nextRememberedEnemyQuerySecondsByUnit[unitIndex] =
            _tacticalTimeSeconds + ((unitIndex % 3) + 1) * ThinkInterval;
        _isContextInvestigationGoal[unitIndex] = false;
        _flankTargetByUnit[unitIndex] = default;
        _hasFlankTargetByUnit[unitIndex] = false;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_searchPatrolLegByUnit.Length)
            return;

        _unitBalanceProfiles[unitIndex] = null;
        _searchPatrolLegByUnit[unitIndex] = 0;
        if ((uint)unitIndex < (uint)_unitThinkElapsedSeconds.Length)
            _unitThinkElapsedSeconds[unitIndex] = 0f;
        if ((uint)unitIndex < (uint)_unitThinkDeltaSeconds.Length)
            _unitThinkDeltaSeconds[unitIndex] = 0f;
        if ((uint)unitIndex < (uint)_rememberedEnemyContextByUnit.Length)
            _rememberedEnemyContextByUnit[unitIndex] = null;
        if ((uint)unitIndex < (uint)_cachedRememberedEnemyContextByUnit.Length)
            _cachedRememberedEnemyContextByUnit[unitIndex] = null;
        if ((uint)unitIndex < (uint)_nextRememberedEnemyQuerySecondsByUnit.Length)
            _nextRememberedEnemyQuerySecondsByUnit[unitIndex] = 0f;
        if ((uint)unitIndex < (uint)_isContextInvestigationGoal.Length)
            _isContextInvestigationGoal[unitIndex] = false;
        _flankTargetByUnit[unitIndex] = default;
        _hasFlankTargetByUnit[unitIndex] = false;
        if ((uint)unitIndex < (uint)Store.Capacity)
        {
            Store.UtilityIntent[unitIndex] = BehaviacUnitIntent.None;
            Store.UtilityIntentCommitRemainingSeconds[unitIndex] = 0f;
            Store.UtilityIntentBreakdown[unitIndex] = default;
        }
        Personality.ClearUnit(unitIndex);
    }

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

        Store.EnsureCapacity(units.Capacity);
        ReadOnlySpan<int> active = units.ActiveIndices;
        EnsurePlanningCapacity(units.Capacity);

        // Only a fraction of pawns becomes due each frame. The elapsed time is
        // accumulated per pawn so sensing/intent cadence remains near 150 ms,
        // while cover scans and memory queries no longer spike on one frame.
        _tacticalTimeSeconds += deltaTime;
        int dueCount = 0;
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (Store.HasTarget[unit] &&
                units.TryGetIndex(Store.Target[unit], out int currentTarget) &&
                units.Posture[currentTarget] == UnitPosture.Injured)
            {
                ClearAiming(weapons, unit);
                Store.ClearTarget(unit);
                Store.State[unit] = UnitAiState.Search;
                units.HasTarget[unit] = false;
                units.Target[unit] = units.Position[unit];
                units.Velocity[unit] = Vector3.Zero;
            }

            if (units.Posture[unit] == UnitPosture.Injured)
            {
                _unitThinkElapsedSeconds[unit] = 0f;
                _unitThinkDeltaSeconds[unit] = 0f;
                Store.ClearTarget(unit);
                Store.State[unit] = UnitAiState.Idle;
                Store.LastIntent[unit] = BehaviacUnitIntent.None;
                Store.UtilityIntent[unit] = BehaviacUnitIntent.None;
                Store.UtilityIntentCommitRemainingSeconds[unit] = 0f;
                Store.UtilityIntentBreakdown[unit] = default;
                Store.UtilityIntentContext[unit] = default;
                _hasFlankTargetByUnit[unit] = false;
                _flankTargetByUnit[unit] = default;
                _isContextInvestigationGoal[unit] = false;
                _rememberedEnemyContextByUnit[unit] = null;
                _cachedRememberedEnemyContextByUnit[unit] = null;
                units.HasTarget[unit] = false;
                units.Target[unit] = units.Position[unit];
                units.Velocity[unit] = Vector3.Zero;
                continue;
            }

            float elapsed = _unitThinkElapsedSeconds[unit] + deltaTime;
            _unitThinkElapsedSeconds[unit] = elapsed;
            if (elapsed < ThinkInterval)
                continue;

            _unitThinkDeltaSeconds[unit] = elapsed;
            _unitThinkElapsedSeconds[unit] = 0f;
            _activeUnitScratch[dueCount++] = unit;
        }

        if (dueCount == 0)
            return;

        LastUpdateRanThinkPass = true;

        // Preserve the most recent non-empty metrics sample between due batches;
        // the one-second performance logger should not usually report zero work.
        LastPlanningMilliseconds = 0d;
        LastUtilityDecisions = 0;
        LastUtilityDecisionBatchMilliseconds = 0d;
        LastUtilityDeadDecisions = 0;
        LastUtilityCoverDecisions = 0;
        LastUtilityRetreatDecisions = 0;
        LastUtilityFlankDecisions = 0;
        LastUtilityEngageDecisions = 0;
        LastUtilitySuppressDecisions = 0;
        LastUtilityInvestigateDecisions = 0;
        LastUtilityPatrolDecisions = 0;
        LastUtilityMoveOrderDecisions = 0;
        LastUtilityHoldOrderDecisions = 0;
        LastUtilityAttackOrderDecisions = 0;
        LastUtilityHoldAndEngageDecisions = 0;

        long planningStarted = Stopwatch.GetTimestamp();
        if (dueCount >= 32 && MaxAiPlanningParallelism > 1)
        {
            Parallel.For(0, dueCount, AiPlanningOptions, i =>
            {
                int unit = _activeUnitScratch[i];
                PrepareUnitPlan(
                    units, health, suppression, vision, worldMap,
                    unit, _unitThinkDeltaSeconds[unit]);
            });
        }
        else
        {
            for (int i = 0; i < dueCount; i++)
            {
                int unit = _activeUnitScratch[i];
                PrepareUnitPlan(
                    units, health, suppression, vision, worldMap,
                    unit, _unitThinkDeltaSeconds[unit]);
            }
        }

        for (int i = 0; i < dueCount; i++)
        {
            int unit = _activeUnitScratch[i];
            int visibleTarget = _nearestVisibleEnemyByUnit[unit];
            if (visibleTarget < 0)
                continue;

            _squadCoordinator.ObserveVisibleContact(
                units.GetId(unit), units.GetId(visibleTarget),
                units.Position[visibleTarget], _tacticalTimeSeconds);
        }
        _squadCoordinator.Advance(_tacticalTimeSeconds);

        LastPlanningMilliseconds =
            Stopwatch.GetElapsedTime(planningStarted).TotalMilliseconds;

        for (int i = 0;
             i < dueCount;
             i++)
        {
            int unit = _activeUnitScratch[i];
            float thinkDelta = _unitThinkDeltaSeconds[unit];

            Store.TickMemory(
                unit,
                thinkDelta);

            Store.UtilityIntentCommitRemainingSeconds[unit] = MathF.Max(
                0f,
                Store.UtilityIntentCommitRemainingSeconds[unit] - thinkDelta);

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

            long decisionStarted = Stopwatch.GetTimestamp();
            BehaviacUnitIntent intent = SelectUnitIntent(
                units, health, inventory, weapons, vision, suppression, unit, suppressionState, isAlive);
            LastUtilityDecisionBatchMilliseconds +=
                Stopwatch.GetElapsedTime(decisionStarted).TotalMilliseconds;
            RecordUtilityIntent(intent);

            Store.LastIntent[unit] = intent;

            if (_isContextInvestigationGoal[unit] &&
                intent is not (BehaviacUnitIntent.Patrol or BehaviacUnitIntent.None))
            {
                _isContextInvestigationGoal[unit] = false;
            }

            if (intent == BehaviacUnitIntent.InvestigateLastKnownTarget &&
                TryExecuteLastKnownInvestigation(units, weapons, worldMap, unit))
            {
                continue;
            }

            if (!isAlive ||
                (intent == BehaviacUnitIntent.Dead))
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

            if ((suppressionState != UnitSuppressionState.Panicked ||
                 Store.PlayerOrderType[unit] == UnitAiOrderType.StealthMove) &&
                TryExecutePlayerOrderIntent(
                    units, health, weapons, vision, worldMap, unit, intent))
            {
                continue;
            }

            if (suppressionState != UnitSuppressionState.Panicked &&
                intent == BehaviacUnitIntent.SeekCover)
            {
                // A non-panicked but suppressed/low-health unit may still seek
                // cover under a Hold order. Move to the planned cover, then let
                // the utility scorer resume its hold/engage choice once reached.
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

            if (intent != BehaviacUnitIntent.FlankTarget &&
                Store.State[unit] == UnitAiState.Flank)
            {
                // If Utility AI interrupts a flanking run (lost target, suppression,
                // or a tactical change), cancel the stale destination immediately.
                Store.ClearGoal(unit);
                Store.State[unit] = UnitAiState.Search;
            }

            bool moving = units.Velocity[unit].LengthSquared() > 0.04f;
            if (intent == BehaviacUnitIntent.ProneEngage)
                units.Posture[unit] = UnitPosture.Lying;
            else if (suppressionState == UnitSuppressionState.Panicked)
                units.Posture[unit] = moving ? UnitPosture.Crouching : UnitPosture.Lying;
            else if (moving && units.Posture[unit] == UnitPosture.Lying)
            {
                // Keep a prone movement order low; UnitBodySystem animates it as a crawl.
            }
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
                    (intent == BehaviacUnitIntent.SeekCover))
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

            if (intent == BehaviacUnitIntent.InvestigateContact &&
                TryExecutePerceptionInvestigation(units, weapons, worldMap, unit))
            {
                continue;
            }

            if (intent == BehaviacUnitIntent.InvestigateSquadContact &&
                TryExecuteSquadContactInvestigation(units, weapons, worldMap, unit))
            {
                continue;
            }

            // Attack orders are objectives, not hard target locks. Keep the
            // nearest visible hostile selected so soldiers can switch threats.
            int visibleEnemy = _nearestVisibleEnemyByUnit[unit];

            bool keepCurrentVisibleTarget = false;

            if (Store.HasTarget[unit] &&
                !(Store.PlayerOrderType[unit] == UnitAiOrderType.Attack &&
                  IsCombatReadyTarget(units, health, Store.PlayerOrderTarget[unit]) &&
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
                        units.Position[currentTarget.Index],
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
                    units.Position[visibleEnemy],
                    TargetMemorySeconds);
            }

            // Utility AI can select Patrol only when there is neither a visible
            // target nor a remembered target. Drop an expired target before the
            // existing patrol/search path runs.
            if (intent == BehaviacUnitIntent.Patrol &&
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

            if (!IsCombatReadyTarget(units, health, target))
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

            if (intent == BehaviacUnitIntent.ProneEngage)
            {
                // Prone is a firing posture, not a command to crawl into the enemy.
                // Approach to normal weapon range while standing, then go prone.
                bool approachingForWeaponRange = false;
                if (targetVisible && HasRangedWeapon(units, inventory, unit))
                {
                    float range = GetMaxEffectiveRange(inventory, unit);
                    if (range > 0.1f)
                    {
                        Vector3 position = units.Position[unit];
                        Vector3 targetPosition = units.Position[target.Index];
                        float dx = targetPosition.X - position.X;
                        float dy = targetPosition.Y - position.Y;
                        float distanceSquared = dx * dx + dy * dy;
                        float desiredDistance = MathF.Max(3f, range * GetBalanceProfile(unit, units.FactionTag[unit]).RangedEngagementRangeFraction);
                        float approachThreshold = desiredDistance + MathF.Max(GetBalanceProfile(unit, units.FactionTag[unit]).RangedRangeToleranceMinimum, range * GetBalanceProfile(unit, units.FactionTag[unit]).RangedRangeToleranceFraction);
                        approachingForWeaponRange =
                            distanceSquared > approachThreshold * approachThreshold;
                    }
                }

                units.Posture[unit] = approachingForWeaponRange
                    ? UnitPosture.Standing
                    : UnitPosture.Lying;
            }
            else if (suppressionState == UnitSuppressionState.Panicked)
            {
                units.Posture[unit] = UnitPosture.Lying;
            }
            else if (moving && units.Posture[unit] == UnitPosture.Lying)
            {
                // Do not stand up mid-crawl merely because movement is active.
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
                (intent == BehaviacUnitIntent.RetreatToCover))
            {
                if (Store.State[unit] != UnitAiState.SeekCover)
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
                            GetBalanceProfile(unit, units.FactionTag[unit]),
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

            if (targetVisible &&
                intent == BehaviacUnitIntent.FlankTarget &&
                !_squadCoordinator.TryClaimFlank(units.GetId(unit), target, _tacticalTimeSeconds))
            {
                // One soldier handles the flank; the rest retain their local combat decisions.
                intent = BehaviacUnitIntent.EngageTarget;
            }

            if (targetVisible &&
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
                            vision,
                            worldMap,
                            unit,
                            target.Index,
                            GetMaxEffectiveRange(inventory, unit),
                            GetBalanceProfile(unit, units.FactionTag[unit]),
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
            else if (targetVisible &&
                     intent == BehaviacUnitIntent.ProneEngage)
            {
                // There is no dedicated prone-crawl waypoint; ordinary range
                // management below handles approach before the pawn lies down.
                Store.ClearGoal(unit);
            }
            else
            {
                Store.ClearGoal(unit);
            }

            if (targetVisible &&
                (intent is BehaviacUnitIntent.EngageTarget or BehaviacUnitIntent.ProneEngage or
                     BehaviacUnitIntent.HoldPositionEngage))
            {
                if (intent != BehaviacUnitIntent.HoldPositionEngage &&
                    HasRangedWeapon(units, inventory, unit))
                {
                    Vector3 position = units.Position[unit];
                    Vector3 targetPosition = units.Position[target.Index];
                    float range = GetMaxEffectiveRange(inventory, unit);
                    if (range > 0.1f)
                    {
                        float dx = targetPosition.X - position.X;
                        float dy = targetPosition.Y - position.Y;
                        float distanceSquared = dx * dx + dy * dy;
                        float desiredDistance = MathF.Max(3f, range * GetBalanceProfile(unit, units.FactionTag[unit]).RangedEngagementRangeFraction);
                        float approachThreshold = desiredDistance + MathF.Max(GetBalanceProfile(unit, units.FactionTag[unit]).RangedRangeToleranceMinimum, range * GetBalanceProfile(unit, units.FactionTag[unit]).RangedRangeToleranceFraction);

                        if (distanceSquared > approachThreshold * approachThreshold)
                        {
                            float distance = MathF.Sqrt(distanceSquared);
                            float scale = desiredDistance / MathF.Max(0.001f, distance);
                            units.Target[unit] = new Vector3(
                                targetPosition.X - dx * scale,
                                targetPosition.Y - dy * scale,
                                position.Z);
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
                        float desiredDistance = MathF.Max(GetBalanceProfile(unit, units.FactionTag[unit]).MeleeMinimumEngagementDistance, attackDistance * GetBalanceProfile(unit, units.FactionTag[unit]).MeleeEngagementRangeFraction);
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

            if (Store.TargetMemory[unit] > 0f &&
                (intent == BehaviacUnitIntent.SuppressiveFire ||
                 intent == BehaviacUnitIntent.InvestigateLastKnownTarget))
            {
                if ((intent == BehaviacUnitIntent.SuppressiveFire) &&
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

        if (intent == BehaviacUnitIntent.FollowMoveOrder &&
            store.PlayerOrderType[unit] == UnitAiOrderType.StealthMove)
        {
            Vector3 finalDestination = store.PlayerOrderPosition[unit];
            if (ReachedGoal(units.Position[unit], finalDestination))
            {
                store.SetPlayerOrder(unit, UnitAiOrderType.Stop, units.Position[unit]);
                store.ClearTarget(unit);
                store.ClearGoal(unit);
                ClearAiming(weapons, unit);
                units.Target[unit] = units.Position[unit];
                units.HasTarget[unit] = false;
                units.Posture[unit] = UnitPosture.Standing;
                store.State[unit] = UnitAiState.Idle;
                return true;
            }

            AiStealthProfile stealthProfile = GetStealthProfile(units.FactionTag[unit]).Sanitized();
            AiStealthRisk currentRisk = AiStealthMovementPlanner.MeasureRisk(
                units, worldMap, vision, _perceptionMemory, unit, units.Position[unit], stealthProfile);
            units.Posture[unit] = AiStealthMovementPlanner.SelectPosture(currentRisk, stealthProfile);

            if (!store.HasGoal[unit] || ReachedGoal(units.Position[unit], store.Goal[unit]))
            {
                AiStealthWaypointPlan plan = AiStealthMovementPlanner.SelectNextWaypoint(
                    units, worldMap, vision, _perceptionMemory, unit, finalDestination, stealthProfile);
                Vector3 next = plan.Destination;
                if (ReachedGoal(units.Position[unit], next))
                    next = finalDestination;
                store.SetGoal(unit, next);
            }

            store.ClearTarget(unit);
            ClearAiming(weapons, unit);
            units.Target[unit] = store.Goal[unit];
            units.HasTarget[unit] = true;
            store.State[unit] = UnitAiState.Search;
            return true;
        }

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

            // Utility AI selected the task; this action translates it into the
            // existing navigation goal without bypassing the shared execution systems.
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
            IsCombatReadyTarget(units, health, target) &&
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
        if (hasLiveTarget && vision.IsRecentlyVisible(unit, target.Index, units))
        {
            destination = units.Position[target.Index];
            store.SetPlayerOrder(unit, UnitAiOrderType.Attack, destination, target);
            store.RememberTarget(unit, target, destination, TargetMemorySeconds);
        }

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

    private void RecordUtilityIntent(BehaviacUnitIntent intent)
    {
        LastUtilityDecisions++;
        TotalUtilityDecisions++;

        switch (intent)
        {
            case BehaviacUnitIntent.Dead: LastUtilityDeadDecisions++; break;
            case BehaviacUnitIntent.SeekCover: LastUtilityCoverDecisions++; break;
            case BehaviacUnitIntent.RetreatToCover: LastUtilityRetreatDecisions++; break;
            case BehaviacUnitIntent.FlankTarget: LastUtilityFlankDecisions++; break;
            case BehaviacUnitIntent.EngageTarget:
            case BehaviacUnitIntent.ProneEngage: LastUtilityEngageDecisions++; break;
            case BehaviacUnitIntent.SuppressiveFire: LastUtilitySuppressDecisions++; break;
            case BehaviacUnitIntent.InvestigateLastKnownTarget:
            case BehaviacUnitIntent.InvestigateContact:
            case BehaviacUnitIntent.InvestigateSquadContact:
                LastUtilityInvestigateDecisions++;
                break;
            case BehaviacUnitIntent.Patrol: LastUtilityPatrolDecisions++; break;
            case BehaviacUnitIntent.FollowMoveOrder: LastUtilityMoveOrderDecisions++; break;
            case BehaviacUnitIntent.HoldPosition: LastUtilityHoldOrderDecisions++; break;
            case BehaviacUnitIntent.AttackOrder: LastUtilityAttackOrderDecisions++; break;
            case BehaviacUnitIntent.HoldPositionEngage: LastUtilityHoldAndEngageDecisions++; break;
        }
    }

    private bool TryExecuteSquadContactInvestigation(
        UnitStore units,
        UnitWeaponStore weapons,
        WorldMap worldMap,
        int unit)
    {
        UnitId receiver = units.GetId(unit);
        if (!_squadCoordinator.TryGetContact(
                receiver, _tacticalTimeSeconds, out SquadTacticalContact contact) ||
            !_squadCoordinator.TryClaimInvestigation(receiver, contact.Sequence, _tacticalTimeSeconds) ||
            !_squadCoordinator.MarkInvestigated(receiver, contact.Sequence))
            return false;

        Vector3 estimate = contact.EstimatedPosition;
        float angle = (contact.Sequence * 2.39996323f + unit * 0.75487767f) % MathF.Tau;
        float searchOffset = Math.Clamp(contact.PositionErrorRadius * 0.22f, 0.75f, 5f);
        float x = Math.Clamp(estimate.X + MathF.Cos(angle) * searchOffset, 0.5f, worldMap.MaxTileX - 0.5f);
        float y = Math.Clamp(estimate.Y + MathF.Sin(angle) * searchOffset, 0.5f, worldMap.MaxTileY - 0.5f);
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
        Vector3 searchPoint = new(x, y, worldMap.GetSurfaceHeight(tileX, tileY));

        // A squad report is an area to investigate, never an identified combat target.
        ClearAiming(weapons, unit);
        Store.ClearTarget(unit);
        Store.SetGoal(unit, searchPoint);
        units.Target[unit] = searchPoint;
        units.HasTarget[unit] = true;
        Store.State[unit] = UnitAiState.Search;
        _isContextInvestigationGoal[unit] = true;
        return true;
    }

    private bool TryExecuteLastKnownInvestigation(
        UnitStore units,
        UnitWeaponStore weapons,
        WorldMap worldMap,
        int unit)
    {
        TacticalContact? contextContact = _rememberedEnemyContextByUnit[unit];
        bool hasStoredTarget = Store.HasTarget[unit] && Store.TargetMemory[unit] > 0f;
        if (!hasStoredTarget &&
            (!contextContact.HasValue ||
             !contextContact.Value.IsValid ||
             contextContact.Value.ExpiresAtSeconds <= (_perceptionMemory?.CurrentTimeSeconds ?? 0f)))
        {
            _rememberedEnemyContextByUnit[unit] = null;
            return false;
        }

        UnitId storedTarget = hasStoredTarget ? Store.Target[unit] : default;
        Vector3 estimate = hasStoredTarget
            ? Store.LastSeenPosition[unit]
            : contextContact.Value.EstimatedPosition;

        if (!float.IsFinite(estimate.X) ||
            !float.IsFinite(estimate.Y) ||
            !float.IsFinite(estimate.Z) ||
            estimate == Vector3.Zero)
        {
            _rememberedEnemyContextByUnit[unit] = null;
            return false;
        }

        if (_perceptionMemory?.TryGetMemory(unit, out TacticalContactMemory memory) == true)
        {
            if (contextContact.HasValue && contextContact.Value.IsValid)
            {
                TacticalContact rememberedContext = contextContact.Value;
                memory.MarkInvestigated(rememberedContext.ContactId);
                TacticalContact? cachedContext = _cachedRememberedEnemyContextByUnit[unit];
                if (cachedContext.HasValue && cachedContext.Value.Equals(rememberedContext))
                    _cachedRememberedEnemyContextByUnit[unit] = null;
                _nextRememberedEnemyQuerySecondsByUnit[unit] = _perceptionMemory.CurrentTimeSeconds;
            }
            else if (storedTarget.IsValid &&
                     memory.TryGetContact(storedTarget, out TacticalContact rememberedTarget) &&
                     rememberedTarget.LastVisualAtSeconds >= 0f)
            {
                memory.MarkInvestigated(rememberedTarget.ContactId);
            }
        }

        _rememberedEnemyContextByUnit[unit] = null;
        float x = Math.Clamp(estimate.X, 0.5f, worldMap.MaxTileX - 0.5f);
        float y = Math.Clamp(estimate.Y, 0.5f, worldMap.MaxTileY - 0.5f);
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
        Vector3 destination = new(x, y, worldMap.GetSurfaceHeight(tileX, tileY));

        Store.ClearTarget(unit);
        Store.ClearGoal(unit);
        ClearAiming(weapons, unit);
        Store.SetGoal(unit, destination);
        units.Target[unit] = destination;
        units.HasTarget[unit] = true;
        Store.State[unit] = UnitAiState.Search;
        _isContextInvestigationGoal[unit] = true;
        return true;
    }

    private bool TryExecutePerceptionInvestigation(
        UnitStore units,
        UnitWeaponStore weapons,
        WorldMap worldMap,
        int unit)
    {
        if (!TryGetBestInvestigableContact(units, unit, out TacticalContact contact, out float utility) ||
            utility < GetBalanceProfile(unit, units.FactionTag[unit]).MinimumInvestigationUtility ||
            _perceptionMemory == null ||
            !_perceptionMemory.TryGetMemory(unit, out TacticalContactMemory memory))
            return false;

        Vector3 estimate = contact.EstimatedPosition;
        float angle = ((contact.ContactId * 2.39996323f) + unit * 0.75487767f) % MathF.Tau;
        float searchOffset = Math.Clamp(contact.PositionErrorRadius * 0.15f, 0.5f, 6f);
        float x = Math.Clamp(estimate.X + MathF.Cos(angle) * searchOffset, 0.5f, worldMap.MaxTileX - 0.5f);
        float y = Math.Clamp(estimate.Y + MathF.Sin(angle) * searchOffset, 0.5f, worldMap.MaxTileY - 0.5f);
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
        Vector3 searchPoint = new(x, y, worldMap.GetSurfaceHeight(tileX, tileY));

        // Nonvisual evidence provides only a search location, not an identified combat target.
        ClearAiming(weapons, unit);
        Store.ClearTarget(unit);
        Store.SetGoal(unit, searchPoint);
        units.Target[unit] = searchPoint;
        units.HasTarget[unit] = true;
        Store.State[unit] = UnitAiState.Search;
        _isContextInvestigationGoal[unit] = true;
        memory.MarkInvestigated(contact.ContactId);
        return true;
    }

    private bool TryGetBestInvestigableContact(
        UnitStore units,
        int unit,
        out TacticalContact bestContact,
        out float bestUtility)
    {
        bestContact = default;
        bestUtility = 0f;
        if (_perceptionMemory == null ||
            !_perceptionMemory.TryGetMemory(unit, out TacticalContactMemory memory))
            return false;

        float now = _perceptionMemory.CurrentTimeSeconds;
        foreach (TacticalContact candidate in memory.Contacts)
        {
            if (candidate.IsInvestigated ||
                candidate.ExpiresAtSeconds <= now ||
                candidate.Kind == TacticalContactKind.VisualConfirmed ||
                (candidate.GetActiveSignals(now) & PerceptionSignalFlags.Investigate) == 0)
                continue;

            float confidence = candidate.GetEffectiveConfidence(now, 2.5f);
            if (confidence <= 0f)
                continue;

            Vector3 delta = candidate.EstimatedPosition - units.Position[unit];
            float distance = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            float utility = UnitTacticalUtility.EvaluateInvestigation(
                confidence, distance, candidate.PositionErrorRadius, GetBalanceProfile(unit, units.FactionTag[unit]));
            if (utility <= bestUtility)
                continue;

            bestContact = candidate;
            bestUtility = utility;
        }

        return bestContact.IsValid;
    }

    private bool HasPerceivedSignal(
        int unit,
        PerceptionSignalFlags signal,
        float confidenceHalfLife,
        float minimumConfidence)
    {
        if (_perceptionMemory == null ||
            !_perceptionMemory.TryGetMemory(unit, out TacticalContactMemory memory))
            return false;

        float now = _perceptionMemory.CurrentTimeSeconds;
        foreach (TacticalContact contact in memory.Contacts)
        {
            if (contact.ExpiresAtSeconds <= now ||
                (contact.GetActiveSignals(now) & signal) == 0)
                continue;
            if (contact.GetEffectiveConfidence(now, confidenceHalfLife) >= minimumConfidence)
                return true;
        }

        return false;
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
        EnsurePlanningCapacity(units.Capacity);
        _rememberedEnemyContextByUnit[unit] = null;

        UnitAiOrderType orderType = Store.PlayerOrderType[unit];
        bool hasAttackOrder = orderType == UnitAiOrderType.Attack;
        UnitId attackTarget = Store.PlayerOrderTarget[unit];
        if (hasAttackOrder &&
            attackTarget.IsValid &&
            (!units.IsAlive(attackTarget) ||
             health.OverallHitPoints[attackTarget.Index] <= 0f ||
              units.Posture[attackTarget.Index] == UnitPosture.Injured ||
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
                vision.IsRecentlyVisible(unit, attackTarget.Index, units))
            {
                visibleTargetIndex = attackTarget.Index;
            }

            if (visibleTargetIndex >= 0)
            {
                // Switching target also updates the attack task's last-known
                // location, so it can continue searching if LOS is lost again.
                UnitId nearestTarget = units.GetId(visibleTargetIndex);
                Vector3 targetPosition = units.Position[visibleTargetIndex];
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
                // the utility scorer, so it cannot plan around a distant target this tick.
                visibleTargetIndex = currentTarget.Index;
            }
        }

        bool hasVisibleTarget = visibleTargetIndex >= 0;

        bool isInvestigatingContext = _isContextInvestigationGoal[unit] &&
            Store.HasGoal[unit] &&
            !ReachedGoal(units.Position[unit], Store.Goal[unit]);
        if (_isContextInvestigationGoal[unit] && !isInvestigatingContext)
        {
            _isContextInvestigationGoal[unit] = false;
            if (Store.HasGoal[unit] && Store.State[unit] == UnitAiState.Search)
            {
                Store.ClearGoal(unit);
                units.HasTarget[unit] = false;
            }
        }

        TacticalContact rememberedEnemyContext = default;
        float lastKnownTargetUtility = 0f;
        bool hasActiveTargetMemory = Store.HasTarget[unit] && Store.TargetMemory[unit] > 0f;
        bool hasRecentTarget = hasActiveTargetMemory;
        if (!hasVisibleTarget && !hasAttackOrder && !isInvestigatingContext)
        {
            if (hasRecentTarget &&
                _perceptionMemory?.TryGetMemory(unit, out TacticalContactMemory currentMemory) == true &&
                currentMemory.TryGetContact(Store.Target[unit], out TacticalContact currentContact) &&
                currentContact.LastVisualAtSeconds >= 0f)
            {
                float confidence = currentContact.GetEffectiveConfidence(
                    _perceptionMemory.CurrentTimeSeconds, 4f);
                float dx = currentContact.EstimatedPosition.X - units.Position[unit].X;
                float dy = currentContact.EstimatedPosition.Y - units.Position[unit].Y;
                lastKnownTargetUtility = UnitTacticalUtility.EvaluateInvestigation(
                    confidence, MathF.Sqrt(dx * dx + dy * dy), GetBalanceProfile(unit, units.FactionTag[unit]));
            }
            else if (hasRecentTarget)
            {
                float dx = Store.LastSeenPosition[unit].X - units.Position[unit].X;
                float dy = Store.LastSeenPosition[unit].Y - units.Position[unit].Y;
                lastKnownTargetUtility = UnitTacticalUtility.EvaluateInvestigation(
                    0.35f, MathF.Sqrt(dx * dx + dy * dy), GetBalanceProfile(unit, units.FactionTag[unit]));
            }
            else
            {
                if (TryGetBestRememberedHostileContact(
                    units, health, vision, unit, out rememberedEnemyContext))
                {
                    _rememberedEnemyContextByUnit[unit] = rememberedEnemyContext;
                    hasRecentTarget = true;
                    float dx = rememberedEnemyContext.EstimatedPosition.X - units.Position[unit].X;
                    float dy = rememberedEnemyContext.EstimatedPosition.Y - units.Position[unit].Y;
                    float confidence = rememberedEnemyContext.GetEffectiveConfidence(
                        _perceptionMemory!.CurrentTimeSeconds, 4f);
                    lastKnownTargetUtility = UnitTacticalUtility.EvaluateInvestigation(
                        confidence, MathF.Sqrt(dx * dx + dy * dy), GetBalanceProfile(unit, units.FactionTag[unit]));
                }
            }
        }

        if (hasVisibleTarget)
            suppression.RefreshAlertness(unit, 0.82f, units.Position[visibleTargetIndex]);
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
                IsLowHealth(health, unit), GetBalanceProfile(unit, units.FactionTag[unit]));
        }

        float combatUtility = 0f;
        if (hasVisibleTarget)
        {
            Vector3 enemyDelta = units.Position[visibleTargetIndex] - units.Position[unit];
            float enemyDistance = MathF.Sqrt(enemyDelta.X * enemyDelta.X + enemyDelta.Y * enemyDelta.Y);
            combatUtility = UnitTacticalUtility.EvaluateVisibleCombat(enemyDistance, true, GetBalanceProfile(unit, units.FactionTag[unit]));
        }

        bool isSuppressed =
            suppressionState is UnitSuppressionState.Panicked or UnitSuppressionState.Suppressed;
        float healthRatio = health.OverallMaxHitPoints[unit] > 0f
            ? health.OverallHitPoints[unit] / health.OverallMaxHitPoints[unit]
            : 0f;
        bool isLowHealth = healthRatio < LowHealthThreshold;
        bool hasSuppressiveWeapon = HasSuppressFireWeapon(inventory, weapons, unit);
        bool hasRangedWeapon = HasRangedWeapon(units, inventory, unit);
        bool isHealthyEnoughToFlank = !isLowHealth && !isSuppressed;
        bool canFlank = false;
        float flankDistance = 0f;
        float flankEffectiveRange = 0f;

        // Flanking is a ranged-fire maneuver. A melee-only unit should
        // commit toward contact instead of spending its first approach on a
        // wide side/back waypoint.
        if (hasVisibleTarget &&
            isHealthyEnoughToFlank &&
            hasRangedWeapon &&
            unit % 8 == 0)
        {
            Vector3 delta = units.Position[visibleTargetIndex] - units.Position[unit];
            float distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            flankDistance = MathF.Sqrt(distanceSquared);
            bool alreadyFlankedThisTarget = _hasFlankTargetByUnit[unit] &&
                _flankTargetByUnit[unit].Equals(units.GetId(visibleTargetIndex));

            // Flanking is a small long-range maneuver, not the default response
            // to contact. Most shooters should open fire from their current lane.
            float maxFlankRange = MathF.Min(75f, MathF.Max(25f, units.ViewRange[unit] * 0.85f));
            float effectiveRange = GetMaxEffectiveRange(inventory, unit);
            flankEffectiveRange = effectiveRange;
            bool stillOutsideWeaponRange = effectiveRange > 0.1f &&
                distanceSquared > effectiveRange * effectiveRange;
            canFlank = !alreadyFlankedThisTarget &&
                stillOutsideWeaponRange &&
                distanceSquared >= 24f * 24f &&
                distanceSquared <= maxFlankRange * maxFlankRange;
        }

        bool isFlanking =
            Store.State[unit] == UnitAiState.Flank &&
            isHealthyEnoughToFlank &&
            hasRangedWeapon;
        float flankUtility = UnitTacticalUtility.EvaluateFlanking(
            flankDistance,
            flankEffectiveRange,
            canFlank,
            GetBalanceProfile(unit, units.FactionTag[unit]));

        float proneUtility = 0f;
        if (hasVisibleTarget &&
            hasRangedWeapon &&
            suppressionState == UnitSuppressionState.Calm &&
            !isLowHealth &&
            !canFlank &&
            !isFlanking)
        {
            Vector3 targetDelta = units.Position[visibleTargetIndex] - units.Position[unit];
            float distance = MathF.Sqrt(
                targetDelta.X * targetDelta.X + targetDelta.Y * targetDelta.Y);
            bool exposed = !_hasSuppressionCoverByUnit[unit] &&
                !(Store.HasGoal[unit] && Store.State[unit] == UnitAiState.SeekCover);
            proneUtility = UnitTacticalUtility.EvaluateProneFiring(
                distance,
                exposed,
                healthRatio,
                GetBalanceProfile(unit, units.FactionTag[unit]));
        }

        bool hasCoverGoal = Store.HasGoal[unit] &&
            Store.State[unit] == UnitAiState.SeekCover;
        bool hasCover = suppressionState == UnitSuppressionState.Panicked
            ? _hasSuppressionCoverByUnit[unit] || hasCoverGoal
            : (_hasSuppressionCoverByUnit[unit] &&
               !ReachedGoal(units.Position[unit], _suppressionCoverByUnit[unit])) ||
              (hasCoverGoal && !ReachedGoal(units.Position[unit], Store.Goal[unit]));

        bool hasInvestigableContact = false;
        float investigationUtility = 0f;
        if (!isInvestigatingContext &&
            TryGetBestInvestigableContact(units, unit, out _, out investigationUtility))
        {
            hasInvestigableContact =
                investigationUtility >= GetBalanceProfile(unit, units.FactionTag[unit]).MinimumInvestigationUtility;
        }

        bool hasSquadContact = false;
        float squadContactUtility = 0f;
        if (!isInvestigatingContext &&
            _squadCoordinator.TryGetContact(
                units.GetId(unit), _tacticalTimeSeconds, out SquadTacticalContact squadContact))
        {
            Vector3 squadDelta = squadContact.EstimatedPosition - units.Position[unit];
            float squadDistance = MathF.Sqrt(
                squadDelta.X * squadDelta.X + squadDelta.Y * squadDelta.Y);
            squadContactUtility = UnitTacticalUtility.EvaluateInvestigation(
                squadContact.Confidence, squadDistance, GetBalanceProfile(unit, units.FactionTag[unit]));
            hasSquadContact = squadContactUtility >= GetBalanceProfile(unit, units.FactionTag[unit]).MinimumSquadContactUtility;
        }

        bool hasImmediateDanger = suppression.IsInImmediateDanger(unit) ||
            HasPerceivedSignal(unit, PerceptionSignalFlags.ImmediateDanger, 0.75f, 0.14f);
        isAlerted |= hasImmediateDanger ||
            HasPerceivedSignal(unit, PerceptionSignalFlags.PotentialThreat, 2.5f, 0.16f);
        float coverUtility = UnitTacticalUtility.EvaluateCover(
            suppression.Value[unit],
            hasCover,
            hasImmediateDanger,
            suppressionState == UnitSuppressionState.Panicked,
            isLowHealth,
            GetBalanceProfile(unit, units.FactionTag[unit]));

        float fatigue = _metabolism != null && unit < _metabolism.Capacity
            ? _metabolism.Fatigue[unit] / 100f
            : 0f;
        float stress = _metabolism != null && unit < _metabolism.Capacity
            ? _metabolism.Stress[unit] / 100f
            : 0f;
        float energyRatio = _metabolism != null &&
            unit < _metabolism.Capacity &&
            _metabolism.MaxEnergyKcal[unit] > 0f
                ? _metabolism.EnergyKcal[unit] / _metabolism.MaxEnergyKcal[unit]
                : 1f;
        float hydrationRatio = _metabolism != null &&
            unit < _metabolism.Capacity &&
            _metabolism.MaxWaterLiters[unit] > 0f
                ? _metabolism.WaterLiters[unit] / _metabolism.MaxWaterLiters[unit]
                : 1f;
        float shootingSkill = _skills != null
            ? _skills.GetLevel(unit, UnitSkill.Shooting) / (float)UnitSkillStore.MaxLevel
            : 0.5f;
        float meleeSkill = _skills != null
            ? _skills.GetLevel(unit, UnitSkill.Melee) / (float)UnitSkillStore.MaxLevel
            : 0.5f;

        var utilityContext = new UnitIntentUtilityContext
        {
            IsAlive = isAlive,
            HasVisibleTarget = hasVisibleTarget,
            HasRecentTarget = hasRecentTarget,
            HasActiveTargetMemory = hasActiveTargetMemory,
            HasCover = hasCover,
            IsInImmediateDanger = hasImmediateDanger,
            IsSuppressed = isSuppressed,
            IsPanicked = suppressionState == UnitSuppressionState.Panicked,
            IsLowHealth = isLowHealth,
            HasSuppressiveWeapon = hasSuppressiveWeapon,
            HasRangedWeapon = hasRangedWeapon,
            CanFlank = canFlank,
            IsFlanking = isFlanking,
            IsProne = units.Posture[unit] == UnitPosture.Lying,
            IsAlerted = isAlerted,
            HasInvestigableContact = hasInvestigableContact,
            HasSquadContact = hasSquadContact,
            IsInvestigatingContext = isInvestigatingContext,
            OrderType = orderType,
            HealthRatio = healthRatio,
            Pain = health.Pain[unit] / 100f,
            Fatigue = fatigue,
            Stress = stress,
            EnergyRatio = energyRatio,
            HydrationRatio = hydrationRatio,
            SuppressionResistance = _skills?.GetSuppressionResistance(unit) ?? 0.5f,
            ShootingSkill = shootingSkill,
            MeleeSkill = meleeSkill,
            Caution = Personality.Caution[unit],
            Aggression = Personality.Aggression[unit],
            Discipline = Personality.Discipline[unit],
            Alertness = suppression.GetAlertness(unit),
            MoveOrderUtility = moveOrderUtility,
            CombatUtility = combatUtility,
            CoverUtility = coverUtility,
            FlankUtility = flankUtility,
            ProneCombatUtility = proneUtility,
            LastKnownTargetUtility = lastKnownTargetUtility,
            InvestigationUtility = investigationUtility,
            SquadContactUtility = squadContactUtility
        };

        UnitIntentUtilityBreakdown utilityDecision = UnitIntentUtilityScorer.Select(
            in utilityContext,
            Store.UtilityIntent[unit],
            Store.UtilityIntentCommitRemainingSeconds[unit],
            GetBalanceProfile(unit, units.FactionTag[unit]));
        Store.UtilityIntentBreakdown[unit] = utilityDecision;
        Store.UtilityIntentContext[unit] = utilityContext;
        if (utilityDecision.SuggestedIntent != Store.UtilityIntent[unit])
        {
            Store.UtilityIntent[unit] = utilityDecision.SuggestedIntent;
            Store.UtilityIntentCommitRemainingSeconds[unit] =
                UnitIntentUtilityScorer.GetCommitDuration(Personality.Discipline[unit], GetBalanceProfile(unit, units.FactionTag[unit]));
        }

        return utilityDecision.SuggestedIntent;
    }

    private static bool TryFindFlankWaypoint(
        UnitStore units,
        VisionSystem vision,
        WorldMap worldMap,
        int unit,
        int targetIndex,
        float weaponRange,
        AiBalanceProfile balance,
        out Vector3 waypoint)
    {
        waypoint = Vector3.Zero;
        Vector3 position = units.Position[unit];
        Vector3 target = units.Position[targetIndex];
        Vector2 towardTarget = new(target.X - position.X, target.Y - position.Y);
        float distanceSquared = towardTarget.LengthSquared();
        if (!float.IsFinite(distanceSquared) || distanceSquared < 1f)
            return false;

        float distanceToTarget = MathF.Sqrt(distanceSquared);
        Vector2 forward = Vector2.Normalize(towardTarget);
        Vector2 side = new(-forward.Y, forward.X);
        float preferredSide = (unit & 1) == 0 ? 1f : -1f;
        float workingRange = weaponRange > 0.1f
            ? weaponRange * balance.RangedEngagementRangeFraction
            : distanceToTarget * 0.65f;

        float bestScore = float.NegativeInfinity;
        Vector3 bestWaypoint = Vector3.Zero;
        Vector3 targetEye = vision.GetEyePosition(units, targetIndex);

        // Compare all legal side/backoff points. A ranged flank should settle near
        // a useful firing distance, not blindly crawl to a fixed 14-cell orbit.
        // Existing weapon range and actual terrain line-of-sight remain authoritative.
        for (int candidate = 0; candidate < 4; candidate++)
        {
            bool nearOrbit = candidate < 2;
            float sideSign = candidate % 2 == 0 ? preferredSide : -preferredSide;
            float orbitFraction = nearOrbit
                ? balance.FlankNearRangeFraction
                : balance.FlankFarRangeFraction;
            float desiredOrbit = MathF.Min(
                distanceToTarget * 0.94f,
                MathF.Max(6f, workingRange * Math.Clamp(orbitFraction, 0.65f, 1f)));

            // The two components approximate the chosen orbit radius, while existing
            // minimum offsets keep close-range movement from degenerating into a nudge.
            float backoff = MathF.Max(
                nearOrbit ? balance.FlankNearBackoffDistance : balance.FlankFarBackoffDistance,
                desiredOrbit * balance.FlankBackoffProportion);
            float lateral = MathF.Max(
                nearOrbit ? balance.FlankNearLateralDistance : balance.FlankFarLateralDistance,
                desiredOrbit * balance.FlankLateralProportion);
            float targetX = target.X - forward.X * backoff + side.X * lateral * sideSign;
            float targetY = target.Y - forward.Y * backoff + side.Y * lateral * sideSign;
            targetX = Math.Clamp(targetX, 2f, worldMap.MaxTileX - 2f);
            targetY = Math.Clamp(targetY, 2f, worldMap.MaxTileY - 2f);

            int tileX = Math.Clamp((int)MathF.Floor(targetX), 0, worldMap.MaxTileX);
            int tileY = Math.Clamp((int)MathF.Floor(targetY), 0, worldMap.MaxTileY);
            if (tileX < 1 || tileY < 1 ||
                tileX >= worldMap.TileWidth - 1 ||
                tileY >= worldMap.TileHeight - 1 ||
                worldMap.IsNavigationBlocked(tileX, tileY) ||
                worldMap.GetSurfaceLayer(tileX, tileY) < 0)
            {
                continue;
            }

            Vector3 candidateWaypoint = new(
                tileX + 0.5f,
                tileY + 0.5f,
                worldMap.GetSurfaceHeight(tileX, tileY));
            Vector2 toWaypoint = new(
                candidateWaypoint.X - position.X,
                candidateWaypoint.Y - position.Y);
            if (toWaypoint.LengthSquared() <= 4f)
                continue;

            float candidateRange = Vector2.Distance(
                new Vector2(candidateWaypoint.X, candidateWaypoint.Y),
                new Vector2(target.X, target.Y));
            float travelDistance = toWaypoint.Length();
            bool hasLineOfSight = vision.HasLineOfSight(
                worldMap,
                candidateWaypoint + new Vector3(0f, 0f, MathF.Max(0.05f, units.Height[unit])),
                targetEye,
                out _);

            float score =
                -MathF.Abs(candidateRange - desiredOrbit) * balance.FlankRangeErrorWeight -
                travelDistance * balance.FlankTravelCostWeight +
                (hasLineOfSight ? balance.FlankLineOfSightBonus : 0f);

            if (score <= bestScore)
                continue;

            bestScore = score;
            bestWaypoint = candidateWaypoint;
        }

        if (!float.IsFinite(bestScore))
            return false;

        waypoint = bestWaypoint;
        return true;
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
        if (_unitBalanceProfiles.Length < capacity)
            Array.Resize(ref _unitBalanceProfiles, capacity);
        if (_activeUnitScratch.Length < capacity)
            Array.Resize(ref _activeUnitScratch, capacity);
        if (_unitThinkElapsedSeconds.Length < capacity)
            Array.Resize(ref _unitThinkElapsedSeconds, capacity);
        if (_unitThinkDeltaSeconds.Length < capacity)
            Array.Resize(ref _unitThinkDeltaSeconds, capacity);
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
        if (_rememberedEnemyContextByUnit.Length < capacity)
            Array.Resize(ref _rememberedEnemyContextByUnit, capacity);
        if (_cachedRememberedEnemyContextByUnit.Length < capacity)
            Array.Resize(ref _cachedRememberedEnemyContextByUnit, capacity);
        if (_nextRememberedEnemyQuerySecondsByUnit.Length < capacity)
            Array.Resize(ref _nextRememberedEnemyQuerySecondsByUnit, capacity);
        if (_isContextInvestigationGoal.Length < capacity)
            Array.Resize(ref _isContextInvestigationGoal, capacity);
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
        AiBalanceProfile balance = GetBalanceProfile(unit, units.FactionTag[unit]);
        bool foundCover = lowHealthHoldNeedsCover
            ? TryFindCover(units, vision, worldMap, unit, visibleEnemy, balance, out cover)
            : TryFindSuppressionCover(units, vision, worldMap, unit, threat, balance, out cover);

        if (!isPanicked)
            Store.SuppressionRetrySource[unit] = threat;

        if (foundCover)
        {
            _suppressionCoverByUnit[unit] = cover;
            _hasSuppressionCoverByUnit[unit] = true;
        }
        else if (!isPanicked)
        {
            // Suppressed Hold units should not redo a costly failed cover scan
            // every AI tick; retry after the existing short cooldown.
            Store.SuppressionCoverRetry[unit] = 1f;
        }
    }

    private bool TryGetBestRememberedHostileContact(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        int observer,
        out TacticalContact bestContact)
    {
        bestContact = default;
        if (_perceptionMemory == null ||
            !_perceptionMemory.TryGetMemory(observer, out TacticalContactMemory memory))
        {
            _cachedRememberedEnemyContextByUnit[observer] = null;
            return false;
        }

        float now = _perceptionMemory.CurrentTimeSeconds;
        TacticalContact? cached = _cachedRememberedEnemyContextByUnit[observer];
        if (cached.HasValue && cached.Value.IsValid &&
            now < _nextRememberedEnemyQuerySecondsByUnit[observer])
        {
            if (IsUsableRememberedHostileContact(
                    units, health, vision, observer, cached.Value, now, out _))
            {
                bestContact = cached.Value;
                return true;
            }

            _cachedRememberedEnemyContextByUnit[observer] = null;
            _nextRememberedEnemyQuerySecondsByUnit[observer] = now;
        }

        if (now < _nextRememberedEnemyQuerySecondsByUnit[observer])
            return false;
        _nextRememberedEnemyQuerySecondsByUnit[observer] = now + RememberedEnemyRefreshInterval;

        float bestUtility = GetBalanceProfile(observer, units.FactionTag[observer]).MinimumInvestigationUtility;
        foreach (TacticalContact candidate in memory.Contacts)
        {
            if (!IsUsableRememberedHostileContact(
                    units, health, vision, observer, candidate, now, out float utility) ||
                utility <= bestUtility)
            {
                continue;
            }

            bestUtility = utility;
            bestContact = candidate;
        }

        _cachedRememberedEnemyContextByUnit[observer] =
            bestContact.IsValid ? bestContact : null;
        return bestContact.IsValid;
    }

    private bool IsUsableRememberedHostileContact(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        int observer,
        TacticalContact contact,
        float now,
        out float utility)
    {
        utility = 0f;
        if (!contact.IsValid ||
            contact.IsInvestigated ||
            contact.ExpiresAtSeconds <= now ||
            contact.LastVisualAtSeconds < 0f ||
            contact.SubjectUnit is not UnitId subject ||
            !units.TryGetIndex(subject, out int target) ||
            health.OverallHitPoints[target] <= 0f ||
            !IsHostile(units, observer, target) ||
            vision.IsRecentlyVisible(observer, target, units))
        {
            return false;
        }

        float age = now - contact.LastVisualAtSeconds;
        if (!float.IsFinite(age) || age < 0f || age > RememberedEnemyMemorySeconds)
            return false;

        float confidence = contact.GetEffectiveConfidence(now, 4f);
        if (confidence < 0.12f)
            return false;

        float dx = contact.EstimatedPosition.X - units.Position[observer].X;
        float dy = contact.EstimatedPosition.Y - units.Position[observer].Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        utility = UnitTacticalUtility.EvaluateInvestigation(confidence, distance, GetBalanceProfile(observer, units.FactionTag[observer]));
        return utility > GetBalanceProfile(observer, units.FactionTag[observer]).MinimumInvestigationUtility;
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
                units.Posture[target] == UnitPosture.Injured ||
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

    private static bool ShouldKeepCurrentVisibleTarget(
        UnitStore units,
        UnitHealthStore health,
        VisionSystem vision,
        UnitInventoryStore inventory,
        int observer,
        UnitId currentTarget,
        int newlyVisibleTarget)
    {
        if (!IsCombatReadyTarget(units, health, currentTarget) ||
            !IsHostile(units, observer, currentTarget.Index) ||
            !vision.IsRecentlyVisible(observer, currentTarget.Index, units))
        {
            return false;
        }

        if (newlyVisibleTarget < 0 || newlyVisibleTarget == currentTarget.Index)
            return true;

        Vector3 observerPosition = units.Position[observer];
        Vector3 newPosition = units.Position[newlyVisibleTarget];
        float newDx = newPosition.X - observerPosition.X;
        float newDy = newPosition.Y - observerPosition.Y;
        float newDistanceSquared = newDx * newDx + newDy * newDy;
        float contactRange = MathF.Max(
            VisionSystem.CloseAwarenessRange,
            MeleeCombatSystem.GetAttackDistance(
                observer, newlyVisibleTarget, units, inventory));

        if (newDistanceSquared > contactRange * contactRange)
            return true;

        Vector3 currentPosition = units.Position[currentTarget.Index];
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

    private static bool IsCombatReadyTarget(
        UnitStore units,
        UnitHealthStore health,
        UnitId target)
    {
        return units.IsAlive(target) &&
            health.OverallHitPoints[target.Index] > 0f &&
            units.Posture[target.Index] != UnitPosture.Injured;
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
        AiBalanceProfile balance,
        out Vector3 cover)
    {
        cover = Vector3.Zero;

        Vector3 origin =
            units.Position[unit];

        float bestScore = float.NegativeInfinity;

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
                 radius <= balance.CoverSearchRadius;
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
                    tileY >= worldMap.TileHeight - 1 ||
                    worldMap.IsNavigationBlocked(tileX, tileY) ||
                    worldMap.GetSurfaceLayer(tileX, tileY) < 0)
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

                float travelDistance = Vector2.Distance(
                    new Vector2(candidate.X, candidate.Y),
                    new Vector2(origin.X, origin.Y));
                float threatDistance = Vector2.Distance(
                    new Vector2(candidate.X, candidate.Y),
                    new Vector2(threatPoint.X, threatPoint.Y));
                float score =
                    threatDistance * balance.CoverThreatSeparationWeight -
                    travelDistance * balance.CoverTravelCostWeight;

                if (score <= bestScore)
                    continue;

                bestScore = score;
                cover = candidate;
            }
        }

        return float.IsFinite(bestScore);
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
        AiBalanceProfile balance,
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

        float bestScore = float.NegativeInfinity;

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
                 radius <= balance.CoverSearchRadius;
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
                    tileY >= worldMap.TileHeight - 1 ||
                    worldMap.IsNavigationBlocked(tileX, tileY) ||
                    worldMap.GetSurfaceLayer(tileX, tileY) < 0)
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

                float travelDistance = Vector2.Distance(
                    new Vector2(candidate.X, candidate.Y),
                    new Vector2(origin.X, origin.Y));
                float threatDistance = Vector2.Distance(
                    new Vector2(candidate.X, candidate.Y),
                    new Vector2(enemyEye.X, enemyEye.Y));
                float score =
                    threatDistance * balance.CoverThreatSeparationWeight -
                    travelDistance * balance.CoverTravelCostWeight;

                if (score <= bestScore)
                    continue;

                bestScore = score;
                cover = candidate;
            }
        }

        return float.IsFinite(bestScore);
    }
}
