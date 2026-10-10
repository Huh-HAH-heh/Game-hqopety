using System;
using System.Numerics;
using Core.Items;
using Core.Map;
using Core.Unit;

namespace RimClone.Render;

public sealed class LongRangeCombatTestScene
{
    private const int InitialUnitsPerFaction = 400;
    private const int Columns = 40;

    private int _unitsPerFaction = InitialUnitsPerFaction;
    private int UnitsPerFaction => _unitsPerFaction;
    private int TotalUnits => _unitsPerFaction * 2;
    private int Rows => _unitsPerFaction / Columns;
    private const float CombatDistance = 90f;
    private const float BaseHeight = 6f;
    private const float TerrainScale = 2.25f;
    private const float AdvanceDistance = 25f;
    private const float VisionStressRange = 75f;

    private UnitId[] _units =
        new UnitId[InitialUnitsPerFaction * 2];

    private byte[] _side =
        new byte[InitialUnitsPerFaction * 2];

    private bool _initialized;
    private float _elapsed;
    private float _fireTimer;
    private long _hitsAtReset;

    public bool Initialized =>
        _initialized;

    public UnitId FirstUnit =>
        _units[0];

    public int AliveBlue { get; private set; }

    public int AliveRed { get; private set; }

    public int AliveUnits =>
        AliveBlue + AliveRed;

    public void Start(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        if (!_initialized)
        {
            BuildTerrain(worldMap);
            SpawnUnits(simulation, worldMap);
            _initialized = true;
            simulation.AI.Enabled = false;
            simulation.VisionEnabled = false;
            _hitsAtReset = simulation.Projectiles.TotalHits;
        }
        else
        {
            ResetUnits(simulation, worldMap);
        }

        UpdateStats(simulation);
    }

    public void CycleScale(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        int nextUnitsPerFaction = _unitsPerFaction switch
        {
            80 => 400,
            400 => 800,
            _ => 80
        };

        Stop(simulation);

        _unitsPerFaction = nextUnitsPerFaction;
        _units = new UnitId[TotalUnits];
        _side = new byte[TotalUnits];

        Start(simulation, worldMap);
    }

    public void Stop(
        UnitSimulation simulation)
    {
        if (_initialized)
        {
            for (int i = 0; i < TotalUnits; i++)
            {
                if (simulation.Units.TryGetIndex(
                        _units[i],
                        out _))
                {
                    simulation.Destroy(_units[i]);
                }

                _units[i] = default;
                _side[i] = 0;
            }
        }

        simulation.Projectiles.Clear();

        _initialized = false;
        _elapsed = 0f;
        _fireTimer = 0f;
        _hitsAtReset = simulation.Projectiles.TotalHits;
        AliveBlue = 0;
        AliveRed = 0;
    }

    public void Update(
        UnitSimulation simulation,
        WorldMap worldMap,
        float deltaTime)
    {
        if (!_initialized)
            return;

        _elapsed +=
            MathF.Max(
                0f,
                deltaTime);

        // Scripted volleys keep the opening fight active until AI is enabled.
        // The two fire drivers never fire at once.
        if (simulation.AI.Enabled)
        {
            _fireTimer = 0f;
        }
        else
        {
            _fireTimer += MathF.Max(0f, deltaTime);

            if (_fireTimer >= 0.24f)
            {
                _fireTimer -= 0.24f;
                FireVolley(simulation);
            }
        }

        UpdateStats(simulation);
    }

    public string GetStatus(
        UnitSimulation simulation)
    {
        UpdateStats(simulation);

        return
            $"FIREFIGHT {TotalUnits} | " +
            $"Blue {AliveBlue} | " +
            $"Red {AliveRed} | " +
            $"Initial gap {CombatDistance:0}m | Advance {AdvanceDistance:0}m | " +
            $"Fire mode {(simulation.AI.Enabled ? "AI" : "scripted volley")} | " +
            $"Vision {(simulation.VisionEnabled ? "ON" : "OFF")} | " +
            $"Projectiles {simulation.Projectiles.ActiveCount} | " +
            $"Routes {simulation.Navigation.RoutesBuilt} built/{simulation.Navigation.RoutesFailed} failed | " +
            $"Hits {simulation.Projectiles.TotalHits - _hitsAtReset} | " +
            $"Time {_elapsed:0.0}s";
    }

