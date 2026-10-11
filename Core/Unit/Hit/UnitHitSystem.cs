using System;
using System.Numerics;

namespace Core.Unit;

public readonly struct HitVolume
{
    public UnitHealthPartId Part { get; }
    public Vector3 Center { get; }
    public Vector3 Forward { get; }
    public Vector3 Right { get; }
    public Vector3 Radii { get; }

    public HitVolume(
        UnitHealthPartId part,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 radii)
    {
        Part = part;
        Center = center;
        Forward = forward;
        Right = right;
        Radii = radii;
    }
}

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
    public const int MaxHitVolumeCount = 10;
    public const int AimPointsPerVolume = 4;
    public const int MaxAimPointCount =
        MaxHitVolumeCount * AimPointsPerVolume;

    public static Vector3 GetEyePosition(
        UnitStore units,
        int unitIndex)
    {
        Vector3 position = units.Position[unitIndex];
        float height = MathF.Max(0.05f, units.Height[unitIndex]);

        if (units.Type[unitIndex] != UnitType.Colonist)
        {
            return position +
                new Vector3(
                    0f,
                    0f,
                    height * 0.675f);
        }

        Vector3 forward = NormalizeHorizontal(units.BodyNormal[unitIndex]);
        float width = MathF.Max(0.05f, units.Width[unitIndex]);
        float length = MathF.Max(0.05f, units.Length[unitIndex]);
        float headRadius = MathF.Min(width, length) * 0.32f;

        Vector3 headCenter = GetHeadCenter(
            position,
            forward,
            length,
            height,
            units.Posture[unitIndex]);

        return headCenter +
            new Vector3(
                0f,
                0f,
                headRadius * 1.1f * 0.35f);
    }

    private static Vector3 GetHeadCenter(
        Vector3 position,
        Vector3 bodyForward,
        float length,
        float height,
        UnitPosture posture)
    {
        float headHeight = posture switch
        {
            UnitPosture.Lying => height * 0.30f,
            UnitPosture.Crouching => height * 0.88f * 0.62f,
            _ => height * 0.88f
        };

        return position +
            bodyForward * (length * 0.53f) +
            new Vector3(0f, 0f, headHeight);
    }

    public static int GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points)
    {
        Span<HitVolume> volumes =
            stackalloc HitVolume[MaxHitVolumeCount];

        int count = BuildVolumes(
            units,
            unitIndex,
            volumes);

        if (points.Length < count)
            throw new ArgumentException(
                "Insufficient visibility point buffer.",
                nameof(points));

        for (int i = 0; i < count; i++)
        {
            ref readonly HitVolume volume = ref volumes[i];

            float rx = MathF.Max(0.001f, volume.Radii.X);
            float ry = MathF.Max(0.001f, volume.Radii.Y);
            float rz = MathF.Max(0.001f, volume.Radii.Z);
            Vector3 toSource = source - volume.Center;

            float sx = Vector3.Dot(toSource, volume.Right) / rx;
            float sy = Vector3.Dot(toSource, volume.Forward) / ry;
            float sz = toSource.Z / rz;

            // Pick an observer-facing point biased slightly upward, but keep
            // it strictly inside the same ellipsoid that receives projectile hits.
            float upperZ = sz + 0.65f;
            float length = MathF.Sqrt(
                sx * sx +
                sy * sy +
                upperZ * upperZ);

            if (length < 0.000001f)
            {
                sx = 0f;
                sy = 0f;
                upperZ = 1f;
                length = 1f;
            }

            points[i] = GetAimPointInsideVolume(
                volume,
                rx,
                ry,
                rz,
                sx / length,
                sy / length,
                upperZ / length,
                0.88f);
        }

        return count;
    }

    public static int GetAimPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points)
    {
        Span<HitVolume> volumes = stackalloc HitVolume[MaxHitVolumeCount];
        int volumeCount = BuildVolumes(units, unitIndex, volumes);
        int required = volumeCount * AimPointsPerVolume;

        if (points.Length < required)
            throw new ArgumentException("Insufficient aim point buffer.", nameof(points));

        int count = 0;

        for (int i = 0; i < volumeCount; i++)
        {
            ref readonly HitVolume volume = ref volumes[i];

            float rx = MathF.Max(0.001f, volume.Radii.X);
            float ry = MathF.Max(0.001f, volume.Radii.Y);
            float rz = MathF.Max(0.001f, volume.Radii.Z);
            Vector3 toSource = source - volume.Center;

            // Center first, then points on the source-facing side of the same
            // collision volume and an upper-biased point for ridge exposure.
            points[count++] = volume.Center;

            float sx = Vector3.Dot(toSource, volume.Right) / rx;
            float sy = Vector3.Dot(toSource, volume.Forward) / ry;
            float sz = toSource.Z / rz;
            float length = MathF.Sqrt(sx * sx + sy * sy + sz * sz);

            if (length < 0.000001f)
            {
                sx = 0f;
                sy = 1f;
                sz = 0f;
            }
            else
            {
                sx /= length;
                sy /= length;
                sz /= length;
            }

            points[count++] = GetAimPointInsideVolume(
                volume, rx, ry, rz, sx, sy, sz, 0.88f);

            points[count++] = GetAimPointInsideVolume(
                volume, rx, ry, rz, sx, sy, sz, 0.62f);

            float upperZ = sz + 0.65f;
            float upperLength = MathF.Sqrt(sx * sx + sy * sy + upperZ * upperZ);

            if (upperLength > 0.000001f)
            {
                points[count++] = GetAimPointInsideVolume(
                    volume,
                    rx,
                    ry,
                    rz,
                    sx / upperLength,
                    sy / upperLength,
                    upperZ / upperLength,
                    0.82f);
            }
            else
            {
                points[count++] = volume.Center;
            }
        }

        return count;
    }

    private static Vector3 GetAimPointInsideVolume(
        in HitVolume volume,
        float rx,
        float ry,
        float rz,
        float x,
        float y,
        float z,
        float scale)
    {
        return volume.Center +
            volume.Right * (rx * x * scale) +
            volume.Forward * (ry * y * scale) +
            new Vector3(0f, 0f, rz * z * scale);
    }

    public bool TryHitUnit(
        UnitStore units,
        int unitIndex,
        Vector3 start,
        Vector3 end,
        float projectileRadius,
        UnitHealthPartId ignoredPart,
        out UnitHitResult result)
    {
        Span<HitVolume> volumes =
            stackalloc HitVolume[MaxHitVolumeCount];

        int count =
            BuildVolumes(
                units,
                unitIndex,
                volumes);

        bool found = false;
        float bestT = 2f;
        UnitHealthPartId bestPart =
            UnitHealthPartId.None;

        for (int i = 0; i < count; i++)
        {
            ref readonly HitVolume volume =
                ref volumes[i];

            if (volume.Part == ignoredPart)
                continue;

            if (!IntersectEllipsoid(
                    start,
                    end,
                    volume,
                    projectileRadius,
                    out float t))
            {
                continue;
            }

            if (t >= bestT)
                continue;

            bestT = t;
            bestPart = volume.Part;
            found = true;
        }

        if (!found)
        {
            result = default;
            return false;
        }

        result =
            new UnitHitResult(
                bestPart,
                start +
                (end - start) *
                bestT,
                bestT);

        return true;
    }

    public static int GetHitVolumes(
        UnitStore units,
        int unitIndex,
        Span<HitVolume> volumes) =>
        BuildVolumes(units, unitIndex, volumes);

    internal static int BuildVolumes(
        UnitStore units,
        int unitIndex,
        Span<HitVolume> volumes)
    {
        if (volumes.Length < MaxHitVolumeCount)
            throw new ArgumentException(
                "Insufficient hit-volume buffer.",
                nameof(volumes));

        Vector3 position = units.Position[unitIndex];
        Vector3 forward = NormalizeHorizontal(units.BodyNormal[unitIndex]);
        Vector3 headForward = NormalizeHorizontal(units.HeadNormal[unitIndex]);
        Vector3 right = new Vector3(-forward.Y, forward.X, 0f);
        Vector3 headRight = new Vector3(-headForward.Y, headForward.X, 0f);

        float width = MathF.Max(0.05f, units.Width[unitIndex]);
        float length = MathF.Max(0.05f, units.Length[unitIndex]);
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        float unscaledHeight = height;
        float headRadius = MathF.Min(width, length) * 0.32f;
        UnitPosture posture = units.Posture[unitIndex];

        if (units.Type[unitIndex] == UnitType.Greenbob ||
            units.Type[unitIndex] == UnitType.SegmentedMonster)
        {
            volumes[0] = new HitVolume(
                UnitHealthPartId.WholeBody,
                position + new Vector3(0f, 0f, height * 0.5f),
                forward,
                right,
                new Vector3(width * 0.5f, length * 0.5f, height * 0.5f));

            return 1;
        }

        if (posture == UnitPosture.Lying)
        {
            // Prone profile: the full body is low and spread along the ground.
            volumes[0] = new HitVolume(
                UnitHealthPartId.Torso,
                position + new Vector3(0f, 0f, height * 0.22f),
                forward,
                right,
                new Vector3(width * 0.52f, length * 0.34f, height * 0.15f));

            // The head stays at the front of the body. HeadNormal rotates it;
            // it does not make the head orbit around the pawn.
            volumes[1] = new HitVolume(
                UnitHealthPartId.Head,
                GetHeadCenter(
                    position,
                    forward,
                    length,
                    height,
                    UnitPosture.Lying),
                headForward,
                headRight,
                new Vector3(headRadius, headRadius, headRadius * 1.1f));

            volumes[2] = new HitVolume(
                UnitHealthPartId.LeftArm,
                position + forward * (length * 0.08f) -
                right * (width * 0.62f) +
                new Vector3(0f, 0f, height * 0.24f),
                forward,
                right,
                new Vector3(width * 0.28f, width * 0.18f, height * 0.12f));

            volumes[3] = new HitVolume(
                UnitHealthPartId.RightArm,
                position + forward * (length * 0.08f) +
                right * (width * 0.62f) +
                new Vector3(0f, 0f, height * 0.24f),
                forward,
                right,
                new Vector3(width * 0.28f, width * 0.18f, height * 0.12f));

            volumes[4] = new HitVolume(
                UnitHealthPartId.LeftHand,
                position + forward * (length * 0.12f) -
                right * (width * 0.90f) +
                new Vector3(0f, 0f, height * 0.20f),
                forward,
                right,
                new Vector3(width * 0.20f, width * 0.15f, height * 0.08f));

            volumes[5] = new HitVolume(
                UnitHealthPartId.RightHand,
                position + forward * (length * 0.12f) +
                right * (width * 0.90f) +
                new Vector3(0f, 0f, height * 0.20f),
                forward,
                right,
                new Vector3(width * 0.20f, width * 0.15f, height * 0.08f));

            volumes[6] = new HitVolume(
                UnitHealthPartId.LeftLeg,
                position - forward * (length * 0.18f) -
                right * (width * 0.24f) +
                new Vector3(0f, 0f, height * 0.16f),
                forward,
                right,
                new Vector3(width * 0.22f, length * 0.24f, height * 0.10f));

            volumes[7] = new HitVolume(
                UnitHealthPartId.RightLeg,
                position - forward * (length * 0.18f) +
                right * (width * 0.24f) +
                new Vector3(0f, 0f, height * 0.16f),
                forward,
                right,
                new Vector3(width * 0.22f, length * 0.24f, height * 0.10f));

            volumes[8] = new HitVolume(
                UnitHealthPartId.LeftFoot,
                position - forward * (length * 0.42f) -
                right * (width * 0.24f) +
                new Vector3(0f, 0f, height * 0.08f),
                forward,
                right,
                new Vector3(width * 0.18f, length * 0.15f, height * 0.06f));

            volumes[9] = new HitVolume(
                UnitHealthPartId.RightFoot,
                position - forward * (length * 0.42f) +
                right * (width * 0.24f) +
                new Vector3(0f, 0f, height * 0.08f),
                forward,
                right,
                new Vector3(width * 0.18f, length * 0.15f, height * 0.06f));

            return MaxHitVolumeCount;
        }

        if (posture == UnitPosture.Crouching)
            height *= 0.62f;

        volumes[0] = new HitVolume(
            UnitHealthPartId.Torso,
            position + new Vector3(0f, 0f, height * 0.55f),
            forward,
            right,
            new Vector3(width * 0.52f, length * 0.28f, height * 0.30f));

        volumes[1] = new HitVolume(
            UnitHealthPartId.Head,
            GetHeadCenter(
                position,
                forward,
                length,
                unscaledHeight,
                posture),
            headForward,
            headRight,
            new Vector3(headRadius, headRadius, headRadius * 1.1f));

        volumes[2] = new HitVolume(
            UnitHealthPartId.LeftArm,
            position + forward * (length * 0.08f) -
            right * (width * 0.62f) +
            new Vector3(0f, 0f, height * 0.58f),
            forward,
            right,
            new Vector3(width * 0.28f, width * 0.18f, height * 0.28f));

        volumes[3] = new HitVolume(
            UnitHealthPartId.RightArm,
            position + forward * (length * 0.08f) +
            right * (width * 0.62f) +
            new Vector3(0f, 0f, height * 0.58f),
            forward,
            right,
            new Vector3(width * 0.28f, width * 0.18f, height * 0.28f));

        volumes[4] = new HitVolume(
            UnitHealthPartId.LeftHand,
            position + forward * (length * 0.12f) -
            right * (width * 0.90f) +
            new Vector3(0f, 0f, height * 0.42f),
            forward,
            right,
            new Vector3(width * 0.20f, width * 0.15f, height * 0.10f));

        volumes[5] = new HitVolume(
            UnitHealthPartId.RightHand,
            position + forward * (length * 0.12f) +
            right * (width * 0.90f) +
            new Vector3(0f, 0f, height * 0.42f),
            forward,
            right,
            new Vector3(width * 0.20f, width * 0.15f, height * 0.10f));

        volumes[6] = new HitVolume(
            UnitHealthPartId.LeftLeg,
            position - right * (width * 0.24f) +
            new Vector3(0f, 0f, height * 0.23f),
            forward,
            right,
            new Vector3(width * 0.22f, width * 0.20f, height * 0.28f));

        volumes[7] = new HitVolume(
            UnitHealthPartId.RightLeg,
            position + right * (width * 0.24f) +
            new Vector3(0f, 0f, height * 0.23f),
            forward,
            right,
            new Vector3(width * 0.22f, width * 0.20f, height * 0.28f));

        // The visual rig places both legs and feet behind the torso.
        volumes[8] = new HitVolume(
            UnitHealthPartId.LeftFoot,
            position - right * (width * 0.24f) -
            forward * (length * 0.42f) +
            new Vector3(0f, 0f, height * 0.07f),
            forward,
            right,
            new Vector3(width * 0.22f, length * 0.18f, height * 0.08f));

        volumes[9] = new HitVolume(
            UnitHealthPartId.RightFoot,
            position + right * (width * 0.24f) -
            forward * (length * 0.42f) +
            new Vector3(0f, 0f, height * 0.07f),
            forward,
            right,
            new Vector3(width * 0.22f, length * 0.18f, height * 0.08f));

        return MaxHitVolumeCount;
    }

    private static bool IntersectEllipsoid(
        Vector3 start,
        Vector3 end,
        in HitVolume volume,
        float projectileRadius,
        out float t)
    {
        Vector3 radii =
            volume.Radii +
            new Vector3(
                projectileRadius);

        float rx =
            MathF.Max(
                0.001f,
                radii.X);

        float ry =
            MathF.Max(
                0.001f,
                radii.Y);

        float rz =
            MathF.Max(
                0.001f,
                radii.Z);

        Vector3 startDelta =
            start -
            volume.Center;

        Vector3 endDelta =
            end -
            volume.Center;

        float sx =
            Vector3.Dot(
                startDelta,
                volume.Right) /
            rx;

        float sy =
            Vector3.Dot(
                startDelta,
                volume.Forward) /
            ry;

        float sz =
            startDelta.Z /
            rz;

        float ex =
            Vector3.Dot(
                endDelta,
                volume.Right) /
            rx;

        float ey =
            Vector3.Dot(
                endDelta,
                volume.Forward) /
            ry;

        float ez =
            endDelta.Z /
            rz;

        float dx = ex - sx;
        float dy = ey - sy;
        float dz = ez - sz;

        float a =
            dx * dx +
            dy * dy +
            dz * dz;

        float b =
            2f *
            (sx * dx +
             sy * dy +
             sz * dz);

        float c =
            sx * sx +
            sy * sy +
            sz * sz -
            1f;

        if (a < 0.0000001f)
        {
            t = 0f;
            return c <= 0f;
        }

        float discriminant =
            b * b -
            4f *
            a *
            c;

        if (discriminant < 0f)
        {
            t = 0f;
            return false;
        }

        float sqrt =
            MathF.Sqrt(
                discriminant);

        float t0 =
            (-b - sqrt) /
            (2f * a);

        float t1 =
            (-b + sqrt) /
            (2f * a);

        if (t0 > t1)
            (t0, t1) = (t1, t0);

        if (c <= 0f)
            t0 = t1;

        if (t0 < 0f)
            t0 = 0f;

        if (t0 > 1f)
        {
            t = 0f;
            return false;
        }

        t = t0;
        return true;
    }

    private static Vector3 NormalizeHorizontal(
        Vector3 value)
    {
        value.Z = 0f;

        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared < 0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }
}
