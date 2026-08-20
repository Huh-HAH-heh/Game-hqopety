namespace Core.Unit
{
    public struct UnitSize
    {
        // Размер в половинах spatial-cell.
        //
        // 1.0 cell = 2
        // 1.5 cell = 3
        // 2.0 cell = 4
        // 2.5 cell = 5

        public ushort Width;
        public ushort Height;

        public UnitSize(
            ushort width,
            ushort height)
        {
            Width = width;
            Height = height;
        }

        public float WidthCells => Width / 2f;
        public float HeightCells => Height / 2f;
    }
}