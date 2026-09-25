using System;
using System.Numerics;

namespace Core.Unit;

public readonly struct UnitDefinition
{
    public UnitType Type { get; }
    public UnitBodyType BodyType { get; }

    public float MoveSpeed { get; }
    public float Radius { get; }

    public float ViewRange { get; }
    public float FieldOfView { get; }

    public UnitDefinition(
        UnitType type,
        UnitBodyType bodyType,
        float moveSpeed,
        float radius,
        float viewRange,
        float fieldOfView)
    {
        Type = type;
        BodyType = bodyType;
        MoveSpeed = moveSpeed;
        Radius = radius;
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
                    radius: 0.30f,
                    viewRange: 20f,
                    fieldOfView: 110f),

            UnitType.Greenbob =>
                new UnitDefinition(
                    UnitType.Greenbob,
                    UnitBodyType.SmallCreature,
                    moveSpeed: 3.2f,
                    radius: 0.24f,
                    viewRange: 16f,
                    fieldOfView: 120f),

            UnitType.SegmentedMonster =>
                new UnitDefinition(
                    UnitType.SegmentedMonster,
                    UnitBodyType.SegmentedCreature,
                    moveSpeed: 1.4f,
                    radius: 1.0f,
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
