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

    public UnitDefinition(
        UnitType type,
        UnitBodyType bodyType,
        float moveSpeed,
        float width,
        float length,
        float height,
        float viewRange,
        float fieldOfView)
    {
        Type = type;
        BodyType = bodyType;
        MoveSpeed = moveSpeed;
        Width = width;
        Length = length;
        Height = height;
        ViewRange = viewRange;
        FieldOfView = fieldOfView;
    }
}

public static class UnitCatalog
{
    public static UnitDefinition Get(
        UnitType type)
    {
        return type switch
        {
            UnitType.Colonist =>
                new UnitDefinition(
                    UnitType.Colonist,
                    UnitBodyType.Colonist,
                    moveSpeed: 2.8f,
                    width: 0.35f,
                    length: 0.60f,
                    height: 1.75f,
                    viewRange: 20f,
                    fieldOfView: 110f),

            UnitType.Greenbob =>
                new UnitDefinition(
                    UnitType.Greenbob,
                    UnitBodyType.SmallCreature,
                    moveSpeed: 3.2f,
                    width: 0.40f,
                    length: 0.40f,
                    height: 0.40f,
                    viewRange: 16f,
                    fieldOfView: 120f),

            UnitType.SegmentedMonster =>
                new UnitDefinition(
                    UnitType.SegmentedMonster,
                    UnitBodyType.SegmentedCreature,
                    moveSpeed: 1.4f,
                    width: 1.0f,
                    length: 6.0f,
                    height: 0.45f,
                    viewRange: 28f,
                    fieldOfView: 140f),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(type),
                    type,
                    "Неизвестный тип Unit.")
        };
    }
}
