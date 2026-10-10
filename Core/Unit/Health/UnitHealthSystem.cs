using System;

namespace Core.Unit;

public sealed class UnitHealthSystem
{
    public void Update(
        UnitStore units,
        UnitHealthStore health,
        float deltaTime)
    {
        ReadOnlySpan<int> active =
            units.ActiveIndices;

        float[] overallMax =
            health.OverallMaxHitPoints;

        float[] overall =
            health.OverallHitPoints;

        float[] max =
            health.MaxHitPoints;

        float[] hitPoints =
            health.HitPoints;

        float[] bleedRate =
            health.BleedRate;

        int maxParts =
            health.MaxParts;

        int capacity =
            health.Capacity;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            if (unit >= capacity ||
                overall[unit] <= 0f ||
                overallMax[unit] <= 0f)
            {
                continue;
            }

            overall[unit] =
                Math.Clamp(
                    overall[unit],
                    0f,
                    overallMax[unit]);

            int start =
                unit *
                maxParts;

            int count =
                health.PartCount[unit];

            for (int part = 0;
                 part < count;
                 part++)
            {
                int index =
                    start + part;

                if (bleedRate[index] > 0f && deltaTime > 0f)
                {
                    float bleedDamage =
                        bleedRate[index] * deltaTime;

                    float appliedToPart =
                        MathF.Min(
                            bleedDamage,
                            hitPoints[index]);

                    hitPoints[index] -= appliedToPart;

                    // Internal organs keep bleeding after being destroyed.
                    // External wounds only consume the remaining local HP.
                    if (health.Kind[index] ==
                        UnitHealthPartKind.Organ)
                    {
                        overall[unit] =
                            MathF.Max(
                                0f,
                                overall[unit] -
                                bleedDamage);
                    }
                    else
                    {
                        overall[unit] =
                            MathF.Max(
                                0f,
                                overall[unit] -
                                appliedToPart);
                    }

                    bleedRate[index] =
                        MathF.Max(
                            0f,
                            bleedRate[index] -
                            0.01f * deltaTime);
                }

                hitPoints[index] =
                    Math.Clamp(
                        hitPoints[index],
                        0f,
                        max[index]);
            }
        }
    }

    public void AddBleed(
        UnitStore units,
        UnitHealthStore health,
        UnitId unitId,
        UnitHealthPartId partId,
        float amountPerSecond)
    {
        if (amountPerSecond <= 0f ||
            !units.TryGetIndex(unitId, out int unitIndex) ||
            health.OverallHitPoints[unitIndex] <= 0f ||
            !health.TryFindPart(unitIndex, partId, out int partIndex))
            return;

        health.BleedRate[partIndex] =
            MathF.Min(
                10f,
                health.BleedRate[partIndex] + amountPerSecond);
    }

    public bool ApplyDamage(
        UnitStore units,
        UnitHealthStore health,
        UnitId unitId,
        UnitHealthPartId partId,
        float amount)
    {
        if (amount <= 0f ||
            !units.TryGetIndex(
                unitId,
                out int unitIndex) ||
            health.OverallHitPoints[unitIndex] <= 0f)
        {
            return false;
        }

        if (!health.TryFindPart(
                unitIndex,
                partId,
                out int partIndex))
        {
            return false;
        }

        float hitPoints =
            health.HitPoints[partIndex];

        float applied =
            MathF.Min(
                amount,
                hitPoints);

        health.HitPoints[partIndex] =
            hitPoints - applied;

        health.OverallHitPoints[unitIndex] =
            MathF.Max(
                0f,
                health.OverallHitPoints[unitIndex] - applied);

        return true;
    }
}
