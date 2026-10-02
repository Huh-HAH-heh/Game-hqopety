using System;
using System.Numerics;
using Core.Map;
using Core.Unit;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

public sealed class VisionTestScene
{
    private const float BaseHeight = 10f;
    private const float FullWallHeight = 14f;
    private const float LowWallHeight = 11.40f;

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
            centerX - 11;

        int fullWallMaxX =
            centerX - 9;

        int fullWallMinY =
            centerY - 4;

        int fullWallMaxY =
            centerY + 4;

        SetWall(
            worldMap,
            fullWallMinX,
            fullWallMaxX,
            fullWallMinY,
            fullWallMaxY,
            FullWallHeight);

        int lowWallMinX =
            centerX - 11;

        int lowWallMaxX =
            centerX - 9;

        int lowWallMinY =
            centerY + 6;

        int lowWallMaxY =
            centerY + 10;

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
                forward);

        _units[1] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 5f,
                    centerY - 8f,
                    BaseHeight),
                forward,
                forward);

        _units[2] =
            simulation.Spawn(
                UnitType.Greenbob,
                new Vector3(
                    centerX - 6f,
                    centerY - 6f,
                    BaseHeight),
                forward,
                forward);

        _units[3] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 4f,
                    centerY,
                    BaseHeight),
                forward,
                forward);

        _units[4] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 4f,
                    centerY + 8f,
                    BaseHeight),
                forward,
                forward);

        _units[5] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX - 15f,
                    centerY + 12f,
                    BaseHeight),
                forward,
                forward);

        _units[6] =
            simulation.Spawn(
                UnitType.Colonist,
                new Vector3(
                    centerX + 5f,
                    centerY,
                    BaseHeight),
                forward,
                forward);

        _units[7] =
            simulation.Spawn(
                UnitType.SegmentedMonster,
                new Vector3(
                    centerX - 11f,
                    centerY - 9f,
                    BaseHeight),
                forward,
                forward);

        BuildObstacleDebug(
            centerX,
            centerY,
            tilePixelSize);
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
