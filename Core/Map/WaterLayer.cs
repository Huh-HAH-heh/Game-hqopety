using System;

namespace Core.Map;

public sealed class WaterLayer
{
    private readonly byte[] _water;
    private readonly sbyte[] _topLevels;

    public int Width { get; }
    public int Height { get; }
    public int Levels { get; }

    public WaterLayer(
        int width,
        int height,
        int levels = 32)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        if (levels <= 0 || levels > sbyte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(levels));

        Width = width;
        Height = height;
        Levels = levels;

        _water =
            new byte[
                width *
                height *
                levels];

        _topLevels =
            new sbyte[
                width *
                height];

        Array.Fill(
            _topLevels,
            (sbyte)-1);
    }

    public byte GetAmount(
        int x,
        int y,
        int z)
    {
        if (!IsInside(x, y, z))
            return 0;

        return _water[GetIndex(x, y, z)];
    }

    public void SetAmount(
        int x,
        int y,
        int z,
        byte amount)
    {
        if (!IsInside(x, y, z))
            throw new IndexOutOfRangeException();

        int index = GetIndex(x, y, z);
        _water[index] = amount;

        int columnIndex = GetColumnIndex(x, y);
        int top = _topLevels[columnIndex];

        if (amount > 0)
        {
            if (z > top)
                _topLevels[columnIndex] = (sbyte)z;

            return;
        }

        if (z != top)
            return;

        for (int level = z - 1;
             level >= 0;
             level--)
        {
            if (_water[GetIndex(x, y, level)] > 0)
            {
                _topLevels[columnIndex] =
                    (sbyte)level;

                return;
            }
        }

        _topLevels[columnIndex] = -1;
    }

    public void AddAmount(
        int x,
        int y,
        int z,
        int amount)
    {
        if (!IsInside(x, y, z))
            return;

        int index = GetIndex(x, y, z);

        _water[index] =
            (byte)Math.Clamp(
                _water[index] + amount,
                0,
                byte.MaxValue);

        if (_water[index] > 0)
        {
            int columnIndex = GetColumnIndex(x, y);

            if (z > _topLevels[columnIndex])
                _topLevels[columnIndex] = (sbyte)z;
        }
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
        Array.Clear(_water);

        Array.Fill(
            _topLevels,
            (sbyte)-1);
    }

    private int GetColumnIndex(
        int x,
        int y)
    {
        return x + y * Width;
    }

    private int GetIndex(
        int x,
        int y,
        int z)
    {
        return
            x +
            y * Width +
            z * Width * Height;
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
