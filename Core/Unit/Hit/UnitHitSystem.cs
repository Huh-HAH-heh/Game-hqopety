using System;
using System.Numerics;

namespace Core.Unit;

public readonly struct UnitHitResult
{
    public UnitHealthPartId Part { get; }
    public Vector3 Position { get; }
    public float T { get; }

    public UnitHitResult(
        UnitHealthPartId part,
        Vector3 position,
        float t)
    {
        Part = part;
        Position = position;
        T = t;
    }
}

public sealed class UnitHitSystem
{
    public const int MaxAimPointCount = 1;

    public static Vector3 GetEyePosition(
        UnitStore units,
        int unitIndex,
        UnitBodyStore? bodies = null)
    {
        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        return position + new Vector3(0f, 0f, height * 0.8f);
    }

    public static int GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null)
    {
        return GetUnitPoint(units, unitIndex, points);
    }

    public static int GetAimPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null)
    {
        return GetUnitPoint(units, unitIndex, points);
    }

    private static int GetUnitPoint(
        UnitStore units,
        int unitIndex,
        Span<Vector3> points)
    {
        if (points.Length < 1)
            throw new ArgumentException("Insufficient point buffer.", nameof(points));

        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        points[0] = position + new Vector3(0f, 0f, height * 0.5f);
        return 1;
    }

    public bool TryHitUnit(
        UnitStore units,
        int unitIndex,
        Vector3 start,
        Vector3 end,
        float projectileRadius,
        UnitHealthPartId ignoredPart,
        out UnitHitResult result,
        UnitBodyStore? bodies = null)
    {
        Vector3 target = units.Position[unitIndex];
        target.Z += MathF.Max(0.05f, units.Height[unitIndex]) * 0.5f;

        Vector3 line = end - start;
        float lengthSquared = line.LengthSquared();
        float t = lengthSquared > 0.000001f
            ? Math.Clamp(Vector3.Dot(target - start, line) / lengthSquared, 0f, 1f)
            : 0f;

        Vector3 closest = start + line * t;
        float radius = MathF.Max(0f, projectileRadius);
        if ((closest - target).LengthSquared() > radius * radius)
        {
            result = default;
            return false;
        }

        UnitHealthPartId part = units.Type[unitIndex] == UnitType.Colonist
            ? UnitHealthPartId.Torso
            : UnitHealthPartId.WholeBody;

        if (part == ignoredPart)
        {
            result = default;
            return false;
        }

        result = new UnitHitResult(part, closest, t);
        return true;
    }
}
