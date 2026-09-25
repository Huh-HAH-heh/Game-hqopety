using System;
using System.Collections.Generic;
using Core.Map;
using Core.Unit.Components;

namespace Core.Unit
{
    public sealed class UnitSpatialGrid
    {
        private readonly int[] _cellHeadUnitId;

        // ВАЖНО:
        // Теперь массив юнитов НЕ readonly, чтобы сетка могла
        // автоматически расширяться вместе с UnitStore.
        private int[] _unitNextUnitId;

        private readonly int _layerMicroWidth;
        private readonly int _layerMicroHeight;

        private readonly List<int> _emptyList =
            new List<int>(0);

        private readonly List<int> _queryBuffer =
            new List<int>(16);

        public UnitSpatialGrid(
            int maxUnits,
            int regionsX = 16,
            int regionsY = 16,
            int subDivision = 3)
        {
            if (maxUnits < 1)
                maxUnits = 1;

            if (regionsX < 1)
                regionsX = 1;

            if (regionsY < 1)
                regionsY = 1;

            if (subDivision < 1)
                subDivision = 1;

            int cellsPerChunkSide =
                16 * subDivision;

            _layerMicroWidth =
                regionsX * cellsPerChunkSide;

            _layerMicroHeight =
                regionsY * cellsPerChunkSide;

            long calculatedSize =
                (long)_layerMicroWidth *
                _layerMicroHeight;

            if (calculatedSize <= 0 ||
                calculatedSize > 50_000_000)
            {
                _layerMicroWidth = 768;
                _layerMicroHeight = 768;
                calculatedSize = 768L * 768L;
            }

            int totalMicroCells =
                (int)calculatedSize;

            _cellHeadUnitId =
                new int[totalMicroCells];

            _unitNextUnitId =
                new int[maxUnits];

            Array.Fill(
                _cellHeadUnitId,
                -1
            );

            Array.Fill(
                _unitNextUnitId,
                -1
            );
        }

        public int Width =>
            _layerMicroWidth;

        public int Height =>
            _layerMicroHeight;

        public int MaxUnits =>
            _unitNextUnitId.Length;

        private bool IsInside(
            int x,
            int y)
        {
            return
                x >= 0 &&
                x < _layerMicroWidth &&
                y >= 0 &&
                y < _layerMicroHeight;
        }

        private bool IsValidUnitId(
            int unitId)
        {
            return
                unitId >= 0 &&
                unitId < _unitNextUnitId.Length;
        }

        private int GetCellIndex(
            int x,
            int y)
        {
            return
                y * _layerMicroWidth +
                x;
        }

        private void EnsureUnitCapacity(
            int unitId)
        {
            if (unitId < _unitNextUnitId.Length)
                return;

            int oldSize =
                _unitNextUnitId.Length;

            int newSize =
                Math.Max(
                    unitId + 1,
                    Math.Max(
                        16,
                        oldSize * 2
                    )
                );

            Array.Resize(
                ref _unitNextUnitId,
                newSize
            );

            Array.Fill(
                _unitNextUnitId,
                -1,
                oldSize,
                newSize - oldSize
            );
        }

        public void Add(
            SpatialCoord coord,
            int unitId)
        {
            if (unitId < 0)
                return;

            if (!IsInside(
                coord.X,
                coord.Y))
            {
                return;
            }

            // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ:
            // UnitStore может автоматически расшириться,
            // поэтому SpatialGrid тоже обязан расширить массив
            // под новый ID юнита.
            EnsureUnitCapacity(unitId);

            int cellIndex =
                GetCellIndex(
                    coord.X,
                    coord.Y
                );

            // Если этот юнит уже является головой этой клетки,
            // повторно добавлять его не нужно.
            if (_cellHeadUnitId[cellIndex] == unitId)
                return;

            // Если у юнита уже есть ссылка на следующий элемент,
            // он уже зарегистрирован в какой-то цепочке.
            if (_unitNextUnitId[unitId] != -1)
                return;

            _unitNextUnitId[unitId] =
                _cellHeadUnitId[cellIndex];

            _cellHeadUnitId[cellIndex] =
                unitId;
        }

