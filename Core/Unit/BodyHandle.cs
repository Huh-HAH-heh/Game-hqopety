namespace Core.Unit;

public readonly struct BodyHandle
{
    public int Start { get; }
    public int Count { get; }

    public BodyHandle(
        int start,
        int count)
    {
        Start = start;
        Count = count;
    }
}
