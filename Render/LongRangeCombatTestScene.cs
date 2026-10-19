using System;
using System.Numerics;
using Core.Combat;
using Core.Items;
using Core.Map;
using Core.Unit;

namespace RimClone.Render;

public sealed class LongRangeCombatTestScene
{
    // Starts at 800 ranged units (400 per faction); F8 cycles 80/400/800 per side.
    private const int InitialUnitsPerFaction = 400;
    private const int Columns = 40;
    private const float FormationSpacing = 2.5f;

    private int _unitsPerFaction = InitialUnitsPerFaction;
    private int UnitsPerFaction => _unitsPerFaction;
    private int TotalUnits => _unitsPerFaction * 2;
    private int Rows => _unitsPerFaction / Columns;
    private const float CombatDistance = 200f;
    private const float BaseHeight = 6f;
    private const float TerrainScale = 2.25f;
    // The line-to-line gap is wider than vision after only 25 m of advance.
    // Advance far enough for the front ranks to acquire targets while marching.
    private const float AdvanceDistance = 50f;
    private const float VisionStressRange = 85f;
    private const int MeleeDuelUnitsPerSide = 24;
    private const int MeleeFormationColumns = 6;
    private const float MeleeFormationSpacing = 1.5f;
    private const float MeleeBattleOffsetX = 175f;
    private const float MeleeBattleOffsetY = 130f;
    private const float MeleeBattleGap = 18f;

    private UnitId[] _units =
        new UnitId[InitialUnitsPerFaction * 2];

    private byte[] _side =
        new byte[InitialUnitsPerFaction * 2];

    private UnitId[] _thirdFactionUnits =
        new UnitId[MeleeDuelUnitsPerSide];

    private bool _initialized;
    private float _elapsed;
    private float _fireTimer;
    private long _hitsAtReset;
    private long _meleeHitsAtReset;

    public bool Initialized =>
        _initialized;

    public UnitId FirstUnit =>
        _units[0];

    public UnitId FirstMeleeUnit =>
        _thirdFactionUnits[0];

    public int AliveBlue { get; private set; }

    public int AliveRed { get; private set; }

    public int AliveThirdFaction { get; private set; }

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
            SpawnThirdFactionUnits(simulation, worldMap);
            _initialized = true;
            // AI/vision are controlled by the user, not reset by scene setup.
            _hitsAtReset = simulation.Projectiles.TotalHits;
            _meleeHitsAtReset = simulation.MeleeCombat.TotalHits;
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

        for (int i = 0; i < _thirdFactionUnits.Length; i++)
        {
            if (simulation.Units.TryGetIndex(_thirdFactionUnits[i], out _))
                simulation.Destroy(_thirdFactionUnits[i]);
            _thirdFactionUnits[i] = default;
        }

        simulation.Projectiles.Clear();

