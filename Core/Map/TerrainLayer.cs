using System;

namespace Core.Map;

public sealed class TerrainLayer
{
    private readonly ushort[] _materialIds;

    public int ZLevel { get; }
    public int Width { get; }
    public int Height { get; }

    public ReadOnlySpan<ushort> MaterialIds =>
        _materialIds;

    public TerrainLayer(
        int zLevel,
        int width,
        int height)
    {
        if (zLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        ZLevel = zLevel;
        Width = width;
        Height = height;

        _materialIds =
            new ushort[checked(width * height)];
    }

    public ushort GetMaterialId(
        int x,
        int y)
    {
        if (!IsInside(x, y))
            return 0;

        return _materialIds[x + y * Width];
    }

    internal void SetMaterialId(
        int x,
        int y,
        ushort materialId)
    {
        if (!IsInside(x, y))
            throw new IndexOutOfRangeException();

        _materialIds[x + y * Width] = materialId;
    }

    internal ushort GetMaterialIdAtIndex(
        int index)
    {
        return _materialIds[index];
    }

    internal void SetMaterialIdAtIndex(
        int index,
        ushort materialId)
    {
        _materialIds[index] = materialId;
    }

    internal void Clear()
    {
        Array.Clear(_materialIds);
    }

    private bool IsInside(
        int x,
        int y)
    {
        return
            x >= 0 &&
            y >= 0 &&
            x < Width &&
            y < Height;
    }
}
