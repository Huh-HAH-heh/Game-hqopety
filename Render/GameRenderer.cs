using System;
using Core.Items;
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
    private readonly ProjectileRenderSystem _projectileRenderer;
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

        _projectileRenderer =
            new ProjectileRenderSystem();

        _visionTestScene =
            new VisionTestScene();

        CreateVisionTestScene();
        _unitSimulation.AI.Enabled = true;
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

        _visionTestScene.UpdateAiDemo(
            _unitSimulation,
            _worldMap,
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
            true,
            false);

        _projectileRenderer.Draw(
            _window,
            _unitSimulation.Projectiles,
            TerrainTilePixelSize);

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
        int projectiles =
            _unitSimulation.Projectiles.ActiveCount;

        long hits =
            _unitSimulation.Projectiles.TotalHits;

        int teamOneAlive =
            _visionTestScene.GetAliveCount(
                _unitSimulation,
                1);

        int teamTwoAlive =
            _visionTestScene.GetAliveCount(
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
            _visionTestScene.GetAiDemoStage();

        return
            $"RimClone | Units={_unitSimulation.Units.ActiveCount} | " +
            $"Projectiles={projectiles} Hits={hits} | " +
            $"Teams 1:{teamOneAlive} 2:{teamTwoAlive} | " +
            $"AI={ai}:{aiState} | " +
            $"States I={idle} A={attack} C={cover} S={search} D={dead} | " +
            $"Suppression S={suppressed} P={panicked} | " +
            $"Demo={demoStage} LastHit={_unitSimulation.Projectiles.LastHitPart} | " +
            $"Vision {debug} | " +
            $"Visible={visible} Blocked={blocked} " +
            $"FOV={outsideFov} Range={outOfRange} | " +
            $"A=AI B=ballistic F=direct M=fire N=aim K=target L=ammo C=test Y=reset TAB=unit V=vision";
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

        if (key == Keyboard.Key.A)
        {
            _unitSimulation.AI.Enabled =
                !_unitSimulation.AI.Enabled;
            return;
        }

        if (key == Keyboard.Key.C)
        {
            CombatSelfTest.Run();
            return;
        }


        if (key == Keyboard.Key.Y)
        {
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
