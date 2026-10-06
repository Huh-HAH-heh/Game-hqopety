using System;
using Core.Map;

namespace Core.Unit;

public sealed class UnitSpatialGrid
{
    private int _width;
    private int _height;

    private int[] _cellHeads = Array.Empty<int>();
    private int[] _nodeUnit = Array.Empty<int>();
    private int[] _nodeNext = Array.Empty<int>();
    private int[] _touchedCells = Array.Empty<int>();
    private int _touchedCount;
    private int _nodeCount;

    public void Ensure(
        WorldMap worldMap,
        int unitCapacity)
    {
        int width = worldMap.TileWidth;
        int height = worldMap.TileHeight;

        if (_width == width &&
            _height == height &&
            _nodeUnit.Length >= unitCapacity)
        {
            return;
        }

        _width = width;
        _height = height;

        int cellCount =
            width *
            height;

        _cellHeads =
            new int[cellCount];

        Array.Fill(
            _cellHeads,
            -1);

        _touchedCells =
            new int[cellCount];

        int nodeCapacity =
            Math.Max(
                64,
                unitCapacity * 16);

        _nodeUnit =
            new int[nodeCapacity];

        _nodeNext =
            new int[nodeCapacity];

        _touchedCount = 0;
        _nodeCount = 0;
    }

    public void Build(
        UnitStore units)
    {
        for (int i = 0;
             i < _touchedCount;
             i++)
        {
            _cellHeads[
                _touchedCells[i]] = -1;
        }

        _touchedCount = 0;
        _nodeCount = 0;

        ReadOnlySpan<int> active =
            units.ActiveIndices;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            float radius =
                MathF.Max(
                    0.05f,
                    units.Radius[unit]);

            int minX =
                Math.Max(
                    0,
                    (int)MathF.Floor(
                        units.Position[unit].X -
                        radius));

            int maxX =
                Math.Min(
                    _width - 1,
                    (int)MathF.Floor(
                        units.Position[unit].X +
                        radius));

            int minY =
                Math.Max(
                    0,
                    (int)MathF.Floor(
                        units.Position[unit].Y -
                        radius));

            int maxY =
                Math.Min(
                    _height - 1,
                    (int)MathF.Floor(
                        units.Position[unit].Y +
                        radius));

            for (int y = minY;
                 y <= maxY;
                 y++)
            {
                for (int x = minX;
                     x <= maxX;
                     x++)
                {
                    Add(
                        x,
                        y,
                        unit);
                }
            }
        }
    }

    public int GetCellHead(
        int x,
        int y)
    {
        if (x < 0 ||
            y < 0 ||
            x >= _width ||
            y >= _height)
        {
            return -1;
        }

        return _cellHeads[
            x +
            y * _width];
    }

    public int GetNextNode(
        int node)
    {
        return _nodeNext[node];
    }

    public int GetNodeUnit(
        int node)
    {
        return _nodeUnit[node];
    }

    private void Add(
        int x,
        int y,
        int unit)
    {
        int cell =
            x +
            y * _width;

        if (_cellHeads[cell] < 0)
        {
            _touchedCells[
                _touchedCount++] =
                cell;
        }

        EnsureNodeCapacity(
            _nodeCount + 1);

        int node =
            _nodeCount++;

        _nodeUnit[node] = unit;
        _nodeNext[node] =
            _cellHeads[cell];

        _cellHeads[cell] = node;
    }

    private void EnsureNodeCapacity(
        int required)
    {
        if (required <=
            _nodeUnit.Length)
        {
            return;
        }

        int newCapacity =
            _nodeUnit.Length * 2;

        if (newCapacity < required)
            newCapacity = required;

        Array.Resize(
            ref _nodeUnit,
            newCapacity);

        Array.Resize(
            ref _nodeNext,
            newCapacity);
    }
}
