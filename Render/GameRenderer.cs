using System;
using Core.Map;
using Core.Unit;
using RimClone.Render;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

namespace RimClone.Render;

public sealed class GameRenderer
{
    private const float TileSize = 16f;

    private const float TerrainTilePixelSize =
        TileSize / 3f;

    private readonly WorldMap _worldMap;

    private readonly GameCamera _camera;
    private readonly GameInput _input;
    private readonly MapRenderSystem _mapRenderer;
    private readonly UnitSimulation _unitSimulation;
    private readonly UnitRenderSystem _unitRenderer;
    private readonly VisionTestScene _visionTestScene;

    private UnitId _selectedUnit;

    private RenderWindow _window = null!;

    private bool _showDebugGrid;
    private bool _showVisionDebug = true;
    private int _visionTestIndex;

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

        _visionTestScene =
            new VisionTestScene();

        CreateVisionTestScene();
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

        if (_showVisionDebug)
        {
            _visionTestScene.DrawDebug(
                _window);
        }

        _unitRenderer.Draw(
            _window,
            _unitSimulation,
            _worldMap,
            _camera.View,
            TerrainTilePixelSize,
            _selectedUnit,
            _showVisionDebug,
            _showVisionDebug);

        _window.SetTitle(
            BuildWindowTitle());

        _window.Display();
    }

    private string BuildWindowTitle()
    {
        int visible = 0;
        int blocked = 0;
        int outsideFov = 0;
        int outOfRange = 0;

        if (_unitSimulation.Units.TryGetIndex(
                _selectedUnit,
                out int observerIndex))
        {
            ReadOnlySpan<UnitId> testUnits =
                _visionTestScene.Units;

            for (int i = 0;
                 i < testUnits.Length;
                 i++)
            {
                if (!_unitSimulation.Units.TryGetIndex(
                        testUnits[i],
                        out int targetIndex) ||
                    targetIndex == observerIndex)
                {
                    continue;
                }

                VisionState state =
                    _unitSimulation.Vision.Evaluate(
                        _unitSimulation.Units,
                        _worldMap,
                        observerIndex,
                        targetIndex).State;

                switch (state)
                {
                    case VisionState.Visible:
                        visible++;
                        break;

                    case VisionState.HiddenByTerrain:
                        blocked++;
                        break;

                    case VisionState.OutsideFieldOfView:
                        outsideFov++;
                        break;

                    case VisionState.OutOfRange:
                        outOfRange++;
                        break;
                }
            }
        }

        string debug =
            _showVisionDebug
                ? "ON"
                : "OFF";

        return
            $"RimClone | Units={_unitSimulation.Units.ActiveCount} | " +
            $"Vision {debug} | " +
            $"Visible={visible} Blocked={blocked} " +
            $"FOV={outsideFov} Range={outOfRange} | " +
            $"V=debug TAB=unit G=grid";
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

        if (key == Keyboard.Key.V)
        {
            _showVisionDebug =
                !_showVisionDebug;
            return;
        }

        if (key == Keyboard.Key.Tab)
        {
            _visionTestIndex =
                (_visionTestIndex + 1) %
                _visionTestScene.UnitCount;

            _selectedUnit =
                _visionTestScene.GetUnit(
                    _visionTestIndex);

            return;
        }

        if (key == Keyboard.Key.R)
        {
            _visionTestIndex = 0;
            _selectedUnit =
                _visionTestScene.Observer;
            return;
        }

        if (key == Keyboard.Key.T)
        {
            if (!_unitSimulation.Units.IsAlive(
                    _selectedUnit))
            {
                return;
            }

            float centerX =
                _worldMap.TileWidth * 0.5f;

            float centerY =
                _worldMap.TileHeight * 0.5f;

            _unitSimulation.SetTarget(
                _selectedUnit,
                new System.Numerics.Vector3(
                    centerX,
                    centerY - 20f,
                    0f));
        }
    }

    private void CreateVisionTestScene()
    {
        _visionTestScene.Setup(
            _worldMap,
            _unitSimulation,
            TerrainTilePixelSize);

        _selectedUnit =
            _visionTestScene.Observer;
    }
}
