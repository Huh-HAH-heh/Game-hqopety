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
                BehaviacUnitIntent.InvestigateAudibleContact)
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
                   BehaviacUnitIntent.InvestigateAudibleContact &&
               simulation.AI.Store.HasGoal[listener.Index] &&
               simulation.AI.Store.State[listener.Index] == UnitAiState.Search &&
               simulation.Units.HasTarget[listener.Index] &&
               !simulation.AI.Store.HasTarget[listener.Index] &&
               goalDelta.LengthSquared() <= maxSearchOffset * maxSearchOffset;
    }
}
