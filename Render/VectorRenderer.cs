using Core.AI;
using Core.Input.Systems;
using Core.Items;
using Core.Map;
using Core.Structs;
using Core.Unit;
using Core.Unit.Components;
using Core.Unit.Components.AiComponents;
using Core.Unit.Systems;
using Render;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using System;

namespace RimClone.Render;

public sealed class VectorRenderer
{
    private int _gameFrame;
    private const int TileSize = 16;

    private static readonly float MicroCellPixelSize = (float)TileSize / MapRegion.SubDivision;
    private static readonly float RegionPixelSize = MapRegion.MicroSize * MicroCellPixelSize;

    private readonly MouseInputSystem _mouseInputSystem = new();

    private WorldSimulation _simulation;
    private PathfindingSystem _pathfindingSystem;
    private UnitMovementSystem _movementSystem;
    private UnitCpuBrainSystem _cpuBrainSystem;

    private MapRenderSystem _mapRenderSystem;
    private UnitRenderSystem _unitRenderSystem;

    private RenderWindow _window;
    private WorldMap _worldMap;
    private GameCamera _gameCamera;
    private UnitStore _unitStore;
    private UnitSpatialGrid _spatialGrid;
    private EdificeStore _edificeStore;

    private bool _showDebugGrid;

    public void InitializeAndRun(WorldMap worldMap, UnitStore unitStore, UnitSpatialGrid spatialGrid, EdificeStore edificeStore)
    {
        _worldMap = worldMap;
        _unitStore = unitStore;
        _spatialGrid = spatialGrid;
        _edificeStore = edificeStore;

        InitializeSystems();
        InitializeWindow();

        _mouseInputSystem.MoveCommandRequested += IssueMoveCommands;

        Run();
    }

    private void InitializeSystems()
    {
        _pathfindingSystem = new PathfindingSystem();
        _movementSystem = new UnitMovementSystem();

        _movementSystem.MoveInterrupted += OnMoveInterrupted;

        MoveTask moveTask = new MoveTask(
            _pathfindingSystem,
            _movementSystem);

        _cpuBrainSystem = new UnitCpuBrainSystem(moveTask);
        _simulation = new WorldSimulation(_movementSystem);

        _mapRenderSystem = new MapRenderSystem(MicroCellPixelSize);
        _unitRenderSystem = new UnitRenderSystem();
    }
    private void OnMoveInterrupted(int unitId)
    {
        Console.WriteLine(
            $"[MOVEMENT] INTERRUPTED unit={unitId}");
    }
    private void IssueMoveCommands(IReadOnlyList<int> unitIds, IReadOnlyList<SpatialCoord> points)
    {
        int count = Math.Min(unitIds.Count, points.Count);
        //Console.WriteLine($"[VECTOR] MOVE COMMANDS units={unitIds.Count} points={points.Count}");

        for (int i = 0; i < count; i++)
        {
            SpatialCoord target = points[i];
            //Console.WriteLine(
            //$"[VECTOR] UNIT {unitIds[i]} -> ({target.X},{target.Y},{target.Z})");

            _cpuBrainSystem.IssueCommand(
                _unitStore,
                unitIds[i],
                new AiCommand
                {
                    OpCode = AiOpCode.MoveToTarget,
                    TargetX = target.X,
                    TargetY = target.Y,
                    TargetZ = target.Z
                },
                AiCommandInsertMode.ReplaceAll);
        }
    }

    private void InitializeWindow()
    {
        _window = new RenderWindow(
            new VideoMode(new Vector2u(1280, 720)),
            "RimClone");

        _window.SetFramerateLimit(60);

        _gameCamera = new GameCamera(
            new Vector2f(400, 400),
            new Vector2f(1280, 720),
            moveSpeed: 450f);

        _gameCamera.CenterOnStreet(MicroCellPixelSize);

        _window.Closed += (_, _) => _window.Close();
        _window.MouseWheelScrolled += (_, e) => _gameCamera.HandleZoom(e.Delta);
        _window.KeyPressed += (_, e) => HandleKey(e.Code);
    }

    private void Run()
    {
        float radius = 0.4f * MicroCellPixelSize;

        CircleShape unitShape = new CircleShape(radius)
        {
            Origin = new Vector2f(radius, radius)
        };

        Clock clock = new();

        while (_window.IsOpen)
        {
            _window.DispatchEvents();

            float deltaTime = clock.Restart().AsSeconds();

            if (deltaTime > 0.1f)
                deltaTime = 0.1f;

            Update(deltaTime);
            Draw(unitShape);
        }
    }

