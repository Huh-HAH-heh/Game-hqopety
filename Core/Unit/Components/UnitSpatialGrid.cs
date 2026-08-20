using System;
using System.Collections.Generic;
using Core.Unit.Components;
using Core.Map;

namespace Core.Unit
{
    public sealed class UnitSpatialGrid
    {
        private readonly int[] _cellHeadUnitId;
        private readonly int[] _unitNextUnitId;

        // Делаем ширину динамической, чтобы сетка не зависела от порядка инициализации статических классов
        private readonly int _layerMicroWidth;

        private readonly List<int> _emptyList = new List<int>(0);
        private readonly List<int> _queryBuffer = new List<int>(4);

        /// <summary>
        /// Обновленный безопасный конструктор пространственной сетки.
        /// </summary>
        /// <param name="maxUnits">Максимальное количество муравьев в игре</param>
        /// <param name="regionsX">Ширина карты в чанках (например, 16)</param>
        /// <param name="regionsY">Высота карты в чанках (например, 16)</param>
        /// <param name="subDivision">Количество суб-ячеек в тайле (например, 3)</param>
        public UnitSpatialGrid(int maxUnits, int regionsX = 16, int regionsY = 16, int subDivision = 3)
        {
            // Вычисляем размеры локально и безопасно
            int cellsPerChunkSide = 16 * subDivision; // 16 тайлов в чанке * 3 = 48
            _layerMicroWidth = regionsX * cellsPerChunkSide; // 16 * 48 = 768
            int layerMicroHeight = regionsY * cellsPerChunkSide;

            // Защита (Sanity Check): предотвращаем дикие аллокации, если переданы неверные данные
            long calculatedSize = (long)_layerMicroWidth * layerMicroHeight;
            if (calculatedSize <= 0 || calculatedSize > 50_000_000)
            {
                // Если размеры улетели в космос, принудительно ставим безопасный дефолт для вашей карты 16х16
                _layerMicroWidth = 16 * 16 * 3; // 768
                calculatedSize = 768 * 768; // 589 824
            }

            int totalMicroCells = (int)calculatedSize;

            // Выделяем память
            _cellHeadUnitId = new int[totalMicroCells];
            _unitNextUnitId = new int[maxUnits];

            // Заполняем пустыми указателями -1
            Array.Fill(_cellHeadUnitId, -1);
            Array.Fill(_unitNextUnitId, -1);
        }

        // Вшиваем математику перевода 2D микро-координат в плоский 1D индекс
        private int GetCellIndex(int mx, int my)
        {
            return (my * _layerMicroWidth) + mx;
        }

        /// <summary>
        /// Добавить муравья в пространственную микро-ячейку. С защитой от дублирования.
        /// </summary>
        public void Add(SpatialCoord coord, int unitId)
        {
            int cellIndex = GetCellIndex(coord.X, coord.Y);

            // ЗАЩИТА: Если муравей уже зарегистрирован как голова этой ячейки 
            // или его следующая связь уже настроена — игнорируем повторное добавление, 
            // чтобы предотвратить бесконечные циклы памяти (OutOfMemory).
            if (_cellHeadUnitId[cellIndex] == unitId || _unitNextUnitId[unitId] != -1)
            {
                return;
            }

            // Вставляем муравья в начало связанного списка ячейки (Паттерн Head-Next)
            _unitNextUnitId[unitId] = _cellHeadUnitId[cellIndex];
            _cellHeadUnitId[cellIndex] = unitId;
        }

        public void Remove(SpatialCoord coord, int unitId)
        {
            int cellIndex = GetCellIndex(coord.X, coord.Y);
            int current = _cellHeadUnitId[cellIndex];
            int prev = -1;

            while (current != -1)
            {
                if (current == unitId)
                {
                    if (prev == -1)
                        _cellHeadUnitId[cellIndex] = _unitNextUnitId[current];
                    else
                        _unitNextUnitId[prev] = _unitNextUnitId[current];

                    _unitNextUnitId[current] = -1;
                    return;
                }
                prev = current;
                current = _unitNextUnitId[current];
            }
        }

        public void Move(SpatialCoord from, SpatialCoord to, int unitId)
        {
            if (from == to) return;
            Remove(from, unitId);
            Add(to, unitId);
        }

        public IReadOnlyList<int> GetUnitsAt(SpatialCoord coord)
        {
            int cellIndex = GetCellIndex(coord.X, coord.Y);
            int current = _cellHeadUnitId[cellIndex];

            if (current == -1) return _emptyList;

            _queryBuffer.Clear();

            // ИСПРАВЛЕНО: Защитный счетчик. Если в памяти возникнет петля, 
            // игра не зависнет намертво, а просто разорвет цикл!
            int safetyCounter = 0;

            while (current != -1)
            {
                _queryBuffer.Add(current);
                current = _unitNextUnitId[current];

                safetyCounter++;
                if (safetyCounter > 100)
                {
                    break; // Экстренно рвем бесконечный цикл, спасая игру от зависания
                }
            }
            return _queryBuffer;
        }

        /// <summary>
        /// Найти всех муравьев в радиусе вокруг указанной микро-ячейки. С защитой от зависания.
        /// </summary>
        public void GetNearby(SpatialCoord center, int cellRadius, List<int> resultList)
        {
            if (cellRadius < 0) return;

            int minX = Math.Max(0, center.X - cellRadius);
            int maxX = Math.Min(_layerMicroWidth - 1, center.X + cellRadius);

            int minY = Math.Max(0, center.Y - cellRadius);
            int maxY = Math.Min(_layerMicroWidth - 1, center.Y + cellRadius);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int cellIndex = (y * _layerMicroWidth) + x;
                    int currentUnitId = _cellHeadUnitId[cellIndex];

                    int safetyCounter = 0;

                    while (currentUnitId != -1)
                    {
                        resultList.Add(currentUnitId);
                        currentUnitId = _unitNextUnitId[currentUnitId];

                        safetyCounter++;
                        if (safetyCounter > 200)
                        {
                            Console.WriteLine($"⚠️ КРИТИЧЕСКАЯ ОШИБКА: Обнаружена циклическая ссылка для юнита {currentUnitId} в ячейке {x},{y}!");
                            break; // Экстренно рвем бесконечный цикл, предотвращая падение игры
                        }
                    }
                }
            }
        }
    }
}

