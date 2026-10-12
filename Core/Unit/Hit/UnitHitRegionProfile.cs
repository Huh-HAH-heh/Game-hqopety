using System;
using System.Numerics;

namespace Core.Unit;

[Flags]
public enum UnitAnatomyMarkerRole : ushort
{
    None = 0,
    Vision = 1,
    Target = 2,
    Aim = 4,
    Eye = 8,
    Vital = 16,
    Interaction = 32,
    Locomotion = 64,
    WeaponMount = 128,
    Muzzle = 256
}

/// <summary>
/// A named functional point attached to a circular anatomy node.
/// LocalOffset is relative to that node's center and rotates with the unit.
/// </summary>
public readonly struct UnitAnatomyMarker
{
    public string Id { get; }
    public UnitAnatomyMarkerRole Roles { get; }
    public Vector3 LocalOffset { get; }

    public UnitAnatomyMarker(
        string id,
        UnitAnatomyMarkerRole roles,
        Vector3 localOffset = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A marker needs a stable ID.", nameof(id));
        if (!float.IsFinite(localOffset.X) ||
            !float.IsFinite(localOffset.Y) ||
            !float.IsFinite(localOffset.Z))
            throw new ArgumentOutOfRangeException(nameof(localOffset));

        Id = id;
        Roles = roles;
        LocalOffset = localOffset;
    }
}

/// <summary>
/// A named health/tissue attachment on a geometry node. Part is optional legacy
/// metadata for old armor/diagnostic code; Id identifies this exact tissue.
/// </summary>
public readonly struct HitRegionPartBinding
{
    public string Id { get; }
    public string Name { get; }
    public string ParentId { get; }
    public UnitHealthPartId Part { get; }
    public float Weight { get; }
    public UnitHealthPartKind Kind { get; }
    public float MaxHitPoints { get; }
    public UnitHealthPartId ParentPart { get; }
    public int ArmorZone { get; }

    public HitRegionPartBinding(
        UnitHealthPartId part,
        float weight = 1f,
        UnitHealthPartKind kind = UnitHealthPartKind.BodyPart,
        float maxHitPoints = 40f,
        UnitHealthPartId parentPart = UnitHealthPartId.None,
        string? id = null,
        string? parentId = null,
        int armorZone = -1)
    {
        if (part == UnitHealthPartId.None && string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Generic anatomy parts require a stable ID.", nameof(id));
        if (armorZone < -1 || armorZone >= UnitInventoryStore.BodyZoneCount)
            throw new ArgumentOutOfRangeException(nameof(armorZone));
        if (!float.IsFinite(weight) || weight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(weight));
        if (!float.IsFinite(maxHitPoints) || maxHitPoints <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxHitPoints));

        Id = id ?? string.Empty;
        Name = string.IsNullOrWhiteSpace(id) ? part.ToString() : id;
        ParentId = parentId ?? string.Empty;
        Part = part;
        Weight = weight;
        Kind = kind;
        MaxHitPoints = maxHitPoints;
        ParentPart = parentPart;
        ArmorZone = armorZone;
    }
}

