# PathfindingSystem — модель работы

`PathfindingSystem` отвечает за управление запросами на построение маршрутов, а `PureAStarPathfinder` непосредственно выполняет алгоритм A*.

## Архитектура

Каждый экземпляр `PathfindingSystem` работает независимо и имеет:

- собственный набор `RouteTask`;
- четыре очереди запросов по приоритетам;
- один фоновый worker-поток;
- один `PureAStarPathfinder`.

Например, можно создать два независимых экземпляра:

```csharp
PathfindingSystem playerPathfinding = new();
PathfindingSystem aiPathfinding = new();
```
Работа очереди

Для каждого приоритета существует отдельная FIFO-очередь:

Critical → [A][B]
High     → [C][D]
Normal   → [E][F]
Low      → [G][H]

## одновременно выполняется только один поиск.

Это сделано намеренно: главная задача — не блокировать игровой поток, а не обязательно считать несколько A* параллельно.

# Отмена

Cancel(routeId) не обязан мгновенно останавливать уже выполняющийся A*.

## Смысл отмены:

результат маршрута больше не нужен.

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;

namespace Core.AI;

public enum PathRequestState : byte
{
    Pending,
    Completed,
    Failed,
    Cancelled
}

public enum PathPriority : byte
{
    Critical,
    High,
    Normal,
    Low
}

public sealed class PathfindingSystem : IDisposable
{
    private const int DefaultPathCapacity = 1536;

    private readonly RouteTask[] _tasks;

    // Отдельная очередь для каждого уровня приоритета.
    // Critical имеет индекс 0, Low — индекс 3.
    private readonly Queue<RouteTask>[] _queues;

    private readonly object _sync = new();
    private readonly AutoResetEvent _queueSignal = new(false);

    private readonly Thread _workerThread;

    private bool _stopRequested;
    private int _nextRouteId;

    public PathfindingSystem(
        int capacity = 256)
    {
        if (capacity < 1)
            capacity = 1;

        _tasks =
            new RouteTask[capacity];

        _queues =
            new Queue<RouteTask>[
                Enum.GetValues<PathPriority>().Length];

        for (int i = 0; i < _queues.Length; i++)
        {
            _queues[i] =
                new Queue<RouteTask>();
        }

        _workerThread =
            new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "PathfindingWorker"
            };

        _workerThread.Start();
    }

    public int RequestPath(
        MapLayer layer,
        SpatialCoord start,
        SpatialCoord target,
        PathPriority priority = PathPriority.Normal)
    {
        if (layer == null)
            return -1;

        lock (_sync)
        {
            int index =
                FindFreeTask();

            if (index < 0)
                return -1;

            RouteTask task =
                new RouteTask(
                    ++_nextRouteId,
                    layer,
                    start,
                    target,
                    priority);

            _tasks[index] =
                task;

            _queues[(int)priority].Enqueue(task);

            _queueSignal.Set();

            return task.RouteId;
        }
    }

    public PathRequestState GetState(
        int routeId)
    {
        lock (_sync)
        {
            int index =
                FindTask(routeId);

            if (index < 0)
                return PathRequestState.Failed;

            return _tasks[index].State;
        }
    }

    public bool TryGetRoute(
        int routeId,
        out SpatialCoord[]? path,
        out int length)
    {
        lock (_sync)
        {
            path = null;
            length = 0;

            int index =
                FindTask(routeId);

            if (index < 0)
                return false;

            RouteTask task =
                _tasks[index];

            if (task.State !=
                PathRequestState.Completed)
            {
                return false;
            }

            if (task.Path == null ||
                task.PathLength <= 0)
            {
                return false;
            }

            path =
                task.Path;

            length =
                task.PathLength;

            return true;
        }
    }

    public void Cancel(
        int routeId)
    {
        lock (_sync)
        {
            int index =
                FindTask(routeId);

            if (index < 0)
                return;

            RouteTask task =
                _tasks[index];

            if (task.State !=
                PathRequestState.Pending)
            {
                return;
            }

            task.State =
                PathRequestState.Cancelled;
        }
    }

    public void Remove(
        int routeId)
    {
        lock (_sync)
        {
            int index =
                FindTask(routeId);

            if (index < 0)
                return;

            RouteTask task =
                _tasks[index];

            task.State =
                PathRequestState.Cancelled;

            _tasks[index] =
                null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_stopRequested)
                return;

            _stopRequested = true;
        }

        _queueSignal.Set();

        _workerThread.Join();

        _queueSignal.Dispose();
    }

    private void WorkerLoop()
    {
        while (true)
        {
            RouteTask? task;

            lock (_sync)
            {
                if (_stopRequested)
                    return;

                task =
                    DequeueNextTask();
            }

            if (task == null)
            {
                _queueSignal.WaitOne();
                continue;
            }

            ProcessTask(task);
        }
    }

    private void ProcessTask(
        RouteTask task)
    {
        lock (_sync)
        {
            if (task.State ==
                PathRequestState.Cancelled)
            {
                return;
            }
        }

        if (task.Start.Z != task.Target.Z)
        {
            CompleteFailed(task);
            return;
        }

        SpatialCoord[] path =
            new SpatialCoord[
                DefaultPathCapacity];

        bool success =
            PureAStarPathfinder.FindRoute(
                task.Layer,
                task.Start,
                task.Target,
                path,
                path.Length,
                out int length);

        lock (_sync)
        {
            if (task.State ==
                PathRequestState.Cancelled)
            {
                return;
            }

            if (!success)
            {
                task.State =
                    PathRequestState.Failed;

                return;
            }

            task.Path =
                path;

            task.PathLength =
                length;

            task.State =
                PathRequestState.Completed;
        }
    }

    private void CompleteFailed(
        RouteTask task)
    {
        lock (_sync)
        {
            if (task.State ==
                PathRequestState.Cancelled)
            {
                return;
            }

            task.State =
                PathRequestState.Failed;
        }
    }

    private RouteTask? DequeueNextTask()
    {
        for (int i = 0; i < _queues.Length; i++)
        {
            Queue<RouteTask> queue =
                _queues[i];

            while (queue.Count > 0)
            {
                RouteTask task =
                    queue.Dequeue();

                if (task.State ==
                    PathRequestState.Cancelled)
                {
                    continue;
                }

                return task;
            }
        }

        return null;
    }

    private int FindFreeTask()
    {
        for (int i = 0;
             i < _tasks.Length;
             i++)
        {
            if (_tasks[i] == null)
                return i;
        }

        return -1;
    }

    private int FindTask(
        int routeId)
    {
        for (int i = 0;
             i < _tasks.Length;
             i++)
        {
            if (_tasks[i] != null &&
                _tasks[i].RouteId == routeId)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class RouteTask
    {
        public readonly int RouteId;
        public readonly MapLayer Layer;

        public readonly SpatialCoord Start;
        public readonly SpatialCoord Target;
        public readonly PathPriority Priority;

        public PathRequestState State;

        public SpatialCoord[]? Path;
        public int PathLength;

        public RouteTask(
            int routeId,
            MapLayer layer,
            SpatialCoord start,
            SpatialCoord target,
            PathPriority priority)
        {
            RouteId = routeId;
            Layer = layer;
            Start = start;
            Target = target;
            Priority = priority;

            State =
                PathRequestState.Pending;
        }
    }
}
```