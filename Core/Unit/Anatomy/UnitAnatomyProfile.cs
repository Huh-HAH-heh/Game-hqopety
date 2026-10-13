using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// One authored anatomy node. Rendering shape, collision primitive, internal/external
/// classification, health tissues and functional markers live in this single record.
/// Runtime transforms are compiled to UnitBodyStore's flat arrays.
/// </summary>
public readonly struct UnitAnatomyNode
{
    private readonly string _id;
    private readonly string _parentId;
    private readonly BodyPartDefinition _shape;
    private readonly float _hitHalfHeight;
    private readonly HitRegionPartBinding[] _tissues;
    private readonly UnitAnatomyMarker[] _markers;
    private readonly bool _hasShape;

    // Only used to build compatibility views for older custom hit-profile callers.
    private readonly Vector3 _legacyCenterFactors;
    private readonly Vector3 _legacyLocalCenter;
    private readonly float _legacyRadius;
    private readonly float _legacyWidthRadiusFactor;
    private readonly float _legacyLengthRadiusFactor;
    private readonly float _legacyHeightHalfExtentFactor;

    public string Id => _id;
    public string ParentId => _parentId;
    public BodyPartDefinition Shape => _shape;
    public float HitHalfHeight => _hitHalfHeight;
    public bool HasShape => _hasShape;
    public ReadOnlySpan<HitRegionPartBinding> Tissues => _tissues;
    public ReadOnlySpan<UnitAnatomyMarker> Markers => _markers;

    private UnitAnatomyNode(
        string id,
        string parentId,
        BodyPartDefinition shape,
        float hitHalfHeight,
        HitRegionPartBinding[] tissues,
        UnitAnatomyMarker[] markers,
        bool hasShape,
        Vector3 legacyLocalCenter,
        Vector3 legacyCenterFactors,
        float legacyRadius,
        float legacyWidthRadiusFactor,
        float legacyLengthRadiusFactor,
        float legacyHeightHalfExtentFactor)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An anatomy node requires a stable ID.", nameof(id));
        if (!float.IsFinite(hitHalfHeight) || hitHalfHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(hitHalfHeight));
        if (tissues == null || tissues.Length == 0)
            throw new ArgumentException("An anatomy node requires at least one tissue.", nameof(tissues));
        if (markers == null)
            throw new ArgumentNullException(nameof(markers));

        for (int i = 0; i < markers.Length; i++)
        {
            for (int j = i + 1; j < markers.Length; j++)
            {
                if (string.Equals(markers[i].Id, markers[j].Id, StringComparison.Ordinal))
                    throw new ArgumentException("Marker IDs must be unique within an anatomy node.", nameof(markers));
            }
        }

        _id = id;
        _parentId = parentId ?? string.Empty;
        _shape = shape;
        _hitHalfHeight = hitHalfHeight;
        _tissues = (HitRegionPartBinding[])tissues.Clone();
        _markers = (UnitAnatomyMarker[])markers.Clone();
        _hasShape = hasShape;
        _legacyLocalCenter = legacyLocalCenter;
        _legacyCenterFactors = legacyCenterFactors;
        _legacyRadius = legacyRadius;
        _legacyWidthRadiusFactor = legacyWidthRadiusFactor;
        _legacyLengthRadiusFactor = legacyLengthRadiusFactor;
        _legacyHeightHalfExtentFactor = legacyHeightHalfExtentFactor;
    }

    public static UnitAnatomyNode Part(
        string id,
        string parentId,
        BodyPartDefinition shape,
        float hitHalfHeight,
        HitRegionPartBinding[] tissues,
        params UnitAnatomyMarker[] markers)
    {
        float radius = MathF.Max(MathF.Abs(shape.Scale.X), MathF.Abs(shape.Scale.Y));
        return new UnitAnatomyNode(
            id, parentId, shape, hitHalfHeight, tissues, markers, true,
            shape.LocalPosition, Vector3.Zero, radius, 0f, 0f, 0f);
    }

    // Compatibility factories for collision-only custom anatomy.
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
        new(
            id, parentId, default, halfHeight, tissues, markers, false,
            localCenter, Vector3.Zero, radius, 0f, 0f, 0f);

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
        params UnitAnatomyMarker[] markers) =>
        new(
            id, parentId, default, MathF.Max(0.001f, heightHalfExtentFactor),
            tissues, markers, false, Vector3.Zero, centerFactors, 0f,
            widthRadiusFactor, lengthRadiusFactor, heightHalfExtentFactor);

    public bool TryGetMarker(UnitAnatomyMarkerRole role, out UnitAnatomyMarker marker)
    {
        for (int i = 0; i < _markers.Length; i++)
        {
            if ((_markers[i].Roles & role) == role)
            {
                marker = _markers[i];
                return true;
            }
        }

        marker = default;
        return false;
    }

    public bool TryGetMarker(string id, out UnitAnatomyMarker marker)
    {
        for (int i = 0; i < _markers.Length; i++)
        {
            if (string.Equals(_markers[i].Id, id, StringComparison.Ordinal))
            {
                marker = _markers[i];
                return true;
            }
        }

        marker = default;
        return false;
    }

    internal UnitCircleHitRegion ToCompatibilityRegion()
    {
        if (_hasShape)
        {
            string parentId = _shape.Anchor == BodyPartAnchor.UnitHead
                ? string.Empty
                : _parentId;

            return new UnitCircleHitRegion(
                _id, parentId, _legacyLocalCenter, _legacyRadius,
                _hitHalfHeight, _tissues, _markers);
        }

        if (_legacyWidthRadiusFactor != 0f ||
            _legacyLengthRadiusFactor != 0f ||
            _legacyHeightHalfExtentFactor != 0f ||
            _legacyCenterFactors != Vector3.Zero)
        {
            return UnitCircleHitRegion.RelativeToUnit(
                _id, _parentId, _legacyCenterFactors,
                _legacyWidthRadiusFactor, _legacyLengthRadiusFactor,
                _legacyHeightHalfExtentFactor, _tissues, _markers);
        }

        return new UnitCircleHitRegion(
            _id, _parentId, _legacyLocalCenter, _legacyRadius,
            _hitHalfHeight, _tissues, _markers);
    }

    public static implicit operator UnitCircleHitRegion(UnitAnatomyNode node) =>
        node.ToCompatibilityRegion();
}