    private void BuildTerrain(
        WorldMap worldMap)
    {
        int centerX =
            worldMap.TileWidth / 2;

        int centerY =
            worldMap.TileHeight / 2;

        int minX =
            Math.Max(
                4,
                centerX - (int)(100f * TerrainScale));

        int maxX =
            Math.Min(
                worldMap.MaxTileX - 4,
                centerX + (int)(100f * TerrainScale));

        int minY =
            Math.Max(
                4,
                centerY - (int)(45f * TerrainScale));

        int maxY =
            Math.Min(
                worldMap.MaxTileY - 4,
                centerY + (int)(45f * TerrainScale));

        for (int y = minY;
             y <= maxY;
             y++)
        {
            for (int x = minX;
                 x <= maxX;
                 x++)
            {
                float dx =
                    x - centerX;

                float dy =
                    y - centerY;

                float height =
                    BaseHeight;

                // Long rolling terrain.
                height +=
                    2.2f *
                    MathF.Sin(
                        dx * (0.075f / TerrainScale));

                height +=
                    1.7f *
                    MathF.Cos(
                        dy * (0.12f / TerrainScale));

                height +=
                    1.4f *
                    MathF.Sin(
                        (dx + dy) * (0.055f / TerrainScale));

                // Central high ridge with deliberate firing lanes.
                if (MathF.Abs(dx) <= 4f * TerrainScale)
                {
                    int lane =
                        (int)MathF.Floor(
                            (dy + 45f * TerrainScale) / (9f * TerrainScale));

                    bool ridgeSegment =
                        lane % 3 != 1;

                    if (ridgeSegment)
                    {
                        float ridgeFactor =
                            1f -
                            MathF.Abs(dx) / (4f * TerrainScale);

                        height +=
                            10f *
                            MathF.Max(
                                0f,
                                ridgeFactor);
                    }
                }

                // Two offset secondary ridges.
                float ridgeA =
                    MathF.Abs(
                        dy -
                        0.24f * dx -
                        15f * TerrainScale);

                if (ridgeA < 2.0f * TerrainScale &&
                    ((int)MathF.Floor(
                        (dx + 100f * TerrainScale) / (20f * TerrainScale)) % 2 == 0))
                {
                    height +=
                        6f *
                        (1f -
                         ridgeA / (2f * TerrainScale));
                }

                float ridgeB =
                    MathF.Abs(
                        dy +
                        0.19f * dx +
                        17f * TerrainScale);

                if (ridgeB < 2.0f * TerrainScale &&
                    ((int)MathF.Floor(
                        (dx + 100f * TerrainScale) / (24f * TerrainScale)) % 2 != 0))
                {
                    height +=
                        5f *
                        (1f -
                         ridgeB / (2f * TerrainScale));
                }

                bool hardCover =
                    IsHardCover(
                        x,
                        y,
                        centerX,
                        centerY,
                        TerrainScale);

                if (hardCover)
                    height += 5f;

                ushort heightUnits =
                    checked(
                        (ushort)Math.Clamp(
                            MathF.Round(
                                MathF.Max(
                                    2f,
                                    height) *
                                10f),
                            0f,
                            ushort.MaxValue));

                worldMap.SetSolidHeight(
                    x,
                    y,
                    heightUnits,
                    hardCover
                        ? (ushort)2
                        : (ushort)1);
            }
        }

        ClearWater(
            worldMap,
            minX,
            maxX,
            minY,
            maxY);
    }

    private static bool IsHardCover(
        int x,
        int y,
        int centerX,
        int centerY,
        float scale)
    {
        int dx = x - centerX;
        int dy = y - centerY;

        if (Math.Abs(
                dx + (int)(34f * scale)) <= 2f * scale &&
            Math.Abs(
                dy) <= 12f * scale)
        {
            return true;
        }

        if (Math.Abs(
                dx - (int)(38f * scale)) <= 2f * scale &&
            Math.Abs(
                dy + (int)(8f * scale)) <= 13f * scale)
        {
            return true;
        }

        return
            Math.Abs(dx) <= 2 * scale &&
            Math.Abs(dy) <= 42 * scale &&
            ((int)MathF.Floor(
                (dy + 42f * scale) / (8f * scale)) % 3 == 0);
    }

    private static void ClearWater(
        WorldMap worldMap,
        int minX,
        int maxX,
        int minY,
        int maxY)
    {
        // Clearing the water layer once avoids millions of per-cell API calls.
        worldMap.Water.Clear();
    }

