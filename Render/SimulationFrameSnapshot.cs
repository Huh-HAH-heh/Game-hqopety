using System;
using System.Numerics;
using System.Collections.Generic;
using System.Diagnostics;
using Core.Items;
using Core.AI;
using Core.Combat;
using Core.Map;
using Core.AI.Perception;
using Core.AI.Behaviac;
using Core.Unit;

namespace RimClone.Render;

public readonly record struct RenderWeaponVisual(
    WeaponConfig? Config,
    Vector3 AimPoint,
    bool AimIndicatorActive,
    FireMode FireMode,
    AimMode AimMode,
    TargetMode TargetMode,
    int Ammo,
    int ReserveAmmo,
    byte AmmoType,
    bool Reloading,
    float ReloadTimer,
    float Cooldown,
    float AimTimer,
    byte BurstRemaining,
    float Recoil);

public readonly record struct RenderUnitState(
    int Index,
    uint Generation,
    Vector3 Position,
    Vector3 BodyNormal,
    Vector3 HeadNormal,
    float Width,
    float Length,
    float HitPoints,
    float HealthRatio,
    ushort FactionTag,
    UnitAiState AiState,
    BehaviacUnitIntent AiIntent,
    bool HasTarget,
    Vector3 TargetPosition,
    bool HasGoal,
    Vector3 Goal,
    float Suppression,
    UnitSuppressionState SuppressionState,
    bool HasApparel,
    bool HasPoweredArmor,
    RenderWeaponVisual PrimaryWeapon,
    RenderWeaponVisual SecondaryWeapon,
    RenderWeaponVisual MeleeWeapon,
    RenderWeaponVisual UtilityWeapon,
    int BodyPartStart,
    int BodyPartCount,
    UnitType Type,
    UnitPosture Posture,
    Vector3 Velocity,
    float MoveSpeed,
    float Height,
    float Radius,
    float ViewRange,
    float FieldOfView,
    float MaxHitPoints,
    int LocationId,
    bool HasMoveTarget,
    Vector3 MoveTargetPosition,
    UnitAiOrderType PlayerOrderType,
    Vector3 PlayerOrderPosition,
    UnitId PlayerOrderTarget,
    int OrderPathStart,
    int OrderPathCount);

public readonly record struct RenderAimRing(Vector3 AimPoint, float Progress);

public readonly record struct RenderVisionContact(
    UnitId Target,
    Vector3 VisiblePoint,
    int VisiblePointIndex);

public readonly record struct RenderSoundPulse(
    Vector3 Position, TacticalStimulusType Type, float AudibleRange,
    float Intensity, float AgeSeconds);

public readonly record struct RenderAuditoryContact(
    UnitId Observer, Vector3 ObserverPosition, Vector3 EstimatedPosition,
    float ErrorRadius, float Confidence, float AgeSeconds, TacticalStimulusType Type);

public readonly record struct RenderMemoryContact(
    UnitId Observer,
    Vector3 ObserverPosition,
    UnitId? SubjectUnit,
    ushort SubjectFactionTag,
    Vector3 EstimatedPosition,
    float ErrorRadius,
    float Confidence,
    float AgeSeconds,
    float VisualAgeSeconds,
    TacticalContactKind Kind,
    TacticalStimulusType Type,
    bool HasVisualEvidence,
    bool HasAuditoryEvidence,
    bool HasCommunicationEvidence,
    bool IsInvestigated);

public readonly record struct RenderProjectileTrace(
    Vector3 Start,
    Vector3 End,
    ushort Faction,
    bool IsLaser,
    float Lifetime);

public readonly record struct RenderProjectileImpact(
    Vector3 Position,
    float Lifetime,
    ProjectileImpactKind Kind);

/// <summary>
/// Immutable horizontal body geometry captured for one render frame.
/// Scale and rotation come from the same articulated body transforms used by hit testing.
/// </summary>
public readonly record struct RenderBodyPart(
    Vector3 Position,
    Vector3 Scale,
    float Rotation,
    BodyPrimitive Primitive,
    BodyPartRole Role);

/// <summary>
/// Immutable data published by the simulation thread and consumed by the renderer.
/// No mutable simulation-store arrays escape this object.
/// </summary>
public sealed class SimulationFrameSnapshot
{
    public RenderUnitState[] Units { get; init; } = Array.Empty<RenderUnitState>();
    public Vector2[] OrderPathPoints { get; init; } = Array.Empty<Vector2>();
    public RenderBodyPart[] BodyParts { get; init; } = Array.Empty<RenderBodyPart>();
    public RenderProjectileTrace[] Traces { get; init; } = Array.Empty<RenderProjectileTrace>();
    public RenderAimRing[] AimRings { get; init; } = Array.Empty<RenderAimRing>();
    public UnitId VisionDebugObserver { get; init; }
    public Vector3 VisionDebugObserverEye { get; init; }
    public RenderVisionContact[] VisionContacts { get; init; } = Array.Empty<RenderVisionContact>();
    public RenderSoundPulse[] SoundPulses { get; init; } = Array.Empty<RenderSoundPulse>();
    public RenderAuditoryContact[] SoundContacts { get; init; } = Array.Empty<RenderAuditoryContact>();
    public RenderMemoryContact[] MemoryContacts { get; init; } = Array.Empty<RenderMemoryContact>();
    public RenderProjectileImpact[] Impacts { get; init; } = Array.Empty<RenderProjectileImpact>();
    public MeleeHitVisual[] MeleeHitVisuals { get; init; } = Array.Empty<MeleeHitVisual>();

    public int UnitCount => Units.Length;
    public int ActiveProjectiles { get; init; }
    public int ProjectileCapacity { get; init; }
    public long TotalShotsFired { get; init; }
    public long TotalRoundsConsumed { get; init; }
    public long TotalProjectilesSpawned { get; init; }
    public long TotalHits { get; init; }
    public string LastHitPart { get; init; } = "None";

