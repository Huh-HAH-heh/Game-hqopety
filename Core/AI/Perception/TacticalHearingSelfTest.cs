using System.Numerics;
using Core.AI.Behaviac;
using Core.Items;
using Core.Map;
using Core.Unit;

namespace Core.AI.Perception;

/// <summary>
/// End-to-end hearing regression: a real weapon shot must be delivered as
/// an anonymous sound contact, and Behaviac must choose an audible investigation
/// without acquiring the hidden shooter as a visual target.
/// </summary>
public static class TacticalHearingSelfTest
{
    public static bool CoalescesGunshotsInSpaceAndTime()
    {
        var worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64) { VisionEnabled = false };
        UnitId shooter = simulation.Spawn(
            UnitType.Colonist, new Vector3(3.5f, 4.5f, 0.1f), factionTag: 1);
        UnitId listener = simulation.Spawn(
            UnitType.Colonist, new Vector3(12.5f, 8.5f, 0.1f), factionTag: 2);

        for (int i = 0; i < 8; i++)
        {
            simulation.HearingSystem.EmitGunshot(
                shooter, new Vector3(3.5f + i * 0.15f, 4.5f, 0.1f), 1);
        }

        // A distinct spatial cluster stays distinct even in the same 50 ms window.
        for (int i = 0; i < 2; i++)
        {
            simulation.HearingSystem.EmitGunshot(
                shooter, new Vector3(45.5f + i * 0.1f, 4.5f, 0.1f), 1);
        }

        simulation.Update(worldMap, 0.025f);
        if (simulation.HearingSystem.PendingAcousticBatchCount != 2 ||
            simulation.HearingSystem.LastGunshotEventsQueued != 0 ||
            simulation.HearingSystem.HasInvestigableContact(listener.Index))
            return false;

        simulation.Update(worldMap, 0.025f);

