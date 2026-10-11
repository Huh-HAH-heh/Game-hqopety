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
    // Colonists have 19 modeled shapes in UnitBodyCatalog. Every collision
    // volume now follows one of those shapes instead of being placed separately.
    public const int MaxHitVolumeCount = 19;
    public const int AimPointsPerVolume = 4;
    public const int MaxAimPointCount =
        MaxHitVolumeCount * AimPointsPerVolume;

    public static Vector3 GetEyePosition(
        UnitStore units,
        int unitIndex,
        UnitBodyStore? bodies = null)
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

        if (bodies != null)
        {
            int start = units.BodyStart[unitIndex];
            int end = Math.Min(
                start + Math.Max(0, (int)units.BodyCount[unitIndex]),
                bodies.Count);
            Vector3[] partPositions = bodies.WorldPosition;
            Vector3[] partScales = bodies.Scale;
            BodyPartRole[] roles = bodies.Role;

            for (int part = Math.Max(0, start); part < end; part++)
            {
                if (roles[part] != BodyPartRole.Head)
                    continue;

                float headHeight = GetHeadHeight(
                    height,
                    units.Posture[unitIndex]);

                return new Vector3(
                    partPositions[part].X,
                    partPositions[part].Y,
                    position.Z + headHeight +
                    MathF.Abs(partScales[part].Z) * 0.35f);
            }
        }

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

    private static float GetHeadHeight(
        float height,
        UnitPosture posture) =>
        posture switch
        {
            UnitPosture.Lying => height * 0.30f,
            UnitPosture.Crouching => height * 0.88f * 0.62f,
            _ => height * 0.88f
        };

    private static Vector3 GetHeadCenter(
        Vector3 position,
        Vector3 bodyForward,
        float length,
        float height,
        UnitPosture posture)
    {
        return position +
            bodyForward * (length * 0.53f) +
            new Vector3(0f, 0f, GetHeadHeight(height, posture));
    }

    public static int GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null)
    {
        Span<HitVolume> volumes =
            stackalloc HitVolume[MaxHitVolumeCount];

        int count = BuildVolumes(
            units,
            unitIndex,
            volumes,
            bodies);

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
            float upperZ = sz + 1.0f;
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
        Span<Vector3> points,
        UnitBodyStore? bodies = null)
    {
        Span<HitVolume> volumes = stackalloc HitVolume[MaxHitVolumeCount];
        int volumeCount = BuildVolumes(units, unitIndex, volumes, bodies);
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

            float upperZ = sz + 1.0f;
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
        out UnitHitResult result,
        UnitBodyStore? bodies = null)
    {
        Span<HitVolume> volumes =
            stackalloc HitVolume[MaxHitVolumeCount];

        int count =
            BuildVolumes(
                units,
                unitIndex,
                volumes,
                bodies);

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
        Span<HitVolume> volumes,
        UnitBodyStore? bodies = null) =>
        BuildVolumes(units, unitIndex, volumes, bodies);

    internal static int BuildVolumes(
        UnitStore units,
        int unitIndex,
        Span<HitVolume> volumes,
        UnitBodyStore? bodies = null)
    {
        if (volumes.Length < MaxHitVolumeCount)
            throw new ArgumentException(
                "Insufficient hit-volume buffer.",
                nameof(volumes));

        if (bodies != null)
        {
            int bodyVolumeCount = BuildBodyVolumes(
                units,
                bodies,
                unitIndex,
                volumes);

            if (bodyVolumeCount > 0)
                return bodyVolumeCount;
        }

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

            return 10;
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

        return 10;
    }

    private static int BuildBodyVolumes(
        UnitStore units,
        UnitBodyStore bodies,
        int unitIndex,
        Span<HitVolume> volumes)
    {
        int start = units.BodyStart[unitIndex];
        int count = Math.Max(0, (int)units.BodyCount[unitIndex]);
        if (start < 0 || count == 0 || start >= bodies.Count)
            return 0;

        count = Math.Min(count, bodies.Count - start);
        if (count > volumes.Length)
            throw new InvalidOperationException(
                $"Body has {count} shapes, but hit-volume capacity is {volumes.Length}.");

        Vector3 rootPosition = units.Position[unitIndex];
        Vector3 bodyForward = NormalizeHorizontal(units.BodyNormal[unitIndex]);
        Vector3 bodyRight = new(-bodyForward.Y, bodyForward.X, 0f);
        float height = MathF.Max(0.05f, units.Height[unitIndex]);
        UnitPosture posture = units.Posture[unitIndex];

        Vector3[] positions = bodies.WorldPosition;
        Vector3[] scales = bodies.Scale;
        float[] rotations = bodies.WorldRotation;
        BodyPartRole[] roles = bodies.Role;
        UnitType type = units.Type[unitIndex];

        for (int offset = 0; offset < count; offset++)
        {
            int partIndex = start + offset;
            BodyPartRole role = roles[partIndex];
            UnitHealthPartId partId = GetHealthPartId(
                units,
                bodies,
                unitIndex,
                partIndex,
                start,
                bodyRight);

            float rotation = rotations[partIndex];
            Vector3 forward = new(
                MathF.Cos(rotation),
                MathF.Sin(rotation),
                0f);
            Vector3 right = new(-forward.Y, forward.X, 0f);
            Vector3 scale = scales[partIndex];

            float headRadius = MathF.Min(
                MathF.Max(0.05f, units.Width[unitIndex]),
                MathF.Max(0.05f, units.Length[unitIndex])) * 0.32f;

            GetVerticalHitShape(
                type,
                posture,
                rootPosition.Z,
                height,
                headRadius,
                role,
                scale,
                out float centerZ,
                out float radiusZ);

            Vector3 position = positions[partIndex];
            volumes[offset] = new HitVolume(
                partId,
                new Vector3(position.X, position.Y, centerZ),
                forward,
                right,
                new Vector3(
                    MathF.Max(0.012f, MathF.Abs(scale.Y)),
                    MathF.Max(0.012f, MathF.Abs(scale.X)),
                    MathF.Max(0.025f, radiusZ)));
        }

        return count;
    }

    private static UnitHealthPartId GetHealthPartId(
        UnitStore units,
        UnitBodyStore bodies,
        int unitIndex,
        int partIndex,
        int bodyStart,
        Vector3 bodyRight)
    {
        BodyPartRole[] roles = bodies.Role;
        BodyPartRole role = roles[partIndex];

        if (units.Type[unitIndex] != UnitType.Colonist)
            return UnitHealthPartId.WholeBody;

        return role switch
        {
            BodyPartRole.Head => UnitHealthPartId.Head,
            BodyPartRole.Torso or BodyPartRole.Neck => UnitHealthPartId.Torso,
            BodyPartRole.UpperArm or
            BodyPartRole.Elbow or
            BodyPartRole.Forearm =>
                IsRightLimb(units, bodies, unitIndex, partIndex, bodyStart, bodyRight)
                    ? UnitHealthPartId.RightArm
                    : UnitHealthPartId.LeftArm,
            BodyPartRole.Hand =>
                IsRightLimb(units, bodies, unitIndex, partIndex, bodyStart, bodyRight)
                    ? UnitHealthPartId.RightHand
                    : UnitHealthPartId.LeftHand,
            BodyPartRole.Thigh or
            BodyPartRole.Knee or
            BodyPartRole.Shin =>
                IsRightLimb(units, bodies, unitIndex, partIndex, bodyStart, bodyRight)
                    ? UnitHealthPartId.RightLeg
                    : UnitHealthPartId.LeftLeg,
            BodyPartRole.Foot =>
                IsRightLimb(units, bodies, unitIndex, partIndex, bodyStart, bodyRight)
                    ? UnitHealthPartId.RightFoot
                    : UnitHealthPartId.LeftFoot,
            _ => UnitHealthPartId.Torso
        };
    }

    private static bool IsRightLimb(
        UnitStore units,
        UnitBodyStore bodies,
        int unitIndex,
        int partIndex,
        int bodyStart,
        Vector3 bodyRight)
    {
        int[] parents = bodies.Parent;
        BodyPartRole[] roles = bodies.Role;
        Vector3[] positions = bodies.WorldPosition;
        int ancestor = partIndex;

        while (ancestor >= bodyStart)
        {
            BodyPartRole role = roles[ancestor];
            if (role == BodyPartRole.UpperArm ||
                role == BodyPartRole.Thigh)
            {
                Vector3 offset = positions[ancestor] - units.Position[unitIndex];
                float side = Vector3.Dot(offset, bodyRight);
                if (MathF.Abs(side) > 0.0001f)
                    return side > 0f;
            }

            ancestor = parents[ancestor];
        }

        Vector3 fallbackOffset =
            positions[partIndex] - units.Position[unitIndex];
        return Vector3.Dot(fallbackOffset, bodyRight) >= 0f;
    }

    private static void GetVerticalHitShape(
        UnitType type,
        UnitPosture posture,
        float rootZ,
        float unitHeight,
        float hitHeadRadius,
        BodyPartRole role,
        Vector3 scale,
        out float centerZ,
        out float radiusZ)
    {
        if (type != UnitType.Colonist)
        {
            centerZ = rootZ + unitHeight * 0.5f;
            radiusZ = unitHeight * 0.5f;
            return;
        }

        bool lying = posture == UnitPosture.Lying;
        float poseHeight = posture == UnitPosture.Crouching
            ? unitHeight * 0.62f
            : unitHeight;

        float centerFactor;
        float radiusFactor;

        switch (role)
        {
            case BodyPartRole.Head:
                centerZ = rootZ + GetHeadHeight(unitHeight, posture);
                radiusZ = MathF.Max(0.025f, hitHeadRadius * 1.1f);
                return;

            case BodyPartRole.Torso:
                centerFactor = lying ? 0.22f : 0.55f;
                radiusFactor = lying ? 0.15f : 0.30f;
                break;

            case BodyPartRole.Neck:
                centerFactor = posture switch
                {
                    UnitPosture.Lying => 0.26f,
                    UnitPosture.Crouching => 0.46f,
                    _ => 0.72f
                };
                radiusFactor = 0.12f;
                break;

            case BodyPartRole.UpperArm:
            case BodyPartRole.Elbow:
            case BodyPartRole.Forearm:
                centerFactor = lying ? 0.24f : 0.58f;
                radiusFactor = lying ? 0.12f : 0.28f;
                break;

            case BodyPartRole.Hand:
                centerFactor = lying ? 0.20f : 0.42f;
                radiusFactor = lying ? 0.08f : 0.10f;
                break;

            case BodyPartRole.Thigh:
            case BodyPartRole.Knee:
            case BodyPartRole.Shin:
                centerFactor = lying ? 0.16f : 0.23f;
                radiusFactor = lying ? 0.10f : 0.28f;
                break;

            case BodyPartRole.Foot:
                centerFactor = lying ? 0.08f : 0.07f;
                radiusFactor = lying ? 0.06f : 0.08f;
                break;

            default:
                centerFactor = lying ? 0.22f : 0.55f;
                radiusFactor = lying ? 0.15f : 0.30f;
                break;
        }

        centerZ = rootZ + poseHeight * centerFactor;
        radiusZ = poseHeight * radiusFactor;
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
