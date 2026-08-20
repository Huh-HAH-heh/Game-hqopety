
namespace Core.Unit
{
    public struct TileCoord
    {
        public int X;
        public int Y;
        public int Z;

        public TileCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        public enum TileSlot : byte
        {
            Center = 0,

            TopLeft = 1,
            TopRight = 2,
            BottomRight = 3,
            BottomLeft = 4
        }
    }
}

