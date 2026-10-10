using System.Numerics;

namespace Core.Unit;

public enum BodyPartAnchor : byte
{
    Parent,
    UnitBody,
    UnitHead
}

/// <summary>
/// Data-only description of one body part. Scale stores half-extents
/// for boxes and radii for circles. Parents must appear earlier in the
/// definition. Anchors make body/head orientation choices explicit.
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

    public BodyPartDefinition(
        Vector3 localPosition,
        Vector3 scale,
        BodyPrimitive primitive,
        int parentIndex = -1,
        float localRotation = 0f,
        BodyPartMotion motion = BodyPartMotion.StaticRelative,
        float followDistance = 0f,
        BodyPartAnchor anchor = BodyPartAnchor.Parent)
    {
        LocalPosition = localPosition;
        Scale = scale;
        Primitive = primitive;
        ParentIndex = parentIndex;
        LocalRotation = localRotation;
        Motion = motion;
        FollowDistance = followDistance;
        Anchor = anchor;
    }
}