    public bool AiEnabled { get; init; }
    public bool VisionEnabled { get; init; }
    public int BlueAlive { get; init; }
    public int BlueDead { get; init; }
    public int RedAlive { get; init; }
    public int RedDead { get; init; }
    public int ThirdFactionAlive { get; init; }
    public long MeleeSwings { get; init; }
    public long MeleeHits { get; init; }
    public int AttackCount { get; init; }
    public int SearchCount { get; init; }
    public int CoverCount { get; init; }
    public int IdleCount { get; init; }
    public int DeadCount { get; init; }
    public int SuppressedCount { get; init; }
    public int PanickedCount { get; init; }

    public double SnapshotCaptureMs { get; init; }
    public long SnapshotAllocatedBytes { get; init; }
    public long PerceptionAllocatedBytes { get; init; }
    public double SimulationMs { get; init; }
    public double PerceptionMs { get; init; }
    public double HearingSenseMs { get; init; }
    public double VisualSenseMs { get; init; }
    public double VisualPublishMs { get; init; }
    public int VisualObservationsPublished { get; init; }
    public int HearingQueuedPulses { get; init; }
    public int HearingListenerChecks { get; init; }
    public int HearingListenerCellsVisited { get; init; }
    public int HearingContactsRecorded { get; init; }
    public int GunshotEventsQueued { get; init; }
    public int GunshotBatchesQueued { get; init; }
    public int GunshotsMerged { get; init; }
    public double SuppressionMs { get; init; }
    public double BodyMs { get; init; }
    public double MeleeMs { get; init; }
    public double VisionMs { get; init; }
    public long VisionCandidatePairs { get; init; }
    public long VisionTargetEvaluations { get; init; }
    public long VisionLineOfSightChecks { get; init; }
    public long VisionCandidatesScanned { get; init; }
    public long VisionMemoryEntriesScanned { get; init; }
    public double AiMs { get; init; }
    public double AiPlanningMs { get; init; }
    public bool UtilityAiEnabled { get; init; }
    public bool UtilityAiReady { get; init; }
    public int UtilityAiDecisionCount { get; init; }
    public double UtilityAiDecisionMs { get; init; }
    public int UtilityAiDeadCount { get; init; }
    public int UtilityAiCoverCount { get; init; }
    public int UtilityAiRetreatCount { get; init; }
    public int UtilityAiFlankCount { get; init; }
    public int UtilityAiEngageCount { get; init; }
    public int UtilityAiInvestigateCount { get; init; }
    public bool HasUtilityAiDebugState { get; init; }
    public UnitId UtilityAiDebugObserver { get; init; }
    public UnitIntentUtilityBreakdown UtilityAiDebugDecision { get; init; }
    public UnitIntentUtilityContext UtilityAiDebugContext { get; init; }
    public bool BehaviacEnabled { get; init; }
    public bool BehaviacRuntimeReady { get; init; }
    public int BehaviacDecisionCount { get; init; }
    public double BehaviacDecisionMs { get; init; }
    public int BehaviacDeadCount { get; init; }
    public int BehaviacCoverCount { get; init; }
    public int BehaviacRetreatCount { get; init; }
    public int BehaviacFlankCount { get; init; }
    public int BehaviacEngageCount { get; init; }
    public int BehaviacInvestigateCount { get; init; }
    public double MovementMs { get; init; }
    public double WeaponsMs { get; init; }
    public double BallisticsMs { get; init; }
    public double ProjectileGridMs { get; init; }
    public int ProjectilesVisited { get; init; }
    public int TerrainCellsTraced { get; init; }
    public int ProjectileUnitCandidates { get; init; }
    public double HealthMs { get; init; }
    public int RoutesBuilt { get; init; }
    public int RoutesFailed { get; init; }
    public int NavigationSearches { get; init; }
    public long NavigationCellsExpanded { get; init; }
    public double NavigationSearchMs { get; init; }
    public double NavigationGridMs { get; init; }
    public int RoutesReused { get; init; }
    public long RouteTrafficCells { get; init; }

    private static RenderWeaponVisual CaptureWeaponVisual(
        UnitInventoryStore inventory,
        UnitWeaponStore weapons,
        int unitIndex,
        UnitWeaponSlot slot)
    {
        short itemSlot = inventory.GetWeaponEquipment(unitIndex, slot);
        WeaponConfig? config = itemSlot >= 0
            ? inventory.GetItem(unitIndex, itemSlot) as WeaponConfig
            : null;

        int stateIndex = UnitWeaponStore.GetIndex(unitIndex, slot);
        return new RenderWeaponVisual(
            config,
            weapons.AimPoint[stateIndex],
            weapons.AimIndicatorActive[stateIndex],
            weapons.CurrentFireMode[stateIndex],
            weapons.CurrentAimMode[stateIndex],
            weapons.CurrentTargetMode[stateIndex],
            weapons.Ammo[stateIndex],
            weapons.ReserveAmmo[stateIndex],
            weapons.CurrentAmmoType[stateIndex],
            weapons.Reloading[stateIndex],
            weapons.ReloadTimer[stateIndex],
            weapons.Cooldown[stateIndex],
            weapons.AimTimer[stateIndex],
            weapons.BurstRemaining[stateIndex],
            weapons.Recoil[stateIndex]);
    }

