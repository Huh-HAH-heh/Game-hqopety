using System;
using System.Numerics;
using Core.Items;
using Core.Map;

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

    public int AliveUnits { get; private set; }

    public int AliveBlue { get; private set; }

    public int AliveRed { get; private set; }

    public float Elapsed =>
        _elapsed;

    public long Hits =>
        _lastHits;

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
                centerX - 64);

        int maxX =
            Math.Min(
                worldMap.MaxTileX - 4,
                centerX + 64);

        int minY =
            Math.Max(
                4,
                centerY - 48);

        int maxY =
            Math.Min(
                worldMap.MaxTileY - 4,
                centerY + 48);

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
                    BaseTerrainHeight;

                // Large central valley.
                float valley =
                    8f *
                    MathF.Exp(
                        -(dx * dx) /
                        (2f * 42f * 42f));

                height -= valley;

                // Two broad hills.
                height +=
                    8f *
                    MathF.Exp(
                        -(
                            (dx + 26f) *
                            (dx + 26f) +
                            dy * dy) /
                        (2f * 18f * 18f));

                height +=
                    10f *
                    MathF.Exp(
                        -(
                            (dx - 30f) *
                            (dx - 30f) +
                            (dy - 12f) *
                            (dy - 12f)) /
                        (2f * 20f * 20f));

                // Long diagonal ridge.
                float ridge =
                    MathF.Abs(
                        dy -
                        dx * 0.34f);

                if (ridge < 3.0f)
                {
                    height +=
                        7f *
                        (1f -
                         ridge / 3.0f);
                }

                // Broken side ridge with passes.
                float sideRidge =
                    MathF.Abs(dx + 8f);

                if (sideRidge < 2.5f)
                {
                    float pass =
                        MathF.Abs(
                            dy % 18f);

                    float factor =
                        pass < 3f
                            ? pass / 3f
                            : 1f;

                    height +=
                        6f *
                        factor;
                }

                // Small terrain noise creates uneven sight lines.
                height +=
                    1.4f *
                    MathF.Sin(
                        x * 0.31f) *
                    MathF.Cos(
                        y * 0.27f);

                // Keep the two deployment shelves traversable.
                if (MathF.Abs(dx) > 50f)
                {
                    height =
                        8.5f +
                        0.8f *
                        MathF.Sin(
                            y * 0.22f);
                }

                // Artificial fortified rocks / hard cover.
                if (IsCoverBlock(
                        x,
                        y,
                        centerX,
                        centerY))
                {
                    height += 7f;
                }

                ushort heightUnits =
                    checked(
                        (ushort)Math.Clamp(
                            MathF.Round(
                                MathF.Max(
                                    3f,
                                    height) *
                                10f),
                            0f,
                            ushort.MaxValue));

                ushort material =
                    IsCoverBlock(
                        x,
                        y,
                        centerX,
                        centerY)
                        ? (ushort)2
                        : (ushort)1;

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

    private static bool IsCoverBlock(
        int x,
        int y,
        int centerX,
        int centerY)
    {
        int dx = x - centerX;
        int dy = y - centerY;

        if (Math.Abs(dx - 28) <= 2 &&
            Math.Abs(dy) <= 10)
        {
            return true;
        }

        if (Math.Abs(dx + 24) <= 2 &&
            Math.Abs(dy - 18) <= 12)
        {
            return true;
        }

        if (Math.Abs(dx) <= 2 &&
            dy >= 10 &&
            dy <= 24)
        {
            return true;
        }

        return false;
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

        ItemConfig weapon =
            (testIndex % 5 == 0)
                ? WeaponCatalog.Pistol
                : WeaponCatalog.AssaultRifle;

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
                    HardArmorDamageFactor = 0.5f,
                    Weight = 7f,
                    Layer = ArmorLayer.Armor
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
        if (_elapsed <= 0.001f)
            _hitsAtReset = simulation.Projectiles.TotalHits;
    }
}
