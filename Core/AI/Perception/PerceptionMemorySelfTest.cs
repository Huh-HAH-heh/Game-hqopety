using System;
using System.Numerics;
using Core.AI.Behaviac;
using Core.Map;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>Deterministic regression tests for shared perception memory and sensor bridges.</summary>
public static class PerceptionMemorySelfTest
{
    public static bool DetectionAndIdentityAreSeparate()
    {
        var memory = new TacticalContactMemory();
        TacticalContact? contact = memory.Record(Observation(
            PerceptionSense.Auditory,
            TacticalStimulusType.Gunshot,
            new Vector3(10f, 10f, 0.1f),
            occurredAt: 1f,
            receivedAt: 1f,
            confidence: 0.8f,
            positionError: 3f));

        return contact != null &&
               !contact.SubjectUnit.HasValue &&
               contact.GetDetectionLevel(1f) == PerceptionDetectionLevel.Detected &&
               contact.GetIdentificationLevel(1f) == PerceptionIdentificationLevel.Unknown &&
               contact.PositionErrorRadius >= 3f;
    }

    public static bool IndependentSensesFuseAnonymousContact()
    {
        var memory = new TacticalContactMemory();
        TacticalContact? sound = memory.Record(Observation(
            PerceptionSense.Auditory,
            TacticalStimulusType.Gunshot,
            new Vector3(10f, 10f, 0.1f),
            1f, 1f, 0.4f, 1.2f));
        if (sound == null)
            return false;

        TacticalContact? sight = memory.Record(Observation(
            PerceptionSense.Visual,
            TacticalStimulusType.VisualContact,
            new Vector3(10.5f, 10f, 0.1f),
            1.2f, 1.2f, 0.7f, 0.5f));

        return sight != null &&
               sight.ContactId == sound.ContactId &&
               memory.Contacts.Count == 1 &&
               sight.Observations.Count == 2 &&
               !sight.SubjectUnit.HasValue &&
               sight.GetIdentificationLevel(1.2f) == PerceptionIdentificationLevel.Unknown &&
               MathF.Abs(sight.Confidence - 0.82f) < 0.002f;
    }

    public static bool RepeatedSameSenseEvidenceIsBounded()
    {
        var memory = new TacticalContactMemory();
        TacticalContact? contact = null;
        for (int i = 0; i < 5; i++)
        {
            float time = 1f + i * 0.1f;
            contact = memory.Record(Observation(
                PerceptionSense.Auditory,
                TacticalStimulusType.Gunshot,
                new Vector3(10f, 10f, 0.1f),
                time, time, 0.1f + i * 0.1f, 1f));
            if (contact == null)
                return false;
        }

        return memory.Contacts.Count == 1 &&
               contact.Observations.Count == 4 &&
               MathF.Abs(contact.Confidence - 0.5f) < 0.002f;
    }

    public static bool ExplicitCorrelationCanIdentifyAnonymousContact()
    {
        var memory = new TacticalContactMemory();
        TacticalContact? anonymous = memory.Record(Observation(
            PerceptionSense.Auditory,
            TacticalStimulusType.Gunshot,
            new Vector3(10f, 10f, 0.1f),
            1f, 1f, 0.5f, 2f));
        if (anonymous == null)
            return false;

        UnitId subject = new(7, 1);
        TacticalContact? identified = memory.Record(Observation(
            PerceptionSense.Visual,
            TacticalStimulusType.VisualContact,
            new Vector3(10.25f, 10f, 0.1f),
            1.1f, 1.1f, 0.9f, 0.25f, subject),
            anonymous.ContactId);

        return identified != null &&
               identified.ContactId == anonymous.ContactId &&
               identified.SubjectUnit == subject &&
               identified.Kind == TacticalContactKind.VisualConfirmed &&
               identified.Observations.Count == 2 &&
               identified.GetIdentificationLevel(1.1f) == PerceptionIdentificationLevel.Identified;
    }

