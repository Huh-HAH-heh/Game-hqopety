using System;
using System.Threading;
using Core.Unit.Components;

namespace Core.AI;

public sealed class PathTrafficMemory
{
    private const int MaxGridSize = 768;

    private const int MaxGridCells =
        MaxGridSize * MaxGridSize;

    /*
     * ============================================================
     * TRAFFIC HEAT
     * ============================================================
     */

    private const float HeatScale =
        10000f;

    private const float HeatAdd =
        1.0f;

    private const float HeatDecay =
        0.85f;

    private const float MaxHeat =
        4.0f;

    /*
     * ============================================================
     * OCCUPANCY
     * ============================================================
     *
     * 0 = пусто
     * 1 = один юнит
     * 2 = два юнита
     * 3 = полностью занято
     *
     * Для pathfinding:
     *
     * occupancy >= 3
     *          ↓
     *     непроходимо
     *
     * Occupancy не отменяет приказ.
     * Он только влияет на возможность построения
     * конкретного маршрута.
     * ============================================================
     */

    private const int MaxUnitsPerTile = 3;

    private readonly byte[] _occupancy;

    private readonly int[] _heat;

    private readonly int[] _lastFrame;

    public PathTrafficMemory()
    {
        _occupancy =
            new byte[
                MaxGridCells];

        _heat =
            new int[
                MaxGridCells];

        _lastFrame =
            new int[
                MaxGridCells];
    }

    /*
     * ============================================================
     * OCCUPANCY
     * ============================================================
     */

    public void ClearOccupancy()
    {
        Array.Clear(
            _occupancy,
            0,
            _occupancy.Length);
    }

    public void SetOccupancy(
        int index,
        int count)
    {
        if ((uint)index >=
            MaxGridCells)
        {
            return;
        }

        if (count < 0)
            count = 0;

        if (count > MaxUnitsPerTile)
            count = MaxUnitsPerTile;

        Volatile.Write(
            ref _occupancy[index],
            (byte)count);
    }

    public void SetOccupancy(
        int x,
        int y,
        int count)
    {
        if ((uint)x >=
                MaxGridSize ||
            (uint)y >=
                MaxGridSize)
        {
            return;
        }

        SetOccupancy(
            y * MaxGridSize + x,
            count);
    }

    public int GetOccupancy(
        int index)
    {
        if ((uint)index >=
            MaxGridCells)
        {
            return 0;
        }

        return Volatile.Read(
            ref _occupancy[index]);
    }

    public bool IsFull(
        int index)
    {
        return
            GetOccupancy(index) >=
            MaxUnitsPerTile;
    }

    /*
     * Удобная проверка координатами.
     */
    public bool IsFull(
        int x,
        int y)
    {
        if ((uint)x >=
                MaxGridSize ||
            (uint)y >=
                MaxGridSize)
        {
            return false;
        }

        return IsFull(
            y * MaxGridSize + x);
    }

    /*
     * ============================================================
     * TRAFFIC HEAT
     * ============================================================
     *
     * Это отдельная система от occupancy.
     *
     * Occupancy:
     *     3/3 = нельзя пройти.
     *
     * Traffic heat:
     *     маршрут можно пройти,
     *     но клетка становится менее привлекательной.
     * ============================================================
     */

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
            encoded /
            HeatScale;

        int lastFrame =
            Volatile.Read(
                ref _lastFrame[index]);

        int elapsed =
            currentFrame -
            lastFrame;

        if (elapsed <= 0)
            return heat;

        for (int i = 0;
             i < elapsed;
             i++)
        {
            heat *=
                HeatDecay;

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

        if (length >
            path.Length)
        {
            length =
                path.Length;
        }

        for (int i = 0;
             i < length;
             i++)
        {
            SpatialCoord p =
                path[i];

            if ((uint)p.X >=
                    MaxGridSize ||
                (uint)p.Y >=
                    MaxGridSize)
            {
                continue;
            }

            int index =
                p.Y *
                MaxGridSize +
                p.X;

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
            encoded /
            HeatScale;

        int lastFrame =
            Volatile.Read(
                ref _lastFrame[index]);

        int elapsed =
            currentFrame -
            lastFrame;

        if (elapsed > 0)
        {
            for (int i = 0;
                 i < elapsed;
                 i++)
            {
                heat *=
                    HeatDecay;

                if (heat < 0.001f)
                {
                    heat =
                        0f;

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
            (int)(
                heat *
                HeatScale));
    }
}