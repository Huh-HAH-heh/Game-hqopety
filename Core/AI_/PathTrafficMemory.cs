using System;
using System.Threading;
using Core.Unit.Components;

namespace Core.AI;

public sealed class PathTrafficMemory
{
    private const int MaxGridSize = 768;
    private const int MaxGridCells = MaxGridSize * MaxGridSize;

    /*
     * Масштабируем float -> int, чтобы чтение/запись
     * одного элемента можно было делать атомарно.
     */
    private const float HeatScale = 10000f;

    /*
     * Насколько маршрут нагревает клетку.
     */
    private const float HeatAdd = 1.0f;

    /*
     * Скорость забывания.
     *
     * 1.0 = никогда не забывать.
     * 0.0 = забыть мгновенно.
     */
    private const float HeatDecay = 0.85f;

    private const float MaxHeat = 4.0f;

    private readonly int[] _heat;
    private readonly int[] _lastFrame;

    public PathTrafficMemory()
    {
        _heat =
            new int[MaxGridCells];

        _lastFrame =
            new int[MaxGridCells];
    }

    public float GetCost(
        int index,
        int currentFrame)
    {
        if ((uint)index >=
            MaxGridCells)
        {
            return 0f;
        }

        int encoded =
            Volatile.Read(
                ref _heat[index]);

        if (encoded <= 0)
            return 0f;

        float heat =
            encoded / HeatScale;

        int lastFrame =
            Volatile.Read(
                ref _lastFrame[index]);

        int elapsed =
            currentFrame - lastFrame;

        if (elapsed <= 0)
            return heat;

        for (int i = 0;
             i < elapsed;
             i++)
        {
            heat *= HeatDecay;

            if (heat < 0.001f)
                return 0f;
        }

        return heat;
    }

    public void AddRoute(
        SpatialCoord[] path,
        int length,
        int currentFrame)
    {
        if (path == null ||
            length <= 0)
        {
            return;
        }

        if (length > path.Length)
            length = path.Length;

        for (int i = 0;
             i < length;
             i++)
        {
            SpatialCoord p =
                path[i];

            if ((uint)p.X >= MaxGridSize ||
                (uint)p.Y >= MaxGridSize)
            {
                continue;
            }

            int index =
                p.Y * MaxGridSize + p.X;

            AddHeat(
                index,
                currentFrame);
        }
    }

    private void AddHeat(
        int index,
        int currentFrame)
    {
        int encoded =
            Volatile.Read(
                ref _heat[index]);

        float heat =
            encoded / HeatScale;

        int lastFrame =
            Volatile.Read(
                ref _lastFrame[index]);

        int elapsed =
            currentFrame - lastFrame;

        if (elapsed > 0)
        {
            for (int i = 0;
                 i < elapsed;
                 i++)
            {
                heat *= HeatDecay;

                if (heat < 0.001f)
                {
                    heat = 0f;
                    break;
                }
            }
        }

        heat =
            MathF.Min(
                MaxHeat,
                heat + HeatAdd);

        Volatile.Write(
            ref _lastFrame[index],
            currentFrame);

        Volatile.Write(
            ref _heat[index],
            (int)(heat * HeatScale));
    }
}