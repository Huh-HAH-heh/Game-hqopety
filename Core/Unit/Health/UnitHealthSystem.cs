using System;

namespace Core.Unit;

public sealed class UnitHealthSystem
{
    public void Update(UnitStore units, UnitHealthStore health, float deltaTime)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        float[] overallMax = health.OverallMaxHitPoints;
        float[] overall = health.OverallHitPoints;
        float[] max = health.MaxHitPoints;
        float[] hitPoints = health.HitPoints;
        float[] bleedRate = health.BleedRate;
        UnitHealthPartKind[] kinds = health.Kind;
        short[] partCounts = health.PartCount;
        int maxParts = health.MaxParts;
        int capacity = health.Capacity;

        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            if (unit >= capacity || overall[unit] <= 0f || overallMax[unit] <= 0f)
                continue;

            overall[unit] = Math.Clamp(overall[unit], 0f, overallMax[unit]);
            int start = unit * maxParts;

            for (int part = 0; part < partCounts[unit]; part++)
            {
                int index = start + part;

                if (bleedRate[index] > 0f && deltaTime > 0f)
                {
                    float bleedDamage = bleedRate[index] * deltaTime;
                    float appliedToPart = MathF.Min(bleedDamage, hitPoints[index]);
                    hitPoints[index] -= appliedToPart;

                    // Internal organs keep bleeding after being destroyed.
                    // External wounds only consume the remaining local HP.
                    float damageToUnit = kinds[index] == UnitHealthPartKind.Organ
                        ? bleedDamage
                        : appliedToPart;

                    overall[unit] = MathF.Max(0f, overall[unit] - damageToUnit);
                    bleedRate[index] = MathF.Max(0f, bleedRate[index] - 0.01f * deltaTime);
                }

                hitPoints[index] = Math.Clamp(hitPoints[index], 0f, max[index]);
            }
        }
    }

    public void AddRegionBleed(
        UnitStore units,
        UnitHealthStore health,
        UnitId unitId,
        int regionIndex,
        int bindingIndex,
        float amountPerSecond)
    {
        if (amountPerSecond <= 0f ||
            !units.TryGetIndex(unitId, out int unitIndex) ||
            health.OverallHitPoints[unitIndex] <= 0f ||
            !health.TryFindRegionPart(unitIndex, regionIndex, bindingIndex, out int partIndex))
        {
            return;
        }

        health.BleedRate[partIndex] = MathF.Min(10f, health.BleedRate[partIndex] + amountPerSecond);
    }

    public bool ApplyRegionDamage(
        UnitStore units,
        UnitHealthStore health,
        UnitId unitId,
        int regionIndex,
        int bindingIndex,
        float amount)
    {
        if (amount <= 0f ||
            !units.TryGetIndex(unitId, out int unitIndex) ||
            health.OverallHitPoints[unitIndex] <= 0f ||
            !health.TryFindRegionPart(unitIndex, regionIndex, bindingIndex, out int partIndex))
        {
            return false;
        }

        return ApplyDamageAtIndex(health, unitIndex, partIndex, amount);
    }

    public void AddBleed(
        UnitStore units,
        UnitHealthStore health,
        UnitId unitId,
        UnitHealthPartId partId,
        float amountPerSecond,
        UnitHitRegionStore? hitRegions = null)
    {
        if (amountPerSecond <= 0f ||
            !units.TryGetIndex(unitId, out int unitIndex) ||
            health.OverallHitPoints[unitIndex] <= 0f ||
            !UnitHitSystem.TryFindBindingForPart(
                units, unitIndex, partId,
                out int regionIndex, out int bindingIndex, hitRegions))
        {
            return;
        }

        AddRegionBleed(units, health, unitId, regionIndex, bindingIndex, amountPerSecond);
    }

    public bool ApplyDamage(
        UnitStore units,
        UnitHealthStore health,
        UnitId unitId,
        UnitHealthPartId partId,
        float amount,
        UnitHitRegionStore? hitRegions = null)
    {
        if (amount <= 0f ||
            !units.TryGetIndex(unitId, out int unitIndex) ||
            health.OverallHitPoints[unitIndex] <= 0f ||
            !UnitHitSystem.TryFindBindingForPart(
                units, unitIndex, partId,
                out int regionIndex, out int bindingIndex, hitRegions))
        {
            return false;
        }

        return ApplyRegionDamage(units, health, unitId, regionIndex, bindingIndex, amount);
    }

    private static bool ApplyDamageAtIndex(
        UnitHealthStore health,
        int unitIndex,
        int partIndex,
        float amount)
    {
        float hitPoints = health.HitPoints[partIndex];
        float applied = MathF.Min(amount, hitPoints);

        health.HitPoints[partIndex] = hitPoints - applied;
        health.OverallHitPoints[unitIndex] =
            MathF.Max(0f, health.OverallHitPoints[unitIndex] - applied);

        return applied > 0f;
    }
}