        return simulation.HearingSystem.LastGunshotEventsQueued == 10 &&
               simulation.HearingSystem.LastGunshotBatchesQueued == 2 &&
               simulation.HearingSystem.LastGunshotsMerged == 8 &&
               simulation.HearingSystem.PendingAcousticBatchCount == 0 &&
               simulation.HearingSystem.TryGetBestInvestigableContact(
                   listener.Index, out TacticalContact contact) &&
               contact.LastStimulusType == TacticalStimulusType.Gunshot &&
               !contact.SubjectUnit.HasValue;
    }


    public static bool CoalescesFootstepsInLargerSpatialAndTimeCells()
    {
        var worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64) { VisionEnabled = false };
        UnitId first = simulation.Spawn(
            UnitType.Colonist, new Vector3(10.5f, 10.5f, 0.1f), factionTag: 1);
        UnitId second = simulation.Spawn(
            UnitType.Colonist, new Vector3(12.5f, 10.5f, 0.1f), factionTag: 1);
        UnitId listener = simulation.Spawn(
            UnitType.Colonist, new Vector3(26.5f, 10.5f, 0.1f), factionTag: 2);

        simulation.Units.Velocity[first.Index] = Vector3.UnitX;
        simulation.Units.Velocity[second.Index] = Vector3.UnitX;

        simulation.Update(worldMap, 0.05f);
        if (simulation.HearingSystem.PendingAcousticBatchCount != 1 ||
            simulation.HearingSystem.LastContactsRecorded != 0)
            return false;

        simulation.Update(worldMap, 0.05f);
        if (simulation.HearingSystem.PendingAcousticBatchCount != 1 ||
            simulation.HearingSystem.LastContactsRecorded != 0)
            return false;

        simulation.Update(worldMap, 0.05f);

        return simulation.HearingSystem.PendingAcousticBatchCount == 0 &&
               simulation.HearingSystem.LastQueuedPulseCount == 1 &&
               simulation.HearingSystem.LastContactsRecorded == 1 &&
               simulation.HearingSystem.TryGetBestInvestigableContact(
                   listener.Index, out TacticalContact contact) &&
               contact.LastStimulusType == TacticalStimulusType.Footstep;
    }

    public static bool SharedAcousticEventsKeepListenerContactsIndependent()
    {
        var worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64) { VisionEnabled = false };
        UnitId source = simulation.Spawn(
            UnitType.Colonist, new Vector3(10.5f, 10.5f, 0.1f), factionTag: 1);
        UnitId eastListener = simulation.Spawn(
            UnitType.Colonist, new Vector3(20.5f, 10.5f, 0.1f), factionTag: 2);
        UnitId northListener = simulation.Spawn(
            UnitType.Colonist, new Vector3(10.5f, 20.5f, 0.1f), factionTag: 3);
        simulation.Units.Velocity[source.Index] = Vector3.UnitX;

        simulation.Update(worldMap, 0.05f);
        simulation.Update(worldMap, 0.05f);
        simulation.Update(worldMap, 0.05f);

        if (!simulation.HearingSystem.TryGetBestInvestigableContact(
                eastListener.Index, out TacticalContact eastContact) ||
            !simulation.HearingSystem.TryGetBestInvestigableContact(
                northListener.Index, out TacticalContact northContact))
            return false;

        // Both listeners hear the same event, but neither gains its source identity.
        // Their local estimates differ because each observer has a distinct bearing estimate.
        return !eastContact.SubjectUnit.HasValue &&
               !northContact.SubjectUnit.HasValue &&
               eastContact.LastStimulusType == TacticalStimulusType.Footstep &&
               northContact.LastStimulusType == TacticalStimulusType.Footstep &&
               Vector3.DistanceSquared(
                   eastContact.EstimatedPosition, northContact.EstimatedPosition) > 0.01f;
    }

    public static bool FootstepListenerWorkIsBoundedAndKeepsDistinctDirections()
    {
        var worldMap = new WorldMap(regionsX: 2, regionsY: 2, layerCount: 64);
        var simulation = new UnitSimulation(8, 64) { VisionEnabled = false };
        UnitId listener = simulation.Spawn(
            UnitType.Colonist, new Vector3(48f, 48f, 0.1f), factionTag: 2);

        Vector3[] sourcePositions =
        {
            new(66f, 48f, 0.1f),
            new(54f, 65f, 0.1f),
            new(33f, 59f, 0.1f),
            new(33f, 37f, 0.1f),
            new(54f, 31f, 0.1f)
        };

        for (int i = 0; i < sourcePositions.Length; i++)
        {
            UnitId source = simulation.Spawn(
                UnitType.Colonist, sourcePositions[i], factionTag: 1);
            simulation.Units.Velocity[source.Index] = Vector3.UnitX;
        }

        simulation.Update(worldMap, 0.05f);
        simulation.Update(worldMap, 0.05f);
        simulation.Update(worldMap, 0.05f);

        return simulation.HearingSystem.PendingSoundEventCount == 0 &&
               simulation.HearingSystem.LastQueuedPulseCount == sourcePositions.Length &&
               simulation.HearingSystem.LastFootstepClustersPublished ==
                   simulation.HearingSystem.FootstepClustersPerObserver &&
               simulation.HearingSystem.LastFootstepEventsCoalesced == 1 &&
               simulation.HearingSystem.LastContactsRecorded ==
                   simulation.HearingSystem.FootstepClustersPerObserver &&
               simulation.HearingSystem.TryGetBestInvestigableContact(
                   listener.Index, out TacticalContact contact) &&
               !contact.SubjectUnit.HasValue &&
               contact.LastStimulusType == TacticalStimulusType.Footstep;
    }

    public static bool UrgentDangerBypassesAcousticBatchWindow()
    {
        var worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64) { VisionEnabled = false };
        UnitId source = simulation.Spawn(
            UnitType.Colonist, new Vector3(10.5f, 10.5f, 0.1f), factionTag: 1);
        UnitId listener = simulation.Spawn(
            UnitType.Colonist, new Vector3(12.5f, 10.5f, 0.1f), factionTag: 2);

        simulation.HearingSystem.EmitDanger(
            source, 1, TacticalStimulusType.NearMiss,
            new Vector3(12f, 10.5f, 0.1f), intensity: 1f, audibleRange: 40f);
        simulation.Update(worldMap, 0.025f);

        return simulation.HearingSystem.PendingAcousticBatchCount == 0 &&
               simulation.HearingSystem.HasImmediateDangerContact(listener.Index);
    }

    public static bool Run()
    {
        var worldMap = new WorldMap(regionsX: 1, regionsY: 1, layerCount: 64);
        var simulation = new UnitSimulation(4, 64)
        {
            VisionEnabled = false
        };

        UnitId shooter = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(3.5f, 4.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId listener = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(8.5f, 8.5f, 0.1f),
            -Vector3.UnitX,
            -Vector3.UnitX,
            factionTag: 2);
        UnitId friendly = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(4.5f, 12.5f, 0.1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        int weaponSlot = simulation.AddInventoryItem(shooter, WeaponCatalog.AssaultRifle);
        if (weaponSlot < 0 ||
            !simulation.EquipWeapon(shooter, weaponSlot, UnitWeaponSlot.Primary))
            return false;

        // This goes through UnitWeaponSystem.ShotFired, the same event hook
        // used during normal gameplay. Vision is disabled, so no sighting can
        // explain the listener's contact or subsequent response.
        if (!simulation.FireWeapon(shooter, UnitWeaponSlot.Primary, Vector3.UnitX) ||
            simulation.TotalShotsFired <= 0)
            return false;
        simulation.AI.Enabled = true;
        simulation.Update(worldMap, 0.05f);

        if (!simulation.HearingSystem.TryGetBestInvestigableContact(
                listener.Index, out TacticalContact contact) ||
            contact.LastStimulusType != TacticalStimulusType.Gunshot ||
            contact.SubjectUnit.HasValue ||
            contact.Confidence < 0.16f)
            return false;

        // Same-faction units deliberately ignore friendly gunshots.
        if (simulation.HearingSystem.HasInvestigableContact(friendly.Index))
            return false;

        for (int i = 0; i < 8; i++)
        {
            if (simulation.AI.Store.LastIntent[listener.Index] ==
                BehaviacUnitIntent.InvestigateContact)
            {
                break;
            }

            simulation.Update(worldMap, 0.05f);
        }

        Vector3 goal = simulation.AI.Store.Goal[listener.Index];
        Vector2 goalDelta = new(
            goal.X - contact.EstimatedPosition.X,
            goal.Y - contact.EstimatedPosition.Y);
        float maxSearchOffset = MathF.Max(
            1f,
            contact.PositionErrorRadius * 0.15f) + 0.1f;

        return simulation.AI.Store.LastIntent[listener.Index] ==
                   BehaviacUnitIntent.InvestigateContact &&
               simulation.AI.Store.HasGoal[listener.Index] &&
               simulation.AI.Store.State[listener.Index] == UnitAiState.Search &&
               simulation.Units.HasTarget[listener.Index] &&
               !simulation.AI.Store.HasTarget[listener.Index] &&
               goalDelta.LengthSquared() <= maxSearchOffset * maxSearchOffset;
    }
}
