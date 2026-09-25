using System;

namespace Core.Map;

public sealed class WaterLayer
{
    private readonly float[] _levels;

    public int Width { get; }
    public int Height { get; }

    public WaterLayer(int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        _levels = new float[width * height];
    }

    public float GetLevel(int x, int y)
    {
        if (!IsInside(x, y))
            return 0f;

        return _levels[x + y * Width];
    }

    public void SetLevel(int x, int y, float level)
    {
        if (!IsInside(x, y))
            throw new IndexOutOfRangeException();

        _levels[x + y * Width] = MathF.Max(0f, level);
    }

    public float GetDepth(int x, int y, float terrainHeight)
    {
        return MathF.Max(0f, GetLevel(x, y) - terrainHeight);
    }

    public bool HasWater(int x, int y, float terrainHeight)
    {
        return GetLevel(x, y) > terrainHeight;
    }

    public void Clear()
    {
        Array.Clear(_levels);
    }

    private bool IsInside(int x, int y)
    {
        return x >= 0 && y >= 0 && x < Width && y < Height;
    }
}