    public static bool ConflictingIdentityCannotHijackCorrelatedContact()
    {
        var memory = new TacticalContactMemory();
        UnitId firstSubject = new(7, 1);
        UnitId differentSubject = new(8, 1);
        TacticalContact? original = memory.Record(Observation(
            PerceptionSense.Visual,
            TacticalStimulusType.VisualContact,
            new Vector3(10f, 10f, 0.1f),
            1f, 1f, 0.9f, 0.25f, firstSubject));
        if (original == null)
            return false;

        TacticalContact? rejected = memory.Record(Observation(
            PerceptionSense.Visual,
            TacticalStimulusType.VisualContact,
            new Vector3(10.1f, 10f, 0.1f),
            1.1f, 1.1f, 0.9f, 0.25f, differentSubject),
            original.ContactId);

        return rejected == null &&
               memory.Contacts.Count == 1 &&
               original.SubjectUnit == firstSubject;
    }

    public static bool ExpiredObservationIsRejected()
    {
        var store = new PerceptionMemoryStore(2);
        PerceptionObservation expired = Observation(
            PerceptionSense.Auditory,
            TacticalStimulusType.Gunshot,
            new Vector3(10f, 10f, 0.1f),
            occurredAt: 1f,
            receivedAt: 2f,
            confidence: 0.9f,
            positionError: 2f,
            lifetime: 0.5f);

        TacticalContact? contact = store.RecordObservation(0, expired);
        return contact == null &&
               store.TryGetMemory(0, out TacticalContactMemory memory) &&
               memory.Contacts.Count == 0 &&
               MathF.Abs(store.CurrentTimeSeconds - 2f) < 0.0001f;
    }

    public static bool ConfidenceDecaysWithEvidenceAge()
    {
        var memory = new TacticalContactMemory();
        TacticalContact? contact = memory.Record(Observation(
            PerceptionSense.Auditory,
            TacticalStimulusType.Gunshot,
            new Vector3(10f, 10f, 0.1f),
            occurredAt: 1f,
            receivedAt: 1f,
            confidence: 0.8f,
            positionError: 2f,
            lifetime: 10f));

        return contact != null &&
               MathF.Abs(contact.GetEffectiveConfidence(3f, 2f) - 0.4f) < 0.002f;
    }

    public static bool HearingPublishesToSharedMemoryWithAiDisabled()
    {
        var world = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64) { VisionEnabled = false };
        simulation.Spawn(UnitType.Colonist, new Vector3(8f, 8f, 0.1f), factionTag: 1);
        UnitId listener = simulation.Spawn(
            UnitType.Colonist, new Vector3(11f, 8f, 0.1f), factionTag: 2);

        if (simulation.AI.Enabled)
            return false;

        simulation.ReportExplosion(
            new Vector3(8f, 8f, 0.1f),
            sourceFaction: 1,
            intensity: 1f,
            audibleRange: 20f);
        simulation.Update(world, 0.05f);

        if (simulation.AI.Enabled ||
            !simulation.PerceptionMemory.TryGetMemory(listener.Index, out TacticalContactMemory memory))
            return false;

        for (int i = 0; i < memory.Contacts.Count; i++)
        {
            TacticalContact contact = memory.Contacts[i];
            if (contact.LastStimulusType == TacticalStimulusType.Explosion &&
                contact.Observations.Count > 0 &&
                contact.Observations[contact.Observations.Count - 1].Sense == PerceptionSense.Auditory &&
                !contact.SubjectUnit.HasValue)
                return true;
        }

