using System;
using System.Diagnostics;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

public enum VisionState : byte
{
    Self = 0,
    OutOfRange = 1,
    OutsideFieldOfView = 2,
    HiddenByTerrain = 3,
    Visible = 4
}

public readonly struct VisionCheck
{
    public VisionState State { get; }
    public int VisiblePoint { get; }
    public Vector3 ObserverEye { get; }
    public Vector3 TargetCenter { get; }
    public Vector3 VisibleTargetPoint { get; }
    public Vector3 BlockingPoint { get; }

    public bool IsVisible =>
        State == VisionState.Visible;

    public VisionCheck(
        VisionState state,
        int visiblePoint,
        Vector3 observerEye,
        Vector3 targetCenter,
        Vector3 visibleTargetPoint,
        Vector3 blockingPoint)
    {
        State = state;
        VisiblePoint = visiblePoint;
        ObserverEye = observerEye;
        TargetCenter = targetCenter;
        VisibleTargetPoint = visibleTargetPoint;
        BlockingPoint = blockingPoint;
    }
}

public sealed class VisionSystem
{
    public const int VisibilityPointCount = 7;

    private const float UpdateInterval = 0.10f;
    private const float Epsilon = 0.02f;
    private const int SpatialCellSize = 16;

    private float _updateTimer;
    private int[] _spatialCellHeads = Array.Empty<int>();
    private int[] _nextInCell = Array.Empty<int>();
    private int _spatialCellsX;
    private int _spatialCellsY;
    private int _spatialCellCount;
    private int _candidatePairCount;
    private int _lineOfSightChecks;

    public double LastUpdateMilliseconds { get; private set; }
    public int LastCandidatePairs { get; private set; }
    public int LastLineOfSightChecks { get; private set; }
    public int LastVisibleTargetCount { get; private set; }
    public long UpdateCount { get; private set; }
    private int[] _visibleStarts;
    private int[] _visibleCounts;
    private int[] _visibleTargets;
    private int _visibleTargetCount;