/// <summary>
/// A circular XY hit region with vertical thickness. Its center is relative
/// to the unit root, so the same profile can be shared by every unit instance.
/// </summary>
public readonly struct UnitCircleHitRegion
{
    private readonly HitRegionPartBinding[] _parts;
    private readonly UnitAnatomyMarker[] _markers;

    public string Id { get; }
    public string ParentId { get; }
    public ReadOnlySpan<UnitAnatomyMarker> Markers => _markers;
    public Vector3 LocalCenter { get; }
    public Vector3 DimensionCenterFactors { get; }
    public float Radius { get; }
    public float WidthRadiusFactor { get; }
    public float LengthRadiusFactor { get; }
    public float HalfHeight { get; }
    public float HeightHalfExtentFactor { get; }
    public ReadOnlySpan<HitRegionPartBinding> Parts => _parts;

    public UnitCircleHitRegion(
        Vector3 localCenter,
        float radius,
        float halfHeight,
        params HitRegionPartBinding[] parts)
        : this(
            localCenter,
            Vector3.Zero,
            radius,
            0f,
            0f,
            halfHeight,
            0f,
            parts,
            string.Empty,
            string.Empty,
            Array.Empty<UnitAnatomyMarker>())
    {
    }

    public UnitCircleHitRegion(
        string id,
        Vector3 localCenter,
        float radius,
        float halfHeight,
        HitRegionPartBinding[] parts,
        params UnitAnatomyMarker[] markers)
        : this(id, string.Empty, localCenter, radius, halfHeight, parts, markers)
    {
    }

    public UnitCircleHitRegion(
        string id,
        string parentId,
        Vector3 localCenter,
        float radius,
        float halfHeight,
        HitRegionPartBinding[] parts,
        params UnitAnatomyMarker[] markers)
        : this(
            localCenter,
            Vector3.Zero,
            radius,
            0f,
            0f,
            halfHeight,
            0f,
            parts,
            id,
            parentId ?? string.Empty,
            markers)
    {
    }

    private UnitCircleHitRegion(
        Vector3 localCenter,
        Vector3 dimensionCenterFactors,
        float radius,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float halfHeight,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] parts,
        string id,
        string parentId,
        UnitAnatomyMarker[] markers)
    {
        if (!IsFinite(localCenter) || !IsFinite(dimensionCenterFactors))
            throw new ArgumentOutOfRangeException(nameof(localCenter));
        if (!float.IsFinite(radius) || radius < 0f ||
            !float.IsFinite(widthRadiusFactor) || widthRadiusFactor < 0f ||
            !float.IsFinite(lengthRadiusFactor) || lengthRadiusFactor < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (!float.IsFinite(halfHeight) || halfHeight < 0f ||
            !float.IsFinite(heightHalfExtentFactor) || heightHalfExtentFactor < 0f)
            throw new ArgumentOutOfRangeException(nameof(halfHeight));
        if (parts == null || parts.Length == 0)
            throw new ArgumentException("A hit region must register at least one body part.", nameof(parts));

        if (markers == null)
            throw new ArgumentNullException(nameof(markers));

        for (int i = 0; i < markers.Length; i++)
        {
            for (int j = i + 1; j < markers.Length; j++)
            {
                if (string.Equals(markers[i].Id, markers[j].Id, StringComparison.Ordinal))
                    throw new ArgumentException("Marker IDs must be unique within a node.", nameof(markers));
            }
        }

        Id = id ?? string.Empty;
        ParentId = parentId ?? string.Empty;
        _markers = (UnitAnatomyMarker[])markers.Clone();
        LocalCenter = localCenter;
        DimensionCenterFactors = dimensionCenterFactors;
        Radius = radius;
        WidthRadiusFactor = widthRadiusFactor;
        LengthRadiusFactor = lengthRadiusFactor;
        HalfHeight = halfHeight;
        HeightHalfExtentFactor = heightHalfExtentFactor;
        _parts = (HitRegionPartBinding[])parts.Clone();
    }

    public static UnitCircleHitRegion RelativeToUnit(
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        params HitRegionPartBinding[] parts)
    {
        return RelativeToUnit(
            string.Empty, centerFactors, widthRadiusFactor, lengthRadiusFactor,
            heightHalfExtentFactor, parts, Array.Empty<UnitAnatomyMarker>());
    }

    public static UnitCircleHitRegion RelativeToUnit(
        string id,
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] parts,
        params UnitAnatomyMarker[] markers) =>
        RelativeToUnit(id, string.Empty, centerFactors, widthRadiusFactor,
            lengthRadiusFactor, heightHalfExtentFactor, parts, markers);

    public static UnitCircleHitRegion RelativeToUnit(
        string id,
        string parentId,
        Vector3 centerFactors,
        float widthRadiusFactor,
        float lengthRadiusFactor,
        float heightHalfExtentFactor,
        HitRegionPartBinding[] parts,
        params UnitAnatomyMarker[] markers)
    {
        return new UnitCircleHitRegion(
            Vector3.Zero,
            centerFactors,
            0f,
            widthRadiusFactor,
            lengthRadiusFactor,
            0f,
            heightHalfExtentFactor,
            parts,
            id,
            parentId ?? string.Empty,
            markers);
    }

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

    public bool HasMarker(string id)
    {
        for (int i = 0; i < _markers.Length; i++)
        {
            if (string.Equals(_markers[i].Id, id, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public class UnitHitRegionProfile
{
    private readonly UnitCircleHitRegion[] _regions;

    public int RegionCount => _regions.Length;
    public ReadOnlySpan<UnitCircleHitRegion> Regions => _regions;

    public UnitHitRegionProfile(params UnitCircleHitRegion[] regions)
    {
        if (regions == null || regions.Length == 0)
            throw new ArgumentException("A hit profile must contain at least one region.", nameof(regions));

        _regions = (UnitCircleHitRegion[])regions.Clone();

        for (int i = 0; i < _regions.Length; i++)
        {
            UnitCircleHitRegion region = _regions[i];

            if (!string.IsNullOrWhiteSpace(region.Id))
            {
                for (int j = i + 1; j < _regions.Length; j++)
                {
                    if (string.Equals(region.Id, _regions[j].Id, StringComparison.Ordinal))
                        throw new ArgumentException($"Duplicate anatomy node ID '{region.Id}'.", nameof(regions));
                }
            }

            for (int p = 0; p < region.Parts.Length; p++)
            {
                HitRegionPartBinding part = region.Parts[p];
                if (string.IsNullOrWhiteSpace(part.Id))
                    continue;

                for (int otherRegion = 0; otherRegion <= i; otherRegion++)
                {
                    ReadOnlySpan<HitRegionPartBinding> otherParts = _regions[otherRegion].Parts;
                    int limit = otherRegion == i ? p : otherParts.Length;
                    for (int otherPart = 0; otherPart < limit; otherPart++)
                    {
                        if (string.Equals(part.Id, otherParts[otherPart].Id, StringComparison.Ordinal))
                            throw new ArgumentException($"Duplicate anatomy part ID '{part.Id}'.", nameof(regions));
                    }
                }
            }

            if (region.Parts.Length == 0)
                throw new ArgumentException("Every hit region must register body parts.", nameof(regions));

            float totalWeight = 0f;
            foreach (HitRegionPartBinding part in region.Parts)
                totalWeight += part.Weight;

            if (!float.IsFinite(totalWeight) || totalWeight <= 0f)
                throw new ArgumentException("Hit-region part weights must have a positive finite total.", nameof(regions));
        }

        for (int i = 0; i < _regions.Length; i++)
        {
            UnitCircleHitRegion region = _regions[i];
            if (!string.IsNullOrWhiteSpace(region.ParentId))
            {
                int parentIndex = FindRegion(region.ParentId);
                if (parentIndex < 0 || parentIndex >= i)
                {
                    throw new ArgumentException(
                        $"Parent node '{region.ParentId}' of anatomy node '{region.Id}' must exist earlier in the profile.",
                        nameof(regions));
                }
            }

            foreach (HitRegionPartBinding binding in region.Parts)
            {
                if (!string.IsNullOrWhiteSpace(binding.ParentId) &&
                    !TryFindPart(binding.ParentId, out _, out _))
                {
                    throw new ArgumentException(
                        $"Anatomy tissue '{binding.Id}' references missing parent tissue '{binding.ParentId}'.",
                        nameof(regions));
                }
            }
        }
    }

    public int FindRegion(string id)
    {
        for (int i = 0; i < _regions.Length; i++)
        {
            if (string.Equals(_regions[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    public bool TryFindPart(
        UnitHealthPartId part,
        out int regionIndex,
        out int bindingIndex)
    {
        if (part != UnitHealthPartId.None)
        {
            for (int i = 0; i < _regions.Length; i++)
            {
                ReadOnlySpan<HitRegionPartBinding> bindings = _regions[i].Parts;
                for (int j = 0; j < bindings.Length; j++)
                {
                    if (bindings[j].Part != part)
                        continue;

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

    public bool TryFindPart(string id, out int regionIndex, out int bindingIndex)
    {
        for (int i = 0; i < _regions.Length; i++)
        {
            ReadOnlySpan<HitRegionPartBinding> parts = _regions[i].Parts;
            for (int j = 0; j < parts.Length; j++)
            {
                if (!string.Equals(parts[j].Id, id, StringComparison.Ordinal))
                    continue;

                regionIndex = i;
                bindingIndex = j;
                return true;
            }
        }

        regionIndex = -1;
        bindingIndex = -1;
        return false;
    }
}

/// <summary>
/// Per-unit profile references. Immutable profiles are shared; no hit-region
/// objects are allocated during vision or projectile updates.
/// </summary>
public sealed class UnitHitRegionStore
{
    private UnitHitRegionProfile?[] _profiles;

    public int Capacity => _profiles.Length;

    public UnitHitRegionStore(int initialCapacity = 1024)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));

        _profiles = new UnitHitRegionProfile?[initialCapacity];
    }

    public UnitHitRegionProfile? GetProfile(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_profiles.Length)
            return null;

        return _profiles[unitIndex];
    }

    public void SetProfile(int unitIndex, UnitHitRegionProfile profile)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        EnsureCapacity(unitIndex + 1);
        _profiles[unitIndex] = profile;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex < (uint)_profiles.Length)
            _profiles[unitIndex] = null;
    }

    public void EnsureCapacity(int required)
    {
        if (required <= _profiles.Length)
            return;

        int capacity = _profiles.Length;
        while (capacity < required)
            capacity *= 2;

        Array.Resize(ref _profiles, capacity);
    }
}

/// <summary>
/// Default profiles are data definitions. Collision and visibility do not
/// branch on creature type; they consume only the profile assigned to a unit.
/// </summary>
public static class UnitHitRegionCatalog
{
    private static readonly UnitAnatomyProfile Colonist = new(
        UnitAnatomyNode.RelativeCircle(
            "legs",
            new Vector3(0f, 0f, 0.15f),
            widthRadiusFactor: 0.22f,
            lengthRadiusFactor: 0.18f,
            heightHalfExtentFactor: 0.15f,
            new[]
            {
                new HitRegionPartBinding(UnitHealthPartId.LeftLeg, 1f, maxHitPoints: 35f, id: "leg.left"),
                new HitRegionPartBinding(UnitHealthPartId.LeftFoot, 0.5f, maxHitPoints: 14f, parentPart: UnitHealthPartId.LeftLeg, id: "foot.left", parentId: "leg.left"),
                new HitRegionPartBinding(UnitHealthPartId.RightLeg, 1f, maxHitPoints: 35f, id: "leg.right"),
                new HitRegionPartBinding(UnitHealthPartId.RightFoot, 0.5f, maxHitPoints: 14f, parentPart: UnitHealthPartId.RightLeg, id: "foot.right", parentId: "leg.right")
            },
            new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
            new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)),
        UnitAnatomyNode.RelativeCircle(
            "torso",
            new Vector3(0f, 0f, 0.50f),
            widthRadiusFactor: 0.22f,
            lengthRadiusFactor: 0.18f,
            heightHalfExtentFactor: 0.22f,
            new[]
            {
                new HitRegionPartBinding(UnitHealthPartId.Torso, 5f, maxHitPoints: 60f, id: "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.LeftArm, 1f, maxHitPoints: 30f, id: "arm.left"),
                new HitRegionPartBinding(UnitHealthPartId.LeftHand, 0.5f, maxHitPoints: 12f, parentPart: UnitHealthPartId.LeftArm, id: "hand.left", parentId: "arm.left"),
                new HitRegionPartBinding(UnitHealthPartId.RightArm, 1f, maxHitPoints: 30f, id: "arm.right"),
                new HitRegionPartBinding(UnitHealthPartId.RightHand, 0.5f, maxHitPoints: 12f, parentPart: UnitHealthPartId.RightArm, id: "hand.right", parentId: "arm.right"),
                new HitRegionPartBinding(UnitHealthPartId.Heart, 0.25f, UnitHealthPartKind.Organ, 15f, UnitHealthPartId.Torso, "organ.heart", "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.LeftLung, 0.20f, UnitHealthPartKind.Organ, 12f, UnitHealthPartId.Torso, "organ.lung.left", "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.RightLung, 0.20f, UnitHealthPartKind.Organ, 12f, UnitHealthPartId.Torso, "organ.lung.right", "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.Stomach, 0.15f, UnitHealthPartKind.Organ, 12f, UnitHealthPartId.Torso, "organ.stomach", "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.Liver, 0.15f, UnitHealthPartKind.Organ, 15f, UnitHealthPartId.Torso, "organ.liver", "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.LeftKidney, 0.10f, UnitHealthPartKind.Organ, 10f, UnitHealthPartId.Torso, "organ.kidney.left", "torso.main"),
                new HitRegionPartBinding(UnitHealthPartId.RightKidney, 0.10f, UnitHealthPartKind.Organ, 10f, UnitHealthPartId.Torso, "organ.kidney.right", "torso.main")
            },
            new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
            new UnitAnatomyMarker("muzzle", UnitAnatomyMarkerRole.Muzzle, new Vector3(0.30f, 0f, 0.25f)),
            new UnitAnatomyMarker("interaction", UnitAnatomyMarkerRole.Interaction)),
        UnitAnatomyNode.RelativeCircle(
            "head",
            "torso",
            new Vector3(0f, 0f, 0.34f),
            widthRadiusFactor: 0.20f,
            lengthRadiusFactor: 0.05f,
            heightHalfExtentFactor: 0.13f,
            new[]
            {
                new HitRegionPartBinding(UnitHealthPartId.Head, 4f, maxHitPoints: 40f, id: "head.main"),
                new HitRegionPartBinding(UnitHealthPartId.Brain, 1f, UnitHealthPartKind.Organ, 10f, UnitHealthPartId.Head, "organ.brain", "head.main")
            },
            new UnitAnatomyMarker("eye", UnitAnatomyMarkerRole.Eye, new Vector3(0f, 0f, 0.05f)),
            new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision, new Vector3(0f, 0f, 0.02f))));

    private static readonly UnitAnatomyProfile SmallCreature = new(
        UnitAnatomyNode.RelativeCircle(
            "body",
            new Vector3(0f, 0f, 0.50f),
            widthRadiusFactor: 0.25f,
            lengthRadiusFactor: 0.10f,
            heightHalfExtentFactor: 0.50f,
            new[]
            {
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, 1f, maxHitPoints: 100f, id: "body.main")
            },
            new UnitAnatomyMarker("eye", UnitAnatomyMarkerRole.Eye, new Vector3(0f, 0f, 0.15f)),
            new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision)));

    private static readonly UnitAnatomyProfile ScavenRat = new(
        UnitAnatomyNode.RelativeCircle(
            "rat.body",
            new Vector3(0f, 0f, 0.80f),
            widthRadiusFactor: 0.70f,
            lengthRadiusFactor: 0.32f,
            heightHalfExtentFactor: 0.40f,
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.WholeBody, 1f,
                    maxHitPoints: 80f, id: "rat.body.tissue")
            },
            new UnitAnatomyMarker(
                "target",
                UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
            new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion)),
        UnitAnatomyNode.RelativeCircle(
            "rat.head",
            "rat.body",
            new Vector3(0.34f, 0f, 0.02f),
            widthRadiusFactor: 0.24f,
            lengthRadiusFactor: 0.08f,
            heightHalfExtentFactor: 0.20f,
            new[]
            {
                new HitRegionPartBinding(
                    UnitHealthPartId.Head, 0.6f,
                    maxHitPoints: 30f, id: "rat.head.tissue",
                    parentId: "rat.body.tissue")
            },
            new UnitAnatomyMarker("eye", UnitAnatomyMarkerRole.Eye),
            new UnitAnatomyMarker(
                "target",
                UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision)));

    private static readonly UnitAnatomyProfile SegmentedCreature = CreateSegmented();

    public static UnitHitRegionProfile Get(UnitBodyType bodyType) =>
        bodyType switch
        {
            UnitBodyType.Colonist => Colonist,
            UnitBodyType.SmallCreature => SmallCreature,
            UnitBodyType.ScavenRat => ScavenRat,
            UnitBodyType.SegmentedCreature => SegmentedCreature,
            _ => throw new ArgumentOutOfRangeException(nameof(bodyType), bodyType, "Unknown anatomy profile.")
        };

    private static UnitAnatomyProfile CreateSegmented()
    {
        var nodes = new UnitAnatomyNode[10];

        for (int i = 0; i < nodes.Length; i++)
        {
            string id = $"segment.{i}";
            string parentId = i == 0 ? string.Empty : $"segment.{i - 1}";
            string tissueId = $"{id}.tissue";
            string parentTissueId = i == 0 ? string.Empty : $"segment.{i - 1}.tissue";

            var tissue = new HitRegionPartBinding(
                UnitHealthPartId.WholeBody,
                1f,
                UnitHealthPartKind.BodyPart,
                100f,
                id: tissueId,
                parentId: parentTissueId);

            nodes[i] = UnitAnatomyNode.RelativeCircle(
                id,
                parentId,
                new Vector3(i == 0 ? 0f : -0.62f, 0f, i == 0 ? 0.50f : 0f),
                widthRadiusFactor: 0.48f,
                lengthRadiusFactor: 0f,
                heightHalfExtentFactor: 0.50f,
                new[] { tissue },
                new UnitAnatomyMarker("target", UnitAnatomyMarkerRole.Target | UnitAnatomyMarkerRole.Aim | UnitAnatomyMarkerRole.Vision),
                new UnitAnatomyMarker("locomotion", UnitAnatomyMarkerRole.Locomotion));
        }

        return new UnitAnatomyProfile(nodes);
    }
}
