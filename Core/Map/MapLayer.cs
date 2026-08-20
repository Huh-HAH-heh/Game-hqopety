using Core.Structs;
using System;

namespace Core.Map;

/// <summary>
/// Слой карты, состоящий из сетки регионов 16х16.
/// Управляет пространственным распределением чанков.
/// </summary>
public class MapLayer
{
    public const int WidthInRegions = 16;
    public const int HeightInRegions = 16;

    public int ZLevel { get; private set; }

    private readonly MapRegion[,] _regions;

    public MapLayer(int zLevel)
    {
        ZLevel = zLevel;
        _regions = new MapRegion[WidthInRegions, HeightInRegions];

        for (int ry = 0; ry < HeightInRegions; ry++)
        {
            for (int rx = 0; rx < WidthInRegions; rx++)
            {
                _regions[rx, ry] = new MapRegion(rx, ry, ZLevel);
            }
        }
    }

    /// <summary>
    /// Прямой доступ к региону для систем глобального поиска и адресации.
    /// </summary>
    public MapRegion GetRegion(int rx, int ry)
    {
        if (rx < 0 || rx >= WidthInRegions || ry < 0 || ry >= HeightInRegions)
            return null;

        return _regions[rx, ry];
    }

    /// <summary>
    /// Передает во внешний код (рендерер) только те регионы, которые попадают в рамки видимости.
    /// </summary>
    public void ForEachVisibleRegion(int minRegionX, int maxRegionX, int minRegionY, int maxRegionY, Action<MapRegion> action)
    {
        // Жесткое ограничение по границам слоя
        minRegionX = Math.Max(0, minRegionX);
        maxRegionX = Math.Min(WidthInRegions - 1, maxRegionX);
        minRegionY = Math.Max(0, minRegionY);
        maxRegionY = Math.Min(HeightInRegions - 1, maxRegionY);

        for (int ry = minRegionY; ry <= maxRegionY; ry++)
        {
            for (int rx = minRegionX; rx <= maxRegionX; rx++)
            {
                action(_regions[rx, ry]);
            }
        }
    }

    /// <summary>
    /// Фасад для работы с микро-ячейкой напрямую по ГЛОБАЛЬНЫМ микро-координатам мира.
    /// </summary>
    public ref MicroCell GetMicroCell(int globalMicroX, int globalMicroY)
    {
        // Теперь размер чанка — 48 микро-ячеек. 
        // Деление на константу (48) компилятор C# автоматически оптимизирует в быстрые битовые инструкции мультипликации (Mul), так что обычный знак '/' здесь работает со скоростью сдвига.
        int rx = globalMicroX / MapRegion.MicroSize;
        int ry = globalMicroY / MapRegion.MicroSize;

        if (rx < 0 || rx >= WidthInRegions || ry < 0 || ry >= HeightInRegions)
            throw new IndexOutOfRangeException("Глобальные микро-координаты вышли за пределы слоя карты.");

        // Получаем локальные координаты микро-ячейки внутри чанка (остаток от деления на 48)
        int lmx = globalMicroX % MapRegion.MicroSize;
        int lmy = globalMicroY % MapRegion.MicroSize;

        // Запрашиваем ячейку у региона по микро-координатам
        return ref _regions[rx, ry].GetLocalMicroCell(lmx, lmy);
    }
}