/// <summary>
/// Canonical immutable profile used by body rendering, vision and hit resolution.
/// The inherited regions are a legacy compatibility projection, not runtime source data.
/// </summary>
public sealed class UnitAnatomyProfile : UnitHitRegionProfile
{
    private readonly UnitAnatomyNode[] _nodes;
    private readonly BodyPartDefinition[] _bodyParts;
    private readonly int[] _visibleRegions;
    private readonly UnitAnatomyVolume[] _volumes;

    public override int RegionCount => _nodes.Length;
    public int NodeCount => _nodes.Length;
    public int VisibleRegionCount => _visibleRegions.Length;
    public bool HasSharedShapes { get; }
    public ReadOnlySpan<BodyPartDefinition> BodyParts => _bodyParts;
    public ReadOnlySpan<UnitAnatomyNode> Nodes => _nodes;
    public ReadOnlySpan<UnitAnatomyVolume> Volumes => _volumes;
    public int VolumeCount => _volumes.Length;

    public UnitAnatomyProfile(params UnitAnatomyNode[] nodes)
        : this(nodes, Convert(nodes), Array.Empty<UnitAnatomyVolume>())
    {
    }

    public UnitAnatomyProfile(UnitAnatomyVolume[] volumes, params UnitAnatomyNode[] nodes)
        : this(nodes, Convert(nodes), volumes)
    {
    }

    public static UnitAnatomyProfile FromBodyParts(ReadOnlySpan<BodyPartDefinition> bodyParts)
    {
        if (bodyParts.Length == 0)
            throw new ArgumentException("A custom anatomy requires at least one body part.", nameof(bodyParts));

        var nodes = new UnitAnatomyNode[bodyParts.Length];
        for (int i = 0; i < bodyParts.Length; i++)
        {
            BodyPartDefinition shape = bodyParts[i];
            string id = $"custom.part.{i}";
            string parentId = shape.ParentIndex >= 0
                ? $"custom.part.{shape.ParentIndex}"
                : string.Empty;
            string tissueId = $"{id}.tissue";
            string parentTissueId = shape.ParentIndex >= 0
                ? $"custom.part.{shape.ParentIndex}.tissue"
                : string.Empty;

            var tissue = new HitRegionPartBinding(
                UnitHealthPartId.WholeBody,
                kind: UnitHealthPartKind.BodyPart,
                maxHitPoints: 100f,
                id: tissueId,
                parentId: string.IsNullOrWhiteSpace(parentTissueId) ? null : parentTissueId);

            nodes[i] = UnitAnatomyNode.Part(
                id,
                parentId,
                shape,
                MathF.Max(0.001f, MathF.Abs(shape.Scale.Z)),
                new[] { tissue });
        }

        return new UnitAnatomyProfile(nodes);
    }

