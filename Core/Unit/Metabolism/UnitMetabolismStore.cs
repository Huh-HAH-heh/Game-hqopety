using System;

namespace Core.Unit;

public enum UnitMetabolicCondition : byte
{
    Normal,
    Hungry,
    Starving,
    Dehydrated,
    Exhausted,
    Critical
}

/// <summary>
/// Flat per-unit metabolism data. Values are deliberately stored in arrays
/// so hundreds or thousands of units do not allocate individual state objects.
/// </summary>
public sealed class UnitMetabolismStore
{
    public const float StomachCapacityKcal = 1400f;
    public const float StomachCapacityLiters = 1.5f;

    private byte[] _strength;
    private byte[] _constitution;
    private byte[] _dexterity;
    private byte[] _intelligence;
    private float[] _bodyMassKg;
    private float[] _energyKcal;
    private float[] _maxEnergyKcal;
    private float[] _waterLiters;
    private float[] _maxWaterLiters;
    private float[] _muscleMassKg;
    private float[] _bodyFatKg;
    private float[] _bloodLiters;
    private float[] _maxBloodLiters;
    private float[] _fatigue;
    private float[] _stress;
    private float[] _proteinReserveGrams;
    private float[] _carbohydrateReserveGrams;
    private float[] _fatReserveGrams;
    private float[] _micronutrientReserve;
    private float[] _stomachKcal;
    private float[] _gutKcal;
    private float[] _stomachProtein;
    private float[] _gutProtein;
    private float[] _stomachCarbohydrate;
    private float[] _gutCarbohydrate;
    private float[] _stomachFat;
    private float[] _gutFat;
    private float[] _stomachWater;
    private float[] _gutWater;
    private float[] _stomachMicronutrients;
    private float[] _gutMicronutrients;
    private float[] _movementMultiplier;
    private float[] _effectiveStrength;
    private float[] _effectiveConstitution;
    private float[] _effectiveDexterity;
    private UnitMetabolicCondition[] _condition;

    public int Capacity => _energyKcal.Length;
    public byte[] Strength => _strength;
    public byte[] Constitution => _constitution;
    public byte[] Dexterity => _dexterity;
    public byte[] Intelligence => _intelligence;
    public float[] BodyMassKg => _bodyMassKg;
    public float[] EnergyKcal => _energyKcal;
    public float[] MaxEnergyKcal => _maxEnergyKcal;
    public float[] WaterLiters => _waterLiters;
    public float[] MaxWaterLiters => _maxWaterLiters;
    public float[] MuscleMassKg => _muscleMassKg;
    public float[] BodyFatKg => _bodyFatKg;
    public float[] BloodLiters => _bloodLiters;
    public float[] MaxBloodLiters => _maxBloodLiters;
    public float[] Fatigue => _fatigue;
    public float[] Stress => _stress;
    public float[] ProteinReserveGrams => _proteinReserveGrams;
    public float[] CarbohydrateReserveGrams => _carbohydrateReserveGrams;
    public float[] FatReserveGrams => _fatReserveGrams;
    public float[] MicronutrientReserve => _micronutrientReserve;
    public float[] StomachKcal => _stomachKcal;
    public float[] GutKcal => _gutKcal;
    public float[] StomachProtein => _stomachProtein;
    public float[] GutProtein => _gutProtein;
    public float[] StomachCarbohydrate => _stomachCarbohydrate;
    public float[] GutCarbohydrate => _gutCarbohydrate;
    public float[] StomachFat => _stomachFat;
    public float[] GutFat => _gutFat;
    public float[] StomachWater => _stomachWater;
    public float[] GutWater => _gutWater;
    public float[] StomachMicronutrients => _stomachMicronutrients;
    public float[] GutMicronutrients => _gutMicronutrients;
    public float[] MovementMultiplier => _movementMultiplier;
    public float[] EffectiveStrength => _effectiveStrength;
    public float[] EffectiveConstitution => _effectiveConstitution;
    public float[] EffectiveDexterity => _effectiveDexterity;
    public UnitMetabolicCondition[] Condition => _condition;

