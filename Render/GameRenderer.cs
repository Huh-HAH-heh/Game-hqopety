using System;
using System.Diagnostics;
using Core.Items;
using Core.Map;
using Core.Unit;
using RimClone.Render;
using SFML.Graphics;
using SFML.System;
using SFML.Window;
using World;

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
    private readonly ProjectileRenderSystem _projectileRenderer;
    private readonly VisionTestScene _visionTestScene;
    private readonly LongRangeCombatTestScene _massCombatTestScene;

    private UnitId _selectedUnit;

    private RenderWindow _window = null!;
    private View? _uiView;
    private GameMenuOverlay? _gameMenuOverlay;
    private GameMenuActionType? _draggingMenuSlider;
    private GameMenuPage _menuPage;
    private int _selectedResolutionIndex;
    private bool _showTerrainExtrema;
    private bool _closeRequested;

    private bool _showDebugGrid;
    private int _visibleMaxLayer;
    private bool _showVisionDebug;
    private bool _massCombatMode;
    private bool _terrainStressMode = true;
    private int _visionTestIndex;

    private float _perfTimer;
    private int _perfFrames;
    private float _fps;
    private long _workingSetBytes;
    private long _managedHeapBytes;
    private long _allocatedBytes;
    private long _lastAllocatedBytesSample;
    private double _allocatedBytesPerSecond;
    private float _titleTimer;
    private string _windowTitle = "RimClone";

    public GameRenderer(
        WorldMap worldMap)
    {
        _worldMap =
            worldMap;

        _visibleMaxLayer =
            Math.Max(0, worldMap.LayerCount - 1);

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

        _projectileRenderer =
            new ProjectileRenderSystem();

        _visionTestScene =
            new VisionTestScene();

        _massCombatTestScene =
            new LongRangeCombatTestScene();

        // Start in a terrain-only stress test. The 160-unit combat test
        // is still available via C, but must not contaminate idle render
        // profiling with periodic volleys and projectile simulation.
        _unitSimulation.AI.Enabled = false;
        _unitSimulation.VisionEnabled = false;
        _showVisionDebug = false;
        _lastAllocatedBytesSample =
            GC.GetTotalAllocatedBytes(false);
    }

    public void Run()
    {
        InitializeWindow();

        _camera.CenterOnWorld(
            TerrainTilePixelSize,
            _worldMap);

        Clock clock =
            new Clock();

        while (_window.IsOpen && !_closeRequested)
        {
            _window.DispatchEvents();

            if (_closeRequested)
                break;

            float deltaTime =
                clock.Restart().AsSeconds();

            if (deltaTime > 0.1f)
                deltaTime = 0.1f;

            Update(deltaTime);
            Draw();
        }

        // Release GPU resources while the graphics context still exists.
        _mapRenderer.Dispose();
        _gameMenuOverlay?.Dispose();
        _uiView?.Dispose();
        _window.Close();
    }

    private void Update(
        float deltaTime)
    {
        _input.Update();

        _perfTimer += deltaTime;
        _perfFrames++;
        _titleTimer += MathF.Max(0f, deltaTime);

        if (_perfTimer >= 1f)
        {
            float sampleSeconds = _perfTimer;

            _fps =
                _perfFrames /
                sampleSeconds;

            _perfFrames = 0;
            _perfTimer = 0f;

            using Process process =
                Process.GetCurrentProcess();

            _workingSetBytes =
                process.WorkingSet64;

            _managedHeapBytes =
                GC.GetTotalMemory(false);

            _allocatedBytes =
                GC.GetTotalAllocatedBytes(false);

            _allocatedBytesPerSecond =
                Math.Max(
                    0L,
                    _allocatedBytes - _lastAllocatedBytesSample) /
                sampleSeconds;

            _lastAllocatedBytesSample =
                _allocatedBytes;

            Console.WriteLine(
                $"[PERF] FPS={_fps:0.0} " +
                $"RAM={_workingSetBytes / 1024d / 1024d:0.0}MB " +
                $"Heap={_managedHeapBytes / 1024d / 1024d:0.0}MB " +
                $"Alloc/s={_allocatedBytesPerSecond / 1024d / 1024d:0.00}MB/s " +
                $"TerrainQ={_mapRenderer.TerrainQuadCount:N0} " +
                $"Chunks={_mapRenderer.TerrainChunkCacheCount} " +
                $"LayerShader={_mapRenderer.UsesTerrainLayerShader} " +
                $"VisibleV={_mapRenderer.TerrainVertexCount:N0} " +
                $"CacheV={_mapRenderer.TerrainCachedVertexCount:N0} " +
                $"ScratchV={_mapRenderer.TerrainScratchCapacity:N0} " +
                $"VBO={_mapRenderer.UsesTerrainVertexBuffer} " +
                $"LastChunkBuild={_mapRenderer.LastTerrainBuildMilliseconds:0.0}ms " +
                $"ChunkBuilds={_mapRenderer.TerrainMeshRebuildCount} " +
                $"Projectiles={_unitSimulation.Projectiles.ActiveCount}/{_unitSimulation.Projectiles.Capacity}");
        }

        if (_menuPage == GameMenuPage.Closed)
        {
            _camera.Update(
                _input.MoveDirection,
                deltaTime);

            if (!_terrainStressMode && !_massCombatMode)
            {
                _visionTestScene.UpdateAiDemo(
                    _unitSimulation,
                    _worldMap,
                    deltaTime);
            }

            if (_massCombatMode)
            {
                _massCombatTestScene.Update(
                    _unitSimulation,
                    _worldMap,
                    deltaTime);
            }

            if (!_terrainStressMode)
            {
                _unitSimulation.Update(
                    _worldMap,
                    deltaTime);
            }
        }
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
            _showDebugGrid,
            _visibleMaxLayer);

        if (_showVisionDebug &&
            !_massCombatMode &&
            !_terrainStressMode)
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
            _showVisionDebug && !_massCombatMode,
            !_massCombatMode,
            false);

        _projectileRenderer.Draw(
            _window,
            _unitSimulation.Projectiles,
            TerrainTilePixelSize);

        _gameMenuOverlay?.DrawWorldMarkers(
            _window,
            _worldMap,
            TerrainTilePixelSize,
            _camera.ZoomLevel,
            _showTerrainExtrema);

        if (_uiView != null)
        {
            _window.SetView(_uiView);

            Vector2i mousePixels = Mouse.GetPosition(_window);
            Vector2f uiMouseCoordinates =
                _window.MapPixelToCoords(mousePixels, _uiView);

            _gameMenuOverlay?.Draw(
                _window,
                _window.Size.X,
                _window.Size.Y,
                _menuPage,
                GetGameSettingsSnapshot(),
                new Vector2i(
                    (int)uiMouseCoordinates.X,
                    (int)uiMouseCoordinates.Y));
        }

        if (_titleTimer >= 0.25f)
        {
            _titleTimer = 0f;
            _windowTitle = BuildWindowTitle();
            _window.SetTitle(_windowTitle);
        }

        _window.Display();
    }

    private string BuildWindowTitle()
    {
        int visible = 0;
        int blocked = 0;
        int outsideFov = 0;
        int outOfRange = 0;
        int projectiles =
            _unitSimulation.Projectiles.ActiveCount;

        long hits =
            _unitSimulation.Projectiles.TotalHits;

        int teamOneAlive =
            _massCombatMode
                ? _massCombatTestScene.AliveBlue
                : _visionTestScene.GetAliveCount(
                    _unitSimulation,
                    1);

        int teamTwoAlive =
            _massCombatMode
                ? _massCombatTestScene.AliveRed
                : _visionTestScene.GetAliveCount(
                    _unitSimulation,
                    2);

        int idle = 0;
        int attack = 0;
        int cover = 0;
        int search = 0;
        int dead = 0;
        int suppressed = 0;
        int panicked = 0;

        ReadOnlySpan<int> aiUnits =
            _unitSimulation.Units.ActiveIndices;

        for (int i = 0; i < aiUnits.Length; i++)
        {
            switch (_unitSimulation.AI.Store.State[aiUnits[i]])
            {
                case UnitAiState.Attack: attack++; break;
                case UnitAiState.SeekCover: cover++; break;
                case UnitAiState.Search: search++; break;
                case UnitAiState.Dead: dead++; break;
                default: idle++; break;
            }
        }

        ReadOnlySpan<int> suppressionUnits =
            _unitSimulation.Units.ActiveIndices;

        for (int i = 0; i < suppressionUnits.Length; i++)
        {
            int unit = suppressionUnits[i];

            switch (_unitSimulation.Suppression.GetState(unit))
            {
                case UnitSuppressionState.Suppressed:
                    suppressed++;
                    break;

                case UnitSuppressionState.Panicked:
                    panicked++;
                    break;
            }
        }

        if (!_massCombatMode &&
            _unitSimulation.Units.TryGetIndex(
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

        string ai =
            _unitSimulation.AI.Enabled
                ? "ON"
                : "OFF";

        string aiState =
            _unitSimulation.Units.TryGetIndex(
                _selectedUnit,
                out int selectedIndex)
                ? _unitSimulation.AI.Store.State[selectedIndex].ToString()
                : "None";

        string demoStage =
            _terrainStressMode
                ? "TERRAIN STRESS"
                : _massCombatMode
                    ? "MASS"
                    : _visionTestScene.GetAiDemoStage();

        string massCombat =
            _massCombatMode
                ? _massCombatTestScene.GetStatus(
                    _unitSimulation)
                : "MASS OFF";

        return
            $"RimClone | FPS={_fps:0.0} RAM={_workingSetBytes / 1024d / 1024d:0}MB " +
            $"Heap={_managedHeapBytes / 1024d / 1024d:0}MB " +
            $"Alloc/s={_allocatedBytesPerSecond / 1024d / 1024d:0.0}MB/s TotalAlloc={_allocatedBytes / 1024d / 1024d:0}MB | " +
            $"Terrain={_mapRenderer.TerrainQuadCount:N0} quads Chunks={_mapRenderer.TerrainChunkCacheCount} LayerShader={_mapRenderer.UsesTerrainLayerShader} VisibleV={_mapRenderer.TerrainVertexCount:N0} CacheV={_mapRenderer.TerrainCachedVertexCount:N0} ScratchV={_mapRenderer.TerrainScratchCapacity:N0} VBO={_mapRenderer.UsesTerrainVertexBuffer} LastChunkBuild={_mapRenderer.LastTerrainBuildMilliseconds:0.0}ms ChunkBuilds={_mapRenderer.TerrainMeshRebuildCount} | " +
            $"Units={_unitSimulation.Units.ActiveCount} | " +
            $"Projectiles={projectiles}/{_unitSimulation.Projectiles.Capacity} Hits={hits} | " +
            $"Teams 1:{teamOneAlive} 2:{teamTwoAlive} | " +
            $"AI={ai}:{aiState} | " +
            $"States I={idle} A={attack} C={cover} S={search} D={dead} | " +
            $"Suppression S={suppressed} P={panicked} | " +
            $"Demo={demoStage} LastHit={_unitSimulation.Projectiles.LastHitPart} | " +
            $"{massCombat} | " +
            $"Vision {debug} | " +
            $"Visible={visible} Blocked={blocked} " +
            $"FOV={outsideFov} Range={outOfRange} | " +
            $"Z={_visibleMaxLayer + 1}/{_worldMap.LayerCount} PgUp/PgDn=layer Shift+Wheel=layer F2=settings •••=menu | " +
            $"A=AI B=ballistic F=direct M=fire N=aim K=target L=ammo C=MASS Y=reset TAB=unit V=vision";
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
        _uiView = CreateUiView(_window.Size);
        _gameMenuOverlay = new GameMenuOverlay();
        _selectedResolutionIndex = 1;

        _window.Closed +=
            (_, _) =>
                _closeRequested = true;

        _window.Resized +=
            (_, e) =>
                UpdateWindowViews(e.Size);

        _window.MouseWheelScrolled +=
            (_, e) =>
            {
                if (_menuPage != GameMenuPage.Closed)
                    return;

                bool shiftPressed =
                    Keyboard.IsKeyPressed(Keyboard.Key.LShift) ||
                    Keyboard.IsKeyPressed(Keyboard.Key.RShift);

                if (shiftPressed)
                {
                    ScrollTerrainLayers(
                        e.Delta > 0f ? 1 : -1);
                    return;
                }

                _camera.HandleZoom(e.Delta);
            };

        _window.MouseButtonPressed +=
            (_, e) =>
            {
                if (e.Button != Mouse.Button.Left ||
                    _gameMenuOverlay == null)
                {
                    return;
                }

                Vector2f uiCoordinates =
                    _uiView != null
                        ? _window.MapPixelToCoords(e.Position, _uiView)
                        : new Vector2f(e.Position.X, e.Position.Y);

                Vector2i uiPosition = new Vector2i(
                    (int)uiCoordinates.X,
                    (int)uiCoordinates.Y);

                if (_gameMenuOverlay.IsSliderHit(
                        _menuPage,
                        uiPosition,
                        out GameMenuActionType sliderType))
                {
                    _draggingMenuSlider = sliderType;
                    ApplyGameMenuAction(
                        _gameMenuOverlay.GetSliderAction(
                            sliderType,
                            uiPosition.X,
                            _worldMap.LayerCount));
                    return;
                }

                _draggingMenuSlider = null;

                GameMenuAction? action =
                    _gameMenuOverlay.HandleClick(
                        uiPosition,
                        _menuPage,
                        _worldMap.LayerCount);

                if (action.HasValue)
                    ApplyGameMenuAction(action.Value);
            };

        _window.MouseMoved +=
            (_, e) =>
            {
                if (!_draggingMenuSlider.HasValue ||
                    _menuPage != GameMenuPage.Settings ||
                    _gameMenuOverlay == null)
                {
                    return;
                }

                Vector2f uiCoordinates =
                    _uiView != null
                        ? _window.MapPixelToCoords(e.Position, _uiView)
                        : new Vector2f(e.Position.X, e.Position.Y);

                ApplyGameMenuAction(
                    _gameMenuOverlay.GetSliderAction(
                        _draggingMenuSlider.Value,
                        (int)uiCoordinates.X,
                        _worldMap.LayerCount));
            };

        _window.MouseButtonReleased +=
            (_, e) =>
            {
                if (e.Button == Mouse.Button.Left)
                    _draggingMenuSlider = null;
            };

        _window.KeyPressed +=
            (_, e) =>
                HandleKey(e.Code);
    }

    private void HandleKey(
        Keyboard.Key key)
    {
        if (key == Keyboard.Key.PageUp)
        {
            ScrollTerrainLayers(1);
            return;
        }

        if (key == Keyboard.Key.PageDown)
        {
            ScrollTerrainLayers(-1);
            return;
        }

        if (key == Keyboard.Key.G)
        {
            _showDebugGrid =
                !_showDebugGrid;
            return;
        }

        if (key == Keyboard.Key.F2)
        {
            _menuPage = _menuPage == GameMenuPage.Settings
                ? GameMenuPage.Closed
                : GameMenuPage.Settings;
            _draggingMenuSlider = null;
            return;
        }

        if (key == Keyboard.Key.Escape)
        {
            _menuPage = _menuPage switch
            {
                GameMenuPage.Settings => GameMenuPage.Main,
                GameMenuPage.Main => GameMenuPage.Closed,
                _ => GameMenuPage.Closed
            };
            _draggingMenuSlider = null;
            return;
        }

        if (key == Keyboard.Key.A)
        {
            _unitSimulation.AI.Enabled =
                !_unitSimulation.AI.Enabled;
            return;
        }

        if (key == Keyboard.Key.C)
        {
            ToggleTerrainCombatTest();
            return;
        }


        if (key == Keyboard.Key.Y)
        {
            if (_terrainStressMode || _massCombatMode)
                return;

            _visionTestScene.ResetAiDemo(
                _unitSimulation,
                _worldMap);
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
            if (_massCombatMode)
            {
                _selectedUnit =
                    _massCombatTestScene.FirstUnit;

                return;
            }

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

        if (key == Keyboard.Key.F)
        {
            if (!_unitSimulation.Units.TryGetIndex(
                    _selectedUnit,
                    out int shooterIndex))
            {
                return;
            }

            _unitSimulation.FireWeapon(
                _selectedUnit,
                UnitWeaponSlot.Primary,
                _unitSimulation.Units.HeadNormal[
                    shooterIndex]);
            return;
        }

        if (key == Keyboard.Key.B)
        {
            if (!_unitSimulation.Units.TryGetIndex(
                    _selectedUnit,
                    out int shooterIndex) ||
                !_unitSimulation.AI.Store.HasTarget[shooterIndex])
            {
                return;
            }

            UnitId target =
                _unitSimulation.AI.Store.Target[shooterIndex];

            if (_unitSimulation.Units.IsAlive(target))
            {
                _unitSimulation.FireWeaponAt(
                    _selectedUnit,
                    UnitWeaponSlot.Primary,
                    target);
            }

            return;
        }

        if (key == Keyboard.Key.H)
        {
            _unitSimulation.ApplyDamage(
                _selectedUnit,
                UnitHealthPartId.Torso,
                70f);
            return;
        }

        if (key == Keyboard.Key.M)
        {
            if (_unitSimulation.Units.TryGetIndex(
                    _selectedUnit,
                    out int unitIndex))
            {
                _unitSimulation.Weapons.CycleFireMode(
                    unitIndex,
                    UnitWeaponSlot.Primary);
            }

            return;
        }

        if (key == Keyboard.Key.N)
        {
            if (_unitSimulation.Units.TryGetIndex(
                    _selectedUnit,
                    out int unitIndex))
            {
                _unitSimulation.Weapons.CycleAimMode(
                    unitIndex,
                    UnitWeaponSlot.Primary);
            }

            return;
        }

        if (key == Keyboard.Key.K)
        {
            if (_unitSimulation.Units.TryGetIndex(
                    _selectedUnit,
                    out int unitIndex))
            {
                _unitSimulation.Weapons.CycleTargetMode(
                    unitIndex,
                    UnitWeaponSlot.Primary);
            }

            return;
        }

        if (key == Keyboard.Key.L)
        {
            if (_unitSimulation.Units.TryGetIndex(
                    _selectedUnit,
                    out int unitIndex))
            {
                int stateIndex =
                    UnitWeaponStore.GetIndex(
                        unitIndex,
                        UnitWeaponSlot.Primary);

                int currentAmmo =
                    _unitSimulation.Weapons.CurrentAmmoType[stateIndex];

                int nextAmmo =
                    currentAmmo + 1;

                short inventorySlot =
                    _unitSimulation.Inventory.GetWeaponEquipment(
                        unitIndex,
                        UnitWeaponSlot.Primary);

                if (inventorySlot >= 0)
                {
                    RangedWeaponConfig? weapon =
                        _unitSimulation.Inventory.GetItem(
                            unitIndex,
                            inventorySlot) as RangedWeaponConfig;

                    int ammoCount =
                        weapon?.AmmoSet?.Count ?? 1;

                    nextAmmo =
                        ammoCount <= 0
                            ? 0
                            : nextAmmo % ammoCount;
                }

                _unitSimulation.SelectAmmunition(
                    _selectedUnit,
                    UnitWeaponSlot.Primary,
                    nextAmmo);
            }

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

    private GameSettingsSnapshot GetGameSettingsSnapshot()
    {
        Vector2u currentSize = _window.Size;

        return new GameSettingsSnapshot(
            _visibleMaxLayer,
            _showDebugGrid,
            _showTerrainExtrema,
            _mapRenderer.BaseGray,
            _mapRenderer.HeightContrast,
            _mapRenderer.DepthShade,
            _mapRenderer.LayerScreenOffset,
            _worldMap.LayerCount,
            _selectedResolutionIndex,
            currentSize.X,
            currentSize.Y);
    }

    private void ApplyGameMenuAction(GameMenuAction action)
    {
        switch (action.Type)
        {
            case GameMenuActionType.OpenMainMenu:
                _menuPage = GameMenuPage.Main;
                _draggingMenuSlider = null;
                break;

            case GameMenuActionType.Resume:
                _menuPage = GameMenuPage.Closed;
                _draggingMenuSlider = null;
                break;

            case GameMenuActionType.OpenSettings:
                _menuPage = GameMenuPage.Settings;
                _draggingMenuSlider = null;
                break;

            case GameMenuActionType.BackToMainMenu:
                _menuPage = GameMenuPage.Main;
                _draggingMenuSlider = null;
                break;

            case GameMenuActionType.Exit:
                _closeRequested = true;
                break;

            case GameMenuActionType.PreviousResolution:
                _selectedResolutionIndex =
                    (_selectedResolutionIndex - 1 + GameMenuOverlay.ResolutionCount) %
                    GameMenuOverlay.ResolutionCount;
                break;

            case GameMenuActionType.NextResolution:
                _selectedResolutionIndex =
                    (_selectedResolutionIndex + 1) %
                    GameMenuOverlay.ResolutionCount;
                break;

            case GameMenuActionType.ApplyResolution:
                ApplySelectedResolution();
                break;

            case GameMenuActionType.SetBaseGray:
                _mapRenderer.SetVisualSettings(
                    action.Value,
                    _mapRenderer.HeightContrast,
                    _mapRenderer.DepthShade,
                    _mapRenderer.LayerScreenOffset);
                break;

            case GameMenuActionType.SetHeightContrast:
                _mapRenderer.SetVisualSettings(
                    _mapRenderer.BaseGray,
                    action.Value,
                    _mapRenderer.DepthShade,
                    _mapRenderer.LayerScreenOffset);
                break;

            case GameMenuActionType.SetDepthShade:
                _mapRenderer.SetVisualSettings(
                    _mapRenderer.BaseGray,
                    _mapRenderer.HeightContrast,
                    action.Value,
                    _mapRenderer.LayerScreenOffset);
                break;

            case GameMenuActionType.SetLayerOffset:
                _mapRenderer.SetVisualSettings(
                    _mapRenderer.BaseGray,
                    _mapRenderer.HeightContrast,
                    _mapRenderer.DepthShade,
                    action.Value);
                break;

            case GameMenuActionType.SetLayer:
                _visibleMaxLayer = Math.Clamp(
                    action.Layer,
                    0,
                    _worldMap.LayerCount - 1);
                break;

            case GameMenuActionType.ToggleGrid:
                _showDebugGrid = !_showDebugGrid;
                break;

            case GameMenuActionType.ToggleExtrema:
                _showTerrainExtrema = !_showTerrainExtrema;
                break;

            case GameMenuActionType.ResetTerrainSettings:
                _mapRenderer.ResetVisualSettings();
                _showDebugGrid = false;
                _showTerrainExtrema = false;
                _visibleMaxLayer = _worldMap.LayerCount - 1;
                break;
        }
    }

    private void ApplySelectedResolution()
    {
        GameResolution resolution =
            GameMenuOverlay.GetResolution(_selectedResolutionIndex);

        _window.Size = new Vector2u(resolution.Width, resolution.Height);
        UpdateWindowViews(_window.Size);
    }

    private static View CreateUiView(Vector2u size)
    {
        return new View(
            new Vector2f(size.X * 0.5f, size.Y * 0.5f),
            new Vector2f(size.X, size.Y));
    }

    private void UpdateWindowViews(Vector2u size)
    {
        if (size.X == 0 || size.Y == 0)
            return;

        _camera.Resize(new Vector2f(size.X, size.Y));

        _uiView?.Dispose();
        _uiView = CreateUiView(size);
    }

    private void ScrollTerrainLayers(
        int direction)
    {
        _visibleMaxLayer =
            Math.Clamp(
                _visibleMaxLayer + direction,
                0,
                _worldMap.LayerCount - 1);
    }

    private void ToggleTerrainCombatTest()
    {
        if (_terrainStressMode)
        {
            _terrainStressMode = false;
            EnterMassCombatMode();
            return;
        }

        if (!_massCombatMode)
            return;

        _massCombatTestScene.Stop(_unitSimulation);
        _massCombatMode = false;
        _terrainStressMode = true;
        _showVisionDebug = false;

        _unitSimulation.AI.Enabled = false;
        _unitSimulation.VisionEnabled = false;
        _unitSimulation.Projectiles.Clear();

        WorldGenerator.Generate(_worldMap);

        _visibleMaxLayer =
            Math.Max(
                0,
                _worldMap.LayerCount - 1);

        _mapRenderer.ResetVisualSettings();
        _showDebugGrid = false;
        _showTerrainExtrema = false;
        _menuPage = GameMenuPage.Closed;
        _draggingMenuSlider = null;

        _selectedUnit = default;

        _camera.CenterOnWorld(
            TerrainTilePixelSize,
            _worldMap);
    }

    private void EnterMassCombatMode()
    {
        if (!_massCombatMode)
        {
            ReadOnlySpan<UnitId> testUnits =
                _visionTestScene.Units;

            for (int i = 0;
                 i < testUnits.Length;
                 i++)
            {
                if (_unitSimulation.Units.IsAlive(
                        testUnits[i]))
                {
                    _unitSimulation.Destroy(
                        testUnits[i]);
                }
            }

            _unitSimulation.Projectiles.Clear();
            _massCombatMode = true;
            _showVisionDebug = false;
        }

        _massCombatTestScene.Start(
            _unitSimulation,
            _worldMap);

        _selectedUnit =
            _massCombatTestScene.FirstUnit;

        _camera.CenterOnWorld(
            TerrainTilePixelSize,
            _worldMap);
    }

    private void CreateVisionTestScene()
    {
        _visionTestScene.Setup(
            _worldMap,
            _unitSimulation,
            TerrainTilePixelSize);

        _selectedUnit =
            _visionTestScene.Observer;

        ReadOnlySpan<UnitId> units =
            _visionTestScene.Units;

        for (int i = 0;
             i < units.Length;
             i++)
        {
            if (!_unitSimulation.Units.TryGetIndex(
                    units[i],
                    out int unitIndex) ||
                _unitSimulation.Units.Type[unitIndex] !=
                UnitType.Colonist)
            {
                continue;
            }

            int inventorySlot =
                _unitSimulation.AddInventoryItem(
                    units[i],
                    WeaponCatalog.AssaultRifle);

            if (inventorySlot >= 0)
            {
                _unitSimulation.EquipWeapon(
                    units[i],
                    inventorySlot,
                    UnitWeaponSlot.Primary);
            }
        }
    }
}
