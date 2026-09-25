namespace Core.Map;

public struct TileRange
{
    public ushort StartZ;
    public ushort EndZ;

    public ushort MaterialId;
    public byte State;

    public readonly ushort Thickness =>
        (ushort)(EndZ - StartZ);
}
