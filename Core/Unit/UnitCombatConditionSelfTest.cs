using System.Numerics;

namespace Core.Unit;

public static class UnitCombatConditionSelfTest
{
    public static bool Run()
    {
        var simulation = new UnitSimulation(4, 32);
        UnitId unit = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 0.1f),
            factionTag: 1);

        UnitCombatCondition healthy =
            UnitCombatConditionSystem.Calculate(simulation.Health, unit.Index);

        if (!healthy.CanUseTwoHandedWeapon ||
            healthy.AimSpreadMultiplier != 1f ||
            healthy.ReloadTimeMultiplier != 1f)
            return false;

        int start = unit.Index * simulation.Health.MaxParts;
        int count = simulation.Health.PartCount[unit.Index];
        bool damagedHand = false;

        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            if (simulation.Health.Part[index] != UnitHealthPartId.LeftHand)
                continue;

            simulation.Health.HitPoints[index] = 0f;
            damagedHand = true;
        }

        if (!damagedHand)
            return false;

        UnitCombatCondition injured =
            UnitCombatConditionSystem.Calculate(simulation.Health, unit.Index);

        return !injured.CanUseTwoHandedWeapon &&
               injured.AimSpreadMultiplier > healthy.AimSpreadMultiplier &&
               injured.ReloadTimeMultiplier > healthy.ReloadTimeMultiplier &&
               injured.MeleeDamageMultiplier < healthy.MeleeDamageMultiplier;
    }
}
