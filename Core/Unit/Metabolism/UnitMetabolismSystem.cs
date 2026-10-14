using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// SCUM-inspired survival metabolism: digestion, hydration, energy balance,
/// body composition, fatigue and blood volume. All work is allocation-free
/// once a store has reached its high-water capacity.
/// </summary>
public sealed class UnitMetabolismSystem
{
    private const float KcalAbsorptionPerHour = 240f;
    private const float WaterAbsorptionPerSecond = 0.0008f;
    private const float FatKcalPerKg = 7700f;

    public void Update(
        UnitStore units,
        UnitHealthStore health,
        UnitSuppressionStore suppression,
        UnitMetabolismStore metabolism,
        float deltaTime)
    {
        if (deltaTime <= 0f || !float.IsFinite(deltaTime))
            return;

        ReadOnlySpan<int> active = units.ActiveIndices;
        float[] overallHp = health.OverallHitPoints;
        Vector3[] velocity = units.Velocity;
        float[] moveSpeed = units.MoveSpeed;
        float[] bleedRates = health.BleedRate;
        short[] partCounts = health.PartCount;
        int maxParts = health.MaxParts;
        float[] suppressionValue = suppression.Value;

        for (int n = 0; n < active.Length; n++)
        {
            int unit = active[n];
            if (overallHp[unit] <= 0f)
                continue;

            float massScale = Math.Clamp(metabolism.BodyMassKg[unit] / 75f, 0.12f, 3f);
            float currentSpeed = velocity[unit].Length();
            float effort = Math.Clamp(
                currentSpeed / MathF.Max(0.1f, moveSpeed[unit]),
                0f,
                1.25f);

            UpdateDigestion(metabolism, unit, deltaTime);

            float energyDemand = (70f + 350f * effort) * massScale * deltaTime / 3600f;
            float storedEnergy = metabolism.EnergyKcal[unit];
            metabolism.EnergyKcal[unit] = MathF.Max(0f, storedEnergy - energyDemand);
            float energyDeficit = MathF.Max(0f, energyDemand - storedEnergy);

            // Body fat buffers the energy reserve, but cannot fully cover hard exertion.
            if (energyDeficit > 0f && metabolism.BodyFatKg[unit] > 0.5f)
            {
                float mobilized = MathF.Min(
                    energyDeficit,
                    110f * massScale * deltaTime / 3600f);
                metabolism.BodyFatKg[unit] = MathF.Max(
                    0.5f,
                    metabolism.BodyFatKg[unit] - mobilized / FatKcalPerKg);
                metabolism.EnergyKcal[unit] += mobilized;
            }

            float waterDemand = (0.075f + 0.35f * effort) * massScale * deltaTime / 3600f;
            metabolism.WaterLiters[unit] = MathF.Max(
                0f,
                metabolism.WaterLiters[unit] - waterDemand);

            float fatigue = metabolism.Fatigue[unit];
            if (effort > 0.08f)
            {
                float depletion = 0.018f + 0.035f * MathF.Min(1f, effort);
                if (metabolism.EnergyKcal[unit] / metabolism.MaxEnergyKcal[unit] < 0.15f)
                    depletion *= 1.6f;
                if (metabolism.WaterLiters[unit] / metabolism.MaxWaterLiters[unit] < 0.2f)
                    depletion *= 1.6f;
                fatigue += depletion * deltaTime;
            }
            else
            {
                float recovery = metabolism.EnergyKcal[unit] > 0f &&
                                 metabolism.WaterLiters[unit] > 0f
                    ? 0.025f
                    : 0.008f;
                fatigue -= recovery * deltaTime;
            }
            metabolism.Fatigue[unit] = Math.Clamp(fatigue, 0f, 100f);

            float stressTarget = Math.Clamp(suppressionValue[unit] * 100f, 0f, 100f);
            float stress = metabolism.Stress[unit];
            stress += (stressTarget > stress
                ? (stressTarget - stress) * MathF.Min(1f, deltaTime * 0.8f)
                : -0.12f * deltaTime);
            metabolism.Stress[unit] = Math.Clamp(stress, 0f, 100f);

            UpdateBloodVolume(health, metabolism, unit, deltaTime, massScale, maxParts,
                bleedRates, partCounts);
            UpdateComposition(metabolism, unit, deltaTime, massScale);
            UpdatePerformance(metabolism, unit);
        }
    }

