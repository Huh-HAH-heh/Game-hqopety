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

/// <summary>Compatibility access to archetype stats in UnitAnatomyCatalog.</summary>
public static class UnitCatalog
{
    public static UnitDefinition Get(UnitType type) =>
        UnitAnatomyCatalog.Get(type).Definition;
}