    internal static void CaptureHearingDebug(
        UnitSimulation simulation,
        out RenderSoundPulse[] soundPulses,
        out RenderAuditoryContact[] soundContacts)
    {
        UnitStore units = simulation.Units;
        float nowSeconds = simulation.PerceptionMemory.CurrentTimeSeconds;
        TacticalSoundDebugEvent[] events = simulation.GetRecentSoundDebugEvents();
        soundPulses = new RenderSoundPulse[events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            TacticalSoundDebugEvent sound = events[i];
            soundPulses[i] = new RenderSoundPulse(
                sound.Position, sound.Type, sound.AudibleRange, sound.Intensity,
                MathF.Max(0f, nowSeconds - sound.OccurredAtSeconds));
        }

        const int MaxContactsPerObserver = 3;
        List<RenderAuditoryContact> captured = new();
        ReadOnlySpan<int> active = units.ActiveIndices;
        for (int activeIndex = 0; activeIndex < active.Length; activeIndex++)
        {
            int observerIndex = active[activeIndex];
            if (simulation.Health.OverallHitPoints[observerIndex] <= 0f ||
                !simulation.PerceptionMemory.TryGetMemory(observerIndex, out TacticalContactMemory memory))
                continue;

            RenderAuditoryContact best0 = default, best1 = default, best2 = default;
            float bestTime0 = -1f, bestTime1 = -1f, bestTime2 = -1f;
            IReadOnlyList<TacticalContact> contacts = memory.Contacts;
            for (int contactIndex = 0; contactIndex < contacts.Count; contactIndex++)
            {
                TacticalContact contact = contacts[contactIndex];
                if (contact.ExpiresAtSeconds <= nowSeconds)
                    continue;

                PerceptionObservation latest = default;
                bool foundAuditory = false;
                IReadOnlyList<PerceptionObservation> observations = contact.Observations;
                for (int observationIndex = 0; observationIndex < observations.Count; observationIndex++)
                {
                    PerceptionObservation observation = observations[observationIndex];
                    if (observation.Sense != PerceptionSense.Auditory ||
                        observation.ExpiresAtSeconds <= nowSeconds)
                        continue;
                    if (!foundAuditory || observation.ReceivedAtSeconds >= latest.ReceivedAtSeconds)
                    {
                        latest = observation;
                        foundAuditory = true;
                    }
                }

                if (!foundAuditory)
                    continue;
                float confidence = latest.GetEffectiveDetectionConfidence(nowSeconds, 2.5f);
                if (confidence < 0.12f)
                    continue;

                RenderAuditoryContact item = new(
                    units.GetId(observerIndex), units.Position[observerIndex],
                    latest.EstimatedPosition, latest.PositionErrorRadius,
                    confidence, MathF.Max(0f, nowSeconds - latest.OccurredAtSeconds),
                    latest.StimulusType);
                float received = latest.ReceivedAtSeconds;
                if (received > bestTime0 || (received == bestTime0 && confidence > best0.Confidence))
                {
                    best2 = best1; bestTime2 = bestTime1;
                    best1 = best0; bestTime1 = bestTime0;
                    best0 = item; bestTime0 = received;
                }
                else if (received > bestTime1 || (received == bestTime1 && confidence > best1.Confidence))
                {
                    best2 = best1; bestTime2 = bestTime1;
                    best1 = item; bestTime1 = received;
                }
                else if (received > bestTime2 || (received == bestTime2 && confidence > best2.Confidence))
                {
                    best2 = item; bestTime2 = received;
                }
            }

            if (bestTime0 >= 0f) captured.Add(best0);
            if (bestTime1 >= 0f) captured.Add(best1);
            if (bestTime2 >= 0f) captured.Add(best2);
        }

        const int MaxTotalAuditoryContacts = 1024;
        if (captured.Count > MaxTotalAuditoryContacts)
        {
            captured.Sort(static (left, right) =>
            {
                int byAge = left.AgeSeconds.CompareTo(right.AgeSeconds);
                return byAge != 0 ? byAge : right.Confidence.CompareTo(left.Confidence);
            });
            captured.RemoveRange(MaxTotalAuditoryContacts, captured.Count - MaxTotalAuditoryContacts);
        }

        soundContacts = captured.ToArray();
    }

