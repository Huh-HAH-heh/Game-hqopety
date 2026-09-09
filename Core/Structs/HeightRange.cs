namespace Core.Structs;

public readonly struct HeightRange
{
    public readonly float Min;
    public readonly float Max;

    public HeightRange(float min, float max)
    {
        if (min <= max)
        {
            Min = min;
            Max = max;
        }
        else
        {
            Min = max;
            Max = min;
        }
    }

    public static HeightRange Point(float height)
    {
        return new HeightRange(height, height);
    }
}