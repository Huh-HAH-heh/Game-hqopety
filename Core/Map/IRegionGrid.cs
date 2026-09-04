using Core.Structs;

namespace Core.Map;

public interface IRegionGrid
{
    int WidthInRegions { get; }
    int HeightInRegions { get; }
    MapRegion GetRegion(int regionX, int regionY);
    ref MicroCell GetMicroCell(int globalX, int globalY);
    bool IsPassable(int globalX, int globalY);
    bool CanStep(int srcX, int srcY, int dstX, int dstY);
}