    private void Update(float deltaTime)
    {
        _gameFrame++;

        _pathfindingSystem.UpdateFrame(_gameFrame);

        _gameCamera.UpdateInput(deltaTime);

        Vector2i mousePixels = Mouse.GetPosition(_window);
        Vector2f mouseWorld =
            _window.MapPixelToCoords(
                mousePixels,
                _gameCamera.View);

        _mouseInputSystem.Update(
            _window,
            _unitStore,
            _worldMap.CurrentViewZ,
            MicroCellPixelSize,
            mouseWorld);

        _simulation.Update(
            _unitStore,
            _spatialGrid,
            _worldMap,
            _edificeStore,
            MicroCellPixelSize,
            deltaTime);

        _cpuBrainSystem.Update(
            _unitStore,
            _spatialGrid,
            _worldMap,
            _edificeStore,
            _simulation.EffectSystem,
            MicroCellPixelSize,
            deltaTime);

        UpdateTileInspector(mouseWorld);
    }

    private void UpdateTileInspector(Vector2f mouseWorld)
    {
        if (!Mouse.IsButtonPressed(Mouse.Button.Left))
            return;

        int x = (int)(mouseWorld.X / MicroCellPixelSize);
        int y = (int)(mouseWorld.Y / MicroCellPixelSize);
        int max = MapRegion.MicroSize * 16 - 1;

        if (x < 0 || y < 0 || x > max || y > max)
            return;

        MapLayer layer = _worldMap.GetLayer(_worldMap.CurrentViewZ);

        if (layer == null)
            return;

        ref var cell = ref layer.GetMicroCell(x, y);

        string info = TileMetadataSystem.InspectMicroCell(
            ref cell,
            x,
            y,
            _edificeStore);

        Console.Clear();
        Console.WriteLine(info);
    }

    private void Draw(CircleShape unitShape)
    {
        _window.Clear(new Color(20, 20, 25));
        _window.SetView(_gameCamera.View);

        _mapRenderSystem.Draw(
            _window,
            _worldMap,
            _edificeStore,
            _gameCamera.View,
            RegionPixelSize,
            MicroCellPixelSize,
            _gameCamera.ZoomLevel,
            _showDebugGrid);

        int aliveCount = _unitRenderSystem.Draw(
            _window,
            _unitStore,
            unitShape,
            _worldMap.CurrentViewZ);

        DrawSelection();

        _simulation.EffectSystem.Draw(
            _window,
            _worldMap.CurrentViewZ);

        _mouseInputSystem.DrawSelectionBox(_window);

        _window.SetTitle(
            $"RimClone | Units: {aliveCount} | Z: {_worldMap.CurrentViewZ}");

        _window.Display();
    }

    private void DrawSelection()
    {
        for (int i = 0; i < _unitStore.Count; i++)
        {
            if (_unitStore.HealthMasks[i] == 0)
                continue;

            if (_unitStore.Positions[i].Spatial.Z != _worldMap.CurrentViewZ)
                continue;

            if (!_mouseInputSystem.SelectedUnitIds.Contains(i))
                continue;

            float radius = 0.4f * MicroCellPixelSize + 2f;

            CircleShape ring = new CircleShape(radius)
            {
                Origin = new Vector2f(radius, radius),
                Position = new Vector2f(
                    _unitStore.Positions[i].RenderX * MicroCellPixelSize + MicroCellPixelSize * 0.5f,
                    _unitStore.Positions[i].RenderY * MicroCellPixelSize + MicroCellPixelSize * 0.5f),
                FillColor = Color.Transparent,
                OutlineColor = new Color(0, 255, 130, 220),
                OutlineThickness = 1f
            };

            _window.Draw(ring);
        }
    }

    private void HandleKey(Keyboard.Key key)
    {
        if (key == Keyboard.Key.PageUp)
        {
            _worldMap.ChangeViewFloor(1);
            return;
        }

        if (key == Keyboard.Key.PageDown)
        {
            _worldMap.ChangeViewFloor(-1);
            return;
        }

        if (key == Keyboard.Key.G)
        {
            _showDebugGrid = !_showDebugGrid;
            Console.WriteLine(_showDebugGrid ? "Micro grid ON" : "Micro grid OFF");
        }
    }
}