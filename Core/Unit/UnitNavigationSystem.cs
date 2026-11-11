using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

public enum UnitNavigationStatus : byte
{
    Unknown = 0,
    FollowingRoute = 1,
    WaitingForRouteBudget = 2,
    CachedFailure = 3,
    StartBlocked = 4,
    GoalBlocked = 5,
    DisconnectedTerrain = 6,
    PathNotFound = 7,
    NoUsableWaypoints = 8,
    FollowingPartialRoute = 9,
    MovingTowardGoalWhileWaitingForRoute = 10,
    FollowingHotPath = 11
}

/// <summary>
/// Builds and caches terrain-aware routes. Path requests are throttled so
/// mass commands do not run hundreds of A* searches in one frame.
/// </summary>
public sealed class UnitNavigationSystem
{
    public const int NavigationCellsPerMeter = 2;
    public const float NavigationCellSize = 1f / NavigationCellsPerMeter;

    private const float MaxStepHeight = 1.5f;
    private const float MaxTerrainSlopeGrade = 0.95f;
    private const float HeightCost = 0.35f;
    private const int MinRouteBuildsPerUpdate = 2;
    private const int MaxRouteBuildsPerUpdate = 8;
    private const int MaxExpandedNodesPerSearch = 16_384;
    private const float MaxRouteChunkCost = 28f;
    private const float RouteRefreshIntervalSeconds = 5f;
    private const float RouteRefreshRetrySeconds = 0.5f;
    private const float LocalTetherRefreshIntervalSeconds = 0.18f;
    private const float LocalTetherRefreshDistance = 1.2f;
    private const float LocalTetherMaxLaneOffset = 0.36f;
    private const float DirectFallbackDistance = 3f;
    private const float HotPathLookAheadDistance = 5f;
    private const float HotPathMinimumProgress = 0.45f;
    private const float HotPathMaximumWeightedPenaltyPerMeter = 0.16f;
    private const float HotPathWeightedPenaltyTolerance = 0.15f;
    private const int FailedRouteRetryUpdates = 120;
    private const int RouteCohortSize = 16;
    private const float RouteReuseCostPerUnit = 0.10f;
    private const float HotPathCostPenalty = 0.18f;
    private const float ShortcutCostTolerance = 0.05f;
    private const int MovingGoalTolerance = 8;
    private const float WaypointRadius = 0.12f;
    private const int MaxWaypointShortcutCells = 32;

    private static readonly int[] DirectionX =
        { 1, -1, 0, 0, 1, 1, -1, -1 };

    private static readonly int[] DirectionY =
        { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static readonly float DiagonalCost =
        MathF.Sqrt(2f);

    private int[][] _paths = Array.Empty<int[]>();
    private UnitNavigationStatus[] _lastStatuses = Array.Empty<UnitNavigationStatus>();
    private int[] _pathCursor = Array.Empty<int>();
    private int[][] _waypoints = Array.Empty<int[]>();
    private int[][] _waypointPathIndices = Array.Empty<int[]>();
    private int[] _waypointCursor = Array.Empty<int>();
    private int[] _routeGoalX = Array.Empty<int>();
    private int[] _routeGoalY = Array.Empty<int>();
    private float[] _routeStartX = Array.Empty<float>();
    private float[] _routeStartY = Array.Empty<float>();
    private int[] _routeFootprintX = Array.Empty<int>();
    private int[] _routeFootprintY = Array.Empty<int>();
    private int[] _failedFootprintX = Array.Empty<int>();
    private int[] _failedFootprintY = Array.Empty<int>();
    private UnitId[] _routeOwner = Array.Empty<UnitId>();
    private int[] _failedGoalX = Array.Empty<int>();
    private int[] _failedGoalY = Array.Empty<int>();
    private int[] _failedStartCell = Array.Empty<int>();
    private long[] _failedTerrainVersion = Array.Empty<long>();
    private long[] _failedRetryAfterUpdate = Array.Empty<long>();
    private long[] _routeTerrainVersion = Array.Empty<long>();
    private bool[] _routeTrafficRegistered = Array.Empty<bool>();
    private bool[] _routeIsPartial = Array.Empty<bool>();
    private float[] _routeRefreshAtSeconds = Array.Empty<float>();
    private Vector2[] _localTetherPoints = Array.Empty<Vector2>();
    private Vector2[] _localTetherGoals = Array.Empty<Vector2>();
    private UnitId[] _localTetherOwners = Array.Empty<UnitId>();
    private int[] _localTetherPathCursors = Array.Empty<int>();
    private int[] _localTetherWaypointCursors = Array.Empty<int>();
    private long[] _localTetherTerrainVersions = Array.Empty<long>();
    private float[] _localTetherRefreshAtSeconds = Array.Empty<float>();
    private bool[] _localTetherValid = Array.Empty<bool>();

    private int[] _routeTraffic = Array.Empty<int>();
    private long _routeTrafficTerrainVersion = long.MinValue;

    public int RoutesBuilt { get; private set; }
    public int RoutesFailed { get; private set; }
    public int RoutesBuiltThisUpdate { get; private set; }
    public int RoutesFailedThisUpdate { get; private set; }
    public int SearchesThisUpdate { get; private set; }
    public long CellsExpandedThisUpdate { get; private set; }
    public double SearchMillisecondsThisUpdate { get; private set; }
    public double NavigationGridBuildMilliseconds { get; private set; }
    public long RouteTrafficCellsConsideredThisUpdate { get; private set; }
    public int RoutesBuiltUsingPriorRoutesThisUpdate { get; private set; }

    private int _lastSearchCellsExpanded;
    private long _lastSearchTrafficCellsConsidered;
    private UnitNavigationStatus _lastBuildFailureStatus = UnitNavigationStatus.PathNotFound;

    public UnitNavigationStatus GetLastStatus(int unitIndex) =>
        (uint)unitIndex < (uint)_lastStatuses.Length
            ? _lastStatuses[unitIndex]
            : UnitNavigationStatus.Unknown;

    public int GetRoutePointCount(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_paths.Length)
            return 0;

        int[] path = _paths[unitIndex] ?? Array.Empty<int>();
        int start = Math.Clamp(_pathCursor[unitIndex], 0, path.Length);
        return path.Length - start;
    }

    public int CopyRoutePoints(int unitIndex, Span<Vector2> destination)
    {
        if ((uint)unitIndex >= (uint)_paths.Length || _width <= 0)
            return 0;

        int[] path = _paths[unitIndex] ?? Array.Empty<int>();
        int start = Math.Clamp(_pathCursor[unitIndex], 0, path.Length);
        int routeCount = path.Length - start;
        int count = Math.Min(destination.Length, routeCount);
        if (count == 0)
            return 0;

        for (int i = 0; i < count; i++)
        {
            int sourceOffset = count == routeCount
                ? i
                : count == 1
                    ? routeCount - 1
                    : (int)MathF.Round(i * (routeCount - 1f) / (count - 1f));
            int cell = path[start + sourceOffset];
            destination[i] = GetWaypointPosition(cell);
        }

        return count;
    }

    public int CopyWaypointPoints(int unitIndex, Span<Vector2> destination)
    {
        if ((uint)unitIndex >= (uint)_waypoints.Length || _width <= 0)
            return 0;

        int[] waypoints = _waypoints[unitIndex] ?? Array.Empty<int>();
        int start = Math.Clamp(_waypointCursor[unitIndex], 0, waypoints.Length);
        int count = Math.Min(destination.Length, waypoints.Length - start);
        for (int i = 0; i < count; i++)
        {
            int cell = waypoints[start + i];
            destination[i] = GetWaypointPosition(cell);
        }

        return count;
    }

    private int _width;
    private int _height;
    private int _cellCount;
    private int _searchStamp;
    private int _routeBuildsRemaining;
    private long _updateSequence;
    private float _navigationTimeSeconds;

    private float[] _gScore = Array.Empty<float>();
    private float[] _fScore = Array.Empty<float>();
    private float[] _navigationHeights = Array.Empty<float>();
    private float[] _navigationEdgeCost = Array.Empty<float>();
    private bool[] _navigationPassable = Array.Empty<bool>();
    private byte[] _navigationNeighbors = Array.Empty<byte>();
    private int[] _navigationComponent = Array.Empty<int>();
    private int[] _componentQueue = Array.Empty<int>();
    private long _navigationGridTerrainVersion = long.MinValue;
    private int[] _parent = Array.Empty<int>();
    private int[] _seenStamp = Array.Empty<int>();
    private int[] _closedStamp = Array.Empty<int>();
    private int[] _heap = Array.Empty<int>();
    private int[] _heapPosition = Array.Empty<int>();
    private int[] _reversePath = Array.Empty<int>();
    private int _heapCount;

    public void BeginUpdate(UnitStore units, WorldMap worldMap, float deltaTime = 0f)
    {
        _updateSequence++;
        if (float.IsFinite(deltaTime) && deltaTime > 0f)
            _navigationTimeSeconds += deltaTime;
        EnsureUnitCapacity(units.Capacity);
        EnsureWorkspace(worldMap);
        EnsureRouteTrafficVersion(worldMap);
        NavigationGridBuildMilliseconds = 0d;
        EnsureNavigationGrid(worldMap);
        _routeBuildsRemaining = Math.Clamp(
            units.ActiveCount / 32,
            MinRouteBuildsPerUpdate,
            MaxRouteBuildsPerUpdate);
        RoutesBuiltThisUpdate = 0;
        RoutesFailedThisUpdate = 0;
        SearchesThisUpdate = 0;
        CellsExpandedThisUpdate = 0;
        SearchMillisecondsThisUpdate = 0d;
        RouteTrafficCellsConsideredThisUpdate = 0;
        RoutesBuiltUsingPriorRoutesThisUpdate = 0;
    }

