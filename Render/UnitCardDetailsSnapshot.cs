using System;
using Core.Items;
using Core.Unit;

namespace RimClone.Render;

public readonly record struct RenderHealthPart(
    UnitHealthPartId Part,
    UnitHealthPartKind Kind,
    float HitPoints,
    float MaxHitPoints,
    float BleedRate);

public readonly record struct RenderMetabolismState(
    byte Strength,
    byte Constitution,
    byte Dexterity,
    byte Intelligence,
    float EffectiveStrength,
    float EffectiveConstitution,
    float EffectiveDexterity,
    float EnergyKcal,
    float MaxEnergyKcal,
    float WaterLiters,
    float MaxWaterLiters,
    float ProteinReserveGrams,
    float CarbohydrateReserveGrams,
    float FatReserveGrams,
    float MicronutrientReserve,
    float StomachKcal,
    float GutKcal,
    float StomachWaterLiters,
    float GutWaterLiters,
    float BodyMassKg,
    float MuscleMassKg,
    float BodyFatKg,
    float BloodLiters,
    float MaxBloodLiters,
    float Fatigue,
    float Stress,
    float MovementMultiplier,
    UnitMetabolicCondition Condition);

public readonly record struct RenderInventoryItem(
    ItemConfig Config,
    float Durability,
    float MaxDurability,
    bool Equipped);

public readonly record struct RenderPawnSkill(
    UnitSkill Skill,
    byte Level,
    UnitSkillPassion Passion,
    float Experience,
    float ExperienceToNextLevel);

public sealed class UnitCardDetailsSnapshot
{
    public UnitId Unit { get; init; }
    public RenderHealthPart[] HealthParts { get; init; } = Array.Empty<RenderHealthPart>();
    public AnatomyCardEntry[] AnatomyEntries { get; init; } = Array.Empty<AnatomyCardEntry>();
    public RenderInventoryItem[] Inventory { get; init; } = Array.Empty<RenderInventoryItem>();
    public RenderPawnSkill[] PawnSkills { get; init; } = Array.Empty<RenderPawnSkill>();
    public float SuppressionResistance { get; init; }
    public RenderMetabolismState Metabolism { get; init; }

