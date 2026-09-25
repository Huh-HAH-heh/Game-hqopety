using System;

namespace Core.Map;

public sealed class WaterLayer
{
    private readonly byte[] _water;

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

        if (levels <= 0)
            throw new ArgumentOutOfRangeException(nameof(levels));

        Width = width;
        Height = height;
        Levels = levels;

        _water =
            new byte[
                width *
                height *
                levels];
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

        _water[GetIndex(x, y, z)] = amount;
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

        for (int z = Levels - 1;
             z >= 0;
             z--)
        {
            if (_water[GetIndex(x, y, z)] > 0)
                return z;
        }

        return -1;
    }

    public void Clear()
    {
        Array.Clear(_water);
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
