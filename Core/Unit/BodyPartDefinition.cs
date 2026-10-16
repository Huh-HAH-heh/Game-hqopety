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
    /// <summary>Rotation around world/local X (pitch), in radians.</summary>
    public float LocalRotationX { get; }
    /// <summary>Rotation around world/local Y (tilt), in radians.</summary>
    public float LocalRotationY { get; }
    /// <summary>Rotation around world/local Z (top-down rotation), in radians.</summary>
    public float LocalRotation { get; }
    public Vector3 LocalEulerRotation => new(LocalRotationX, LocalRotationY, LocalRotation);
    public float FollowDistance { get; }
    public int ParentIndex { get; }
    public BodyPrimitive Primitive { get; }
    public BodyPartMotion Motion { get; }
    public BodyPartAnchor Anchor { get; }
    public BodyPartRole Role { get; }
    public BodyPartPivot Pivot { get; }
    public string? SymmetryPartnerId { get; }

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
        float localRotationX = 0f,
        float localRotationY = 0f,
        string? symmetryPartnerId = null)
    {
        LocalPosition = localPosition;
        Scale = scale;
        Primitive = primitive;
        ParentIndex = parentIndex;
        LocalRotationX = localRotationX;
        LocalRotationY = localRotationY;
        LocalRotation = localRotation;
        Motion = motion;
        FollowDistance = followDistance;
        Anchor = anchor;
        Role = role;
        Pivot = pivot;
        SymmetryPartnerId = symmetryPartnerId;
    }
}