    internal static void CaptureMemoryDebug(
        UnitSimulation simulation,
        UnitId observerId,
        out RenderMemoryContact[] memoryContacts)
    {
        memoryContacts = Array.Empty<RenderMemoryContact>();
        UnitStore units = simulation.Units;
        if (!observerId.IsValid ||
            !units.TryGetIndex(observerId, out int observerIndex) ||
            simulation.Health.OverallHitPoints[observerIndex] <= 0f ||
            !simulation.PerceptionMemory.TryGetMemory(observerIndex, out TacticalContactMemory memory))
        {
            return;
        }

        float nowSeconds = simulation.PerceptionMemory.CurrentTimeSeconds;
        ushort observerFaction = units.FactionTag[observerIndex];
        Vector3 observerPosition = units.Position[observerIndex];
        IReadOnlyList<TacticalContact> contacts = memory.Contacts;
        List<RenderMemoryContact> captured = new(Math.Min(contacts.Count, 32));

        for (int i = 0; i < contacts.Count; i++)
        {
            TacticalContact contact = contacts[i];
            if (contact.ExpiresAtSeconds <= nowSeconds)
                continue;

            bool hasVisualEvidence = contact.LastVisualAtSeconds >= 0f;
            bool hasAuditoryEvidence = false;
            bool hasCommunicationEvidence =
                contact.LastStimulusType == TacticalStimulusType.AllyContactReport;
            IReadOnlyList<PerceptionObservation> observations = contact.Observations;
            for (int observationIndex = 0; observationIndex < observations.Count; observationIndex++)
            {
                PerceptionObservation observation = observations[observationIndex];
                if (observation.ExpiresAtSeconds <= nowSeconds)
                    continue;

                hasAuditoryEvidence |= observation.Sense == PerceptionSense.Auditory;
                hasCommunicationEvidence |= observation.Sense == PerceptionSense.Communication ||
                    observation.StimulusType == TacticalStimulusType.AllyContactReport;
            }

            ushort subjectFaction = 0;
            if (contact.SubjectUnit is UnitId subject)
            {
                if (!units.TryGetIndex(subject, out int subjectIndex))
                    continue;

                subjectFaction = units.FactionTag[subjectIndex];
                if (!FactionRules.ShouldAttack(observerFaction, subjectFaction))
                    continue;
            }

            if (!hasVisualEvidence && !hasAuditoryEvidence && !hasCommunicationEvidence)
                continue;

            float visualAge = hasVisualEvidence
                ? MathF.Max(0f, nowSeconds - contact.LastVisualAtSeconds)
                : -1f;
            float confidence = contact.GetEffectiveConfidence(
                nowSeconds,
                hasVisualEvidence ? 4f : 2.5f);
            // A last-known visual track can outlive its original observation record.
            // Keep rendering that retained track with age-decayed confidence until
            // the contact itself expires.
            if (hasVisualEvidence && confidence < 0.06f)
                confidence = MathF.Exp(-0.69314718f * visualAge / 4f);
            if (confidence < 0.06f)
                continue;

            captured.Add(new RenderMemoryContact(
                observerId,
                observerPosition,
                contact.SubjectUnit,
                subjectFaction,
                contact.EstimatedPosition,
                contact.PositionErrorRadius,
                confidence,
                MathF.Max(0f, nowSeconds - contact.LastEventAtSeconds),
                visualAge,
                contact.Kind,
                contact.LastStimulusType,
                hasVisualEvidence,
                hasAuditoryEvidence,
                hasCommunicationEvidence,
                contact.IsInvestigated));
        }

        const int MaxMemoryContacts = 32;
        if (captured.Count > MaxMemoryContacts)
        {
            captured.Sort(static (left, right) =>
            {
                int leftRank = left.HasVisualEvidence ? 0 : left.HasCommunicationEvidence ? 1 : 2;
                int rightRank = right.HasVisualEvidence ? 0 : right.HasCommunicationEvidence ? 1 : 2;
                int byKind = leftRank.CompareTo(rightRank);
                if (byKind != 0)
                    return byKind;

                float leftAge = left.HasVisualEvidence ? left.VisualAgeSeconds : left.AgeSeconds;
                float rightAge = right.HasVisualEvidence ? right.VisualAgeSeconds : right.AgeSeconds;
                int byAge = leftAge.CompareTo(rightAge);
                return byAge != 0 ? byAge : right.Confidence.CompareTo(left.Confidence);
            });
            captured.RemoveRange(MaxMemoryContacts, captured.Count - MaxMemoryContacts);
        }

        memoryContacts = captured.ToArray();
    }

