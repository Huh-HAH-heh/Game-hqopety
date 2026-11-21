using System;
using System.Collections.Generic;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

/// <summary>
/// Local multi-level A* over walkable surfaces sampled from the 3D voxel field.
/// Untouched height-map terrain keeps one compact surface node per navigation
/// cell; edited 3D chunk columns may contribute several vertically separated
/// nodes (bridge deck, tunnel floor, cave floor, etc.).
/// </summary>
public sealed class VoxelNavigationSystem
{
    public const int CellsPerMeter = 2;
    public const float CellSizeMeters = 1f / CellsPerMeter;

    private const int RegionSizeMeters = 128;
    private const int CellsPerRegion = RegionSizeMeters * CellsPerMeter;
    private const float MaximumStepHeight = 0.62f;
    private const float MaximumGrade = 1.05f;
    private const float NodeArrivalRadius = 0.28f;
    private const int MaximumCachedRegions = 4;
    private const int MaximumSurfaceNodesPerCell = 24;
    private static readonly int[] DirectionX = { 1, -1, 0, 0 };
    private static readonly int[] DirectionY = { 0, 0, 1, -1 };

    private readonly record struct RegionKey(int X, int Y);

    private readonly record struct Node(
        int CellX,
        int CellY,
        float X,
        float Y,
        float Height,
        float Clearance,
        ushort MaterialId);

    private sealed class Graph
    {
        public required RegionKey Key { get; init; }
        public required long TerrainVersion { get; init; }
        public required int OriginCellX { get; init; }
        public required int OriginCellY { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int[][] CellNodes { get; init; }
        public required Node[] Nodes { get; init; }
    }

    private sealed class Route
    {
        public UnitId Owner;
        public RegionKey Region;
        public long TerrainVersion;
        public int GoalNode;
        public float TargetX;
        public float TargetY;
        public float TargetHeight;
        public int[] Nodes = Array.Empty<int>();
        public int Cursor;
    }

    private sealed class MultiRegionRoute
    {
        public UnitId Owner;
        public RegionKey[] Regions = Array.Empty<RegionKey>();
        public Vector3[] Waypoints = Array.Empty<Vector3>();
        public float TargetX;
        public float TargetY;
        public float TargetHeight;
        public int Cursor;
    }

    private readonly Dictionary<RegionKey, Graph> _graphs = new();
    private readonly Dictionary<int, Route> _routes = new();
    private readonly Dictionary<int, MultiRegionRoute> _multiRegionRoutes = new();
    private WorldMap? _observedMap;
    private long _observedNavigationVersion = long.MinValue;

    public int CachedRegionCount => _graphs.Count;
    public int CachedRouteCount => _routes.Count + _multiRegionRoutes.Count;

    /// <summary>
    /// Returns true from ownsVoxelRegion when the requested route is inside a
    /// region containing edited voxel chunks. In that case a missing waypoint
    /// means there is no supported multi-level route; callers must not silently
    /// fall back to the old top-surface-only path.
    /// </summary>
    public bool TryGetWaypoint(
        WorldMap worldMap,
        UnitId unitId,
        Vector3 position,
        Vector3 target,
        float bodyHeight,
        out bool ownsVoxelRegion,
        out Vector3 waypoint)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        ownsVoxelRegion = false;
        waypoint = default;

        if (!IsFinite(position) || !IsFinite(target) ||
            position.X < 0f || position.Y < 0f ||
            target.X < 0f || target.Y < 0f ||
            position.X > worldMap.MaxTileX ||
            position.Y > worldMap.MaxTileY ||
            target.X > worldMap.MaxTileX ||
            target.Y > worldMap.MaxTileY)
            return false;

