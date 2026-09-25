namespace Core.Map;

public readonly struct Tile
{
    public const int MaxRanges = 250;

    public byte X { get; }
    public byte Y { get; }

    public Tile(
        byte x,
        byte y)
    {
        X = x;
        Y = y;
    }
}
