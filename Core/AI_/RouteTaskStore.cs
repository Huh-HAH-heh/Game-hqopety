using Core.Map;
using Core.Unit.Components;

namespace Core.AI;

public sealed class RouteTask
{
    public readonly int RouteId;
    public readonly MapLayer Layer;

    public SpatialCoord Start;
    public SpatialCoord Target;

    public PathPriority Priority;
    public PathRequestState State;

    public int QueuedFrame;
    public bool IsOld;

    public SpatialCoord[]? Path;
    public int PathLength;

    public RouteTask(
        int routeId,
        MapLayer layer,
        SpatialCoord start,
        SpatialCoord target,
        PathPriority priority,
        int queuedFrame)
    {
        RouteId = routeId;
        Layer = layer;

        Start = start;
        Target = target;

        Priority = priority;
        State = PathRequestState.Pending;

        QueuedFrame = queuedFrame;
        IsOld = false;
    }
}

internal sealed class RouteTaskStore
{
    private readonly Dictionary<int, RouteTask> _tasks = new();

    public int Count =>
        _tasks.Count;

    public void Add(RouteTask task)
    {
        _tasks.Add(
            task.RouteId,
            task);
    }

    public bool TryGet(
        int routeId,
        out RouteTask? task)
    {
        return _tasks.TryGetValue(
            routeId,
            out task);
    }

    public bool Remove(
        int routeId)
    {
        return _tasks.Remove(
            routeId);
    }

    public void Clear()
    {
        _tasks.Clear();
    }
}
