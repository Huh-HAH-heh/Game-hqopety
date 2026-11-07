using System;
using System.Numerics;
using Core.Items;
using Core.Map;
using Core.Unit;

namespace RimClone.Render;

public sealed class MassCombatTestScene
{
    private const int UnitCountPerFaction = 80;
    private const int TotalUnitCount = UnitCountPerFaction * 2;
    private const float BaseTerrainHeight = 8f;

    private readonly UnitId[] _units =
        new UnitId[TotalUnitCount];

    private readonly byte[] _side =
        new byte[TotalUnitCount];

    private bool _initialized;
    private float _elapsed;
    private long _hitsAtReset;

    public int UnitCount =>
        TotalUnitCount;

    public bool Initialized =>
        _initialized;

    public UnitId FirstUnit =>
        _units[0];

    public int AliveUnits { get; private set; }

    public int AliveBlue { get; private set; }

    public int AliveRed { get; private set; }

    public float Elapsed =>
        _elapsed;

    public long Hits { get; private set; }

    public void Start(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        if (!_initialized)
        {
            BuildTerrain(worldMap);
            SpawnUnits(simulation, worldMap);
            _initialized = true;
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

        _elapsed += MathF.Max(0f, deltaTime);
        UpdateStats(simulation);
    }

    public string GetStatus(
        UnitSimulation simulation)
    {
        UpdateStats(simulation);

        return
            $"MASS {TotalUnitCount} | " +
            $"Blue {AliveBlue} | " +
            $"Red {AliveRed} | " +
            $"Alive {AliveUnits} | " +
            $"RUINED CITY | Walls, rubble, craters | " +
            $"Projectiles {simulation.Projectiles.ActiveCount} | " +
            $"Hits {simulation.Projectiles.TotalHits - _hitsAtReset} | " +
            $"Time {_elapsed:0.0}s";
    }

    public void Stop(UnitSimulation simulation)
    {
        if (_initialized)
        {
            for (int i = 0; i < _units.Length; i++)
            {
                if (simulation.Units.TryGetIndex(_units[i], out _))
                    simulation.Destroy(_units[i]);

                _units[i] = default;
                _side[i] = 0;
            }
        }

        simulation.Projectiles.Clear();
        _initialized = false;
        _elapsed = 0f;
        _hitsAtReset = simulation.Projectiles.TotalHits;
        AliveUnits = 0;
        AliveBlue = 0;
        AliveRed = 0;
    }

    private void BuildTerrain(
        WorldMap worldMap)
    {
        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;
        int minX = Math.Max(4, centerX - 64);
        int maxX = Math.Min(worldMap.MaxTileX - 4, centerX + 64);
        int minY = Math.Max(4, centerY - 48);
        int maxY = Math.Min(worldMap.MaxTileY - 4, centerY + 48);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - centerX;
                float dy = y - centerY;

                // Broken street grid: roads bend, narrow and reconnect at offset junctions.
                float streetYNorth =
                    -29f + MathF.Sin(dx * 0.052f + 0.3f) * 4.8f +
                    MathF.Sin(dx * 0.13f) * 1.2f;
                float streetYMain =
                    1.5f + MathF.Sin(dx * 0.047f + 0.8f) * 5.5f +
                    MathF.Sin(dx * 0.15f + 1.4f) * 1.3f;
                float streetYSouth =
                    29f + MathF.Sin(dx * 0.045f + 2.1f) * 4.2f;

                float streetXWest =
                    -46f + MathF.Sin(dy * 0.055f + 0.7f) * 3.5f;
                float streetXMidWest =
                    -15f + MathF.Sin(dy * 0.061f + 1.4f) * 3.0f;
                float streetXMidEast =
                    17f + MathF.Sin(dy * 0.052f + 2.0f) * 3.6f;
                float streetXEast =
                    47f + MathF.Sin(dy * 0.048f + 0.4f) * 3.2f;

                float streetDistance = MathF.Min(
                    MathF.Min(
                        MathF.Min(
                            MathF.Abs(dy - streetYNorth),
                            MathF.Abs(dy - streetYMain)),
                        MathF.Abs(dy - streetYSouth)),
                    MathF.Min(
                        MathF.Min(
                            MathF.Abs(dx - streetXWest),
                            MathF.Abs(dx - streetXMidWest)),
                        MathF.Min(
                            MathF.Abs(dx - streetXMidEast),
                            MathF.Abs(dx - streetXEast))));

                // Streets sit lower than the broken building plots around them.
                float roadCut = 1f - SmoothStep(2.1f, 5.4f, streetDistance);
                float height =
                    BaseTerrainHeight +
                    0.55f * MathF.Sin(dx * 0.16f + MathF.Sin(dy * 0.08f)) +
                    0.42f * MathF.Cos(dy * 0.19f - dx * 0.04f) -
                    roadCut * 1.25f;

                // Surviving shells of apartment blocks. Their walls have gaps and
                // collapsed sections; rubble aprons turn the edges into walkable ramps.
                height += RuinedBuildingHeight(dx, dy, -32f, -15f, 9.5f, 7f, 0.12f, 0.4f);
                height += RuinedBuildingHeight(dx, dy, -1f, -16f, 8f, 7.5f, -0.16f, 1.7f);
                height += RuinedBuildingHeight(dx, dy, 31f, -15f, 9f, 7f, 0.10f, 2.8f);
                height += RuinedBuildingHeight(dx, dy, -45f, 14f, 8f, 8.5f, -0.18f, 3.9f);
                height += RuinedBuildingHeight(dx, dy, -26f, 38f, 10f, 6.5f, 0.18f, 5.1f);
                height += RuinedBuildingHeight(dx, dy, 7f, 37f, 9f, 7f, -0.13f, 6.2f);
                height += RuinedBuildingHeight(dx, dy, 39f, 36f, 10f, 7.5f, 0.16f, 7.3f);
                height += RuinedBuildingHeight(dx, dy, 48f, -2f, 7f, 8f, -0.22f, 8.4f);

                // Cross-street debris and collapsed facades create staggered choke points,
                // but leave several gaps so a squad can flank instead of hitting a wall.
                float debrisOne = GaussianHill(dx, dy, -11f, -2f, 4.2f, 2.1f);
                float debrisTwo = GaussianHill(dx, dy, 14f, 5f, 5.2f, 2.5f);
                float debrisThree = GaussianHill(dx, dy, -39f, -2f, 4.8f, 2.0f);
                float debrisFour = GaussianHill(dx, dy, 31f, 12f, 5.5f, 2.2f);
                height += debrisOne + debrisTwo + debrisThree + debrisFour;

                // Bomb craters with broken, non-circular rims.
                height += CraterRelief(dx, dy, -18f, 18f, 8.5f, 6.5f, 0.3f);
                height += CraterRelief(dx, dy, 22f, -35f, 7.5f, 5.5f, 1.8f);
                height += CraterRelief(dx, dy, 51f, 20f, 6f, 8f, 2.9f);

                // Keep slopes climbable for the movement system; silhouettes and
                // LOS are broken by 2-5 m ruin heights, not by navigation blockers.
                height = Math.Clamp(height, 3.5f, 17f);

                bool isRubble = roadCut < 0.55f &&
                    (debrisOne + debrisTwo + debrisThree + debrisFour > 0.45f ||
                     height > BaseTerrainHeight + 2.0f);
                ushort material = isRubble ? (ushort)2 : (ushort)1;
                ushort heightUnits = checked(
                    (ushort)MathF.Round(height * WorldMap.HeightUnitsPerMeter));

                worldMap.SetSolidHeight(
                    x,
                    y,
                    heightUnits,
                    material);
            }
        }