        SynchronizeTerrainVersion(worldMap);
        int startRegionX = (int)MathF.Floor(position.X / RegionSizeMeters);
        int startRegionY = (int)MathF.Floor(position.Y / RegionSizeMeters);
        int goalRegionX = (int)MathF.Floor(target.X / RegionSizeMeters);
        int goalRegionY = (int)MathF.Floor(target.Y / RegionSizeMeters);
        RegionKey startRegion = new(startRegionX, startRegionY);
        RegionKey goalRegion = new(goalRegionX, goalRegionY);
        if (startRegion != goalRegion)
        {
            return TryGetHierarchicalWaypoint(
                worldMap, unitId, position, target, bodyHeight,
                startRegion, goalRegion, out ownsVoxelRegion, out waypoint);
        }

        RegionKey key = startRegion;
        if (!RegionHasVoxelEdits(worldMap, key))
            return false;

        Graph? graph = GetOrBuildGraph(worldMap, key);
        if (graph == null)
            return false;

        ownsVoxelRegion = true;
        if (graph.Nodes.Length == 0)
            return false;

        float preferredGoalHeight = target.Z > 0.01f ? target.Z : position.Z;
        float requiredClearance = MathF.Max(0.1f, bodyHeight);
        int startNode = FindNearestNode(
            graph, position.X, position.Y, position.Z, requiredClearance, maximumCellRadius: 3);
        int goalNode = FindNearestNode(
            graph, target.X, target.Y, preferredGoalHeight, requiredClearance, maximumCellRadius: 5);
        if (startNode < 0 || goalNode < 0)
            return false;

        if (!_routes.TryGetValue(unitId.Index, out Route? route) ||
            route.Owner != unitId ||
            route.Region != key ||
            route.GoalNode != goalNode ||
            MathF.Abs(route.TargetX - target.X) > 0.35f ||
            MathF.Abs(route.TargetY - target.Y) > 0.35f ||
            MathF.Abs(route.TargetHeight - preferredGoalHeight) > 0.25f)
        {
            int[] path = FindPath(graph, startNode, goalNode, requiredClearance);
            if (path.Length == 0)
            {
                _routes.Remove(unitId.Index);
                return false;
            }

            route = new Route
            {
                Owner = unitId,
                Region = key,
                TerrainVersion = worldMap.NavigationVersion,
                GoalNode = goalNode,
                TargetX = target.X,
                TargetY = target.Y,
                TargetHeight = preferredGoalHeight,
                Nodes = path,
                Cursor = 0
            };
            _routes[unitId.Index] = route;
        }

        while (route.Cursor < route.Nodes.Length - 1)
        {
            Node current = graph.Nodes[route.Nodes[route.Cursor]];
            float dx = current.X - position.X;
            float dy = current.Y - position.Y;
            if (dx * dx + dy * dy > NodeArrivalRadius * NodeArrivalRadius ||
                MathF.Abs(current.Height - position.Z) > 0.30f)
                break;
            route.Cursor++;
        }

        Node next = graph.Nodes[route.Nodes[route.Cursor]];
        if (route.Cursor == route.Nodes.Length - 1)
            waypoint = new Vector3(target.X, target.Y, next.Height);
        else
            waypoint = new Vector3(next.X, next.Y, next.Height);

        return true;
    }

    private void SynchronizeTerrainVersion(WorldMap worldMap)
    {
        if (!ReferenceEquals(_observedMap, worldMap))
        {
            if (_observedMap != null)
                _observedMap.Voxels.Changed -= OnVoxelFieldChanged;

            _observedMap = worldMap;
            _observedMap.Voxels.Changed += OnVoxelFieldChanged;
            _observedNavigationVersion = worldMap.NavigationVersion;
            _graphs.Clear();
            _routes.Clear();
            _multiRegionRoutes.Clear();
            return;
        }

        // Non-voxel world changes (navigation blockers, legacy height edits)
        // have no voxel bounds, so they still invalidate the whole graph cache.
        if (_observedNavigationVersion != worldMap.NavigationVersion)
        {
            _observedNavigationVersion = worldMap.NavigationVersion;
            _graphs.Clear();
            _routes.Clear();
            _multiRegionRoutes.Clear();
        }
    }

