using System;
using System.Diagnostics;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

/// <summary>
/// Builds and caches terrain-aware routes. Path requests are throttled so
/// mass commands do not run hundreds of A* searches in one frame.
/// </summary>
public sealed class UnitNavigationSystem
{
    private const float MaxStepHeight = 1.5f;
    private const float HeightCost = 0.15f;
    private const int MaxRouteBuildsPerUpdate = 16;
    private const int MovingGoalTolerance = 4;
    private const float WaypointRadius = 0.12f;

    private static readonly int[] DirectionX =
        { 1, -1, 0, 0, 1, 1, -1, -1 };

    private static readonly int[] DirectionY =
        { 0, 0, 1, -1, 1, -1, 1, -1 };

    private static readonly float DiagonalCost =
        MathF.Sqrt(2f);

    private int[][] _paths = Array.Empty<int[]>();
    private int[] _pathCursor = Array.Empty<int>();
    private int[] _routeGoalX = Array.Empty<int>();
    private int[] _routeGoalY = Array.Empty<int>();
    private int[] _routeFootprintX = Array.Empty<int>();
    private int[] _routeFootprintY = Array.Empty<int>();
    private int[] _failedFootprintX = Array.Empty<int>();
    private int[] _failedFootprintY = Array.Empty<int>();
    private UnitId[] _routeOwner = Array.Empty<UnitId>();
    private int[] _failedGoalX = Array.Empty<int>();
    private int[] _failedGoalY = Array.Empty<int>();
    private int[] _failedStartCell = Array.Empty<int>();
    private long[] _failedTerrainVersion = Array.Empty<long>();
    private long[] _routeTerrainVersion = Array.Empty<long>();

    public int RoutesBuilt { get; private set; }
    public int RoutesFailed { get; private set; }
    public int RoutesBuiltThisUpdate { get; private set; }
    public int RoutesFailedThisUpdate { get; private set; }
    public int SearchesThisUpdate { get; private set; }
    public long CellsExpandedThisUpdate { get; private set; }
    public double SearchMillisecondsThisUpdate { get; private set; }
    public double NavigationGridBuildMilliseconds { get; private set; }

    private int _lastSearchCellsExpanded;

    private int _width;
    private int _height;
    private int _cellCount;
    private int _searchStamp;
    private int _routeBuildsRemaining;

    private float[] _gScore = Array.Empty<float>();
    private float[] _fScore = Array.Empty<float>();
    private float[] _navigationHeights = Array.Empty<float>();
    private bool[] _navigationPassable = Array.Empty<bool>();
    private byte[] _navigationNeighbors = Array.Empty<byte>();
    private long _navigationGridTerrainVersion = long.MinValue;
    private int[] _parent = Array.Empty<int>();
    private int[] _seenStamp = Array.Empty<int>();
    private int[] _closedStamp = Array.Empty<int>();
    private int[] _heap = Array.Empty<int>();
    private int[] _heapPosition = Array.Empty<int>();
    private int[] _reversePath = Array.Empty<int>();
    private int _heapCount;

