using Core.Input.Systems;
using Core.Items;
using Core.Map;
using Core.Unit;
using Core.Unit.Components;
using Render;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;

namespace RimClone.Render
{
    public class VectorRenderer
    {
        private bool _showDebugGrid = false; // По умолчанию отладочная сетка выключена
        private readonly WorldSimulation _simulation = new WorldSimulation();
        private MapRenderSystem _mapRenderSystem;
        private UnitRenderSystem _unitRenderSystem;

        private RenderWindow _window;
        private WorldMap _worldMap;
        private GameCamera _gameCamera;
        private UnitStore _unitStore;
       
        private UnitSpatialGrid _spatialGrid;
        private EdificeStore _edificeStore;
        private const int TileSize = 16;
        private static readonly float MicroCellPixelSize = (float)TileSize / MapRegion.SubDivision;
        private static readonly float RegionPixelSize = MapRegion.MicroSize * MicroCellPixelSize;

        // В полях класса VectorRenderer:
        private readonly Core.Unit.Systems.UnitCpuBrainSystem _cpuBrainSystem = new Core.Unit.Systems.UnitCpuBrainSystem(); // <-- ДОБАВИЛИ СЮДА!

        private readonly MouseInputSystem _mouseInputSystem = new MouseInputSystem();

        //Автоматический менеджер тактического деления на огневые группы (Fireteams)
   