    private void OnVoxelFieldChanged(SparseVoxelField.VoxelBounds bounds)
    {
        WorldMap? map = _observedMap;
        if (map == null)
            return;

        // WorldMap subscribed before this navigation system; its callback has
        // already incremented NavigationVersion for this bounded edit.
        _observedNavigationVersion = map.NavigationVersion;

        float minimumX = Math.Max(0, bounds.MinX - 5) * SparseVoxelField.VoxelSizeMeters;
        float maximumX = Math.Min(map.Voxels.Width, bounds.MaxX + 5) *
            SparseVoxelField.VoxelSizeMeters;
        float minimumY = Math.Max(0, bounds.MinZ - 5) * SparseVoxelField.VoxelSizeMeters;
        float maximumY = Math.Min(map.Voxels.Depth, bounds.MaxZ + 5) *
            SparseVoxelField.VoxelSizeMeters;

        bool IntersectsEditedArea(RegionKey key)
        {
            float regionMinX = key.X * RegionSizeMeters;
            float regionMaxX = regionMinX + RegionSizeMeters;
            float regionMinY = key.Y * RegionSizeMeters;
            float regionMaxY = regionMinY + RegionSizeMeters;
            return minimumX < regionMaxX && maximumX > regionMinX &&
                   minimumY < regionMaxY && maximumY > regionMinY;
        }

        HashSet<RegionKey> invalidated = new();
        foreach (RegionKey key in _graphs.Keys)
        {
            if (IntersectsEditedArea(key))
                invalidated.Add(key);
        }

        // A route may outlive its graph in the bounded LRU cache. Keep its
        // region dependency independently so edits cannot leave a stale path.
        foreach (Route route in _routes.Values)
        {
            if (IntersectsEditedArea(route.Region))
                invalidated.Add(route.Region);
        }

        foreach (MultiRegionRoute route in _multiRegionRoutes.Values)
        {
            foreach (RegionKey key in route.Regions)
            {
                if (IntersectsEditedArea(key))
                    invalidated.Add(key);
            }
        }

        if (invalidated.Count == 0)
            return;

        foreach (RegionKey key in invalidated)
            _graphs.Remove(key);

        List<int>? removeRoutes = null;
        foreach ((int unitIndex, Route route) in _routes)
        {
            if (invalidated.Contains(route.Region))
                (removeRoutes ??= new List<int>()).Add(unitIndex);
        }

        if (removeRoutes != null)
        {
            foreach (int unitIndex in removeRoutes)
                _routes.Remove(unitIndex);
        }

        removeRoutes = null;
        foreach ((int unitIndex, MultiRegionRoute route) in _multiRegionRoutes)
        {
            bool intersects = false;
            foreach (RegionKey key in route.Regions)
            {
                if (invalidated.Contains(key))
                {
                    intersects = true;
                    break;
                }
            }

            if (intersects)
                (removeRoutes ??= new List<int>()).Add(unitIndex);
        }

        if (removeRoutes != null)
        {
            foreach (int unitIndex in removeRoutes)
                _multiRegionRoutes.Remove(unitIndex);
        }
    }


