using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Data-only description of one visible body part.
/// Scale contains half-extents for boxes and radii for circles/ellipses.
/// ParentIndex is -1 for a part attached to the unit root, or an index
/// of an earlier part in the same definition.
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

    public BodyPartDefinition(
        Vector3 localPosition,
        Vector3 scale,
        BodyPrimitive primitive,
        int parentIndex = -1,
        float localRotation = 0f,
        BodyPartMotion motion = BodyPartMotion.StaticRelative,
        float followDistance = 0f)
    {
        LocalPosition = localPosition;
        Scale = scale;
        Primitive = primitive;
        ParentIndex = parentIndex;
        LocalRotation = localRotation;
        Motion = motion;
        FollowDistance = followDistance;
    }
}