        public void InitializeAndRun(WorldMap worldMap, UnitStore unitStore,  UnitSpatialGrid spatialGrid, EdificeStore edificeStore)
        {
            _worldMap = worldMap;
            _unitStore = unitStore;
            
            _spatialGrid = spatialGrid;

            // ИСПРАВЛЕНО: Присваиваем тот самый store из генератора, где лежат наши кибитки и генераторы!
            // Старую строчку пересоздания с занулением ("new EdificeStore") УДАЛИЛИ!
            _edificeStore = edificeStore;

            // Инициализируем логические системы
            var unitMovementSystem = new UnitMovementSystem();
            
            _simulation.Initialize(unitMovementSystem);

            // Инициализируем графические системы
            _mapRenderSystem = new MapRenderSystem(MicroCellPixelSize);
            _unitRenderSystem = new UnitRenderSystem();

            // Создание окна
            var videoMode = new VideoMode(new Vector2u(1280, 720));
            _window = new RenderWindow(videoMode, "RimClone - Decoupled Micro-World Edition");
            _window.SetFramerateLimit(60);

            _gameCamera = new GameCamera(new Vector2f(400, 400), new Vector2f(1280, 720), moveSpeed: 450f);

            // ИСПРАВЛЕНО: Автоматически центрируем камеру ровно на вертикальный проспект, чтобы он не улетал влево!
            _gameCamera.CenterOnStreet(MicroCellPixelSize);

            // Регистрация ввода
            _window.Closed += (sender, e) => _window.Close();
            _window.MouseWheelScrolled += (sender, e) => _gameCamera.HandleZoom(e.Delta);


            // ИСПРАВЛЕНО: Перенесли переключение сетки на клавишу G прямо в твой блок KeyPressed!
            _window.KeyPressed += (sender, e) =>
            {
                if (e.Code == Keyboard.Key.PageUp) _worldMap.ChangeViewFloor(1);
                if (e.Code == Keyboard.Key.PageDown) _worldMap.ChangeViewFloor(-1);

                // Триггер отладочной сетки на букву G
                if (e.Code == Keyboard.Key.G)
                {
                    _showDebugGrid = !_showDebugGrid;
                    Console.WriteLine(_showDebugGrid
                        ? "🔲 Отладочная сетка микро-ячеек ВКЛЮЧЕНА (Режим разработчика)"
                        : "🟩 Отладочная сетка ВЫКЛЮЧЕНА (Кинематографичный режим)");
                }
            };

            // Кружочек для муравьев
            float renderRadius = 0.4f * MicroCellPixelSize;
            CircleShape antShape = new CircleShape(renderRadius);
            antShape.Origin = new Vector2f(renderRadius, renderRadius);

            Clock deltaClock = new Clock();
            // ========================================================
            // ГЛАВНЫЙ ИГРОВОЙ ЦИКЛ ПРИЛОЖЕНИЯ
            // ========================================================

            // ========================================================
            // ГЛАВНЫЙ ИГРОВОЙ ЦИКЛ ПРИЛОЖЕНИЯ (ВОССТАНОВЛЕНИЕ СИМУЛЯЦИИ)
            while (_window.IsOpen)
            {
                _window.DispatchEvents();
                float deltaTime = deltaClock.Restart().AsSeconds();

                if (deltaTime > 0.1f) deltaTime = 0.1f;

                // ----------------========================================
                // ШАГ 1. ПОЛНАЯ СИМУЛЯЦИЯ ЛОГИКИ МИРА (СТРОГО ДО ОТРИСОВКИ!)
                // ----------------================================--------
                Vector2i debugMousePos = Mouse.GetPosition(_window);
                Vector2f debugWorldPos = _window.MapPixelToCoords(debugMousePos, _gameCamera.View);

                // А. Считываем рамку и тактические иерархические приказы игрока (ПКМ/ЛКМ)
                // Передаем _squadStore третьим аргументом, чтобы мышка видела под-отряды!
                _mouseInputSystem.Update(_window, _unitStore, _worldMap.CurrentViewZ, MicroCellPixelSize, deltaTime);



                // Б. Обновляем плавный физический скролл камеры
                _gameCamera.UpdateInput(deltaTime);

                // В. ТАКТИЧЕСКИЙ КЛИК-ИНСПЕКТОР ТАЙЛОВ КАРТЫ
                if (Mouse.IsButtonPressed(Mouse.Button.Left))
                {
                    int clickMx = (int)(debugWorldPos.X / MicroCellPixelSize);
                    int clickMy = (int)(debugWorldPos.Y / MicroCellPixelSize);
                    int maxCoord = (16 * 48) - 1;

                    if (clickMx >= 0 && clickMx <= maxCoord && clickMy >= 0 && clickMy <= maxCoord)
                    {
                        int currentZ = _worldMap.CurrentViewZ;
                        var currentLayer = _worldMap.GetLayer(currentZ);

                        if (currentLayer != null)
                        {
                            ref var targetCell = ref currentLayer.GetMicroCell(clickMx, clickMy);
                            string tileInfo = Core.Map.TileMetadataSystem.InspectMicroCell(ref targetCell, clickMx, clickMy, _edificeStore);
                            Console.Clear();
                            Console.WriteLine(tileInfo);
                        }
                    }
                }

                // Г. ТВОЯ РОДНАЯ СИМУЛЯЦИЯ ДВИЖЕНИЯ (МУРАВЕЙНИК)
                // Она монопольно плавно двигает RenderX/Y, инкрементирует Progress 
                // и вызывает поклеточный TryStartMove для сквадов и их дочерних групп!
                _simulation.Update(
                    _unitStore,
                  
                    _spatialGrid,
                    _worldMap,
                    _edificeStore,
                    MicroCellPixelSize,
                    deltaTime
                 
                );

           
                // Ж. ТАКТ 4 и 5: Запуск ИИ-процессоров ЦП одиночных пешек (Переключение опкодов, таймеры зажима автомата)
                _cpuBrainSystem.Update(
                    _unitStore,
                  
                    _spatialGrid,
                    _worldMap,
                    _edificeStore,
                    _simulation.EffectSystem,
                    MicroCellPixelSize,
                    deltaTime
                );


                // ----------------========================================
                // ШАГ 2. ГРАФИЧЕСКИЙ КОНВЕЙЕР И ОТРИСОВКА СЦЕНЫ
                // ----------------=======================================
                _window.Clear(new Color(20, 20, 25));
                _window.SetView(_gameCamera.View);

                // 3. ОТРИСОВКА КАРТЫ ВЕРТЕКСНЫМ МАССИВОМ
                _mapRenderSystem.Draw(_window, _worldMap, _edificeStore, _gameCamera.View, RegionPixelSize, MicroCellPixelSize, _gameCamera.ZoomLevel, _showDebugGrid);

                // 4. ОТРИСОВКА ЖИВЫХ СУЩЕСТВ ПОВЕРХ КАРТЫ
                int aliveCount = _unitRenderSystem.Draw(_window, _unitStore, antShape, _worldMap.CurrentViewZ);

                // 4.1 РЕНДЕР МЯТНЫХ КОЛЕЦ ВЫДЕЛЕНИЯ РАМКИ МЫШИ
                for (int i = 0; i < _unitStore.Count; i++)
                {
                    if (_unitStore.HealthMasks[i] > 0 && _unitStore.Positions[i].Spatial.Z == _worldMap.CurrentViewZ)
                    {
                        if (_mouseInputSystem.SelectedUnitIds.Contains(i))
                        {
                            float ringRadius = (0.4f * MicroCellPixelSize) + 2f;
                            CircleShape selectRing = new CircleShape(ringRadius);
                            selectRing.Origin = new Vector2f(ringRadius, ringRadius);
                            selectRing.Position = new Vector2f(
                                _unitStore.Positions[i].RenderX * MicroCellPixelSize + MicroCellPixelSize * 0.5f,
                                _unitStore.Positions[i].RenderY * MicroCellPixelSize + MicroCellPixelSize * 0.5f
                            );

                            selectRing.FillColor = Color.Transparent;
                            selectRing.OutlineColor = new Color(0, 255, 130, 220);
                            selectRing.OutlineThickness = 1f;
                            _window.Draw(selectRing);
                        }
                    }
                }

                // 5. ОТРИСОВКА ВИЗУАЛЬНЫХ ЭФФЕКТОВ БОЯ
                _simulation.EffectSystem.Draw(_window, _worldMap.CurrentViewZ);

                // Е. Отрисовываем синюю рамку зажима мыши поверх всего экрана
                _mouseInputSystem.DrawSelectionBox(_window);

                _window.SetTitle($"RimClone | Микро-существ: {aliveCount} | Этаж камеры: {_worldMap.CurrentViewZ}");
                _window.Display();
            }


        }
    }
}
