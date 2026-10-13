using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// One anatomical node. The same shape transform feeds the body rig and the
/// hit/aim/vision queries; tissues and markers are attached to that shape.
/// </summary>
public readonly struct UnitAnatomyNode
{
    private readonly UnitCircleHitRegion _geometry;
    private readonly BodyPartDefinition _shape;
    private readonly string _id;
    private readonly string _parentId;
    private readonly float _hitHalfHeight;
    private readonly bool _hasShape;

    public string Id => _id;
    public string ParentId => _parentId;
    public UnitCircleHitRegion Geometry => _geometry;
    public BodyPartDefinition Shape => _shape;
    public float HitHalfHeight => _hitHalfHeight;
    public bool HasShape => _hasShape;

    private UnitAnatomyNode(
        string id,
        string parentId,
        UnitCircleHitRegion geometry,
        BodyPartDefinition shape,
        float hitHalfHeight,
        bool hasShape)
    {
        _id = id;
        _parentId = parentId;
        _geometry = geometry;
        _shape = shape;
        _hitHalfHeight = hitHalfHeight;
        _hasShape = hasShape;
    }

    public static UnitAnatomyNode Part(
        string id,
        string parentId,
        BodyPartDefinition shape,
        float hitHalfHeight,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An anatomy node requires a stable ID.", nameof(id));
        if (!float.IsFinite(hitHalfHeight) || hitHalfHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(hitHalfHeight));
        if (tissues == null || tissues.Length == 0)
            throw new ArgumentException("An anatomy node requires at least one tissue.", nameof(tissues));

        float radius = MathF.Max(MathF.Abs(shape.Scale.X), MathF.Abs(shape.Scale.Y));
        string geometryParent = shape.Anchor == BodyPartAnchor.UnitHead
            ? string.Empty
            : parentId ?? string.Empty;

        var geometry = new UnitCircleHitRegion(
            id, geometryParent, shape.LocalPosition, radius, hitHalfHeight, tissues, markers);

        return new UnitAnatomyNode(
            id, parentId ?? string.Empty, geometry, shape, hitHalfHeight, true);
    }

    // Compatibility factories for callers that still author collision-only nodes.
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
        params UnitAnatomyMarker[] markers)
    {
        var geometry = new UnitCircleHitRegion(
            id, parentId, localCenter, radius, halfHeight, tissues, markers);
        return new UnitAnatomyNode(
            id, parentId ?? string.Empty, geometry, default, halfHeight, false);
    }

    public static UnitAnatomyNode RelativeCircle(
        string id,
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers) =>
        RelativeCircle(
            id, string.Empty, centerFactors, widthRadiusFactor,
            lengthRadiusFactor, heightHalfExtentFactor, tissues, markers);

    public static UnitAnatomyNode RelativeCircle(
        string id,
        string parentId,
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers)
    {
        var geometry = UnitCircleHitRegion.RelativeToUnit(
            id, parentId, centerFactors, widthRadiusFactor,
            lengthRadiusFactor, heightHalfExtentFactor, tissues, markers);
        return new UnitAnatomyNode(
            id, parentId ?? string.Empty, geometry, default,
            geometry.HalfHeight, false);
    }

    public static implicit operator UnitCircleHitRegion(UnitAnatomyNode node) =>
        node._geometry;
}

/// <summary>
/// Immutable anatomy used by rendering, health registration, aiming, vision,
/// and projectile collision. Canonical profiles keep node order identical to
/// the compact body-store block for O(1) transform lookup.
/// </summary>
public sealed class UnitAnatomyProfile : UnitHitRegionProfile
{
    private readonly UnitAnatomyNode[] _nodes;
    private readonly BodyPartDefinition[] _bodyParts;
    private readonly int[] _visibleRegions;

    public int NodeCount => _nodes.Length;
    public int VisibleRegionCount => _visibleRegions.Length;
    public bool HasSharedShapes { get; }
    public ReadOnlySpan<BodyPartDefinition> BodyParts => _bodyParts;
    public ReadOnlySpan<UnitAnatomyNode> Nodes => _nodes;

    public UnitAnatomyProfile(params UnitAnatomyNode[] nodes)
        : this(nodes, Convert(nodes))
    {
    }

    public UnitAnatomyNode GetNode(int index) =>
        (uint)index < (uint)_nodes.Length
            ? _nodes[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    public int GetVisibleRegionIndex(int visibleIndex) =>
        (uint)visibleIndex < (uint)_visibleRegions.Length
            ? _visibleRegions[visibleIndex]
            : throw new ArgumentOutOfRangeException(nameof(visibleIndex));

    public float GetHitHalfHeight(int index)
    {
        if ((uint)index >= (uint)_nodes.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _nodes[index].HasShape
            ? _nodes[index].HitHalfHeight
            : MathF.Max(0.001f, _nodes[index].Geometry.HalfHeight);
    }

    public bool IsInternalNode(int index) =>
        (uint)index < (uint)_nodes.Length &&
        _nodes[index].HasShape &&
        _nodes[index].Shape.Role == BodyPartRole.Internal;

    private UnitAnatomyProfile(UnitAnatomyNode[] nodes, UnitCircleHitRegion[] regions)
        : base(regions)
    {
        _nodes = (UnitAnatomyNode[])nodes.Clone();
        bool sharedShapes = nodes.Length > 0;
        for (int i = 0; i < nodes.Length; i++)
            sharedShapes &= nodes[i].HasShape;

        HasSharedShapes = sharedShapes;
        _bodyParts = sharedShapes
            ? new BodyPartDefinition[nodes.Length]
            : Array.Empty<BodyPartDefinition>();

        int visibleCount = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].HasShape || nodes[i].Shape.Role != BodyPartRole.Internal)
                visibleCount++;
        }

        _visibleRegions = new int[visibleCount];
        int visibleIndex = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (!nodes[i].HasShape || nodes[i].Shape.Role != BodyPartRole.Internal)
                _visibleRegions[visibleIndex++] = i;
        }

        if (!sharedShapes)
            return;

        for (int i = 0; i < nodes.Length; i++)
        {
            BodyPartDefinition shape = nodes[i].Shape;
            if (shape.ParentIndex < -1 || shape.ParentIndex >= i)
                throw new ArgumentException($"Parent index of anatomy node '{nodes[i].Id}' must precede the node.", nameof(nodes));

            _bodyParts[i] = shape;
        }
    }

    private static UnitCircleHitRegion[] Convert(UnitAnatomyNode[] nodes)
    {
        if (nodes == null)
            throw new ArgumentNullException(nameof(nodes));
        if (nodes.Length == 0)
            throw new ArgumentException("An anatomy profile requires at least one node.", nameof(nodes));

        var regions = new UnitCircleHitRegion[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
            regions[i] = nodes[i].Geometry;

        return regions;
    }
}
