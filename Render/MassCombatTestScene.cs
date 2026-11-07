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
            $"LOS 26m | Broken relief | " +
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

                // Low-frequency relief gives the battle area broad, uneven shoulders.
                float roughness =
                    0.95f * MathF.Sin(dx * 0.105f + MathF.Sin(dy * 0.061f) * 0.8f) +
                    0.85f * MathF.Cos(dy * 0.137f - dx * 0.043f) +
                    0.48f * MathF.Sin((dx + dy) * 0.29f) *
                    MathF.Cos((dx - dy) * 0.21f) +
                    0.28f * MathF.Sin(x * 0.71f) * MathF.Cos(y * 0.63f);

                float height = BaseTerrainHeight + roughness;

                // Broken diagonal ridge: several high shoulders with low saddles between them.
                float ridgeOneLine =
                    dx * 0.34f +
                    MathF.Sin(dx * 0.075f) * 5f +
                    MathF.Sin(dy * 0.11f) * 2f - 4f;
                float ridgeOneDistance = MathF.Abs(dy - ridgeOneLine);
                float ridgeOneGate =
                    0.16f + 0.84f * MathF.Abs(MathF.Sin(dx * 0.11f + dy * 0.035f));
                float ridgeOne =
                    MathF.Max(0f, 4.8f - ridgeOneDistance * 0.95f) * ridgeOneGate;
                height += ridgeOne;

                // A second ridge crosses the first at an angle, creating blind corners.
                float ridgeTwoLine =
                    -dx * 0.41f +
                    MathF.Sin(dx * 0.082f + 1.6f) * 4.5f +
                    MathF.Sin(dy * 0.09f) * 1.8f + 9f;
                float ridgeTwoDistance = MathF.Abs(dy - ridgeTwoLine);
                float ridgeTwoGate =
                    0.12f + 0.88f * MathF.Abs(MathF.Cos(dx * 0.095f - dy * 0.03f + 0.6f));
                float ridgeTwo =
                    MathF.Max(0f, 4.1f - ridgeTwoDistance * 0.88f) * ridgeTwoGate;
                height += ridgeTwo;

                // Short broken spurs rise from the middle instead of forming one clean wall.
                float ridgeThreeLine = 2f + MathF.Sin(dy * 0.12f) * 4.2f;
                float ridgeThreeDistance = MathF.Abs(dx - ridgeThreeLine);
                float ridgeThreeGate =
                    0.20f + 0.80f * MathF.Abs(MathF.Sin(dy * 0.145f + 0.9f));
                float ridgeThree =
                    MathF.Max(0f, 3.4f - ridgeThreeDistance * 0.9f) * ridgeThreeGate;
                height += ridgeThree;

                // Winding shallow ravines cut through the ridges and provide flank routes.
                float gullyCenterY =
                    dx * 0.24f +
                    MathF.Sin(dx * 0.067f + 0.5f) * 6f -
                    8f;
                float gullyDistance = MathF.Abs(dy - gullyCenterY);
                height -= MathF.Max(0f, 3.0f - gullyDistance * 0.64f);

                float crossGullyCenterX =
                    -5f +
                    dy * 0.28f +
                    MathF.Sin(dy * 0.085f) * 4f;
                float crossGullyDistance = MathF.Abs(dx - crossGullyCenterX);
                height -= MathF.Max(0f, 2.1f - crossGullyDistance * 0.72f);

                // Isolated knolls break sight lines without becoming impassable cliffs.
                float knolls =
                    GaussianHill(dx, dy, -34f, -23f, 8.5f, 3.0f) +
                    GaussianHill(dx, dy, 29f, -27f, 10f, 3.5f) +
                    GaussianHill(dx, dy, -39f, 25f, 9f, 2.8f) +
                    GaussianHill(dx, dy, 37f, 21f, 8f, 3.1f);
                height += knolls;

                // A shallow crater and its broken rim create a small close-quarters basin.
                float craterX = (dx + 19f) / 11f;
                float craterY = (dy - 17f) / 8f;
                float craterDistance = MathF.Sqrt(craterX * craterX + craterY * craterY);
                if (craterDistance < 0.62f)
                {
                    height -= 2.2f * (1f - craterDistance / 0.62f);
                }
                else if (craterDistance < 1.08f)
                {
                    float rim = 1f - MathF.Abs(craterDistance - 0.84f) / 0.24f;
                    height += 2.0f * MathF.Max(0f, rim);
                }

                // Keep every slope traversable; height differences, not blockers, occlude vision.
                height = Math.Clamp(height, 3.5f, 19f);

                float rockScore = ridgeOne + ridgeTwo + ridgeThree + knolls * 0.35f;
                ushort material = rockScore > 2.5f ? (ushort)2 : (ushort)1;
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
        simulation.Units.ViewRange[id.Index] = 26f;

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
