using System;
using Core.Map;
using Core.Unit;
using System.Numerics;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public sealed class GameRenderer
{
    private const float TileSize = 16f;

    // Сохраняем прежний масштаб: старый MicroCell был
    // TileSize / SubDivision, то есть 16 / 3 пикселя.
    private const float TerrainTilePixelSize =
        TileSize / 3f;

    private readonly WorldMap _worldMap;

    private readonly GameCamera _camera;
    private readonly GameInput _input;
    private readonly MapRenderSystem _mapRenderer;
    private readonly UnitSimulation _unitSimulation;
    private readonly UnitRenderSystem _unitRenderer;

    private UnitId _selectedUnit;

    private RenderWindow _window = null!;

    private bool _showDebugGrid;

    public GameRenderer(
        WorldMap worldMap)
    {
        _worldMap =
            worldMap;

        _camera =
            new GameCamera(
                new Vector2f(
                    400f,
                    400f),
                new Vector2f(
                    1280f,
                    720f),
                450f);

        _input =
            new GameInput();

        _mapRenderer =
            new MapRenderSystem(
                TerrainTilePixelSize);

        _unitSimulation =
            new UnitSimulation();

        _unitRenderer =
            new UnitRenderSystem();

        CreateDemoUnits();
    }

    public void Run()
    {
        InitializeWindow();

        _camera.CenterOnWorld(
            TerrainTilePixelSize,
            _worldMap);

        Clock clock =
            new Clock();

        while (_window.IsOpen)
        {
            _window.DispatchEvents();

            float deltaTime =
                clock.Restart().AsSeconds();

            if (deltaTime > 0.1f)
                deltaTime = 0.1f;

            Update(deltaTime);
            Draw();
        }
    }

    private void Update(
        float deltaTime)
    {
        _input.Update();

        _camera.Update(
            _input.MoveDirection,
            deltaTime);

        _unitSimulation.Update(
            _worldMap,
            deltaTime);
    }

    private void Draw()
    {
        _window.Clear(
            new Color(
                7,
                8,
                9));

        _window.SetView(
            _camera.View);

        _mapRenderer.Draw(
            _window,
            _worldMap,
            _camera.View,
            TerrainTilePixelSize,
            _camera.ZoomLevel,
            _showDebugGrid);

        _unitRenderer.Draw(
            _window,
            _unitSimulation,
            _camera.View,
            TerrainTilePixelSize,
            _selectedUnit);

        _window.SetTitle(
            $"RimClone | " +
            $"{_worldMap.TileWidth}x" +
            $"{_worldMap.TileHeight} | " +
            $"Units={_unitSimulation.Units.ActiveCount}");

        _window.Display();
    }

    private void InitializeWindow()
    {
        _window =
            new RenderWindow(
                new VideoMode(
                    new Vector2u(
                        1280,
                        720)),
                "RimClone");

        _window.SetFramerateLimit(60);

        _window.Closed +=
            (_, _) =>
                _window.Close();

        _window.MouseWheelScrolled +=
            (_, e) =>
                _camera.HandleZoom(
                    e.Delta);

        _window.KeyPressed +=
            (_, e) =>
                HandleKey(e.Code);

    }

    private void HandleKey(
        Keyboard.Key key)
    {
        if (key == Keyboard.Key.G)
        {
            _showDebugGrid =
                !_showDebugGrid;
            return;
        }

        if (key == Keyboard.Key.T)
        {
            float centerX =
                _worldMap.TileWidth * 0.5f;

            float centerY =
                _worldMap.TileHeight * 0.5f;

            _unitSimulation.SetTarget(
                _selectedUnit,
                new Vector3(
                    centerX,
                    centerY - 20f,
                    0f));
        }
    }

    private void CreateDemoUnits()
    {
        const int StressUnitCount = 500;
        const int Columns = 25;
        const int Rows = 20;

        float margin =
            12f;

        float usableWidth =
            _worldMap.TileWidth -
            margin * 2f;

        float usableHeight =
            _worldMap.TileHeight -
            margin * 2f;

        UnitId firstUnit =
            new UnitId(
                -1,
                0);

        for (int i = 0;
             i < StressUnitCount;
             i++)
        {
            int column =
                i % Columns;

            int row =
                i / Columns;

            float x =
                margin +
                usableWidth *
                ((column + 0.5f) /
                 Columns);

            float y =
                margin +
                usableHeight *
                ((row + 0.5f) /
                 Rows);

            UnitId unit =
                _unitSimulation.Spawn(
                    UnitType.Colonist,
                    new Vector3(
                        x,
                        y,
                        0f),
                    bodyAngle:
                        0f);

            if (i == 0)
            {
                firstUnit = unit;
            }

            float targetX =
                Math.Clamp(
                    x + 24f,
                    margin,
                    _worldMap.MaxTileX - margin);

            float targetY =
                Math.Clamp(
                    y + 16f,
                    margin,
                    _worldMap.MaxTileY - margin);

            _unitSimulation.SetTarget(
                unit,
                new Vector3(
                    targetX,
                    targetY,
                    0f));
        }

        _selectedUnit =
            firstUnit;
    }


}

