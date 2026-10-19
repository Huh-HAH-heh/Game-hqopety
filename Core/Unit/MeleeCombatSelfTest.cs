using System.Numerics;
using Core.Items;
using Core.Map;

namespace Core.Unit;

public static class MeleeCombatSelfTest
{
    public static bool Run()
    {
        var simulation = new UnitSimulation(8, 64);
        UnitId attacker = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 0.1f),
            factionTag: 1);
        UnitId defender = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(11.35f, 10.5f, 0.1f),
            factionTag: 2);

        int attackerWeapon = simulation.AddInventoryItem(attacker, MeleeWeaponCatalog.CombatKnife, 100f);
        int defenderWeapon = simulation.AddInventoryItem(defender, MeleeWeaponCatalog.ShockBaton, 100f);
        if (attackerWeapon < 0 || defenderWeapon < 0 ||
            !simulation.EquipWeapon(attacker, attackerWeapon, UnitWeaponSlot.Melee) ||
            !simulation.EquipWeapon(defender, defenderWeapon, UnitWeaponSlot.Melee))
            return false;

        simulation.AI.Store.RememberTarget(
            attacker.Index, defender, simulation.Units.Position[defender.Index], 30f);

        var healthSystem = new UnitHealthSystem();
        simulation.MeleeCombat.InitializeUnit(attacker.Index, attacker);
        simulation.MeleeCombat.InitializeUnit(defender.Index, defender);

        bool staggerStopsMovement = false;
        var movementSystem = new UnitMovementSystem();
        var worldMap = new WorldMap(regionsX: 10, regionsY: 10, layerCount: 16);

        for (int i = 0; i < 600; i++)
        {
            simulation.MeleeCombat.Update(
                simulation.Units,
                simulation.AI.Store,
                simulation.Inventory,
                simulation.Health,
                healthSystem,
                simulation.Skills,
                simulation.Suppression,
                simulation.HitRegions,
                simulation.Bodies,
                0.05f);

            if (simulation.MeleeCombat.IsMovementLocked(defender.Index) &&
                simulation.Health.OverallHitPoints[attacker.Index] > 0f &&
                simulation.Health.OverallHitPoints[defender.Index] > 0f)
            {
                Vector3 positionBefore = simulation.Units.Position[defender.Index];
                simulation.Units.Target[defender.Index] = positionBefore + new Vector3(8f, 0f, 0f);
                simulation.Units.HasTarget[defender.Index] = true;
                simulation.Units.Velocity[defender.Index] = new Vector3(4f, 0f, 0f);
                movementSystem.Update(
                    simulation.Units, worldMap, 0.1f,
                    simulation.Health, simulation.MeleeCombat);

                staggerStopsMovement =
                    Vector3.DistanceSquared(
                        positionBefore, simulation.Units.Position[defender.Index]) < 0.000001f &&
                    simulation.Units.Velocity[defender.Index] == Vector3.Zero;
                break;
            }

            if (simulation.Health.OverallHitPoints[attacker.Index] <= 0f ||
                simulation.Health.OverallHitPoints[defender.Index] <= 0f)
                break;
        }

        var melee = simulation.MeleeCombat;
        return staggerStopsMovement &&
               melee.TotalSwings > 0 &&
               melee.TotalHits > 0 &&
               melee.TotalMisses + melee.TotalDodges + melee.TotalParries <= melee.TotalSwings + melee.TotalRipostes &&
               (melee.TotalParries == 0 || melee.TotalRipostes > 0);
    }
}
