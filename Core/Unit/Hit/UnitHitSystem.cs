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
            stackalloc HitVolume[10];

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

    private static int BuildVolumes(
        UnitStore units,
        int unitIndex,
        Span<HitVolume> volumes)
    {
        Vector3 position =
            units.Position[unitIndex];

        Vector3 forward =
            NormalizeHorizontal(
                units.BodyNormal[unitIndex]);

        Vector3 headForward =
            NormalizeHorizontal(
                units.HeadNormal[unitIndex]);

        Vector3 right =
            new Vector3(
                -forward.Y,
                forward.X,
                0f);

        Vector3 headRight =
            new Vector3(
                -headForward.Y,
                headForward.X,
                0f);

        float width =
            MathF.Max(
                0.05f,
                units.Width[unitIndex]);

        float length =
            MathF.Max(
                0.05f,
                units.Length[unitIndex]);

        float height =
            MathF.Max(
                0.05f,
                units.Height[unitIndex]);

        if (units.Type[unitIndex] ==
            UnitType.Greenbob)
        {
            volumes[0] =
                new HitVolume(
                    UnitHealthPartId.WholeBody,
                    position +
                    new Vector3(
                        0f,
                        0f,
                        height * 0.5f),
                    forward,
                    right,
                    new Vector3(
                        width * 0.5f,
                        length * 0.5f,
                        height * 0.5f));

            return 1;
        }

        if (units.Type[unitIndex] ==
            UnitType.SegmentedMonster)
        {
            volumes[0] =
                new HitVolume(
                    UnitHealthPartId.WholeBody,
                    position +
                    new Vector3(
                        0f,
                        0f,
                        height * 0.5f),
                    forward,
                    right,
                    new Vector3(
                        width * 0.5f,
                        length * 0.5f,
                        height * 0.5f));

            return 1;
        }

        if (units.Posture[unitIndex] ==
            UnitPosture.Lying)
        {
            volumes[0] =
                new HitVolume(
                    UnitHealthPartId.Torso,
                    position +
                    new Vector3(
                        0f,
                        0f,
                        height * 0.45f),
                    forward,
                    right,
                    new Vector3(
                        width * 0.55f,
                        length * 0.45f,
                        height * 0.32f));

            volumes[1] =
                new HitVolume(
                    UnitHealthPartId.Head,
                    position +
                    headForward *
                    (length * 0.48f) +
                    new Vector3(
                        0f,
                        0f,
                        height * 0.58f),
                    headForward,
                    headRight,
                    new Vector3(
                        MathF.Min(width, length) * 0.30f,
                        MathF.Min(width, length) * 0.30f,
                        MathF.Min(width, length) * 0.30f));

            return 2;
        }

        float torsoZ =
            height * 0.55f;

        volumes[0] =
            new HitVolume(
                UnitHealthPartId.Torso,
                position +
                new Vector3(
                    0f,
                    0f,
                    torsoZ),
                forward,
                right,
                new Vector3(
                    width * 0.52f,
                    length * 0.28f,
                    height * 0.30f));

        float headRadius =
            MathF.Min(
                width,
                length) * 0.32f;

        volumes[1] =
            new HitVolume(
                UnitHealthPartId.Head,
                position +
                headForward *
                (length * 0.36f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.88f),
                headForward,
                headRight,
                new Vector3(
                    headRadius,
                    headRadius,
                    headRadius * 1.1f));

        volumes[2] =
            new HitVolume(
                UnitHealthPartId.LeftArm,
                position -
                right *
                (width * 0.62f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.58f),
                forward,
                right,
                new Vector3(
                    width * 0.28f,
                    width * 0.18f,
                    height * 0.28f));

        volumes[3] =
            new HitVolume(
                UnitHealthPartId.RightArm,
                position +
                right *
                (width * 0.62f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.58f),
                forward,
                right,
                new Vector3(
                    width * 0.28f,
                    width * 0.18f,
                    height * 0.28f));

        volumes[4] =
            new HitVolume(
                UnitHealthPartId.LeftHand,
                position -
                right *
                (width * 0.90f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.42f),
                forward,
                right,
                new Vector3(
                    width * 0.20f,
                    width * 0.15f,
                    height * 0.10f));

        volumes[5] =
            new HitVolume(
                UnitHealthPartId.RightHand,
                position +
                right *
                (width * 0.90f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.42f),
                forward,
                right,
                new Vector3(
                    width * 0.20f,
                    width * 0.15f,
                    height * 0.10f));

        volumes[6] =
            new HitVolume(
                UnitHealthPartId.LeftLeg,
                position -
                right *
                (width * 0.24f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.23f),
                forward,
                right,
                new Vector3(
                    width * 0.22f,
                    width * 0.20f,
                    height * 0.28f));

        volumes[7] =
            new HitVolume(
                UnitHealthPartId.RightLeg,
                position +
                right *
                (width * 0.24f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.23f),
                forward,
                right,
                new Vector3(
                    width * 0.22f,
                    width * 0.20f,
                    height * 0.28f));

        volumes[8] =
            new HitVolume(
                UnitHealthPartId.LeftFoot,
                position -
                right *
                (width * 0.24f) +
                forward *
                (length * 0.18f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.07f),
                forward,
                right,
                new Vector3(
                    width * 0.22f,
                    length * 0.18f,
                    height * 0.08f));

        volumes[9] =
            new HitVolume(
                UnitHealthPartId.RightFoot,
                position +
                right *
                (width * 0.24f) +
                forward *
                (length * 0.18f) +
                new Vector3(
                    0f,
                    0f,
                    height * 0.07f),
                forward,
                right,
                new Vector3(
                    width * 0.22f,
                    length * 0.18f,
                    height * 0.08f));

        return 10;
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