    public UnitMetabolismStore(int initialCapacity = 1024)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _strength = new byte[initialCapacity];
        _constitution = new byte[initialCapacity];
        _dexterity = new byte[initialCapacity];
        _intelligence = new byte[initialCapacity];
        _bodyMassKg = new float[initialCapacity];
        _energyKcal = new float[initialCapacity];
        _maxEnergyKcal = new float[initialCapacity];
        _waterLiters = new float[initialCapacity];
        _maxWaterLiters = new float[initialCapacity];
        _muscleMassKg = new float[initialCapacity];
        _bodyFatKg = new float[initialCapacity];
        _bloodLiters = new float[initialCapacity];
        _maxBloodLiters = new float[initialCapacity];
        _fatigue = new float[initialCapacity];
        _stress = new float[initialCapacity];
        _proteinReserveGrams = new float[initialCapacity];
        _carbohydrateReserveGrams = new float[initialCapacity];
        _fatReserveGrams = new float[initialCapacity];
        _micronutrientReserve = new float[initialCapacity];
        _stomachKcal = new float[initialCapacity];
        _gutKcal = new float[initialCapacity];
        _stomachProtein = new float[initialCapacity];
        _gutProtein = new float[initialCapacity];
        _stomachCarbohydrate = new float[initialCapacity];
        _gutCarbohydrate = new float[initialCapacity];
        _stomachFat = new float[initialCapacity];
        _gutFat = new float[initialCapacity];
        _stomachWater = new float[initialCapacity];
        _gutWater = new float[initialCapacity];
        _stomachMicronutrients = new float[initialCapacity];
        _gutMicronutrients = new float[initialCapacity];
        _movementMultiplier = new float[initialCapacity];
        _effectiveStrength = new float[initialCapacity];
        _effectiveConstitution = new float[initialCapacity];
        _effectiveDexterity = new float[initialCapacity];
        _condition = new UnitMetabolicCondition[initialCapacity];
    }

    public void InitializeUnit(int index, UnitType type) =>
        InitializeUnit(index, UnitAnatomyCatalog.Get(type).Definition);

    public void InitializeUnit(int index, in UnitDefinition definition)
    {
        EnsureCapacity(index + 1);

        float mass = definition.BodyMassKg;
        float muscle = definition.MuscleMassKg;
        float fat = definition.BodyFatKg;
        float blood = definition.BloodLiters;
        float energy = definition.BaseEnergyKcal;
        float water = definition.BaseWaterLiters;
        byte strength = definition.Strength;
        byte constitution = definition.Constitution;
        byte dexterity = definition.Dexterity;
        byte intelligence = definition.Intelligence;

        // Stable per-slot variation gives colonists different baselines without RNG allocations.
        uint seed = unchecked((uint)index * 747796405u + 2891336453u);
        _strength[index] = (byte)Math.Clamp((int)strength + (int)(seed % 3u) - 1, 1, 8);
        seed = unchecked(seed * 2246822519u + 3266489917u);
        _constitution[index] = (byte)Math.Clamp((int)constitution + (int)(seed % 3u) - 1, 1, 8);
        seed = unchecked(seed * 2246822519u + 3266489917u);
        _dexterity[index] = (byte)Math.Clamp((int)dexterity + (int)(seed % 3u) - 1, 1, 8);
        seed = unchecked(seed * 2246822519u + 3266489917u);
        _intelligence[index] = (byte)Math.Clamp((int)intelligence + (int)(seed % 3u) - 1, 1, 8);
        mass *= 0.96f + ((seed >> 8) % 9u) * 0.01f;
        muscle *= 0.9f + ((seed >> 16) % 21u) * 0.01f;
        fat *= 0.85f + ((seed >> 24) % 31u) * 0.01f;
        _bodyMassKg[index] = mass;
        _maxEnergyKcal[index] = energy * 1.55f;
        _energyKcal[index] = energy;
        _maxWaterLiters[index] = water * 1.45f;
        _waterLiters[index] = water;
        _muscleMassKg[index] = muscle;
        _bodyFatKg[index] = fat;
        _maxBloodLiters[index] = blood;
        _bloodLiters[index] = blood;
        _fatigue[index] = 0f;
        _stress[index] = 0f;
        _proteinReserveGrams[index] = 120f;
        _carbohydrateReserveGrams[index] = 250f;
        _fatReserveGrams[index] = 450f;
        _micronutrientReserve[index] = 100f;
        _stomachKcal[index] = 0f;
        _gutKcal[index] = 0f;
        _stomachProtein[index] = 0f;
        _gutProtein[index] = 0f;
        _stomachCarbohydrate[index] = 0f;
        _gutCarbohydrate[index] = 0f;
        _stomachFat[index] = 0f;
        _gutFat[index] = 0f;
        _stomachWater[index] = 0f;
        _gutWater[index] = 0f;
        _stomachMicronutrients[index] = 0f;
        _gutMicronutrients[index] = 0f;
        _movementMultiplier[index] = 1f;
        _effectiveStrength[index] = strength;
        _effectiveConstitution[index] = constitution;
        _effectiveDexterity[index] = dexterity;
        _condition[index] = UnitMetabolicCondition.Normal;

    }

    public void ClearUnit(int index)
    {
        if ((uint)index >= (uint)Capacity)
            return;

        _strength[index] = 0;
        _constitution[index] = 0;
        _dexterity[index] = 0;
        _intelligence[index] = 0;
        _bodyMassKg[index] = 0f;
        _energyKcal[index] = 0f;
        _maxEnergyKcal[index] = 0f;
        _waterLiters[index] = 0f;
        _maxWaterLiters[index] = 0f;
        _muscleMassKg[index] = 0f;
        _bodyFatKg[index] = 0f;
        _bloodLiters[index] = 0f;
        _maxBloodLiters[index] = 0f;
        _fatigue[index] = 0f;
        _stress[index] = 0f;
        _proteinReserveGrams[index] = 0f;
        _carbohydrateReserveGrams[index] = 0f;
        _fatReserveGrams[index] = 0f;
        _micronutrientReserve[index] = 0f;
        _stomachKcal[index] = 0f;
        _gutKcal[index] = 0f;
        _stomachProtein[index] = 0f;
        _gutProtein[index] = 0f;
        _stomachCarbohydrate[index] = 0f;
        _gutCarbohydrate[index] = 0f;
        _stomachFat[index] = 0f;
        _gutFat[index] = 0f;
        _stomachWater[index] = 0f;
        _gutWater[index] = 0f;
        _stomachMicronutrients[index] = 0f;
        _gutMicronutrients[index] = 0f;
        _movementMultiplier[index] = 1f;
        _effectiveStrength[index] = 0f;
        _effectiveConstitution[index] = 0f;
        _effectiveDexterity[index] = 0f;
        _condition[index] = UnitMetabolicCondition.Normal;
    }

    /// <summary>Returns the fraction of the food accepted by the stomach.</summary>
    public float AddFood(
        int index,
        float caloriesKcal,
        float proteinGrams,
        float carbohydrateGrams,
        float fatGrams,
        float micronutrientUnits = 12f)
    {
        if ((uint)index >= (uint)Capacity ||
            caloriesKcal <= 0f || !float.IsFinite(caloriesKcal))
        {
            return 0f;
        }

        float accepted = Math.Clamp(
            (StomachCapacityKcal - _stomachKcal[index]) / caloriesKcal,
            0f,
            1f);
        float calories = caloriesKcal * accepted;
        if (calories <= 0f)
            return 0f;

        _stomachKcal[index] += calories;
        _stomachProtein[index] += MathF.Max(0f, proteinGrams) * accepted;
        _stomachCarbohydrate[index] += MathF.Max(0f, carbohydrateGrams) * accepted;
        _stomachFat[index] += MathF.Max(0f, fatGrams) * accepted;
        _stomachMicronutrients[index] += MathF.Max(0f, micronutrientUnits) * accepted;
        return accepted;
    }

    /// <summary>Returns the amount of water accepted into the stomach, in liters.</summary>
    public float AddWater(int index, float liters)
    {
        if ((uint)index >= (uint)Capacity ||
            liters <= 0f || !float.IsFinite(liters))
        {
            return 0f;
        }

        float accepted = MathF.Min(
            liters,
            MathF.Max(0f, StomachCapacityLiters - _stomachWater[index]));
        _stomachWater[index] += accepted;
        return accepted;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= Capacity)
            return;

        int capacity = Math.Max(Capacity * 2, required);
        Array.Resize(ref _strength, capacity);
        Array.Resize(ref _constitution, capacity);
        Array.Resize(ref _dexterity, capacity);
        Array.Resize(ref _intelligence, capacity);
        Array.Resize(ref _bodyMassKg, capacity);
        Array.Resize(ref _energyKcal, capacity);
        Array.Resize(ref _maxEnergyKcal, capacity);
        Array.Resize(ref _waterLiters, capacity);
        Array.Resize(ref _maxWaterLiters, capacity);
        Array.Resize(ref _muscleMassKg, capacity);
        Array.Resize(ref _bodyFatKg, capacity);
        Array.Resize(ref _bloodLiters, capacity);
        Array.Resize(ref _maxBloodLiters, capacity);
        Array.Resize(ref _fatigue, capacity);
        Array.Resize(ref _stress, capacity);
        Array.Resize(ref _proteinReserveGrams, capacity);
        Array.Resize(ref _carbohydrateReserveGrams, capacity);
        Array.Resize(ref _fatReserveGrams, capacity);
        Array.Resize(ref _micronutrientReserve, capacity);
        Array.Resize(ref _stomachKcal, capacity);
        Array.Resize(ref _gutKcal, capacity);
        Array.Resize(ref _stomachProtein, capacity);
        Array.Resize(ref _gutProtein, capacity);
        Array.Resize(ref _stomachCarbohydrate, capacity);
        Array.Resize(ref _gutCarbohydrate, capacity);
        Array.Resize(ref _stomachFat, capacity);
        Array.Resize(ref _gutFat, capacity);
        Array.Resize(ref _stomachWater, capacity);
        Array.Resize(ref _gutWater, capacity);
        Array.Resize(ref _stomachMicronutrients, capacity);
        Array.Resize(ref _gutMicronutrients, capacity);
        Array.Resize(ref _movementMultiplier, capacity);
        Array.Resize(ref _effectiveStrength, capacity);
        Array.Resize(ref _effectiveConstitution, capacity);
        Array.Resize(ref _effectiveDexterity, capacity);
        Array.Resize(ref _condition, capacity);
    }
}
