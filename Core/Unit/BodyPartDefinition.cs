using System.Numerics;

namespace Core.Unit;

public enum BodyPartAnchor : byte
{
    Parent,
    UnitBody,
    UnitHead
}

/// <summary>
/// Geometry and attachment data for one renderable anatomical part.
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

    public BodyPartDefinition(
        Vector3 localPosition,
        Vector3 scale,
        BodyPrimitive primitive,
        int parentIndex = -1,
        float localRotation = 0f,
        BodyPartMotion motion = BodyPartMotion.StaticRelative,
        float followDistance = 0f,
        BodyPartAnchor anchor = BodyPartAnchor.Parent,
        BodyPartRole role = BodyPartRole.Generic)
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
    }
}