    private void SpawnUnits(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        int centerX =
            worldMap.TileWidth / 2;

        int centerY =
            worldMap.TileHeight / 2;

        for (int side = 0;
             side < 2;
             side++)
        {
            ushort faction =
                side == 0
                    ? (ushort)1
                    : (ushort)2;

            float baseX =
                side == 0
                    ? centerX -
                      CombatDistance * 0.5f
                    : centerX +
                      CombatDistance * 0.5f;

            for (int row = 0;
                 row < Rows;
                 row++)
            {
                for (int column = 0;
                     column < Columns;
                     column++)
                {
                    int local =
                        row * Columns +
                        column;

                    int index =
                        side * UnitsPerFaction +
                        local;

                    float x =
                        baseX +
                        (row -
                         (Rows - 1) * 0.5f) *
                        1.5f;

                    float y =
                        centerY -
                        (Columns - 1) * 0.75f +
                        column * 1.5f +
                        (row % 2) * 0.75f;

                    SpawnUnit(
                        simulation,
                        worldMap,
                        index,
                        side,
                        faction,
                        x,
                        y);
                }
            }
        }
    }

    private void SpawnUnit(
        UnitSimulation simulation,
        WorldMap worldMap,
        int index,
        int side,
        ushort faction,
        float x,
        float y)
    {
        int tileX =
            Math.Clamp(
                (int)MathF.Floor(x),
                1,
                worldMap.MaxTileX - 1);

        int tileY =
            Math.Clamp(
                (int)MathF.Floor(y),
                1,
                worldMap.MaxTileY - 1);

        Vector3 position =
            new Vector3(
                tileX + 0.5f,
                tileY + 0.5f,
                worldMap.GetSurfaceHeight(
                    tileX,
                    tileY));

        Vector3 direction =
            side == 0
                ? new Vector3(
                    1f,
                    0f,
                    0f)
                : new Vector3(
                    -1f,
                    0f,
                    0f);

        UnitId id =
            simulation.Spawn(
                UnitType.Colonist,
                position,
                direction,
                direction,
                factionTag: faction);

        _units[index] = id;
        _side[index] = (byte)side;

        int unitIndex =
            id.Index;

        // Give each unit a real movement order so the test exercises route planning.
        SetMarchTarget(simulation, id, worldMap, side, tileX, tileY);
        simulation.Units.ViewRange[unitIndex] = VisionStressRange;
        simulation.Units.FieldOfView[unitIndex] = 180f;

        simulation.SetPosture(
            id,
            index % 7 == 0
                ? UnitPosture.Crouching
                : UnitPosture.Standing);

        int inventorySlot =
            simulation.AddInventoryItem(
                id,
                WeaponCatalog.AssaultRifle);

        if (inventorySlot >= 0)
        {
            simulation.EquipWeapon(
                id,
                inventorySlot,
                UnitWeaponSlot.Primary);

            simulation.Weapons.AddReserveAmmo(
                unitIndex,
                UnitWeaponSlot.Primary,
                1_000);
        }

        simulation.AI.Store.InitializeUnit(
            unitIndex);
    }

    private void ResetUnits(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        int centerX =
            worldMap.TileWidth / 2;

        int centerY =
            worldMap.TileHeight / 2;

        _elapsed = 0f;
        _fireTimer = 0f;
        _hitsAtReset =
            simulation.Projectiles.TotalHits;

        simulation.AI.Enabled = false;
        simulation.VisionEnabled = false;
        simulation.Projectiles.Clear();

        for (int i = 0;
             i < TotalUnits;
             i++)
        {
            if (!simulation.Units.TryGetIndex(
                    _units[i],
                    out int unit))
            {
                continue;
            }

            int side =
                _side[i];

            int local =
                i % UnitsPerFaction;

            int row =
                local / Columns;

            int column =
                local % Columns;

            float baseX =
                side == 0
                    ? centerX -
                      CombatDistance * 0.5f
                    : centerX +
                      CombatDistance * 0.5f;

            float x =
                baseX +
                (row -
                 (Rows - 1) * 0.5f) *
                1.5f;

            float y =
                centerY -
                (Columns - 1) * 0.75f +
                column * 1.5f +
                (row % 2) * 0.75f;

            int tileX =
                Math.Clamp(
                    (int)MathF.Floor(x),
                    1,
                    worldMap.MaxTileX - 1);

            int tileY =
                Math.Clamp(
                    (int)MathF.Floor(y),
                    1,
                    worldMap.MaxTileY - 1);

            simulation.Units.Position[unit] =
                new Vector3(
                    tileX + 0.5f,
                    tileY + 0.5f,
                    worldMap.GetSurfaceHeight(
                        tileX,
                        tileY));

            simulation.Units.Velocity[unit] =
                Vector3.Zero;

            SetMarchTarget(simulation, _units[i], worldMap, side, tileX, tileY);

            simulation.Units.MoveSpeed[unit] =
                UnitCatalog.Get(UnitType.Colonist).MoveSpeed;

            simulation.Units.ViewRange[unit] =
                VisionStressRange;

            simulation.Units.FieldOfView[unit] =
                180f;

            simulation.SetHeadNormal(
                _units[i],
                side == 0
                    ? new Vector3(
                        1f,
                        0f,
                        0f)
                    : new Vector3(
                        -1f,
                        0f,
                        0f));

            RestoreHealth(
                simulation,
                unit);

            simulation.Suppression.ClearUnit(
                unit);

            simulation.AI.Store.InitializeUnit(
                unit);

            RestoreWeapon(
                simulation,
                unit);
        }
    }

