using System;
using System.Collections.Generic;
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

    /// <summary>Edits transforms of the current topology while preserving stable IDs.</summary>
    public UnitAnatomyProfile WithBodyParts(ReadOnlySpan<BodyPartDefinition> bodyParts)
    {
        if (bodyParts.Length != NodeCount)
            throw new ArgumentException("Edited body-part count must match the anatomy profile.", nameof(bodyParts));

        var ids = new string[NodeCount];
        for (int i = 0; i < ids.Length; i++)
            ids[i] = _nodes[i].Id;

        return WithRig(ids, bodyParts, _volumes);
    }

    public UnitAnatomyProfile WithRig(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts) =>
        WithRig(regionIds, bodyParts, _volumes);

    /// <summary>
    /// Rebuilds a rig with editable organs. Every organ's full bounding volume is
    /// constrained to the ellipsoid of its host region. Organs create their own
    /// damageable tissue bindings while preserving all existing stable tissue IDs.
    /// </summary>
    public UnitAnatomyProfile WithRig(
        ReadOnlySpan<string> regionIds,
        ReadOnlySpan<BodyPartDefinition> bodyParts,
        ReadOnlySpan<UnitAnatomyVolume> volumes)
    {
        if (!HasSharedShapes || regionIds.Length != bodyParts.Length ||
            regionIds.Length == 0 || regionIds.Length > 128 || volumes.Length > 256)
            throw new ArgumentException("The rig must have 1–128 regions and at most 256 organs.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < regionIds.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(regionIds[i]) || !seen.Add(regionIds[i]))
                throw new ArgumentException($"Empty or duplicate anatomy region ID '{regionIds[i]}'.");
            if (bodyParts[i].ParentIndex < -1 || bodyParts[i].ParentIndex >= i)
                throw new ArgumentException($"Parent of '{regionIds[i]}' must precede it in the list.");
        }

        var nextVolumes = new UnitAnatomyVolume[volumes.Length];
        var volumeIds = new HashSet<string>(StringComparer.Ordinal);
        var bindingIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < volumes.Length; i++)
        {
            UnitAnatomyVolume volume = volumes[i];
            if (!volumeIds.Add(volume.Id))
                throw new ArgumentException($"Duplicate organ ID '{volume.Id}'.");
            if (!bindingIds.Add(volume.TargetBindingId))
                throw new ArgumentException($"Duplicate organ tissue ID '{volume.TargetBindingId}'.");

            int parentIndex = IndexOf(regionIds, volume.ParentRegionId);
            if (parentIndex < 0)
                throw new ArgumentException($"Organ '{volume.Id}' references missing host region '{volume.ParentRegionId}'.");
            if (!ContainsBinding(regionIds, volume.EntryBindingId))
                throw new ArgumentException($"Organ '{volume.Id}' references missing entry tissue '{volume.EntryBindingId}'.");
            nextVolumes[i] = FitVolume(volume, bodyParts[parentIndex]);
        }

        var nodes = new UnitAnatomyNode[regionIds.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            BodyPartDefinition shape = bodyParts[i];
            string parentId = shape.ParentIndex >= 0 ? regionIds[shape.ParentIndex] : string.Empty;
            int oldIndex = FindRegion(regionIds[i]);
            HitRegionPartBinding[] tissues;
            UnitAnatomyMarker[] markers;

            if (oldIndex >= 0)
            {
                UnitAnatomyNode previous = _nodes[oldIndex];
                tissues = previous.Tissues.ToArray();
                markers = previous.Markers.ToArray();
            }
            else
            {
                string tissueId = $"{regionIds[i]}.tissue";
                string parentTissueId = shape.ParentIndex >= 0
                    ? nodes[shape.ParentIndex].Tissues[0].Id
                    : string.Empty;
                tissues = new[]
                {
                    new HitRegionPartBinding(
                        UnitHealthPartId.WholeBody,
                        kind: UnitHealthPartKind.BodyPart,
                        maxHitPoints: 30f,
                        id: tissueId,
                        parentId: string.IsNullOrWhiteSpace(parentTissueId) ? null : parentTissueId)
                };
                markers = new[]
                {
                    new UnitAnatomyMarker(
                        "target",
                        UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision)
                };
            }

            var filtered = new List<HitRegionPartBinding>(tissues.Length + 2);
            for (int tissueIndex = 0; tissueIndex < tissues.Length; tissueIndex++)
            {
                HitRegionPartBinding tissue = tissues[tissueIndex];
                bool wasVolumeTissue = ContainsTarget(_volumes, tissue.Id);
                bool stillUsed = ContainsTarget(nextVolumes, tissue.Id);
                if (wasVolumeTissue && !stillUsed && tissue.Kind == UnitHealthPartKind.Organ)
                    continue;
                filtered.Add(tissue);
            }
            tissues = filtered.ToArray();

            for (int volumeIndex = 0; volumeIndex < nextVolumes.Length; volumeIndex++)
            {
                UnitAnatomyVolume volume = nextVolumes[volumeIndex];
                if (volume.ParentRegionId != regionIds[i] ||
                    ContainsBinding(tissues, volume.TargetBindingId))
                    continue;

                filtered = new List<HitRegionPartBinding>(tissues)
                {
                    new HitRegionPartBinding(
                        UnitHealthPartId.WholeBody,
                        weight: 1f,
                        kind: UnitHealthPartKind.Organ,
                        maxHitPoints: 20f,
                        id: volume.TargetBindingId,
                        parentId: volume.EntryBindingId)
                };
                tissues = filtered.ToArray();
            }

            nodes[i] = UnitAnatomyNode.Part(
                regionIds[i],
                parentId,
                shape,
                MathF.Max(0.001f, MathF.Abs(shape.Scale.Z)),
                tissues,
                markers);
        }

        return new UnitAnatomyProfile(nodes, Convert(nodes), nextVolumes);
    }

    private bool ContainsBinding(ReadOnlySpan<string> regionIds, string bindingId)
    {
        for (int i = 0; i < regionIds.Length; i++)
        {
            int oldIndex = FindRegion(regionIds[i]);
            if (string.Equals(bindingId, $"{regionIds[i]}.tissue", StringComparison.Ordinal))
                return true;
            if (oldIndex < 0)
                continue;
            ReadOnlySpan<HitRegionPartBinding> tissues = _nodes[oldIndex].Tissues;
            for (int j = 0; j < tissues.Length; j++)
                if (tissues[j].Id == bindingId)
                    return true;
        }
        return false;
    }

    private static bool ContainsBinding(ReadOnlySpan<HitRegionPartBinding> tissues, string bindingId)
    {
        for (int i = 0; i < tissues.Length; i++)
            if (tissues[i].Id == bindingId)
                return true;
        return false;
    }

    private static bool ContainsTarget(ReadOnlySpan<UnitAnatomyVolume> volumes, string bindingId)
    {
        for (int i = 0; i < volumes.Length; i++)
            if (volumes[i].TargetBindingId == bindingId)
                return true;
        return false;
    }

    private static int IndexOf(ReadOnlySpan<string> ids, string id)
    {
        for (int i = 0; i < ids.Length; i++)
            if (string.Equals(ids[i], id, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private static UnitAnatomyVolume FitVolume(UnitAnatomyVolume volume, BodyPartDefinition host)
    {
        Vector3 scale = Vector3.Max(Vector3.Abs(host.Scale), new Vector3(0.006f));
        Vector3 center = volume.LocalCenter;
        Vector3 limits = scale * 0.85f;

        // A triangle's bounding box is larger than its actual silhouette. Keep
        // organs within the inscribed centre instead of allowing them into corners.
        if (host.Primitive == BodyPrimitive.Triangle)
            limits = new Vector3(scale.X * 0.25f, scale.Y * 0.25f, scale.Z * 0.85f);

        Vector3 extents = Vector3.Min(Vector3.Max(volume.HalfExtents, new Vector3(0.002f)), limits);
        float cos = MathF.Abs(MathF.Cos(volume.LocalRotation));
        float sin = MathF.Abs(MathF.Sin(volume.LocalRotation));
        extents = Vector3.Max(extents, new Vector3(0.002f));

        // Enforce containment after the minimum organ size is applied as well.
        // The minimum extents must not undo the previous containment correction.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            float rotatedHalfX = cos * extents.X + sin * extents.Y;
            float rotatedHalfY = sin * extents.X + cos * extents.Y;
            float nx = (MathF.Abs(center.X) + rotatedHalfX) / scale.X;
            float ny = (MathF.Abs(center.Y) + rotatedHalfY) / scale.Y;
            float nz = (MathF.Abs(center.Z) + extents.Z) / scale.Z;
            float radius = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (radius <= 0.90f)
                break;

            float factor = 0.90f / radius;
            center *= factor;
            extents = Vector3.Max(extents * factor, new Vector3(0.002f));
        }
        return new UnitAnatomyVolume(
            volume.Id,
            volume.ParentRegionId,
            volume.EntryBindingId,
            volume.TargetBindingId,
            center,
            extents,
            volume.Primitive,
            volume.LocalRotation,
            volume.EnergyLoss,
            volume.PenetrationLoss,
            volume.DamageMultiplier,
            volume.BleedMultiplier);
    }

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
        // IDs are the stable references used by parents, hit volumes and health.
        for (int i = 0; i < _nodes.Length; i++)
        {
            for (int prior = 0; prior < i; prior++)
            {
                if (string.Equals(_nodes[i].Id, _nodes[prior].Id, StringComparison.Ordinal))
                    throw new ArgumentException($"Duplicate anatomy region ID '{_nodes[i].Id}'.", nameof(_nodes));
            }

            ReadOnlySpan<HitRegionPartBinding> bindings = _nodes[i].Tissues;
            for (int b = 0; b < bindings.Length; b++)
            {
                string id = bindings[b].Id;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                for (int priorRegion = 0; priorRegion <= i; priorRegion++)
                {
                    ReadOnlySpan<HitRegionPartBinding> priorBindings = _nodes[priorRegion].Tissues;
                    int limit = priorRegion == i ? b : priorBindings.Length;
                    for (int priorBinding = 0; priorBinding < limit; priorBinding++)
                    {
                        if (string.Equals(id, priorBindings[priorBinding].Id, StringComparison.Ordinal))
                            throw new ArgumentException($"Duplicate anatomy tissue ID '{id}'.", nameof(_nodes));
                    }
                }
            }
        }

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