    public static UnitCardDetailsSnapshot Capture(UnitSimulation simulation, UnitId id)
    {
        UnitStore units = simulation.Units;
        if (!units.TryGetIndex(id, out int unitIndex))
            return new UnitCardDetailsSnapshot { Unit = id };

        UnitHealthStore health = simulation.Health;
        int healthStart = unitIndex * health.MaxParts;
        int healthCount = health.PartCount[unitIndex];
        UnitHealthPartId[] parts = health.Part;
        UnitHealthPartKind[] kinds = health.Kind;
        float[] hitPoints = health.HitPoints;
        float[] maxHitPoints = health.MaxHitPoints;
        float[] bleedRates = health.BleedRate;
        bool[] present = health.Present;

        int visibleHealthCount = 0;
        for (int i = 0; i < healthCount; i++)
        {
            if (present[healthStart + i])
                visibleHealthCount++;
        }

        RenderHealthPart[] healthParts = new RenderHealthPart[visibleHealthCount];
        AnatomyCardEntry[] anatomyEntries = simulation.HitRegions.GetProfile(unitIndex) is UnitAnatomyProfile anatomyProfile
            ? AnatomyCardEntryFactory.Capture(anatomyProfile, health, unitIndex)
            : Array.Empty<AnatomyCardEntry>();
        int healthWrite = 0;
        for (int i = 0; i < healthCount; i++)
        {
            int source = healthStart + i;
            if (!present[source])
                continue;

            healthParts[healthWrite++] = new RenderHealthPart(
                parts[source],
                kinds[source],
                hitPoints[source],
                maxHitPoints[source],
                bleedRates[source]);
        }

        UnitInventoryStore inventory = simulation.Inventory;
        int inventoryStart = unitIndex * UnitInventoryStore.MaxInventorySlots;
        bool[] occupied = inventory.Occupied;
        ItemConfig[] items = inventory.Items;
        int itemCount = 0;

        for (int slot = 0; slot < UnitInventoryStore.MaxInventorySlots; slot++)
        {
            if (occupied[inventoryStart + slot] && items[inventoryStart + slot] != null)
                itemCount++;
        }

        RenderInventoryItem[] inventoryItems = new RenderInventoryItem[itemCount];
        float[] durability = inventory.Durability;
        float[] maxDurability = inventory.MaxDurability;
        int itemWrite = 0;

        for (int slot = 0; slot < UnitInventoryStore.MaxInventorySlots; slot++)
        {
            int itemIndex = inventoryStart + slot;
            if (!occupied[itemIndex] || items[itemIndex] == null)
                continue;

            bool equipped = false;
            for (int weaponSlot = 0; weaponSlot < UnitInventoryStore.WeaponSlotCount; weaponSlot++)
            {
                if (inventory.GetWeaponEquipment(unitIndex, (UnitWeaponSlot)weaponSlot) == slot)
                {
                    equipped = true;
                    break;
                }
            }

            if (!equipped)
            {
                for (int zone = 0; zone < UnitInventoryStore.BodyZoneCount && !equipped; zone++)
                {
                    for (int layer = 0; layer < UnitInventoryStore.ArmorLayerCount; layer++)
                    {
                        if (inventory.GetBodyEquipment(unitIndex, zone, (ArmorLayer)layer) == slot)
                        {
                            equipped = true;
                            break;
                        }
                    }
                }
            }

            inventoryItems[itemWrite++] = new RenderInventoryItem(
                items[itemIndex],
                durability[itemIndex],
                maxDurability[itemIndex],
                equipped);
        }

        UnitSkillStore skills = simulation.Skills;
        RenderPawnSkill[] pawnSkills = new RenderPawnSkill[UnitSkillStore.SkillCount];
        for (int i = 0; i < pawnSkills.Length; i++)
        {
            UnitSkill skill = (UnitSkill)i;
            pawnSkills[i] = new RenderPawnSkill(
                skill,
                skills.GetLevel(unitIndex, skill),
                skills.GetPassion(unitIndex, skill),
                skills.GetExperience(unitIndex, skill),
                skills.GetExperienceToNextLevel(unitIndex, skill));
        }

        UnitMetabolismStore metabolism = simulation.Metabolism;
        RenderMetabolismState metabolicState = new(
            metabolism.Strength[unitIndex],
            metabolism.Constitution[unitIndex],
            metabolism.Dexterity[unitIndex],
            metabolism.Intelligence[unitIndex],
            metabolism.EffectiveStrength[unitIndex],
            metabolism.EffectiveConstitution[unitIndex],
            metabolism.EffectiveDexterity[unitIndex],
            metabolism.EnergyKcal[unitIndex],
            metabolism.MaxEnergyKcal[unitIndex],
            metabolism.WaterLiters[unitIndex],
            metabolism.MaxWaterLiters[unitIndex],
            metabolism.ProteinReserveGrams[unitIndex],
            metabolism.CarbohydrateReserveGrams[unitIndex],
            metabolism.FatReserveGrams[unitIndex],
            metabolism.MicronutrientReserve[unitIndex],
            metabolism.StomachKcal[unitIndex],
            metabolism.GutKcal[unitIndex],
            metabolism.StomachWater[unitIndex],
            metabolism.GutWater[unitIndex],
            metabolism.BodyMassKg[unitIndex],
            metabolism.MuscleMassKg[unitIndex],
            metabolism.BodyFatKg[unitIndex],
            metabolism.BloodLiters[unitIndex],
            metabolism.MaxBloodLiters[unitIndex],
            metabolism.Fatigue[unitIndex],
            metabolism.Stress[unitIndex],
            metabolism.MovementMultiplier[unitIndex],
            metabolism.Condition[unitIndex]);

        return new UnitCardDetailsSnapshot
        {
            Unit = id,
            HealthParts = healthParts,
            AnatomyEntries = anatomyEntries,
            Inventory = inventoryItems,
            PawnSkills = pawnSkills,
            SuppressionResistance = skills.GetSuppressionResistance(unitIndex),
            Metabolism = metabolicState
        };
    }
}