    private static void UpdateDigestion(
        UnitMetabolismStore state,
        int unit,
        float deltaTime)
    {
        float transferFraction = 1f - MathF.Exp(-deltaTime / 180f);
        Transfer(ref state.StomachKcal[unit], ref state.GutKcal[unit], transferFraction);
        Transfer(ref state.StomachProtein[unit], ref state.GutProtein[unit], transferFraction);
        Transfer(ref state.StomachCarbohydrate[unit], ref state.GutCarbohydrate[unit], transferFraction);
        Transfer(ref state.StomachFat[unit], ref state.GutFat[unit], transferFraction);
        Transfer(ref state.StomachMicronutrients[unit], ref state.GutMicronutrients[unit], transferFraction);

        float absorbedKcal = MathF.Min(
            state.GutKcal[unit],
            KcalAbsorptionPerHour * deltaTime / 3600f);
        if (absorbedKcal > 0f && state.GutKcal[unit] > 0f)
        {
            float fraction = absorbedKcal / state.GutKcal[unit];
            state.GutKcal[unit] -= absorbedKcal;
            state.EnergyKcal[unit] += absorbedKcal;

            float excess = MathF.Max(0f, state.EnergyKcal[unit] - state.MaxEnergyKcal[unit]);
            if (excess > 0f)
            {
                state.EnergyKcal[unit] = state.MaxEnergyKcal[unit];
                state.BodyFatKg[unit] += excess / FatKcalPerKg;
            }

            state.ProteinReserveGrams[unit] = MathF.Min(
                220f, state.ProteinReserveGrams[unit] + Take(ref state.GutProtein[unit], fraction));
            state.CarbohydrateReserveGrams[unit] = MathF.Min(
                500f, state.CarbohydrateReserveGrams[unit] + Take(ref state.GutCarbohydrate[unit], fraction));
            state.FatReserveGrams[unit] = MathF.Min(
                900f, state.FatReserveGrams[unit] + Take(ref state.GutFat[unit], fraction));
            state.MicronutrientReserve[unit] = MathF.Min(
                100f, state.MicronutrientReserve[unit] + Take(ref state.GutMicronutrients[unit], fraction));
        }

        Transfer(ref state.StomachWater[unit], ref state.GutWater[unit],
            1f - MathF.Exp(-deltaTime / 120f));
        float absorbedWater = MathF.Min(
            state.GutWater[unit],
            WaterAbsorptionPerSecond * deltaTime);
        state.GutWater[unit] -= absorbedWater;
        state.WaterLiters[unit] = MathF.Min(
            state.MaxWaterLiters[unit],
            state.WaterLiters[unit] + absorbedWater);

        state.MicronutrientReserve[unit] = MathF.Max(
            0f,
            state.MicronutrientReserve[unit] - deltaTime * (20f / 86400f));
    }

    private static void UpdateBloodVolume(
        UnitHealthStore health,
        UnitMetabolismStore state,
        int unit,
        float deltaTime,
        float massScale,
        int maxParts,
        float[] bleedRates,
        short[] partCounts)
    {
        float bleedingPerSecond = 0f;
        int start = unit * maxParts;
        int count = Math.Min(partCounts[unit], maxParts);
        for (int part = 0; part < count; part++)
            bleedingPerSecond += bleedRates[start + part];

        if (bleedingPerSecond > 0f)
        {
            state.BloodLiters[unit] = MathF.Max(
                0f,
                state.BloodLiters[unit] - bleedingPerSecond * deltaTime * 0.003f);
        }
        else if (state.EnergyKcal[unit] > state.MaxEnergyKcal[unit] * 0.15f &&
                 state.WaterLiters[unit] > state.MaxWaterLiters[unit] * 0.2f &&
                 state.ProteinReserveGrams[unit] > 20f)
        {
            state.BloodLiters[unit] = MathF.Min(
                state.MaxBloodLiters[unit],
                state.BloodLiters[unit] + 0.000008f * massScale * deltaTime);
        }
    }

