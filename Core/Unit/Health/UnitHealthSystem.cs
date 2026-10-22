using System;

namespace Core.Unit;

public sealed class UnitHealthSystem
{
    public void Update(UnitStore units, UnitHealthStore health, float deltaTime)
    {
        ReadOnlySpan<int> active = units.ActiveIndices;
        float[] overallMax = health.OverallMaxHitPoints;
        float[] overall = health.OverallHitPoints;
        float[] pain = health.Pain;
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
            if (unit >= capacity)
                continue;

            if (overall[unit] <= 0f || overallMax[unit] <= 0f)
            {
                pain[unit] = 0f;
                continue;
            }

            overall[unit] = Math.Clamp(overall[unit], 0f, overallMax[unit]);
            int start = unit * maxParts;
            int count = partCounts[unit];

            for (int part = 0; part < count; part++)
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

            pain[unit] = CalculatePain(
                overall[unit],
                overallMax[unit],
                start,
                count,
                max,
                hitPoints,
                bleedRate,
                kinds);
        }
    }

    private static float CalculatePain(
        float overallHitPoints,
        float overallMaxHitPoints,
        int start,
        int count,
        float[] maxHitPoints,
        float[] hitPoints,
        float[] bleedRate,
        UnitHealthPartKind[] kinds)
    {
        // Overall damage keeps pain meaningful for profiles with very few local tissues.
        float overallDamage = overallMaxHitPoints > 0f
            ? 1f - Math.Clamp(overallHitPoints / overallMaxHitPoints, 0f, 1f)
            : 0f;
        float injuryLoad = overallDamage * 0.65f;

        for (int part = 0; part < count; part++)
        {
            int index = start + part;
            float maxPartHp = maxHitPoints[index];
            if (maxPartHp <= 0f)
                continue;

            float missing = Math.Clamp(
                (maxPartHp - hitPoints[index]) / maxPartHp,
                0f,
                1f);
            float tissueWeight = kinds[index] == UnitHealthPartKind.Organ
                ? 1.2f
                : 1f;

            injuryLoad += missing * tissueWeight;

            // Active bleeding increases pain on top of direct tissue damage.
            injuryLoad += Math.Clamp(bleedRate[index], 0f, 10f) * 0.08f;
        }

        // Minor wounds create little pain; multiple severe injuries approach 100.
        return Math.Clamp(
            100f * (1f - MathF.Exp(-0.45f * injuryLoad)),
            0f,
            100f);
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
