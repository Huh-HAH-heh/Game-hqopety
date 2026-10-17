using System;

namespace Core.Map;

public sealed class WaterLayer
{
    // Allocate storage only for columns that actually contain water.
    // A dense 768x768x320 byte array costs about 180 MiB even on a dry map.
    private readonly byte[]?[] _waterByColumn;
    private readonly short[] _topLevels;

    public int Width { get; }
    public int Height { get; }
    public int Levels { get; }
    internal int AllocatedColumnCount { get; private set; }

    public long Version { get; private set; }

    public WaterLayer(
        int width,
        int height,
        int levels = 320)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        if (levels <= 0 || levels > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(levels));

        Width = width;
        Height = height;
        Levels = levels;

        _waterByColumn = new byte[]?[checked(width * height)];

        _topLevels =
            new short[
                width *
                height];

        Array.Fill(
            _topLevels,
            (short)-1);

        Version++;
    }

    public byte GetAmount(
        int x,
        int y,
        int z)
    {
        if (!IsInside(x, y, z))
            return 0;

        byte[]? column = _waterByColumn[GetColumnIndex(x, y)];
        return column == null ? (byte)0 : column[z];
    }

    public void SetAmount(
        int x,
        int y,
        int z,
        byte amount)
    {
        if (!IsInside(x, y, z))
            throw new IndexOutOfRangeException();

        int columnIndex = GetColumnIndex(x, y);
        byte[]? column = _waterByColumn[columnIndex];

        if (amount > 0 && column == null)
        {
            column = new byte[Levels];
            _waterByColumn[columnIndex] = column;
            AllocatedColumnCount++;
        }

        if (column != null)
            column[z] = amount;

        Version++;

        int top = _topLevels[columnIndex];

        if (amount > 0)
        {
            if (z > top)
                _topLevels[columnIndex] = (short)z;

            return;
        }

        if (z != top)
            return;

        for (int level = z - 1;
             level >= 0;
             level--)
        {
            if (column != null && column[level] > 0)
            {
                _topLevels[columnIndex] =
                    (short)level;

                return;
            }
        }

        _topLevels[columnIndex] = -1;
        _waterByColumn[columnIndex] = null;
        AllocatedColumnCount--;
    }

    public void AddAmount(
        int x,
        int y,
        int z,
        int amount)
    {
        if (!IsInside(x, y, z))
            return;

        int columnIndex = GetColumnIndex(x, y);
        byte[]? column = _waterByColumn[columnIndex];

        if (column == null)
        {
            if (amount <= 0)
            {
                Version++;
                return;
            }

            column = new byte[Levels];
            _waterByColumn[columnIndex] = column;
            AllocatedColumnCount++;
        }

        column[z] =
            (byte)Math.Clamp(
                column[z] + amount,
                0,
                byte.MaxValue);

        Version++;

        if (column[z] > 0)
        {
            if (z > _topLevels[columnIndex])
                _topLevels[columnIndex] = (short)z;

            return;
        }

        // Keep the cached top level exact when the highest water cell drains.
        if (z != _topLevels[columnIndex])
            return;

        for (int level = z - 1; level >= 0; level--)
        {
            if (column[level] <= 0)
                continue;

            _topLevels[columnIndex] = (short)level;
            return;
        }

        _topLevels[columnIndex] = -1;
        _waterByColumn[columnIndex] = null;
        AllocatedColumnCount--;
    }

    public bool HasWater(
        int x,
        int y,
        int z)
    {
        return GetAmount(x, y, z) > 0;
    }

    public int GetTopLevel(
        int x,
        int y)
    {
        if (x < 0 ||
            y < 0 ||
            x >= Width ||
            y >= Height)
        {
            return -1;
        }

        return _topLevels[GetColumnIndex(x, y)];
    }

    public void Clear()
    {
        Array.Clear(_waterByColumn);
        AllocatedColumnCount = 0;

        Array.Fill(
            _topLevels,
            (short)-1);

        Version++;
    }

    private int GetColumnIndex(
        int x,
        int y)
    {
        return x + y * Width;
    }

    private bool IsInside(
        int x,
        int y,
        int z)
    {
        return
            x >= 0 &&
            y >= 0 &&
            z >= 0 &&
            x < Width &&
            y < Height &&
            z < Levels;
    }
}
