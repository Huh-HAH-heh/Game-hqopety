using Core.Map;
using Core.Structs;
using Core.Unit.Components;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Core.AI;

public enum PathRequestState : byte
{
    Pending,
    Processing,
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

    /*
     * <= 5 logical CPU
     *      -> 1 worker
     *
     * >= 6 logical CPU
     *      -> 2 обычных
     *      -> 1 old-biased
     */
    private const int MultiWorkerCpuThreshold = 6;

    /*
     * Если worker один:
     *
     * 2 обычные задачи
     * 1 попытка старой
     */
    private const int SingleWorkerNormalQuota = 2;

    private readonly RouteTaskStore _tasks;

    private readonly int _capacity;

    /*
     * Critical = 0
     * High     = 1
     * Normal   = 2
     * Low      = 3
     */
    private readonly Queue<RouteTask>[] _queues;

    /*
     * Общая память недавно использованных путей.
     *
     * A* её только читает.
     * Готовые маршруты добавляются сюда
     * после завершения поиска.
     */
    private readonly PathTrafficMemory _trafficMemory;

    private readonly object _sync = new();

    private readonly Thread[] _workers;

    private readonly int _oldAfterFrames;

    /*
     * Игровой поток пишет сюда.
     * Worker'ы только читают.
     */
    private int _currentFrame;

    private bool _stopRequested;

    private int _nextRouteId;

    public PathfindingSystem(
        int capacity = 5000,
        int oldAfterFrames = 5)
    {
        _capacity =
            Math.Max(
                1,
                capacity);

        _oldAfterFrames =
            Math.Max(
                1,
                oldAfterFrames);

        _tasks =
            new RouteTaskStore();

        _trafficMemory =
            new PathTrafficMemory();

        _queues =
            new Queue<RouteTask>[
                Enum.GetValues<PathPriority>().Length];

        for (int i = 0;
             i < _queues.Length;
             i++)
        {
            _queues[i] =
                new Queue<RouteTask>();
        }

        int logicalProcessors =
            Environment.ProcessorCount;

        if (logicalProcessors <
            MultiWorkerCpuThreshold)
        {
            _workers =
                new Thread[1];

            _workers[0] =
                CreateWorker(
                    "PathfindingWorker",
                    false);
        }
        else
        {
            _workers =
                new Thread[3];

            _workers[0] =
                CreateWorker(
                    "PathfindingWorker-1",
                    false);

            _workers[1] =
                CreateWorker(
                    "PathfindingWorker-2",
                    false);

            _workers[2] =
                CreateWorker(
                    "PathfindingOldWorker",
                    true);
        }
    }