    public bool TryGetWaypoint(
        UnitStore units,
        int unitIndex,
        WorldMap worldMap,
        Vector3 position,
        Vector3 target,
        out Vector2 waypoint)
    {
        EnsureUnitCapacity(units.Capacity);
        EnsureWorkspace(worldMap);
        EnsureRouteTrafficVersion(worldMap);
        EnsureNavigationGrid(worldMap);
        _lastStatuses[unitIndex] = UnitNavigationStatus.Unknown;

        int startX = WorldToNavigationCell(position.X, _width);
        int startY = WorldToNavigationCell(position.Y, _height);
        int targetX = WorldToNavigationCell(target.X, _width);
        int targetY = WorldToNavigationCell(target.Y, _height);
        int startCell = startX + startY * _width;
        int footprintX = GetFootprintRadius(units.Width[unitIndex]);
        int footprintY = GetFootprintRadius(units.Length[unitIndex]);
        UnitId owner = units.GetId(unitIndex);

        if (_routeOwner[unitIndex] != owner)
            RemoveRouteTraffic(unitIndex);

        bool sameOwner = _routeOwner[unitIndex] == owner;
        bool footprintChanged = sameOwner &&
            (_routeFootprintX[unitIndex] != footprintX ||
             _routeFootprintY[unitIndex] != footprintY);
        int[] route = sameOwner ? _paths[unitIndex] ?? Array.Empty<int>() : Array.Empty<int>();
        int[] waypoints = sameOwner ? _waypoints[unitIndex] ?? Array.Empty<int>() : Array.Empty<int>();
        int[] waypointPathIndices = sameOwner
            ? _waypointPathIndices[unitIndex] ?? Array.Empty<int>()
            : Array.Empty<int>();
        int length = route.Length;
        int cursor = sameOwner ? _pathCursor[unitIndex] : 0;
        int waypointCursor = sameOwner ? _waypointCursor[unitIndex] : 0;

        bool targetFarMoved =
            sameOwner &&
            (Math.Abs(targetX - _routeGoalX[unitIndex]) > MovingGoalTolerance ||
             Math.Abs(targetY - _routeGoalY[unitIndex]) > MovingGoalTolerance);
        bool goalChanged = sameOwner &&
            (targetX != _routeGoalX[unitIndex] ||
             targetY != _routeGoalY[unitIndex]);
        bool passedBlockedCorner = false;
        if (sameOwner &&
            !targetFarMoved &&
            !footprintChanged &&
            _routeTerrainVersion[unitIndex] == worldMap.TerrainVersion)
        {
            passedBlockedCorner = AdvanceWaypointCursor(
                worldMap, units, unitIndex, position, target,
                route, waypoints, waypointPathIndices,
                ref cursor, ref waypointCursor);
        }

        bool routeFinished = length > 0 && cursor >= length;
        float distanceToGoalX = position.X - target.X;
        float distanceToGoalY = position.Y - target.Y;
        bool repositionedAwayFromFinishedRoute =
            routeFinished &&
            (_routeIsPartial[unitIndex] ||
             distanceToGoalX * distanceToGoalX + distanceToGoalY * distanceToGoalY > 2.25f);

        bool deviatedFromRoute = false;
        if (sameOwner && waypointCursor < waypoints.Length)
        {
            deviatedFromRoute = !HasClearStraightRoute(
                worldMap,
                position.X,
                position.Y,
                waypoints[waypointCursor],
                footprintX,
                footprintY);
        }

        bool routeInvalid =
            !sameOwner ||
            length == 0 ||
            targetFarMoved ||
            deviatedFromRoute ||
            passedBlockedCorner ||
            (routeFinished && goalChanged) ||
            repositionedAwayFromFinishedRoute ||
            footprintChanged ||
            (sameOwner && _routeTerrainVersion[unitIndex] != worldMap.TerrainVersion);

        // Periodically refresh a still-valid local path. When the A* budget is
        // busy, keep following the validated route rather than stopping.
        bool refreshDue =
            !routeInvalid &&
            sameOwner &&
            length > 0 &&
            waypointCursor < waypoints.Length &&
            _navigationTimeSeconds >= _routeRefreshAtSeconds[unitIndex];

        // Prefer a short, footprint-safe move directly toward the objective.
        // A* is requested only when the next reachable segment is blocked.
        if ((routeInvalid || refreshDue) &&
            TryGetHotPathWaypoint(
                worldMap,
                position,
                target,
                footprintX,
                footprintY,
                out Vector2 hotPathWaypoint,
                out bool obstacleAhead) &&
            (routeInvalid || !obstacleAhead))
        {
            ClearRoute(unitIndex, preserveLocalTether: true);
            _lastStatuses[unitIndex] = UnitNavigationStatus.FollowingHotPath;
            waypoint = hotPathWaypoint;
            return true;
        }

        bool needsRoute = routeInvalid || refreshDue;
        if (needsRoute)
        {
            // Exclude this pawn's own cached corridor from route reuse costs.
            // A periodic refresh restores the old corridor if no replacement is ready.
            RemoveRouteTraffic(unitIndex);

            bool failedAlready =
                sameOwner &&
                _failedGoalX[unitIndex] == targetX &&
                _failedGoalY[unitIndex] == targetY &&
                _failedStartCell[unitIndex] == startCell &&
                _failedFootprintX[unitIndex] == footprintX &&
                _failedFootprintY[unitIndex] == footprintY &&
                _failedTerrainVersion[unitIndex] == worldMap.TerrainVersion &&
                _failedRetryAfterUpdate[unitIndex] > _updateSequence;

            if (!failedAlready && _routeBuildsRemaining > 0)
            {
                _routeBuildsRemaining--;

                long searchStarted = Stopwatch.GetTimestamp();
                bool pathFound = TryBuildPath(
                    worldMap,
                    startX,
                    startY,
                    targetX,
                    targetY,
                    footprintX,
                    footprintY,
                    out int[] path,
                    out bool partialRoute);

                SearchesThisUpdate++;
                SearchMillisecondsThisUpdate +=
                    Stopwatch.GetElapsedTime(searchStarted).TotalMilliseconds;
                CellsExpandedThisUpdate += _lastSearchCellsExpanded;

                if (pathFound)
                {
                    BuildSmoothedPath(
                        worldMap,
                        path,
                        position.X,
                        position.Y,
                        footprintX,
                        footprintY,
                        out int[] smoothedWaypoints,
                        out int[] waypointIndices);

                    InvalidateLocalTether(unitIndex);
                    _paths[unitIndex] = path;
                    _pathCursor[unitIndex] = 0;
                    _waypoints[unitIndex] = smoothedWaypoints;
                    _waypointPathIndices[unitIndex] = waypointIndices;
                    _waypointCursor[unitIndex] = 0;
                    _routeGoalX[unitIndex] = targetX;
                    _routeGoalY[unitIndex] = targetY;
                    _routeStartX[unitIndex] = position.X;
                    _routeStartY[unitIndex] = position.Y;
                    _routeFootprintX[unitIndex] = footprintX;
                    _routeFootprintY[unitIndex] = footprintY;
                    _routeOwner[unitIndex] = owner;
                    _routeTerrainVersion[unitIndex] = worldMap.TerrainVersion;
                    _routeIsPartial[unitIndex] = partialRoute;
                    _routeRefreshAtSeconds[unitIndex] =
                        _navigationTimeSeconds + RouteRefreshIntervalSeconds;
                    RoutesBuilt++;
                    RoutesBuiltThisUpdate++;
                    _failedGoalX[unitIndex] = int.MinValue;
                    _failedGoalY[unitIndex] = int.MinValue;
                    _failedRetryAfterUpdate[unitIndex] = 0;

                    route = path;
                    waypoints = smoothedWaypoints;
                    waypointPathIndices = waypointIndices;
                    length = route.Length;
                    cursor = 0;
                    waypointCursor = 0;
                    sameOwner = true;
                    RegisterRouteTraffic(unitIndex);

                    if (_lastSearchTrafficCellsConsidered > 0)
                        RoutesBuiltUsingPriorRoutesThisUpdate++;
                }
                else if (refreshDue)
                {
                    // A failed refresh does not invalidate a route that was
                    // already validated against the current terrain.
                    _routeRefreshAtSeconds[unitIndex] =
                        _navigationTimeSeconds + RouteRefreshRetrySeconds;
                    RegisterRouteTraffic(unitIndex);
                    RoutesFailed++;
                    RoutesFailedThisUpdate++;
                }
                else
                {
                    _lastStatuses[unitIndex] = _lastBuildFailureStatus;
                    InvalidateLocalTether(unitIndex);
                    _routeIsPartial[unitIndex] = false;
                    _paths[unitIndex] = Array.Empty<int>();
                    _pathCursor[unitIndex] = 0;
                    _waypoints[unitIndex] = Array.Empty<int>();
                    _waypointPathIndices[unitIndex] = Array.Empty<int>();
                    _waypointCursor[unitIndex] = 0;
                    _routeGoalX[unitIndex] = targetX;
                    _routeGoalY[unitIndex] = targetY;
                    _routeFootprintX[unitIndex] = footprintX;
                    _routeFootprintY[unitIndex] = footprintY;
                    _routeOwner[unitIndex] = owner;
                    _routeRefreshAtSeconds[unitIndex] = 0f;
                    _failedGoalX[unitIndex] = targetX;
                    _failedGoalY[unitIndex] = targetY;
                    _failedStartCell[unitIndex] = startCell;
                    _failedFootprintX[unitIndex] = footprintX;
                    _failedFootprintY[unitIndex] = footprintY;
                    _failedTerrainVersion[unitIndex] = worldMap.TerrainVersion;
                    _failedRetryAfterUpdate[unitIndex] =
                        _updateSequence + FailedRouteRetryUpdates;
                    RoutesFailed++;
                    RoutesFailedThisUpdate++;
                    // A completed search failure is not a reason to blindly
                    // approach an unreachable target.
                    waypoint = default;
                    return false;
                }
            }
            else if (refreshDue)
            {
                // Search budget is shared across pawns. Preserve the old safe
                // route and try again after a short delay.
                _routeRefreshAtSeconds[unitIndex] =
                    _navigationTimeSeconds + RouteRefreshRetrySeconds;
                RegisterRouteTraffic(unitIndex);
            }
            else
            {
                // Do not follow an invalid route while waiting for a replacement.
                _lastStatuses[unitIndex] = failedAlready
                    ? UnitNavigationStatus.CachedFailure
                    : UnitNavigationStatus.WaitingForRouteBudget;
                if (!failedAlready &&
                    TryGetDirectFallbackWaypoint(
                        worldMap, position, target, footprintX, footprintY, out waypoint))
                {
                    _lastStatuses[unitIndex] =
                        UnitNavigationStatus.MovingTowardGoalWhileWaitingForRoute;
                    return true;
                }
                waypoint = default;
                return false;
            }
        }

        if (route.Length == 0 || waypoints.Length == 0 ||
            waypointPathIndices.Length != waypoints.Length)
        {
            _lastStatuses[unitIndex] = UnitNavigationStatus.NoUsableWaypoints;
            waypoint = default;
            return false;
        }

        AdvanceWaypointCursor(
            worldMap, units, unitIndex, position, target,
            route, waypoints, waypointPathIndices,
            ref cursor, ref waypointCursor);

        if (waypointCursor < waypoints.Length)
        {
            int cell = waypoints[waypointCursor];
            waypoint = GetWaypointPosition(cell);
            _lastStatuses[unitIndex] = _routeIsPartial[unitIndex]
                ? UnitNavigationStatus.FollowingPartialRoute
                : UnitNavigationStatus.FollowingRoute;
            return true;
        }

        // A partial route ends at a safe search frontier. The next A* request
        // rebuilds from the pawn's new position.
        if (_routeIsPartial[unitIndex] &&
            TryGetDirectFallbackWaypoint(
                worldMap, position, target, footprintX, footprintY, out waypoint))
        {
            _lastStatuses[unitIndex] =
                UnitNavigationStatus.MovingTowardGoalWhileWaitingForRoute;
            return true;
        }

        // A complete path finishes the remaining sub-tile distance to the goal.
        if (!_routeIsPartial[unitIndex] &&
            targetX == _routeGoalX[unitIndex] &&
            targetY == _routeGoalY[unitIndex])
        {
            waypoint = new Vector2(target.X, target.Y);
            _lastStatuses[unitIndex] = UnitNavigationStatus.FollowingRoute;
            return true;
        }

        waypoint = default;
        return false;
    }

