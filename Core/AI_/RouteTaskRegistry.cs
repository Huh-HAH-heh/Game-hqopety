using System;
using System.Collections.Generic;
using System.Text;

namespace Core.AI;

internal sealed class RouteTaskRegistry
{
    private RouteTask?[] _tasks;

    public RouteTaskRegistry(int capacity)
    {
        _tasks = new RouteTask[Math.Max(1, capacity)];
    }

    public int Add(RouteTask task)
    {
        for (int i = 0; i < _tasks.Length; i++)
        {
            if (_tasks[i] == null)
            {
                _tasks[i] = task;
                return i;
            }
        }

        int oldSize = _tasks.Length;
        int newSize = oldSize * 2;

        Array.Resize(ref _tasks, newSize);

        _tasks[oldSize] = task;

        return oldSize;
    }

    public RouteTask? Get(int index)
    {
        if ((uint)index >= (uint)_tasks.Length)
            return null;

        return _tasks[index];
    }

    public void Remove(int index)
    {
        if ((uint)index >= (uint)_tasks.Length)
            return;

        _tasks[index] = null;
    }

    public int FindByRouteId(int routeId)
    {
        for (int i = 0; i < _tasks.Length; i++)
        {
            RouteTask? task = _tasks[i];

            if (task != null && task.RouteId == routeId)
                return i;
        }

        return -1;
    }
}