    public VisionSystem(
        int initialUnitCapacity = 1024)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialUnitCapacity));

        _visibleStarts =
            new int[initialUnitCapacity];

        _visibleCounts =
            new int[initialUnitCapacity];

        _nextInCell =
            new int[initialUnitCapacity];

        _visibleTargets =
            new int[Math.Max(64, initialUnitCapacity * 4)];
    }

    public ReadOnlySpan<int> GetVisibleTargets(
        int unitIndex)
    {
        if (unitIndex < 0 ||
            unitIndex >= _visibleCounts.Length)
        {
            return ReadOnlySpan<int>.Empty;
        }

        int count =
            _visibleCounts[unitIndex];

        if (count <= 0)
            return ReadOnlySpan<int>.Empty;

        return _visibleTargets.AsSpan(
            _visibleStarts[unitIndex],
            count);
    }

    public void Update(
        UnitStore units,
        WorldMap worldMap,
        float deltaTime,
        UnitHealthStore? health = null)
    {
        if (deltaTime < 0f)
            return;

        if (units.ActiveCount == 0)
        {
            Array.Clear(_visibleCounts);
            _visibleTargetCount = 0;
            _updateTimer = 0f;
            return;
        }

        _updateTimer += deltaTime;

        if (_updateTimer < UpdateInterval)
            return;

        _updateTimer %= UpdateInterval;

        long started = Stopwatch.GetTimestamp();
        EnsureUnitCapacity(units.Capacity);
        EnsureSpatialCapacity(worldMap);

        Array.Clear(_visibleCounts);
        Array.Fill(_spatialCellHeads, -1, 0, _spatialCellCount);
        _visibleTargetCount = 0;
        _candidatePairCount = 0;
        _lineOfSightChecks = 0;

        ReadOnlySpan<int> active = units.ActiveIndices;
        Vector3[] positions = units.Position;
        float[] ranges = units.ViewRange;

        // Build an allocation-free spatial hash once per vision tick.
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            _nextInCell[unit] = -1;

            if (health != null && health.OverallHitPoints[unit] <= 0f)
                continue;

            Vector3 position = positions[unit];
            int cellX = Math.Clamp(
                (int)MathF.Floor(position.X / SpatialCellSize),
                0,
                _spatialCellsX - 1);
            int cellY = Math.Clamp(
                (int)MathF.Floor(position.Y / SpatialCellSize),
                0,
                _spatialCellsY - 1);
            int cell = cellX + cellY * _spatialCellsX;

            _nextInCell[unit] = _spatialCellHeads[cell];
            _spatialCellHeads[cell] = unit;
        }

        for (int i = 0; i < active.Length; i++)
        {
            int observer = active[i];

            if (health != null && health.OverallHitPoints[observer] <= 0f)
                continue;

            int start = _visibleTargetCount;
            _visibleStarts[observer] = start;

            Vector3 position = positions[observer];
            float range = MathF.Max(0f, ranges[observer]);

            int minCellX = Math.Clamp(
                (int)MathF.Floor((position.X - range) / SpatialCellSize),
                0,
                _spatialCellsX - 1);
            int maxCellX = Math.Clamp(
                (int)MathF.Floor((position.X + range) / SpatialCellSize),
                0,
                _spatialCellsX - 1);
            int minCellY = Math.Clamp(
                (int)MathF.Floor((position.Y - range) / SpatialCellSize),
                0,
                _spatialCellsY - 1);
            int maxCellY = Math.Clamp(
                (int)MathF.Floor((position.Y + range) / SpatialCellSize),
                0,
                _spatialCellsY - 1);

            for (int cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (int cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    int cell = cellX + cellY * _spatialCellsX;

                    for (int target = _spatialCellHeads[cell];
                         target >= 0;
                         target = _nextInCell[target])
                    {
                        if (observer == target)
                            continue;

                        _candidatePairCount++;

                        VisionCheck check = Evaluate(
                            units,
                            worldMap,
                            observer,
                            target);

                        if (check.IsVisible)
                            AppendVisibleTarget(target);
                    }
                }
            }

            _visibleCounts[observer] =
                _visibleTargetCount - start;
        }

        LastUpdateMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LastCandidatePairs = _candidatePairCount;
        LastLineOfSightChecks = _lineOfSightChecks;
        LastVisibleTargetCount = _visibleTargetCount;
        UpdateCount++;
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_visibleCounts.Length)
            return;

        _visibleStarts[unitIndex] = 0;
        _visibleCounts[unitIndex] = 0;
        _updateTimer = UpdateInterval;
    }

    private void EnsureSpatialCapacity(WorldMap worldMap)
    {
        int cellsX = Math.Max(
            1,
            (worldMap.TileWidth + SpatialCellSize - 1) / SpatialCellSize);
        int cellsY = Math.Max(
            1,
            (worldMap.TileHeight + SpatialCellSize - 1) / SpatialCellSize);
        int cells = checked(cellsX * cellsY);

        if (_spatialCellsX == cellsX &&
            _spatialCellsY == cellsY &&
            _spatialCellHeads.Length >= cells)
        {
            return;
        }

        _spatialCellsX = cellsX;
        _spatialCellsY = cellsY;
        _spatialCellCount = cells;
        _spatialCellHeads = new int[cells];
    }

    public VisionCheck Evaluate(
        UnitStore units,
        WorldMap worldMap,
        int observerIndex,
        int targetIndex)
    {
        if (observerIndex == targetIndex)
        {
            Vector3 eye =
                GetEyePosition(
                    units,
                    observerIndex);

            return new VisionCheck(
                VisionState.Self,
                -1,
                eye,
                units.Position[targetIndex],
                units.Position[targetIndex],
                Vector3.Zero);
        }

        Vector3 observerPosition =
            units.Position[observerIndex];

        Vector3 targetCenter =
            units.Position[targetIndex];

        Vector2 toTarget =
            new Vector2(
                targetCenter.X - observerPosition.X,
                targetCenter.Y - observerPosition.Y);

        float distanceSquared =
            toTarget.LengthSquared();

        float range =
            units.ViewRange[observerIndex];

        if (distanceSquared >
            range * range)
        {
            return new VisionCheck(
                VisionState.OutOfRange,
                -1,
                GetEyePosition(
                    units,
                    observerIndex),
                targetCenter,
                targetCenter,
                Vector3.Zero);
        }

        float distance =
            MathF.Sqrt(
                distanceSquared);

        if (distance < 0.0001f)
        {
            return new VisionCheck(
                VisionState.Visible,
                0,
                GetEyePosition(
                    units,
                    observerIndex),
                targetCenter,
                targetCenter,
                Vector3.Zero);
        }

        toTarget /= distance;

        Vector3 forward =
            NormalizeHorizontal(
                units.HeadNormal[observerIndex]);

        float dot =
            forward.X * toTarget.X +
            forward.Y * toTarget.Y;

        float halfFovRadians =
            units.FieldOfView[observerIndex] *
            MathF.PI /
            360f;

        if (dot <
            MathF.Cos(halfFovRadians))
        {
            return new VisionCheck(
                VisionState.OutsideFieldOfView,
                -1,
                GetEyePosition(
                    units,
                    observerIndex),
                targetCenter,
                targetCenter,
                Vector3.Zero);
        }

        Vector3 observerEye =
            GetEyePosition(
                units,
                observerIndex);

        Span<Vector3> points =
            stackalloc Vector3[
                VisibilityPointCount];

        GetVisibilityPoints(
            units,
            targetIndex,
            points);

        Vector3 firstBlockingPoint =
            Vector3.Zero;

        for (int i = 0;
             i < points.Length;
             i++)
        {
            Vector3 point =
                points[i];

            _lineOfSightChecks++;

            if (HasLineOfSight(
                    worldMap,
                    observerEye,
                    point,
                    out Vector3 blockingPoint))
            {
                return new VisionCheck(
                    VisionState.Visible,
                    i,
                    observerEye,
                    targetCenter,
                    point,
                    Vector3.Zero);
            }

            if (i == 0)
            {
                firstBlockingPoint =
                    blockingPoint;
            }
        }

        return new VisionCheck(
            VisionState.HiddenByTerrain,
            -1,
            observerEye,
            targetCenter,
            targetCenter,
            firstBlockingPoint);
    }

    public Vector3 GetEyePosition(
        UnitStore units,
        int unitIndex)
    {
        Vector3 position =
            units.Position[unitIndex];

        Vector3 headNormal =
            NormalizeHorizontal(
                units.HeadNormal[unitIndex]);

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

        float bodyLength =
            units.Posture[unitIndex] ==
            UnitPosture.Lying
                ? length
                : length * 0.60f;

        float headRadius =
            MathF.Min(
                width,
                length) *
            0.32f;

        float headDistance =
            bodyLength * 0.5f +
            headRadius * 1.15f;

        float eyeHeight =
            units.Posture[unitIndex] switch
            {
                UnitPosture.Lying => height * 0.55f,
                UnitPosture.Crouching => height * 0.62f,
                _ => height
            };

        return
            position +
            headNormal * headDistance +
            new Vector3(
                0f,
                0f,
                eyeHeight);
    }

    public void GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Span<Vector3> points)
    {
        if (points.Length <
            VisibilityPointCount)
        {
            throw new ArgumentException(
                "Недостаточно места для точек видимости.",
                nameof(points));
        }

        Vector3 center =
            units.Position[unitIndex];

        Vector3 forward =
            NormalizeHorizontal(
                units.HeadNormal[unitIndex]);

        Vector3 right =
            new Vector3(
                -forward.Y,
                forward.X,
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

        bool lying =
            units.Posture[unitIndex] ==
            UnitPosture.Lying;

        float heightFactor =
            units.Posture[unitIndex] ==
            UnitPosture.Crouching
                ? 0.62f
                : 1f;

        height *= heightFactor;

        float halfWidth =
            width * 0.5f;

        float halfLength =
            lying
                ? length * 0.5f
                : length * 0.30f;

        points[0] =
            center +
            forward *
            (halfLength + width * 0.12f) +
            new Vector3(
                0f,
                0f,
                height);

        points[1] =
            center +
            forward *
            (halfLength * 0.45f) +
            new Vector3(
                0f,
                0f,
                height * 0.72f);

        points[2] =
            center +
            forward *
            (halfLength * 0.35f) +
            right *
            halfWidth * 0.85f +
            new Vector3(
                0f,
                0f,
                height * 0.66f);

        points[3] =
            center +
            forward *
            (halfLength * 0.35f) -
            right *
            halfWidth * 0.85f +
            new Vector3(
                0f,
                0f,
                height * 0.66f);

        points[4] =
            center -
            forward *
            (halfLength * 0.25f) +
            new Vector3(
                0f,
                0f,
                height * 0.45f);

        points[5] =
            center -
            forward *
            (halfLength * 0.15f) +
            right *
            halfWidth * 0.90f +
            new Vector3(
                0f,
                0f,
                height * 0.22f);

        points[6] =
            center -
            forward *
            (halfLength * 0.15f) -
            right *
            halfWidth * 0.90f +
            new Vector3(
                0f,
                0f,
                height * 0.22f);
    }

    public bool HasLineOfSight(
        WorldMap worldMap,
        Vector3 start,
        Vector3 end,
        out Vector3 blockingPoint)
    {
        blockingPoint = Vector3.Zero;

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float dz = end.Z - start.Z;

        float horizontalLengthSquared =
            dx * dx + dy * dy;

        if (horizontalLengthSquared < 0.000001f)
        {
            int verticalCellX = (int)MathF.Floor(end.X);
            int verticalCellY = (int)MathF.Floor(end.Y);

            float lowZ = MathF.Min(start.Z, end.Z);
            float highZ = MathF.Max(start.Z, end.Z);

            int firstZ = Math.Max(0, (int)MathF.Floor(lowZ));
            int lastZ = Math.Min(
                worldMap.LayerCount - 1,
                (int)MathF.Floor(highZ));

            for (int z = firstZ; z <= lastZ; z++)
            {
                if (worldMap.GetMaterialId(verticalCellX, verticalCellY, z) == 0)
                    continue;

                bool intersects =
                    MathF.Abs(dz) < 0.000001f
                        ? start.Z >= z - Epsilon &&
                          start.Z < z + 1f - Epsilon
                        : highZ > z + Epsilon &&
                          lowZ < z + 1f - Epsilon;

                if (!intersects)
                    continue;

                float boundaryZ =
                    dz < 0f ? z + 1f : z;

                float hitT =
                    MathF.Abs(dz) < 0.000001f
                        ? 0f
                        : Math.Clamp(
                            (boundaryZ - start.Z) / dz,
                            0f,
                            1f);

                blockingPoint =
                    start + (end - start) * hitT;

                return false;
            }

            return true;
        }

        int cellX =
            (int)MathF.Floor(start.X);

        int cellY =
            (int)MathF.Floor(start.Y);

        int targetCellX =
            (int)MathF.Floor(end.X);

        int targetCellY =
            (int)MathF.Floor(end.Y);

        int stepX = Math.Sign(dx);
        int stepY = Math.Sign(dy);

        if (stepX < 0 &&
            MathF.Abs(start.X - MathF.Round(start.X)) < Epsilon)
        {
            cellX--;
        }

        if (stepY < 0 &&
            MathF.Abs(start.Y - MathF.Round(start.Y)) < Epsilon)
        {
            cellY--;
        }

        float tDeltaX =
            stepX == 0
                ? float.PositiveInfinity
                : MathF.Abs(1f / dx);

        float tDeltaY =
            stepY == 0
                ? float.PositiveInfinity
                : MathF.Abs(1f / dy);

        float nextBoundaryX =
            stepX > 0 ? cellX + 1f : cellX;

        float nextBoundaryY =
            stepY > 0 ? cellY + 1f : cellY;

        float tMaxX =
            stepX == 0
                ? float.PositiveInfinity
                : (nextBoundaryX - start.X) / dx;

        float tMaxY =
            stepY == 0
                ? float.PositiveInfinity
                : (nextBoundaryY - start.Y) / dy;

        float segmentStart = 0f;

        while (segmentStart < 1f)
        {
            float segmentEnd =
                MathF.Min(
                    1f,
                    MathF.Min(tMaxX, tMaxY));

            if (segmentEnd <= segmentStart)
                break;

            float checkEnd =
                segmentEnd >= 1f
                    ? 1f - 0.0001f
                    : segmentEnd;

            if (checkEnd > segmentStart)
            {
                float z0 = start.Z + dz * segmentStart;
                float z1 = start.Z + dz * checkEnd;
                float lowZ = MathF.Min(z0, z1);
                float highZ = MathF.Max(z0, z1);

                int firstZ = Math.Max(0, (int)MathF.Floor(lowZ));
                int lastZ = Math.Min(
                    worldMap.LayerCount - 1,
                    (int)MathF.Floor(highZ));

                for (int z = firstZ; z <= lastZ; z++)
                {
                    if (worldMap.GetMaterialId(cellX, cellY, z) == 0)
                        continue;

                    bool intersects =
                        MathF.Abs(dz) < 0.000001f
                            ? z0 >= z - Epsilon &&
                              z0 < z + 1f - Epsilon
                            : highZ > z + Epsilon &&
                              lowZ < z + 1f - Epsilon;

                    if (!intersects)
                        continue;

                    float hitT;

                    if (MathF.Abs(dz) < 0.000001f)
                    {
                        hitT = segmentStart;
                    }
                    else
                    {
                        float entryZ = dz > 0f ? z : z + 1f;
                        hitT = Math.Clamp(
                            (entryZ - start.Z) / dz,
                            segmentStart,
                            checkEnd);
                    }

                    blockingPoint =
                        start + (end - start) * hitT;

                    return false;
                }
            }

            if (segmentEnd >= 1f)
                break;

            if (tMaxX < tMaxY)
            {
                cellX += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY < tMaxX)
            {
                cellY += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                cellX += stepX;
                cellY += stepY;
                tMaxX += tDeltaX;
                tMaxY += tDeltaY;
            }

            if (cellX < 0 ||
                cellY < 0 ||
                cellX >= worldMap.TileWidth ||
                cellY >= worldMap.TileHeight)
            {
                break;
            }

            if (cellX == targetCellX &&
                cellY == targetCellY &&
                segmentEnd >= 1f)
            {
                break;
            }

            segmentStart = segmentEnd;
        }

        return true;
    }

    private void AppendVisibleTarget(
        int targetIndex)
    {
        EnsureVisibleTargetCapacity(
            _visibleTargetCount + 1);

        _visibleTargets[
            _visibleTargetCount++] =
            targetIndex;
    }

    private void EnsureUnitCapacity(
        int capacity)
    {
        if (_visibleStarts.Length >= capacity)
            return;

        Array.Resize(
            ref _visibleStarts,
            capacity);

        Array.Resize(
            ref _visibleCounts,
            capacity);

        Array.Resize(
            ref _nextInCell,
            capacity);
    }

    private void EnsureVisibleTargetCapacity(
        int required)
    {
        if (required <=
            _visibleTargets.Length)
        {
            return;
        }

        int newCapacity =
            _visibleTargets.Length * 2;

        if (newCapacity < required)
            newCapacity = required;

        Array.Resize(
            ref _visibleTargets,
            newCapacity);
    }

    private static Vector3 NormalizeHorizontal(
        Vector3 value)
    {
        value.Z = 0f;

        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared <
            0.000001f)
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