        ClearTestWater(
            worldMap,
            minX,
            maxX,
            minY,
            maxY);
    }

    private static float RuinedBuildingHeight(
        float dx,
        float dy,
        float centerX,
        float centerY,
        float halfWidth,
        float halfDepth,
        float rotation,
        float seed)
    {
        float offsetX = dx - centerX;
        float offsetY = dy - centerY;
        float sin = MathF.Sin(rotation);
        float cos = MathF.Cos(rotation);
        float localX = offsetX * cos + offsetY * sin;
        float localY = -offsetX * sin + offsetY * cos;

        float edgeX = MathF.Abs(localX) - halfWidth;
        float edgeY = MathF.Abs(localY) - halfDepth;
        float outsideX = MathF.Max(0f, edgeX);
        float outsideY = MathF.Max(0f, edgeY);
        float signedDistance =
            MathF.Sqrt(outsideX * outsideX + outsideY * outsideY) +
            MathF.Min(MathF.Max(edgeX, edgeY), 0f);

        // A broken wall ring, with some full-height stubs and several collapsed gaps.
        float tangent = MathF.Abs(edgeX) > MathF.Abs(edgeY) ? localY : localX;
        float wallGaps =
            0.18f + 0.82f * MathF.Abs(MathF.Sin(tangent * 0.31f + seed));
        float wallBand =
            1f - SmoothStep(0.45f, 2.8f, MathF.Abs(signedDistance));
        float walls = 4.1f * wallBand * wallGaps;

        // Sloped rubble aprons connect the remaining walls to street level.
        float apron =
            1.55f * MathF.Exp(-(signedDistance * signedDistance) / 13f);

        // Inside the footprint only broken piles remain; there is no intact roof.
        float interior = 1f - SmoothStep(-1.5f, 1.8f, signedDistance);
        float collapsedRoof =
            interior * (
                GaussianHill(localX, localY, -halfWidth * 0.32f, -halfDepth * 0.15f, 3.8f, 2.0f) +
                GaussianHill(localX, localY, halfWidth * 0.30f, halfDepth * 0.34f, 3.2f, 1.7f));

        return walls + apron + collapsedRoof;
    }

    private static float CraterRelief(
        float dx,
        float dy,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float seed)
    {
        float nx = (dx - centerX) / radiusX;
        float ny = (dy - centerY) / radiusY;
        float angle = MathF.Atan2(ny, nx);
        float roughRadius =
            MathF.Sqrt(nx * nx + ny * ny) +
            0.09f * MathF.Sin(angle * 5f + seed) +
            0.06f * MathF.Cos(angle * 3f - seed);

        if (roughRadius < 0.62f)
            return -2.0f * (1f - roughRadius / 0.62f);

        if (roughRadius < 1.05f)
        {
            float rim = 1f - MathF.Abs(roughRadius - 0.82f) / 0.23f;
            return 1.7f * MathF.Max(0f, rim);
        }

        return 0f;
    }

    private static float GaussianHill(
        float dx,
        float dy,
        float centerX,
        float centerY,
        float radius,
        float height)
    {
        float offsetX = dx - centerX;
        float offsetY = dy - centerY;
        float distanceSquared = offsetX * offsetX + offsetY * offsetY;
        return height * MathF.Exp(-distanceSquared / (2f * radius * radius));
    }

    private static float SmoothStep(float min, float max, float value)
    {
        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static void ClearTestWater(
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

        int index = 0;

        for (int side = 0; side < 2; side++)
        {
            ushort faction =
                side == 0
                    ? (ushort)1
                    : (ushort)2;

            for (int row = 0;
                 row < 10;
                 row++)
            {
                for (int column = 0;
                     column < 8;
                     column++)
                {
                    float baseX =
                        side == 0
                            ? centerX - 18f
                            : centerX + 2f;

                    float x =
                        baseX +
                        column * 2.0f;

                    float y =
                        centerY -
                        9f +
                        row * 2.0f;

                    AddUnit(
                        simulation,
                        worldMap,
                        index,
                        side,
                        faction,
                        new Vector3(
                            x,
                            y,
                            0f));

                    index++;
                }
            }
        }
    }

    private void AddUnit(
        UnitSimulation simulation,
        WorldMap worldMap,
        int testIndex,
        int side,
        ushort faction,
        Vector3 requestedPosition)
    {
        int tileX =
            Math.Clamp(
                (int)MathF.Floor(
                    requestedPosition.X),
                1,
                worldMap.MaxTileX - 1);

        int tileY =
            Math.Clamp(
                (int)MathF.Floor(
                    requestedPosition.Y),
                1,
                worldMap.MaxTileY - 1);

        float z =
            worldMap.GetSurfaceHeight(
                tileX,
                tileY);

        Vector3 position =
            new Vector3(
                tileX + 0.5f,
                tileY + 0.5f,
                z);

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

        _units[testIndex] = id;
        _side[testIndex] = (byte)side;

        RangedWeaponConfig weapon =
            (testIndex % 3) switch
            {
                0 => WeaponCatalog.PulseRepeater,
                1 => WeaponCatalog.LaserCarbine,
                _ => WeaponCatalog.Railgun
            };

        int weaponSlot =
            simulation.AddInventoryItem(
                id,
                weapon);

        if (weaponSlot >= 0)
        {
            simulation.EquipWeapon(
                id,
                weaponSlot,
                UnitWeaponSlot.Primary);
        }

        if (testIndex % 3 == 0)
        {
            ArmorConfig armor =
                new ArmorConfig
                {
                    Name = "MassTest Armor",
                    Coverage =
                        ArmorCoverage.Torso |
                        ArmorCoverage.Head,
                    ProtectedZone = 1,
                    ArmorRating = 48f,
                    SharpRating = 48f,
                    BluntRating = 24f,
                    HeatRating = 20f,
                    PenetrationResistance = 48f,
                    EnergyLoss = 180f,
                    ArmorDamageCoefficient = 0.25f,
                    DamageAbsorption = 0.35f,
                    HardArmorDamageFactor = 0.5f,
                    Weight = 7f,
                    Layer = ArmorLayer.Outer,
                    WorldColorR = 112,
                    WorldColorG = 119,
                    WorldColorB = 128,
                    WorldRenderPadding = 0.12f
                };

            int armorSlot =
                simulation.AddInventoryItem(
                    id,
                    armor,
                    maxDurability: 100f);

            if (armorSlot >= 0)
            {
                simulation.EquipArmor(
                    id,
                    armorSlot);
            }
        }

        if (testIndex % 11 == 0)
        {
            simulation.SetPosture(
                id,
                UnitPosture.Crouching);
        }

        SetCombatTarget(
            simulation,
            id,
            side);
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

        for (int i = 0;
             i < _units.Length;
             i++)
        {
            if (!simulation.Units.TryGetIndex(
                    _units[i],
                    out int index))
            {
                continue;
            }

            int side =
                _side[i];

            int row =
                (i % UnitCountPerFaction) / 8;

            int column =
                i % 8;

            float baseX =
                side == 0
                    ? centerX - 18f
                    : centerX + 2f;

            float x =
                baseX +
                column * 2f;

            float y =
                centerY -
                9f +
                row * 2f;

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

            simulation.Units.Position[index] =
                new Vector3(
                    tileX + 0.5f,
                    tileY + 0.5f,
                    worldMap.GetSurfaceHeight(
                        tileX,
                        tileY));

            simulation.Units.Velocity[index] =
                Vector3.Zero;

            simulation.Units.HasTarget[index] =
                false;

            simulation.Units.HeadNormal[index] =
                side == 0
                    ? new Vector3(
                        1f,
                        0f,
                        0f)
                    : new Vector3(
                        -1f,
                        0f,
                        0f);

            simulation.SetFactionTag(
                _units[i],
                side == 0
                    ? (ushort)1
                    : (ushort)2);

            simulation.SetPosture(
                _units[i],
                i % 11 == 0
                    ? UnitPosture.Crouching
                    : UnitPosture.Standing);

            RestoreHealth(
                simulation,
                index);

            simulation.Suppression.ClearUnit(
                index);

            simulation.AI.Store.InitializeUnit(
                index);

            RestoreWeapon(
                simulation,
                index);

            SetCombatTarget(
                simulation,
                _units[i],
                side);
        }
    }

    private static void RestoreHealth(
        UnitSimulation simulation,
        int unitIndex)
    {
        simulation.Health.OverallHitPoints[unitIndex] =
            simulation.Health.OverallMaxHitPoints[unitIndex];

        int start =
            unitIndex *
            simulation.Health.MaxParts;

        int count =
            simulation.Health.PartCount[unitIndex];

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
        int unitIndex)
    {
        for (int slot = 0;
             slot < UnitInventoryStore.WeaponSlotCount;
             slot++)
        {
            short inventorySlot =
                simulation.Inventory.GetWeaponEquipment(
                    unitIndex,
                    (UnitWeaponSlot)slot);

            if (inventorySlot < 0)
                continue;

            if (simulation.Inventory.GetItem(
                    unitIndex,
                    inventorySlot) is WeaponConfig weapon)
            {
                simulation.Weapons.ConfigureSlot(
                    unitIndex,
                    (UnitWeaponSlot)slot,
                    weapon);

                simulation.Weapons.AddReserveAmmo(
                    unitIndex,
                    (UnitWeaponSlot)slot,
                    500);
            }
        }
    }

    private static void SetCombatTarget(
        UnitSimulation simulation,
        UnitId id,
        int side)
    {
        simulation.SetHeadNormal(
            id,
            side == 0
                ? new Vector3(
                    1f,
                    0f,
                    0f)
                : new Vector3(
                    -1f,
                    0f,
                    0f));
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

            if (unit >= simulation.Health.Capacity ||
                simulation.Health.OverallHitPoints[unit] <= 0f)
            {
                continue;
            }

            if (simulation.Units.FactionTag[unit] == 1)
                blue++;
            else if (simulation.Units.FactionTag[unit] == 2)
                red++;
        }

        AliveBlue = blue;
        AliveRed = red;
        AliveUnits = blue + red;
        Hits = simulation.Projectiles.TotalHits - _hitsAtReset;
        if (_elapsed <= 0.001f)
            _hitsAtReset = simulation.Projectiles.TotalHits;
    }
}