    /// <summary>
    /// Validates one short movement segment against the same terrain/footprint
    /// rules used by navigation. Used by the movement integrator to prevent
    /// inertia from carrying a unit through a blocked cell.
    /// </summary>
    public bool CanMoveDirectly(
        UnitStore units,
        int unitIndex,
        WorldMap worldMap,
        Vector2 start,
        Vector2 end)
    {
        EnsureUnitCapacity(units.Capacity);
        EnsureWorkspace(worldMap);
        EnsureNavigationGrid(worldMap);
        return HasClearStraightRouteToPoint(
            worldMap,
            start,
            end,
            GetFootprintRadius(units.Width[unitIndex]),
            GetFootprintRadius(units.Length[unitIndex]));
    }

    public bool TryResolveNavigableGoal(
        UnitStore units,
        int unitIndex,
        WorldMap worldMap,
        Vector2 requested,
        HashSet<int>? reservedFootprintCells,
        out Vector2 goal)
    {
        goal = default;
        if ((uint)unitIndex >= (uint)units.Capacity)
            return false;

        EnsureUnitCapacity(units.Capacity);
        EnsureWorkspace(worldMap);
        EnsureNavigationGrid(worldMap);

        int radiusX = GetFootprintRadius(units.Width[unitIndex]);
        int radiusY = GetFootprintRadius(units.Length[unitIndex]);
        int centerX = WorldToNavigationCell(requested.X, _width);
        int centerY = WorldToNavigationCell(requested.Y, _height);

        // Keep the same physical 24 m search radius on the finer grid.
        const int maximumSearchRadius = 24 * NavigationCellsPerMeter;
        for (int radius = 0; radius <= maximumSearchRadius; radius++)
        {
            float bestDistance = float.PositiveInfinity;
            int bestX = -1;
            int bestY = -1;

            for (int y = Math.Max(radiusY, centerY - radius);
                 y <= Math.Min(_height - 1 - radiusY, centerY + radius); y++)
            {
                for (int x = Math.Max(radiusX, centerX - radius);
                     x <= Math.Min(_width - 1 - radiusX, centerX + radius); x++)
                {
                    if (Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY)) != radius)
                        continue;

                    bool reserved = false;
                    if (reservedFootprintCells != null)
                    {
                        for (int offsetY = -radiusY; offsetY <= radiusY && !reserved; offsetY++)
                        {
                            for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
                            {
                                int occupied = x + offsetX + (y + offsetY) * _width;
                                if (reservedFootprintCells.Contains(occupied))
                                {
                                    reserved = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (reserved ||
                        !IsFootprintPassable(worldMap, x, y, radiusX, radiusY))
                    {
                        continue;
                    }

                    Vector2 candidatePosition = GetWaypointPosition(x + y * _width);
                    float dx = candidatePosition.X - requested.X;
                    float dy = candidatePosition.Y - requested.Y;
                    float score = dx * dx + dy * dy;
                    if (score >= bestDistance)
                        continue;

                    bestDistance = score;
                    bestX = x;
                    bestY = y;
                }
            }

            if (bestX < 0)
                continue;

            float cellOriginX = bestX * NavigationCellSize;
            float cellOriginY = bestY * NavigationCellSize;
            float requestedOffsetX = requested.X -
                MathF.Floor(requested.X / NavigationCellSize) * NavigationCellSize;
            float requestedOffsetY = requested.Y -
                MathF.Floor(requested.Y / NavigationCellSize) * NavigationCellSize;
            float resolvedOffsetX = bestX == centerX
                ? requested.X - cellOriginX
                : requestedOffsetX;
            float resolvedOffsetY = bestY == centerY
                ? requested.Y - cellOriginY
                : requestedOffsetY;
            float inset = NavigationCellSize * 0.12f;
            goal = new Vector2(
                cellOriginX + Math.Clamp(resolvedOffsetX, inset, NavigationCellSize - inset),
                cellOriginY + Math.Clamp(resolvedOffsetY, inset, NavigationCellSize - inset));
            if (reservedFootprintCells != null)
            {
                for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
                {
                    for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
                    {
                        reservedFootprintCells.Add(
                            bestX + offsetX + (bestY + offsetY) * _width);
                    }
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Picks the furthest safe point along the straight segment toward the goal,
    /// bounded to a short look-ahead. This is the hot path: it needs no graph
    /// search while nearby terrain is traversable.
    /// </summary>
    private bool TryGetHotPathWaypoint(
        WorldMap worldMap,
        Vector3 position,
        Vector3 target,
        int footprintX,
        int footprintY,
        out Vector2 waypoint,
        out bool obstacleAhead)
    {
        obstacleAhead = false;
        Vector2 start = new(position.X, position.Y);
        Vector2 goal = new(target.X, target.Y);
        Vector2 delta = goal - start;
        float distance = delta.Length();

        if (distance <= 0.05f)
        {
            waypoint = goal;
            return true;
        }

        float stepDistance = MathF.Min(HotPathLookAheadDistance, distance);
        Vector2 candidate = start + delta * (stepDistance / distance);
        bool directSegmentClear = HasClearStraightRouteToPoint(
            worldMap, start, candidate, footprintX, footprintY);
        if (directSegmentClear)
        {
            if (IsWeightedStraightRouteAcceptable(
                    worldMap, start, candidate, footprintX, footprintY))
            {
                waypoint = candidate;
                return true;
            }

            // A clear but expensive segment is exactly where weighted A*
            // should compare alternate corridors. Do not inch through it.
            obstacleAhead = true;
            waypoint = default;
            return false;
        }

        // The look-ahead intersects a hard obstacle. Find the closest safe,
        // low-cost point before it; if progress is negligible, hand control to A*.
        obstacleAhead = true;
        float safeDistance = 0f;
        float blockedDistance = stepDistance;
        for (int attempt = 0; attempt < 7; attempt++)
        {
            float testDistance = (safeDistance + blockedDistance) * 0.5f;
            if (testDistance < 0.1f)
                break;

            candidate = start + delta * (testDistance / distance);
            if (IsWeightedStraightRouteAcceptable(
                    worldMap, start, candidate, footprintX, footprintY))
            {
                safeDistance = testDistance;
            }
            else
            {
                blockedDistance = testDistance;
            }
        }

        if (safeDistance >= HotPathMinimumProgress)
        {
            waypoint = start + delta * (safeDistance / distance);
            return true;
        }

        waypoint = default;
        return false;
    }

    private bool TryGetDirectFallbackWaypoint(
        WorldMap worldMap,
        Vector3 position,
        Vector3 target,
        int footprintX,
        int footprintY,
        out Vector2 waypoint)
    {
        Vector2 start = new(position.X, position.Y);
        Vector2 goal = new(target.X, target.Y);
        Vector2 delta = goal - start;
        float distance = delta.Length();

        if (distance <= 0.05f)
        {
            waypoint = goal;
            return true;
        }

        float stepDistance = MathF.Min(DirectFallbackDistance, distance);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Vector2 candidate = start + delta * (stepDistance / distance);
            if (IsWeightedStraightRouteAcceptable(
                    worldMap, start, candidate, footprintX, footprintY))
            {
                waypoint = candidate;
                return true;
            }

            stepDistance *= 0.5f;
            if (stepDistance < 0.25f)
                break;
        }

        waypoint = default;
        return false;
    }

    /// <summary>
    /// Returns a safe point ahead on the smoothed route, allowing units to pass
    /// intermediate control points without treating each point as a stopping place.
    /// </summary>
    public bool TryGetLookAheadWaypoint(
        UnitStore units,
        int unitIndex,
        WorldMap worldMap,
        Vector3 position,
        Vector3 target,
        float currentSpeed,
        out Vector2 waypoint,
        out float distanceToGoal,
        out float distanceToNextTurn,
        out float turnSeverity)
    {
        waypoint = default;
        distanceToGoal = 0f;
        distanceToNextTurn = float.PositiveInfinity;
        turnSeverity = 0f;

        if (!TryGetWaypoint(units, unitIndex, worldMap, position, target, out Vector2 currentWaypoint))
            return false;

        Vector2 position2 = new(position.X, position.Y);
        if (_lastStatuses[unitIndex] ==
            UnitNavigationStatus.MovingTowardGoalWhileWaitingForRoute)
        {
            // Do not let a stale cached route replace the short fallback point
            // returned while the next route chunk is waiting for search budget.
            waypoint = currentWaypoint;
            distanceToGoal = Vector2.Distance(
                position2, new Vector2(target.X, target.Y));
            return true;
        }

        // A sub-cell goal is already reachable inside the current passable tile.
        // Do not route the unit through that tile's centre before reaching the actual goal.
        if (WorldToNavigationCell(position.X, _width) == WorldToNavigationCell(target.X, _width) &&
            WorldToNavigationCell(position.Y, _height) == WorldToNavigationCell(target.Y, _height))
        {
            waypoint = new Vector2(target.X, target.Y);
            distanceToGoal = Vector2.Distance(
                new Vector2(position.X, position.Y),
                new Vector2(target.X, target.Y));
            distanceToNextTurn = float.PositiveInfinity;
            turnSeverity = 0f;
            return true;
        }

        int[] waypoints = _waypoints[unitIndex] ?? Array.Empty<int>();
        int cursor = Math.Clamp(_waypointCursor[unitIndex], 0, waypoints.Length);
        Vector2 previous = position2;
        Vector2 previousDirection = Vector2.Zero;
        float walked = 0f;

        for (int i = cursor; i < waypoints.Length; i++)
        {
            Vector2 next = GetWaypointPosition(waypoints[i]);
            Vector2 segment = next - previous;
            float length = segment.Length();
            if (length > 0.0001f)
            {
                Vector2 direction = segment / length;
                if (previousDirection != Vector2.Zero)
                    RecordTurn(previousDirection, direction, walked,
                        ref distanceToNextTurn, ref turnSeverity);

                walked += length;
                previousDirection = direction;
            }
            previous = next;
        }

        Vector2 finalTarget = new(target.X, target.Y);
        Vector2 finalSegment = finalTarget - previous;
        float finalLength = finalSegment.Length();
        if (finalLength > 0.0001f)
        {
            Vector2 direction = finalSegment / finalLength;
            if (previousDirection != Vector2.Zero && finalLength > 0.75f)
                RecordTurn(previousDirection, direction, walked,
                    ref distanceToNextTurn, ref turnSeverity);
            walked += finalLength;
        }

        distanceToGoal = walked;

        int footprintX = GetFootprintRadius(units.Width[unitIndex]);
        int footprintY = GetFootprintRadius(units.Length[unitIndex]);
        UnitId owner = units.GetId(unitIndex);
        Vector2 cachedOffset = _localTetherPoints[unitIndex] - position2;
        bool cachedTetherMatches =
            _localTetherValid[unitIndex] &&
            _localTetherOwners[unitIndex] == owner &&
            _localTetherTerrainVersions[unitIndex] == worldMap.TerrainVersion &&
            Vector2.DistanceSquared(_localTetherGoals[unitIndex], finalTarget) <= 0.0004f &&
            _localTetherPathCursors[unitIndex] == _pathCursor[unitIndex] &&
            _localTetherWaypointCursors[unitIndex] == cursor;
        bool cachedTetherAhead =
            currentSpeed <= 0.05f ||
            Vector2.Dot(cachedOffset, new Vector2(
                units.Velocity[unitIndex].X,
                units.Velocity[unitIndex].Y)) >= -0.05f;
        if (cachedTetherMatches &&
            _navigationTimeSeconds < _localTetherRefreshAtSeconds[unitIndex] &&
            cachedOffset.LengthSquared() >
                LocalTetherRefreshDistance * LocalTetherRefreshDistance &&
            cachedTetherAhead &&
            HasClearStraightRouteToPoint(
                worldMap, position2, _localTetherPoints[unitIndex], footprintX, footprintY))
        {
            waypoint = _localTetherPoints[unitIndex];
            return true;
        }

        float lookAheadDistance = Math.Clamp(
            2.6f + MathF.Max(0f, currentSpeed) * 0.48f,
            2.6f,
            4.8f);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            Vector2 candidate = GetPointAlongRoute(
                position2, finalTarget, waypoints, cursor, lookAheadDistance);
            if (HasClearStraightRouteToPoint(
                    worldMap, position2, candidate, footprintX, footprintY))
            {
                candidate = OffsetLocalTetherInsideCorridor(
                    worldMap, unitIndex, position2, candidate, footprintX, footprintY);

                _localTetherPoints[unitIndex] = candidate;
                _localTetherGoals[unitIndex] = finalTarget;
                _localTetherOwners[unitIndex] = owner;
                _localTetherPathCursors[unitIndex] = _pathCursor[unitIndex];
                _localTetherWaypointCursors[unitIndex] = cursor;
                _localTetherTerrainVersions[unitIndex] = worldMap.TerrainVersion;
                _localTetherRefreshAtSeconds[unitIndex] =
                    _navigationTimeSeconds + LocalTetherRefreshIntervalSeconds;
                _localTetherValid[unitIndex] = true;
                waypoint = candidate;
                return true;
            }

            lookAheadDistance *= 0.65f;
        }

        // A route cell is not automatically reachable from the pawn's current
        // position: it may have drifted past a corner or been displaced by contact.
        // Never steer blindly into that point; let the next update rebuild the route.
        InvalidateLocalTether(unitIndex);
        if (!HasClearStraightRouteToPoint(
                worldMap, position2, currentWaypoint, footprintX, footprintY))
        {
            waypoint = default;
            return false;
        }

        waypoint = currentWaypoint;
        return true;
    }

    private Vector2 OffsetLocalTetherInsideCorridor(
        WorldMap worldMap,
        int unitIndex,
        Vector2 start,
        Vector2 centerlinePoint,
        int footprintX,
        int footprintY)
    {
        Vector2 direction = centerlinePoint - start;
        float lengthSquared = direction.LengthSquared();
        if (lengthSquared <= 0.01f)
            return centerlinePoint;

        direction /= MathF.Sqrt(lengthSquared);
        Vector2 side = new(-direction.Y, direction.X);
        float laneOffset = GetStableLaneOffset(unitIndex);
        for (int attempt = 0; attempt < 5 && MathF.Abs(laneOffset) >= 0.035f; attempt++)
        {
            Vector2 candidate = centerlinePoint + side * laneOffset;
            if (IsWeightedStraightRouteAcceptable(
                    worldMap, start, candidate, footprintX, footprintY))
            {
                return candidate;
            }

            laneOffset *= 0.55f;
        }

        return centerlinePoint;
    }

    private static float GetStableLaneOffset(int unitIndex)
    {
        // Golden-ratio spacing distributes nearby unit indices across the
        // corridor without allocating per-unit random state or frame noise.
        float phase = (unitIndex * 0.61803398875f) % 1f;
        return (phase * 2f - 1f) * LocalTetherMaxLaneOffset;
    }

    private void InvalidateLocalTether(int unitIndex)
    {
        if ((uint)unitIndex < (uint)_localTetherValid.Length)
            _localTetherValid[unitIndex] = false;
    }

    private Vector2 GetWaypointPosition(int cell) =>
        new(
            (cell % _width + 0.5f) * NavigationCellSize,
            (cell / _width + 0.5f) * NavigationCellSize);

    private static int WorldToNavigationCell(float coordinate, int gridLength) =>
        Math.Clamp(
            (int)MathF.Floor(coordinate * NavigationCellsPerMeter),
            0,
            gridLength - 1);

    private static float SampleSurfaceHeight(WorldMap worldMap, float x, float y)
    {
        if (worldMap.UsesDiscreteBlockTerrain)
            return worldMap.GetSurfaceHeightAt(x, y);

        int x0 = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
        int x1 = Math.Min(x0 + 1, worldMap.MaxTileX);
        int y1 = Math.Min(y0 + 1, worldMap.MaxTileY);

        float tx = Math.Clamp(x - x0, 0f, 1f);
        float ty = Math.Clamp(y - y0, 0f, 1f);
        float z00 = worldMap.GetSurfaceHeight(x0, y0);
        float z10 = worldMap.GetSurfaceHeight(x1, y0);
        float z01 = worldMap.GetSurfaceHeight(x0, y1);
        float z11 = worldMap.GetSurfaceHeight(x1, y1);

        float top = z00 + (z10 - z00) * tx;
        float bottom = z01 + (z11 - z01) * tx;
        return top + (bottom - top) * ty;
    }

    private static int ToWorldTile(int navigationCell) =>
        navigationCell / NavigationCellsPerMeter;

    private static void RecordTurn(
        Vector2 incoming,
        Vector2 outgoing,
        float distance,
        ref float distanceToNextTurn,
        ref float turnSeverity)
    {
        float dot = Math.Clamp(Vector2.Dot(incoming, outgoing), -1f, 1f);
        if (dot >= 0.94f || distance >= distanceToNextTurn)
            return;

        distanceToNextTurn = distance;
        turnSeverity = Math.Clamp((0.98f - dot) / 0.98f, 0f, 1f);
    }

    private Vector2 GetPointAlongRoute(
        Vector2 start,
        Vector2 target,
        int[] waypoints,
        int cursor,
        float lookAheadDistance)
    {
        Vector2 previous = start;
        float remaining = lookAheadDistance;

        for (int i = cursor; i < waypoints.Length; i++)
        {
            Vector2 next = GetWaypointPosition(waypoints[i]);
            Vector2 segment = next - previous;
            float length = segment.Length();
            if (length > 0.0001f)
            {
                if (remaining <= length)
                    return previous + segment * (remaining / length);
                remaining -= length;
            }
            previous = next;
        }

        Vector2 finalSegment = target - previous;
        float finalLength = finalSegment.Length();
        if (finalLength > 0.0001f && remaining < finalLength)
            return previous + finalSegment * (remaining / finalLength);

        return target;
    }

    private bool AdvanceWaypointCursor(
        WorldMap worldMap,
        UnitStore units,
        int unitIndex,
        Vector3 position,
        Vector3 target,
        int[] route,
        int[] waypoints,
        int[] waypointPathIndices,
        ref int cursor,
        ref int waypointCursor)
    {
        Vector2 current = new(position.X, position.Y);
        int footprintX = GetFootprintRadius(units.Width[unitIndex]);
        int footprintY = GetFootprintRadius(units.Length[unitIndex]);
        bool passedBlockedCorner = false;
        while (waypointCursor < waypoints.Length)
        {
            Vector2 waypoint = GetWaypointPosition(waypoints[waypointCursor]);
            Vector2 offset = current - waypoint;
            bool reached = offset.LengthSquared() <= WaypointRadius * WaypointRadius;
            bool passed = false;

            if (!reached)
            {
                Vector2 previous = waypointCursor == 0
                    ? new Vector2(_routeStartX[unitIndex], _routeStartY[unitIndex])
                    : GetWaypointPosition(waypoints[waypointCursor - 1]);
                Vector2 incoming = waypoint - previous;
                float length = incoming.Length();
                if (length > 0.0001f)
                {
                    Vector2 direction = incoming / length;
                    float forward = Vector2.Dot(offset, direction);
                    float lateral = MathF.Abs(offset.X * direction.Y - offset.Y * direction.X);

                    Vector3 storedVelocity = units.Velocity[unitIndex];
                    Vector2 planarVelocity = new(storedVelocity.X, storedVelocity.Y);
                    float speed = planarVelocity.Length();

                    // A waypoint is a path control point, not a physical stop.
                    // Let the cursor pass it when inertia carries the pawn through
                    // the turn corridor; otherwise the controller would reverse
                    // toward a waypoint it can no longer reach cleanly.
                    float bodyTolerance = MathF.Max(
                        0.9f,
                        MathF.Max(units.Width[unitIndex], units.Length[unitIndex]) * 0.5f + 0.25f);
                    float inertiaTolerance = MathF.Min(4f, speed * 0.8f);
                    float passTolerance = MathF.Max(bodyTolerance, inertiaTolerance);
                    bool movingAlongIncoming = speed <= 0.08f ||
                        Vector2.Dot(planarVelocity, direction) >= -0.05f;

                    bool finalPointBehind = waypointCursor == waypoints.Length - 1 &&
                        Vector2.Dot(new Vector2(target.X, target.Y) - waypoint, direction) < -0.05f &&
                        Vector2.Distance(current, new Vector2(target.X, target.Y)) > 0.4f;

                    if (forward >= 0f &&
                        lateral <= passTolerance &&
                        movingAlongIncoming &&
                        !finalPointBehind)
                    {
                        // Do not skip a corner unless the next leg is also visible
                        // from the pawn's actual position. Otherwise it can cut
                        // through the inside of a ruined wall and oscillate at it.
                        Vector2 nextPoint = waypointCursor + 1 < waypoints.Length
                            ? GetWaypointPosition(waypoints[waypointCursor + 1])
                            : new Vector2(target.X, target.Y);
                        passed = HasClearStraightRouteToPoint(
                            worldMap, current, nextPoint, footprintX, footprintY);
                        if (!passed)
                        {
                            // The pawn has already crossed this corner, but the
                            // next leg is not visible from its actual position.
                            // Rebuild from here instead of steering backwards to
                            // a waypoint that inertia has carried behind the pawn.
                            passedBlockedCorner = true;
                            break;
                        }
                    }
                }
            }

            if (passedBlockedCorner || (!reached && !passed))
                break;

            int reachedRouteIndex = waypointPathIndices[waypointCursor];
            if (_routeTrafficRegistered[unitIndex])
            {
                for (int i = cursor; i <= reachedRouteIndex && i < route.Length; i++)
                    DecrementRouteTraffic(route[i]);
            }

            cursor = Math.Max(cursor, reachedRouteIndex + 1);
            waypointCursor++;
        }

        _pathCursor[unitIndex] = cursor;
        _waypointCursor[unitIndex] = waypointCursor;
        if (waypointCursor >= waypoints.Length)
            _routeTrafficRegistered[unitIndex] = false;

        return passedBlockedCorner;
    }

    public void ClearRoute(int unitIndex) =>
        ClearRoute(unitIndex, preserveLocalTether: false);

    private void ClearRoute(int unitIndex, bool preserveLocalTether)
    {
        if ((uint)unitIndex >= (uint)_paths.Length ||
            _paths[unitIndex] == null)
        {
            return;
        }

        if (!preserveLocalTether)
            InvalidateLocalTether(unitIndex);

        RemoveRouteTraffic(unitIndex);
        _paths[unitIndex] = Array.Empty<int>();
        _lastStatuses[unitIndex] = UnitNavigationStatus.Unknown;
        _pathCursor[unitIndex] = 0;
        _waypoints[unitIndex] = Array.Empty<int>();
        _waypointPathIndices[unitIndex] = Array.Empty<int>();
        _waypointCursor[unitIndex] = 0;
        _routeOwner[unitIndex] = default;
        _failedGoalX[unitIndex] = int.MinValue;
        _failedGoalY[unitIndex] = int.MinValue;
        _failedRetryAfterUpdate[unitIndex] = 0;
        _routeIsPartial[unitIndex] = false;
        _routeRefreshAtSeconds[unitIndex] = 0f;
    }

    private bool TryBuildPath(
        WorldMap worldMap,
        int startX,
        int startY,
        int targetX,
        int targetY,
        int footprintRadiusX,
        int footprintRadiusY,
        out int[] path,
        out bool partialRoute)
    {
        path = Array.Empty<int>();
        partialRoute = false;
        _lastSearchCellsExpanded = 0;
        _lastSearchTrafficCellsConsidered = 0;
        _lastBuildFailureStatus = UnitNavigationStatus.PathNotFound;

        if (!IsInside(startX, startY) ||
            !IsFootprintPassable(worldMap, startX, startY, footprintRadiusX, footprintRadiusY))
        {
            _lastBuildFailureStatus = UnitNavigationStatus.StartBlocked;
            return false;
        }

        if (!IsInside(targetX, targetY) ||
            !IsFootprintPassable(worldMap, targetX, targetY, footprintRadiusX, footprintRadiusY))
        {
            _lastBuildFailureStatus = UnitNavigationStatus.GoalBlocked;
            return false;
        }

        int start = startX + startY * _width;
        int goal = targetX + targetY * _width;

        // Cheaply reject routes across disconnected terrain islands. Without
        // this, a failed request can scan all 588k+ map cells just to conclude
        // that a canyon or blocked passage cannot be crossed.
        if (footprintRadiusX == 0 &&
            footprintRadiusY == 0 &&
            _navigationComponent[start] != _navigationComponent[goal])
        {
            _lastBuildFailureStatus = UnitNavigationStatus.DisconnectedTerrain;
            return false;
        }

        if (start == goal)
        {
            path = new[] { start };
            return true;
        }

        int stamp = NextSearchStamp();
        _heapCount = 0;

        Touch(start, stamp);
        _gScore[start] = 0f;
        _fScore[start] = Heuristic(startX, startY, targetX, targetY);
        HeapPush(start);

        // Keep two rolling frontiers:
        // 1) the hot-path frontier with the greatest safe progress toward the goal;
        // 2) the old heuristic frontier as a fallback for routes that must first
        //    move sideways or briefly away from the goal to get around a barrier.
        Vector2 goalDirection = new(targetX - startX, targetY - startY);
        float goalDistance = goalDirection.Length();
        if (goalDistance > 0.0001f)
            goalDirection /= goalDistance;
        else
            goalDirection = Vector2.UnitX;
        Vector2 goalSide = new(-goalDirection.Y, goalDirection.X);

        int bestHotPathCell = start;
        float bestHotPathScore = 0f;
        int bestPartialCell = start;
        float bestPartialScore =
            _gScore[start] - 0.35f * Heuristic(startX, startY, targetX, targetY);

        while (_heapCount > 0 &&
               _lastSearchCellsExpanded < MaxExpandedNodesPerSearch)
        {
            int current = HeapPop();
            if (_gScore[current] > MaxRouteChunkCost)
                continue;

            _lastSearchCellsExpanded++;

            int x = current % _width;
            int y = current / _width;
            float partialScore =
                _gScore[current] - 0.35f * Heuristic(x, y, targetX, targetY);
            if (partialScore > bestPartialScore + 0.0001f)
            {
                bestPartialScore = partialScore;
                bestPartialCell = current;
            }

            // A* still validates the route around terrain, but its next chunk
            // ends at the most useful reachable point along the goal direction.
            // Cross-track drift and extra detour cost are tie-break penalties,
            // not hard limits, so an obstacle can still force a wide detour.
            float offsetX = x - startX;
            float offsetY = y - startY;
            float forwardProgress =
                (offsetX * goalDirection.X + offsetY * goalDirection.Y) * NavigationCellSize;
            float lateralDrift = MathF.Abs(
                offsetX * goalSide.X + offsetY * goalSide.Y) * NavigationCellSize;
            float hotPathScore =
                forwardProgress - lateralDrift * 0.35f -
                _gScore[current] * HotPathCostPenalty;
            if (hotPathScore > bestHotPathScore + 0.0001f)
            {
                bestHotPathScore = hotPathScore;
                bestHotPathCell = current;
            }

            if (current == goal)
            {
                path = ReconstructPath(start, goal);
                return path.Length > 0;
            }

            _closedStamp[current] = stamp;

            for (int direction = 0; direction < DirectionX.Length; direction++)
            {
                int dx = DirectionX[direction];
                int dy = DirectionY[direction];
                int nx = x + dx;
                int ny = y + dy;

                if (!IsInside(nx, ny) ||
                    !CanStep(worldMap, x, y, nx, ny, dx, dy, footprintRadiusX, footprintRadiusY))
                {
                    continue;
                }

                int neighbor = nx + ny * _width;

                if (_closedStamp[neighbor] == stamp)
                    continue;

                Touch(neighbor, stamp);

                int priorRouteCount = _routeTraffic[neighbor];
                if (priorRouteCount > 0)
                {
                    _lastSearchTrafficCellsConsidered++;
                    RouteTrafficCellsConsideredThisUpdate++;
                }

                // The search, Theta* shortcuts and straight-route validation
                // must all use the same terrain-aware edge cost.
                float tentativeG =
                    _gScore[current] + GetStepCost(current, neighbor, dx, dy);
                int parentCell = current;

                // Theta*: bypass cell-to-cell turns when the parent's continuous
                // segment is visible and preserves the same terrain clearance.
                int ancestor = _parent[current];
                if (ancestor >= 0 &&
                    ancestor != current &&
                    CanTravelBetweenCells(
                        worldMap,
                        ancestor,
                        neighbor,
                        footprintRadiusX,
                        footprintRadiusY,
                        out float anyAngleCost))
                {
                    float anyAngleG = _gScore[ancestor] + anyAngleCost;
                    if (anyAngleG < tentativeG)
                    {
                        tentativeG = anyAngleG;
                        parentCell = ancestor;
                    }
                }

                if (tentativeG > MaxRouteChunkCost || tentativeG >= _gScore[neighbor])
                    continue;

                _parent[neighbor] = parentCell;
                _gScore[neighbor] = tentativeG;
                _fScore[neighbor] =
                    tentativeG + Heuristic(nx, ny, targetX, targetY);

                if (_heapPosition[neighbor] < 0)
                    HeapPush(neighbor);
                else
                    SiftUp(_heapPosition[neighbor]);
            }
        }

        // A distant command is approached as a rolling sequence of local A*
        // chunks. The next search starts at the new position and can choose a
        // different curve as nearby obstacles and route traffic change.
        int nextHotPoint = bestHotPathCell != start
            ? bestHotPathCell
            : bestPartialCell;
        if (nextHotPoint != start)
        {
            int[] partialPath = ReconstructPath(start, nextHotPoint);
            if (partialPath.Length > 1)
            {
                path = partialPath;
                partialRoute = true;
                return true;
            }
        }

        return false;
    }

    private void BuildSmoothedPath(
        WorldMap worldMap,
        int[] path,
        float startX,
        float startY,
        int footprintRadiusX,
        int footprintRadiusY,
        out int[] waypoints,
        out int[] waypointPathIndices)
    {
        if (path.Length <= 1)
        {
            waypoints = path;
            waypointPathIndices = path.Length == 0 ? Array.Empty<int>() : new[] { 0 };
            return;
        }

        var cells = new int[path.Length];
        var indices = new int[path.Length];
        int count = 0;
        int anchor = 0;

        // Use the unit's real position for the first segment instead of pulling
        // it to the center of its starting cell first.
        int first = 0;
        int lastCandidate = Math.Min(path.Length - 1, MaxWaypointShortcutCells);
        for (int candidate = lastCandidate; candidate >= 1; candidate--)
        {
            if (!CanShortcutPreservingPathCost(
                    worldMap,
                    startX,
                    startY,
                    path,
                    0,
                    candidate,
                    footprintRadiusX,
                    footprintRadiusY))
            {
                continue;
            }

            first = candidate;
            break;
        }

        cells[count] = path[first];
        indices[count++] = first;
        anchor = first;

        // Greedy string-pulling: choose the farthest cell up to a fixed lookahead
        // that can be reached in a straight, fully traversable segment.
        while (anchor < path.Length - 1)
        {
            int selected = anchor + 1;
            lastCandidate = Math.Min(
                path.Length - 1,
                anchor + MaxWaypointShortcutCells);

            for (int candidate = lastCandidate; candidate > anchor + 1; candidate--)
            {
                if (!CanShortcutPreservingPathCost(
                        worldMap,
                        GetWaypointPosition(path[anchor]).X,
                        GetWaypointPosition(path[anchor]).Y,
                        path,
                        anchor,
                        candidate,
                        footprintRadiusX,
                        footprintRadiusY))
                {
                    continue;
                }

                selected = candidate;
                break;
            }

            cells[count] = path[selected];
            indices[count++] = selected;
            anchor = selected;
        }

        Array.Resize(ref cells, count);
        Array.Resize(ref indices, count);
        waypoints = cells;
        waypointPathIndices = indices;
    }

    private bool HasClearStraightRoute(
        WorldMap worldMap,
        float startX,
        float startY,
        int targetCell,
        int footprintRadiusX,
        int footprintRadiusY)
    {
        if ((uint)targetCell >= (uint)_cellCount)
            return false;

        int x = WorldToNavigationCell(startX, _width);
        int y = WorldToNavigationCell(startY, _height);
        int targetX = targetCell % _width;
        int targetY = targetCell / _width;

        return CanTravelStraight(
            worldMap,
            startX,
            startY,
            x,
            y,
            targetX,
            targetY,
            footprintRadiusX,
            footprintRadiusY);
    }

    private bool CanTravelBetweenCells(
        WorldMap worldMap,
        int startCell,
        int targetCell,
        int footprintRadiusX,
        int footprintRadiusY)
    {
        int startX = startCell % _width;
        int startY = startCell / _width;
        int targetX = targetCell % _width;
        int targetY = targetCell / _width;

        Vector2 startPosition = GetWaypointPosition(startCell);
        Vector2 targetPosition = GetWaypointPosition(targetCell);
        return CanTravelStraightToPoint(
            worldMap,
            startPosition.X,
            startPosition.Y,
            startX,
            startY,
            targetX,
            targetY,
            targetPosition.X,
            targetPosition.Y,
            footprintRadiusX,
            footprintRadiusY);
    }

    private bool CanTravelBetweenCells(
        WorldMap worldMap,
        int startCell,
        int targetCell,
        int footprintRadiusX,
        int footprintRadiusY,
        out float travelCost)
    {
        int startX = startCell % _width;
        int startY = startCell / _width;
        int targetX = targetCell % _width;
        int targetY = targetCell / _width;
        Vector2 startPosition = GetWaypointPosition(startCell);
        Vector2 targetPosition = GetWaypointPosition(targetCell);

        return CanTravelStraightToPoint(
            worldMap,
            startPosition.X,
            startPosition.Y,
            startX,
            startY,
            targetX,
            targetY,
            targetPosition.X,
            targetPosition.Y,
            footprintRadiusX,
            footprintRadiusY,
            out travelCost);
    }

    private bool CanShortcutPreservingPathCost(
        WorldMap worldMap,
        float startX,
        float startY,
        int[] path,
        int anchor,
        int candidate,
        int footprintRadiusX,
        int footprintRadiusY)
    {
        if (candidate <= anchor ||
            !CanTravelStraightToPoint(
                worldMap,
                startX,
                startY,
                WorldToNavigationCell(startX, _width),
                WorldToNavigationCell(startY, _height),
                path[candidate] % _width,
                path[candidate] / _width,
                GetWaypointPosition(path[candidate]).X,
                GetWaypointPosition(path[candidate]).Y,
                footprintRadiusX,
                footprintRadiusY,
                out float shortcutCost))
        {
            return false;
        }

        float originalPathCost = _gScore[path[candidate]] - _gScore[path[anchor]];

        // String-pulling may remove bends, but it must not replace the A* route
        // with a geometrically shorter segment that crosses more expensive cells.
        return shortcutCost <= originalPathCost + ShortcutCostTolerance;
    }

    private float GetStepCost(int fromCell, int toCell, int dx, int dy) =>
        (dx != 0 && dy != 0 ? DiagonalCost : 1f) * NavigationCellSize +
        GetStepPenalty(fromCell, toCell);

    private float GetStepPenalty(int fromCell, int toCell)
    {
        float heightDifference = MathF.Abs(
            _navigationHeights[fromCell] - _navigationHeights[toCell]);

        int deltaX = Math.Abs(toCell % _width - fromCell % _width);
        int deltaY = Math.Abs(toCell / _width - fromCell / _width);
        float horizontalDistance =
            (deltaX != 0 && deltaY != 0 ? DiagonalCost : 1f) * NavigationCellSize;

        // Price a slope by its actual 3D travel distance, not only by the
        // vertical difference. A steep but technically traversable face must
        // compete fairly with a longer, flatter route around the obstacle.
        float terrainDistance = MathF.Sqrt(
            horizontalDistance * horizontalDistance +
            heightDifference * heightDifference);
        float heightCost =
            terrainDistance - horizontalDistance +
            heightDifference * HeightCost;

        // Clearance is a property of the whole edge, so use both cells.
        // Route traffic stays destination-weighted to spread units across paths.
        float edgeCost = (_navigationEdgeCost[fromCell] +
            _navigationEdgeCost[toCell]) * 0.5f;

        int priorRouteCount = _routeTraffic[toCell];
        float routeReuseCost = Math.Min(
            priorRouteCount,
            RouteCohortSize - 1) * RouteReuseCostPerUnit * NavigationCellSize;

        return heightCost + edgeCost + routeReuseCost;
    }

    private bool IsWeightedStraightRouteAcceptable(
        WorldMap worldMap,
        Vector2 start,
        Vector2 end,
        int footprintRadiusX,
        int footprintRadiusY)
    {
        int startX = WorldToNavigationCell(start.X, _width);
        int startY = WorldToNavigationCell(start.Y, _height);
        int targetX = WorldToNavigationCell(end.X, _width);
        int targetY = WorldToNavigationCell(end.Y, _height);

        if (!CanTravelStraightToPoint(
                worldMap,
                start.X,
                start.Y,
                startX,
                startY,
                targetX,
                targetY,
                end.X,
                end.Y,
                footprintRadiusX,
                footprintRadiusY,
                out _,
                out float weightedPenalty))
        {
            return false;
        }

        float distance = Vector2.Distance(start, end);
        float maximumPenalty =
            distance * HotPathMaximumWeightedPenaltyPerMeter +
            HotPathWeightedPenaltyTolerance;
        return weightedPenalty <= maximumPenalty;
    }

    private bool CanTravelStraight(
        WorldMap worldMap,
        float startX,
        float startY,
        int x,
        int y,
        int targetX,
        int targetY,
        int footprintRadiusX,
        int footprintRadiusY) =>
        CanTravelStraightToPoint(
            worldMap, startX, startY, x, y, targetX, targetY,
            (targetX + 0.5f) * NavigationCellSize,
            (targetY + 0.5f) * NavigationCellSize,
            footprintRadiusX, footprintRadiusY);

    private bool HasClearStraightRouteToPoint(
        WorldMap worldMap,
        Vector2 start,
        Vector2 end,
        int footprintRadiusX,
        int footprintRadiusY)
    {
        int startX = WorldToNavigationCell(start.X, _width);
        int startY = WorldToNavigationCell(start.Y, _height);
        int targetX = WorldToNavigationCell(end.X, _width);
        int targetY = WorldToNavigationCell(end.Y, _height);
        return CanTravelStraightToPoint(
            worldMap, start.X, start.Y, startX, startY,
            targetX, targetY, end.X, end.Y, footprintRadiusX, footprintRadiusY);
    }

    private bool CanTravelStraightToPoint(
        WorldMap worldMap,
        float startX,
        float startY,
        int x,
        int y,
        int targetX,
        int targetY,
        float endX,
        float endY,
        int footprintRadiusX,
        int footprintRadiusY) =>
        CanTravelStraightToPoint(
            worldMap, startX, startY, x, y, targetX, targetY,
            endX, endY, footprintRadiusX, footprintRadiusY, out _, out _);

    private bool CanTravelStraightToPoint(
        WorldMap worldMap,
        float startX,
        float startY,
        int x,
        int y,
        int targetX,
        int targetY,
        float endX,
        float endY,
        int footprintRadiusX,
        int footprintRadiusY,
        out float travelCost) =>
        CanTravelStraightToPoint(
            worldMap, startX, startY, x, y, targetX, targetY,
            endX, endY, footprintRadiusX, footprintRadiusY, out travelCost, out _);

    private bool CanTravelStraightToPoint(
        WorldMap worldMap,
        float startX,
        float startY,
        int x,
        int y,
        int targetX,
        int targetY,
        float endX,
        float endY,
        int footprintRadiusX,
        int footprintRadiusY,
        out float travelCost,
        out float weightedPenalty)
    {
        travelCost = 0f;
        weightedPenalty = 0f;
        if (!IsInside(x, y) ||
            !IsInside(targetX, targetY) ||
            !IsFootprintPassable(worldMap, x, y, footprintRadiusX, footprintRadiusY) ||
            !IsFootprintPassable(worldMap, targetX, targetY, footprintRadiusX, footprintRadiusY))
        {
            return false;
        }
        startX *= NavigationCellsPerMeter;
        startY *= NavigationCellsPerMeter;
        endX *= NavigationCellsPerMeter;
        endY *= NavigationCellsPerMeter;
        float deltaX = endX - startX;
        float deltaY = endY - startY;

        int stepX = Math.Sign(deltaX);
        int stepY = Math.Sign(deltaY);

        if (x == targetX && y == targetY)
            return true;

        // If starting exactly on a boundary and travelling negative, the ray
        // immediately enters the cell on the negative side.
        if (stepX < 0 && MathF.Abs(startX - MathF.Round(startX)) < 0.00001f)
            x--;
        if (stepY < 0 && MathF.Abs(startY - MathF.Round(startY)) < 0.00001f)
            y--;

        if (!IsInside(x, y) ||
            !IsFootprintPassable(worldMap, x, y, footprintRadiusX, footprintRadiusY))
        {
            return false;
        }

        float deltaStepX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / deltaX);
        float deltaStepY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / deltaY);
        float nextBoundaryX = stepX > 0 ? x + 1f : x;
        float nextBoundaryY = stepY > 0 ? y + 1f : y;
        float maxX = stepX == 0 ? float.PositiveInfinity : (nextBoundaryX - startX) / deltaX;
        float maxY = stepY == 0 ? float.PositiveInfinity : (nextBoundaryY - startY) / deltaY;

        int maxSteps = _width + _height + 4;
        for (int step = 0; (x != targetX || y != targetY) && step < maxSteps; step++)
        {
            int moveX;
            int moveY;
            if (maxX < maxY - 0.000001f)
            {
                moveX = stepX;
                moveY = 0;
            }
            else if (maxY < maxX - 0.000001f)
            {
                moveX = 0;
                moveY = stepY;
            }
            else
            {
                moveX = stepX;
                moveY = stepY;
            }

            if (moveX == 0 && moveY == 0)
                return false;

            int nextX = x + moveX;
            int nextY = y + moveY;
            if (!CanStep(
                    worldMap,
                    x,
                    y,
                    nextX,
                    nextY,
                    moveX,
                    moveY,
                    footprintRadiusX,
                    footprintRadiusY))
            {
                return false;
            }

            int fromCell = x + y * _width;
            int toCell = nextX + nextY * _width;
            travelCost += GetStepCost(fromCell, toCell, moveX, moveY);
            weightedPenalty += GetStepPenalty(fromCell, toCell);

            x = nextX;
            y = nextY;
            if (moveX != 0) maxX += deltaStepX;
            if (moveY != 0) maxY += deltaStepY;
        }

        return x == targetX && y == targetY;
    }

    private bool CanStep(
        WorldMap worldMap,
        int x,
        int y,
        int nx,
        int ny,
        int dx,
        int dy,
        int footprintRadiusX,
        int footprintRadiusY)
    {
        if (footprintRadiusX == 0 && footprintRadiusY == 0)
        {
            int direction = dy == 0
                ? (dx > 0 ? 0 : 1)
                : dx == 0
                    ? (dy > 0 ? 2 : 3)
                    : dx > 0
                        ? (dy > 0 ? 4 : 5)
                        : (dy > 0 ? 6 : 7);

            int cell = x + y * _width;
            return (_navigationNeighbors[cell] & (1 << direction)) != 0;
        }

        // Check the full rectangular footprint, not just the unit's center.
        // Every footprint cell must have a surface and be traversable.
        for (int offsetY = -footprintRadiusY; offsetY <= footprintRadiusY; offsetY++)
        {
            for (int offsetX = -footprintRadiusX; offsetX <= footprintRadiusX; offsetX++)
            {
                if (!CanStepCell(
                        worldMap,
                        x + offsetX,
                        y + offsetY,
                        nx + offsetX,
                        ny + offsetY,
                        dx,
                        dy))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool CanStepCell(
        WorldMap worldMap,
        int x,
        int y,
        int nx,
        int ny,
        int dx,
        int dy)
    {
        if (!IsSurface(worldMap, x, y) || !IsSurface(worldMap, nx, ny))
            return false;

        float currentHeight = _navigationHeights[x + y * _width];
        float nextHeight = _navigationHeights[nx + ny * _width];
        float heightDifference = MathF.Abs(nextHeight - currentHeight);
        if (worldMap.UsesDiscreteBlockTerrain)
        {
            int fromWorldX = ToWorldTile(x);
            int fromWorldY = ToWorldTile(y);
            int toWorldX = ToWorldTile(nx);
            int toWorldY = ToWorldTile(ny);

            if (fromWorldX == toWorldX && fromWorldY == toWorldY)
            {
                if (!worldMap.Blocks.IsTransitionCell(fromWorldX, fromWorldY) &&
                    heightDifference > 0.0001f)
                {
                    return false;
                }
            }
            else if (!worldMap.Blocks.CanTraverse(
                         fromWorldX, fromWorldY, toWorldX, toWorldY))
            {
                return false;
            }
        }
        else if (!IsSlopePassable(heightDifference, dx, dy))
        {
            return false;
        }

        if (dx != 0 && dy != 0 &&
            (!CanCardinalStep(worldMap, x, y, x + dx, y) ||
             !CanCardinalStep(worldMap, x, y, x, y + dy) ||
             !CanCardinalStep(worldMap, x + dx, y, nx, ny) ||
             !CanCardinalStep(worldMap, x, y + dy, nx, ny)))
            return false;

        return true;
    }

    private bool IsFootprintPassable(
        WorldMap worldMap,
        int x,
        int y,
        int radiusX,
        int radiusY)
    {
        if (!IsInside(x, y))
            return false;

        float centerHeight = _navigationHeights[x + y * _width];
        for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
        {
            for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
            {
                int cellX = x + offsetX;
                int cellY = y + offsetY;
                if (!IsInside(cellX, cellY))
                    return false;

                int index = cellX + cellY * _width;
                int distanceX = Math.Abs(offsetX);
                int distanceY = Math.Abs(offsetY);
                float horizontalDistance =
                    MathF.Sqrt(distanceX * distanceX + distanceY * distanceY) * NavigationCellSize;
                float allowedHeightDifference = MathF.Min(
                    MaxStepHeight,
                    horizontalDistance * MaxTerrainSlopeGrade);
                float heightDifference = MathF.Abs(
                    _navigationHeights[index] - centerHeight);
                bool transitionFootprint = worldMap.UsesDiscreteBlockTerrain &&
                    (worldMap.Blocks.IsTransitionCell(
                         ToWorldTile(x),
                         ToWorldTile(y)) ||
                     worldMap.Blocks.IsTransitionCell(
                         ToWorldTile(cellX),
                         ToWorldTile(cellY)));
                if (!_navigationPassable[index] ||
                    (!transitionFootprint &&
                     heightDifference > allowedHeightDifference + 0.0001f))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int GetFootprintRadius(float size)
    {
        // Expand the unit to the navigation-cell grid. Include every cell
        // touched by its half-extent plus half a navigation cell; otherwise
        // sub-metre pawns are treated as points and can overhang block edges.
        float halfExtent = MathF.Max(0.1f, size) * 0.5f;
        float cellsBeyondCenter =
            halfExtent / NavigationCellSize - 0.5f;
        return Math.Max(0, (int)MathF.Ceiling(cellsBeyondCenter));
    }

    private bool CanCardinalStep(
        WorldMap worldMap,
        int x,
        int y,
        int nx,
        int ny)
    {
        if (!IsSurface(worldMap, x, y) || !IsSurface(worldMap, nx, ny))
            return false;

        return CanStepCell(
            worldMap,
            x,
            y,
            nx,
            ny,
            nx - x,
            ny - y);
    }

    private static bool IsSlopePassable(
        float heightDifference,
        int dx,
        int dy)
    {
        if (heightDifference > MaxStepHeight)
            return false;

        float horizontalDistance =
            (dx != 0 && dy != 0 ? DiagonalCost : 1f) * NavigationCellSize;
        return heightDifference <=
            horizontalDistance * MaxTerrainSlopeGrade + 0.0001f;
    }

    private bool IsSurface(WorldMap worldMap, int x, int y)
    {
        if (!IsInside(x, y))
            return false;

        return _navigationPassable[x + y * _width];
    }

    private bool IsInside(int x, int y)
    {
        return x >= 0 && y >= 0 && x < _width && y < _height;
    }

    private static int ClampCell(int value, int length)
    {
        return Math.Clamp(value, 0, length - 1);
    }

    private static float Heuristic(int x, int y, int targetX, int targetY)
    {
        int dx = Math.Abs(targetX - x);
        int dy = Math.Abs(targetY - y);
        int diagonal = Math.Min(dx, dy);

        return (diagonal * DiagonalCost + Math.Max(dx, dy) - diagonal) * NavigationCellSize;
    }

    private int[] ReconstructPath(int start, int goal)
    {
        int count = 0;
        int cell = goal;

        while (cell >= 0 && count < _reversePath.Length)
        {
            _reversePath[count++] = cell;

            if (cell == start)
                break;

            cell = _parent[cell];
        }

        if (count == 0 || _reversePath[count - 1] != start)
            return Array.Empty<int>();

        int[] path = new int[count];

        for (int i = 0; i < count; i++)
            path[i] = _reversePath[count - i - 1];

        return path;
    }

    private void Touch(int cell, int stamp)
    {
        if (_seenStamp[cell] == stamp)
            return;

        _seenStamp[cell] = stamp;
        _gScore[cell] = float.PositiveInfinity;
        _fScore[cell] = float.PositiveInfinity;
        _parent[cell] = -1;
        _heapPosition[cell] = -1;
    }

    private int NextSearchStamp()
    {
        if (_searchStamp == int.MaxValue)
        {
            Array.Clear(_seenStamp);
            Array.Clear(_closedStamp);
            _searchStamp = 0;
        }

        return ++_searchStamp;
    }

    private void HeapPush(int cell)
    {
        int index = _heapCount++;
        _heap[index] = cell;
        _heapPosition[cell] = index;
        SiftUp(index);
    }

    private int HeapPop()
    {
        int result = _heap[0];
        _heapPosition[result] = -1;
        _heapCount--;

        if (_heapCount > 0)
        {
            int last = _heap[_heapCount];
            _heap[0] = last;
            _heapPosition[last] = 0;
            SiftDown(0);
        }

        return result;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parentIndex = (index - 1) / 2;

            if (!HeapLess(_heap[index], _heap[parentIndex]))
                break;

            SwapHeap(index, parentIndex);
            index = parentIndex;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            int left = index * 2 + 1;
            if (left >= _heapCount)
                return;

            int right = left + 1;
            int best = right < _heapCount &&
                HeapLess(_heap[right], _heap[left])
                    ? right
                    : left;

            if (!HeapLess(_heap[best], _heap[index]))
                return;

            SwapHeap(index, best);
            index = best;
        }
    }

    private bool HeapLess(int leftCell, int rightCell)
    {
        float left = _fScore[leftCell];
        float right = _fScore[rightCell];

        if (left != right)
            return left < right;

        if (_gScore[leftCell] != _gScore[rightCell])
            return _gScore[leftCell] > _gScore[rightCell];

        return leftCell < rightCell;
    }

    private void SwapHeap(int left, int right)
    {
        int temp = _heap[left];
        _heap[left] = _heap[right];
        _heap[right] = temp;
        _heapPosition[_heap[left]] = left;
        _heapPosition[_heap[right]] = right;
    }

    private void EnsureUnitCapacity(int capacity)
    {
        if (_paths.Length >= capacity)
            return;

        int newCapacity = Math.Max(capacity, Math.Max(16, _paths.Length * 2));
        Array.Resize(ref _paths, newCapacity);
        Array.Resize(ref _lastStatuses, newCapacity);
        Array.Resize(ref _pathCursor, newCapacity);
        Array.Resize(ref _waypoints, newCapacity);
        Array.Resize(ref _waypointPathIndices, newCapacity);
        Array.Resize(ref _waypointCursor, newCapacity);
        Array.Resize(ref _routeGoalX, newCapacity);
        Array.Resize(ref _routeGoalY, newCapacity);
        Array.Resize(ref _routeStartX, newCapacity);
        Array.Resize(ref _routeStartY, newCapacity);
        Array.Resize(ref _routeFootprintX, newCapacity);
        Array.Resize(ref _routeFootprintY, newCapacity);
        Array.Resize(ref _failedFootprintX, newCapacity);
        Array.Resize(ref _failedFootprintY, newCapacity);
        Array.Resize(ref _routeOwner, newCapacity);
        Array.Resize(ref _failedGoalX, newCapacity);
        Array.Resize(ref _failedGoalY, newCapacity);
        Array.Resize(ref _failedStartCell, newCapacity);
        Array.Resize(ref _failedTerrainVersion, newCapacity);
        Array.Resize(ref _failedRetryAfterUpdate, newCapacity);
        Array.Resize(ref _routeTerrainVersion, newCapacity);
        Array.Resize(ref _routeTrafficRegistered, newCapacity);
        Array.Resize(ref _routeIsPartial, newCapacity);
        Array.Resize(ref _routeRefreshAtSeconds, newCapacity);
        Array.Resize(ref _localTetherPoints, newCapacity);
        Array.Resize(ref _localTetherGoals, newCapacity);
        Array.Resize(ref _localTetherOwners, newCapacity);
        Array.Resize(ref _localTetherPathCursors, newCapacity);
        Array.Resize(ref _localTetherWaypointCursors, newCapacity);
        Array.Resize(ref _localTetherTerrainVersions, newCapacity);
        Array.Resize(ref _localTetherRefreshAtSeconds, newCapacity);
        Array.Resize(ref _localTetherValid, newCapacity);

        for (int i = 0; i < newCapacity; i++)
        {
            if (_paths[i] == null)
            {
                _paths[i] = Array.Empty<int>();
                _failedGoalX[i] = int.MinValue;
                _failedGoalY[i] = int.MinValue;
                _failedStartCell[i] = -1;
                _failedFootprintX[i] = int.MinValue;
                _failedFootprintY[i] = int.MinValue;
            }

            if (_waypoints[i] == null)
                _waypoints[i] = Array.Empty<int>();
            if (_waypointPathIndices[i] == null)
                _waypointPathIndices[i] = Array.Empty<int>();
        }
    }

    private void EnsureWorkspace(WorldMap worldMap)
    {
        if (_width == worldMap.TileWidth * NavigationCellsPerMeter &&
            _height == worldMap.TileHeight * NavigationCellsPerMeter)
        {
            return;
        }

        _width = checked(worldMap.TileWidth * NavigationCellsPerMeter);
        _height = checked(worldMap.TileHeight * NavigationCellsPerMeter);
        _cellCount = checked(_width * _height);

        _gScore = new float[_cellCount];
        _fScore = new float[_cellCount];
        _routeTraffic = new int[_cellCount];
        _routeTrafficTerrainVersion = long.MinValue;
        Array.Clear(_routeTrafficRegistered);
        _navigationHeights = new float[_cellCount];
        _navigationEdgeCost = new float[_cellCount];
        _navigationPassable = new bool[_cellCount];
        _navigationNeighbors = new byte[_cellCount];
        _navigationComponent = new int[_cellCount];
        _componentQueue = new int[_cellCount];
        _navigationGridTerrainVersion = long.MinValue;
        _parent = new int[_cellCount];
        _seenStamp = new int[_cellCount];
        _closedStamp = new int[_cellCount];
        _heap = new int[_cellCount];
        _heapPosition = new int[_cellCount];
        _reversePath = new int[_cellCount];

        Array.Clear(_paths);
        Array.Clear(_pathCursor);
        Array.Clear(_waypoints);
        Array.Clear(_waypointPathIndices);
        Array.Clear(_waypointCursor);
        Array.Clear(_routeOwner);
        Array.Clear(_localTetherValid);
        _searchStamp = 0;
    }

    private void EnsureRouteTrafficVersion(WorldMap worldMap)
    {
        if (_routeTrafficTerrainVersion == worldMap.TerrainVersion)
            return;

        Array.Clear(_routeTraffic);
        Array.Clear(_routeTrafficRegistered);
        _routeTrafficTerrainVersion = worldMap.TerrainVersion;
    }

    private void RegisterRouteTraffic(int unitIndex)
    {
        if (_routeTrafficRegistered[unitIndex])
            return;

        int[]? path = _paths[unitIndex];
        if (path == null || path.Length == 0)
            return;

        int start = Math.Clamp(_pathCursor[unitIndex], 0, path.Length);
        for (int i = start; i < path.Length; i++)
        {
            int cell = path[i];
            if ((uint)cell < (uint)_routeTraffic.Length)
                _routeTraffic[cell]++;
        }

        _routeTrafficRegistered[unitIndex] = true;
    }

    private void RemoveRouteTraffic(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_routeTrafficRegistered.Length ||
            !_routeTrafficRegistered[unitIndex])
        {
            return;
        }

        int[]? path = _paths[unitIndex];
        if (path != null)
        {
            int start = Math.Clamp(_pathCursor[unitIndex], 0, path.Length);
            for (int i = start; i < path.Length; i++)
                DecrementRouteTraffic(path[i]);
        }

        _routeTrafficRegistered[unitIndex] = false;
    }

    private void DecrementRouteTraffic(int cell)
    {
        if ((uint)cell < (uint)_routeTraffic.Length &&
            _routeTraffic[cell] > 0)
        {
            _routeTraffic[cell]--;
        }
    }

    // Cache surface heights and legal 8-way steps once per terrain version.
    // A* expands many thousands of nodes per route; asking WorldMap to validate
    // every edge repeatedly was dominating mass-movement updates.
    private void EnsureNavigationGrid(WorldMap worldMap)
    {
        if (_navigationGridTerrainVersion == worldMap.TerrainVersion)
            return;

        long started = Stopwatch.GetTimestamp();

        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;

            for (int x = 0; x < _width; x++)
            {
                int cell = row + x;
                int worldX = ToWorldTile(x);
                int worldY = ToWorldTile(y);
                bool passable =
                    !worldMap.IsNavigationBlocked(worldX, worldY) &&
                    worldMap.GetSurfaceLayer(worldX, worldY) >= 0;

                float sampleX = (x + 0.5f) * NavigationCellSize;
                float sampleY = (y + 0.5f) * NavigationCellSize;
                _navigationPassable[cell] = passable;
                _navigationHeights[cell] =
                    SampleSurfaceHeight(worldMap, sampleX, sampleY);
                _navigationNeighbors[cell] = 0;
            }
        }

        // First build the cardinal edges.
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;

            for (int x = 0; x < _width; x++)
            {
                int cell = row + x;
                if (!_navigationPassable[cell])
                    continue;

                byte mask = 0;

                if (x + 1 < _width &&
                    _navigationPassable[cell + 1] &&
                    CanStepCell(worldMap, x, y, x + 1, y, 1, 0))
                    mask |= 1 << 0;

                if (x > 0 &&
                    _navigationPassable[cell - 1] &&
                    CanStepCell(worldMap, x, y, x - 1, y, -1, 0))
                    mask |= 1 << 1;

                if (y + 1 < _height &&
                    _navigationPassable[cell + _width] &&
                    CanStepCell(worldMap, x, y, x, y + 1, 0, 1))
                    mask |= 1 << 2;

                if (y > 0 &&
                    _navigationPassable[cell - _width] &&
                    CanStepCell(worldMap, x, y, x, y - 1, 0, -1))
                    mask |= 1 << 3;

                _navigationNeighbors[cell] = mask;
            }
        }

        // A diagonal is legal only when both orthogonal routes around its
        // corner exist, matching the old no-corner-cutting rule.
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;

            for (int x = 0; x < _width; x++)
            {
                int cell = row + x;
                byte mask = _navigationNeighbors[cell];
                if (!_navigationPassable[cell])
                    continue;

                if (x + 1 < _width && y + 1 < _height &&
                    (mask & (1 << 0)) != 0 &&
                    (mask & (1 << 2)) != 0 &&
                    (_navigationNeighbors[cell + 1] & (1 << 2)) != 0 &&
                    (_navigationNeighbors[cell + _width] & (1 << 0)) != 0 &&
                    CanStepCell(worldMap, x, y, x + 1, y + 1, 1, 1))
                    mask |= 1 << 4;

                if (x + 1 < _width && y > 0 &&
                    (mask & (1 << 0)) != 0 &&
                    (mask & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell + 1] & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell - _width] & (1 << 0)) != 0 &&
                    CanStepCell(worldMap, x, y, x + 1, y - 1, 1, -1))
                    mask |= 1 << 5;

                if (x > 0 && y + 1 < _height &&
                    (mask & (1 << 1)) != 0 &&
                    (mask & (1 << 2)) != 0 &&
                    (_navigationNeighbors[cell - 1] & (1 << 2)) != 0 &&
                    (_navigationNeighbors[cell + _width] & (1 << 1)) != 0 &&
                    CanStepCell(worldMap, x, y, x - 1, y + 1, -1, 1))
                    mask |= 1 << 6;

                if (x > 0 && y > 0 &&
                    (mask & (1 << 1)) != 0 &&
                    (mask & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell - 1] & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell - _width] & (1 << 1)) != 0 &&
                    CanStepCell(worldMap, x, y, x - 1, y - 1, -1, -1))
                    mask |= 1 << 7;

                _navigationNeighbors[cell] = mask;
            }
        }