    private bool TryGetHierarchicalWaypoint(
        WorldMap map,
        UnitId unitId,
        Vector3 position,
        Vector3 target,
        float bodyHeight,
        RegionKey startRegion,
        RegionKey goalRegion,
        out bool ownsVoxelRegion,
        out Vector3 waypoint)
    {
        ownsVoxelRegion = false;
        waypoint = default;

        List<RegionKey> xFirst = BuildRegionPath(startRegion, goalRegion, xFirst: true);
        List<RegionKey> yFirst = BuildRegionPath(startRegion, goalRegion, xFirst: false);
        if (xFirst.Count < 2 || xFirst.Count > 40 ||
            yFirst.Count < 2 || yFirst.Count > 40)
            return false;

        bool xFirstHasEdits = CorridorHasVoxelEdits(map, xFirst);
        bool yFirstHasEdits = CorridorHasVoxelEdits(map, yFirst);
        if (!xFirstHasEdits && !yFirstHasEdits)
            return false;

        ownsVoxelRegion = true;
        float preferredGoalHeight = target.Z > 0.01f ? target.Z : position.Z;
        bool routeMatches =
            _multiRegionRoutes.TryGetValue(unitId.Index, out MultiRegionRoute? route) &&
            route.Owner == unitId &&
            (RegionsEqual(route.Regions, xFirst) || RegionsEqual(route.Regions, yFirst)) &&
            MathF.Abs(route.TargetX - target.X) <= 0.35f &&
            MathF.Abs(route.TargetY - target.Y) <= 0.35f &&
            MathF.Abs(route.TargetHeight - preferredGoalHeight) <= 0.25f;
        if (!routeMatches)
        {
            List<RegionKey>? selectedPath = null;
            Vector3[] waypoints = Array.Empty<Vector3>();
            if (xFirstHasEdits && TryBuildHierarchicalPath(
                    map, xFirst, position, target, bodyHeight, preferredGoalHeight,
                    out waypoints))
            {
                selectedPath = xFirst;
            }
            else if (yFirstHasEdits && TryBuildHierarchicalPath(
                         map, yFirst, position, target, bodyHeight, preferredGoalHeight,
                         out waypoints))
            {
                selectedPath = yFirst;
            }

            if (selectedPath == null)
            {
                _multiRegionRoutes.Remove(unitId.Index);
                return false;
            }

            route = new MultiRegionRoute
            {
                Owner = unitId,
                Regions = selectedPath.ToArray(),
                Waypoints = waypoints,
                TargetX = target.X,
                TargetY = target.Y,
                TargetHeight = preferredGoalHeight,
                Cursor = 0
            };
            _multiRegionRoutes[unitId.Index] = route;
        }

        AdvanceRoute(route.Waypoints, ref route.Cursor, position);
        Vector3 next = route.Waypoints[route.Cursor];
        waypoint = route.Cursor == route.Waypoints.Length - 1
            ? new Vector3(target.X, target.Y, next.Z)
            : next;
        return true;
    }

    private static bool RegionsEqual(
        RegionKey[] first,
        List<RegionKey> second)
    {
        if (first.Length != second.Count)
            return false;
        for (int i = 0; i < first.Length; i++)
        {
            if (first[i] != second[i])
                return false;
        }
        return true;
    }

