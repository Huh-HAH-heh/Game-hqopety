using System;
using System.Numerics;
using Core.Items;
using Core.Map;
using Core.Unit;

namespace RimClone.Render;

public sealed class LongRangeCombatTestScene
{
    private const int UnitsPerFaction = 80;
    private const int TotalUnits = UnitsPerFaction * 2;
    private const int Columns = 4;
    private const int Rows = UnitsPerFaction / Columns;
    private const float CombatDistance = 176f;
    private const float BaseHeight = 6f;

    private readonly UnitId[] _units =
        new UnitId[TotalUnits];

    private readonly byte[] _side =
        new byte[TotalUnits];

    private bool _initialized;
    private float _elapsed;
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
            _hitsAtReset = simulation.Projectiles.TotalHits;
        }
        else
        {
            ResetUnits(simulation, worldMap);
        }

        UpdateStats(simulation);
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

        UpdateStats(simulation);
    }

    public string GetStatus(
        UnitSimulation simulation)
    {
        UpdateStats(simulation);

        return
            $"LONG-RANGE {TotalUnits} | " +
            $"Blue {AliveBlue} | " +
            $"Red {AliveRed} | " +
            $"Range {CombatDistance:0}m/180m | " +
            $"Projectiles {simulation.Projectiles.ActiveCount} | " +
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
                centerX - 100);

        int maxX =
            Math.Min(
                worldMap.MaxTileX - 4,
                centerX + 100);

        int minY =
            Math.Max(
                4,
                centerY - 45);

        int maxY =
            Math.Min(
                worldMap.MaxTileY - 4,
                centerY + 45);

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
                        dx * 0.075f);

                height +=
                    1.7f *
                    MathF.Cos(
                        dy * 0.12f);

                height +=
                    1.4f *
                    MathF.Sin(
                        (dx + dy) * 0.055f);

                // Central high ridge with deliberate firing lanes.
                if (MathF.Abs(dx) <= 4f)
                {
                    int lane =
                        (int)MathF.Floor(
                            (dy + 45f) / 9f);

                    bool ridgeSegment =
                        lane % 3 != 1;

                    if (ridgeSegment)
                    {
                        float ridgeFactor =
                            1f -
                            MathF.Abs(dx) / 4f;

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
                        15f);

                if (ridgeA < 2.0f &&
                    ((int)MathF.Floor(
                        (dx + 100f) / 20f) % 2 == 0))
                {
                    height +=
                        6f *
                        (1f -
                         ridgeA / 2f);
                }

                float ridgeB =
                    MathF.Abs(
                        dy +
                        0.19f * dx +
                        17f);

                if (ridgeB < 2.0f &&
                    ((int)MathF.Floor(
                        (dx + 100f) / 24f) % 2 != 0))
                {
                    height +=
                        5f *
                        (1f -
                         ridgeB / 2f);
                }

                bool hardCover =
                    IsHardCover(
                        x,
                        y,
                        centerX,
                        centerY);

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
        int centerY)
    {
        int dx = x - centerX;
        int dy = y - centerY;

        if (Math.Abs(
                dx + 34) <= 2 &&
            Math.Abs(
                dy) <= 12)
        {
            return true;
        }

        if (Math.Abs(
                dx - 38) <= 2 &&
            Math.Abs(
                dy + 8) <= 13)
        {
            return true;
        }

        return
            Math.Abs(dx) <= 2 &&
            Math.Abs(dy) <= 42 &&
            ((int)MathF.Floor(
                (dy + 42f) / 8f) % 3 == 0);
    }

    private static void ClearWater(
        WorldMap worldMap,
        int minX,
        int maxX,
        int minY,
        int maxY)
    {
        for (int y = minY;
             y <= maxY;
             y++)
        {
            for (int x = minX;
                 x <= maxX;
                 x++)
            {
                for (int z = 0;
                     z < worldMap.Water.Levels;
                     z++)
                {
                    worldMap.Water.SetAmount(
                        x,
                        y,
                        z,
                        0);
                }
            }
        }
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
                        (column -
                         (Columns - 1) * 0.5f) *
                        1.5f;

                    float y =
                        centerY -
                        (Rows - 1) * 0.75f +
                        row * 1.5f;

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

        // The range test intentionally freezes the battle line.
        simulation.Units.MoveSpeed[unitIndex] = 0f;
        simulation.Units.ViewRange[unitIndex] = 220f;
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
        _hitsAtReset =
            simulation.Projectiles.TotalHits;

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
                (column -
                 (Columns - 1) * 0.5f) *
                1.5f;

            float y =
                centerY -
                (Rows - 1) * 0.75f +
                row * 1.5f;

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

            simulation.Units.MoveSpeed[unit] =
                0f;

            simulation.Units.ViewRange[unit] =
                220f;

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
