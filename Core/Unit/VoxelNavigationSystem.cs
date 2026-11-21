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

    private readonly Dictionary<RegionKey, Graph> _graphs = new();
    private readonly Dictionary<int, Route> _routes = new();
    private WorldMap? _observedMap;
    private long _observedNavigationVersion = long.MinValue;

    public int CachedRegionCount => _graphs.Count;
    public int CachedRouteCount => _routes.Count;

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

        int startRegionX = (int)MathF.Floor(position.X / RegionSizeMeters);
        int startRegionY = (int)MathF.Floor(position.Y / RegionSizeMeters);
        int goalRegionX = (int)MathF.Floor(target.X / RegionSizeMeters);
        int goalRegionY = (int)MathF.Floor(target.Y / RegionSizeMeters);
        if (startRegionX != goalRegionX || startRegionY != goalRegionY)
            return false;

        SynchronizeTerrainVersion(worldMap);
        RegionKey key = new(startRegionX, startRegionY);
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
            return;
        }

        // Non-voxel world changes (navigation blockers, legacy height edits)
        // have no voxel bounds, so they still invalidate the whole graph cache.
        if (_observedNavigationVersion != worldMap.NavigationVersion)
        {
            _observedNavigationVersion = worldMap.NavigationVersion;
            _graphs.Clear();
            _routes.Clear();
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

        int minSampleX = Math.Max(0, originCellX * 5);
        int minSampleY = Math.Max(0, originCellY * 5);
        int maxSampleX = Math.Min(map.Voxels.Width - 1, (originCellX + width) * 5 - 1);
        int maxSampleY = Math.Min(map.Voxels.Depth - 1, (originCellY + height) * 5 - 1);
        if (!map.Voxels.HasEditsInArea(minSampleX, maxSampleX, minSampleY, maxSampleY))
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