    private static bool RegionsEqual(List<RegionKey> first, List<RegionKey> second)
    {
        if (first.Count != second.Count)
            return false;
        for (int i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i])
                return false;
        }
        return true;
    }

    private static void AdvanceRoute(
        Vector3[] waypoints,
        ref int cursor,
        Vector3 position)
    {
        while (cursor < waypoints.Length - 1)
        {
            Vector3 current = waypoints[cursor];
            float dx = current.X - position.X;
            float dy = current.Y - position.Y;
            if (dx * dx + dy * dy > NodeArrivalRadius * NodeArrivalRadius ||
                MathF.Abs(current.Z - position.Z) > 0.30f)
                break;
            cursor++;
        }
    }

    private static List<RegionKey> BuildRegionPath(
        RegionKey start,
        RegionKey goal,
        bool xFirst)
    {
        List<RegionKey> result = new();
        result.Add(start);
        int x = start.X;
        int y = start.Y;

        if (xFirst)
        {
            int stepX = Math.Sign(goal.X - x);
            while (x != goal.X)
            {
                x += stepX;
                result.Add(new RegionKey(x, y));
            }

            int stepY = Math.Sign(goal.Y - y);
            while (y != goal.Y)
            {
                y += stepY;
                result.Add(new RegionKey(x, y));
            }
        }
        else
        {
            int stepY = Math.Sign(goal.Y - y);
            while (y != goal.Y)
            {
                y += stepY;
                result.Add(new RegionKey(x, y));
            }

            int stepX = Math.Sign(goal.X - x);
            while (x != goal.X)
            {
                x += stepX;
                result.Add(new RegionKey(x, y));
            }
        }

        return result;
    }

    private static bool CorridorHasVoxelEdits(
        WorldMap map,
        List<RegionKey> regions)
    {
        foreach (RegionKey key in regions)
        {
            if (RegionHasVoxelEdits(map, key))
                return true;
        }
        return false;
    }

    private static bool RegionHasVoxelEdits(WorldMap map, RegionKey key)
    {
        int minX = key.X * RegionSizeMeters * 10;
        int minY = key.Y * RegionSizeMeters * 10;
        int maxX = Math.Min(map.Voxels.Width, (key.X + 1) * RegionSizeMeters * 10);
        int maxY = Math.Min(map.Voxels.Depth, (key.Y + 1) * RegionSizeMeters * 10);
        return minX < maxX && minY < maxY &&
            map.Voxels.HasEditsInArea(minX, maxX - 1, minY, maxY - 1);
    }

    private bool TryBuildHierarchicalPath(
        WorldMap map,
        List<RegionKey> regions,
        Vector3 position,
        Vector3 target,
        float bodyHeight,
        float preferredGoalHeight,
        out Vector3[] waypoints)
    {
        waypoints = Array.Empty<Vector3>();
        if (regions.Count < 2 || regions.Count > 40)
            return false;

        float requiredClearance = MathF.Max(0.1f, bodyHeight);
        Graph? currentGraph = GetOrBuildGraph(map, regions[0]);
        if (currentGraph == null)
            return false;

        int currentNode = FindNearestNode(
            currentGraph, position.X, position.Y, position.Z,
            requiredClearance, maximumCellRadius: 3);
        if (currentNode < 0)
            return false;

        List<Vector3> routePoints = new();
        for (int transition = 0; transition < regions.Count - 1; transition++)
        {
            RegionKey fromRegion = regions[transition];
            RegionKey toRegion = regions[transition + 1];
            Graph? nextGraph = GetOrBuildGraph(map, toRegion);
            if (nextGraph == null ||
                !TryFindRegionPortal(
                    currentGraph, nextGraph, fromRegion, toRegion,
                    position, target, requiredClearance,
                    out int exitNode, out int entryNode))
                return false;

            int[] localPath = FindPath(
                currentGraph, currentNode, exitNode, requiredClearance);
            if (localPath.Length == 0)
                return false;

            AppendWaypoints(currentGraph, localPath, routePoints);
            currentGraph = nextGraph;
            currentNode = entryNode;
        }

        int goalNode = FindNearestNode(
            currentGraph, target.X, target.Y, preferredGoalHeight,
            requiredClearance, maximumCellRadius: 5);
        if (goalNode < 0)
            return false;

        int[] finalPath = FindPath(
            currentGraph, currentNode, goalNode, requiredClearance);
        if (finalPath.Length == 0)
            return false;

        AppendWaypoints(currentGraph, finalPath, routePoints);
        if (routePoints.Count == 0)
            return false;

        waypoints = routePoints.ToArray();
        return true;
    }

    private static void AppendWaypoints(
        Graph graph,
        int[] path,
        List<Vector3> output)
    {
        foreach (int nodeIndex in path)
        {
            Node node = graph.Nodes[nodeIndex];
            Vector3 point = new(node.X, node.Y, node.Height);
            if (output.Count > 0 &&
                Vector3.DistanceSquared(output[^1], point) < 0.0001f)
                continue;
            output.Add(point);
        }
    }

    private static bool TryFindRegionPortal(
        Graph from,
        Graph to,
        RegionKey fromRegion,
        RegionKey toRegion,
        Vector3 start,
        Vector3 target,
        float bodyHeight,
        out int fromNode,
        out int toNode)
    {
        fromNode = -1;
        toNode = -1;
        bool crossesX = fromRegion.X != toRegion.X;
        int alongCount;
        int fromFixed;
        int toFixed;
        float expectedAlong;
        if (crossesX)
        {
            bool positive = toRegion.X > fromRegion.X;
            fromFixed = positive ? from.Width - 1 : 0;
            toFixed = positive ? 0 : to.Width - 1;
            alongCount = Math.Min(from.Height, to.Height);
            float boundary = (positive ? fromRegion.X + 1 : fromRegion.X) *
                RegionSizeMeters;
            float denominator = target.X - start.X;
            float blend = MathF.Abs(denominator) > 0.0001f
                ? Math.Clamp((boundary - start.X) / denominator, 0f, 1f)
                : 0.5f;
            expectedAlong = start.Y + (target.Y - start.Y) * blend;
        }
        else
        {
            bool positive = toRegion.Y > fromRegion.Y;
            fromFixed = positive ? from.Height - 1 : 0;
            toFixed = positive ? 0 : to.Height - 1;
            alongCount = Math.Min(from.Width, to.Width);
            float boundary = (positive ? fromRegion.Y + 1 : fromRegion.Y) *
                RegionSizeMeters;
            float denominator = target.Y - start.Y;
            float blend = MathF.Abs(denominator) > 0.0001f
                ? Math.Clamp((boundary - start.Y) / denominator, 0f, 1f)
                : 0.5f;
            expectedAlong = start.X + (target.X - start.X) * blend;
        }

        float bestCost = float.PositiveInfinity;
        float maximumPortalStep = Math.Min(
            MaximumStepHeight,
            CellSizeMeters * MaximumGrade + 0.08f);
        for (int along = 0; along < alongCount; along++)
        {
            int fromCell = crossesX
                ? fromFixed + along * from.Width
                : along + fromFixed * from.Width;
            int toCell = crossesX
                ? toFixed + along * to.Width
                : along + toFixed * to.Width;

            foreach (int firstNode in from.CellNodes[fromCell])
            {
                Node first = from.Nodes[firstNode];
                if (first.Clearance + 0.0001f < bodyHeight)
                    continue;

                foreach (int secondNode in to.CellNodes[toCell])
                {
                    Node second = to.Nodes[secondNode];
                    if (second.Clearance + 0.0001f < bodyHeight)
                        continue;

                    float heightDifference = MathF.Abs(first.Height - second.Height);
                    if (heightDifference > maximumPortalStep)
                        continue;

                    float alongPosition = crossesX
                        ? (first.Y + second.Y) * 0.5f
                        : (first.X + second.X) * 0.5f;
                    float deviation = alongPosition - expectedAlong;
                    float cost = deviation * deviation +
                        heightDifference * heightDifference * 4f;
                    if (cost >= bestCost)
                        continue;

                    bestCost = cost;
                    fromNode = firstNode;
                    toNode = secondNode;
                }
            }
        }

        return fromNode >= 0 && toNode >= 0;
    }

    private Graph? GetOrBuildGraph(WorldMap map, RegionKey key)
    {
        if (_graphs.TryGetValue(key, out Graph? cached))
            return cached;

        int originCellX = key.X * CellsPerRegion;
        int originCellY = key.Y * CellsPerRegion;
        int width = Math.Clamp(map.TileWidth * CellsPerMeter - originCellX, 0, CellsPerRegion);
        int height = Math.Clamp(map.TileHeight * CellsPerMeter - originCellY, 0, CellsPerRegion);
        if (width == 0 || height == 0)
            return null;

        List<Node> nodes = new();
        List<int>?[] cellLists = new List<int>?[width * height];
        Span<SparseVoxelField.WalkableSurface> surfaces =
            stackalloc SparseVoxelField.WalkableSurface[MaximumSurfaceNodesPerCell];

        for (int cellY = 0; cellY < height; cellY++)
        {
            for (int cellX = 0; cellX < width; cellX++)
            {
                int globalCellX = originCellX + cellX;
                int globalCellY = originCellY + cellY;
                float worldX = (globalCellX + 0.5f) * CellSizeMeters;
                float worldY = (globalCellY + 0.5f) * CellSizeMeters;
                int tileX = Math.Clamp((int)MathF.Floor(worldX), 0, map.MaxTileX);
                int tileY = Math.Clamp((int)MathF.Floor(worldY), 0, map.MaxTileY);
                if (map.IsNavigationBlocked(tileX, tileY))
                    continue;

                int cellIndex = cellX + cellY * width;
                List<int>? cellNodes = null;
                if (map.HasVoxelEditsAt(worldX, worldY))
                {
                    int surfaceCount = map.FindVoxelWalkableSurfacesAt(
                        worldX, worldY, minimumUpNormal: 0.72f, surfaces);
                    for (int surfaceIndex = 0; surfaceIndex < surfaceCount; surfaceIndex++)
                    {
                        SparseVoxelField.WalkableSurface surface = surfaces[surfaceIndex];
                        AddNode(
                            nodes, ref cellNodes,
                            cellX, cellY, worldX, worldY,
                            surface.HeightMeters,
                            surface.ClearanceMeters,
                            surface.MaterialId);
                    }
                }
                else
                {
                    float floor = map.GetSurfaceHeightAt(worldX, worldY);
                    if (floor > 0f)
                    {
                        AddNode(
                            nodes, ref cellNodes,
                            cellX, cellY, worldX, worldY,
                            floor, float.MaxValue, materialId: 1);
                    }
                }

                cellLists[cellIndex] = cellNodes;
            }
        }

        int[][] frozenCells = new int[cellLists.Length][];
        for (int i = 0; i < cellLists.Length; i++)
            frozenCells[i] = cellLists[i]?.ToArray() ?? Array.Empty<int>();

        Graph graph = new()
        {
            Key = key,
            TerrainVersion = map.TerrainVersion,
            OriginCellX = originCellX,
            OriginCellY = originCellY,
            Width = width,
            Height = height,
            CellNodes = frozenCells,
            Nodes = nodes.ToArray()
        };

        if (_graphs.Count >= MaximumCachedRegions)
            _graphs.Clear();
        _graphs.Add(key, graph);
        return graph;
    }

    private static void AddNode(
        List<Node> nodes,
        ref List<int>? cellNodes,
        int cellX,
        int cellY,
        float worldX,
        float worldY,
        float floorHeight,
        float clearance,
        ushort materialId)
    {
        int nodeIndex = nodes.Count;
        nodes.Add(new Node(
            cellX, cellY, worldX, worldY,
            floorHeight, clearance, materialId));
        (cellNodes ??= new List<int>(2)).Add(nodeIndex);
    }

    private static int FindNearestNode(
        Graph graph,
        float worldX,
        float worldY,
        float preferredHeight,
        float bodyHeight,
        int maximumCellRadius)
    {
        int globalCellX = (int)MathF.Floor(worldX * CellsPerMeter);
        int globalCellY = (int)MathF.Floor(worldY * CellsPerMeter);
        int localCellX = globalCellX - graph.OriginCellX;
        int localCellY = globalCellY - graph.OriginCellY;
        int bestNode = -1;
        float bestCost = float.PositiveInfinity;

        for (int radius = 0; radius <= maximumCellRadius; radius++)
        {
            int minX = Math.Max(0, localCellX - radius);
            int maxX = Math.Min(graph.Width - 1, localCellX + radius);
            int minY = Math.Max(0, localCellY - radius);
            int maxY = Math.Min(graph.Height - 1, localCellY + radius);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (radius > 0 &&
                        x > minX && x < maxX &&
                        y > minY && y < maxY)
                        continue;

                    int cell = x + y * graph.Width;
                    foreach (int nodeIndex in graph.CellNodes[cell])
                    {
                        Node node = graph.Nodes[nodeIndex];
                        if (node.Clearance + 0.0001f < bodyHeight)
                            continue;

                        float dx = node.X - worldX;
                        float dy = node.Y - worldY;
                        float horizontal = MathF.Sqrt(dx * dx + dy * dy);
                        float vertical = MathF.Abs(node.Height - preferredHeight);
                        float cost = horizontal + vertical * 2.0f;
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestNode = nodeIndex;
                        }
                    }
                }
            }

            if (bestNode >= 0)
                return bestNode;
        }

        return bestNode;
    }

    private static int[] FindPath(
        Graph graph,
        int startNode,
        int goalNode,
        float bodyHeight)
    {
        if (startNode == goalNode)
            return new[] { startNode };

        int count = graph.Nodes.Length;
        float[] scores = new float[count];
        Array.Fill(scores, float.PositiveInfinity);
        int[] parents = new int[count];
        Array.Fill(parents, -1);
        bool[] closed = new bool[count];

        PriorityQueue<int, float> open = new();
        scores[startNode] = 0f;
        open.Enqueue(startNode, Heuristic(graph.Nodes[startNode], graph.Nodes[goalNode]));
        int expanded = 0;
        int maximumExpanded = Math.Min(count, 65_536);

        while (open.TryDequeue(out int current, out _) && expanded < maximumExpanded)
        {
            if (closed[current])
                continue;
            if (current == goalNode)
                return ReconstructPath(parents, current, startNode);

            closed[current] = true;
            expanded++;

            Node from = graph.Nodes[current];
            for (int direction = 0; direction < DirectionX.Length; direction++)
            {
                int nextCellX = from.CellX + DirectionX[direction];
                int nextCellY = from.CellY + DirectionY[direction];
                if ((uint)nextCellX >= (uint)graph.Width ||
                    (uint)nextCellY >= (uint)graph.Height)
                    continue;

                int nextCell = nextCellX + nextCellY * graph.Width;
                foreach (int next in graph.CellNodes[nextCell])
                {
                    if (closed[next])
                        continue;

                    Node to = graph.Nodes[next];
                    if (to.Clearance + 0.0001f < bodyHeight)
                        continue;

                    float horizontalDistance = CellSizeMeters;
                    float heightDifference = MathF.Abs(to.Height - from.Height);
                    float maximumSlopeStep = horizontalDistance * MaximumGrade + 0.08f;
                    if (heightDifference > MaximumStepHeight ||
                        heightDifference > maximumSlopeStep)
                        continue;

                    float movementCost = horizontalDistance +
                        heightDifference * 0.42f +
                        GetMaterialTraversalPenalty(to.MaterialId);
                    float tentative = scores[current] + movementCost;
                    if (tentative >= scores[next])
                        continue;

                    scores[next] = tentative;
                    parents[next] = current;
                    float priority = tentative + Heuristic(to, graph.Nodes[goalNode]);
                    open.Enqueue(next, priority);
                }
            }
        }

        return Array.Empty<int>();
    }

    private static float GetMaterialTraversalPenalty(ushort materialId)
    {
        VoxelMaterialDefinition material = VoxelMaterialCatalog.Get(materialId);
        return Math.Clamp(material.Porosity * 0.04f, 0f, 0.08f);
    }

    private static float Heuristic(Node from, Node to)
    {
        float dx = from.X - to.X;
        float dy = from.Y - to.Y;
        return MathF.Sqrt(dx * dx + dy * dy) +
            MathF.Abs(from.Height - to.Height) * 0.5f;
    }

    private static int[] ReconstructPath(int[] parents, int current, int start)
    {
        List<int> path = new();
        path.Add(current);
        while (current != start)
        {
            current = parents[current];
            if (current < 0)
                return Array.Empty<int>();
            path.Add(current);
        }
        path.Reverse();
        return path.ToArray();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