    public void BeginUpdate(UnitStore units, WorldMap worldMap)
    {
        EnsureUnitCapacity(units.Capacity);
        EnsureWorkspace(worldMap);
        NavigationGridBuildMilliseconds = 0d;
        EnsureNavigationGrid(worldMap);
        _routeBuildsRemaining = MaxRouteBuildsPerUpdate;
        RoutesBuiltThisUpdate = 0;
        RoutesFailedThisUpdate = 0;
        SearchesThisUpdate = 0;
        CellsExpandedThisUpdate = 0;
        SearchMillisecondsThisUpdate = 0d;
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

        int startX = ClampCell((int)MathF.Floor(position.X), _width);
        int startY = ClampCell((int)MathF.Floor(position.Y), _height);
        int targetX = ClampCell((int)MathF.Floor(target.X), _width);
        int targetY = ClampCell((int)MathF.Floor(target.Y), _height);
        int startCell = startX + startY * _width;
        int footprintX = GetFootprintRadius(units.Width[unitIndex]);
        int footprintY = GetFootprintRadius(units.Length[unitIndex]);
        UnitId owner = units.GetId(unitIndex);

        bool sameOwner = _routeOwner[unitIndex] == owner;
        bool footprintChanged = sameOwner &&
            (_routeFootprintX[unitIndex] != footprintX ||
             _routeFootprintY[unitIndex] != footprintY);
        int length = sameOwner ? _paths[unitIndex]?.Length ?? 0 : 0;
        int cursor = sameOwner ? _pathCursor[unitIndex] : 0;
        bool targetFarMoved =
            sameOwner &&
            (Math.Abs(targetX - _routeGoalX[unitIndex]) > MovingGoalTolerance ||
             Math.Abs(targetY - _routeGoalY[unitIndex]) > MovingGoalTolerance);
        bool goalChanged = sameOwner &&
            (targetX != _routeGoalX[unitIndex] ||
             targetY != _routeGoalY[unitIndex]);
        bool routeFinished = length > 0 && cursor >= length;
        float distanceToGoalX = position.X - target.X;
        float distanceToGoalY = position.Y - target.Y;
        bool repositionedAwayFromFinishedRoute =
            routeFinished &&
            distanceToGoalX * distanceToGoalX + distanceToGoalY * distanceToGoalY > 2.25f;
        bool deviatedFromRoute = false;
        if (sameOwner && length > 0 && cursor < length)
        {
            int[] cachedPath = _paths[unitIndex] ?? Array.Empty<int>();
            if (cursor < cachedPath.Length)
            {
                int nextCell = cachedPath[cursor];
                float routeDx = position.X - (nextCell % _width + 0.5f);
                float routeDy = position.Y - (nextCell / _width + 0.5f);
                deviatedFromRoute = routeDx * routeDx + routeDy * routeDy > 9f;
            }
        }
        bool needsRoute =
            !sameOwner ||
            length == 0 ||
            targetFarMoved ||
            deviatedFromRoute ||
            (routeFinished && goalChanged) ||
            repositionedAwayFromFinishedRoute ||
            footprintChanged ||
            (sameOwner && _routeTerrainVersion[unitIndex] != worldMap.TerrainVersion);

        if (needsRoute)
        {
            bool failedAlready =
                sameOwner &&
                _failedGoalX[unitIndex] == targetX &&
                _failedGoalY[unitIndex] == targetY &&
                _failedStartCell[unitIndex] == startCell &&
                _failedFootprintX[unitIndex] == footprintX &&
                _failedFootprintY[unitIndex] == footprintY &&
                _failedTerrainVersion[unitIndex] == worldMap.TerrainVersion;

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
                    out int[] path);

                SearchesThisUpdate++;
                SearchMillisecondsThisUpdate +=
                    Stopwatch.GetElapsedTime(searchStarted).TotalMilliseconds;
                CellsExpandedThisUpdate += _lastSearchCellsExpanded;

                if (pathFound)
                {
                    _paths[unitIndex] = path;
                    _pathCursor[unitIndex] = 0;
                    _routeGoalX[unitIndex] = targetX;
                    _routeGoalY[unitIndex] = targetY;
                    _routeFootprintX[unitIndex] = footprintX;
                    _routeFootprintY[unitIndex] = footprintY;
                    _routeOwner[unitIndex] = owner;
                    _routeTerrainVersion[unitIndex] = worldMap.TerrainVersion;
                    RoutesBuilt++;
                    RoutesBuiltThisUpdate++;
                    _failedGoalX[unitIndex] = int.MinValue;
                    _failedGoalY[unitIndex] = int.MinValue;
                    length = path.Length;
                    cursor = 0;
                    sameOwner = true;
                }
                else
                {
                    _paths[unitIndex] = Array.Empty<int>();
                    _pathCursor[unitIndex] = 0;
                    _routeGoalX[unitIndex] = targetX;
                    _routeGoalY[unitIndex] = targetY;
                    _routeFootprintX[unitIndex] = footprintX;
                    _routeFootprintY[unitIndex] = footprintY;
                    _routeOwner[unitIndex] = owner;
                    _failedGoalX[unitIndex] = targetX;
                    _failedGoalY[unitIndex] = targetY;
                    _failedStartCell[unitIndex] = startCell;
                    _failedFootprintX[unitIndex] = footprintX;
                    _failedFootprintY[unitIndex] = footprintY;
                    _failedTerrainVersion[unitIndex] = worldMap.TerrainVersion;
                    RoutesFailed++;
                    RoutesFailedThisUpdate++;
                    waypoint = default;
                    return false;
                }
            }
            else
            {
                // Do not follow a stale route while waiting for the pathfinding budget.
                waypoint = default;
                return false;
            }
        }

        int[]? route = _paths[unitIndex];

        if (route == null || route.Length == 0)
        {
            waypoint = default;
            return false;
        }

        cursor = _pathCursor[unitIndex];

        // Skip cells already reached, but retain the route's final cell.
        while (cursor < route.Length)
        {
            int cell = route[cursor];
            float cellX = cell % _width + 0.5f;
            float cellY = cell / _width + 0.5f;
            float dx = position.X - cellX;
            float dy = position.Y - cellY;

            if (dx * dx + dy * dy > WaypointRadius * WaypointRadius)
                break;

            cursor++;
        }

        _pathCursor[unitIndex] = cursor;

        if (cursor < route.Length)
        {
            int cell = route[cursor];
            waypoint = new Vector2(
                cell % _width + 0.5f,
                cell / _width + 0.5f);
            return true;
        }

        // Once the route reaches the goal cell, finish the remaining sub-tile
        // distance to the requested target. A moving target is replanned above.
        if (targetX == _routeGoalX[unitIndex] &&
            targetY == _routeGoalY[unitIndex])
        {
            waypoint = new Vector2(target.X, target.Y);
            return true;
        }

        waypoint = default;
        return false;
    }

    public void ClearRoute(int unitIndex)
    {
        if ((uint)unitIndex >= (uint)_paths.Length ||
            _paths[unitIndex] == null)
        {
            return;
        }

        _paths[unitIndex] = Array.Empty<int>();
        _pathCursor[unitIndex] = 0;
        _routeOwner[unitIndex] = default;
        _failedGoalX[unitIndex] = int.MinValue;
        _failedGoalY[unitIndex] = int.MinValue;
    }

    private bool TryBuildPath(
        WorldMap worldMap,
        int startX,
        int startY,
        int targetX,
        int targetY,
        int footprintRadiusX,
        int footprintRadiusY,
        out int[] path)
    {
        path = Array.Empty<int>();
        _lastSearchCellsExpanded = 0;

        if (!IsInside(startX, startY) ||
            !IsInside(targetX, targetY) ||
            !IsFootprintPassable(worldMap, startX, startY, footprintRadiusX, footprintRadiusY) ||
            !IsFootprintPassable(worldMap, targetX, targetY, footprintRadiusX, footprintRadiusY))
        {
            return false;
        }

        int start = startX + startY * _width;
        int goal = targetX + targetY * _width;

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

        while (_heapCount > 0)
        {
            int current = HeapPop();
            _lastSearchCellsExpanded++;

            if (current == goal)
            {
                path = ReconstructPath(start, goal);
                return path.Length > 0;
            }

            _closedStamp[current] = stamp;

            int x = current % _width;
            int y = current / _width;

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

                float heightDifference = MathF.Abs(
                    _navigationHeights[current] -
                    _navigationHeights[neighbor]);

                float stepCost =
                    dx != 0 && dy != 0
                        ? DiagonalCost
                        : 1f;

                float tentativeG =
                    _gScore[current] +
                    stepCost +
                    heightDifference * HeightCost;

                if (tentativeG >= _gScore[neighbor])
                    continue;

                _parent[neighbor] = current;
                _gScore[neighbor] = tentativeG;
                _fScore[neighbor] =
                    tentativeG + Heuristic(nx, ny, targetX, targetY);

                if (_heapPosition[neighbor] < 0)
                    HeapPush(neighbor);
                else
                    SiftUp(_heapPosition[neighbor]);
            }
        }

        return false;
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

    private static bool CanStepCell(
        WorldMap worldMap,
        int x,
        int y,
        int nx,
        int ny,
        int dx,
        int dy)
    {
        if (!IsSurface(worldMap, x, y) ||
            !IsSurface(worldMap, nx, ny))
        {
            return false;
        }

        float currentHeight = worldMap.GetSurfaceHeight(x, y);
        float nextHeight = worldMap.GetSurfaceHeight(nx, ny);

        if (MathF.Abs(nextHeight - currentHeight) > MaxStepHeight)
            return false;

        if (dx != 0 && dy != 0 &&
            (!CanCardinalStep(worldMap, x, y, x + dx, y) ||
             !CanCardinalStep(worldMap, x, y, x, y + dy) ||
             !CanCardinalStep(worldMap, x + dx, y, nx, ny) ||
             !CanCardinalStep(worldMap, x, y + dy, nx, ny)))
        {
            return false;
        }

        return true;
    }

    private static bool IsFootprintPassable(
        WorldMap worldMap,
        int x,
        int y,
        int radiusX,
        int radiusY)
    {
        float centerHeight = worldMap.GetSurfaceHeight(x, y);

        for (int offsetY = -radiusY; offsetY <= radiusY; offsetY++)
        {
            for (int offsetX = -radiusX; offsetX <= radiusX; offsetX++)
            {
                int cellX = x + offsetX;
                int cellY = y + offsetY;

                if (worldMap.IsNavigationBlocked(cellX, cellY) ||
                    worldMap.GetSurfaceLayer(cellX, cellY) < 0)
                {
                    return false;
                }

                if (MathF.Abs(
                        worldMap.GetSurfaceHeight(cellX, cellY) - centerHeight) >
                    MaxStepHeight)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int GetFootprintRadius(float size)
    {
        return Math.Max(
            0,
            (int)MathF.Ceiling((MathF.Max(0.1f, size) - 1f) * 0.5f));
    }

    private static bool CanCardinalStep(
        WorldMap worldMap,
        int x,
        int y,
        int nx,
        int ny)
    {
        if (!IsSurface(worldMap, x, y) ||
            !IsSurface(worldMap, nx, ny))
        {
            return false;
        }

        return MathF.Abs(
            worldMap.GetSurfaceHeight(x, y) -
            worldMap.GetSurfaceHeight(nx, ny)) <= MaxStepHeight;
    }

    private static bool IsSurface(WorldMap worldMap, int x, int y)
    {
        return !worldMap.IsNavigationBlocked(x, y) &&
               worldMap.GetSurfaceLayer(x, y) >= 0;
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

        return diagonal * DiagonalCost + Math.Max(dx, dy) - diagonal;
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
        Array.Resize(ref _pathCursor, newCapacity);
        Array.Resize(ref _routeGoalX, newCapacity);
        Array.Resize(ref _routeGoalY, newCapacity);
        Array.Resize(ref _routeFootprintX, newCapacity);
        Array.Resize(ref _routeFootprintY, newCapacity);
        Array.Resize(ref _failedFootprintX, newCapacity);
        Array.Resize(ref _failedFootprintY, newCapacity);
        Array.Resize(ref _routeOwner, newCapacity);
        Array.Resize(ref _failedGoalX, newCapacity);
        Array.Resize(ref _failedGoalY, newCapacity);
        Array.Resize(ref _failedStartCell, newCapacity);
        Array.Resize(ref _failedTerrainVersion, newCapacity);
        Array.Resize(ref _routeTerrainVersion, newCapacity);

        for (int i = 0; i < newCapacity; i++)
        {
            if (i >= capacity || _paths[i] != null)
                continue;

            _paths[i] = Array.Empty<int>();
            _failedGoalX[i] = int.MinValue;
            _failedGoalY[i] = int.MinValue;
            _failedStartCell[i] = -1;
            _failedFootprintX[i] = int.MinValue;
            _failedFootprintY[i] = int.MinValue;
        }
    }

    private void EnsureWorkspace(WorldMap worldMap)
    {
        if (_width == worldMap.TileWidth &&
            _height == worldMap.TileHeight)
        {
            return;
        }

        _width = worldMap.TileWidth;
        _height = worldMap.TileHeight;
        _cellCount = checked(_width * _height);

        _gScore = new float[_cellCount];
        _fScore = new float[_cellCount];
        _navigationHeights = new float[_cellCount];
        _navigationPassable = new bool[_cellCount];
        _navigationNeighbors = new byte[_cellCount];
        _navigationGridTerrainVersion = long.MinValue;
        _parent = new int[_cellCount];
        _seenStamp = new int[_cellCount];
        _closedStamp = new int[_cellCount];
        _heap = new int[_cellCount];
        _heapPosition = new int[_cellCount];
        _reversePath = new int[_cellCount];

        Array.Clear(_paths);
        Array.Clear(_pathCursor);
        Array.Clear(_routeOwner);
        _searchStamp = 0;
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
                bool passable =
                    !worldMap.IsNavigationBlocked(x, y) &&
                    worldMap.GetSurfaceLayer(x, y) >= 0;

                _navigationPassable[cell] = passable;
                _navigationHeights[cell] = worldMap.GetSurfaceHeight(x, y);
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

                float height = _navigationHeights[cell];
                byte mask = 0;

                if (x + 1 < _width &&
                    _navigationPassable[cell + 1] &&
                    MathF.Abs(height - _navigationHeights[cell + 1]) <= MaxStepHeight)
                    mask |= 1 << 0;

                if (x > 0 &&
                    _navigationPassable[cell - 1] &&
                    MathF.Abs(height - _navigationHeights[cell - 1]) <= MaxStepHeight)
                    mask |= 1 << 1;

                if (y + 1 < _height &&
                    _navigationPassable[cell + _width] &&
                    MathF.Abs(height - _navigationHeights[cell + _width]) <= MaxStepHeight)
                    mask |= 1 << 2;

                if (y > 0 &&
                    _navigationPassable[cell - _width] &&
                    MathF.Abs(height - _navigationHeights[cell - _width]) <= MaxStepHeight)
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
                    (_navigationNeighbors[cell + _width] & (1 << 0)) != 0)
                    mask |= 1 << 4;

                if (x + 1 < _width && y > 0 &&
                    (mask & (1 << 0)) != 0 &&
                    (mask & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell + 1] & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell - _width] & (1 << 0)) != 0)
                    mask |= 1 << 5;

                if (x > 0 && y + 1 < _height &&
                    (mask & (1 << 1)) != 0 &&
                    (mask & (1 << 2)) != 0 &&
                    (_navigationNeighbors[cell - 1] & (1 << 2)) != 0 &&
                    (_navigationNeighbors[cell + _width] & (1 << 1)) != 0)
                    mask |= 1 << 6;

                if (x > 0 && y > 0 &&
                    (mask & (1 << 1)) != 0 &&
                    (mask & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell - 1] & (1 << 3)) != 0 &&
                    (_navigationNeighbors[cell - _width] & (1 << 1)) != 0)
                    mask |= 1 << 7;

                _navigationNeighbors[cell] = mask;
            }
        }

        _navigationGridTerrainVersion = worldMap.TerrainVersion;
        NavigationGridBuildMilliseconds =
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
