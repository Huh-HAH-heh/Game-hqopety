using Core.Structs;
using System;

namespace Core.Map;

public class MapRegion
{
    // По-прежнему 16 больших логических тайлов для космоса/моря
    public const int Size = 16;
    // Коэффициент микро-деления (3х3)
    public const int SubDivision = 3;

    // Ширина региона в микро-ячейках = 48
    public const int MicroSize = Size * SubDivision;
    // Всего микро-ячеек в чанке = 2304
    public const int TotalMicroCells = MicroSize * MicroSize;

    public int RegionX { get; private set; }
    public int RegionY { get; private set; }
    public int ZLevel { get; private set; }

    // Теперь чанк хранит плоский массив сверхлегких микро-ячеек вместо Tile
    private readonly MicroCell[] _microCells;

    public MapRegion(int regionX, int regionY, int zLevel)
    {
        RegionX = regionX;
        RegionY = regionY;
        ZLevel = zLevel;
        _microCells = new MicroCell[TotalMicroCells];
    }

    /// <summary>
    /// Мгновенный доступ по локальным МИКРО-координатам (0..47) внутри чанка.
    /// </summary>
    public ref MicroCell GetLocalMicroCell(int lmx, int lmy)
    {
        // Формула плоской матрицы для ширины 48
        int index = lmx + (lmy * MicroSize);
        return ref _microCells[index];
    }

    /// <summary>
    /// Высокопроизводительный итератор для рендера и логики.
    /// Возвращает микро-ячейку и её ГЛОБАЛЬНЫЕ микро-координаты в мире.
    /// </summary>
    public void ForEachMicroCell(MicroCellIteratorDelegate action)
    {
        // Начальные глобальные микро-координаты левого верхнего угла этого чанка
        int regionLeftMicroX = RegionX * MicroSize;
        int regionTopMicroY = RegionY * MicroSize;

        for (int i = 0; i < TotalMicroCells; i++)
        {
            // Переводим одномерный индекс массива в 2D локальные координаты чанка
            int lmx = i % MicroSize;
            int lmy = i / MicroSize;

            int gmx = regionLeftMicroX + lmx;
            int gmy = regionTopMicroY + lmy;

            action(ref _microCells[i], gmx, gmy);
        }
    }
}

public delegate void MicroCellIteratorDelegate(ref MicroCell cell, int globalMicroX, int globalMicroY);