    public static SimulationFrameSnapshot Capture(
        UnitSimulation simulation,
        UnitId visionDebugObserver = default,
        WorldMap? visionDebugMap = null,
        bool includeHearingDebug = false,
        bool includeMemoryDebug = false,
        UnitId memoryDebugObserver = default,
        bool includeUtilityAiDebug = false,
        UnitId utilityAiDebugObserver = default)
    {
        long captureStarted = Stopwatch.GetTimestamp();
        long captureAllocatedBytesStarted = GC.GetAllocatedBytesForCurrentThread();
        UnitStore units = simulation.Units;
        bool hasUtilityAiDebugState = false;
        UnitIntentUtilityBreakdown utilityAiDebugDecision = default;
        UnitIntentUtilityContext utilityAiDebugContext = default;
        if (includeUtilityAiDebug &&
            utilityAiDebugObserver.IsValid &&
            units.TryGetIndex(utilityAiDebugObserver, out int utilityDebugIndex))
        {
            UnitAiStore utilityStore = simulation.AI.Store;
            hasUtilityAiDebugState = simulation.AI.Enabled &&
                utilityStore.UtilityIntent[utilityDebugIndex] != BehaviacUnitIntent.None;
            if (hasUtilityAiDebugState)
            {
                utilityAiDebugDecision = utilityStore.UtilityIntentBreakdown[utilityDebugIndex];
                utilityAiDebugContext = utilityStore.UtilityIntentContext[utilityDebugIndex];
            }
        }

        ReadOnlySpan<int> active = units.ActiveIndices;
        ReadOnlySpan<MeleeHitVisual> sourceMeleeHits = simulation.MeleeCombat.Visuals;
        MeleeHitVisual[] meleeHitVisuals = new MeleeHitVisual[sourceMeleeHits.Length];
        sourceMeleeHits.CopyTo(meleeHitVisuals);
        RenderUnitState[] unitStates = new RenderUnitState[active.Length];

        // Flatten active gameplay hit zones and fallback body visuals into one immutable
        // array for the render thread. Each unit stores a range to avoid per-unit arrays.
        UnitBodyStore bodyStore = simulation.Bodies;
        UnitHitRegionStore hitRegionStore = simulation.HitRegions;
        int[] sourceBodyStarts = units.BodyStart;
        short[] sourceBodyCounts = units.BodyCount;
        UnitType[] unitTypes = units.Type;
        int totalBodyParts = 0;
        for (int i = 0; i < active.Length; i++)
        {
            int unitIndex = active[i];
            UnitHitRegionProfile? profile = hitRegionStore.GetProfile(unitIndex);

            if (unitTypes[unitIndex] == UnitType.Colonist &&
                profile is UnitAnatomyProfile)
            {
                totalBodyParts += UnitGameplayZoneSystem.ZoneCount;
                continue;
            }

            if (unitTypes[unitIndex] == UnitType.Colonist && profile != null)
            {
                totalBodyParts += profile.RegionCount;
                continue;
            }

            int start = Math.Max(0, sourceBodyStarts[unitIndex]);
            int count = Math.Max(0, (int)sourceBodyCounts[unitIndex]);
            totalBodyParts += Math.Min(count, Math.Max(0, bodyStore.Count - start));
        }

        RenderBodyPart[] renderBodyParts = new RenderBodyPart[totalBodyParts];
        Vector3[] sourceBodyPositions = bodyStore.WorldPosition;
        Vector3[] sourceBodyScales = bodyStore.Scale;
        float[] sourceBodyRotations = bodyStore.WorldRotation;
        BodyPrimitive[] sourceBodyPrimitives = bodyStore.Primitive;
        BodyPartRole[] sourceBodyRoles = bodyStore.Role;
        int renderBodyPartWrite = 0;

        int blueAlive = 0;
        int blueDead = 0;
        int redAlive = 0;
        int redDead = 0;
        int thirdFactionAlive = 0;
        int attack = 0;
        int search = 0;
        int cover = 0;
        int idle = 0;
        int dead = 0;
        int suppressed = 0;
        int panicked = 0;

        float[] hitPoints = simulation.Health.OverallHitPoints;
        float[] maxHitPoints = simulation.Health.OverallMaxHitPoints;
        ushort[] factions = units.FactionTag;
        Vector3[] positions = units.Position;
        float[] widths = units.Width;
        float[] lengths = units.Length;
        float[] heights = units.Height;
        float[] radii = units.Radius;
        float[] moveSpeeds = units.MoveSpeed;
        float[] viewRanges = units.ViewRange;
        float[] fieldOfViews = units.FieldOfView;
        Vector3[] velocities = units.Velocity;
        UnitType[] types = units.Type;
        UnitPosture[] postures = units.Posture;
        int[] locations = units.LocationId;
        Vector3[] bodyNormals = units.BodyNormal;
        Vector3[] headNormals = units.HeadNormal;
        UnitAiStore aiStore = simulation.AI.Store;
        UnitAiState[] aiStates = aiStore.State;
        BehaviacUnitIntent[] aiIntents = aiStore.LastIntent;
        UnitAiOrderType[] playerOrderTypes = aiStore.PlayerOrderType;
        Vector3[] playerOrderPositions = aiStore.PlayerOrderPosition;
        UnitId[] playerOrderTargets = aiStore.PlayerOrderTarget;
        int[] orderPathStarts = new int[active.Length];
        int[] orderPathCounts = new int[active.Length];
        int totalOrderPathPoints = 0;

        for (int i = 0; i < active.Length; i++)
        {
            int index = active[i];
            orderPathStarts[i] = totalOrderPathPoints;
            if (playerOrderTypes[index] is UnitAiOrderType.Move or UnitAiOrderType.Attack)
            {
                orderPathCounts[i] = Math.Min(
                    32,
                    simulation.Navigation.GetRoutePointCount(index));
                totalOrderPathPoints += orderPathCounts[i];
            }
        }

        Vector2[] orderPathPoints = new Vector2[totalOrderPathPoints];
        for (int i = 0; i < active.Length; i++)
        {
            int count = orderPathCounts[i];
            if (count > 0)
            {
                orderPathCounts[i] = simulation.Navigation.CopyRoutePoints(
                    active[i],
                    orderPathPoints.AsSpan(orderPathStarts[i], count));
            }
        }

        UnitInventoryStore inventoryStore = simulation.Inventory;
        UnitSuppressionStore suppressionStore = simulation.Suppression;
        UnitWeaponStore weaponStore = simulation.Weapons;

        for (int i = 0; i < active.Length; i++)
        {
            int index = active[i];
            float hp = hitPoints[index];
            float healthRatio = maxHitPoints[index] > 0f
                ? hp / maxHitPoints[index]
                : 0f;
            ushort faction = factions[index];
            UnitAiState aiState = aiStates[index];
            BehaviacUnitIntent aiIntent = aiIntents[index];

            bool hasApparel = false;
            bool hasPoweredArmor = false;
            for (int zone = 0; zone < UnitInventoryStore.BodyZoneCount; zone++)
            {
                for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
                {
                    short itemSlot = inventoryStore.GetBodyEquipment(
                        index,
                        zone,
                        (ArmorLayer)layer);

                    if (itemSlot < 0 ||
                        inventoryStore.GetItem(index, itemSlot) is not ArmorConfig armor)
                    {
                        continue;
                    }

                    hasApparel = true;
                    hasPoweredArmor |= armor.MovementSpeedMultiplier > 1f;
                }
            }

            RenderWeaponVisual primaryWeapon = CaptureWeaponVisual(
                inventoryStore, weaponStore, index, UnitWeaponSlot.Primary);
            RenderWeaponVisual secondaryWeapon = CaptureWeaponVisual(
                inventoryStore, weaponStore, index, UnitWeaponSlot.Secondary);
            RenderWeaponVisual meleeWeapon = CaptureWeaponVisual(
                inventoryStore, weaponStore, index, UnitWeaponSlot.Melee);
            RenderWeaponVisual utilityWeapon = CaptureWeaponVisual(
                inventoryStore, weaponStore, index, UnitWeaponSlot.Utility);

            bool hasTarget = false;
            Vector3 targetPosition = Vector3.Zero;
            if (aiStore.HasTarget[index] &&
                units.TryGetIndex(aiStore.Target[index], out int targetIndex))
            {
                hasTarget = true;
                targetPosition = positions[targetIndex];
            }

            bool hasGoal = aiStore.HasGoal[index];
            Vector3 goal = aiStore.Goal[index];

            float suppression = suppressionStore.Value[index];
            UnitSuppressionState suppressionState = suppressionStore.GetState(index);

            int renderBodyPartStart = renderBodyPartWrite;
            int sourceBodyStart = Math.Max(0, sourceBodyStarts[index]);
            int sourceBodyCount = Math.Max(0, (int)sourceBodyCounts[index]);
            sourceBodyCount = Math.Min(
                sourceBodyCount,
                Math.Max(0, bodyStore.Count - sourceBodyStart));

            UnitHitRegionProfile? hitProfile = hitRegionStore.GetProfile(index);

            if (unitTypes[index] == UnitType.Colonist &&
                hitProfile is UnitAnatomyProfile)
            {
                // Draw the exact three procedural zones used by projectile hit testing.
                // Do not substitute the informational anatomy rig for gameplay geometry.
                for (int zoneIndex = 0; zoneIndex < UnitGameplayZoneSystem.ZoneCount; zoneIndex++)
                {
                    UnitGameplayZoneGeometry zone = UnitGameplayZoneSystem.GetGeometry(
                        units, index, zoneIndex);
                    renderBodyParts[renderBodyPartWrite++] =
                        UnitGameplayZoneRenderGeometry.CreatePart(zone);
                }
            }
            else if (unitTypes[index] == UnitType.Colonist && hitProfile != null)
            {
                ReadOnlySpan<UnitCircleHitRegion> regions = hitProfile.Regions;

                for (int regionIndex = 0; regionIndex < regions.Length; regionIndex++)
                {
                    UnitCircleHitRegion region = regions[regionIndex];
                    UnitHitRegionGeometry geometry = UnitHitSystem.GetRegionGeometry(
                        units,
                        index,
                        regionIndex,
                        hitRegionStore);

                    renderBodyParts[renderBodyPartWrite++] =
                        UnitHitRegionRenderGeometry.CreatePart(
                            units,
                            index,
                            region,
                            geometry);
                }
            }
            else
            {
                for (int part = 0; part < sourceBodyCount; part++)
                {
                    int sourceIndex = sourceBodyStart + part;
                    renderBodyParts[renderBodyPartWrite++] = new RenderBodyPart(
                        sourceBodyPositions[sourceIndex],
                        sourceBodyScales[sourceIndex],
                        sourceBodyRotations[sourceIndex],
                        sourceBodyPrimitives[sourceIndex],
                        sourceBodyRoles[sourceIndex]);
                }
            }

            unitStates[i] = new RenderUnitState(
                index,
                units.GetId(index).Generation,
                positions[index],
                bodyNormals[index],
                headNormals[index],
                widths[index],
                lengths[index],
                hp,
                healthRatio,
                faction,
                aiState,
                aiIntent,
                hasTarget,
                targetPosition,
                hasGoal,
                goal,
                suppression,
                suppressionState,
                hasApparel,
                hasPoweredArmor,
                primaryWeapon,
                secondaryWeapon,
                meleeWeapon,
                utilityWeapon,
                renderBodyPartStart,
                renderBodyPartWrite - renderBodyPartStart,
                types[index],
                postures[index],
                velocities[index],
                moveSpeeds[index],
                heights[index],
                radii[index],
                viewRanges[index],
                fieldOfViews[index],
                maxHitPoints[index],
                locations[index],
                units.HasTarget[index],
                units.HasTarget[index] ? units.Target[index] : Vector3.Zero,
                playerOrderTypes[index],
                playerOrderPositions[index],
                playerOrderTargets[index],
                orderPathStarts[i],
                orderPathCounts[i]);

            bool alive = hp > 0f;
            if (faction == 1)
            {
                if (alive) blueAlive++;
                else blueDead++;
            }
            else if (faction == 2)
            {
                if (alive) redAlive++;
                else redDead++;
            }
            else if (faction == 3 && alive)
            {
                thirdFactionAlive++;
            }

            switch (aiState)
            {
                case UnitAiState.Attack: attack++; break;
                case UnitAiState.Search: search++; break;
                case UnitAiState.SeekCover: cover++; break;
                case UnitAiState.Dead: dead++; break;
                default: idle++; break;
            }

            switch (simulation.Suppression.GetState(index))
            {
                case UnitSuppressionState.Suppressed: suppressed++; break;
                case UnitSuppressionState.Panicked: panicked++; break;
            }
        }

        List<RenderAimRing> aimRings = new(Math.Min(active.Length, 128));
        bool[] targetDrawn = new bool[units.Capacity];
        UnitInventoryStore inventory = simulation.Inventory;
        UnitWeaponStore weaponStates = simulation.Weapons;

        for (int i = 0; i < active.Length; i++)
        {
            int shooterIndex = active[i];
            for (int slot = 0; slot < UnitInventoryStore.WeaponSlotCount; slot++)
            {
                UnitWeaponSlot weaponSlot = (UnitWeaponSlot)slot;
                int stateIndex = UnitWeaponStore.GetIndex(shooterIndex, weaponSlot);

                if (!weaponStates.AimIndicatorActive[stateIndex] ||
                    weaponStates.AimTimer[stateIndex] <= 0f)
                {
                    continue;
                }

                UnitId targetId = weaponStates.AimTarget[stateIndex];
                if (!units.TryGetIndex(targetId, out int targetIndex) ||
                    targetDrawn[targetIndex] ||
                    hitPoints[targetIndex] <= 0f)
                {
                    continue;
                }

                short inventorySlot = inventory.GetWeaponEquipment(shooterIndex, weaponSlot);
                if (inventorySlot < 0 ||
                    inventory.GetItem(shooterIndex, inventorySlot) is not RangedWeaponConfig weapon)
                {
                    continue;
                }

                Vector3 aimPoint = weaponStates.AimPoint[stateIndex];
                if (aimPoint == Vector3.Zero)
                    continue;

                Vector3 shooterPosition = positions[shooterIndex];
                float dx = aimPoint.X - shooterPosition.X;
                float dy = aimPoint.Y - shooterPosition.Y;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                float duration = UnitWeaponSystem.GetAimDuration(
                    weapon,
                    weaponStates.CurrentAimMode[stateIndex],
                    distance);
                if (duration <= 0f)
                    continue;

                float progress = Math.Clamp(
                    weaponStates.AimTimer[stateIndex] / duration,
                    0f,
                    1f);

                aimRings.Add(new RenderAimRing(aimPoint, progress));
                targetDrawn[targetIndex] = true;
                break;
            }
        }

        ProjectileStore projectiles = simulation.Projectiles;
        Vector3[] traceStarts = projectiles.TraceStarts;
        Vector3[] traceEnds = projectiles.TraceEnds;
        ushort[] traceFactions = projectiles.TraceFactions;
        bool[] traceLasers = projectiles.TraceLasers;
        float[] traceLifetimes = projectiles.TraceLifetimes;

        int traceCount = 0;
        for (int i = 0; i < projectiles.TraceCapacityCount; i++)
        {
            if (traceLifetimes[i] > 0f)
                traceCount++;
        }

        RenderProjectileTrace[] traces = new RenderProjectileTrace[traceCount];
        int traceWrite = 0;
        for (int i = 0; i < projectiles.TraceCapacityCount; i++)
        {
            float lifetime = traceLifetimes[i];
            if (lifetime <= 0f)
                continue;

            traces[traceWrite++] = new RenderProjectileTrace(
                traceStarts[i],
                traceEnds[i],
                traceFactions[i],
                traceLasers[i],
                lifetime);
        }

        Vector3[] impactPositions = projectiles.ImpactPositions;
        float[] impactLifetimes = projectiles.ImpactLifetimes;
        ProjectileImpactKind[] impactKinds = projectiles.ImpactKinds;

        int impactCount = 0;
        for (int i = 0; i < projectiles.ImpactCapacityCount; i++)
        {
            if (impactLifetimes[i] > 0f)
                impactCount++;
        }

        RenderProjectileImpact[] impacts = new RenderProjectileImpact[impactCount];
        int impactWrite = 0;
        for (int i = 0; i < projectiles.ImpactCapacityCount; i++)
        {
            float lifetime = impactLifetimes[i];
            if (lifetime <= 0f)
                continue;

            impacts[impactWrite++] = new RenderProjectileImpact(
                impactPositions[i],
                lifetime,
                impactKinds[i]);
        }

        VisionSystem vision = simulation.Vision;
        UnitNavigationSystem navigation = simulation.Navigation;

        UnitId capturedVisionObserver = default;
        Vector3 visionObserverEye = Vector3.Zero;
        RenderVisionContact[] visionContacts = Array.Empty<RenderVisionContact>();
        if (simulation.VisionEnabled &&
            visionDebugMap != null &&
            visionDebugObserver.IsValid &&
            units.TryGetIndex(visionDebugObserver, out int visionObserverIndex) &&
            simulation.Health.OverallHitPoints[visionObserverIndex] > 0f)
        {
            capturedVisionObserver = units.GetId(visionObserverIndex);
            visionObserverEye = vision.GetEyePosition(
                units,
                visionObserverIndex,
                bodyStore);
            ReadOnlySpan<int> visibleTargets = vision.GetVisibleTargets(visionObserverIndex);
            List<RenderVisionContact> contacts = new(visibleTargets.Length * 2);

            // Recheck each zone only for this selected observer's current visible
            // targets. The normal all-unit vision and AI cadence stays unchanged.
            for (int i = 0; i < visibleTargets.Length; i++)
            {
                int targetIndex = visibleTargets[i];
                if ((uint)targetIndex >= (uint)units.Capacity ||
                    targetIndex == visionObserverIndex ||
                    simulation.Health.OverallHitPoints[targetIndex] <= 0f)
                {
                    continue;
                }

                UnitId targetId = units.GetId(targetIndex);
                if (!targetId.IsValid)
                    continue;

                int pointCount = UnitHitSystem.GetVisibilityPointCount(
                    units, targetIndex, hitRegionStore);
                for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                {
                    VisionCheck check = vision.EvaluateVisibilityPoint(
                        units,
                        visionDebugMap,
                        visionObserverIndex,
                        targetIndex,
                        pointIndex,
                        bodyStore);
                    if (!check.IsVisible)
                        continue;

                    contacts.Add(new RenderVisionContact(
                        targetId,
                        check.VisibleTargetPoint,
                        check.VisiblePoint));
                }
            }

            visionContacts = contacts.ToArray();
        }

        RenderSoundPulse[] soundPulses = Array.Empty<RenderSoundPulse>();
        RenderAuditoryContact[] soundContacts = Array.Empty<RenderAuditoryContact>();
        if (includeHearingDebug)
            CaptureHearingDebug(simulation, out soundPulses, out soundContacts);

        RenderMemoryContact[] memoryContacts = Array.Empty<RenderMemoryContact>();
        if (includeMemoryDebug)
            CaptureMemoryDebug(simulation, memoryDebugObserver, out memoryContacts);

        return new SimulationFrameSnapshot
        {
            Units = unitStates,
            OrderPathPoints = orderPathPoints,
            BodyParts = renderBodyParts,
            Traces = traces,
            AimRings = aimRings.ToArray(),
            VisionDebugObserver = capturedVisionObserver,
            VisionDebugObserverEye = visionObserverEye,
            VisionContacts = visionContacts,
            SoundPulses = soundPulses,
            SoundContacts = soundContacts,
            MemoryContacts = memoryContacts,
            Impacts = impacts,
            MeleeHitVisuals = meleeHitVisuals,
            ActiveProjectiles = projectiles.ActiveCount,
            ProjectileCapacity = projectiles.Capacity,
            TotalShotsFired = simulation.TotalShotsFired,
            TotalRoundsConsumed = simulation.TotalRoundsConsumed,
            TotalProjectilesSpawned = simulation.TotalProjectilesSpawned,
            TotalHits = projectiles.TotalHits,
            LastHitPart = projectiles.LastHitPart.ToString(),
            AiEnabled = simulation.AI.Enabled,
            VisionEnabled = simulation.VisionEnabled,
            BlueAlive = blueAlive,
            BlueDead = blueDead,
            RedAlive = redAlive,
            RedDead = redDead,
            ThirdFactionAlive = thirdFactionAlive,
            MeleeSwings = simulation.MeleeCombat.TotalSwings,
            MeleeHits = simulation.MeleeCombat.TotalHits,
            AttackCount = attack,
            SearchCount = search,
            CoverCount = cover,
            IdleCount = idle,
            DeadCount = dead,
            SuppressedCount = suppressed,
            PanickedCount = panicked,
            SnapshotCaptureMs = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds,
            SnapshotAllocatedBytes = Math.Max(
                0L, GC.GetAllocatedBytesForCurrentThread() - captureAllocatedBytesStarted),
            PerceptionAllocatedBytes = simulation.LastPerceptionAllocatedBytes,
            SimulationMs = simulation.LastSimulationUpdateMilliseconds,
            PerceptionMs = simulation.LastPerceptionUpdateMilliseconds,
            HearingSenseMs = simulation.LastHearingSenseMilliseconds,
            VisualSenseMs = simulation.LastVisualSenseMilliseconds,
            VisualPublishMs = simulation.LastVisualPublishMilliseconds,
            VisualObservationsPublished = simulation.LastVisualObservationsPublished,
            HearingQueuedPulses = simulation.LastHearingQueuedPulses,
            HearingListenerChecks = simulation.LastHearingListenerChecks,
            HearingListenerCellsVisited = simulation.LastHearingListenerCellsVisited,
            HearingContactsRecorded = simulation.LastHearingContactsRecorded,
            GunshotEventsQueued = simulation.LastGunshotEventsQueued,
            GunshotBatchesQueued = simulation.LastGunshotBatchesQueued,
            GunshotsMerged = simulation.LastGunshotsMerged,
            SuppressionMs = simulation.LastSuppressionUpdateMilliseconds,
            BodyMs = simulation.LastBodyUpdateMilliseconds,
            MeleeMs = simulation.LastMeleeUpdateMilliseconds,
            VisionMs = vision.LastUpdateMilliseconds,
            VisionCandidatePairs = vision.LastCandidatePairs,
            VisionTargetEvaluations = vision.LastTargetEvaluations,
            VisionLineOfSightChecks = vision.LastLineOfSightChecks,
            VisionCandidatesScanned = vision.LastActiveCandidatesScanned,
            VisionMemoryEntriesScanned = vision.LastVisibilityMemoryEntriesScanned,
            AiMs = simulation.LastAIUpdateMilliseconds,
            AiPlanningMs = simulation.AI.LastPlanningMilliseconds,
            UtilityAiEnabled = simulation.AI.Enabled && simulation.AI.UseUtilityDecisionLayer,
            UtilityAiReady = simulation.AI.UtilityDecisionReady,
            UtilityAiDecisionCount = simulation.AI.LastUtilityDecisions,
            UtilityAiDecisionMs = simulation.AI.LastUtilityDecisionBatchMilliseconds,
            UtilityAiDeadCount = simulation.AI.LastUtilityDeadDecisions,
            UtilityAiCoverCount = simulation.AI.LastUtilityCoverDecisions,
            UtilityAiRetreatCount = simulation.AI.LastUtilityRetreatDecisions,
            UtilityAiFlankCount = simulation.AI.LastUtilityFlankDecisions,
            UtilityAiEngageCount = simulation.AI.LastUtilityEngageDecisions,
            UtilityAiInvestigateCount = simulation.AI.LastUtilityInvestigateDecisions,
            HasUtilityAiDebugState = hasUtilityAiDebugState,
            UtilityAiDebugObserver = hasUtilityAiDebugState ? utilityAiDebugObserver : default,
            UtilityAiDebugDecision = utilityAiDebugDecision,
            UtilityAiDebugContext = utilityAiDebugContext,
            BehaviacEnabled = false,
            BehaviacRuntimeReady = false,
            BehaviacDecisionCount = 0,
            BehaviacDecisionMs = 0d,
            BehaviacDeadCount = 0,
            BehaviacCoverCount = 0,
            BehaviacRetreatCount = 0,
            BehaviacFlankCount = 0,
            BehaviacEngageCount = 0,
            BehaviacInvestigateCount = 0,
            MovementMs = simulation.LastMovementUpdateMilliseconds,
            WeaponsMs = simulation.LastWeaponUpdateMilliseconds,
            BallisticsMs = simulation.LastProjectileUpdateMilliseconds,
            ProjectileGridMs = simulation.LastProjectileGridBuildMilliseconds,
            ProjectilesVisited = simulation.LastProjectilesVisited,
            TerrainCellsTraced = simulation.LastProjectileTerrainCellsTraced,
            ProjectileUnitCandidates = simulation.LastProjectileUnitCandidates,
            HealthMs = simulation.LastHealthUpdateMilliseconds,
            RoutesBuilt = navigation.RoutesBuiltThisUpdate,
            RoutesFailed = navigation.RoutesFailedThisUpdate,
            NavigationSearches = navigation.SearchesThisUpdate,
            NavigationCellsExpanded = navigation.CellsExpandedThisUpdate,
            NavigationSearchMs = navigation.SearchMillisecondsThisUpdate,
            NavigationGridMs = navigation.NavigationGridBuildMilliseconds,
            RoutesReused = navigation.RoutesBuiltUsingPriorRoutesThisUpdate,
            RouteTrafficCells = navigation.RouteTrafficCellsConsideredThisUpdate
        };
    }
}
