using System;
using System.Numerics;

namespace Core.Unit;

/// <summary>
/// A small gameplay-facing hit/visibility proxy. It deliberately does not read
/// UnitBodyStore or anatomy rig transforms; only root dimensions, facing and posture
/// affect the three zones.
/// </summary>
public enum UnitGameplayZone : byte
{
    Head = 0,
    Torso = 1,
    LowerBody = 2
}

public readonly struct UnitGameplayZoneGeometry
{
    public UnitGameplayZone Zone { get; }
    public Vector3 Center { get; }
    public float HalfForward { get; }
    public float HalfSide { get; }
    public float HalfHeight { get; }
    public float Rotation { get; }
    public float BoundingRadius => MathF.Sqrt(HalfForward * HalfForward + HalfSide * HalfSide);

    public UnitGameplayZoneGeometry(
        UnitGameplayZone zone,
        Vector3 center,
        float halfForward,
        float halfSide,
        float halfHeight,
        float rotation)
    {
        Zone = zone;
        Center = center;
        HalfForward = halfForward;
        HalfSide = halfSide;
        HalfHeight = halfHeight;
        Rotation = rotation;
    }

    public Vector3 HalfExtents => new(HalfForward, HalfSide, HalfHeight);
}

/// <summary>
/// Procedural gameplay collision/visibility zones. The rig can change its local
/// geometry without changing which volumes the world uses for hits and perception.
/// </summary>
public static class UnitGameplayZoneSystem
{
    public const int ZoneCount = 3;

    public static UnitGameplayZoneGeometry GetGeometry(
        UnitStore units,
        int unitIndex,
        int zoneIndex)
    {
        if ((uint)unitIndex >= (uint)units.Capacity)
            throw new ArgumentOutOfRangeException(nameof(unitIndex));
        if ((uint)zoneIndex >= ZoneCount)
            throw new ArgumentOutOfRangeException(nameof(zoneIndex));

        UnitGameplayZone zone = (UnitGameplayZone)zoneIndex;
        Vector3 root = units.Position[unitIndex];
        Vector3 facing = units.BodyNormal[unitIndex];
        float facingLength = MathF.Sqrt(facing.X * facing.X + facing.Y * facing.Y);
        if (facingLength < 0.0001f)
        {
            facing = Vector3.UnitX;
        }
        else
        {
            facing = new Vector3(facing.X / facingLength, facing.Y / facingLength, 0f);
        }

        float width = MathF.Max(0.10f, units.Width[unitIndex]);
        float length = MathF.Max(width * 1.2f, units.Length[unitIndex]);
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        float radius = MathF.Max(0.01f, units.Radius[unitIndex]);
        UnitPosture posture = units.Posture[unitIndex];

        float forwardOffset;
        float heightFraction;
        float halfForward;
        float halfSide;
        float halfHeight;
        float horizontalScale = 1f;

        if (posture == UnitPosture.Lying)
        {
            // Prone is the deliberate "worm" pose: zones stretch along the facing
            // axis, while their vertical thickness collapses toward the ground.
            switch (zone)
            {
                case UnitGameplayZone.Head:
                    forwardOffset = length * 0.48f;
                    heightFraction = 0.16f;
                    halfForward = MathF.Max(length * 0.27f, width * 0.24f);
                    halfSide = MathF.Max(width * 0.36f, radius * 0.34f);
                    halfHeight = MathF.Max(0.035f, height * 0.065f);
                    break;
                case UnitGameplayZone.Torso:
                    forwardOffset = length * 0.04f;
                    heightFraction = 0.15f;
                    halfForward = MathF.Max(length * 0.43f, width * 0.44f);
                    halfSide = MathF.Max(width * 0.48f, radius * 0.45f);
                    halfHeight = MathF.Max(0.045f, height * 0.075f);
                    break;
                default:
                    forwardOffset = -length * 0.45f;
                    heightFraction = 0.12f;
                    halfForward = MathF.Max(length * 0.31f, width * 0.30f);
                    halfSide = MathF.Max(width * 0.40f, radius * 0.38f);
                    halfHeight = MathF.Max(0.04f, height * 0.07f);
                    break;
            }
        }
        else
        {
            switch (posture)
            {
                case UnitPosture.Crouching:
                    horizontalScale = 0.96f;
                    break;
                case UnitPosture.Sitting:
                    horizontalScale = 0.88f;
                    break;
            }

            switch (zone)
            {
                case UnitGameplayZone.Head:
                    forwardOffset = posture switch
                    {
                        UnitPosture.Crouching => length * 0.16f,
                        UnitPosture.Sitting => length * 0.12f,
                        _ => length * 0.24f
                    };
                    heightFraction = posture switch
                    {
                        UnitPosture.Crouching => 0.62f,
                        UnitPosture.Sitting => 0.68f,
                        _ => 0.82f
                    };
                    halfForward = MathF.Max(length * 0.22f, width * 0.20f);
                    halfSide = MathF.Max(width * 0.34f, radius * 0.34f);
                    halfHeight = MathF.Max(0.055f, height * 0.105f);
                    break;
                case UnitGameplayZone.Torso:
                    forwardOffset = 0f;
                    heightFraction = posture switch
                    {
                        UnitPosture.Crouching => 0.40f,
                        UnitPosture.Sitting => 0.43f,
                        _ => 0.52f
                    };
                    halfForward = MathF.Max(length * 0.37f, width * 0.40f);
                    halfSide = MathF.Max(width * 0.48f, radius * 0.52f);
                    halfHeight = MathF.Max(0.08f, height * 0.16f);
                    break;
                default:
                    forwardOffset = posture switch
                    {
                        UnitPosture.Crouching => -length * 0.12f,
                        UnitPosture.Sitting => -length * 0.06f,
                        _ => -length * 0.18f
                    };
                    heightFraction = posture switch
                    {
                        UnitPosture.Crouching => 0.19f,
                        UnitPosture.Sitting => 0.18f,
                        _ => 0.22f
                    };
                    halfForward = MathF.Max(length * 0.29f, width * 0.31f);
                    halfSide = MathF.Max(width * 0.42f, radius * 0.44f);
                    halfHeight = MathF.Max(0.06f, height * 0.11f);
                    break;
            }

            halfForward *= horizontalScale;
            halfSide *= horizontalScale;
        }

        Vector3 center = new(
            root.X + facing.X * forwardOffset,
            root.Y + facing.Y * forwardOffset,
            root.Z + height * heightFraction);
        return new UnitGameplayZoneGeometry(
            zone,
            center,
            MathF.Max(0.025f, halfForward),
            MathF.Max(0.025f, halfSide),
            MathF.Max(0.025f, halfHeight),
            MathF.Atan2(facing.Y, facing.X));
    }

