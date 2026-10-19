using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Core.Map;
using Core.Combat;

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
    private const float UpdateInterval = 0.10f;
    private const float Epsilon = 0.02f;
    private const float LayerEpsilon = Epsilon * WorldMap.HeightUnitsPerMeter;
    private const int VisibilityMemoryUpdates = 60;
    private const int VisibilitySectorCount = 8;
    private const int CandidatesPerSector = 2;
    private const int MaxTargetCandidatesPerObserver = 64;
    private const int TargetScanStride = 97;
    private const int TargetObserverOffset = 37;
    /// <summary>Hostiles inside this distance can be noticed outside the facing cone.</summary>
    public const float CloseAwarenessRange = 1.5f;
    private const int NearbyCellRadius = (int)CloseAwarenessRange + 1;
    private const float CloseAwarenessRangeSquared = CloseAwarenessRange * CloseAwarenessRange;
    private const int CandidateSlots = VisibilitySectorCount * CandidatesPerSector;
    private static readonly int MaxVisionParallelism =
        Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2));
    private static readonly ParallelOptions VisionParallelOptions = new()
    {
        MaxDegreeOfParallelism = MaxVisionParallelism
    };

    private readonly UnitHitRegionStore? _hitRegions;
    private UnitBodyStore? _bodies;
    private float _updateTimer;
    private int _candidatePairCount;
    private int _lineOfSightChecks;
    private int _targetEvaluationCount;
    private int[] _lastVisibleUpdate = Array.Empty<int>();
    private int[] _sectorTargetCursors = Array.Empty<int>();
    private int _visibilityCapacity;
    private int _visionUpdateSequence;
    private long _visibilityTerrainVersion = long.MinValue;

    private int _spatialWidth;
    private int _spatialHeight;
    private int[] _spatialCellCounts = Array.Empty<int>();
    private int[] _spatialCellStarts = Array.Empty<int>();
    private int[] _spatialCellWriteCursors = Array.Empty<int>();
    private int[] _spatialCellLastUnit = Array.Empty<int>();
    private int[] _spatialUnits = Array.Empty<int>();
    private int[] _occupiedSpatialCells = Array.Empty<int>();
    private int _occupiedSpatialCellCount;
    private int _spatialMarkSequence;
    private int[][] _workerCandidateTargetMarks = Array.Empty<int[]>();
    private readonly int[] _workerCandidateMarkSequences = new int[MaxVisionParallelism];
    private int[] _candidateTargetsByObserver = Array.Empty<int>();
    private int[] _observerCandidatePairs = Array.Empty<int>();
    private int[] _observerLineOfSightChecks = Array.Empty<int>();
    private int[] _observerTargetEvaluations = Array.Empty<int>();
    private int[] _observerActiveCandidatesScanned = Array.Empty<int>();

    public double LastUpdateMilliseconds { get; private set; }
    public int LastCandidatePairs { get; private set; }
    public int LastActiveCandidatesScanned { get; private set; }
    public int LastTargetEvaluations { get; private set; }
    public int LastLineOfSightChecks { get; private set; }
    public int LastVisibleTargetCount { get; private set; }
    public int LastVisibilityMemoryEntriesScanned { get; private set; }
    public long UpdateCount { get; private set; }
    private int[] _aliveUnits;
    private int[] _visibleStarts;
    private int[] _visibleCounts;
    private int[] _visibleTargets;
    private int _visibleTargetCount;

    public VisionSystem(
        int initialUnitCapacity = 1024,
        UnitHitRegionStore? hitRegions = null)
    {
        if (initialUnitCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(initialUnitCapacity));

        _hitRegions = hitRegions;

        _aliveUnits =
            new int[initialUnitCapacity];

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
        UnitHealthStore? health = null,
        UnitBodyStore? bodies = null)
    {
        _bodies = bodies;
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

        ReadOnlySpan<int> allActive = units.ActiveIndices;
        int aliveCount = 0;

        for (int i = 0; i < allActive.Length; i++)
        {
            int unit = allActive[i];

            if (health != null && health.OverallHitPoints[unit] <= 0f)
                continue;

            _aliveUnits[aliveCount++] = unit;
        }

        ReadOnlySpan<int> active = _aliveUnits.AsSpan(0, aliveCount);

        EnsureSpatialGrid(worldMap, units.Capacity);
        BuildSpatialGrid(units, active);

        // Candidate gathering and LOS checks are independent per observer.
        // BuildSpatialGrid above is read-only from this point through the worker phase.
        if (aliveCount >= 32 && MaxVisionParallelism > 1)
        {
            int partitionCount = Math.Min(MaxVisionParallelism, aliveCount);
            Parallel.For(0, partitionCount, VisionParallelOptions, partition =>
            {
                int[] marks = _workerCandidateTargetMarks[partition];
                int markSequence = _workerCandidateMarkSequences[partition];
                int begin = partition * aliveCount / partitionCount;
                int finish = (partition + 1) * aliveCount / partitionCount;

                for (int i = begin; i < finish; i++)
                    ProcessObserver(
                        i, aliveCount, units, worldMap, health, bodies,
                        marks, ref markSequence);

                _workerCandidateMarkSequences[partition] = markSequence;
            });
        }
        else
        {
            int markSequence = _workerCandidateMarkSequences[0];
            int[] marks = _workerCandidateTargetMarks[0];
            for (int i = 0; i < aliveCount; i++)
                ProcessObserver(
                    i, aliveCount, units, worldMap, health, bodies,
                    marks, ref markSequence);
            _workerCandidateMarkSequences[0] = markSequence;
        }

        // Workers only touch per-observer rows. Assemble the compact shared visible
        // target list on this thread after all visibility cache writes are complete.
        for (int i = 0; i < aliveCount; i++)
        {
            int observer = _aliveUnits[i];
            _candidatePairCount += _observerCandidatePairs[observer];
            _lineOfSightChecks += _observerLineOfSightChecks[observer];
            _targetEvaluationCount += _observerTargetEvaluations[observer];
            LastActiveCandidatesScanned += _observerActiveCandidatesScanned[observer];

            int start = _visibleTargetCount;
            _visibleStarts[observer] = start;
            int candidateOffset = observer * CandidateSlots;

            BuildVisibleTargetList(
                observer,
                _candidateTargetsByObserver.AsSpan(candidateOffset, CandidateSlots));

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

    private void ProcessObserver(
        int activeSlot,
        int activeCount,
        UnitStore units,
        WorldMap worldMap,
        UnitHealthStore? health,
        UnitBodyStore? bodies,
        int[] candidateTargetMarks,
        ref int markSequence)
    {
        int observer = _aliveUnits[activeSlot];
        int candidateOffset = observer * CandidateSlots;
        Span<int> candidateTargets = _candidateTargetsByObserver.AsSpan(
            candidateOffset, CandidateSlots);
        candidateTargets.Fill(-1);

        Span<float> candidateDistances = stackalloc float[CandidateSlots];
        Span<int> rotationDistances = stackalloc int[VisibilitySectorCount];
        Span<int> sampledTargets = stackalloc int[MaxTargetCandidatesPerObserver];

        int candidatePairs = 0;
        int lineOfSightChecks = 0;
        int targetEvaluations = 0;
        int activeCandidatesScanned = 0;

        if (health != null && health.OverallHitPoints[observer] <= 0f)
        {
            _observerCandidatePairs[observer] = 0;
            _observerLineOfSightChecks[observer] = 0;
            _observerTargetEvaluations[observer] = 0;
            _observerActiveCandidatesScanned[observer] = 0;
            return;
        }

        Vector3 observerEye = UnitHitSystem.GetEyePosition(
            units, observer, bodies, _hitRegions);
        float range = MathF.Max(0f, units.ViewRange[observer]);
        float rangeSquared = range * range;
        Vector3 forward = NormalizeHorizontal(units.HeadNormal[observer]);
        float halfFovRadians = units.FieldOfView[observer] * MathF.PI / 360f;
        float minFacingDot = MathF.Cos(halfFovRadians);

        for (int sector = 0; sector < VisibilitySectorCount; sector++)
        {
            int slot = sector * CandidatesPerSector;
            candidateDistances[slot] = float.PositiveInfinity;
            candidateDistances[slot + 1] = 0f;
            rotationDistances[sector] = int.MaxValue;
        }

        if (markSequence >= int.MaxValue - 1)
        {
            Array.Clear(candidateTargetMarks);
            markSequence = 0;
        }
        int sampleMark = ++markSequence;

        int sampledCount = CollectNearbyTargets(
            observer,
            observerEye,
            units,
            sampledTargets,
            candidateTargetMarks,
            sampleMark);

        // Nearby spatial targets have priority. Fill the remaining slots from a
        // rolling global window without restoring the quadratic all-pairs scan.
        int scanCount = Math.Min(
            activeCount,
            MaxTargetCandidatesPerObserver + sampledCount);
        int scanStart = (int)(
            ((long)observer * TargetObserverOffset +
             (long)_visionUpdateSequence * TargetScanStride) %
            activeCount);

        for (int sample = 0;
             sample < scanCount &&
             sampledCount < MaxTargetCandidatesPerObserver;
             sample++)
        {
            activeCandidatesScanned++;
            int activeSlotInList = scanStart + sample;
            if (activeSlotInList >= activeCount)
                activeSlotInList -= activeCount;

            int target = _aliveUnits[activeSlotInList];
            if (target == observer || candidateTargetMarks[target] == sampleMark)
                continue;

            candidateTargetMarks[target] = sampleMark;
            if (!FactionRules.ShouldAttack(
                    units.FactionTag[observer],
                    units.FactionTag[target]))
            {
                continue;
            }

            sampledTargets[sampledCount++] = target;
        }

        for (int sample = 0; sample < sampledCount; sample++)
        {
            int target = sampledTargets[sample];
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

            candidatePairs++;
            if (!TryGetInViewRegionPoint(
                    units,
                    observer,
                    target,
                    observerEye,
                    forward,
                    rangeSquared,
                    minFacingDot,
                    out Vector3 targetPoint,
                    out float distanceSquared,
                    bodies))
            {
                continue;
            }

            float dx = targetPoint.X - observerEye.X;
            float dy = targetPoint.Y - observerEye.Y;
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

            int slot = sector * CandidatesPerSector;
            if (distanceSquared < candidateDistances[slot])
            {
                candidateTargets[slot] = target;
                candidateDistances[slot] = distanceSquared;
            }

            int cursor = _sectorTargetCursors[observer * VisibilitySectorCount + sector];
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

            targetEvaluations++;
            VisionCheck check = EvaluateCore(
                units, worldMap, observer, target, null, bodies,
                ref lineOfSightChecks);

            int pairIndex = observer * _visibilityCapacity + target;
            _lastVisibleUpdate[pairIndex] = check.IsVisible
                ? _visionUpdateSequence
                : 0;
        }

        // Rotate the far-target pick independently from the nearest target.
        for (int sector = 0; sector < VisibilitySectorCount; sector++)
        {
            int sampledTarget = candidateTargets[sector * CandidatesPerSector + 1];
            if (sampledTarget >= 0)
            {
                _sectorTargetCursors[observer * VisibilitySectorCount + sector] =
                    (sampledTarget + 1) % _visibilityCapacity;
            }
        }

        _observerCandidatePairs[observer] = candidatePairs;
        _observerLineOfSightChecks[observer] = lineOfSightChecks;
        _observerTargetEvaluations[observer] = targetEvaluations;
        _observerActiveCandidatesScanned[observer] = activeCandidatesScanned;
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
        int targetIndex,
        UnitBodyStore? bodies = null)
    {
        int lineOfSightChecks = 0;
        VisionCheck result = EvaluateCore(
            units, worldMap, observerIndex, targetIndex, null, bodies,
            ref lineOfSightChecks);
        Interlocked.Add(ref _lineOfSightChecks, lineOfSightChecks);
        return result;
    }

    public VisionCheck EvaluateTargetMode(
        UnitStore units,
        WorldMap worldMap,
        int observerIndex,
        int targetIndex,
        TargetMode targetMode,
        UnitBodyStore? bodies = null)
    {
        int lineOfSightChecks = 0;
        VisionCheck result = EvaluateCore(
            units, worldMap, observerIndex, targetIndex, targetMode, bodies,
            ref lineOfSightChecks);
        Interlocked.Add(ref _lineOfSightChecks, lineOfSightChecks);
        return result;
    }

    private VisionCheck EvaluateCore(
        UnitStore units,
        WorldMap worldMap,
        int observerIndex,
        int targetIndex,
        TargetMode? targetMode,
        UnitBodyStore? bodies,
        ref int lineOfSightChecks)
    {
        // Direct combat LOS checks often omit the optional body store. Reuse the
        // same live rig bound during Update instead of reverting to standing-height
        // aim points between rotating perception samples.
        bodies ??= _bodies;

        Vector3 targetCenter = units.Position[targetIndex];
        Vector3 observerEye = UnitHitSystem.GetEyePosition(
            units,
            observerIndex,
            bodies,
            _hitRegions);

        if (observerIndex == targetIndex)
        {
            return new VisionCheck(
                VisionState.Self,
                -1,
                observerEye,
                targetCenter,
                targetCenter,
                Vector3.Zero);
        }

        float range = MathF.Max(0f, units.ViewRange[observerIndex]);
        float rangeSquared = range * range;
        Vector3 forward = NormalizeHorizontal(units.HeadNormal[observerIndex]);
        float halfFovRadians = units.FieldOfView[observerIndex] * MathF.PI / 360f;
        float minFacingDot = MathF.Cos(halfFovRadians);

        int pointCount = UnitHitSystem.GetVisibilityPointCount(
            units,
            targetIndex,
            _hitRegions);
        UnitAnatomyMarkerRole pointRole = targetMode.HasValue
            ? UnitAnatomyMarkerRole.Aim
            : UnitAnatomyMarkerRole.Vision;

        bool anyRegionInRange = false;
        bool anyRegionInFov = false;
        bool hasBlockingPoint = false;
        Vector3 firstBlockingPoint = Vector3.Zero;
        bool hasVisibleTargetPoint = false;
        int selectedPointIndex = -1;
        Vector3 selectedTargetPoint = targetCenter;
        float selectedTargetHeight = 0f;
        float selectedMiddleDistance = float.PositiveInfinity;
        const int MaxCapturedVisibleMarkers = 128;
        Span<float> visibleHeights = targetMode.HasValue
            ? stackalloc float[MaxCapturedVisibleMarkers]
            : Span<float>.Empty;
        Span<int> visibleIndices = targetMode.HasValue
            ? stackalloc int[MaxCapturedVisibleMarkers]
            : Span<int>.Empty;
        Span<Vector3> visiblePoints = targetMode.HasValue
            ? stackalloc Vector3[MaxCapturedVisibleMarkers]
            : Span<Vector3>.Empty;
        int visiblePointCount = 0;
        int capturedVisiblePointCount = 0;
        float minTargetHeight = float.PositiveInfinity;
        float maxTargetHeight = float.NegativeInfinity;

        // Cache target marker coordinates once. Geometry lookup can traverse the
        // articulated anatomy and used to be repeated for the LOS pass below.
        Span<int> visibilityRegionIndices = pointCount <= 64
            ? stackalloc int[pointCount]
            : new int[pointCount];
        Span<Vector3> visibilityPoints = pointCount <= 64
            ? stackalloc Vector3[pointCount]
            : new Vector3[pointCount];

        for (int i = 0; i < pointCount; i++)
        {
            int regionIndex = UnitHitSystem.GetVisibilityRegionIndex(
                units, targetIndex, i, _hitRegions);
            Vector3 point = UnitHitSystem.GetGameplayTargetPoint(
                units, targetIndex, regionIndex, observerEye, _hitRegions, pointRole, bodies);
            visibilityRegionIndices[i] = regionIndex;
            visibilityPoints[i] = point;
            minTargetHeight = MathF.Min(minTargetHeight, point.Z);
            maxTargetHeight = MathF.Max(maxTargetHeight, point.Z);
        }

        float middleTargetHeight = pointCount == 0
            ? targetCenter.Z
            : (minTargetHeight + maxTargetHeight) * 0.5f;

        for (int i = 0; i < pointCount; i++)
        {
            int regionIndex = visibilityRegionIndices[i];
            Vector3 point = visibilityPoints[i];

            float dx = point.X - observerEye.X;
            float dy = point.Y - observerEye.Y;
            float distanceSquared = dx * dx + dy * dy;

            if (distanceSquared > rangeSquared)
                continue;

            anyRegionInRange = true;

            if (distanceSquared > 0.0001f)
            {
                float inverseDistance = 1f / MathF.Sqrt(distanceSquared);
                float facingDot = (forward.X * dx + forward.Y * dy) * inverseDistance;
                if (facingDot < minFacingDot &&
                    distanceSquared > CloseAwarenessRangeSquared)
                {
                    continue;
                }
            }

            anyRegionInFov = true;
            lineOfSightChecks++;

            if (HasLineOfSight(
                    worldMap,
                    observerEye,
                    point,
                    out Vector3 blockingPoint))
            {
                if (!targetMode.HasValue)
                {
                    return new VisionCheck(
                        VisionState.Visible,
                        i,
                        observerEye,
                        targetCenter,
                        point,
                        Vector3.Zero);
                }

                float height = point.Z;
                visiblePointCount++;

                if (capturedVisiblePointCount < visibleHeights.Length)
                {
                    visibleHeights[capturedVisiblePointCount] = height;
                    visibleIndices[capturedVisiblePointCount] = regionIndex;
                    visiblePoints[capturedVisiblePointCount] = point;
                    capturedVisiblePointCount++;
                }

                bool preferred = !hasVisibleTargetPoint;
                float middleDistance = MathF.Abs(height - middleTargetHeight);

                if (hasVisibleTargetPoint)
                {
                    preferred = targetMode.Value switch
                    {
                        TargetMode.Head => height > selectedTargetHeight,
                        TargetMode.Legs => height < selectedTargetHeight,
                        _ => middleDistance < selectedMiddleDistance
                    };
                }

                if (preferred)
                {
                    hasVisibleTargetPoint = true;
                    selectedPointIndex = regionIndex;
                    selectedTargetPoint = point;
                    selectedTargetHeight = height;
                    selectedMiddleDistance = middleDistance;
                }

                continue;
            }

            if (!hasBlockingPoint)
            {
                firstBlockingPoint = blockingPoint;
                hasBlockingPoint = true;
            }
        }

        if (hasVisibleTargetPoint)
        {
            // Middle targeting means the median of the visible segment markers,
            // not a hardcoded humanoid torso or the middle of hidden body parts.
            if (targetMode.HasValue &&
                targetMode.Value != TargetMode.Head &&
                targetMode.Value != TargetMode.Legs &&
                visiblePointCount > 0 &&
                visiblePointCount == capturedVisiblePointCount)
            {
                Span<float> sortedHeights = stackalloc float[MaxCapturedVisibleMarkers];
                visibleHeights[..capturedVisiblePointCount].CopyTo(sortedHeights);
                sortedHeights[..capturedVisiblePointCount].Sort();
                float medianHeight = sortedHeights[capturedVisiblePointCount / 2];
                float bestDistance = float.PositiveInfinity;

                for (int i = 0; i < capturedVisiblePointCount; i++)
                {
                    float distance = MathF.Abs(visibleHeights[i] - medianHeight);
                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    selectedPointIndex = visibleIndices[i];
                    selectedTargetPoint = visiblePoints[i];
                }
            }

            return new VisionCheck(
                VisionState.Visible,
                selectedPointIndex,
                observerEye,
                targetCenter,
                selectedTargetPoint,
                Vector3.Zero);
        }

        VisionState state = !anyRegionInRange
            ? VisionState.OutOfRange
            : !anyRegionInFov
                ? VisionState.OutsideFieldOfView
                : VisionState.HiddenByTerrain;

        return new VisionCheck(
            state,
            -1,
            observerEye,
            targetCenter,
            targetCenter,
            firstBlockingPoint);
    }

    private bool TryGetInViewRegionPoint(
        UnitStore units,
        int observerIndex,
        int targetIndex,
        Vector3 observerEye,
        Vector3 forward,
        float rangeSquared,
        float minFacingDot,
        out Vector3 targetPoint,
        out float bestDistanceSquared,
        UnitBodyStore? bodies = null)
    {
        targetPoint = Vector3.Zero;
        bestDistanceSquared = float.PositiveInfinity;
        int pointCount = UnitHitSystem.GetVisibilityPointCount(
            units,
            targetIndex,
            _hitRegions);

        for (int i = 0; i < pointCount; i++)
        {
            int regionIndex = UnitHitSystem.GetVisibilityRegionIndex(
                units, targetIndex, i, _hitRegions);
            Vector3 point = UnitHitSystem.GetGameplayTargetPoint(
                units,
                targetIndex,
                regionIndex,
                observerEye,
                _hitRegions,
                UnitAnatomyMarkerRole.Vision,
                bodies);

            float dx = point.X - observerEye.X;
            float dy = point.Y - observerEye.Y;
            float distanceSquared = dx * dx + dy * dy;

            if (distanceSquared > rangeSquared ||
                distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            if (distanceSquared > 0.0001f)
            {
                float inverseDistance = 1f / MathF.Sqrt(distanceSquared);
                float facingDot = (forward.X * dx + forward.Y * dy) * inverseDistance;
                if (facingDot < minFacingDot &&
                    distanceSquared > CloseAwarenessRangeSquared)
                {
                    continue;
                }
            }

            targetPoint = point;
            bestDistanceSquared = distanceSquared;
        }

        return float.IsFinite(bestDistanceSquared);
    }

    public int GetVisibilityPointCount(
        UnitStore units,
        int unitIndex)
    {
        return UnitHitSystem.GetVisibilityPointCount(
            units,
            unitIndex,
            _hitRegions);
    }

    public Vector3 GetEyePosition(
        UnitStore units,
        int unitIndex,
        UnitBodyStore? bodies = null)
    {
        return UnitHitSystem.GetEyePosition(
            units,
            unitIndex,
            bodies ?? _bodies,
            _hitRegions);
    }

    public void GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Span<Vector3> points,
        UnitBodyStore? bodies = null)
    {
        bodies ??= _bodies;
        Vector3 source = GetEyePosition(
            units,
            unitIndex,
            bodies);

        _ = GetVisibilityPoints(
            units,
            unitIndex,
            source,
            points,
            bodies);
    }

    public int GetVisibilityPoints(
        UnitStore units,
        int unitIndex,
        Vector3 source,
        Span<Vector3> points,
        UnitBodyStore? bodies = null)
    {
        return UnitHitSystem.GetVisibilityPoints(
            units,
            unitIndex,
            source,
            points,
            bodies ?? _bodies,
            _hitRegions);
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

    private void EnsureSpatialGrid(
        WorldMap worldMap,
        int unitCapacity)
    {
        int width = worldMap.TileWidth;
        int height = worldMap.TileHeight;
        bool dimensionsChanged =
            _spatialWidth != width ||
            _spatialHeight != height;

        if (dimensionsChanged)
        {
            int cellCount = checked(width * height);
            _spatialCellCounts = new int[cellCount];
            _spatialCellStarts = new int[cellCount];
            _spatialCellWriteCursors = new int[cellCount];
            _spatialCellLastUnit = new int[cellCount];
            _occupiedSpatialCells = new int[cellCount];
            _spatialWidth = width;
            _spatialHeight = height;
            _occupiedSpatialCellCount = 0;
            _spatialMarkSequence = 0;
        }
        else
        {
            // Only cells touched by hit regions need resetting each update.
            for (int i = 0; i < _occupiedSpatialCellCount; i++)
                _spatialCellCounts[_occupiedSpatialCells[i]] = 0;

            _occupiedSpatialCellCount = 0;
        }

        if (_spatialUnits.Length < unitCapacity)
            Array.Resize(ref _spatialUnits, unitCapacity);

    }

    private void BuildSpatialGrid(
        UnitStore units,
        ReadOnlySpan<int> active)
    {
        // Index each hit region's footprint, not just the unit root. This keeps
        // extended/custom profiles discoverable when a region is near an observer
        // but its root is in another cell.
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            UnitHitRegionProfile profile =
                _hitRegions?.GetProfile(unit) ??
                UnitHitRegionCatalog.Get(UnitCatalog.Get(units.Type[unit]).BodyType);
            int mark = NextSpatialMark();

            int regionCount = UnitHitSystem.GetGameplayDetectionRegionCount(profile);
            for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
            {
                UnitHitRegionGeometry geometry = UnitHitSystem.GetGameplayDetectionRegionGeometry(
                    units,
                    unit,
                    regionIndex,
                    _hitRegions);

                AddRegionToSpatialGrid(
                    unit,
                    geometry.Center,
                    geometry.Radius,
                    mark,
                    writeUnit: false);
            }
        }

        // Prefix sums are calculated only for cells touched by any hit region.
        int offset = 0;
        for (int i = 0; i < _occupiedSpatialCellCount; i++)
        {
            int cell = _occupiedSpatialCells[i];
            _spatialCellStarts[cell] = offset;
            _spatialCellWriteCursors[cell] = offset;
            offset += _spatialCellCounts[cell];
        }

        if (_spatialUnits.Length < offset)
        {
            int capacity = Math.Max(1, _spatialUnits.Length);
            while (capacity < offset)
                capacity = checked(capacity * 2);

            Array.Resize(ref _spatialUnits, capacity);
        }

        // Use fresh marks on this pass to de-duplicate a unit across overlapping
        // regions within each cell while writing the compact cell lists.
        for (int i = 0; i < active.Length; i++)
        {
            int unit = active[i];
            UnitHitRegionProfile profile =
                _hitRegions?.GetProfile(unit) ??
                UnitHitRegionCatalog.Get(UnitCatalog.Get(units.Type[unit]).BodyType);
            int mark = NextSpatialMark();

            int regionCount = UnitHitSystem.GetGameplayDetectionRegionCount(profile);
            for (int regionIndex = 0; regionIndex < regionCount; regionIndex++)
            {
                UnitHitRegionGeometry geometry = UnitHitSystem.GetGameplayDetectionRegionGeometry(
                    units,
                    unit,
                    regionIndex,
                    _hitRegions);

                AddRegionToSpatialGrid(
                    unit,
                    geometry.Center,
                    geometry.Radius,
                    mark,
                    writeUnit: true);
            }
        }
    }

    private void AddRegionToSpatialGrid(
        int unit,
        Vector3 center,
        float regionRadius,
        int mark,
        bool writeUnit)
    {
        float radius = MathF.Max(0.001f, regionRadius);
        float radiusSquared = radius * radius;
        int minX = Math.Max(0, (int)MathF.Floor(center.X - radius));
        int maxX = Math.Min(_spatialWidth - 1, (int)MathF.Floor(center.X + radius));
        int minY = Math.Max(0, (int)MathF.Floor(center.Y - radius));
        int maxY = Math.Min(_spatialHeight - 1, (int)MathF.Floor(center.Y + radius));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                // Only keep cells whose square actually overlaps the region.
                float closestX = Math.Clamp(center.X, x, x + 1f);
                float closestY = Math.Clamp(center.Y, y, y + 1f);
                float dx = center.X - closestX;
                float dy = center.Y - closestY;
                if (dx * dx + dy * dy > radiusSquared)
                    continue;

                int cell = x + y * _spatialWidth;
                if (_spatialCellLastUnit[cell] == mark)
                    continue;

                _spatialCellLastUnit[cell] = mark;

                if (writeUnit)
                {
                    _spatialUnits[_spatialCellWriteCursors[cell]++] = unit;
                }
                else
                {
                    if (_spatialCellCounts[cell] == 0)
                        _occupiedSpatialCells[_occupiedSpatialCellCount++] = cell;

                    _spatialCellCounts[cell]++;
                }
            }
        }
    }

    private int NextSpatialMark()
    {
        if (_spatialMarkSequence == int.MaxValue)
        {
            Array.Clear(_spatialCellLastUnit);
            _spatialMarkSequence = 0;
        }

        return ++_spatialMarkSequence;
    }

    private int CollectNearbyTargets(
        int observer,
        Vector3 position,
        UnitStore units,
        Span<int> targets,
        int[] candidateTargetMarks,
        int sampleMark)
    {
        int centerX = (int)MathF.Floor(position.X);
        int centerY = (int)MathF.Floor(position.Y);

        if (centerX < 0 || centerY < 0 ||
            centerX >= _spatialWidth || centerY >= _spatialHeight)
        {
            return 0;
        }

        // Keep the nearest hostile contacts from nearby cells. Sampling arbitrary
        // units here used to let nearby friendlies fill the entire candidate budget,
        // so a hostile one tile away could never reach the visibility test.
        int count = 0;
        Span<float> targetDistances = stackalloc float[MaxTargetCandidatesPerObserver];
        Vector3 observerPosition = units.Position[observer];
        ushort observerFaction = units.FactionTag[observer];

        for (int radius = 0; radius <= NearbyCellRadius; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                        continue;

                    int x = centerX + dx;
                    int y = centerY + dy;

                    if (x < 0 || y < 0 ||
                        x >= _spatialWidth || y >= _spatialHeight)
                    {
                        continue;
                    }

                    int cell = x + y * _spatialWidth;
                    int cellCount = _spatialCellCounts[cell];
                    if (cellCount == 0)
                        continue;

                    int cellStart = _spatialCellStarts[cell];
                    for (int i = 0; i < cellCount; i++)
                    {
                        int target = _spatialUnits[cellStart + i];
                        if (target == observer ||
                            candidateTargetMarks[target] == sampleMark)
                        {
                            continue;
                        }

                        candidateTargetMarks[target] = sampleMark;
                        if (!FactionRules.ShouldAttack(
                                observerFaction,
                                units.FactionTag[target]))
                        {
                            continue;
                        }

                        Vector3 targetPosition = units.Position[target];
                        float tx = targetPosition.X - observerPosition.X;
                        float ty = targetPosition.Y - observerPosition.Y;
                        float distanceSquared = tx * tx + ty * ty;

                        // A bounded insertion keeps only the nearest candidates,
                        // without allocations or an all-pairs scan.
                        if (count == targets.Length &&
                            distanceSquared >= targetDistances[count - 1])
                        {
                            continue;
                        }

                        int insertAt = Math.Min(count, targets.Length - 1);
                        if (count < targets.Length)
                            count++;

                        while (insertAt > 0 &&
                               targetDistances[insertAt - 1] > distanceSquared)
                        {
                            targets[insertAt] = targets[insertAt - 1];
                            targetDistances[insertAt] = targetDistances[insertAt - 1];
                            insertAt--;
                        }

                        targets[insertAt] = target;
                        targetDistances[insertAt] = distanceSquared;
                    }
                }
            }
        }

        return count;
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

    private bool IsWithinRangeAndFov(
        UnitStore units,
        int observer,
        int target,
        out float distanceSquared)
    {
        Vector3 observerEye = UnitHitSystem.GetEyePosition(
            units, observer, _bodies, _hitRegions);
        float range = MathF.Max(0f, units.ViewRange[observer]);
        float rangeSquared = range * range;
        Vector3 forward = NormalizeHorizontal(units.HeadNormal[observer]);
        float minFacingDot = MathF.Cos(
            units.FieldOfView[observer] * MathF.PI / 360f);

        distanceSquared = float.PositiveInfinity;
        int pointCount = UnitHitSystem.GetVisibilityPointCount(
            units, target, _hitRegions);

        for (int i = 0; i < pointCount; i++)
        {
            int regionIndex = UnitHitSystem.GetVisibilityRegionIndex(
                units, target, i, _hitRegions);
            Vector3 point = UnitHitSystem.GetGameplayTargetPoint(
                units, target, regionIndex, observerEye, _hitRegions,
                UnitAnatomyMarkerRole.Vision, _bodies);

            float dx = point.X - observerEye.X;
            float dy = point.Y - observerEye.Y;
            float candidateDistanceSquared = dx * dx + dy * dy;

            if (candidateDistanceSquared > rangeSquared)
                continue;

            if (candidateDistanceSquared > 0.0001f)
            {
                float inverseDistance = 1f / MathF.Sqrt(candidateDistanceSquared);
                float facingDot = (forward.X * dx + forward.Y * dy) * inverseDistance;
                if (facingDot < minFacingDot &&
                    candidateDistanceSquared > CloseAwarenessRangeSquared)
                {
                    continue;
                }
            }

            distanceSquared = MathF.Min(distanceSquared, candidateDistanceSquared);
        }

        return float.IsFinite(distanceSquared);
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
            Array.Resize(ref _aliveUnits, capacity);
            Array.Resize(ref _visibleStarts, capacity);
            Array.Resize(ref _visibleCounts, capacity);
        }

        if (_observerCandidatePairs.Length < capacity)
        {
            Array.Resize(ref _observerCandidatePairs, capacity);
            Array.Resize(ref _observerLineOfSightChecks, capacity);
            Array.Resize(ref _observerTargetEvaluations, capacity);
            Array.Resize(ref _observerActiveCandidatesScanned, capacity);
        }

        int candidateCapacity = checked(capacity * CandidateSlots);
        if (_candidateTargetsByObserver.Length < candidateCapacity)
            Array.Resize(ref _candidateTargetsByObserver, candidateCapacity);

        if (_workerCandidateTargetMarks.Length < MaxVisionParallelism)
            Array.Resize(ref _workerCandidateTargetMarks, MaxVisionParallelism);

        for (int i = 0; i < MaxVisionParallelism; i++)
        {
            int[] marks = _workerCandidateTargetMarks[i] ?? Array.Empty<int>();
            if (marks.Length < capacity)
                Array.Resize(ref marks, capacity);
            _workerCandidateTargetMarks[i] = marks;
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
