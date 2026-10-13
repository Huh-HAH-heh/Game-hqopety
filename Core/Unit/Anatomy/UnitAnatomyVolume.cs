using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// A damageable volume inside an anatomy node. It adds no rendered body part:
/// collision is traced only after its configured entry tissue is penetrated.
/// Positions and extents use the same world units as BodyPartDefinition.
/// </summary>
public readonly struct UnitAnatomyVolume
{
    public string Id { get; }
    public string ParentRegionId { get; }
    public string EntryBindingId { get; }
    public string TargetBindingId { get; }
    public Vector3 LocalCenter { get; }
    public Vector3 HalfExtents { get; }
    public BodyPrimitive Primitive { get; }
    public float LocalRotation { get; }
    public float EnergyLoss { get; }
    public float PenetrationLoss { get; }
    public float DamageMultiplier { get; }
    public float BleedMultiplier { get; }

    public UnitAnatomyVolume(
        string id,
        string parentRegionId,
        string entryBindingId,
        string targetBindingId,
        Vector3 localCenter,
        Vector3 halfExtents,
        BodyPrimitive primitive = BodyPrimitive.Circle,
        float localRotation = 0f,
        float energyLoss = 18f,
        float penetrationLoss = 1f,
        float damageMultiplier = 0.75f,
        float bleedMultiplier = 1f)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An anatomy volume requires a stable ID.", nameof(id));
        if (string.IsNullOrWhiteSpace(parentRegionId))
            throw new ArgumentException("An anatomy volume requires a parent region ID.", nameof(parentRegionId));
        if (string.IsNullOrWhiteSpace(entryBindingId))
            throw new ArgumentException("An anatomy volume requires an entry tissue ID.", nameof(entryBindingId));
        if (string.IsNullOrWhiteSpace(targetBindingId))
            throw new ArgumentException("An anatomy volume requires a target tissue ID.", nameof(targetBindingId));
        if (!IsFinite(localCenter))
            throw new ArgumentOutOfRangeException(nameof(localCenter));
        if (!IsFinite(halfExtents) || halfExtents.X <= 0f || halfExtents.Y <= 0f || halfExtents.Z <= 0f)
            throw new ArgumentOutOfRangeException(nameof(halfExtents));
        if (!float.IsFinite(localRotation))
            throw new ArgumentOutOfRangeException(nameof(localRotation));
        if (!float.IsFinite(energyLoss) || energyLoss < 0f)
            throw new ArgumentOutOfRangeException(nameof(energyLoss));
        if (!float.IsFinite(penetrationLoss) || penetrationLoss < 0f)
            throw new ArgumentOutOfRangeException(nameof(penetrationLoss));
        if (!float.IsFinite(damageMultiplier) || damageMultiplier < 0f)
            throw new ArgumentOutOfRangeException(nameof(damageMultiplier));
        if (!float.IsFinite(bleedMultiplier) || bleedMultiplier < 0f)
            throw new ArgumentOutOfRangeException(nameof(bleedMultiplier));

        Id = id;
        ParentRegionId = parentRegionId;
        EntryBindingId = entryBindingId;
        TargetBindingId = targetBindingId;
        LocalCenter = localCenter;
        HalfExtents = halfExtents;
        Primitive = primitive;
        LocalRotation = localRotation;
        EnergyLoss = energyLoss;
        PenetrationLoss = penetrationLoss;
        DamageMultiplier = damageMultiplier;
        BleedMultiplier = bleedMultiplier;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>Nearest intersected internal volume and its health binding.</summary>
public readonly struct UnitAnatomyVolumeHit
{
    public UnitAnatomyVolume Volume { get; }
    public int VolumeIndex { get; }
    public int RegionIndex { get; }
    public int BindingIndex { get; }
    public Vector3 Position { get; }
    public float Distance { get; }

    internal UnitAnatomyVolumeHit(
        UnitAnatomyVolume volume,
        int volumeIndex,
        int regionIndex,
        int bindingIndex,
        Vector3 position,
        float distance)
    {
        Volume = volume;
        VolumeIndex = volumeIndex;
        RegionIndex = regionIndex;
        BindingIndex = bindingIndex;
        Position = position;
        Distance = distance;
    }
}