    public static Vector3 GetTargetPoint(
        UnitStore units,
        int unitIndex,
        int zoneIndex,
        Vector3 source)
    {
        UnitGameplayZoneGeometry zone = GetGeometry(units, unitIndex, zoneIndex);
        Vector3 towardSource = new(
            source.X - zone.Center.X,
            source.Y - zone.Center.Y,
            0f);
        float lengthSquared = towardSource.LengthSquared();
        if (lengthSquared < 0.000001f)
        {
            towardSource = new Vector3(MathF.Cos(zone.Rotation), MathF.Sin(zone.Rotation), 0f);
        }
        else
        {
            towardSource /= MathF.Sqrt(lengthSquared);
        }

        float cos = MathF.Cos(zone.Rotation);
        float sin = MathF.Sin(zone.Rotation);
        float localForward = towardSource.X * cos + towardSource.Y * sin;
        float localSide = -towardSource.X * sin + towardSource.Y * cos;
        float ellipseDenominator = MathF.Sqrt(
            localForward * localForward / (zone.HalfForward * zone.HalfForward) +
            localSide * localSide / (zone.HalfSide * zone.HalfSide));
        float distance = ellipseDenominator > 0.0001f ? 1f / ellipseDenominator : zone.BoundingRadius;

        return new Vector3(
            zone.Center.X + towardSource.X * distance,
            zone.Center.Y + towardSource.Y * distance,
            zone.Center.Z);
    }

    public static bool ZoneAllowsPart(UnitGameplayZone zone, UnitHealthPartId part) =>
        part switch
        {
            UnitHealthPartId.WholeBody => true,
            UnitHealthPartId.Head or UnitHealthPartId.Brain or UnitHealthPartId.Neck =>
                zone == UnitGameplayZone.Head,
            UnitHealthPartId.Torso or
            UnitHealthPartId.Heart or UnitHealthPartId.LeftLung or UnitHealthPartId.RightLung or
            UnitHealthPartId.Stomach or UnitHealthPartId.Liver or
            UnitHealthPartId.LeftKidney or UnitHealthPartId.RightKidney or
            UnitHealthPartId.LeftArm or UnitHealthPartId.LeftHand or
            UnitHealthPartId.RightArm or UnitHealthPartId.RightHand =>
                zone == UnitGameplayZone.Torso,
            UnitHealthPartId.LeftLeg or UnitHealthPartId.LeftFoot or
            UnitHealthPartId.RightLeg or UnitHealthPartId.RightFoot =>
                zone == UnitGameplayZone.LowerBody,
            _ => false
        };

    public static bool RegionNameMatches(UnitGameplayZone zone, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        string name = id.ToLowerInvariant();
        bool lowerSegment =
            name.Contains("leg") || name.Contains("foot") ||
            name.Contains("hip") || name.Contains("lower") ||
            name.Contains("rear") || name.Contains("front");

        return zone switch
        {
            UnitGameplayZone.Head =>
                name.Contains("head") || name.Contains("brain") ||
                name.Contains("skull") || name.Contains("neck"),
            UnitGameplayZone.Torso =>
                !lowerSegment &&
                (name.Contains("torso") || name.Contains("body") ||
                 name.Contains("chest") || name.Contains("abdomen") ||
                 name.Contains("middle") || name.Contains("core")),
            UnitGameplayZone.LowerBody => lowerSegment,
            _ => false
        };
    }
}
