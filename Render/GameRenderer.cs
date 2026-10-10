using System;
using System.Diagnostics;
using Core.Items;
using Core.Combat;
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
    private CombatStatusOverlay? _combatStatusOverlay;
    private readonly LongRangeCombatTestScene _massCombatTestScene;

    private UnitId _selectedUnit;

    private RenderWindow _window = null!;
    private View? _uiView;
    private GameMenuOverlay? _gameMenuOverlay;
    private GameMenuActionType? _draggingMenuSlider;
    private GameMenuPage _menuPage;
    private int _selectedResolutionIndex;
    private bool _showTerrainExtrema;
    private bool _showHeightContours = true;
    private bool _showContourLabels = true;
    private bool _closeRequested;

    private bool _showDebugGrid;
    private int _visibleMaxLayer;
    private bool _showVisionDebug;
    private bool _massCombatMode;
    private bool _simulationPaused;
    private bool _terrainStressMode;
    private bool _showCombatTracers = true;
    private bool _showCombatHud = true;
    private bool _showAiStateDebug = true;
    private int _visionTestIndex;

    private float _perfTimer;
    private int _perfFrames;
    private float _perfFrameTimeSum;
    private float _maxFrameMs;
    private int _framesOver16Ms;
    private int _framesOver33Ms;
    private int _framesOver50Ms;
    private long _lastShotsSample;
    private long _lastRoundsSample;
    private long _lastProjectilesSpawnedSample;
    private long _lastHitsSample;
    private double _combatShotsPerSecond;
    private double _combatHitEventsPerSecond;
    private double _combatHitEventsPerRound;
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

        _visibleMaxLayer = 0;

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

        // Always produce a rate-limited [SHOT]/[HIT]/[BLOCKED] combat trace.
        CombatDiagnostics.Enabled = true;

        // The battlefield is a live micro-AI demonstration by default.
        // A is idempotent: it only enables AI and cannot switch it off.
        _unitSimulation.AI.Enabled = true;
        _unitSimulation.VisionEnabled = true;
        _showVisionDebug = false;

        // Launch directly into the 800-unit navigation/combat test.
        _terrainStressMode = false;
        EnterMassCombatMode();
        for (int i = 0; i < 3; i++)
            _camera.HandleZoom(-1f);

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

            float rawDeltaTime =
                clock.Restart().AsSeconds();
            float deltaTime = rawDeltaTime;

            if (deltaTime > 0.1f)
                deltaTime = 0.1f;

            Update(deltaTime, rawDeltaTime);
            Draw();
        }

        // Release GPU resources while the graphics context still exists.
        _mapRenderer.Dispose();
        _projectileRenderer.Dispose();
        _gameMenuOverlay?.Dispose();
        _combatStatusOverlay?.Dispose();
        _uiView?.Dispose();
        _window.Close();
    }

    private void Update(
        float deltaTime,
        float rawDeltaTime)
    {
        _unitSimulation.BeginMetricsFrame();
        _input.Update();

        rawDeltaTime = MathF.Max(0f, rawDeltaTime);
        _perfTimer += rawDeltaTime;
        _perfFrames++;
        _perfFrameTimeSum += rawDeltaTime;
        float frameMs = rawDeltaTime * 1000f;
        _maxFrameMs = MathF.Max(_maxFrameMs, frameMs);

        if (frameMs > 16.67f) _framesOver16Ms++;
        if (frameMs > 33.33f) _framesOver33Ms++;
        if (frameMs > 50f) _framesOver50Ms++;

        _titleTimer += MathF.Max(0f, deltaTime);

        if (_perfTimer >= 1f)
        {
            float sampleSeconds = _perfTimer;

            _fps = _perfFrameTimeSum > 0f
                ? _perfFrames / _perfFrameTimeSum
                : 0f;

            int framesOver16Ms = _framesOver16Ms;
            int framesOver33Ms = _framesOver33Ms;
            int framesOver50Ms = _framesOver50Ms;
            float maxFrameMs = _maxFrameMs;

            _perfFrames = 0;
            _perfFrameTimeSum = 0f;
            _maxFrameMs = 0f;
            _framesOver16Ms = 0;
            _framesOver33Ms = 0;
            _framesOver50Ms = 0;
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

            long totalShots = _unitSimulation.TotalShotsFired;
            long totalRounds = _unitSimulation.TotalRoundsConsumed;
            long totalProjectilesSpawned = _unitSimulation.TotalProjectilesSpawned;
            long totalHits = _unitSimulation.Projectiles.TotalHits;

            long shotsDelta = Math.Max(0L, totalShots - _lastShotsSample);
            long roundsDelta = Math.Max(0L, totalRounds - _lastRoundsSample);
            long projectilesDelta = Math.Max(0L, totalProjectilesSpawned - _lastProjectilesSpawnedSample);
            long hitsDelta = totalHits >= _lastHitsSample
                ? totalHits - _lastHitsSample
                : totalHits;

            double shotsPerSecond = shotsDelta / sampleSeconds;
            double roundsPerSecond = roundsDelta / sampleSeconds;
            double projectilesPerSecond = projectilesDelta / sampleSeconds;
            _combatShotsPerSecond = shotsDelta / sampleSeconds;
            _combatHitEventsPerSecond = hitsDelta / sampleSeconds;
            _combatHitEventsPerRound = roundsDelta > 0
                ? hitsDelta / (double)roundsDelta
                : 0d;

            _lastShotsSample = totalShots;
            _lastRoundsSample = totalRounds;
            _lastProjectilesSpawnedSample = totalProjectilesSpawned;
            _lastHitsSample = totalHits;

            string visionMetrics = _unitSimulation.VisionEnabled
                ? $"{_unitSimulation.Vision.LastUpdateMilliseconds:0.0}ms " +
                  $"{_unitSimulation.Vision.LastCandidatePairs:N0} pairs " +
                  $"{_unitSimulation.Vision.LastTargetEvaluations:N0} targets " +
                  $"{_unitSimulation.Vision.LastLineOfSightChecks:N0} LOS " +
                  $"sampled={_unitSimulation.Vision.LastActiveCandidatesScanned:N0} " +
                  $"cache={_unitSimulation.Vision.LastVisibilityMemoryEntriesScanned:N0}"
                : "OFF";

            Console.WriteLine(
                $"[PERF] FPS={_fps:0.0} " +
                $"FrameMax={maxFrameMs:0.0}ms >16={framesOver16Ms} >33={framesOver33Ms} >50={framesOver50Ms} " +
                $"RAM={_workingSetBytes / 1024d / 1024d:0.0}MB " +
                $"Heap={_managedHeapBytes / 1024d / 1024d:0.0}MB " +
                $"Alloc/s={_allocatedBytesPerSecond / 1024d / 1024d:0.00}MB/s " +
                $"TotalAlloc={_allocatedBytes / 1024d / 1024d:0.0}MB " +
                $"GC={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                $"Units={_unitSimulation.Units.ActiveCount} " +
                $"SimPaused={_simulationPaused} " +
                $"Sim={_unitSimulation.LastSimulationUpdateMilliseconds:0.0}ms " +
                $"Vision={visionMetrics} " +
                $"AI={_unitSimulation.LastAIUpdateMilliseconds:0.0}ms " +
                $"Move={_unitSimulation.LastMovementUpdateMilliseconds:0.0}ms " +
                $"Weapons={_unitSimulation.LastWeaponUpdateMilliseconds:0.0}ms " +
                $"Ballistics={_unitSimulation.LastProjectileUpdateMilliseconds:0.0}ms " +
                $"Grid={_unitSimulation.LastProjectileGridBuildMilliseconds:0.0}ms " +
                $"P={_unitSimulation.LastProjectilesVisited} " +
                $"TraceCells={_unitSimulation.LastProjectileTerrainCellsTraced} " +
                $"UnitCandidates={_unitSimulation.LastProjectileUnitCandidates} " +
                $"Shots/s={shotsPerSecond:0} Rounds/s={roundsPerSecond:0} " +
                $"ProjectileSpawn/s={projectilesPerSecond:0} " +
                $"HitEvents/s={_combatHitEventsPerSecond:0} " +
                $"Events/Round={_combatHitEventsPerRound:0.00} TotalHitEvents={totalHits} " +
                $"Health={_unitSimulation.LastHealthUpdateMilliseconds:0.0}ms " +
                $"Nav={_unitSimulation.Navigation.RoutesBuiltThisUpdate} built/" +
                    $"{_unitSimulation.Navigation.RoutesFailedThisUpdate} failed/" +
                    $"{_unitSimulation.Navigation.SearchesThisUpdate} A*/" +
                    $"{_unitSimulation.Navigation.CellsExpandedThisUpdate:N0} nodes/" +
                    $"{_unitSimulation.Navigation.SearchMillisecondsThisUpdate:0.0}ms " +
                $"NavGrid={_unitSimulation.Navigation.NavigationGridBuildMilliseconds:0.0}ms " +
                $"RouteReuse={_unitSimulation.Navigation.RoutesBuiltUsingPriorRoutesThisUpdate}/" +
                    $"{_unitSimulation.Navigation.RouteTrafficCellsConsideredThisUpdate:N0} " +
                $"VisibleQ={_mapRenderer.TerrainQuadCount:N0} " +
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

            if (!_simulationPaused)
            {
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
            _showAiStateDebug && _unitSimulation.AI.Enabled,
            false);

        _projectileRenderer.Draw(
            _window,
            _unitSimulation.Projectiles,
            TerrainTilePixelSize,
            _showCombatTracers);

        _gameMenuOverlay?.DrawWorldMarkers(
            _window,
            _worldMap,
            TerrainTilePixelSize,
            _camera.ZoomLevel,
            _showTerrainExtrema,
            _camera.View,
            _visibleMaxLayer,
            _showHeightContours,
            _showContourLabels);

        if (_uiView != null)
        {
            _window.SetView(_uiView);

            _combatStatusOverlay?.Draw(
                _window,
                _unitSimulation,
                _combatShotsPerSecond,
                _combatHitEventsPerSecond,
                _combatHitEventsPerRound,
                _showCombatTracers,
                _showAiStateDebug);

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
            $"Z={_visibleMaxLayer * 0.1f:0.0}/{(_worldMap.LayerCount - 1) * 0.1f:0.0}m PgUp/PgDn=Z-slice Shift+PgUp/PgDn=1m Shift+Wheel=Z F2=settings •••=menu | " +
            $"A=enable AI B=ballistic F=direct M=fire N=aim K=target L=ammo C=MASS Y=reset TAB=unit V=debug F6=vision F7=pause F8=scale I=AI-states T=tracers F9=HUD";
    }

    private void InitializeWindow()
    {
        _window =
            new RenderWindow(
                VideoMode.DesktopMode,
                "RimClone");

        _window.SetFramerateLimit(60);
        _camera.Resize(
            new Vector2f(
                _window.Size.X,
                _window.Size.Y));
        _uiView = CreateUiView(_window.Size);
        _gameMenuOverlay = new GameMenuOverlay();
        _combatStatusOverlay = new CombatStatusOverlay();
        _selectedResolutionIndex = GameMenuOverlay.FindResolutionIndex(_window.Size.X, _window.Size.Y);

        _window.Closed +=
            (_, _) =>
                _closeRequested = true;

        _window.Resized +=
            (_, e) =>
            {
                if (e.Size.X == 0 || e.Size.Y == 0)
                    return;

                UpdateWindowViews(e.Size);
            };

        _window.GainedFocus +=
            (_, _) =>
            {
                Vector2u size = _window.Size;
                if (size.X > 0 && size.Y > 0)
                    UpdateWindowViews(size);

                _mapRenderer.InvalidateGraphicsResources();
                Console.WriteLine(
                    "[Render] Window focus restored; terrain GPU cache will rebuild.");
            };

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
            int step =
                Keyboard.IsKeyPressed(Keyboard.Key.LShift) ||
                Keyboard.IsKeyPressed(Keyboard.Key.RShift)
                    ? 10
                    : 1;

            ScrollTerrainLayers(step);
            return;
        }

        if (key == Keyboard.Key.PageDown)
        {
            int step =
                Keyboard.IsKeyPressed(Keyboard.Key.LShift) ||
                Keyboard.IsKeyPressed(Keyboard.Key.RShift)
                    ? 10
                    : 1;

            ScrollTerrainLayers(-step);
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
            bool wasEnabled = _unitSimulation.AI.Enabled;
            _unitSimulation.AI.Enabled = true;
            _unitSimulation.VisionEnabled = true;
            CombatDiagnostics.Enabled = true;

            Console.WriteLine(
                wasEnabled
                    ? "[TEST] AI is already enabled; A never disables it."
                    : "[TEST] AI enabled; vision and combat diagnostics enabled. A will not turn AI off.");
            return;
        }

        if (key == Keyboard.Key.T)
        {
            _showCombatTracers = !_showCombatTracers;
            Console.WriteLine($"[TEST] Tracers={_showCombatTracers}");
            return;
        }

        if (key == Keyboard.Key.F9)
        {
            _showCombatHud = !_showCombatHud;
            if (_combatStatusOverlay != null)
                _combatStatusOverlay.Visible = _showCombatHud;
            return;
        }

        if (key == Keyboard.Key.I)
        {
            _showAiStateDebug = !_showAiStateDebug;
            Console.WriteLine($"[TEST] AI state markers={_showAiStateDebug}");
            return;
        }

        if (key == Keyboard.Key.F6)
        {
            _unitSimulation.VisionEnabled =
                !_unitSimulation.VisionEnabled;

            Console.WriteLine(
                $"[TEST] Vision simulation={_unitSimulation.VisionEnabled}");
            return;
        }

        if (key == Keyboard.Key.F7)
        {
            _simulationPaused = !_simulationPaused;
            Console.WriteLine(
                $"[TEST] Simulation paused={_simulationPaused}; rendering remains active");
            return;
        }

        if (key == Keyboard.Key.F8)
        {
            if (!_massCombatMode)
                return;

            _massCombatTestScene.CycleScale(
                _unitSimulation,
                _worldMap);

            _selectedUnit = _massCombatTestScene.FirstUnit;
            _camera.CenterOnWorld(TerrainTilePixelSize, _worldMap);

            Console.WriteLine(
                $"[TEST] {_massCombatTestScene.GetStatus(_unitSimulation)}");
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
            _showHeightContours,
            _showContourLabels,
            _mapRenderer.BaseGray,
            _mapRenderer.HeightContrast,
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
                    _mapRenderer.HeightContrast);
                break;

            case GameMenuActionType.SetHeightContrast:
                _mapRenderer.SetVisualSettings(
                    _mapRenderer.BaseGray,
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

            case GameMenuActionType.ToggleHeightContours:
                _showHeightContours = !_showHeightContours;
                break;

            case GameMenuActionType.ToggleContourLabels:
                _showContourLabels = !_showContourLabels;
                break;

            case GameMenuActionType.ResetTerrainSettings:
                _mapRenderer.ResetVisualSettings();
                _showDebugGrid = false;
                _showTerrainExtrema = false;
                _showHeightContours = true;
                _showContourLabels = true;
                _visibleMaxLayer = 0;
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

        // Keep the selector synchronized with the actual client size, including
        // manual resize/maximize and any size the OS adjusts after applying it.
        _selectedResolutionIndex =
            GameMenuOverlay.FindResolutionIndex(size.X, size.Y);

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

        _visibleMaxLayer = 0;

        _mapRenderer.ResetVisualSettings();
        _showDebugGrid = false;
        _showTerrainExtrema = false;
        _showHeightContours = true;
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