    private static void SetMarchTarget(
        UnitSimulation simulation,
        UnitId unit,
        WorldMap worldMap,
        int side,
        int tileX,
        int tileY)
    {
        int direction = side == 0 ? 1 : -1;
        int targetX = Math.Clamp(
            tileX + direction * (int)AdvanceDistance,
            1,
            worldMap.MaxTileX - 1);

        int targetY = Math.Clamp(
            tileY,
            1,
            worldMap.MaxTileY - 1);

        simulation.SetTarget(
            unit,
            new Vector3(
                targetX + 0.5f,
                targetY + 0.5f,
                worldMap.GetSurfaceHeight(targetX, targetY)));
    }

    private void FireVolley(
        UnitSimulation simulation)
    {
        for (int i = 0;
             i < TotalUnits;
             i++)
        {
            UnitId shooter =
                _units[i];

            if (!simulation.Units.TryGetIndex(
                    shooter,
                    out int shooterIndex) ||
                simulation.Health.OverallHitPoints[shooterIndex] <= 0f)
            {
                continue;
            }

            int targetSlot =
                i < UnitsPerFaction
                    ? i + UnitsPerFaction
                    : i - UnitsPerFaction;

            UnitId target =
                _units[targetSlot];

            if (!simulation.Units.TryGetIndex(
                    target,
                    out int targetIndex) ||
                simulation.Health.OverallHitPoints[targetIndex] <= 0f)
            {
                continue;
            }

            simulation.FireWeaponAt(
                shooter,
                UnitWeaponSlot.Primary,
                target);
        }
    }

    private static void RestoreHealth(
        UnitSimulation simulation,
        int unit)
    {
        simulation.Health.OverallHitPoints[unit] =
            simulation.Health.OverallMaxHitPoints[unit];

        int start =
            unit *
            simulation.Health.MaxParts;

        int count =
            simulation.Health.PartCount[unit];

        for (int i = 0;
             i < count;
             i++)
        {
            int part =
                start + i;

            simulation.Health.HitPoints[part] =
                simulation.Health.MaxHitPoints[part];

            simulation.Health.BleedRate[part] =
                0f;
        }
    }

    private static void RestoreWeapon(
        UnitSimulation simulation,
        int unit)
    {
        short inventorySlot =
            simulation.Inventory.GetWeaponEquipment(
                unit,
                UnitWeaponSlot.Primary);

        if (inventorySlot < 0)
            return;

        if (simulation.Inventory.GetItem(
                unit,
                inventorySlot) is not WeaponConfig weapon)
        {
            return;
        }

        simulation.Weapons.ConfigureSlot(
            unit,
            UnitWeaponSlot.Primary,
            weapon);

        simulation.Weapons.AddReserveAmmo(
            unit,
            UnitWeaponSlot.Primary,
            1_000);
    }

    private void UpdateStats(
        UnitSimulation simulation)
    {
        int blue = 0;
        int red = 0;

        ReadOnlySpan<int> active =
            simulation.Units.ActiveIndices;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unit =
                active[i];

            if (simulation.Units.FactionTag[unit] == 1 &&
                simulation.Health.OverallHitPoints[unit] > 0f)
            {
                blue++;
            }
            else if (simulation.Units.FactionTag[unit] == 2 &&
                     simulation.Health.OverallHitPoints[unit] > 0f)
            {
                red++;
            }
        }

        AliveBlue = blue;
        AliveRed = red;
    }
}