        _initialized = false;
        _elapsed = 0f;
        _fireTimer = 0f;
        _hitsAtReset = simulation.Projectiles.TotalHits;
        AliveBlue = 0;
        AliveRed = 0;
        AliveThirdFaction = 0;
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
            $"FIGHT {TotalUnits + _thirdFactionUnits.Length} total | " +
            $"Blue {AliveBlue} | Red {AliveRed} | F3 melee {AliveThirdFaction} | " +
            $"Initial center gap {CombatDistance:0}m | Advance {AdvanceDistance:0}m | " +
            $"Fire mode {(simulation.AI.Enabled ? "AI" : "scripted volley")} | " +
            $"Vision {(simulation.VisionEnabled ? "ON" : "OFF")} | " +
            $"Projectiles {simulation.Projectiles.ActiveCount} | " +
            $"Routes {simulation.Navigation.RoutesBuilt} built/{simulation.Navigation.RoutesFailed} failed | " +
            $"Shots hit {simulation.Projectiles.TotalHits - _hitsAtReset} | " +
            $"Melee hits {simulation.MeleeCombat.TotalHits - _meleeHitsAtReset} | " +
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
                centerY - (int)(75f * TerrainScale));

        int maxY =
            Math.Min(
                worldMap.MaxTileY - 4,
                centerY + (int)(75f * TerrainScale));

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

                    // Keep most cross-map sight lines open. Cover is arranged
                    // in staggered pockets instead of a continuous firing wall.
                    bool ridgeSegment =
                        lane % 4 == 0;

                    if (ridgeSegment)
                    {
                        float ridgeFactor =
                            1f -
                            MathF.Abs(dx) / (4f * TerrainScale);

                        height +=
                            7f *
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
                        3.5f *
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
                        3.5f *
                        (1f -
                         ridgeB / (2f * TerrainScale));
                }

                // Three broken, winding ridgelines cut across the advance.
                // Their broad shoulders stay traversable; only the crests block LOS.
                // Sine-weighted gaps create saddles so A* has alternate routes to flank.
                float ridgeCLine =
                    13f * TerrainScale +
                    dy * 0.22f +
                    MathF.Sin(dy * 0.032f) * 10f * TerrainScale;
                float ridgeCDistance =
                    MathF.Abs(dx - ridgeCLine);
                float ridgeCExposure =
                    Math.Clamp(
                        (MathF.Sin(dy / (17f * TerrainScale) + 0.8f) + 0.28f) * 1.25f,
                        0f,
                        1f);

                if (ridgeCDistance < 6.5f * TerrainScale)
                {
                    height +=
                        7.5f *
                        (1f - ridgeCDistance / (6.5f * TerrainScale)) *
                        ridgeCExposure;
                }

                float ridgeDLine =
                    -14f * TerrainScale -
                    dy * 0.17f +
                    MathF.Sin(dy / (21f * TerrainScale)) * 8f * TerrainScale;
                float ridgeDDistance =
                    MathF.Abs(dx - ridgeDLine);
                float ridgeDExposure =
                    Math.Clamp(
                        (MathF.Cos(dy / (14f * TerrainScale) + 1.3f) + 0.30f) * 1.15f,
                        0f,
                        1f);

                if (ridgeDDistance < 5.5f * TerrainScale)
                {
                    height +=
                        5.8f *
                        (1f - ridgeDDistance / (5.5f * TerrainScale)) *
                        ridgeDExposure;
                }

                // A winding drainage cut creates low ground between raised shoulders.
                // It is deliberately shallow enough to navigate but deep enough to
                // break silhouettes when units move along the bottom.
                float washoutCenterY =
                    dx * 0.20f +
                    MathF.Sin(dx * 0.035f + 0.4f) * 8f * TerrainScale -
                    10f * TerrainScale;
                float washoutDistance =
                    MathF.Abs(dy - washoutCenterY);

                if (washoutDistance < 3.2f * TerrainScale)
                {
                    height =
                        MathF.Max(
                            2f,
                            height -
                            2.8f *
                            (1f - washoutDistance / (3.2f * TerrainScale)));
                }

                // A shallow impact basin with a broken rim adds nearby hard
                // occlusion, so a unit can disappear even when it is not far away.
                float craterDx =
                    (dx - 34f * TerrainScale) / (13f * TerrainScale);
                float craterDy =
                    (dy + 18f * TerrainScale) / (10f * TerrainScale);
                float craterDistance =
                    MathF.Sqrt(craterDx * craterDx + craterDy * craterDy);

                if (craterDistance < 0.62f)
                {
                    height -=
                        2.6f * (1f - craterDistance / 0.62f);
                }
                else if (craterDistance < 1f)
                {
                    float rim =
                        1f - MathF.Abs(craterDistance - 0.81f) / 0.19f;
                    height +=
                        3.8f * MathF.Max(0f, rim);
                }

                bool hardCover =
                    IsHardCover(
                        x,
                        y,
                        centerX,
                        centerY,
                        TerrainScale);

                // Do not encode cover as a 5 m vertical cliff. Those cliffs
                // overlapped the deployment rows and made A* report permanent
                // no-route failures until terrain editing invalidated the cache.
                // Keep the material marker and a low rise without trapping units.
                if (hardCover)
                    height += 1f;

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
                dx + (int)(42f * scale)) <= 2f * scale &&
            Math.Abs(
                dy) <= 12f * scale)
        {
            return true;
        }

        if (Math.Abs(
                dx - (int)(46f * scale)) <= 2f * scale &&
            Math.Abs(
                dy + (int)(8f * scale)) <= 13f * scale)
        {
            return true;
        }

        // Do not place repeated hard-cover blocks across the central firing lanes.
        // The two offset lines above remain flank cover; the center stays traversable.
        return false;
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
                        FormationSpacing;

                    float y =
                        centerY -
                        (Columns - 1) * FormationSpacing * 0.5f +
                        column * FormationSpacing +
                        (row % 2) * FormationSpacing * 0.5f;

                    if (side == 1 &&
                        local >= UnitsPerFaction - MeleeDuelUnitsPerSide)
                    {
                        GetMeleeFormationPosition(
                            worldMap,
                            side,
                            local - (UnitsPerFaction - MeleeDuelUnitsPerSide),
                            out x,
                            out y);
                    }

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

        bool isMeleeDuelUnit =
            side == 1 &&
            index % UnitsPerFaction >= UnitsPerFaction - MeleeDuelUnitsPerSide;

        // Keep the detached squad out of the main firefight until it spots faction 3.
        if (isMeleeDuelUnit)
            simulation.SetTarget(id, position);
        else
            SetMarchTarget(simulation, id, worldMap, side, tileX, tileY);

        simulation.Units.ViewRange[unitIndex] = VisionStressRange;
        simulation.Units.FieldOfView[unitIndex] = 180f;

        simulation.SetPosture(
            id,
            index % 7 == 0
                ? UnitPosture.Crouching
                : UnitPosture.Standing);

        int inventorySlot = simulation.AddInventoryItem(
            id,
            isMeleeDuelUnit
                ? MeleeWeaponCatalog.CombatKnife
                : WeaponCatalog.AssaultRifle);

        if (inventorySlot >= 0)
        {
            UnitWeaponSlot weaponSlot = isMeleeDuelUnit
                ? UnitWeaponSlot.Melee
                : UnitWeaponSlot.Primary;

            simulation.EquipWeapon(id, inventorySlot, weaponSlot);

            if (!isMeleeDuelUnit)
            {
                simulation.Weapons.AddReserveAmmo(
                    unitIndex,
                    UnitWeaponSlot.Primary,
                    1_000);
                ConfigureCombatModes(simulation, unitIndex, index);
            }
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
        _hitsAtReset = simulation.Projectiles.TotalHits;
        _meleeHitsAtReset = simulation.MeleeCombat.TotalHits;

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
                FormationSpacing;

            float y =
                centerY -
                (Columns - 1) * FormationSpacing * 0.5f +
                column * FormationSpacing +
                (row % 2) * FormationSpacing * 0.5f;

            bool isMeleeDuelUnit =
                side == 1 &&
                local >= UnitsPerFaction - MeleeDuelUnitsPerSide;

            if (isMeleeDuelUnit)
            {
                GetMeleeFormationPosition(
                    worldMap,
                    side,
                    local - (UnitsPerFaction - MeleeDuelUnitsPerSide),
                    out x,
                    out y);
            }

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

            if (isMeleeDuelUnit)
                simulation.SetTarget(_units[i], simulation.Units.Position[unit]);
            else
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

            simulation.AI.Store.InitializeUnit(unit);
            simulation.AI.InitializeUnit(unit);
            if (isMeleeDuelUnit)
                simulation.MeleeCombat.InitializeUnit(unit, _units[i]);
            else
                RestoreWeapon(simulation, unit);
        }

        ResetThirdFactionUnits(simulation, worldMap);
    }

    private void SpawnThirdFactionUnits(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        for (int i = 0; i < _thirdFactionUnits.Length; i++)
        {
            GetMeleeFormationPosition(worldMap, 2, i, out float x, out float y);
            int tileX = Math.Clamp((int)MathF.Floor(x), 1, worldMap.MaxTileX - 1);
            int tileY = Math.Clamp((int)MathF.Floor(y), 1, worldMap.MaxTileY - 1);
            Vector3 position = new(
                tileX + 0.5f,
                tileY + 0.5f,
                worldMap.GetSurfaceHeight(tileX, tileY));
            Vector3 direction = Vector3.UnitX;
            UnitId id = simulation.Spawn(
                UnitType.Colonist,
                position,
                direction,
                direction,
                factionTag: 3);
            _thirdFactionUnits[i] = id;

            int meleeSlot = simulation.AddInventoryItem(id, MeleeWeaponCatalog.LongBlade);
            if (meleeSlot >= 0)
                simulation.EquipWeapon(id, meleeSlot, UnitWeaponSlot.Melee);

            simulation.Units.ViewRange[id.Index] = VisionStressRange;
            simulation.Units.FieldOfView[id.Index] = 180f;
            simulation.SetPosture(id, UnitPosture.Standing);
            simulation.SetTarget(id, position);
        }
    }

    private void ResetThirdFactionUnits(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        for (int i = 0; i < _thirdFactionUnits.Length; i++)
        {
            UnitId id = _thirdFactionUnits[i];
            if (!simulation.Units.TryGetIndex(id, out int unit))
                continue;

            GetMeleeFormationPosition(worldMap, 2, i, out float x, out float y);
            int tileX = Math.Clamp((int)MathF.Floor(x), 1, worldMap.MaxTileX - 1);
            int tileY = Math.Clamp((int)MathF.Floor(y), 1, worldMap.MaxTileY - 1);
            simulation.Units.Position[unit] = new Vector3(
                tileX + 0.5f,
                tileY + 0.5f,
                worldMap.GetSurfaceHeight(tileX, tileY));
            simulation.Units.Velocity[unit] = Vector3.Zero;
            simulation.Units.ViewRange[unit] = VisionStressRange;
            simulation.Units.FieldOfView[unit] = 180f;
            simulation.AI.Store.InitializeUnit(unit);
            simulation.AI.InitializeUnit(unit);
            simulation.MeleeCombat.InitializeUnit(unit, id);
            RestoreHealth(simulation, unit);
            simulation.Suppression.ClearUnit(unit);
            simulation.SetHeadNormal(id, Vector3.UnitX);
            simulation.SetPosture(id, UnitPosture.Standing);
            simulation.SetTarget(id, simulation.Units.Position[unit]);
        }
    }

    private static void GetMeleeFormationPosition(
        WorldMap worldMap,
        int side,
        int localIndex,
        out float x,
        out float y)
    {
        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;
        int row = localIndex / MeleeFormationColumns;
        int column = localIndex % MeleeFormationColumns;
        float leftX = centerX + MeleeBattleOffsetX - MeleeBattleGap;

        x = side == 1
            ? centerX + MeleeBattleOffsetX + row * MeleeFormationSpacing
            : leftX - row * MeleeFormationSpacing;
        y = centerY + MeleeBattleOffsetY -
            (MeleeFormationColumns - 1) * MeleeFormationSpacing * 0.5f +
            column * MeleeFormationSpacing;
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
        ConfigureCombatModes(simulation, unit, unit);
    }

    private static void ConfigureCombatModes(
        UnitSimulation simulation,
        int unitIndex,
        int rosterIndex)
    {
        int stateIndex = UnitWeaponStore.GetIndex(
            unitIndex,
            UnitWeaponSlot.Primary);

        // Keep the mass-battle test representative: every third rifle uses
        // one of CE's three aim styles and a corresponding fire mode.
        switch (rosterIndex % 3)
        {
            case 0:
                simulation.Weapons.CurrentAimMode[stateIndex] = AimMode.SuppressFire;
                simulation.Weapons.CurrentFireMode[stateIndex] = FireMode.Auto;
                break;

            case 1:
                simulation.Weapons.CurrentAimMode[stateIndex] = AimMode.Snapshot;
                simulation.Weapons.CurrentFireMode[stateIndex] = FireMode.Burst;
                break;

            default:
                simulation.Weapons.CurrentAimMode[stateIndex] = AimMode.AimedShot;
                simulation.Weapons.CurrentFireMode[stateIndex] = FireMode.Single;
                break;
        }
    }

    private void UpdateStats(
        UnitSimulation simulation)
    {
        int blue = 0;
        int red = 0;
        int third = 0;

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
            else if (simulation.Units.FactionTag[unit] == 3 &&
                     simulation.Health.OverallHitPoints[unit] > 0f)
            {
                third++;
            }
        }

        AliveBlue = blue;
        AliveRed = red;
        AliveThirdFaction = third;
    }
}