    /*
     * Вызывается один раз за игровой кадр.
     */
    public void UpdateFrame(int frame)
    {
        Volatile.Write(
            ref _currentFrame,
            frame);
    }
    public bool RequeuePath(
    int routeId,
    SpatialCoord newStart,
    SpatialCoord newTarget,
    PathPriority priority = PathPriority.Normal)
    {
        lock (_sync)
        {
            if (!_tasks.TryGet(
                    routeId,
                    out RouteTask? task))
            {
                return false;
            }

            if (task.State !=
                    PathRequestState.Completed &&
                task.State !=
                    PathRequestState.Failed)
            {
                return false;
            }

            task.Start = newStart;
            task.Target = newTarget;

            task.Priority =
                priority;

            task.Path = null;
            task.PathLength = 0;

            task.IsOld = false;

            task.QueuedFrame =
                Volatile.Read(
                    ref _currentFrame);

            task.State =
                PathRequestState.Pending;

            _queues[(int)priority]
                .Enqueue(task);

            Monitor.PulseAll(_sync);

            return true;
        }
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
            if (_stopRequested)
                return -1;

            if (_tasks.Count >= _capacity)
                return -1;

            int routeId =
                ++_nextRouteId;

            int queuedFrame =
                Volatile.Read(
                    ref _currentFrame);

            RouteTask task =
                new RouteTask(
                    routeId,
                    layer,
                    start,
                    target,
                    priority,
                    queuedFrame);

            _tasks.Add(task);

            _queues[(int)priority]
                .Enqueue(task);

            /*
             * Будим worker'ов.
             *
             * Конкретную задачу заберёт
             * только один worker, потому что
             * очередь и State меняются
             * внутри одного lock.
             */
            Monitor.PulseAll(_sync);

            return routeId;
        }
    }

    /*
     * Повторный расчёт того же RouteId.
     *
     * Используется после того, как готовый
     * маршрут оказался непригоден.
     */
    public bool RequeuePath(
        int routeId,
        PathPriority priority = PathPriority.Normal)
    {
        lock (_sync)
        {
            if (!_tasks.TryGet(
                    routeId,
                    out RouteTask? task))
            {
                return false;
            }

            if (task.State !=
                    PathRequestState.Completed &&
                task.State !=
                    PathRequestState.Failed)
            {
                return false;
            }

            task.Priority =
                priority;

            task.Path = null;
            task.PathLength = 0;

            task.IsOld = false;

            task.QueuedFrame =
                Volatile.Read(
                    ref _currentFrame);

            task.State =
                PathRequestState.Pending;

            _queues[(int)priority]
                .Enqueue(task);

            Monitor.PulseAll(_sync);

            return true;
        }
    }

    public PathRequestState GetState(
        int routeId)
    {
        lock (_sync)
        {
            if (!_tasks.TryGet(
                    routeId,
                    out RouteTask? task))
            {
                return PathRequestState.Failed;
            }

            return task.State;
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

            if (!_tasks.TryGet(
                    routeId,
                    out RouteTask? task))
            {
                return false;
            }

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
            if (!_tasks.TryGet(
                    routeId,
                    out RouteTask? task))
            {
                return;
            }

            if (task.State !=
                    PathRequestState.Pending &&
                task.State !=
                    PathRequestState.Processing)
            {
                return;
            }

            task.State =
                PathRequestState.Cancelled;

            Monitor.PulseAll(_sync);
        }
    }

    public void Remove(
        int routeId)
    {
        lock (_sync)
        {
            if (!_tasks.TryGet(
                    routeId,
                    out RouteTask? task))
            {
                return;
            }

            task.State =
                PathRequestState.Cancelled;

            task.Path = null;
            task.PathLength = 0;

            _tasks.Remove(routeId);

            Monitor.PulseAll(_sync);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_stopRequested)
                return;

            _stopRequested = true;

            Monitor.PulseAll(_sync);
        }

        foreach (Thread worker in _workers)
        {
            worker.Join();
        }
    }

    private Thread CreateWorker(
        string name,
        bool oldBiased)
    {
        Thread worker =
            new Thread(
                () => WorkerLoop(oldBiased))
            {
                IsBackground = true,
                Name = name
            };

        worker.Start();

        return worker;
    }

    private void WorkerLoop(
        bool oldBiased)
    {
        /*
         * ВАЖНО:
         *
         * Каждый worker получает
         * собственный A* context.
         *
         * Поэтому здесь больше нет
         * общего SearchLock.
         */
        PureAStarContext context =
            PureAStarPathfinder.CreateContext();

        /*
         * Для однопоточного режима:
         *
         * Normal
         * Normal
         * Old
         *
         * Normal
         * Normal
         * Old
         */
        int normalTaskCount = 0;

        while (true)
        {
            RouteTask? task = null;

            lock (_sync)
            {
                while (!_stopRequested)
                {
                    task =
                        DequeueNextTask(
                            oldBiased,
                            ref normalTaskCount);

                    if (task != null)
                        break;

                    /*
                     * Нет работы.
                     *
                     * Поток спит и не потребляет CPU.
                     */
                    Monitor.Wait(_sync);
                }

                if (_stopRequested)
                    return;
            }

            /*
             * Вне _sync выполняется сам A*.
             *
             * Поэтому Worker 1, 2 и 3
             * действительно могут считать
             * одновременно.
             */
            ProcessTask(
                task!,
                context);
        }
    }

    private RouteTask? DequeueNextTask(
        bool oldBiased,
        ref int normalTaskCount)
    {
        /*
         * Старый worker:
         *
         * сначала ищет старые задачи.
         */
        if (oldBiased)
        {
            RouteTask? oldTask =
                DequeueOldTask();

            if (oldTask != null)
                return oldTask;

            /*
             * Старых нет.
             *
             * Помогает обычным.
             */
            return DequeueNormalPriorityTask();
        }

        /*
         * Обычный worker периодически
         * помогает старым задачам.
         */
        if (normalTaskCount >=
            SingleWorkerNormalQuota)
        {
            RouteTask? oldTask =
                DequeueOldTask();

            if (oldTask != null)
            {
                normalTaskCount = 0;
                return oldTask;
            }
        }

        RouteTask? normalTask =
            DequeueNormalPriorityTask();

        if (normalTask != null)
        {
            normalTaskCount++;
            return normalTask;
        }

        /*
         * Обычных задач нет.
         *
         * Значит можно полностью
         * переключиться на старые.
         */
        RouteTask? fallbackOld =
            DequeueOldTask();

        if (fallbackOld != null)
        {
            normalTaskCount = 0;
            return fallbackOld;
        }

        return null;
    }

    private RouteTask? DequeueNormalPriorityTask()
    {
        for (int i = 0;
             i < _queues.Length;
             i++)
        {
            Queue<RouteTask> queue =
                _queues[i];

            while (queue.Count > 0)
            {
                RouteTask task =
                    queue.Dequeue();

                if (task.State !=
                    PathRequestState.Pending)
                {
                    continue;
                }

                task.State =
                    PathRequestState.Processing;

                return task;
            }
        }

        return null;
    }

    private RouteTask? DequeueOldTask()
    {
        int currentFrame =
            Volatile.Read(
                ref _currentFrame);

        /*
         * Для старых задач:
         *
         * Low
         * Normal
         * High
         * Critical
         */
        int[] order =
        {
            (int)PathPriority.Low,
            (int)PathPriority.Normal,
            (int)PathPriority.High,
            (int)PathPriority.Critical
        };

        for (int i = 0;
             i < order.Length;
             i++)
        {
            Queue<RouteTask> queue =
                _queues[order[i]];

            while (queue.Count > 0)
            {
                RouteTask task =
                    queue.Peek();

                if (task.State !=
                    PathRequestState.Pending)
                {
                    queue.Dequeue();
                    continue;
                }

                int waitedFrames =
                    currentFrame -
                    task.QueuedFrame;

                /*
                 * Она ещё не состарилась.
                 */
                if (!task.IsOld &&
                    waitedFrames <
                    _oldAfterFrames)
                {
                    break;
                }

                queue.Dequeue();

                task.IsOld = true;

                task.State =
                    PathRequestState.Processing;

                return task;
            }
        }

        return null;
    }

    private void ProcessTask(
        RouteTask task,
        PureAStarContext context)
    {
        /*
         * Проверяем отмену перед A*.
         */
        lock (_sync)
        {
            if (task.State ==
                PathRequestState.Cancelled)
            {
                return;
            }
        }

        if (task.Start.Z !=
            task.Target.Z)
        {
            CompleteFailed(task);
            return;
        }

        SpatialCoord[] path =
            new SpatialCoord[
                DefaultPathCapacity];

        int currentFrame =
            Volatile.Read(
                ref _currentFrame);

        /*
         * Каждый worker использует
         * свой context.
         *
         * Общий traffic memory только
         * читается A* во время поиска.
         */
        bool success =
            PureAStarPathfinder.FindRoute(
                context,
                _trafficMemory,
                currentFrame,
                task.Layer,
                task.Start,
                task.Target,
                path,
                path.Length,
                out int length);

        lock (_sync)
        {
            /*
             * За время A* задача могла быть
             * отменена.
             */
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

            /*
             * Маршрут добавляется в память
             * только после полного построения.
             *
             * Здесь мы не держим lock во время A*,
             * а значит сами вычисления
             * остаются параллельными.
             */
            _trafficMemory.AddRoute(
                path,
                length,
                currentFrame);
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
}