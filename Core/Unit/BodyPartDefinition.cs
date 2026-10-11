using System.Numerics;

namespace Core.Unit;

public enum BodyPartAnchor : byte
{
    Parent,
    UnitBody,
    UnitHead
}

/// <summary>Where a part's transform origin sits along its local +X axis.</summary>
public enum BodyPartPivot : byte
{
    Center,
    Start
}

/// <summary>
/// Geometry and attachment data for one renderable anatomical part.
/// LocalPosition is relative to the unit or parent transform. Scale.X/Y are
/// rendered half-extents. Start-pivot parts are positioned from their proximal
/// joint; their render center is offset by Scale.X along the part's own axis.
/// Parents must precede their children in a definition.
/// </summary>
public readonly struct BodyPartDefinition
{
    public Vector3 LocalPosition { get; }
    public Vector3 Scale { get; }
    public float LocalRotation { get; }
    public float FollowDistance { get; }
    public int ParentIndex { get; }
    public BodyPrimitive Primitive { get; }
    public BodyPartMotion Motion { get; }
    public BodyPartAnchor Anchor { get; }
    public BodyPartRole Role { get; }
    public BodyPartPivot Pivot { get; }
    public UnitHealthPartId DamagePart { get; }

    public BodyPartDefinition(
        Vector3 localPosition,
        Vector3 scale,
        BodyPrimitive primitive,
        int parentIndex = -1,
        float localRotation = 0f,
        BodyPartMotion motion = BodyPartMotion.StaticRelative,
        float followDistance = 0f,
        BodyPartAnchor anchor = BodyPartAnchor.Parent,
        BodyPartRole role = BodyPartRole.Generic,
        BodyPartPivot pivot = BodyPartPivot.Center,
        UnitHealthPartId damagePart = UnitHealthPartId.None)
    {
        LocalPosition = localPosition;
        Scale = scale;
        Primitive = primitive;
        ParentIndex = parentIndex;
        LocalRotation = localRotation;
        Motion = motion;
        FollowDistance = followDistance;
        Anchor = anchor;
        Role = role;
        Pivot = pivot;
        DamagePart = damagePart;
    }
}
