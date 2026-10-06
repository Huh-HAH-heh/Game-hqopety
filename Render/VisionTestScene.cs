using System;
using System.Numerics;
using Core.Items;
using Core.Map;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class VisionTestScene
{
    private const float BaseHeight = 10f;
    private const float FullWallHeight = 14f;
    private const float LowWallHeight = 14f;

    private readonly UnitId[] _units =
        new UnitId[8];

    private readonly VertexArray _obstacles =
        new VertexArray(
            PrimitiveType.Lines);

    public int UnitCount =>
        _units.Length;

    public UnitId Observer =>
        _units[0];

    public ReadOnlySpan<UnitId> Units =>
        _units.AsSpan();

    public void Setup(
        WorldMap worldMap,
        UnitSimulation simulation,
        float tilePixelSize)
    {
        int centerX =
            worldMap.TileWidth / 2;

        int centerY =
            worldMap.TileHeight / 2;

        int minX =
            Math.Max(
                2,
                centerX - 48);

        int maxX =
            Math.Min(
                worldMap.MaxTileX - 2,
                centerX + 38);

        int minY =
            Math.Max(
                2,
                centerY - 34);

        int maxY =
            Math.Min(
                worldMap.MaxTileY - 2,
                centerY + 34);

        FlattenArea(
            worldMap,
            minX,
            maxX,
            minY,
            maxY);

        ClearWaterArea(
            worldMap,
            minX,
            maxX,
            minY,
            maxY);

        int fullWallMinX =
            centerX - 13;

        int fullWallMaxX =
            centerX - 11;

        int fullWallMinY =
            centerY - 8;

        int fullWallMaxY =
            centerY - 5;

        SetWall(
            worldMap,
            fullWallMinX,
            fullWallMaxX,
            fullWallMinY,
            fullWallMaxY,
            FullWallHeight);

        int lowWallMinX =
            centerX + 10;

        int lowWallMaxX =
            centerX + 12;

        int lowWallMinY =
            centerY + 5;

        int lowWallMaxY =
            centerY + 8;

        SetWall(
            worldMap,
            lowWallMinX,
            lowWallMaxX,
            lowWallMinY,
            lowWallMaxY,
            LowWallHeight);

        Vector3 forward =
            new Vector3(
                1f,
                0f,
                0f);

        _units[0] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 20f,
                    centerY,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[1] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 5f,
                    centerY - 8f,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[2] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 6f,
                    centerY - 6f,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[3] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 4f,
                    centerY,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[4] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 4f,
                    centerY + 12f,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[5] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 15f,
                    centerY + 12f,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[6] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX + 5f,
                    centerY,
                    BaseHeight),
                forward,
                forward,
                factionTag: 1);

        _units[7] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 11f,
                    centerY - 9f,
                    BaseHeight),
                forward,
                forward,
                factionTag: 2);

        simulation.SetFactionTag(
            _units[4],
            2);

        simulation.SetFactionTag(
            _units[5],
            2);

        simulation.SetFactionTag(
            _units[6],
            2);

        simulation.SetFactionTag(
            _units[7],
            2);

        SetAiTestPositions(
            simulation,
            centerX,
            centerY);

        BuildObstacleDebug(
            centerX,
            centerY,
            tilePixelSize);
    }

    private float _aiDemoResetTimer;

    public void UpdateAiDemo(
        UnitSimulation simulation,
        WorldMap worldMap,
        float deltaTime)
    {
        if (!simulation.AI.Enabled ||
            deltaTime <= 0f)
        {
            return;
        }

        int teamOneAlive =
            CountAlive(
                simulation,
                1);

        int teamTwoAlive =
            CountAlive(
                simulation,
                2);

        if (teamOneAlive > 0 &&
            teamTwoAlive > 0)
        {
            _aiDemoResetTimer = 0f;
            return;
        }

        _aiDemoResetTimer +=
            deltaTime;

        if (_aiDemoResetTimer >= 3f)
        {
            ResetAiDemo(
                simulation,
                worldMap);
        }
    }

    public string GetAiDemoStage()
    {
        return "BATTLE";
    }

    public int GetAliveCount(
        UnitSimulation simulation,
        ushort factionTag)
    {
        return CountAlive(
            simulation,
            factionTag);
    }

    public void ResetAiDemo(
        UnitSimulation simulation,
        WorldMap worldMap)
    {
        _aiDemoResetTimer = 0f;

        SetAiTestPositions(
            simulation,
            worldMap.TileWidth / 2,
            worldMap.TileHeight / 2);

        for (int i = 0;
             i < _units.Length;
             i++)
        {
            if (!simulation.Units.TryGetIndex(
                    _units[i],
                    out int unitIndex))
            {
                continue;
            }

            simulation.SetFactionTag(
                _units[i],
                i < 4
                    ? (ushort)1
                    : (ushort)2);

            simulation.AI.Store.InitializeUnit(
                unitIndex);

            RestoreBattleHealth(
                simulation,
                unitIndex);

            RestoreBattleWeapon(
                simulation,
                unitIndex);
        }
    }

    private static void ApplyLowHealth(
        UnitSimulation simulation,
        UnitId id)
    {
        if (!simulation.Units.TryGetIndex(
                id,
                out int index))
        {
            return;
        }

        simulation.Health.OverallHitPoints[index] = 20f;

        if (simulation.Health.TryFindPart(
                index,
                UnitHealthPartId.Torso,
                out int torso))
        {
            simulation.Health.HitPoints[torso] = 10f;
        }
    }

    private static void SetFullHealth(
        UnitSimulation simulation,
        UnitId id)
    {
        if (!simulation.Units.TryGetIndex(
                id,
                out int index))
        {
            return;
        }

        simulation.Health.OverallHitPoints[index] =
            simulation.Health.OverallMaxHitPoints[index];

        if (simulation.Health.TryFindPart(
                index,
                UnitHealthPartId.Torso,
                out int torso))
        {
            simulation.Health.HitPoints[torso] =
                simulation.Health.MaxHitPoints[torso];
        }
    }

    private static void RestoreBattleHealth(
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
            int partIndex =
                start + i;

            simulation.Health.HitPoints[partIndex] =
                simulation.Health.MaxHitPoints[partIndex];
        }
    }

    private static void RestoreBattleWeapon(
        UnitSimulation simulation,
        int unitIndex)
    {
        short inventorySlot =
            simulation.Inventory.GetWeaponEquipment(
                unitIndex,
                UnitWeaponSlot.Primary);

        if (inventorySlot < 0)
            return;

        if (simulation.Inventory.GetItem(
                unitIndex,
                inventorySlot) is not WeaponConfig weapon)
        {
            return;
        }

        simulation.Weapons.ConfigureSlot(
            unitIndex,
            UnitWeaponSlot.Primary,
            weapon);

        // The battle demo is about combat, not ammunition logistics.
        simulation.Weapons.AddReserveAmmo(
            unitIndex,
            UnitWeaponSlot.Primary,
            1_000_000);
    }

    private void SetAiTestPositions(
        UnitSimulation simulation,
        int centerX,
        int centerY)
    {
        SetPosition(
            simulation,
            _units[0],
            centerX - 8f,
            centerY - 4.5f);

        SetPosition(
            simulation,
            _units[1],
            centerX - 8f,
            centerY - 1.5f);

        SetPosition(
            simulation,
            _units[2],
            centerX - 8f,
            centerY + 1.5f);

        SetPosition(
            simulation,
            _units[3],
            centerX - 8f,
            centerY + 4.5f);

        SetPosition(
            simulation,
            _units[4],
            centerX + 8f,
            centerY - 4.5f);

        SetPosition(
            simulation,
            _units[5],
            centerX + 8f,
            centerY - 1.5f);

        SetPosition(
            simulation,
            _units[6],
            centerX + 8f,
            centerY + 1.5f);

        SetPosition(
            simulation,
            _units[7],
            centerX + 8f,
            centerY + 4.5f);

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

            simulation.Units.Velocity[index] =
                Vector3.Zero;

            simulation.Units.HasTarget[index] =
                false;

            simulation.Units.HeadNormal[index] =
                i == 4 || i == 7
                    ? new Vector3(
                        -1f,
                        0f,
                        0f)
                    : new Vector3(
                        1f,
                        0f,
                        0f);
        }
    }

    private static void SetPosition(
        UnitSimulation simulation,
        UnitId id,
        float x,
        float y)
    {
        if (!simulation.Units.TryGetIndex(
                id,
                out int index))
        {
            return;
        }

        simulation.Units.Position[index] =
            new Vector3(
                x,
                y,
                simulation.Units.Position[index].Z);

        simulation.Units.Velocity[index] =
            Vector3.Zero;
        simulation.Units.HasTarget[index] =
            false;
    }

    private static int CountAlive(
        UnitSimulation simulation,
        ushort factionTag)
    {
        int alive = 0;

        ReadOnlySpan<int> active =
            simulation.Units.ActiveIndices;

        for (int i = 0;
             i < active.Length;
             i++)
        {
            int unitIndex =
                active[i];

            if (simulation.Units.FactionTag[unitIndex] !=
                factionTag)
            {
                continue;
            }

            if (simulation.Health.OverallHitPoints[unitIndex] >
                0f)
            {
                alive++;
            }
        }

        return alive;
    }

    public UnitId GetUnit(
        int index)
    {
        return _units[
            Math.Clamp(
                index,
                0,
                _units.Length - 1)];
    }

    public void DrawDebug(
        RenderWindow window)
    {
        if (_obstacles.VertexCount <= 0)
            return;

        window.Draw(
            _obstacles);
    }

    private void BuildObstacleDebug(
        int centerX,
        int centerY,
        float tilePixelSize)
    {
        _obstacles.Clear();

        AppendRectangle(
            _obstacles,
            (centerX - 11f) *
            tilePixelSize,
            (centerY - 4f) *
            tilePixelSize,
            (centerX - 9f) *
            tilePixelSize,
            (centerY + 4f) *
            tilePixelSize,
            new Color(
                250,
                90,
                90,
                210));

        AppendRectangle(
            _obstacles,
            (centerX - 11f) *
            tilePixelSize,
            (centerY + 6f) *
            tilePixelSize,
            (centerX - 9f) *
            tilePixelSize,
            (centerY + 10f) *
            tilePixelSize,
            new Color(
                250,
                205,
                80,
                210));
    }

    private static void FlattenArea(
        WorldMap worldMap,
        int minX,
        int maxX,
        int minY,
        int maxY)
    {
        ushort heightUnits =
            checked(
                (ushort)MathF.Round(
                    BaseHeight * 10f));

        for (int y = minY;
             y <= maxY;
             y++)
        {
            for (int x = minX;
                 x <= maxX;
                 x++)
            {
                worldMap.SetSolidHeight(
                    x,
                    y,
                    heightUnits,
                    1);
            }
        }
    }

    private static void ClearWaterArea(
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

    private static void SetWall(
        WorldMap worldMap,
        int minX,
        int maxX,
        int minY,
        int maxY,
        float height)
    {
        ushort heightUnits =
            checked(
                (ushort)MathF.Round(
                    height * 10f));

        for (int y = minY;
             y <= maxY;
             y++)
        {
            for (int x = minX;
                 x <= maxX;
                 x++)
            {
                worldMap.SetSolidHeight(
                    x,
                    y,
                    heightUnits,
                    2);
            }
        }
    }

    private static void AppendRectangle(
        VertexArray vertices,
        float minX,
        float minY,
        float maxX,
        float maxY,
        Color color)
    {
        vertices.Append(
            new Vertex(
                new Vector2f(
                    minX,
                    minY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    maxX,
                    minY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    maxX,
                    minY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    maxX,
                    maxY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    maxX,
                    maxY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    minX,
                    maxY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    minX,
                    maxY),
                color));

        vertices.Append(
            new Vertex(
                new Vector2f(
                    minX,
                    minY),
                color));
    }
}
