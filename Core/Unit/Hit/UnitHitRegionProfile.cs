using System;
using System.Numerics;

namespace Core.Unit;

public readonly struct HitRegionPartBinding
{
    public UnitHealthPartId Part { get; }
    public float Weight { get; }
    public UnitHealthPartKind Kind { get; }
    public float MaxHitPoints { get; }
    public UnitHealthPartId ParentPart { get; }

    public HitRegionPartBinding(
        UnitHealthPartId part,
        float weight = 1f,
        UnitHealthPartKind kind = UnitHealthPartKind.BodyPart,
        float maxHitPoints = 40f,
        UnitHealthPartId parentPart = UnitHealthPartId.None)
    {
        if (part == UnitHealthPartId.None)
            throw new ArgumentOutOfRangeException(nameof(part));
        if (!float.IsFinite(weight) || weight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(weight));
        if (!float.IsFinite(maxHitPoints) || maxHitPoints <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxHitPoints));

        Part = part;
        Weight = weight;
        Kind = kind;
        MaxHitPoints = maxHitPoints;
        ParentPart = parentPart;
    }
}

/// <summary>
/// A circular XY hit region with vertical thickness. Its center is relative
/// to the unit root, so the same profile can be shared by every unit instance.
/// </summary>
public readonly struct UnitCircleHitRegion
{
    private readonly HitRegionPartBinding[] _parts;

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
            parts)
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
        HitRegionPartBinding[] parts)
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
        return new UnitCircleHitRegion(
            Vector3.Zero,
            centerFactors,
            0f,
            widthRadiusFactor,
            lengthRadiusFactor,
            0f,
            heightHalfExtentFactor,
            parts);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public sealed class UnitHitRegionProfile
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

            if (region.Parts.Length == 0)
                throw new ArgumentException("Every hit region must register body parts.", nameof(regions));

            float totalWeight = 0f;
            foreach (HitRegionPartBinding part in region.Parts)
                totalWeight += part.Weight;

            if (!float.IsFinite(totalWeight) || totalWeight <= 0f)
                throw new ArgumentException("Hit-region part weights must have a positive finite total.", nameof(regions));
        }
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
    private static readonly UnitHitRegionProfile Colonist = new(
        UnitCircleHitRegion.RelativeToUnit(
            new Vector3(0f, 0f, 0.15f),
            widthRadiusFactor: 0.22f,
            lengthRadiusFactor: 0.18f,
            heightHalfExtentFactor: 0.15f,
            new HitRegionPartBinding(UnitHealthPartId.LeftLeg, 1f, maxHitPoints: 35f),
            new HitRegionPartBinding(UnitHealthPartId.RightLeg, 1f, maxHitPoints: 35f),
            new HitRegionPartBinding(UnitHealthPartId.LeftFoot, 0.5f, maxHitPoints: 14f),
            new HitRegionPartBinding(UnitHealthPartId.RightFoot, 0.5f, maxHitPoints: 14f)),
        UnitCircleHitRegion.RelativeToUnit(
            new Vector3(0f, 0f, 0.50f),
            widthRadiusFactor: 0.22f,
            lengthRadiusFactor: 0.18f,
            heightHalfExtentFactor: 0.22f,
            new HitRegionPartBinding(UnitHealthPartId.Torso, 5f, maxHitPoints: 60f),
            new HitRegionPartBinding(UnitHealthPartId.LeftArm, 1f, maxHitPoints: 30f),
            new HitRegionPartBinding(UnitHealthPartId.RightArm, 1f, maxHitPoints: 30f),
            new HitRegionPartBinding(UnitHealthPartId.LeftHand, 0.5f, maxHitPoints: 12f),
            new HitRegionPartBinding(UnitHealthPartId.RightHand, 0.5f, maxHitPoints: 12f)),
        UnitCircleHitRegion.RelativeToUnit(
            new Vector3(0f, 0f, 0.84f),
            widthRadiusFactor: 0.20f,
            lengthRadiusFactor: 0.05f,
            heightHalfExtentFactor: 0.13f,
            new HitRegionPartBinding(UnitHealthPartId.Head, 4f, maxHitPoints: 40f),
            new HitRegionPartBinding(UnitHealthPartId.Brain, 1f, UnitHealthPartKind.Organ, 10f, UnitHealthPartId.Head)));

    private static readonly UnitHitRegionProfile SmallCreature = new(
        UnitCircleHitRegion.RelativeToUnit(
            new Vector3(0f, 0f, 0.50f),
            widthRadiusFactor: 0.25f,
            lengthRadiusFactor: 0.10f,
            heightHalfExtentFactor: 0.50f,
            new HitRegionPartBinding(UnitHealthPartId.WholeBody, 1f, maxHitPoints: 100f)));

    private static readonly UnitHitRegionProfile SegmentedCreature = CreateSegmented();

    public static UnitHitRegionProfile Get(UnitBodyType bodyType) =>
        bodyType switch
        {
            UnitBodyType.Colonist => Colonist,
            UnitBodyType.SmallCreature => SmallCreature,
            UnitBodyType.SegmentedCreature => SegmentedCreature,
            _ => throw new ArgumentOutOfRangeException(nameof(bodyType), bodyType, "Unknown hit-region profile.")
        };

    private static UnitHitRegionProfile CreateSegmented()
    {
        var regions = new UnitCircleHitRegion[10];

        for (int i = 0; i < regions.Length; i++)
        {
            regions[i] = UnitCircleHitRegion.RelativeToUnit(
                new Vector3(-0.62f * i, 0f, 0.50f),
                widthRadiusFactor: 0.48f,
                lengthRadiusFactor: 0f,
                heightHalfExtentFactor: 0.50f,
                new HitRegionPartBinding(UnitHealthPartId.WholeBody, 1f, maxHitPoints: 100f));
        }

        return new UnitHitRegionProfile(regions);
    }
}
