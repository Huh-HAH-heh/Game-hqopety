using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
    private readonly HashSet<UnitId> _selectedUnits = new();
    private bool _isSelectingUnits;
    private Vector2i _selectionDragStartPixel;
    private Vector2i _selectionDragCurrentPixel;
    private Vector2f _selectionDragStartWorld;
    private Vector2f _selectionDragCurrentWorld;
    private const int SelectionDragThreshold = 5;

    private readonly RectangleShape _selectionBox = new(new Vector2f(0f, 0f))
    {
        FillColor = new Color(85, 155, 225, 38),
        OutlineColor = new Color(125, 190, 255, 220),
        OutlineThickness = 1f
    };

    private RenderWindow _window = null!;
    private View? _uiView;
    private GameMenuOverlay? _gameMenuOverlay;
    private ContextMenuOverlay? _contextMenuOverlay;
    private UnitCardOverlay? _unitCardOverlay;
    private UnitId _contextMenuUnit;
    private UnitId _unitCardUnit;
    private UnitCardDetailsSnapshot? _latestUnitCardDetails;
    private long _unitCardRequestKey;
    private GameMenuActionType? _draggingMenuSlider;
    private GameMenuPage _menuPage;
    private int _selectedResolutionIndex;
    private bool _showTerrainExtrema;
    private bool _showHeightContours = true;
    private bool _showContourLabels = true;
    private bool _closeRequested;
    private bool _graphicsRecoveryPending;

    private bool _showDebugGrid;
    private int _visibleMaxLayer;
    private bool _showVisionDebug;
    private bool _massCombatMode;
    private volatile bool _simulationPaused;
    private bool _terrainStressMode;
    private bool _showCombatTracers = true;
    private bool _showCombatHud = true;
    private bool _showAiStateDebug = false;
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
    private Thread? _simulationThread;
    private readonly ConcurrentQueue<Action> _simulationCommands = new();
    private volatile bool _stopSimulationThread;
    private volatile bool _simulationRunEnabled = true;
    private SimulationFrameSnapshot? _latestSnapshot;

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

        if (_massCombatMode)
            StartSimulationThread();

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

        StopSimulationThread();

        // Release GPU resources while the graphics context still exists.
        _mapRenderer.Dispose();
        _projectileRenderer.Dispose();
        _gameMenuOverlay?.Dispose();
        _contextMenuOverlay?.Dispose();
        _unitCardOverlay?.Dispose();
        _combatStatusOverlay?.Dispose();
        _uiView?.Dispose();
        _selectionBox.Dispose();
        _window.Close();
    }

    private void Update(
        float deltaTime,
        float rawDeltaTime)
    {
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

            using Process process = Process.GetCurrentProcess();
            _workingSetBytes = process.WorkingSet64;
            _managedHeapBytes = GC.GetTotalMemory(false);
            _allocatedBytes = GC.GetTotalAllocatedBytes(false);
            _allocatedBytesPerSecond = Math.Max(
                0L,
                _allocatedBytes - _lastAllocatedBytesSample) / sampleSeconds;
            _lastAllocatedBytesSample = _allocatedBytes;

            SimulationFrameSnapshot? snapshot = _simulationThread != null
                ? Volatile.Read(ref _latestSnapshot)
                : null;

            long totalShots = snapshot?.TotalShotsFired ?? _unitSimulation.TotalShotsFired;
            long totalRounds = snapshot?.TotalRoundsConsumed ?? _unitSimulation.TotalRoundsConsumed;
            long totalProjectilesSpawned = snapshot?.TotalProjectilesSpawned ?? _unitSimulation.TotalProjectilesSpawned;
            long totalHits = snapshot?.TotalHits ?? _unitSimulation.Projectiles.TotalHits;
            int activeUnits = snapshot?.UnitCount ?? _unitSimulation.Units.ActiveCount;
            int activeProjectiles = snapshot?.ActiveProjectiles ?? _unitSimulation.Projectiles.ActiveCount;
            int projectileCapacity = snapshot?.ProjectileCapacity ?? _unitSimulation.Projectiles.Capacity;

            long shotsDelta = Math.Max(0L, totalShots - _lastShotsSample);
            long roundsDelta = Math.Max(0L, totalRounds - _lastRoundsSample);
            long projectilesDelta = Math.Max(0L, totalProjectilesSpawned - _lastProjectilesSpawnedSample);
            long hitsDelta = totalHits >= _lastHitsSample
                ? totalHits - _lastHitsSample
                : totalHits;

            double shotsPerSecond = shotsDelta / sampleSeconds;
            double roundsPerSecond = roundsDelta / sampleSeconds;
            double projectilesPerSecond = projectilesDelta / sampleSeconds;
            _combatShotsPerSecond = shotsPerSecond;
            _combatHitEventsPerSecond = hitsDelta / sampleSeconds;
            _combatHitEventsPerRound = roundsDelta > 0
                ? hitsDelta / (double)roundsDelta
                : 0d;

            _lastShotsSample = totalShots;
            _lastRoundsSample = totalRounds;
            _lastProjectilesSpawnedSample = totalProjectilesSpawned;
            _lastHitsSample = totalHits;

            bool visionEnabled = snapshot?.VisionEnabled ?? _unitSimulation.VisionEnabled;
            string visionMetrics = visionEnabled
                ? snapshot != null
                    ? $"{snapshot.VisionMs:0.0}ms {snapshot.VisionCandidatePairs:N0} pairs " +
                      $"{snapshot.VisionTargetEvaluations:N0} targets {snapshot.VisionLineOfSightChecks:N0} LOS " +
                      $"sampled={snapshot.VisionCandidatesScanned:N0} cache={snapshot.VisionMemoryEntriesScanned:N0}"
                    : $"{_unitSimulation.Vision.LastUpdateMilliseconds:0.0}ms " +
                      $"{_unitSimulation.Vision.LastCandidatePairs:N0} pairs " +
                      $"{_unitSimulation.Vision.LastTargetEvaluations:N0} targets " +
                      $"{_unitSimulation.Vision.LastLineOfSightChecks:N0} LOS " +
                      $"sampled={_unitSimulation.Vision.LastActiveCandidatesScanned:N0} " +
                      $"cache={_unitSimulation.Vision.LastVisibilityMemoryEntriesScanned:N0}"
                : "OFF";

            double simMs = snapshot?.SimulationMs ?? _unitSimulation.LastSimulationUpdateMilliseconds;
            double aiMs = snapshot?.AiMs ?? _unitSimulation.LastAIUpdateMilliseconds;
            double moveMs = snapshot?.MovementMs ?? _unitSimulation.LastMovementUpdateMilliseconds;
            double weaponsMs = snapshot?.WeaponsMs ?? _unitSimulation.LastWeaponUpdateMilliseconds;
            double ballisticsMs = snapshot?.BallisticsMs ?? _unitSimulation.LastProjectileUpdateMilliseconds;
            double gridMs = snapshot?.ProjectileGridMs ?? _unitSimulation.LastProjectileGridBuildMilliseconds;
            int pVisited = snapshot?.ProjectilesVisited ?? _unitSimulation.LastProjectilesVisited;
            int traceCells = snapshot?.TerrainCellsTraced ?? _unitSimulation.LastProjectileTerrainCellsTraced;
            int unitCandidates = snapshot?.ProjectileUnitCandidates ?? _unitSimulation.LastProjectileUnitCandidates;
            double healthMs = snapshot?.HealthMs ?? _unitSimulation.LastHealthUpdateMilliseconds;
            int routesBuilt = snapshot?.RoutesBuilt ?? _unitSimulation.Navigation.RoutesBuiltThisUpdate;
            int routesFailed = snapshot?.RoutesFailed ?? _unitSimulation.Navigation.RoutesFailedThisUpdate;
            int navSearches = snapshot?.NavigationSearches ?? _unitSimulation.Navigation.SearchesThisUpdate;
            long navCells = snapshot?.NavigationCellsExpanded ?? _unitSimulation.Navigation.CellsExpandedThisUpdate;
            double navMs = snapshot?.NavigationSearchMs ?? _unitSimulation.Navigation.SearchMillisecondsThisUpdate;
            double navGridMs = snapshot?.NavigationGridMs ?? _unitSimulation.Navigation.NavigationGridBuildMilliseconds;
            int routesReused = snapshot?.RoutesReused ?? _unitSimulation.Navigation.RoutesBuiltUsingPriorRoutesThisUpdate;
            long routeTrafficCells = snapshot?.RouteTrafficCells ?? _unitSimulation.Navigation.RouteTrafficCellsConsideredThisUpdate;

            Console.WriteLine(
                $"[PERF] FPS={_fps:0.0} " +
                $"FrameMax={maxFrameMs:0.0}ms >16={framesOver16Ms} >33={framesOver33Ms} >50={framesOver50Ms} " +
                $"RAM={_workingSetBytes / 1024d / 1024d:0.0}MB " +
                $"Heap={_managedHeapBytes / 1024d / 1024d:0.0}MB " +
                $"Alloc/s={_allocatedBytesPerSecond / 1024d / 1024d:0.00}MB/s " +
                $"TotalAlloc={_allocatedBytes / 1024d / 1024d:0.0}MB " +
                $"GC={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                $"Units={activeUnits} SimPaused={_simulationPaused} Sim={simMs:0.0}ms " +
                $"Vision={visionMetrics} AI={aiMs:0.0}ms Move={moveMs:0.0}ms " +
                $"Weapons={weaponsMs:0.0}ms Ballistics={ballisticsMs:0.0}ms Grid={gridMs:0.0}ms " +
                $"P={pVisited} TraceCells={traceCells} UnitCandidates={unitCandidates} " +
                $"Shots/s={shotsPerSecond:0} Rounds/s={roundsPerSecond:0} " +
                $"ProjectileSpawn/s={projectilesPerSecond:0} HitEvents/s={_combatHitEventsPerSecond:0} " +
                $"Events/Round={_combatHitEventsPerRound:0.00} TotalHitEvents={totalHits} Health={healthMs:0.0}ms " +
                $"Nav={routesBuilt} built/{routesFailed} failed/{navSearches} A*/{navCells:N0} nodes/{navMs:0.0}ms " +
                $"NavGrid={navGridMs:0.0}ms RouteReuse={routesReused}/{routeTrafficCells:N0} " +
                $"VisibleQ={_mapRenderer.TerrainQuadCount:N0} Chunks={_mapRenderer.TerrainChunkCacheCount} " +
                $"LayerShader={_mapRenderer.UsesTerrainLayerShader} VisibleV={_mapRenderer.TerrainVertexCount:N0} " +
                $"CacheV={_mapRenderer.TerrainCachedVertexCount:N0} ScratchV={_mapRenderer.TerrainScratchCapacity:N0} " +
                $"VBO={_mapRenderer.UsesTerrainVertexBuffer} LastChunkBuild={_mapRenderer.LastTerrainBuildMilliseconds:0.0}ms " +
                $"ChunkBuilds={_mapRenderer.TerrainMeshRebuildCount} Projectiles={activeProjectiles}/{projectileCapacity}");
        }

        _simulationRunEnabled = _menuPage == GameMenuPage.Closed && !_simulationPaused;

        if (_menuPage == GameMenuPage.Closed)
        {
            _camera.Update(_input.MoveDirection, deltaTime);

            if (_simulationThread == null && !_simulationPaused)
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
                    _unitSimulation.BeginMetricsFrame();
                    _unitSimulation.Update(_worldMap, deltaTime);
                }
            }
        }
    }

    private void Draw()
    {
        // Re-activate the render context explicitly. On some desktop/window
        // switches the window can regain focus while its GL context is not
        // current yet; drawing against it can leave the window black.
        if (!_window.SetActive(true))
        {
            _graphicsRecoveryPending = true;
            return;
        }

        if (_graphicsRecoveryPending)
        {
            Vector2u size = _window.Size;
            if (size.X > 0 && size.Y > 0)
                UpdateWindowViews(size);

            // Only dispose/rebuild cached GPU objects after SetActive succeeds.
            _mapRenderer.InvalidateGraphicsResources();
            _graphicsRecoveryPending = false;
            Console.WriteLine(
                "[Render] Graphics context restored; terrain GPU cache will rebuild.");
        }

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

        SimulationFrameSnapshot? renderSnapshot = _simulationThread != null
            ? Volatile.Read(ref _latestSnapshot)
            : null;

        if (renderSnapshot != null)
        {
            _unitRenderer.DrawSnapshot(
                _window,
                renderSnapshot,
                _camera.View,
                TerrainTilePixelSize,
                _selectedUnit.IsValid ? _selectedUnit.Index : -1,
                _showAiStateDebug && renderSnapshot.AiEnabled,
                _selectedUnits);

            _projectileRenderer.DrawSnapshot(
                _window,
                renderSnapshot,
                TerrainTilePixelSize,
                _showCombatTracers);
        }
        else
        {
            _unitRenderer.Draw(
                _window,
                _unitSimulation,
                _worldMap,
                _camera.View,
                TerrainTilePixelSize,
                _selectedUnit,
                _showVisionDebug && !_massCombatMode,
                _showAiStateDebug && _unitSimulation.AI.Enabled,
                false,
                _selectedUnits);

            _projectileRenderer.Draw(
                _window,
                _unitSimulation.Projectiles,
                TerrainTilePixelSize,
                _showCombatTracers);
        }

        if (_isSelectingUnits && IsSelectionDrag())
            _window.Draw(_selectionBox);

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

            if (renderSnapshot != null)
            {
                _combatStatusOverlay?.DrawSnapshot(
                    _window,
                    renderSnapshot,
                    _combatShotsPerSecond,
                    _combatHitEventsPerSecond,
                    _combatHitEventsPerRound,
                    _showCombatTracers,
                    _showAiStateDebug);
            }
            else
            {
                _combatStatusOverlay?.Draw(
                    _window,
                    _unitSimulation,
                    _combatShotsPerSecond,
                    _combatHitEventsPerSecond,
                    _combatHitEventsPerRound,
                    _showCombatTracers,
                    _showAiStateDebug);
            }

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

            _contextMenuOverlay?.Draw(
                _window,
                _window.Size.X,
                _window.Size.Y);

            if (_unitCardOverlay?.IsOpen == true)
            {
                SimulationFrameSnapshot? cardSnapshot = renderSnapshot;
                if (cardSnapshot == null)
                    cardSnapshot = SimulationFrameSnapshot.Capture(_unitSimulation);

                if (TryFindRenderUnit(cardSnapshot, _unitCardUnit, out RenderUnitState cardUnit))
                {
                    UnitCardDetailsSnapshot? details = _simulationThread != null
                        ? Volatile.Read(ref _latestUnitCardDetails)
                        : UnitCardDetailsSnapshot.Capture(_unitSimulation, _unitCardUnit);

                    _unitCardOverlay.Draw(
                        _window,
                        cardUnit,
                        details,
                        _window.Size.X,
                        _window.Size.Y);
                }
                else
                {
                    CloseUnitCard();
                }
            }
        }

        if (_titleTimer >= 0.25f)
        {
            _titleTimer = 0f;
            _windowTitle = BuildWindowTitle();
            _window.SetTitle(_windowTitle);
        }

        _window.Display();
    }

    private string BuildSnapshotWindowTitle(SimulationFrameSnapshot snapshot)
    {
        string ai = snapshot.AiEnabled ? "ON" : "OFF";
        string vision = snapshot.VisionEnabled ? "ON" : "OFF";
        string selectedState = "None";

        for (int i = 0; i < snapshot.Units.Length; i++)
        {
            if (!_selectedUnit.IsValid ||
                snapshot.Units[i].Index != _selectedUnit.Index ||
                snapshot.Units[i].Generation != _selectedUnit.Generation)
                continue;

            selectedState = snapshot.Units[i].AiState.ToString();
            break;
        }

        return
            $"RimClone | Units={snapshot.UnitCount} | " +
            $"Projectiles={snapshot.ActiveProjectiles}/{snapshot.ProjectileCapacity} Hits={snapshot.TotalHits} | " +
            $"Teams 1:{snapshot.BlueAlive} 2:{snapshot.RedAlive} | AI={ai}:{selectedState} | " +
            $"States I={snapshot.IdleCount} A={snapshot.AttackCount} C={snapshot.CoverCount} " +
            $"S={snapshot.SearchCount} D={snapshot.DeadCount} | " +
            $"Suppression S={snapshot.SuppressedCount} P={snapshot.PanickedCount} | " +
            $"Demo=FIREFIGHT | LastHit={snapshot.LastHitPart} | " +
            $"Z={_visibleMaxLayer * 0.1f:0.0}/{(_worldMap.LayerCount - 1) * 0.1f:0.0}m " +
            "PgUp/PgDn=Z-slice Shift+PgUp/PgDn=1m Shift+Wheel=Z F2=settings •••=menu | " +
            "A=enable AI B=ballistic F=direct M=fire N=aim K=target L=ammo C=MASS Y=reset TAB=unit V=debug F6=vision F7=pause F8=scale I=AI-states T=tracers F9=HUD | " +
            $"Vision {vision}";
    }

    private string BuildWindowTitle()
    {
        SimulationFrameSnapshot? snapshot = _simulationThread != null
            ? Volatile.Read(ref _latestSnapshot)
            : null;

        if (snapshot != null)
            return BuildSnapshotWindowTitle(snapshot);

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
                        targetIndex,
                        _unitSimulation.Bodies).State;

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

    private void StartSimulationThread()
    {
        if (_simulationThread != null || !_massCombatMode)
            return;

        _stopSimulationThread = false;
        _simulationRunEnabled = _menuPage == GameMenuPage.Closed && !_simulationPaused;

        // Publish a complete initial frame before starting the producer thread.
        Volatile.Write(
            ref _latestSnapshot,
            SimulationFrameSnapshot.Capture(_unitSimulation));

        _simulationThread = new Thread(SimulationLoop)
        {
            IsBackground = true,
            Name = "RimClone.Simulation"
        };
        _simulationThread.Start();
        Console.WriteLine("[Threading] Simulation runs independently; SFML rendering stays on the window thread.");
    }

    private void SetSelectedUnit(UnitId unit)
    {
        _selectedUnit = unit;
        _selectedUnits.Clear();
        if (unit.IsValid)
            _selectedUnits.Add(unit);
    }

    private void BeginWorldSelection(Vector2i mousePixel)
    {
        _isSelectingUnits = true;
        _selectionDragStartPixel = mousePixel;
        _selectionDragCurrentPixel = mousePixel;
        _selectionDragStartWorld =
            _window.MapPixelToCoords(mousePixel, _camera.View);
        _selectionDragCurrentWorld = _selectionDragStartWorld;
        UpdateSelectionBox();
    }

    private bool IsSelectionDrag()
    {
        int dx = _selectionDragCurrentPixel.X - _selectionDragStartPixel.X;
        int dy = _selectionDragCurrentPixel.Y - _selectionDragStartPixel.Y;
        return dx * dx + dy * dy >= SelectionDragThreshold * SelectionDragThreshold;
    }

    private void UpdateSelectionBox()
    {
        float left = MathF.Min(_selectionDragStartWorld.X, _selectionDragCurrentWorld.X);
        float top = MathF.Min(_selectionDragStartWorld.Y, _selectionDragCurrentWorld.Y);
        _selectionBox.Position = new Vector2f(left, top);
        _selectionBox.Size = new Vector2f(
            MathF.Abs(_selectionDragCurrentWorld.X - _selectionDragStartWorld.X),
            MathF.Abs(_selectionDragCurrentWorld.Y - _selectionDragStartWorld.Y));
    }

    private void SelectWorldUnitsInRectangle(Vector2f startPixels, Vector2f endPixels)
    {
        SimulationFrameSnapshot? snapshot = _simulationThread != null
            ? Volatile.Read(ref _latestSnapshot)
            : SimulationFrameSnapshot.Capture(_unitSimulation);

        _selectedUnits.Clear();
        _selectedUnit = default;
        if (snapshot == null)
            return;

        float minX = MathF.Min(startPixels.X, endPixels.X) / TerrainTilePixelSize;
        float maxX = MathF.Max(startPixels.X, endPixels.X) / TerrainTilePixelSize;
        float minY = MathF.Min(startPixels.Y, endPixels.Y) / TerrainTilePixelSize;
        float maxY = MathF.Max(startPixels.Y, endPixels.Y) / TerrainTilePixelSize;
        RenderBodyPart[] parts = snapshot.BodyParts;

        for (int i = 0; i < snapshot.Units.Length; i++)
        {
            RenderUnitState unit = snapshot.Units[i];
            if (unit.HitPoints <= 0f)
                continue;

            int start = Math.Clamp(unit.BodyPartStart, 0, parts.Length);
            int count = Math.Clamp(unit.BodyPartCount, 0, parts.Length - start);
            bool intersects = false;

            for (int partIndex = start; partIndex < start + count; partIndex++)
            {
                if (!IntersectsSelectionRectangle(
                        parts[partIndex], minX, minY, maxX, maxY))
                    continue;

                intersects = true;
                break;
            }

            if (!intersects && count == 0)
            {
                System.Numerics.Vector3 position = unit.Position;
                intersects = position.X >= minX && position.X <= maxX &&
                             position.Y >= minY && position.Y <= maxY;
            }

            if (!intersects)
                continue;

            UnitId id = new(unit.Index, unit.Generation);
            _selectedUnits.Add(id);
            _selectedUnit = id;
        }
    }

    private static bool IntersectsSelectionRectangle(
        in RenderBodyPart part,
        float minX,
        float minY,
        float maxX,
        float maxY)
    {
        float absCos = MathF.Abs(MathF.Cos(part.Rotation));
        float absSin = MathF.Abs(MathF.Sin(part.Rotation));
        float extentX = MathF.Abs(part.Scale.X) * absCos +
                        MathF.Abs(part.Scale.Y) * absSin;
        float extentY = MathF.Abs(part.Scale.X) * absSin +
                        MathF.Abs(part.Scale.Y) * absCos;

        return part.Position.X + extentX >= minX &&
               part.Position.X - extentX <= maxX &&
               part.Position.Y + extentY >= minY &&
               part.Position.Y - extentY <= maxY;
    }

    private void SelectWorldUnit(Vector2i mousePixel)
    {
        Vector2f worldPixels = _window.MapPixelToCoords(mousePixel, _camera.View);
        float worldX = worldPixels.X / TerrainTilePixelSize;
        float worldY = worldPixels.Y / TerrainTilePixelSize;

        SimulationFrameSnapshot? snapshot = _simulationThread != null
            ? Volatile.Read(ref _latestSnapshot)
            : SimulationFrameSnapshot.Capture(_unitSimulation);

        float pixelsPerUnit = TerrainTilePixelSize *
            _window.Size.X / MathF.Max(1f, _camera.View.Size.X);
        float hitPadding = 4f / MathF.Max(1f, pixelsPerUnit);

        if (snapshot != null &&
            TryPickWorldUnit(snapshot, worldX, worldY, hitPadding, out RenderUnitState unit))
        {
            SetSelectedUnit(new UnitId(unit.Index, unit.Generation));
            return;
        }

        SetSelectedUnit(default);
    }

    private static bool TryPickWorldUnit(
        SimulationFrameSnapshot snapshot,
        float worldX,
        float worldY,
        float padding,
        out RenderUnitState selected)
    {
        selected = default;
        bool found = false;
        RenderBodyPart[] parts = snapshot.BodyParts;

        // Units are painted in snapshot order, so the last intersected unit
        // matches the object visually on top when silhouettes overlap.
        for (int i = 0; i < snapshot.Units.Length; i++)
        {
            RenderUnitState unit = snapshot.Units[i];
            if (unit.HitPoints <= 0f)
                continue;

            int start = Math.Clamp(unit.BodyPartStart, 0, parts.Length);
            int count = Math.Clamp(unit.BodyPartCount, 0, parts.Length - start);
            bool hit = false;

            for (int part = start; part < start + count; part++)
            {
                if (!ContainsWorldPoint(parts[part], worldX, worldY, padding))
                    continue;

                hit = true;
                break;
            }

            if (!hit && count == 0)
            {
                float radiusX = MathF.Max(0.18f, unit.Width * 0.5f) + padding;
                float radiusY = MathF.Max(0.18f, unit.Length * 0.5f) + padding;
                float dx = (worldX - unit.Position.X) / radiusX;
                float dy = (worldY - unit.Position.Y) / radiusY;
                hit = dx * dx + dy * dy <= 1f;
            }

            if (hit)
            {
                selected = unit;
                found = true;
            }
        }

        return found;
    }

    private static bool ContainsWorldPoint(
        in RenderBodyPart part,
        float worldX,
        float worldY,
        float padding)
    {
        float dx = worldX - part.Position.X;
        float dy = worldY - part.Position.Y;
        float cos = MathF.Cos(part.Rotation);
        float sin = MathF.Sin(part.Rotation);
        float localX = dx * cos + dy * sin;
        float localY = -dx * sin + dy * cos;
        float halfX = MathF.Max(0.001f, MathF.Abs(part.Scale.X) + padding);
        float halfY = MathF.Max(0.001f, MathF.Abs(part.Scale.Y) + padding);

        switch (part.Primitive)
        {
            case BodyPrimitive.Box:
                return MathF.Abs(localX) <= halfX &&
                       MathF.Abs(localY) <= halfY;

            case BodyPrimitive.Line:
                halfY = MathF.Max(0.008f, halfY);
                return MathF.Abs(localX) <= halfX &&
                       MathF.Abs(localY) <= halfY;

            case BodyPrimitive.Triangle:
                float nx = localX / halfX;
                float ny = localY / halfY;
                return nx >= -1f && nx <= 1f &&
                       MathF.Abs(ny) <= (1f - nx) * 0.5f;

            default:
                float circleX = localX / halfX;
                float circleY = localY / halfY;
                return circleX * circleX + circleY * circleY <= 1f;
        }
    }

    private void CycleMassCombatSelection()
    {
        SimulationFrameSnapshot? snapshot = Volatile.Read(ref _latestSnapshot);
        if (snapshot == null || snapshot.Units.Length == 0)
            return;

        RenderUnitState[] units = snapshot.Units;
        int current = -1;
        for (int i = 0; i < units.Length; i++)
        {
            if (units[i].Index == _selectedUnit.Index &&
                units[i].Generation == _selectedUnit.Generation)
            {
                current = i;
                break;
            }
        }

        int next = current < 0 ? 0 : (current + 1) % units.Length;
        RenderUnitState selected = units[next];
        SetSelectedUnit(new UnitId(selected.Index, selected.Generation));
        _camera.CenterOnPosition(new Vector2f(
            selected.Position.X * TerrainTilePixelSize,
            selected.Position.Y * TerrainTilePixelSize));
    }

    private void StopSimulationThread()
    {
        Thread? thread = _simulationThread;
        if (thread == null)
            return;

        _stopSimulationThread = true;
        thread.Join();
        _simulationThread = null;
        Volatile.Write(
            ref _latestSnapshot,
            SimulationFrameSnapshot.Capture(_unitSimulation));
    }

    private void ExecuteSimulationCommand(Action command)
    {
        if (_simulationThread == null)
        {
            command();
            return;
        }

        _simulationCommands.Enqueue(command);
    }

    private void SimulationLoop()
    {
        long lastTick = Stopwatch.GetTimestamp();
        long nextFrame = lastTick;
        long frameTicks = Math.Max(1L, Stopwatch.Frequency / 60L);

        while (true)
        {
            while (_simulationCommands.TryDequeue(out Action? command))
            {
                try
                {
                    command?.Invoke();
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[Simulation command] {exception}");
                }
            }

            if (_stopSimulationThread)
                break;

            long now = Stopwatch.GetTimestamp();
            float deltaTime = (float)((now - lastTick) / (double)Stopwatch.Frequency);
            lastTick = now;
            if (deltaTime <= 0f)
                deltaTime = 1f / 60f;
            if (deltaTime > 0.1f)
                deltaTime = 0.1f;

            if (_simulationRunEnabled)
            {
                try
                {
                    _unitSimulation.BeginMetricsFrame();
                    _massCombatTestScene.Update(
                        _unitSimulation,
                        _worldMap,
                        deltaTime);
                    _unitSimulation.Update(_worldMap, deltaTime);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[Simulation thread] {exception}");
                }
            }

            try
            {
                Volatile.Write(
                    ref _latestSnapshot,
                    SimulationFrameSnapshot.Capture(_unitSimulation));
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Simulation snapshot] {exception}");
            }

            long detailKey = Volatile.Read(ref _unitCardRequestKey);
            if (detailKey != 0)
            {
                try
                {
                    uint encodedIndex = (uint)detailKey;
                    int unitIndex = (int)encodedIndex - 1;
                    uint generation = (uint)(detailKey >> 32);
                    UnitId detailUnit = new UnitId(unitIndex, generation);
                    Volatile.Write(
                        ref _latestUnitCardDetails,
                        UnitCardDetailsSnapshot.Capture(_unitSimulation, detailUnit));
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[Unit card snapshot] {exception}");
                }
            }
            else
            {
                Volatile.Write(ref _latestUnitCardDetails, null);
            }

            nextFrame += frameTicks;
            long afterUpdate = Stopwatch.GetTimestamp();
            if (nextFrame < afterUpdate)
                nextFrame = afterUpdate;

            while (!_stopSimulationThread)
            {
                long remainingTicks = nextFrame - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                    break;

                int sleepMilliseconds = (int)(remainingTicks * 1000L / Stopwatch.Frequency) - 1;
                Thread.Sleep(Math.Max(1, sleepMilliseconds));
            }
        }
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
        _contextMenuOverlay = new ContextMenuOverlay();
        _unitCardOverlay = new UnitCardOverlay();
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
                // Focus events may arrive before the window's OpenGL context
                // is active again. Defer GPU resource disposal until Draw().
                _graphicsRecoveryPending = true;
            };

        _window.MouseWheelScrolled +=
            (_, e) =>
            {
                if (_menuPage != GameMenuPage.Closed)
                    return;

                Vector2i wheelPixels = Mouse.GetPosition(_window);
                Vector2f wheelUiCoordinates = _uiView != null
                    ? _window.MapPixelToCoords(wheelPixels, _uiView)
                    : new Vector2f(wheelPixels.X, wheelPixels.Y);

                if (_unitCardOverlay?.HandleWheel(
                        new Vector2i((int)wheelUiCoordinates.X, (int)wheelUiCoordinates.Y),
                        e.Delta) == true)
                {
                    return;
                }

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
                if (e.Button == Mouse.Button.Right)
                {
                    _isSelectingUnits = false;
                    _selectionBox.Size = new Vector2f(0f, 0f);

                    Vector2f contextCoordinates = _uiView != null
                        ? _window.MapPixelToCoords(e.Position, _uiView)
                        : new Vector2f(e.Position.X, e.Position.Y);
                    Vector2i contextPosition = new(
                        (int)contextCoordinates.X,
                        (int)contextCoordinates.Y);

                    CloseUnitCard();

                    if (_menuPage == GameMenuPage.Closed &&
                        _contextMenuOverlay != null)
                    {
                        SimulationFrameSnapshot? inputSnapshot = GetInputSnapshot();
                        Vector2f worldPosition = _window.MapPixelToCoords(e.Position, _camera.View);
                        float pixelsPerUnit = TerrainTilePixelSize *
                            _window.Size.X / MathF.Max(1f, _camera.View.Size.X);
                        float hitPadding = 4f / MathF.Max(1f, pixelsPerUnit);

                        bool hasUnit = inputSnapshot != null &&
                            TryPickWorldUnit(
                                inputSnapshot,
                                worldPosition.X / TerrainTilePixelSize,
                                worldPosition.Y / TerrainTilePixelSize,
                                hitPadding,
                                out RenderUnitState contextUnit);

                        _contextMenuUnit = hasUnit
                            ? new UnitId(contextUnit.Index, contextUnit.Generation)
                            : default;

                        _contextMenuOverlay.ToggleAt(
                            contextPosition,
                            _window.Size.X,
                            _window.Size.Y,
                            hasUnit);

                        if (!_contextMenuOverlay.IsOpen)
                            _contextMenuUnit = default;
                    }
                    else
                    {
                        _contextMenuUnit = default;
                        _contextMenuOverlay?.Close();
                    }

                    return;
                }

                if (e.Button != Mouse.Button.Left)
                    return;

                Vector2f uiCoordinates =
                    _uiView != null
                        ? _window.MapPixelToCoords(e.Position, _uiView)
                        : new Vector2f(e.Position.X, e.Position.Y);

                Vector2i uiPosition = new Vector2i(
                    (int)uiCoordinates.X,
                    (int)uiCoordinates.Y);

                if (_unitCardOverlay?.HandleClick(uiPosition) == true)
                    return;

                if (_contextMenuOverlay?.IsOpen == true)
                {
                    bool menuHandled = _contextMenuOverlay.HandleClick(
                        uiPosition,
                        out bool openUnitCard);

                    if (menuHandled)
                    {
                        UnitId contextUnit = _contextMenuUnit;
                        _contextMenuUnit = default;
                        if (openUnitCard && contextUnit.IsValid)
                            OpenUnitCard(contextUnit);
                        return;
                    }

                    _contextMenuUnit = default;
                }

                if (_gameMenuOverlay != null &&
                    _gameMenuOverlay.IsSliderHit(
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
                    _gameMenuOverlay?.HandleClick(
                        uiPosition,
                        _menuPage,
                        _worldMap.LayerCount);

                if (action.HasValue)
                {
                    ApplyGameMenuAction(action.Value);
                    return;
                }

                if (_menuPage == GameMenuPage.Closed)
                    BeginWorldSelection(e.Position);
            };

        _window.MouseMoved +=
            (_, e) =>
            {
                if (_isSelectingUnits)
                {
                    _selectionDragCurrentPixel = e.Position;
                    _selectionDragCurrentWorld =
                        _window.MapPixelToCoords(e.Position, _camera.View);
                    UpdateSelectionBox();
                    return;
                }

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
                if (e.Button != Mouse.Button.Left)
                    return;

                if (_draggingMenuSlider.HasValue)
                {
                    _draggingMenuSlider = null;
                    return;
                }

                if (!_isSelectingUnits)
                    return;

                _selectionDragCurrentPixel = e.Position;
                _selectionDragCurrentWorld =
                    _window.MapPixelToCoords(e.Position, _camera.View);
                bool boxSelection = IsSelectionDrag();
                _isSelectingUnits = false;
                _selectionBox.Size = new Vector2f(0f, 0f);

                if (boxSelection)
                {
                    SelectWorldUnitsInRectangle(
                        _selectionDragStartWorld,
                        _selectionDragCurrentWorld);
                }
                else
                {
                    SelectWorldUnit(e.Position);
                }
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
            CloseUnitCard();
            _contextMenuOverlay?.Close();
            _contextMenuUnit = default;
            _menuPage = _menuPage == GameMenuPage.Settings
                ? GameMenuPage.Closed
                : GameMenuPage.Settings;
            _draggingMenuSlider = null;
            return;
        }

        if (key == Keyboard.Key.Escape)
        {
            if (_unitCardOverlay?.IsOpen == true)
            {
                CloseUnitCard();
                return;
            }

            if (_contextMenuOverlay?.IsOpen == true)
            {
                _contextMenuOverlay.Close();
                _contextMenuUnit = default;
                return;
            }

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
            ExecuteSimulationCommand(() =>
            {
                bool wasEnabled = _unitSimulation.AI.Enabled;
                _unitSimulation.AI.Enabled = true;
                _unitSimulation.VisionEnabled = true;
                CombatDiagnostics.Enabled = true;

                Console.WriteLine(
                    wasEnabled
                        ? "[TEST] AI is already enabled; A never disables it."
                        : "[TEST] AI enabled; vision and combat diagnostics enabled. A will not turn AI off.");
            });
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
            ExecuteSimulationCommand(() =>
            {
                _unitSimulation.VisionEnabled = !_unitSimulation.VisionEnabled;
                Console.WriteLine(
                    $"[TEST] Vision simulation={_unitSimulation.VisionEnabled}");
            });
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

            // Scale changes rebuild both terrain and units, so perform them
            // with the simulation worker stopped rather than racing the map renderer.
            StopSimulationThread();
            _massCombatTestScene.CycleScale(_unitSimulation, _worldMap);
            SetSelectedUnit(_massCombatTestScene.FirstUnit);
            _camera.CenterOnWorld(TerrainTilePixelSize, _worldMap);
            Console.WriteLine($"[TEST] {_massCombatTestScene.GetStatus(_unitSimulation)}");
            StartSimulationThread();
            return;
        }

        if (key == Keyboard.Key.C)
        {
            StopSimulationThread();
            ToggleTerrainCombatTest();
            if (_massCombatMode)
                StartSimulationThread();
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
                CycleMassCombatSelection();
                return;
            }

            _visionTestIndex =
                (_visionTestIndex + 1) %
                _visionTestScene.UnitCount;

            SetSelectedUnit(_visionTestScene.GetUnit(_visionTestIndex));

            return;
        }

        if (key == Keyboard.Key.R)
        {
            _visionTestIndex = 0;
            SetSelectedUnit(_visionTestScene.Observer);
            return;
        }

        if (key == Keyboard.Key.F)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (!_unitSimulation.Units.TryGetIndex(selected, out int shooterIndex))
                    return;

                _unitSimulation.FireWeapon(
                    selected,
                    UnitWeaponSlot.Primary,
                    _unitSimulation.Units.HeadNormal[shooterIndex]);
            });
            return;
        }

        if (key == Keyboard.Key.B)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (!_unitSimulation.Units.TryGetIndex(selected, out int shooterIndex) ||
                    !_unitSimulation.AI.Store.HasTarget[shooterIndex])
                {
                    return;
                }

                UnitId target = _unitSimulation.AI.Store.Target[shooterIndex];
                if (_unitSimulation.Units.IsAlive(target))
                {
                    _unitSimulation.FireWeaponAt(
                        selected,
                        UnitWeaponSlot.Primary,
                        target);
                }
            });
            return;
        }

        if (key == Keyboard.Key.H)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
                _unitSimulation.ApplyDamage(
                    selected,
                    UnitHealthPartId.Torso,
                    70f));
            return;
        }

        if (key == Keyboard.Key.M)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (!_unitSimulation.Units.TryGetIndex(selected, out int unitIndex))
                    return;

                _unitSimulation.Weapons.CycleFireMode(
                    unitIndex,
                    UnitWeaponSlot.Primary);

                int stateIndex = UnitWeaponStore.GetIndex(
                    unitIndex,
                    UnitWeaponSlot.Primary);

                Console.WriteLine(
                    $"[FIRE CONTROL] Unit[{unitIndex}] " +
                    $"Fire={_unitSimulation.Weapons.CurrentFireMode[stateIndex]} " +
                    $"Aim={_unitSimulation.Weapons.CurrentAimMode[stateIndex]}");
            });
            return;
        }

        if (key == Keyboard.Key.N)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (!_unitSimulation.Units.TryGetIndex(selected, out int unitIndex))
                    return;

                _unitSimulation.Weapons.CycleAimMode(
                    unitIndex,
                    UnitWeaponSlot.Primary);

                int stateIndex = UnitWeaponStore.GetIndex(
                    unitIndex,
                    UnitWeaponSlot.Primary);

                Console.WriteLine(
                    $"[FIRE CONTROL] Unit[{unitIndex}] " +
                    $"Fire={_unitSimulation.Weapons.CurrentFireMode[stateIndex]} " +
                    $"Aim={_unitSimulation.Weapons.CurrentAimMode[stateIndex]}");
            });
            return;
        }

        if (key == Keyboard.Key.K)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (_unitSimulation.Units.TryGetIndex(selected, out int unitIndex))
                {
                    _unitSimulation.Weapons.CycleTargetMode(
                        unitIndex,
                        UnitWeaponSlot.Primary);
                }
            });
            return;
        }

        if (key == Keyboard.Key.L)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (!_unitSimulation.Units.TryGetIndex(selected, out int unitIndex))
                    return;

                int stateIndex = UnitWeaponStore.GetIndex(
                    unitIndex,
                    UnitWeaponSlot.Primary);
                int nextAmmo = _unitSimulation.Weapons.CurrentAmmoType[stateIndex] + 1;
                short inventorySlot = _unitSimulation.Inventory.GetWeaponEquipment(
                    unitIndex,
                    UnitWeaponSlot.Primary);

                if (inventorySlot >= 0)
                {
                    RangedWeaponConfig? weapon =
                        _unitSimulation.Inventory.GetItem(unitIndex, inventorySlot) as RangedWeaponConfig;
                    int ammoCount = weapon?.AmmoSet?.Count ?? 1;
                    nextAmmo = ammoCount <= 0 ? 0 : nextAmmo % ammoCount;
                }

                _unitSimulation.SelectAmmunition(
                    selected,
                    UnitWeaponSlot.Primary,
                    nextAmmo);
            });
            return;
        }

        if (key == Keyboard.Key.T)
        {
            UnitId selected = _selectedUnit;
            ExecuteSimulationCommand(() =>
            {
                if (!_unitSimulation.Units.IsAlive(selected))
                    return;

                float centerX = _worldMap.TileWidth * 0.5f;
                float centerY = _worldMap.TileHeight * 0.5f;
                _unitSimulation.SetTarget(
                    selected,
                    new System.Numerics.Vector3(centerX, centerY - 20f, 0f));
            });
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

        SetSelectedUnit(default);

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

        SetSelectedUnit(_massCombatTestScene.FirstUnit);

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

        SetSelectedUnit(_visionTestScene.Observer);

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

            RangedWeaponConfig weapon =
                (i % 3) switch
                {
                    0 => WeaponCatalog.PulseRepeater,
                    1 => WeaponCatalog.LaserCarbine,
                    _ => WeaponCatalog.Railgun
                };

            int inventorySlot =
                _unitSimulation.AddInventoryItem(
                    units[i],
                    weapon);

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