        // A* used to treat every traversable cell as equally good, so shortest
        // paths often scraped along walls and steep contour edges. Prefer cells
        // with more open neighbours, but keep edge cells legal in narrow passages.
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int cell = row + x;
                if (!_navigationPassable[cell])
                {
                    _navigationEdgeCost[cell] = 0f;
                    continue;
                }

                byte mask = _navigationNeighbors[cell];
                int cardinalCount = 0;
                int diagonalCount = 0;
                for (int bit = 0; bit < 4; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                        cardinalCount++;
                    if ((mask & (1 << (bit + 4))) != 0)
                        diagonalCount++;
                }

                _navigationEdgeCost[cell] =
                    ((4 - cardinalCount) * 0.32f +
                     (4 - diagonalCount) * 0.08f) * NavigationCellSize;
            }
        }

        BuildNavigationComponents();

        _navigationGridTerrainVersion = worldMap.TerrainVersion;
        NavigationGridBuildMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void BuildNavigationComponents()
    {
        Array.Fill(_navigationComponent, -1);
        int componentId = 0;

        for (int start = 0; start < _cellCount; start++)
        {
            if (!_navigationPassable[start] ||
                _navigationComponent[start] >= 0)
            {
                continue;
            }

            int head = 0;
            int tail = 0;
            _componentQueue[tail++] = start;
            _navigationComponent[start] = componentId;

            while (head < tail)
            {
                int cell = _componentQueue[head++];
                int x = cell % _width;
                int y = cell / _width;
                byte neighbors = _navigationNeighbors[cell];

                for (int direction = 0; direction < DirectionX.Length; direction++)
                {
                    if ((neighbors & (1 << direction)) == 0)
                        continue;

                    int nx = x + DirectionX[direction];
                    int ny = y + DirectionY[direction];
                    if (!IsInside(nx, ny))
                        continue;

                    int next = nx + ny * _width;
                    if (_navigationComponent[next] >= 0)
                        continue;

                    _navigationComponent[next] = componentId;
                    _componentQueue[tail++] = next;
                }
            }

            componentId++;
        }
    }
}
