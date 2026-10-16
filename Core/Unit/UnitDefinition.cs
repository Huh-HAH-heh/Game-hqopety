using System;

namespace Core.Unit;

public readonly struct UnitDefinition
{
    public UnitType Type { get; }
    public UnitBodyType BodyType { get; }
    public float MoveSpeed { get; }
    public float Width { get; }
    public float Length { get; }
    public float Height { get; }
    public float ViewRange { get; }
    public float FieldOfView { get; }

    public float BodyMassKg { get; }
    public float MuscleMassKg { get; }
    public float BodyFatKg { get; }
    public float BloodLiters { get; }
    public float BaseEnergyKcal { get; }
    public float BaseWaterLiters { get; }
    public byte Strength { get; }
    public byte Constitution { get; }
    public byte Dexterity { get; }
    public byte Intelligence { get; }

    public UnitDefinition(
        UnitType type,
        UnitBodyType bodyType,
        float moveSpeed,
        float width,
        float length,
        float height,
        float viewRange,
        float fieldOfView,
        float? bodyMassKg = null,
        float? muscleMassKg = null,
        float? bodyFatKg = null,
        float? bloodLiters = null,
        float? baseEnergyKcal = null,
        float? baseWaterLiters = null,
        byte? strength = null,
        byte? constitution = null,
        byte? dexterity = null,
        byte? intelligence = null)
    {
        Type = type;
        BodyType = bodyType;
        MoveSpeed = moveSpeed;
        Width = width;
        Length = length;
        Height = height;
        ViewRange = viewRange;
        FieldOfView = fieldOfView;

        BodyMassKg = bodyMassKg ?? DefaultBodyMassKg(type);
        MuscleMassKg = muscleMassKg ?? DefaultMuscleMassKg(type);
        BodyFatKg = bodyFatKg ?? DefaultBodyFatKg(type);
        BloodLiters = bloodLiters ?? DefaultBloodLiters(type);
        BaseEnergyKcal = baseEnergyKcal ?? DefaultEnergyKcal(type);
        BaseWaterLiters = baseWaterLiters ?? DefaultWaterLiters(type);
        Strength = strength ?? DefaultStrength(type);
        Constitution = constitution ?? DefaultConstitution(type);
        Dexterity = dexterity ?? DefaultDexterity(type);
        Intelligence = intelligence ?? DefaultIntelligence(type);
    }

    private static float DefaultBodyMassKg(UnitType type) => type switch
    {
        UnitType.Greenbob => 18f,
        UnitType.SegmentedMonster => 120f,
        _ => 75f
    };

    private static float DefaultMuscleMassKg(UnitType type) => type switch
    {
        UnitType.Greenbob => 5.5f,
        UnitType.SegmentedMonster => 42f,
        _ => 30f
    };

    private static float DefaultBodyFatKg(UnitType type) => type switch
    {
        UnitType.Greenbob => 2.2f,
        UnitType.SegmentedMonster => 18f,
        _ => 13f
    };

    private static float DefaultBloodLiters(UnitType type) => type switch
    {
        UnitType.Greenbob => 1.1f,
        UnitType.SegmentedMonster => 7f,
        _ => 5f
    };

    private static float DefaultEnergyKcal(UnitType type) => type switch
    {
        UnitType.Greenbob => 650f,
        UnitType.SegmentedMonster => 2600f,
        _ => 2200f
    };

    private static float DefaultWaterLiters(UnitType type) => type switch
    {
        UnitType.Greenbob => 0.55f,
        UnitType.SegmentedMonster => 4.1f,
        _ => 2.5f
    };

    private static byte DefaultStrength(UnitType type) => type switch
    {
        UnitType.Greenbob => 3,
        UnitType.SegmentedMonster => 7,
        _ => 4
    };

    private static byte DefaultConstitution(UnitType type) => type switch
    {
        UnitType.Greenbob => 5,
        UnitType.SegmentedMonster => 6,
        _ => 4
    };

    private static byte DefaultDexterity(UnitType type) => type switch
    {
        UnitType.Greenbob => 5,
        UnitType.SegmentedMonster => 2,
        _ => 4
    };

    private static byte DefaultIntelligence(UnitType type) => type switch
    {
        UnitType.Greenbob => 2,
        UnitType.SegmentedMonster => 1,
        _ => 4
    };
}

/// <summary>Compatibility access to archetype stats in UnitAnatomyCatalog.</summary>
public static class UnitCatalog
{
    public static UnitDefinition Get(UnitType type) =>
        UnitAnatomyCatalog.Get(type).Definition;
}