    private static void UpdateComposition(
        UnitMetabolismStore state,
        int unit,
        float deltaTime,
        float massScale)
    {
        float protein = state.ProteinReserveGrams[unit];
        float effort = state.Fatigue[unit] > 0f ? 1f : 0f;
        state.ProteinReserveGrams[unit] = MathF.Max(
            0f,
            protein - (0.0007f + effort * 0.0005f) * deltaTime * massScale);
        state.CarbohydrateReserveGrams[unit] = MathF.Max(
            0f,
            state.CarbohydrateReserveGrams[unit] - 0.0012f * deltaTime * massScale);
        state.FatReserveGrams[unit] = MathF.Max(
            0f,
            state.FatReserveGrams[unit] - 0.0005f * deltaTime * massScale);

        if (state.ProteinReserveGrams[unit] < 12f &&
            state.EnergyKcal[unit] < state.MaxEnergyKcal[unit] * 0.2f)
        {
            state.MuscleMassKg[unit] = MathF.Max(
                0.5f,
                state.MuscleMassKg[unit] - 0.0000015f * deltaTime * massScale);
        }
        else if (state.Fatigue[unit] < 20f &&
                 state.ProteinReserveGrams[unit] > 80f &&
                 state.EnergyKcal[unit] > state.MaxEnergyKcal[unit] * 0.5f)
        {
            state.MuscleMassKg[unit] = MathF.Min(
                state.BodyMassKg[unit] * 0.55f,
                state.MuscleMassKg[unit] + 0.00000012f * deltaTime * massScale);
        }

        state.BodyFatKg[unit] = Math.Clamp(
            state.BodyFatKg[unit],
            0.2f,
            state.BodyMassKg[unit] * 0.6f);
    }

    private static void UpdatePerformance(UnitMetabolismStore state, int unit)
    {
        float energyRatio = state.MaxEnergyKcal[unit] > 0f
            ? state.EnergyKcal[unit] / state.MaxEnergyKcal[unit]
            : 0f;
        float waterRatio = state.MaxWaterLiters[unit] > 0f
            ? state.WaterLiters[unit] / state.MaxWaterLiters[unit]
            : 0f;
        float bloodRatio = state.MaxBloodLiters[unit] > 0f
            ? state.BloodLiters[unit] / state.MaxBloodLiters[unit]
            : 0f;

        float energyFactor = energyRatio < 0.05f ? 0.55f :
            energyRatio < 0.15f ? 0.7f :
            energyRatio < 0.3f ? 0.85f : 1f;
        float waterFactor = waterRatio < 0.08f ? 0.4f :
            waterRatio < 0.2f ? 0.65f :
            waterRatio < 0.35f ? 0.82f : 1f;
        float bloodFactor = bloodRatio < 0.2f ? 0.45f :
            bloodRatio < 0.45f ? 0.7f :
            bloodRatio < 0.65f ? 0.88f : 1f;
        float fatigueFactor = 1f - state.Fatigue[unit] * 0.0045f;
        float nutritionFactor = state.MicronutrientReserve[unit] < 10f ? 0.8f : 1f;
        if (state.ProteinReserveGrams[unit] < 12f)
            nutritionFactor *= 0.85f;

        state.MovementMultiplier[unit] = Math.Clamp(
            energyFactor * waterFactor * bloodFactor * fatigueFactor * nutritionFactor,
            0.2f,
            1f);

        float muscleRatio = state.BodyMassKg[unit] > 0f
            ? state.MuscleMassKg[unit] / (state.BodyMassKg[unit] * 0.4f)
            : 1f;
        float conditionFactor = Math.Clamp(
            0.65f + 0.35f * MathF.Min(1f, waterRatio) -
            state.Fatigue[unit] * 0.0025f,
            0.35f,
            1f);
        state.EffectiveStrength[unit] = state.Strength[unit] *
            Math.Clamp(muscleRatio, 0.4f, 1.2f) * conditionFactor;
        state.EffectiveConstitution[unit] = state.Constitution[unit] *
            Math.Clamp(0.5f + 0.5f * energyRatio, 0.3f, 1f);
        state.EffectiveDexterity[unit] = state.Dexterity[unit] *
            Math.Clamp(0.55f + 0.45f * waterRatio - state.Fatigue[unit] * 0.003f, 0.3f, 1f);

        if (energyRatio < 0.05f || waterRatio < 0.05f || bloodRatio < 0.08f)
            state.Condition[unit] = UnitMetabolicCondition.Critical;
        else if (waterRatio < 0.2f)
            state.Condition[unit] = UnitMetabolicCondition.Dehydrated;
        else if (energyRatio < 0.12f)
            state.Condition[unit] = UnitMetabolicCondition.Starving;
        else if (energyRatio < 0.28f || state.MicronutrientReserve[unit] < 12f)
            state.Condition[unit] = UnitMetabolicCondition.Hungry;
        else if (state.Fatigue[unit] > 75f)
            state.Condition[unit] = UnitMetabolicCondition.Exhausted;
        else
            state.Condition[unit] = UnitMetabolicCondition.Normal;
    }

    private static void Transfer(ref float from, ref float to, float fraction)
    {
        float amount = from * fraction;
        from -= amount;
        to += amount;
    }

    private static float Take(ref float value, float fraction)
    {
        float amount = value * fraction;
        value -= amount;
        return amount;
    }
}