    public UnitAnatomyNode GetNode(int index) =>
        (uint)index < (uint)_nodes.Length
            ? _nodes[index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    public ReadOnlySpan<HitRegionPartBinding> GetTissues(int index) =>
        GetNode(index).Tissues;

    public ReadOnlySpan<UnitAnatomyMarker> GetMarkers(int index) =>
        GetNode(index).Markers;

    public int GetVisibleRegionIndex(int visibleIndex) =>
        (uint)visibleIndex < (uint)_visibleRegions.Length
            ? _visibleRegions[visibleIndex]
            : throw new ArgumentOutOfRangeException(nameof(visibleIndex));

    public float GetHitHalfHeight(int index) => GetNode(index).HitHalfHeight;

    public bool IsInternalNode(int index) =>
        (uint)index < (uint)_nodes.Length &&
        _nodes[index].HasShape &&
        _nodes[index].Shape.Role == BodyPartRole.Internal;

    public override int FindRegion(string id)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (string.Equals(_nodes[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    public override bool TryFindPart(
        UnitHealthPartId part,
        out int regionIndex,
        out int bindingIndex)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            ReadOnlySpan<HitRegionPartBinding> tissues = _nodes[i].Tissues;
            for (int j = 0; j < tissues.Length; j++)
            {
                if (part != UnitHealthPartId.None && tissues[j].Part == part)
                {
                    regionIndex = i;
                    bindingIndex = j;
                    return true;
                }
            }
        }

        regionIndex = -1;
        bindingIndex = -1;
        return false;
    }

    public override bool TryFindPart(string id, out int regionIndex, out int bindingIndex)
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            ReadOnlySpan<HitRegionPartBinding> tissues = _nodes[i].Tissues;
            for (int j = 0; j < tissues.Length; j++)
            {
                if (string.Equals(tissues[j].Id, id, StringComparison.Ordinal))
                {
                    regionIndex = i;
                    bindingIndex = j;
                    return true;
                }
            }
        }

        regionIndex = -1;
        bindingIndex = -1;
        return false;
    }

    public bool TryGetMarker(
        int nodeIndex,
        UnitAnatomyMarkerRole role,
        out UnitAnatomyMarker marker) =>
        GetNode(nodeIndex).TryGetMarker(role, out marker);

    public bool TryGetMarker(
        int nodeIndex,
        string markerId,
        out UnitAnatomyMarker marker) =>
        GetNode(nodeIndex).TryGetMarker(markerId, out marker);

    private UnitAnatomyProfile(
        UnitAnatomyNode[] nodes,
        UnitCircleHitRegion[] compatibilityRegions,
        UnitAnatomyVolume[] volumes)
        : base(compatibilityRegions)
    {
        if (volumes == null)
            throw new ArgumentNullException(nameof(volumes));

        _nodes = (UnitAnatomyNode[])nodes.Clone();
        _volumes = (UnitAnatomyVolume[])volumes.Clone();

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

        ValidateVolumes();

        if (!sharedShapes)
            return;

        for (int i = 0; i < nodes.Length; i++)
        {
            BodyPartDefinition shape = nodes[i].Shape;
            if (shape.ParentIndex < -1 || shape.ParentIndex >= i)
                throw new ArgumentException(
                    $"Parent index of anatomy node '{nodes[i].Id}' must precede the node.",
                    nameof(nodes));

            _bodyParts[i] = shape;
        }
    }

    private void ValidateVolumes()
    {
        for (int i = 0; i < _volumes.Length; i++)
        {
            UnitAnatomyVolume volume = _volumes[i];
            int parentRegion = FindRegion(volume.ParentRegionId);
            if (parentRegion < 0)
                throw new ArgumentException($"Unknown parent region '{volume.ParentRegionId}' in anatomy volume '{volume.Id}'.", nameof(_volumes));

            if (!TryFindPart(volume.EntryBindingId, out int entryRegion, out _) || entryRegion != parentRegion)
                throw new ArgumentException($"Entry tissue '{volume.EntryBindingId}' must belong to region '{volume.ParentRegionId}'.", nameof(_volumes));

            if (!TryFindPart(volume.TargetBindingId, out _, out _))
                throw new ArgumentException($"Unknown target tissue '{volume.TargetBindingId}' in anatomy volume '{volume.Id}'.", nameof(_volumes));

            for (int j = i + 1; j < _volumes.Length; j++)
            {
                if (string.Equals(volume.Id, _volumes[j].Id, StringComparison.Ordinal))
                    throw new ArgumentException($"Duplicate anatomy volume ID '{volume.Id}'.", nameof(_volumes));
            }
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
            regions[i] = nodes[i].ToCompatibilityRegion();

        return regions;
    }
}