        return false;
    }

    public static bool VisionPublishesFreshSightingsToSharedMemory()
    {
        if (!TryCreateVisionScenario(out WorldMap world, out UnitSimulation simulation,
                out UnitId observer, out UnitId target))
            return false;

        simulation.Update(world, 0.11f);
        if (simulation.AI.Enabled ||
            simulation.Vision.UpdateCount <= 0 ||
            !simulation.Vision.WasVisibleOnLatestUpdate(observer.Index, target.Index) ||
            !simulation.PerceptionMemory.TryGetMemory(observer.Index, out TacticalContactMemory memory))
            return false;

        for (int i = 0; i < memory.Contacts.Count; i++)
        {
            TacticalContact contact = memory.Contacts[i];
            if (contact.SubjectUnit != target)
                continue;

            for (int j = 0; j < contact.Observations.Count; j++)
            {
                if (contact.Observations[j].Sense == PerceptionSense.Visual &&
                    contact.Observations[j].IdentificationLevel == PerceptionIdentificationLevel.Identified)
                    return true;
            }
        }

        return false;
    }

    public static bool BehaviacInvestigatesLastKnownPositionWithoutCurrentTargetKnowledge()
    {
        var world = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64) { VisionEnabled = true };
        UnitId observer = simulation.Spawn(
            UnitType.Colonist, new Vector3(10f, 10f, 0.1f),
            Vector3.UnitX, Vector3.UnitX, factionTag: 1);
        UnitId target = simulation.Spawn(
            UnitType.Colonist, new Vector3(12f, 10f, 0.1f),
            -Vector3.UnitX, -Vector3.UnitX, factionTag: 2);
        Vector3 lastSeen = simulation.Units.Position[target.Index];

        simulation.PerceptionMemory.RecordObservation(observer.Index, Observation(
            PerceptionSense.Visual,
            TacticalStimulusType.VisualContact,
            lastSeen,
            occurredAt: 0.1f,
            receivedAt: 0.1f,
            confidence: 1f,
            positionError: 0.25f,
            subject: target));
        simulation.PerceptionMemory.AdvanceTime(0.6f);
        simulation.AI.Store.RememberTarget(observer.Index, target, lastSeen, 3f);
        simulation.AI.UseBehaviacDecisionLayer = true;
        simulation.AI.Enabled = true;

        simulation.AI.Update(
            simulation.Units, simulation.Health, simulation.Inventory,
            simulation.Weapons, simulation.Projectiles, simulation.Suppression,
            simulation.Vision, world, 0.16f);

        Vector3 goal = simulation.AI.Store.Goal[observer.Index];
        return simulation.AI.Store.LastIntent[observer.Index] ==
                   BehaviacUnitIntent.InvestigateLastKnownTarget &&
               !simulation.AI.Store.HasTarget[observer.Index] &&
               simulation.AI.Store.HasGoal[observer.Index] &&
               simulation.AI.Store.State[observer.Index] == UnitAiState.Search &&
               MathF.Abs(goal.X - lastSeen.X) < 0.05f &&
               MathF.Abs(goal.Y - lastSeen.Y) < 0.05f;
    }

    public static bool BehaviacFallsBackToPatrolWithoutAContact()
    {
        var world = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64);
        UnitId observer = simulation.Spawn(
            UnitType.Colonist, new Vector3(10f, 10f, 0.1f), factionTag: 1);
        simulation.AI.UseBehaviacDecisionLayer = true;
        simulation.AI.Enabled = true;

        simulation.AI.Update(
            simulation.Units, simulation.Health, simulation.Inventory,
            simulation.Weapons, simulation.Projectiles, simulation.Suppression,
            simulation.Vision, world, 0.16f);

        return simulation.AI.Store.LastIntent[observer.Index] == BehaviacUnitIntent.Patrol &&
               !simulation.AI.Store.HasTarget[observer.Index] &&
               !simulation.PerceptionMemory.TryGetMemory(observer.Index, out TacticalContactMemory memory);
    }

    public static bool AiVisualContactGateExpiresAfterSightIsLost()
    {
        if (!TryCreateVisionScenario(out WorldMap world, out UnitSimulation simulation,
                out UnitId observer, out UnitId target))
            return false;

        simulation.Update(world, 0.11f);
        if (!simulation.AI.TryGetFreshVisualContact(observer.Index, target, out Vector3 estimate) ||
            Vector3.Distance(estimate, simulation.Units.Position[target.Index]) > 0.05f)
            return false;

        simulation.SetHeadNormal(observer, -Vector3.UnitX);
        for (int i = 0; i < 11; i++)
            simulation.Update(world, 0.11f);

        return !simulation.AI.TryGetFreshVisualContact(observer.Index, target, out _);
    }

    public static bool CachedVisibilityDoesNotRefreshLostContact()
    {
        if (!TryCreateVisionScenario(out WorldMap world, out UnitSimulation simulation,
                out UnitId observer, out UnitId target))
            return false;

        simulation.Update(world, 0.11f);
        if (!simulation.PerceptionMemory.TryGetMemory(observer.Index, out TacticalContactMemory memory) ||
            !memory.TryGetContact(target, out TacticalContact contact) ||
            contact.LastVisualAtSeconds < 0f)
            return false;

        float lastVisual = contact.LastVisualAtSeconds;
        // Keep the target nearby but turn the observer away. The old visibility cache
        // must not count as a fresh visual observation while the target is outside the FOV.
        simulation.SetHeadNormal(observer, -Vector3.UnitX);
        for (int i = 0; i < 11; i++)
            simulation.Update(world, 0.11f);

        bool visualTimeUnchanged =
            MathF.Abs(contact.LastVisualAtSeconds - lastVisual) < 0.0001f;
        bool downgraded = contact.Kind == TacticalContactKind.LastKnown;
        bool notFresh = !simulation.Vision.WasVisibleOnLatestUpdate(observer.Index, target.Index);
        bool aiRemainsDisabled = !simulation.AI.Enabled;
        if (!visualTimeUnchanged || !downgraded || !notFresh || !aiRemainsDisabled)
        {
            Console.WriteLine(
                $"[Perception cache diagnostic] visual={lastVisual:F3}->{contact.LastVisualAtSeconds:F3}; " +
                $"kind={contact.Kind}; fresh={ !notFresh }; aiEnabled={simulation.AI.Enabled};");
        }

        return visualTimeUnchanged && downgraded && notFresh && aiRemainsDisabled;
    }

    private static bool TryCreateVisionScenario(
        out WorldMap world,
        out UnitSimulation simulation,
        out UnitId observer,
        out UnitId target)
    {
        world = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        simulation = new UnitSimulation(4, 64);
        observer = simulation.Spawn(
            UnitType.Colonist, new Vector3(10f, 10f, 0.1f),
            Vector3.UnitX, Vector3.UnitX, factionTag: 1);
        target = simulation.Spawn(
            UnitType.Colonist, new Vector3(12f, 10f, 0.1f),
            -Vector3.UnitX, -Vector3.UnitX, factionTag: 2);
        return !simulation.AI.Enabled && observer.IsValid && target.IsValid;
    }

    private static PerceptionObservation Observation(
        PerceptionSense sense,
        TacticalStimulusType type,
        Vector3 position,
        float occurredAt,
        float receivedAt,
        float confidence,
        float positionError,
        UnitId? subject = null,
        float lifetime = 5f)
    {
        bool identified = subject.HasValue;
        return new PerceptionObservation(
            sense,
            type,
            position,
            positionError,
            intensity: confidence,
            detectionConfidence: confidence,
            identificationConfidence: identified ? confidence : 0f,
            detectionLevel: confidence >= 0.45f
                ? PerceptionDetectionLevel.Detected
                : PerceptionDetectionLevel.Suspected,
            identificationLevel: identified
                ? PerceptionIdentificationLevel.Identified
                : PerceptionIdentificationLevel.Unknown,
            occurredAtSeconds: occurredAt,
            receivedAtSeconds: receivedAt,
            expiresAtSeconds: occurredAt + lifetime,
            identifiedSubject: subject);
    }
}
