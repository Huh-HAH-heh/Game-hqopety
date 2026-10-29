using System;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

/// <summary>
/// Focused regression tests for the force-driven ground controller.
/// These tests intentionally exercise UnitMovementSystem without AI so movement
/// failures are isolated from tactical decision and perception behavior.
/// </summary>
public static class UnitMovementSelfTest
{
    private const float TestDeltaTime = 1f / 60f;
    private const ushort FloorHeight = 10;

    public static bool AcceleratesAndStopsAtCommandedTarget()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        UnitMovementSystem movement = new UnitMovementSystem();
        Vector3 start = simulation.Units.Position[id.Index];
        Vector3 target = new Vector3(13.5f, 10.5f, 1f);
        simulation.Units.SetTarget(id, target);

        movement.Update(simulation.Units, map, TestDeltaTime);
        Vector3 firstPosition = simulation.Units.Position[id.Index];
        float firstStepDistance = PlanarDistance(start, firstPosition);

        if (firstStepDistance <= 0.00001f || firstStepDistance >= 0.02f)
            return false;

        for (int tick = 0; tick < 1200 && simulation.Units.HasTarget[id.Index]; tick++)
            movement.Update(simulation.Units, map, TestDeltaTime);

        Vector3 finalPosition = simulation.Units.Position[id.Index];
        Vector3 finalVelocity = simulation.Units.Velocity[id.Index];
        float remainingDistance = PlanarDistance(finalPosition, target);
        float finalSpeed = MathF.Sqrt(
            finalVelocity.X * finalVelocity.X +
            finalVelocity.Y * finalVelocity.Y);

        return !simulation.Units.HasTarget[id.Index] &&
               remainingDistance <= 0.2f &&
               finalSpeed <= 0.2f &&
               float.IsFinite(finalPosition.X) &&
               float.IsFinite(finalPosition.Y);
    }

    public static bool DirectionChangesRespectInertia()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(10.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        UnitMovementSystem movement = new UnitMovementSystem();
        simulation.Units.SetTarget(id, new Vector3(32.5f, 10.5f, 1f));

        for (int tick = 0; tick < 36; tick++)
            movement.Update(simulation.Units, map, TestDeltaTime);

        Vector3 before3 = simulation.Units.Velocity[id.Index];
        Vector2 before = new Vector2(before3.X, before3.Y);
        float beforeSpeed = before.Length();
        if (beforeSpeed < 0.75f)
            return false;

        // Command a near-right-angle change while the pawn is already moving.
        simulation.Units.SetTarget(id, new Vector3(10.5f, 38.5f, 1f));
        movement.Update(simulation.Units, map, TestDeltaTime);

        Vector3 after3 = simulation.Units.Velocity[id.Index];
        Vector2 after = new Vector2(after3.X, after3.Y);
        float afterSpeed = after.Length();
        if (afterSpeed < 0.01f)
            return false;

        float cosine = Math.Clamp(
            Vector2.Dot(before, after) / (beforeSpeed * afterSpeed),
            -1f,
            1f);
        float turnDegrees = MathF.Acos(cosine) * (180f / MathF.PI);

        // Traction limits the heading change; the velocity must not snap to the
        // new target direction in one simulation tick.
        return turnDegrees < 20f && Vector2.Dot(before, after) > 0f;
    }

    public static bool FollowsRouteAroundWallWithoutCrossingIt()
    {
        WorldMap map = CreateFlatWorld();
        int wallX = map.TileWidth / 2;
        int lowWallY = 3;
        int highWallY = map.TileHeight - 3;

        for (int y = lowWallY; y < highWallY; y++)
        {
            map.SetSolidHeight(wallX, y, 40, 2);
            map.SetNavigationBlocked(wallX, y, true);
        }

        UnitSimulation simulation = new UnitSimulation(4, 64);
        Vector3 start = new Vector3(5.5f, map.TileHeight / 2f + 0.5f, 1f);
        Vector3 target = new Vector3(map.TileWidth - 5.5f, start.Y, 1f);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            start,
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        UnitMovementSystem movement = new UnitMovementSystem();
        simulation.Units.SetTarget(id, target);
        bool madeDetour = false;

        for (int tick = 0; tick < 1800; tick++)
        {
            movement.Update(simulation.Units, map, TestDeltaTime);

            Vector3 position = simulation.Units.Position[id.Index];
            int cellX = Math.Clamp((int)MathF.Floor(position.X), 0, map.TileWidth - 1);
            int cellY = Math.Clamp((int)MathF.Floor(position.Y), 0, map.TileHeight - 1);

            if (cellX == wallX && cellY >= lowWallY && cellY < highWallY)
                return false;

            if (MathF.Abs(position.Y - start.Y) > 2f)
                madeDetour = true;

            if (!simulation.Units.HasTarget[id.Index])
            {
                float remaining = PlanarDistance(position, target);
                Vector3 velocity = simulation.Units.Velocity[id.Index];
                float speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
                return madeDetour && remaining <= 0.25f && speed <= 0.25f;
            }
        }

        return false;
    }

    public static bool ContactsUseReducedRadiusWithoutExplosiveSeparation()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        Vector3 firstStart = new Vector3(15.5f, 15.5f, 1f);
        Vector3 secondStart = new Vector3(15.55f, 15.5f, 1f);

        UnitId first = simulation.Spawn(
            UnitType.Colonist, firstStart, Vector3.UnitX, Vector3.UnitX, factionTag: 1);
        UnitId second = simulation.Spawn(
            UnitType.Colonist, secondStart, Vector3.UnitX, Vector3.UnitX, factionTag: 1);

        float originalRadiusSum =
            simulation.Units.Radius[first.Index] + simulation.Units.Radius[second.Index];
        if (originalRadiusSum <= 0f)
            return false;

        UnitMovementSystem movement = new UnitMovementSystem();
        movement.Update(simulation.Units, map, TestDeltaTime);

        Vector3 firstEnd = simulation.Units.Position[first.Index];
        Vector3 secondEnd = simulation.Units.Position[second.Index];
        float finalDistance = PlanarDistance(firstEnd, secondEnd);

        float expectedContactDistance = originalRadiusSum * 0.72f * 1.04f;
        float firstDisplacement = PlanarDistance(firstStart, firstEnd);
        float secondDisplacement = PlanarDistance(secondStart, secondEnd);

        return finalDistance >= expectedContactDistance * 0.7f &&
               finalDistance <= originalRadiusSum * 0.9f &&
               firstDisplacement < originalRadiusSum &&
               secondDisplacement < originalRadiusSum &&
               float.IsFinite(finalDistance) &&
               float.IsFinite(simulation.Units.Velocity[first.Index].X) &&
               float.IsFinite(simulation.Units.Velocity[second.Index].X);
    }

    private static WorldMap CreateFlatWorld()
    {
        WorldMap map = new WorldMap(
            regionsX: 1,
            regionsY: 1,
            layerCount: 50);

        for (int y = 0; y < map.TileHeight; y++)
        {
            for (int x = 0; x < map.TileWidth; x++)
                map.SetSolidHeight(x, y, FloorHeight, 1);
        }

        return map;
    }

    private static float PlanarDistance(Vector3 left, Vector3 right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
