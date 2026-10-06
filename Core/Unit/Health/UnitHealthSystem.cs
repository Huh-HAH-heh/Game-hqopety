using System;

namespace Core.Unit;

public sealed class UnitHealthSystem
{
    public void Update(
        UnitStore units,
        UnitHealthStore health)
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

            if (unit >= capacity)
                continue;

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

                hitPoints[index] =
                    Math.Clamp(
                        hitPoints[index],
                        0f,
                        max[index]);
            }
        }
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
                out int unitIndex))
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
