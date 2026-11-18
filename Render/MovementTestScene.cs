using System;
using System.Numerics;
using Core.Map;
using Core.Unit;
using World;

namespace RimClone.Render;

/// <summary>
/// Dedicated no-enemy terrain course for checking physical route following
/// and per-unit footprint-aware A* movement without mass combat or autonomous AI.
/// </summary>
public sealed class MovementTestScene
{
    private const int UnitCount = 12;
    private const ushort GroundMaterialId = 1;
    private const ushort RockMaterialId = 2;

    private readonly UnitId[] _units = new UnitId[UnitCount];
    private bool _initialized;

    public bool Initialized => _initialized;
    public ReadOnlySpan<UnitId> Units => _units;
    public Vector3 InitialDestination { get; private set; }

    public void Start(UnitSimulation simulation, WorldMap worldMap)
    {
        if (_initialized)
            Stop(simulation);

        BuildTerrain(worldMap);

        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;

        for (int i = 0; i < _units.Length; i++)
        {
            int column = i % 4;
            int row = i / 4;
            float x = centerX - 66f + column * 1.35f;
            float y = centerY - 2.2f + row * 1.5f;
            int tileX = Math.Clamp((int)MathF.Floor(x), 0, worldMap.MaxTileX);
            int tileY = Math.Clamp((int)MathF.Floor(y), 0, worldMap.MaxTileY);
            Vector3 position = new(x, y, worldMap.GetSurfaceHeight(tileX, tileY));

            _units[i] = simulation.Spawn(
                UnitType.Colonist,
                position,
                bodyAngle: 0f,
                factionTag: 1);
        }

        float goalX = centerX + 66f;
        float goalY = centerY + 2f;
        int goalTileX = Math.Clamp((int)MathF.Floor(goalX), 0, worldMap.MaxTileX);
        int goalTileY = Math.Clamp((int)MathF.Floor(goalY), 0, worldMap.MaxTileY);
        InitialDestination = new Vector3(
            goalX,
            goalY,
            worldMap.GetSurfaceHeight(goalTileX, goalTileY));

        _initialized = true;
    }

    public void Stop(UnitSimulation simulation)
    {
        for (int i = 0; i < _units.Length; i++)
        {
            if (_units[i].IsValid)
                simulation.Destroy(_units[i]);
            _units[i] = default;
        }

        simulation.Projectiles.Clear();
        _initialized = false;
    }

    public string GetStatus() =>
        $"Курс A*: {_units.Length} колонистов без противников | гряды, овраги, террасы и узкие проходы";

    private static void BuildTerrain(WorldMap worldMap)
    {
        // This course represents obstacles through terrain height only.
        // Clear navigation-only blockers left by earlier test runs/scenarios:
        // they are not rendered and therefore appear as invisible walls.
        for (int y = 0; y < worldMap.TileHeight; y++)
        {
            for (int x = 0; x < worldMap.TileWidth; x++)
            {
                if (worldMap.IsNavigationBlocked(x, y))
                    worldMap.SetNavigationBlocked(x, y, false);
            }
        }

        worldMap.Water.Clear();

        int centerX = worldMap.TileWidth / 2;
        int centerY = worldMap.TileHeight / 2;
        const int blendWidth = 64;
        int courseMinX = Math.Max(3, centerX - 78);
        int courseMaxX = Math.Min(worldMap.MaxTileX - 3, centerX + 78);
        int courseMinY = Math.Max(3, centerY - 58);
        int courseMaxY = Math.Min(worldMap.MaxTileY - 3, centerY + 58);
        int minX = Math.Max(3, courseMinX - blendWidth);
        int maxX = Math.Min(worldMap.MaxTileX - 3, courseMaxX + blendWidth);
        int minY = Math.Max(3, courseMinY - blendWidth);
        int maxY = Math.Min(worldMap.MaxTileY - 3, courseMaxY + blendWidth);

        for (int y = minY; y <= maxY; y++)
        {
            float dy = y - centerY;
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - centerX;

                // Rolling ground with a broad diagonal drainage channel.
                float height = 3.2f;
                height += 1.25f * MathF.Sin(dx * 0.055f);
                height += 0.95f * MathF.Cos(dy * 0.072f);
                height += 0.65f * MathF.Sin((dx + dy) * 0.09f);

                float gullyCenterY = 13f + dx * 0.17f + MathF.Sin(dx * 0.045f) * 4.5f;
                float gullyDistance = MathF.Abs(dy - gullyCenterY);
                height -= 2.7f * MathF.Max(0f, 1f - gullyDistance / 4.5f);

                // Two staggered S-shaped ridges. Their lower passes are
                // encoded in the height profile, not hidden navigation blockers.
                float ridgeOneX = -14f + 7f * MathF.Sin(dy * 0.047f);
                float ridgeTwoX = 24f + 8f * MathF.Sin(dy * 0.038f + 1.1f);
                float ridgeOneDistance = MathF.Abs(dx - ridgeOneX);
                float ridgeTwoDistance = MathF.Abs(dx - ridgeTwoX);
                float ridgeOne = MathF.Max(0f, 10.5f - ridgeOneDistance * 1.05f);
                float ridgeTwo = MathF.Max(0f, 8.5f - ridgeTwoDistance * 0.95f);

                float northGate = GateOpening(dy, -25f, 8f);
                float southGate = GateOpening(dy, 27f, 9f);
                height += ridgeOne * (1f - northGate * 0.96f);
                height += ridgeTwo * (1f - southGate * 0.96f);

                // Uneven shelves on the eastern side and two rounded hillocks.
                if (dx > 31f)
                    height += MathF.Min(3f, MathF.Floor((dx - 31f) / 11f)) * 0.62f;

                height += GaussianHill(dx + 43f, dy - 18f, 3.5f, 11f);
                height += GaussianHill(dx - 48f, dy + 27f, 4.2f, 13f);

                height = Math.Clamp(height, 0.8f, 30f);

                float edgeDistance = MathF.Max(
                    MathF.Max(courseMinX - x, x - courseMaxX),
                    MathF.Max(courseMinY - y, y - courseMaxY));
                float blend = edgeDistance <= 0f
                    ? 1f
                    : GateOpening(edgeDistance, 0f, blendWidth);
                if (blend < 1f)
                {
                    // Keep the generated canyon outside the course, but fade
                    // into it over 64 m so there is no artificial A* wall.
                    float existingHeight = worldMap.GetSurfaceHeight(x, y);
                    height = existingHeight + (height - existingHeight) * blend;
                }

                ushort heightUnits = checked((ushort)MathF.Round(height * WorldMap.HeightUnitsPerMeter));
                ushort material = blend > 0.55f && ridgeOne + ridgeTwo > 5f
                    ? RockMaterialId
                    : GroundMaterialId;
                worldMap.SetSolidHeight(x, y, heightUnits, material);
            }
        }

        worldMap.EnableDiscreteBlockTerrain();
        WorldGenerator.GenerateTraversalBlocks(worldMap);

        // The core course now fades into the pre-existing world terrain.
        // There is no invisible navigation boundary at the old rectangular edge.
    }

    private static float GateOpening(float y, float center, float halfWidth)
    {
        float t = Math.Clamp(1f - MathF.Abs(y - center) / halfWidth, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float GaussianHill(
        float x,
        float y,
        float amplitude,
        float radius)
    {
        float distanceSquared = x * x + y * y;
        return amplitude * MathF.Exp(-distanceSquared / (2f * radius * radius));
    }
}
