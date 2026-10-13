using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// Authoring API for one independently addressable anatomical circle.
/// Geometry is unit-relative; tissue attachments retain separate health identities,
/// even when they share the same legacy UnitHealthPartId.
/// </summary>
public readonly struct UnitAnatomyNode
{
    private readonly UnitCircleHitRegion _geometry;

    public string Id => _geometry.Id;
    public string ParentId => _geometry.ParentId;
    public UnitCircleHitRegion Geometry => _geometry;

    private UnitAnatomyNode(UnitCircleHitRegion geometry)
    {
        _geometry = geometry;
    }

    public static UnitAnatomyNode Circle(
        string id,
        Vector3 localCenter,
        float radius,
        float halfHeight,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers) =>
        Circle(id, string.Empty, localCenter, radius, halfHeight, tissues, markers);

    public static UnitAnatomyNode Circle(
        string id,
        string parentId,
        Vector3 localCenter,
        float radius,
        float halfHeight,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers) =>
        new(new UnitCircleHitRegion(id, parentId, localCenter, radius, halfHeight, tissues, markers));

    public static UnitAnatomyNode RelativeCircle(
        string id,
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers) =>
        RelativeCircle(
            id,
            string.Empty,
            centerFactors,
            widthRadiusFactor,
            lengthRadiusFactor,
            heightHalfExtentFactor,
            tissues,
            markers);

    public static UnitAnatomyNode RelativeCircle(
        string id,
        string parentId,
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers) =>
        new(UnitCircleHitRegion.RelativeToUnit(
            id,
            parentId,
            centerFactors,
            widthRadiusFactor,
            lengthRadiusFactor,
            heightHalfExtentFactor,
            tissues,
            markers));

    public static implicit operator UnitCircleHitRegion(UnitAnatomyNode node) =>
        node._geometry;
}

/// <summary>
/// Data-only anatomy profile. Vision, aiming, hit detection, health and damage
/// consume the same immutable node profile.
/// </summary>
public sealed class UnitAnatomyProfile : UnitHitRegionProfile
{
    public UnitAnatomyProfile(params UnitAnatomyNode[] nodes)
        : base(Convert(nodes))
    {
    }

    private static UnitCircleHitRegion[] Convert(UnitAnatomyNode[] nodes)
    {
        if (nodes == null)
            throw new ArgumentNullException(nameof(nodes));

        if (nodes.Length == 0)
            throw new ArgumentException("An anatomy profile requires at least one node.", nameof(nodes));

        var geometry = new UnitCircleHitRegion[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            geometry[i] = nodes[i].Geometry;

        return geometry;
    }
}
