using System;
using System.Threading;
using Core.Unit.Components;

namespace Core.AI;

public sealed class PathTrafficMemory
{
    private const int MaxGridSize = 768;

    private const int MaxGridCells =
        MaxGridSize * MaxGridSize;

    // ============================================================
    // HEAT
    // ============================================================

    private const float HeatScale = 10000f;

    // Каждый маршрут заметно загрязняет карту.
    private const float HeatAdd = 1.5f;

    // Долгая память.
    private const float HeatDecay = 0.992f;

    private const float MinHeat = 0.01f;

    private const float MaxHeat = 12f;

    // Радиус "коридора" старого маршрута.
    private const int RouteMemoryRadius = 1;

    // ============================================================
    // OCCUPANCY
    // ============================================================

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

    // ============================================================
    // OCCUPANCY
    // ============================================================

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
        if ((uint)x >= MaxGridSize ||
            (uint)y >= MaxGridSize)
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

    public bool IsFull(
        int x,
        int y)
    {
        if ((uint)x >= MaxGridSize ||
            (uint)y >= MaxGridSize)
        {
            return false;
        }

        return IsFull(
            y * MaxGridSize + x);
    }

    // ============================================================
    // TRAFFIC COST
    // ============================================================

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

        if (elapsed > 0)
        {
            heat *=
                MathF.Pow(
                    HeatDecay,
                    elapsed);
        }

        if (heat < MinHeat)
            return 0f;

        if (heat > MaxHeat)
            return MaxHeat;

        return heat;
    }

    // ============================================================
    // ADD ROUTE
    // ============================================================

    public void AddRoute(
        SpatialCoord[] path,
        int length,
        int currentFrame)
    {
        if (path == null ||
            length <= 1)
        {
            return;
        }

        if (length > path.Length)
            length = path.Length;

        // Не засоряем стартовую клетку.
        for (int i = 1;
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

            // Центральная клетка маршрута.
            AddHeat(
                p.X,
                p.Y,
                HeatAdd,
                currentFrame);

            // ====================================================
            // КОРИДОР ВОКРУГ МАРШРУТА
            // ====================================================

            for (int dy = -RouteMemoryRadius;
                 dy <= RouteMemoryRadius;
                 dy++)
            {
                for (int dx = -RouteMemoryRadius;
                     dx <= RouteMemoryRadius;
                     dx++)
                {
                    if (dx == 0 &&
                        dy == 0)
                    {
                        continue;
                    }

                    int nx =
                        p.X + dx;

                    int ny =
                        p.Y + dy;

                    if ((uint)nx >= MaxGridSize ||
                        (uint)ny >= MaxGridSize)
                    {
                        continue;
                    }

                    // Чем дальше от центра,
                    // тем слабее память.
                    int distance =
                        Math.Abs(dx) +
                        Math.Abs(dy);

                    float add =
                        distance == 1
                            ? HeatAdd * 0.45f
                            : HeatAdd * 0.20f;

                    AddHeat(
                        nx,
                        ny,
                        add,
                        currentFrame);
                }
            }
        }
    }

    // ============================================================
    // ADD HEAT
    // ============================================================

    private void AddHeat(
        int x,
        int y,
        float amount,
        int currentFrame)
    {
        int index =
            y * MaxGridSize + x;

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
            heat *=
                MathF.Pow(
                    HeatDecay,
                    elapsed);
        }

        heat += amount;

        if (heat > MaxHeat)
            heat = MaxHeat;

        Volatile.Write(
            ref _lastFrame[index],
            currentFrame);

        Volatile.Write(
            ref _heat[index],
            (int)(
                heat *
                HeatScale));
    }

    // ============================================================
    // DEBUG
    // ============================================================

    public float GetHeat(
        int index,
        int currentFrame)
    {
        return GetCost(
            index,
            currentFrame);
    }

    public float GetHeat(
        int x,
        int y,
        int currentFrame)
    {
        if ((uint)x >= MaxGridSize ||
            (uint)y >= MaxGridSize)
        {
            return 0f;
        }

        return GetCost(
            y * MaxGridSize + x,
            currentFrame);
    }
}