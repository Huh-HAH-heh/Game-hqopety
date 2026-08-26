// Path: Assets/Scripts/AI/AsyncPathfindingTester.cs
using Core.AI;
using Core.Map;
using Core.Structs;
using System;
using System.Threading;

namespace RimClone.Render.Test
{
    /// <summary>
    /// Полностью изолированный класс для стресс-тестирования асинхронного поиска пути A* "в вакууме".
    /// Выбирает случайные проходимые точки на фрактальной карте и обсчитывает маршруты в фоновом потоке.
    /// </summary>
    public class AsyncPathfindingTester : IDisposable
    {
        /// <summary>
        /// тест
        /// </summary>
        private AsyncPathfindingTester _pathTester;
/// <summary>
/// тест
/// </summary>
        private const ushort FLAG_WALKABLE = 0x0004; // Оригинальная маска проходимости тайла
        private const int MaxGridSize = 768;          // Фиксированные границы вашей карты

        // Поля управления фоновым потоком
        private Thread _workerThread;
        private bool _isRunning;
        private float _timer = 0f;

        // Потокобезопасный буфер обмена
        private readonly object _lockObject = new object();
        private bool _hasNewRequest = false;
        private bool _hasNewResult = false;

        // Данные текущего запроса
        private short _startX, _startY;
        private short _targetX, _targetY;
        private MapLayer _layerSnapshot;

        // Данные полученного ответа
        private bool _isPathFound;
        private int _pathLength;
        private readonly short[] _sharedPathBuffer = new short[1024 * 2]; // Внутренний буфер обмена
        private readonly short[] _mainThreadPathCopy = new short[1024 * 2]; // Буфер для главного потока

        public AsyncPathfindingTester()
        {
            // Автоматический запуск изолированного фонового рабочего потока
            _isRunning = true;
            _workerThread = new Thread(WorkerLoop)
            {
                Name = "PureAStar_TestWorker",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _workerThread.Start();
        }

        /// <summary>
        /// Главный тактовый метод обновления теста. Вызывается один раз в игровом цикле.
        /// </summary>
        public void Update(WorldMap worldMap, int currentViewZ, float deltaTime)
        {
            if (worldMap == null) return;

            // 1. АСИНХРОННЫЙ ТАЙМЕР НА 1 СЕКУНДУ
            _timer += deltaTime;
            if (_timer >= 1.0f)
            {
                _timer = 0f;
                MapLayer activeLayer = worldMap.GetLayer(currentViewZ);

                if (activeLayer != null)
                {
                    lock (_lockObject)
                    {
                        // Если предыдущий случайный маршрут уже посчитан потоком — генерируем новый
                        if (!_hasNewRequest)
                        {
                            Random rand = new Random();
                            short sX = 0, sY = 0;
                            short tX = 0, tY = 0;

                            // Ищем случайную свободную стартовую клетку внутри светлых коридоров фрактала
                            for (int i = 0; i < 100; i++)
                            {
                                short rx = (short)rand.Next(19, 748);
                                short ry = (short)rand.Next(19, 748);
                                if ((activeLayer.GetMicroCell(rx, ry).Flags & FLAG_WALKABLE) != 0)
                                {
                                    sX = rx;
                                    sY = ry;
                                    break;
                                }
                            }

                            // Ищем случайную свободную конечную клетку внутри светлых коридоров фрактала
                            for (int i = 0; i < 100; i++)
                            {
                                short rx = (short)rand.Next(19, 748);
                                short ry = (short)rand.Next(19, 748);
                                if ((activeLayer.GetMicroCell(rx, ry).Flags & FLAG_WALKABLE) != 0 && (rx != sX || ry != sY))
                                {
                                    tX = rx;
                                    tY = ry;
                                    break;
                                }
                            }

                            // Если обе случайные точки успешно попали на проходимый асфальт — пушим задачу
                            if (sX != 0 && tX != 0)
                            {
                                _startX = sX;
                                _startY = sY;
                                _targetX = tX;
                                _targetY = tY;
                                _layerSnapshot = activeLayer;
                                _hasNewRequest = true;
                            }
                        }
                    }
                }
            }

            // 2. БЕЗОПАСНОЕ ИЗВЛЕЧЕНИЕ РЕЗУЛЬТАТОВ ИЗ ПОТОКА В КОНСОЛЬ
            bool printLog = false;
            int currentLength = 0;
            short logSx = 0, logSy = 0, logTx = 0, logTy = 0;

            lock (_lockObject)
            {
                if (_hasNewResult)
                {
                    if (_isPathFound)
                    {
                        currentLength = _pathLength;
                        Array.Copy(_sharedPathBuffer, 0, _mainThreadPathCopy, 0, currentLength * 2);

                        logSx = _startX;
                        logSy = _startY;
                        logTx = _targetX;
                        logTy = _targetY;
                        printLog = true;
                    }
                    _hasNewResult = false;
                }
            }

            if (printLog)
            {
                Console.WriteLine($"[A* Test] Путь успешно найден асинхронно! Из ({logSx}, {logSy}) -> в ({logTx}, {logTy}) | Длина траектории: {currentLength} шагов.");
            }
        }

        /// <summary>
        /// Внутренний изолированный рабочий цикл фонового потока навигации.
        /// </summary>
        private void WorkerLoop()
        {
            // Локальный буфер потока, чтобы минимизировать время удержания lock во время работы A*
            short[] localOutputBuffer = new short[1024 * 2];

            while (_isRunning)
            {
                bool taskAvailable = false;
                short sx = 0, sy = 0, tx = 0, ty = 0;
                MapLayer executionLayer = null;

                // Быстро копируем параметры задачи под замком lock
                lock (_lockObject)
                {
                    if (_hasNewRequest)
                    {
                        sx = _startX;
                        sy = _startY;
                        tx = _targetX;
                        ty = _targetY;
                        executionLayer = _layerSnapshot;
                        _hasNewRequest = false;
                        taskAvailable = true;
                    }
                }

                if (taskAvailable && executionLayer != null)
                {
                    int totalSteps;

                    // Вызываем исправленный честный безопасный алгоритм A* в вакууме рабочего потока
                    bool success = PureAStarPathfinder.FindRoute(
                        executionLayer,
                        sx, sy,
                        tx, ty,
                        localOutputBuffer,
                        1024,
                        out totalSteps
                    );

                    // Возвращаем результаты вычислений в общую память
                    lock (_lockObject)
                    {
                        _isPathFound = success;
                        _pathLength = totalSteps;

                        if (success)
                        {
                            Array.Copy(localOutputBuffer, 0, _sharedPathBuffer, 0, totalSteps * 2);
                        }
                        _hasNewResult = true;
                    }
                }

                // Разгружаем ядро процессора, засыпая на 5 миллисекунд в ожидании следующего флага
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            _isRunning = false;
            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(500); // Ожидаем корректной выгрузки потока из памяти
            }
        }
    }
}
