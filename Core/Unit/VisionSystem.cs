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
    private const float LayerEpsilon = Epsilon * WorldMap.HeightUnitsPerMeter;
    private const int VisibilityMemoryUpdates = 60;
    private const int VisibilitySectorCount = 8;
    private const int CandidatesPerSector = 2;
    private const int MaxTargetCandidatesPerObserver = 64;
    private const int TargetScanStride = 97;
    private const int TargetObserverOffset = 37;

    private float _updateTimer;
    private int _candidatePairCount;
    private int _lineOfSightChecks;
    private int _targetEvaluationCount;
    private int[] _lastVisibleUpdate = Array.Empty<int>();
    private int[] _sectorTargetCursors = Array.Empty<int>();
    private int _visibilityCapacity;
    private int _visionUpdateSequence;
    private long _visibilityTerrainVersion = long.MinValue;

    public double LastUpdateMilliseconds { get; private set; }
    public int LastCandidatePairs { get; private set; }
    public int LastActiveCandidatesScanned { get; private set; }
    public int LastTargetEvaluations { get; private set; }
    public int LastLineOfSightChecks { get; private set; }
    public int LastVisibleTargetCount { get; private set; }
    public int LastVisibilityMemoryEntriesScanned { get; private set; }
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
            LastActiveCandidatesScanned = 0;
            LastVisibilityMemoryEntriesScanned = 0;
            _updateTimer = 0f;
            return;
        }

        _updateTimer += deltaTime;

        if (_updateTimer < UpdateInterval)
            return;

        _updateTimer %= UpdateInterval;

        long started = Stopwatch.GetTimestamp();
        EnsureUnitCapacity(units.Capacity);

        if (_visionUpdateSequence >= int.MaxValue - VisibilityMemoryUpdates)
        {
            Array.Clear(_lastVisibleUpdate);
            _visionUpdateSequence = 0;
        }

        _visionUpdateSequence++;

        if (_visibilityTerrainVersion != worldMap.TerrainVersion)
        {
            Array.Clear(_lastVisibleUpdate);
            _visibilityTerrainVersion = worldMap.TerrainVersion;
        }

        Array.Clear(_visibleCounts);
        _visibleTargetCount = 0;
        _candidatePairCount = 0;
        _lineOfSightChecks = 0;
        _targetEvaluationCount = 0;
        LastActiveCandidatesScanned = 0;
        LastVisibilityMemoryEntriesScanned = 0;

        ReadOnlySpan<int> active = units.ActiveIndices;
        Vector3[] positions = units.Position;
        float[] ranges = units.ViewRange;

        // Scan a bounded rolling window instead of enumerating every hostile
        // in every observer's radius. The spatial-cell walk still degenerated
        // into O(units^2) in dense firefights. Every window moves through the
        // active list so enemies outside the current sample are eventually seen.
        const int SectorCount = VisibilitySectorCount;
        const int SlotCount = CandidatesPerSector;
        const int CandidateSlots = SectorCount * SlotCount;

        Span<int> candidateTargets = stackalloc int[CandidateSlots];
        Span<float> candidateDistances = stackalloc float[CandidateSlots];
        Span<int> rotationDistances = stackalloc int[SectorCount];

        for (int i = 0; i < active.Length; i++)
        {
            int observer = active[i];

            if (health != null && health.OverallHitPoints[observer] <= 0f)
                continue;

            int start = _visibleTargetCount;
            _visibleStarts[observer] = start;

            Vector3 position = positions[observer];
            float range = MathF.Max(0f, ranges[observer]);
            float rangeSquared = range * range;
            Vector3 forward = NormalizeHorizontal(units.HeadNormal[observer]);
            float halfFovRadians = units.FieldOfView[observer] * MathF.PI / 360f;
            float minFacingDot = MathF.Cos(halfFovRadians);

            for (int sector = 0; sector < SectorCount; sector++)
            {
                int slot = sector * CandidatesPerSector;
                candidateTargets[slot] = -1;
                candidateTargets[slot + 1] = -1;
                candidateDistances[slot] = float.PositiveInfinity;
                candidateDistances[slot + 1] = 0f;
                rotationDistances[sector] = int.MaxValue;
            }

            int scanCount = Math.Min(
                active.Length,
                MaxTargetCandidatesPerObserver);
            int scanStart = (int)(
                ((long)observer * TargetObserverOffset +
                 (long)_visionUpdateSequence * TargetScanStride) %
                active.Length);

            for (int sample = 0; sample < scanCount; sample++)
            {
                LastActiveCandidatesScanned++;
                int activeSlot = scanStart + sample;
                if (activeSlot >= active.Length)
                    activeSlot -= active.Length;

                int target = active[activeSlot];
                if (target == observer ||
                    (health != null && health.OverallHitPoints[target] <= 0f))
                {
                    continue;
                }

                if (!FactionRules.ShouldAttack(
                        units.FactionTag[observer],
                        units.FactionTag[target]))
                {
                    continue;
                }

                _candidatePairCount++;

                Vector3 targetPosition = positions[target];
                float dx = targetPosition.X - position.X;
                float dy = targetPosition.Y - position.Y;
                float distanceSquared = dx * dx + dy * dy;

                if (distanceSquared > rangeSquared)
                    continue;

                int sector;
                if (dx >= 0f)
                {
                    if (dy >= 0f)
                        sector = dx >= dy ? 0 : 1;
                    else
                        sector = dx >= -dy ? 7 : 6;
                }
                else
                {
                    if (dy >= 0f)
                        sector = dy >= -dx ? 2 : 3;
                    else
                        sector = -dx >= -dy ? 4 : 5;
                }

                if (distanceSquared > 0.0001f)
                {
                    float inverseDistance = 1f / MathF.Sqrt(distanceSquared);
                    float facingDot =
                        (forward.X * dx + forward.Y * dy) * inverseDistance;

                    if (facingDot < minFacingDot)
                        continue;
                }

                int slot = sector * CandidatesPerSector;

                // Keep the nearest sampled target and one rotating candidate
                // in each sector. Total pair discovery is bounded to 64 checks/unit.
                if (distanceSquared < candidateDistances[slot])
                {
                    candidateTargets[slot] = target;
                    candidateDistances[slot] = distanceSquared;
                }

                int cursor = _sectorTargetCursors[
                    observer * SectorCount + sector];
                int rotationDistance = target >= cursor
                    ? target - cursor
                    : _visibilityCapacity - cursor + target;

                if (rotationDistance < rotationDistances[sector])
                {
                    rotationDistances[sector] = rotationDistance;
                    candidateTargets[slot + 1] = target;
                }
            }

            for (int candidate = 0; candidate < CandidateSlots; candidate++)
            {
                int target = candidateTargets[candidate];
                if (target < 0)
                    continue;

                bool duplicate = false;
                for (int previous = 0; previous < candidate; previous++)
                {
                    if (candidateTargets[previous] == target)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (duplicate)
                    continue;

                _targetEvaluationCount++;
                VisionCheck check = Evaluate(
                    units,
                    worldMap,
                    observer,
                    target);

                int pairIndex = observer * _visibilityCapacity + target;
                _lastVisibleUpdate[pairIndex] = check.IsVisible
                    ? _visionUpdateSequence
                    : 0;
            }

            // Advance sector cursors independently from nearest candidates so
            // the rotating slot eventually tests every hostile in a crowded sector.
            for (int sector = 0; sector < SectorCount; sector++)
            {
                int sampledTarget =
                    candidateTargets[sector * SlotCount + 1];

                if (sampledTarget >= 0)
                {
                    _sectorTargetCursors[observer * SectorCount + sector] =
                        (sampledTarget + 1) % _visibilityCapacity;
                }
            }

            BuildVisibleTargetList(
                observer,
                candidateTargets);

            _visibleCounts[observer] = _visibleTargetCount - start;
        }

        LastUpdateMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LastCandidatePairs = _candidatePairCount;
        LastTargetEvaluations = _targetEvaluationCount;
        LastLineOfSightChecks = _lineOfSightChecks;
        LastVisibleTargetCount = _visibleTargetCount;
        UpdateCount++;
    }

    public bool IsRecentlyVisible(
        int observerIndex,
        int targetIndex,
        UnitStore units)
    {
        if ((uint)observerIndex >= (uint)_visibilityCapacity ||
            (uint)targetIndex >= (uint)_visibilityCapacity ||
            observerIndex == targetIndex)
        {
            return false;
        }

        int pairIndex = observerIndex * _visibilityCapacity + targetIndex;
        int lastVisible = _lastVisibleUpdate[pairIndex];

        if (lastVisible == 0 ||
            _visionUpdateSequence - lastVisible > VisibilityMemoryUpdates)
        {
            _lastVisibleUpdate[pairIndex] = 0;
            return false;
        }

        if (!FactionRules.ShouldAttack(
                units.FactionTag[observerIndex],
                units.FactionTag[targetIndex]) ||
            !IsWithinRangeAndFov(
                units,
                observerIndex,
                targetIndex,
                out _))
        {
            _lastVisibleUpdate[pairIndex] = 0;
            return false;
        }

        return true;
    }

    public void ForgetVisibleTarget(int observerIndex, int targetIndex)
    {
        if ((uint)observerIndex >= (uint)_visibilityCapacity ||
            (uint)targetIndex >= (uint)_visibilityCapacity)
        {
            return;
        }

        _lastVisibleUpdate[
            observerIndex * _visibilityCapacity + targetIndex] = 0;

        int start = _visibleStarts[observerIndex];
        int end = start + _visibleCounts[observerIndex];

        for (int i = start; i < end; i++)
        {
            if (_visibleTargets[i] == targetIndex)
                _visibleTargets[i] = -1;
        }
    }

    public void ClearUnit(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_visibleCounts.Length)
            return;

        _visibleStarts[unitIndex] = 0;
        _visibleCounts[unitIndex] = 0;

        // Remove this unit from every already-built observer list as well as
        // from the pair cache, because unit indices can be recycled after death.
        for (int observer = 0; observer < _visibleCounts.Length; observer++)
        {
            int start = _visibleStarts[observer];
            int end = start + _visibleCounts[observer];

            for (int i = start; i < end; i++)
            {
                if (_visibleTargets[i] == unitIndex)
                    _visibleTargets[i] = -1;
            }
        }

        if ((uint)unitIndex < (uint)_visibilityCapacity)
        {
            Array.Clear(
                _lastVisibleUpdate,
                unitIndex * _visibilityCapacity,
                _visibilityCapacity);

            for (int observer = 0; observer < _visibilityCapacity; observer++)
                _lastVisibleUpdate[observer * _visibilityCapacity + unitIndex] = 0;

            Array.Clear(
                _sectorTargetCursors,
                unitIndex * VisibilitySectorCount,
                VisibilitySectorCount);
        }

        _updateTimer = UpdateInterval;
    }

    public void ClearAll()
    {
        Array.Clear(_visibleStarts);
        Array.Clear(_visibleCounts);
        Array.Clear(_lastVisibleUpdate);
        Array.Clear(_sectorTargetCursors);
        _visibleTargetCount = 0;
        _visionUpdateSequence = 0;
        _visibilityTerrainVersion = long.MinValue;
        LastCandidatePairs = 0;
        LastActiveCandidatesScanned = 0;
        LastTargetEvaluations = 0;
        LastLineOfSightChecks = 0;
        LastVisibleTargetCount = 0;
        LastVisibilityMemoryEntriesScanned = 0;
        LastUpdateMilliseconds = 0d;
        _updateTimer = 0f;
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
        float startLayerZ = start.Z * WorldMap.HeightUnitsPerMeter;
        float dzLayers = dz * WorldMap.HeightUnitsPerMeter;

        float horizontalLengthSquared =
            dx * dx + dy * dy;

        if (horizontalLengthSquared < 0.000001f)
        {
            int verticalCellX = (int)MathF.Floor(end.X);
            int verticalCellY = (int)MathF.Floor(end.Y);

            float endLayerZ = startLayerZ + dzLayers;
            float lowZ = MathF.Min(startLayerZ, endLayerZ);
            float highZ = MathF.Max(startLayerZ, endLayerZ);

            int firstZ = Math.Max(0, (int)MathF.Floor(lowZ));
            int lastZ = Math.Min(
                worldMap.LayerCount - 1,
                (int)MathF.Floor(highZ));

            for (int z = firstZ; z <= lastZ; z++)
            {
                if (worldMap.GetMaterialId(verticalCellX, verticalCellY, z) == 0)
                    continue;

                bool intersects =
                    MathF.Abs(dzLayers) < 0.000001f
                        ? startLayerZ >= z - LayerEpsilon &&
                          startLayerZ < z + 1f - LayerEpsilon
                        : highZ > z + LayerEpsilon &&
                          lowZ < z + 1f - LayerEpsilon;

                if (!intersects)
                    continue;

                float boundaryZ =
                    dzLayers < 0f ? z + 1f : z;

                float hitT =
                    MathF.Abs(dzLayers) < 0.000001f
                        ? 0f
                        : Math.Clamp(
                            (boundaryZ - startLayerZ) / dzLayers,
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
                float z0 = startLayerZ + dzLayers * segmentStart;
                float z1 = startLayerZ + dzLayers * checkEnd;
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
                        MathF.Abs(dzLayers) < 0.000001f
                            ? z0 >= z - LayerEpsilon &&
                              z0 < z + 1f - LayerEpsilon
                            : highZ > z + LayerEpsilon &&
                              lowZ < z + 1f - LayerEpsilon;

                    if (!intersects)
                        continue;

                    float hitT;

                    if (MathF.Abs(dzLayers) < 0.000001f)
                    {
                        hitT = segmentStart;
                    }
                    else
                    {
                        float entryZ = dzLayers > 0f ? z : z + 1f;
                        hitT = Math.Clamp(
                            (entryZ - startLayerZ) / dzLayers,
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

    private void BuildVisibleTargetList(
        int observer,
        ReadOnlySpan<int> candidates)
    {
        int visibleStart = _visibleTargetCount;

        // Every candidate in this span was selected by the spatial index for
        // this observer. Only publish targets whose LOS was verified this tick.
        // Do not scan every active unit here: doing that for each observer made
        // target-list rebuilding O(units^2) every 100 ms.
        for (int i = 0; i < candidates.Length; i++)
        {
            int target = candidates[i];
            if (target < 0)
                continue;

            LastVisibilityMemoryEntriesScanned++;

            int pairIndex = observer * _visibilityCapacity + target;
            if (_lastVisibleUpdate[pairIndex] != _visionUpdateSequence)
                continue;

            bool duplicate = false;
            for (int previous = visibleStart; previous < _visibleTargetCount; previous++)
            {
                if (_visibleTargets[previous] == target)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                AppendVisibleTarget(target);
        }
    }

    private static bool IsWithinRangeAndFov(
        UnitStore units,
        int observer,
        int target,
        out float distanceSquared)
    {
        Vector3 observerPosition = units.Position[observer];
        Vector3 targetPosition = units.Position[target];

        float dx = targetPosition.X - observerPosition.X;
        float dy = targetPosition.Y - observerPosition.Y;
        distanceSquared = dx * dx + dy * dy;

        float range = MathF.Max(0f, units.ViewRange[observer]);
        if (distanceSquared > range * range)
            return false;

        if (distanceSquared < 0.0001f)
            return true;

        float inverseDistance = 1f / MathF.Sqrt(distanceSquared);
        Vector3 forward = NormalizeHorizontal(units.HeadNormal[observer]);
        float facingDot = (forward.X * dx + forward.Y * dy) * inverseDistance;
        float minFacingDot = MathF.Cos(
            units.FieldOfView[observer] * MathF.PI / 360f);

        return facingDot >= minFacingDot;
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

    private void EnsureUnitCapacity(int capacity)
    {
        if (_visibleStarts.Length < capacity)
        {
            Array.Resize(ref _visibleStarts, capacity);
            Array.Resize(ref _visibleCounts, capacity);
        }

        EnsureVisibilityCacheCapacity(capacity);
    }

    private void EnsureVisibilityCacheCapacity(int capacity)
    {
        if (_visibilityCapacity >= capacity)
            return;

        int newCapacity = Math.Max(
            capacity,
            _visibilityCapacity == 0 ? 32 : _visibilityCapacity * 2);

        int[] nextVisibleUpdates = new int[checked(newCapacity * newCapacity)];
        int[] nextSectorCursors = new int[checked(newCapacity * VisibilitySectorCount)];

        for (int row = 0; row < _visibilityCapacity; row++)
        {
            Array.Copy(
                _lastVisibleUpdate,
                row * _visibilityCapacity,
                nextVisibleUpdates,
                row * newCapacity,
                _visibilityCapacity);

            Array.Copy(
                _sectorTargetCursors,
                row * VisibilitySectorCount,
                nextSectorCursors,
                row * VisibilitySectorCount,
                VisibilitySectorCount);
        }

        _lastVisibleUpdate = nextVisibleUpdates;
        _sectorTargetCursors = nextSectorCursors;
        _visibilityCapacity = newCapacity;
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