        public void Remove(
            SpatialCoord coord,
            int unitId)
        {
            if (!IsValidUnitId(unitId))
                return;

            if (!IsInside(
                coord.X,
                coord.Y))
            {
                return;
            }

            int cellIndex =
                GetCellIndex(
                    coord.X,
                    coord.Y
                );

            int current =
                _cellHeadUnitId[cellIndex];

            int previous =
                -1;

            int safetyCounter =
                0;

            while (current != -1)
            {
                if (!IsValidUnitId(current))
                {
                    return;
                }

                if (current == unitId)
                {
                    int next =
                        _unitNextUnitId[current];

                    if (previous == -1)
                    {
                        _cellHeadUnitId[cellIndex] =
                            next;
                    }
                    else
                    {
                        _unitNextUnitId[previous] =
                            next;
                    }

                    _unitNextUnitId[current] =
                        -1;

                    return;
                }

                previous =
                    current;

                current =
                    _unitNextUnitId[current];

                safetyCounter++;

                if (safetyCounter > 10000)
                {
                    Console.WriteLine(
                        $"[SPATIAL GRID] Remove: обнаружена циклическая ссылка в ({coord.X},{coord.Y})"
                    );

                    return;
                }
            }
        }

        public void Move(
            SpatialCoord from,
            SpatialCoord to,
            int unitId)
        {
            if (from == to)
                return;

            Remove(
                from,
                unitId
            );

            Add(
                to,
                unitId
            );
        }

        public IReadOnlyList<int> GetUnitsAt(
            SpatialCoord coord)
        {
            if (!IsInside(
                coord.X,
                coord.Y))
            {
                return _emptyList;
            }

            int cellIndex =
                GetCellIndex(
                    coord.X,
                    coord.Y
                );

            int current =
                _cellHeadUnitId[cellIndex];

            if (current == -1)
                return _emptyList;

            _queryBuffer.Clear();

            int safetyCounter =
                0;

            while (current != -1)
            {
                if (!IsValidUnitId(current))
                    break;

                _queryBuffer.Add(
                    current
                );

                current =
                    _unitNextUnitId[current];

                safetyCounter++;

                if (safetyCounter > 10000)
                {
                    Console.WriteLine(
                        $"[SPATIAL GRID] GetUnitsAt: обнаружена циклическая ссылка в ({coord.X},{coord.Y})"
                    );

                    break;
                }
            }

            return _queryBuffer;
        }

        public void GetNearby(
            SpatialCoord center,
            int cellRadius,
            List<int> resultList)
        {
            if (resultList == null)
                return;

            if (cellRadius < 0)
                return;

            int minX =
                Math.Max(
                    0,
                    center.X - cellRadius
                );

            int maxX =
                Math.Min(
                    _layerMicroWidth - 1,
                    center.X + cellRadius
                );

            int minY =
                Math.Max(
                    0,
                    center.Y - cellRadius
                );

            int maxY =
                Math.Min(
                    _layerMicroHeight - 1,
                    center.Y + cellRadius
                );

            for (int y = minY;
                 y <= maxY;
                 y++)
            {
                for (int x = minX;
                     x <= maxX;
                     x++)
                {
                    int cellIndex =
                        y * _layerMicroWidth +
                        x;

                    int current =
                        _cellHeadUnitId[cellIndex];

                    int safetyCounter =
                        0;

                    while (current != -1)
                    {
                        if (!IsValidUnitId(current))
                            break;

                        resultList.Add(
                            current
                        );

                        current =
                            _unitNextUnitId[current];

                        safetyCounter++;

                        if (safetyCounter > 10000)
                        {
                            Console.WriteLine(
                                $"[SPATIAL GRID] GetNearby: цикл в ({x},{y})"
                            );

                            break;
                        }
                    }
                }
            }
        }
    }
}