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

    public static bool NavigableGoalPreservesSubCellPosition()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.37f, 10.62f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitNavigationSystem navigation = new UnitNavigationSystem();
        Vector2 requested = new(20.23f, 17.71f);

        return navigation.TryResolveNavigableGoal(
                   simulation.Units,
                   id.Index,
                   map,
                   requested,
                   reservedFootprintCells: null,
                   out Vector2 resolved) &&
               Vector2.Distance(resolved, requested) <= 0.001f;
    }

    public static bool LocalRouteTetherUsesContinuousCorridorCoordinates()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.23f, 10.17f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitNavigationSystem navigation = new UnitNavigationSystem();
        navigation.BeginUpdate(simulation.Units, map, TestDeltaTime);

        Vector3 start = simulation.Units.Position[id.Index];
        Vector3 target = new(35.82f, 22.71f, 1f);
        if (!navigation.TryGetLookAheadWaypoint(
                simulation.Units,
                id.Index,
                map,
                start,
                target,
                currentSpeed: 1.6f,
                out Vector2 tether,
                out _,
                out _,
                out _))
        {
            return false;
        }

        Vector2 direction = Vector2.Normalize(
            new Vector2(target.X - start.X, target.Y - start.Y));
        Vector2 offset = tether - new Vector2(start.X, start.Y);
        float crossTrackDistance = MathF.Abs(
            offset.X * direction.Y - offset.Y * direction.X);
        bool offGridX = MathF.Abs(
            tether.X - (MathF.Floor(tether.X) + 0.5f)) > 0.025f;
        bool offGridY = MathF.Abs(
            tether.Y - (MathF.Floor(tether.Y) + 0.5f)) > 0.025f;

        return crossTrackDistance > 0.08f &&
               (offGridX || offGridY) &&
               navigation.CanMoveDirectly(
                   simulation.Units,
                   id.Index,
                   map,
                   new Vector2(start.X, start.Y),
                   tether);
    }

    public static bool NavigationUsesHalfMeterCellsAndAnyAngleParents()
    {
        WorldMap map = CreateFlatWorld();
        int wallX = map.TileWidth / 2;
        int corridorY = map.TileHeight / 2;
        int openingY = corridorY - 8;
        for (int y = 2; y < map.TileHeight - 2; y++)
            if (Math.Abs(y - openingY) > 2)
                map.SetNavigationBlocked(wallX, y, true);

        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(wallX - 0.5f, corridorY + 0.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitNavigationSystem navigation = simulation.Navigation;
        navigation.BeginUpdate(simulation.Units, map, TestDeltaTime);

        if (!navigation.TryGetWaypoint(
                simulation.Units,
                id.Index,
                map,
                simulation.Units.Position[id.Index],
                new Vector3(wallX + 5.5f, corridorY + 0.5f, 1f),
                out _))
            return false;

        Span<Vector2> points = stackalloc Vector2[128];
        int count = navigation.CopyRoutePoints(id.Index, points);
        bool hasSubmeterPoint = false;
        bool hasAnyAngleJump = false;
        for (int i = 0; i < count; i++)
        {
            float fractionX = points[i].X - MathF.Floor(points[i].X);
            float fractionY = points[i].Y - MathF.Floor(points[i].Y);
            hasSubmeterPoint |=
                MathF.Abs(fractionX - 0.25f) < 0.01f ||
                MathF.Abs(fractionX - 0.75f) < 0.01f ||
                MathF.Abs(fractionY - 0.25f) < 0.01f ||
                MathF.Abs(fractionY - 0.75f) < 0.01f;
            if (i > 0)
                hasAnyAngleJump |= Vector2.Distance(points[i - 1], points[i]) > 0.75f;
        }

        return count > 1 && hasSubmeterPoint && hasAnyAngleJump;
    }

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

    public static bool UnitsMoveWhileWaitingForRouteBudget()
    {
        WorldMap map = CreateFlatWorld();
        const int unitCount = 4;
        UnitSimulation simulation = new UnitSimulation(8, 64);
        UnitId[] ids = new UnitId[unitCount];
        Vector3[] starts = new Vector3[unitCount];

        for (int i = 0; i < unitCount; i++)
        {
            Vector3 start = new Vector3(5.5f, 5.5f + i * 7f, 1f);
            starts[i] = start;
            ids[i] = simulation.Spawn(
                UnitType.Colonist,
                start,
                Vector3.UnitX,
                Vector3.UnitX,
                factionTag: 1);
            simulation.Units.SetTarget(
                ids[i],
                new Vector3(35.5f, start.Y, 1f));
        }

        // Small formations only receive two route builds per update. The last
        // units must still take a short safe step instead of standing idle.
        UnitMovementSystem movement = new UnitMovementSystem();
        movement.Update(simulation.Units, map, TestDeltaTime);

        for (int i = 2; i < unitCount; i++)
        {
            Vector3 position = simulation.Units.Position[ids[i].Index];
            if (!float.IsFinite(position.X) ||
                !float.IsFinite(position.Y) ||
                PlanarDistance(starts[i], position) <= 0.0001f)
            {
                return false;
            }
        }

        return true;
    }

    public static bool CancellingCommandAppliesGradualBraking()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(8.5f, 8.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        UnitMovementSystem movement = new UnitMovementSystem();
        simulation.Units.SetTarget(id, new Vector3(35.5f, 8.5f, 1f));
        for (int tick = 0; tick < 36; tick++)
            movement.Update(simulation.Units, map, TestDeltaTime);

        Vector3 beforePosition = simulation.Units.Position[id.Index];
        Vector3 beforeVelocity3 = simulation.Units.Velocity[id.Index];
        float beforeSpeed = MathF.Sqrt(
            beforeVelocity3.X * beforeVelocity3.X +
            beforeVelocity3.Y * beforeVelocity3.Y);
        if (beforeSpeed < 0.75f)
            return false;

        simulation.Units.ClearTarget(id);
        movement.Update(simulation.Units, map, TestDeltaTime);

        Vector3 afterPosition = simulation.Units.Position[id.Index];
        Vector3 afterVelocity3 = simulation.Units.Velocity[id.Index];
        float afterSpeed = MathF.Sqrt(
            afterVelocity3.X * afterVelocity3.X +
            afterVelocity3.Y * afterVelocity3.Y);

        return !simulation.Units.HasTarget[id.Index] &&
               afterSpeed < beforeSpeed &&
               afterSpeed > 0.05f &&
               PlanarDistance(beforePosition, afterPosition) > 0f;
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

    public static bool WeightedAStarDetourSurvivesWaypointSmoothing()
    {
        WorldMap map = CreateFlatWorld();
        int centerX = map.TileWidth / 2;
        int centerY = map.TileHeight / 2;
        int wallStartX = centerX - 5;
        int wallEndX = centerX + 4;

        // Keep a direct, traversable lane through the centre, but make it
        // narrow enough that its edge-cell penalties should favour the open
        // route above or below it.
        for (int x = wallStartX; x <= wallEndX; x++)
        {
            map.SetNavigationBlocked(x, centerY - 1, true);
            map.SetNavigationBlocked(x, centerY + 1, true);
        }

        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(wallStartX - 5.5f, centerY + 0.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        Vector3 start = simulation.Units.Position[id.Index];
        Vector3 target = new(wallEndX + 5.5f, start.Y, 1f);
        UnitNavigationSystem navigation = simulation.Navigation;

        if (!navigation.CanMoveDirectly(
                simulation.Units,
                id.Index,
                map,
                new Vector2(wallStartX + 0.5f, centerY + 0.5f),
                new Vector2(wallEndX + 0.5f, centerY + 0.5f)))
        {
            return false;
        }

        Vector3 current = start;
        bool builtWeightedRoute = false;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            navigation.BeginUpdate(simulation.Units, map, TestDeltaTime);
            if (!navigation.TryGetWaypoint(
                    simulation.Units,
                    id.Index,
                    map,
                    current,
                    target,
                    out Vector2 next))
            {
                builtWeightedRoute = navigation.SearchesThisUpdate > 0;
                break;
            }

            if (navigation.SearchesThisUpdate > 0)
            {
                builtWeightedRoute = true;
                break;
            }

            current = new Vector3(next.X, next.Y, current.Z);
            simulation.Units.Position[id.Index] = current;
        }

        if (!builtWeightedRoute)
            return false;

        Span<Vector2> route = stackalloc Vector2[128];
        int routeCount = navigation.CopyRoutePoints(id.Index, route);
        bool rawRouteUsesOpenGround = false;
        for (int i = 0; i < routeCount; i++)
        {
            Vector2 point = route[i];
            if (point.X >= wallStartX + 2f &&
                point.X <= wallEndX - 2f &&
                (point.Y <= centerY - 1.5f || point.Y >= centerY + 1.5f))
            {
                rawRouteUsesOpenGround = true;
                break;
            }
        }

        Span<Vector2> waypoints = stackalloc Vector2[128];
        int waypointCount = navigation.CopyWaypointPoints(id.Index, waypoints);
        bool smoothedRouteKeepsOpenGround = false;
        for (int i = 0; i < waypointCount; i++)
        {
            Vector2 point = waypoints[i];
            if (point.X >= wallStartX + 2f &&
                point.X <= wallEndX - 2f &&
                (point.Y <= centerY - 1.5f || point.Y >= centerY + 1.5f))
            {
                smoothedRouteKeepsOpenGround = true;
                break;
            }
        }

        return rawRouteUsesOpenGround && smoothedRouteKeepsOpenGround;
    }

    public static bool OpenGroundKeepsTheHotPathFast()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        UnitId id = simulation.Spawn(
            UnitType.Colonist,
            new Vector3(5.5f, 10.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);

        UnitNavigationSystem navigation = simulation.Navigation;
        navigation.BeginUpdate(simulation.Units, map, TestDeltaTime);
        bool found = navigation.TryGetWaypoint(
            simulation.Units,
            id.Index,
            map,
            simulation.Units.Position[id.Index],
            new Vector3(35.5f, 10.5f, 1f),
            out _);

        return found &&
            navigation.SearchesThisUpdate == 0 &&
            navigation.GetLastStatus(id.Index) == UnitNavigationStatus.FollowingHotPath;
    }

    public static bool DenseMovingCrowdContinuesToAdvance()
    {
        WorldMap map = CreateFlatWorld();
        const int columns = 4;
        const int rows = 4;
        const float spacing = 0.6f;
        const float startX = 6.5f;
        const float startY = 14.5f;

        UnitSimulation simulation = new UnitSimulation(columns * rows, 256);
        UnitId[] ids = new UnitId[columns * rows];
        Vector3[] starts = new Vector3[ids.Length];

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                Vector3 start = new Vector3(
                    startX + column * spacing,
                    startY + row * spacing,
                    1f);
                starts[index] = start;
                ids[index] = simulation.Spawn(
                    UnitType.Colonist,
                    start,
                    Vector3.UnitX,
                    Vector3.UnitX,
                    factionTag: 1);
            }
        }

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                simulation.Units.SetTarget(
                    ids[index],
                    new Vector3(28.5f + column * spacing, starts[index].Y, 1f));
            }
        }

        UnitMovementSystem movement = new UnitMovementSystem();
        for (int tick = 0; tick < 240; tick++)
            movement.Update(simulation.Units, map, TestDeltaTime);

        float totalProgress = 0f;
        int progressingUnits = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            Vector3 position = simulation.Units.Position[ids[i].Index];
            Vector3 velocity = simulation.Units.Velocity[ids[i].Index];
            if (!float.IsFinite(position.X) ||
                !float.IsFinite(position.Y) ||
                !float.IsFinite(velocity.X) ||
                !float.IsFinite(velocity.Y))
                return false;

            float progress = position.X - starts[i].X;
            totalProgress += progress;
            if (progress > 2f)
                progressingUnits++;
        }

        // A dense but mobile group should continue to advance despite soft
        // contacts and route-following; a crowd-wide deadlock fails this check.
        return progressingUnits >= ids.Length * 3 / 4 &&
               totalProgress / ids.Length > 3.5f;
    }

    public static bool OpposingUnitsSteerAroundOneAnother()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        Vector3 firstStart = new Vector3(14.5f, 20.5f, 1f);
        Vector3 secondStart = new Vector3(18.5f, 20.5f, 1f);

        UnitId first = simulation.Spawn(
            UnitType.Colonist, firstStart, Vector3.UnitX, Vector3.UnitX, factionTag: 1);
        UnitId second = simulation.Spawn(
            UnitType.Colonist, secondStart, -Vector3.UnitX, -Vector3.UnitX, factionTag: 1);

        Vector3 firstTarget = new Vector3(21.5f, 20.5f, 1f);
        Vector3 secondTarget = new Vector3(11.5f, 20.5f, 1f);
        simulation.Units.SetTarget(first, firstTarget);
        simulation.Units.SetTarget(second, secondTarget);

        float radiusSum =
            simulation.Units.Radius[first.Index] + simulation.Units.Radius[second.Index];
        UnitMovementSystem movement = new UnitMovementSystem();
        float minimumSeparation = float.PositiveInfinity;
        float maximumLateralDeviation = 0f;

        for (int tick = 0; tick < 240; tick++)
        {
            movement.Update(simulation.Units, map, TestDeltaTime);

            Vector3 firstPosition = simulation.Units.Position[first.Index];
            Vector3 secondPosition = simulation.Units.Position[second.Index];
            minimumSeparation = MathF.Min(
                minimumSeparation, PlanarDistance(firstPosition, secondPosition));
            maximumLateralDeviation = MathF.Max(
                maximumLateralDeviation,
                MathF.Max(
                    MathF.Abs(firstPosition.Y - firstStart.Y),
                    MathF.Abs(secondPosition.Y - secondStart.Y)));

            if (!float.IsFinite(firstPosition.X) ||
                !float.IsFinite(firstPosition.Y) ||
                !float.IsFinite(secondPosition.X) ||
                !float.IsFinite(secondPosition.Y))
                return false;
        }

        Vector3 firstEnd = simulation.Units.Position[first.Index];
        Vector3 secondEnd = simulation.Units.Position[second.Index];

        // The pair should pass on different sides and keep moving. The contact
        // solver remains the final safety net; predictive steering should reduce
        // head-on contact instead of replacing contact resolution.
        return firstEnd.X > secondStart.X &&
               secondEnd.X < firstStart.X &&
               maximumLateralDeviation > 0.05f &&
               minimumSeparation > radiusSum * 0.5f;
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

    public static bool DownedEnemySlowsButDoesNotBlockTraversal()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(4, 64);
        Vector3 start = new(10.5f, 20.5f, 1f);
        Vector3 bodyPosition = new(17.5f, 20.5f, 1f);
        Vector3 destination = new(25.5f, 20.5f, 1f);

        UnitId mover = simulation.Spawn(
            UnitType.Colonist,
            start,
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 1);
        UnitId downedEnemy = simulation.Spawn(
            UnitType.Colonist,
            bodyPosition,
            Vector3.UnitX,
            Vector3.UnitX,
            factionTag: 2);

        simulation.Health.OverallHitPoints[downedEnemy.Index] = 0f;
        simulation.Units.Posture[downedEnemy.Index] = UnitPosture.Lying;
        simulation.Units.SetTarget(mover, destination);

        UnitMovementSystem movement = new UnitMovementSystem();
        float slowestSpeedNearBody = float.PositiveInfinity;
        bool passedBody = false;
        for (int tick = 0; tick < 1200 && simulation.Units.HasTarget[mover.Index]; tick++)
        {
            movement.Update(
                simulation.Units,
                map,
                TestDeltaTime,
                simulation.Health);

            Vector3 position = simulation.Units.Position[mover.Index];
            float distanceToBody = PlanarDistance(position, bodyPosition);
            Vector3 velocity = simulation.Units.Velocity[mover.Index];
            float speed = MathF.Sqrt(
                velocity.X * velocity.X +
                velocity.Y * velocity.Y);
            if (distanceToBody <= 1.4f)
                slowestSpeedNearBody = MathF.Min(slowestSpeedNearBody, speed);
            if (position.X > bodyPosition.X + 1.1f)
                passedBody = true;
        }

        Vector3 finalPosition = simulation.Units.Position[mover.Index];
        return passedBody &&
               slowestSpeedNearBody < simulation.Units.MoveSpeed[mover.Index] * 0.9f &&
               PlanarDistance(finalPosition, destination) <= 0.25f &&
               float.IsFinite(finalPosition.X) &&
               float.IsFinite(finalPosition.Y);
    }

    public static bool ColumnNarrowsWhileAdvancing()
    {
        return TryMeasureFormation(
                   InfantryFormationType.Column,
                   out float initialForward,
                   out float initialLateral,
                   out float finalForward,
                   out float finalLateral,
                   out float progress) &&
               progress > 5f &&
               finalForward > initialForward + 1.5f &&
               finalLateral < initialLateral * 0.9f;
    }

    public static bool LineWidensWhileAdvancing()
    {
        return TryMeasureFormation(
                   InfantryFormationType.Line,
                   out _,
                   out float initialLateral,
                   out _,
                   out float finalLateral,
                   out float progress) &&
               progress > 3.5f &&
               finalLateral > initialLateral + 2f;
    }

    public static bool LineKeepsItsFormationSlotsAtArrival() =>
        FormationSlotsRemainDistinctAtArrival(InfantryFormationType.Line);

    public static bool WedgeKeepsItsFormationSlotsAtArrival() =>
        FormationSlotsRemainDistinctAtArrival(InfantryFormationType.Wedge);

    private static bool FormationSlotsRemainDistinctAtArrival(
        InfantryFormationType formation)
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(16, 256);
        simulation.AI.Enabled = false;

        const int columns = 4;
        const int rows = 3;
        const float startSpacing = 1.35f;
        UnitId[] ids = new UnitId[columns * rows];
        Vector2 initialCenter = Vector2.Zero;
        float spacing = 1.15f;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                Vector3 start = new(
                    6.5f + column * startSpacing,
                    20f + row * startSpacing,
                    1f);
                ids[index] = simulation.Spawn(
                    UnitType.Colonist,
                    start,
                    Vector3.UnitX,
                    Vector3.UnitX,
                    factionTag: 1);
                initialCenter += new Vector2(start.X, start.Y);
                spacing = MathF.Max(
                    spacing,
                    MathF.Max(
                        simulation.Units.Width[ids[index].Index],
                        simulation.Units.Length[ids[index].Index]) * 1.15f);
            }
        }

        initialCenter /= ids.Length;
        Vector3 destination = new(42.5f, 21.1f, 1f);
        Vector2 toDestination = new(destination.X - initialCenter.X,
            destination.Y - initialCenter.Y);
        Vector2 forward = toDestination.LengthSquared() > 0.0001f
            ? Vector2.Normalize(toDestination)
            : Vector2.UnitX;
        Vector2 side = new(-forward.Y, forward.X);

        Vector2 meanOffset = Vector2.Zero;
        for (int i = 0; i < ids.Length; i++)
            meanOffset += InfantryFormation.GetOffset(i, ids.Length, formation, spacing);
        meanOffset /= ids.Length;

        if (!simulation.IssueAStarFormationMoveOrder(
                ids,
                destination,
                formation,
                keepFormationInCombat: false,
                worldMap: map))
        {
            return false;
        }

        for (int tick = 0; tick < 1800; tick++)
        {
            simulation.Update(map, TestDeltaTime);
            bool anyMoving = false;
            for (int i = 0; i < ids.Length; i++)
                anyMoving |= simulation.Units.HasTarget[ids[i].Index];

            simulation.SquadMovement.TryGetInformation(
                1_000_000, out SquadMovementInformation currentInformation);
            if (!anyMoving && !currentInformation.HasMoveOrder)
                break;
        }

        Vector2 finalCenter = Vector2.Zero;
        float totalSlotError = 0f;
        float maximumSlotError = 0f;
        int activeTargets = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            int unit = ids[i].Index;
            Vector3 actual = simulation.Units.Position[unit];
            if (!float.IsFinite(actual.X) || !float.IsFinite(actual.Y))
                return false;

            finalCenter += new Vector2(actual.X, actual.Y);
            Vector2 localOffset =
                InfantryFormation.GetOffset(i, ids.Length, formation, spacing) - meanOffset;
            Vector2 expected = new Vector2(destination.X, destination.Y) +
                side * localOffset.X -
                forward * localOffset.Y;
            float error = Vector2.Distance(
                new Vector2(actual.X, actual.Y), expected);
            totalSlotError += error;
            maximumSlotError = MathF.Max(maximumSlotError, error);
            if (simulation.Units.HasTarget[unit])
                activeTargets++;
        }

        finalCenter /= ids.Length;
        float averageSlotError = totalSlotError / ids.Length;
        float progress = Vector2.Dot(finalCenter - initialCenter, forward);
        simulation.SquadMovement.TryGetInformation(
            1_000_000, out SquadMovementInformation information);
        Console.WriteLine(
            $"[Squad {formation} arrival] progress={progress:0.00}, " +
            $"slotError avg={averageSlotError:0.00}, max={maximumSlotError:0.00}, " +
            $"active={activeTargets}/{ids.Length}, phase={information.Phase}");

        return progress > 12f &&
               averageSlotError < 1.0f &&
               maximumSlotError < 1.8f &&
               activeTargets == 0 &&
               !information.HasMoveOrder;
    }

    public static bool FreeGroupGathersIntoCrowdWhileMoving()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(16, 256);
        simulation.AI.Enabled = false;

        const int columns = 4;
        const int rows = 3;
        const float spacing = 1.7f;
        UnitId[] ids = new UnitId[columns * rows];
        Vector2 initialCenter = Vector2.Zero;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                Vector3 start = new(
                    6.5f + column * spacing,
                    20f + row * spacing,
                    1f);
                ids[index] = simulation.Spawn(
                    UnitType.Colonist,
                    start,
                    Vector3.UnitX,
                    Vector3.UnitX,
                    factionTag: 1);
                initialCenter += new Vector2(start.X, start.Y);
            }
        }

        initialCenter /= ids.Length;
        Vector3 destination = new(42.5f, 21.1f, 1f);
        Vector2 forward = Vector2.Normalize(
            new Vector2(destination.X - initialCenter.X, destination.Y - initialCenter.Y));
        float initialSpread = MeasureGroupRadius(simulation.Units, ids);

        bool issued = simulation.IssueAStarFormationMoveOrder(
            ids,
            destination,
            InfantryFormationType.Free,
            keepFormationInCombat: false,
            worldMap: map);
        if (!issued)
            return false;

        for (int tick = 0; tick < 420; tick++)
            simulation.Update(map, TestDeltaTime);

        float finalSpread = MeasureGroupRadius(
            simulation.Units, ids, out Vector2 finalCenter);
        float progress = Vector2.Dot(finalCenter - initialCenter, forward);
        int movingMembers = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            Vector3 position = simulation.Units.Position[ids[i].Index];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
                return false;
            if (simulation.Units.HasTarget[ids[i].Index])
                movingMembers++;
        }

        Console.WriteLine(
            $"[Squad crowd measure] spread={initialSpread:0.00}->{finalSpread:0.00}, " +
            $"progress={progress:0.00}, moving={movingMembers}/{ids.Length}");

        return progress > 4.5f &&
               finalSpread < initialSpread * 0.88f &&
               movingMembers >= ids.Length * 3 / 4;
    }

    private static float MeasureGroupRadius(
        UnitStore units,
        UnitId[] ids) =>
        MeasureGroupRadius(units, ids, out _);

    private static float MeasureGroupRadius(
        UnitStore units,
        UnitId[] ids,
        out Vector2 center)
    {
        center = Vector2.Zero;
        for (int i = 0; i < ids.Length; i++)
        {
            Vector3 position = units.Position[ids[i].Index];
            center += new Vector2(position.X, position.Y);
        }

        center /= ids.Length;
        float radiusSquared = 0f;
        for (int i = 0; i < ids.Length; i++)
        {
            Vector3 position = units.Position[ids[i].Index];
            Vector2 offset = new Vector2(position.X, position.Y) - center;
            radiusSquared += offset.LengthSquared();
        }

        return MathF.Sqrt(radiusSquared / ids.Length);
    }

    public static bool CollectiveMoveCompletesWithoutArtificialSpacingHold()
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(16, 256);
        simulation.AI.Enabled = false;

        const int columns = 4;
        const int rows = 3;
        const float startSpacing = 1.0f;
        UnitId[] ids = new UnitId[columns * rows];
        Vector2 initialCenter = Vector2.Zero;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                Vector3 start = new(
                    6.5f + column * startSpacing,
                    20.5f + row * startSpacing,
                    1f);
                ids[index] = simulation.Spawn(
                    UnitType.Colonist,
                    start,
                    Vector3.UnitX,
                    Vector3.UnitX,
                    factionTag: 1);
                initialCenter += new Vector2(start.X, start.Y);
            }
        }

        initialCenter /= ids.Length;
        Vector3 destination = new(35.5f, 21.5f, 1f);
        Vector2 forward = Vector2.Normalize(
            new Vector2(destination.X - initialCenter.X, destination.Y - initialCenter.Y));
        bool issued = simulation.IssueAStarFormationMoveOrder(
            ids,
            destination,
            InfantryFormationType.Free,
            keepFormationInCombat: false,
            worldMap: map);
        if (!issued)
            return false;

        for (int tick = 0; tick < 1800; tick++)
        {
            simulation.Update(map, TestDeltaTime);
            bool anyMoving = false;
            for (int i = 0; i < ids.Length; i++)
                anyMoving |= simulation.Units.HasTarget[ids[i].Index];
            if (!anyMoving)
                break;
        }

        Vector2 finalCenter = Vector2.Zero;
        float minimumDistance = float.PositiveInfinity;
        int activeTargets = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            int unit = ids[i].Index;
            Vector3 first = simulation.Units.Position[unit];
            if (!float.IsFinite(first.X) || !float.IsFinite(first.Y))
                return false;

            finalCenter += new Vector2(first.X, first.Y);
            if (simulation.Units.HasTarget[unit])
                activeTargets++;

            for (int j = i + 1; j < ids.Length; j++)
            {
                Vector3 second = simulation.Units.Position[ids[j].Index];
                minimumDistance = MathF.Min(
                    minimumDistance,
                    PlanarDistance(first, second));
            }
        }

        finalCenter /= ids.Length;
        float progress = Vector2.Dot(finalCenter - initialCenter, forward);
        simulation.SquadMovement.TryGetInformation(
            1_000_000, out SquadMovementInformation information);
        Console.WriteLine(
            $"[Squad arrival] minNeighbor={minimumDistance:0.00}, " +
            $"progress={progress:0.00}, activeTargets={activeTargets}/{ids.Length}, " +
            $"phase={information.Phase}");

        // Spacing is diagnostic only. The group must finish its order without
        // being held to an artificial interpersonal radius.
        return progress > 12f &&
               activeTargets == 0 &&
               !information.HasMoveOrder;
    }

    private static bool TryMeasureFormation(
        InfantryFormationType formation,
        out float initialForward,
        out float initialLateral,
        out float finalForward,
        out float finalLateral,
        out float progress)
    {
        WorldMap map = CreateFlatWorld();
        UnitSimulation simulation = new UnitSimulation(16, 256);
        simulation.AI.Enabled = false;

        const int columns = 4;
        const int rows = 3;
        UnitId[] ids = new UnitId[columns * rows];
        Vector2 initialCenter = Vector2.Zero;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                Vector3 start = new(
                    6.5f + column * 1.3f,
                    20f + row * 1.35f,
                    1f);
                int index = row * columns + column;
                ids[index] = simulation.Spawn(
                    UnitType.Colonist,
                    start,
                    Vector3.UnitX,
                    Vector3.UnitX,
                    factionTag: 1);
                initialCenter += new Vector2(start.X, start.Y);
            }
        }

        initialCenter /= ids.Length;
        Vector3 destination = new(42.5f, 21.1f, 1f);
        Vector2 forward = Vector2.Normalize(
            new Vector2(destination.X - initialCenter.X, destination.Y - initialCenter.Y));
        Vector2 side = new(-forward.Y, forward.X);

        int validCount = 0;
        float initialHealth = 0f;
        for (int i = 0; i < ids.Length; i++)
        {
            if (ids[i].IsValid &&
                simulation.Units.TryGetIndex(ids[i], out int unitIndex))
            {
                validCount++;
                initialHealth += simulation.Health.OverallHitPoints[unitIndex];
            }
        }
        bool issued = simulation.IssueAStarFormationMoveOrder(
            ids,
            destination,
            formation,
            keepFormationInCombat: false,
            worldMap: map);
        Console.WriteLine(
            $"[Squad formation issue] mode={formation}, spawned={ids.Length}, " +
            $"valid={validCount}, avgHp={initialHealth / Math.Max(1, validCount):0.0}, issued={issued}");
        if (!issued)
        {
            initialForward = 0f;
            initialLateral = 0f;
            finalForward = 0f;
            finalLateral = 0f;
            progress = 0f;
            return false;
        }

        GetFormationExtents(
            simulation.Units, ids, forward, side,
            out initialForward, out initialLateral, out _);

        int simulationTicks = formation == InfantryFormationType.Column ? 600 : 360;
        for (int tick = 0; tick < simulationTicks; tick++)
            simulation.Update(map, TestDeltaTime);

        GetFormationExtents(
            simulation.Units, ids, forward, side,
            out finalForward, out finalLateral, out Vector2 finalCenter);
        progress = Vector2.Dot(finalCenter - initialCenter, forward);
        simulation.SquadMovement.TryGetInformation(1_000_000, out SquadMovementInformation info);
        int activeTargets = 0;
        float averageSpeed = 0f;
        for (int i = 0; i < ids.Length; i++)
        {
            int unit = ids[i].Index;
            if (simulation.Units.HasTarget[unit])
                activeTargets++;
            Vector3 velocity = simulation.Units.Velocity[unit];
            averageSpeed += MathF.Sqrt(
                velocity.X * velocity.X + velocity.Y * velocity.Y);
        }
        averageSpeed /= ids.Length;
        Console.WriteLine(
            $"[Squad formation measure] mode={formation}, " +
            $"forward={initialForward:0.00}->{finalForward:0.00}, " +
            $"lateral={initialLateral:0.00}->{finalLateral:0.00}, " +
            $"progress={progress:0.00}, targets={activeTargets}/{ids.Length}, " +
            $"speed={averageSpeed:0.00}, phase={info.Phase}, center={info.Center}, " +
            $"velocity={info.Velocity}, following={info.FollowingCount}, " +
            $"rejoining={info.RejoiningCount}, separated={info.SeparatedCount}");
        return true;
    }

    private static void GetFormationExtents(
        UnitStore units,
        UnitId[] ids,
        Vector2 forward,
        Vector2 side,
        out float forwardExtent,
        out float lateralExtent,
        out Vector2 center)
    {
        center = Vector2.Zero;
        for (int i = 0; i < ids.Length; i++)
        {
            Vector3 position = units.Position[ids[i].Index];
            center += new Vector2(position.X, position.Y);
        }
        center /= ids.Length;

        float minForward = float.PositiveInfinity;
        float maxForward = float.NegativeInfinity;
        float minLateral = float.PositiveInfinity;
        float maxLateral = float.NegativeInfinity;
        for (int i = 0; i < ids.Length; i++)
        {
            Vector3 position = units.Position[ids[i].Index];
            Vector2 offset = new Vector2(position.X, position.Y) - center;
            float along = Vector2.Dot(offset, forward);
            float across = Vector2.Dot(offset, side);
            minForward = MathF.Min(minForward, along);
            maxForward = MathF.Max(maxForward, along);
            minLateral = MathF.Min(minLateral, across);
            maxLateral = MathF.Max(maxLateral, across);
        }

        forwardExtent = maxForward - minForward;
        lateralExtent = maxLateral - minLateral;
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
