using System;
using System.Numerics;
using Core.Map;

namespace Core.Unit;

public sealed class ProjectileSystem
{
    private const float Gravity = 9.81f;
    private const float AirDensity = 1.225f;
    private const float TraceEpsilon = 0.00001f;

    private readonly UnitSpatialGrid _unitGrid = new UnitSpatialGrid();
    private readonly UnitHitSystem _hitSystem = new UnitHitSystem();
    private readonly DamageSystem _damageSystem = new DamageSystem();

    public void Update(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitHealthStore health,
        UnitHealthSystem healthSystem,
        ProjectileStore projectiles,
        WorldMap worldMap,
        float deltaTime)
    {
        if (deltaTime <= 0f ||
            projectiles.ActiveCount == 0)
            return;

        _unitGrid.Ensure(
            worldMap,
            units.Capacity);

        _unitGrid.Build(
            units);

        int activeSlot = 0;

        while (activeSlot <
               projectiles.ActiveCount)
        {
            int projectileIndex =
                projectiles.ActiveIndices[
                    activeSlot];

            projectiles.Lifetime[
                projectileIndex] -=
                deltaTime;

            if (projectiles.Lifetime[
                    projectileIndex] <= 0f)
            {
                projectiles.DestroyIndex(
                    projectileIndex);
                continue;
            }

            IntegrateVelocity(
                projectiles,
                projectileIndex,
                deltaTime);

            if (projectiles.Velocity[
                    projectileIndex].LengthSquared() <
                0.0001f)
            {
                projectiles.DestroyIndex(
                    projectileIndex);
                continue;
            }

            Vector3 start =
                projectiles.Position[
                    projectileIndex];

            Vector3 end =
                start +
                projectiles.Velocity[
                    projectileIndex] *
                deltaTime;

            bool alive =
                Trace(
                    units,
                    inventory,
                    health,
                    healthSystem,
                    projectiles,
                    worldMap,
                    projectileIndex,
                    start,
                    end);

            if (!alive)
            {
                projectiles.DestroyIndex(
                    projectileIndex);
                continue;
            }

            projectiles.Position[
                projectileIndex] =
                end;

            if (end.X < 0f ||
                end.Y < 0f ||
                end.X >= worldMap.TileWidth ||
                end.Y >= worldMap.TileHeight ||
                end.Z < -1f)
            {
                projectiles.DestroyIndex(
                    projectileIndex);
                continue;
            }

            activeSlot++;
        }
    }

    private bool Trace(
        UnitStore units,
        UnitInventoryStore inventory,
        UnitHealthStore health,
        UnitHealthSystem healthSystem,
        ProjectileStore projectiles,
        WorldMap worldMap,
        int projectileIndex,
        Vector3 start,
        Vector3 end)
    {
        float dx =
            end.X - start.X;

        float dy =
            end.Y - start.Y;

        if (MathF.Abs(dx) < 0.000001f &&
            MathF.Abs(dy) < 0.000001f)
        {
            return true;
        }

        int cellX =
            (int)MathF.Floor(
                start.X);

        int cellY =
            (int)MathF.Floor(
                start.Y);

        if (cellX < 0 ||
            cellY < 0 ||
            cellX >= worldMap.TileWidth ||
            cellY >= worldMap.TileHeight)
        {
            return false;
        }

        int stepX =
            Math.Sign(dx);

        int stepY =
            Math.Sign(dy);

        if (stepX < 0 &&
            MathF.Abs(
                start.X -
                MathF.Round(start.X)) <
            TraceEpsilon)
        {
            cellX--;
        }

        if (stepY < 0 &&
            MathF.Abs(
                start.Y -
                MathF.Round(start.Y)) <
            TraceEpsilon)
        {
            cellY--;
        }

        float tDeltaX =
            stepX == 0
                ? float.PositiveInfinity
                : MathF.Abs(1f / dx);

        float tDeltaY =
            stepY == 0
                ? float.PositiveInfinity
                : MathF.Abs(1f / dy);

        float nextBoundaryX =
            stepX > 0
                ? cellX + 1f
                : cellX;

        float nextBoundaryY =
            stepY > 0
                ? cellY + 1f
                : cellY;

        float tMaxX =
            stepX == 0
                ? float.PositiveInfinity
                : (nextBoundaryX - start.X) / dx;

        float tMaxY =
            stepY == 0
                ? float.PositiveInfinity
                : (nextBoundaryY - start.Y) / dy;

        float segmentStart = 0f;

        int ignoredUnit = -1;
        UnitHealthPartId ignoredPart =
            UnitHealthPartId.None;

        while (segmentStart < 1f)
        {
            float segmentEnd =
                MathF.Min(
                    1f,
                    MathF.Min(
                        tMaxX,
                        tMaxY));

            if (segmentEnd <= segmentStart)
            {
                if (segmentEnd >= 1f)
                    break;

                if (tMaxX < tMaxY)
                {
                    cellX += stepX;
                    tMaxX += tDeltaX;
                }
                else
                {
                    cellY += stepY;
                    tMaxY += tDeltaY;
                }

                if (cellX < 0 ||
                    cellY < 0 ||
                    cellX >= worldMap.TileWidth ||
                    cellY >= worldMap.TileHeight)
                {
                    return false;
                }

                segmentStart =
                    MathF.Min(
                        segmentEnd + TraceEpsilon,
                        1f);

                continue;
            }

            float terrainT = 2f;
            float terrainExitT = 2f;
            ushort terrainMaterial = 0;

            if (TryGetTerrainInterval(
                    worldMap,
                    cellX,
                    cellY,
                    start,
                    end,
                    segmentStart,
                    segmentEnd,
                    out float terrainEntry,
                    out float terrainExit,
                    out terrainMaterial))
            {
                terrainT = terrainEntry;
                terrainExitT = terrainExit;
            }

            float unitT = 2f;
            int hitUnit = -1;
            UnitHitResult bestHit = default;

            int node =
                _unitGrid.GetCellHead(
                    cellX,
                    cellY);

            Vector3 subStart =
                start +
                (end - start) *
                segmentStart;

            Vector3 subEnd =
                start +
                (end - start) *
                segmentEnd;

            while (node >= 0)
            {
                int unitIndex =
                    _unitGrid.GetNodeUnit(
                        node);

                node =
                    _unitGrid.GetNextNode(
                        node);

                UnitHealthPartId skipPart =
                    unitIndex == ignoredUnit
                        ? ignoredPart
                        : UnitHealthPartId.None;

                if (!_hitSystem.TryHitUnit(
                        units,
                        unitIndex,
                        subStart,
                        subEnd,
                        projectiles.DiameterM[
                            projectileIndex] *
                        0.5f,
                        skipPart,
                        out UnitHitResult localHit))
                {
                    continue;
                }

                float globalT =
                    segmentStart +
                    (segmentEnd - segmentStart) *
                    localHit.T;

                if (globalT >= unitT)
                    continue;

                unitT = globalT;
                hitUnit = unitIndex;

                bestHit =
                    new UnitHitResult(
                        localHit.Part,
                        start +
                        (end - start) *
                        globalT,
                        globalT);
            }

            if (terrainT > 1f &&
                unitT > 1f)
            {
                if (segmentEnd >= 1f)
                    break;

                if (tMaxX < tMaxY)
                {
                    cellX += stepX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY < tMaxX)
                {
                    cellY += stepY;
                    tMaxY += tDeltaY;
                }
                else
                {
                    cellX += stepX;
                    cellY += stepY;
                    tMaxX += tDeltaX;
                    tMaxY += tDeltaY;
                }

                if (cellX < 0 ||
                    cellY < 0 ||
                    cellX >= worldMap.TileWidth ||
                    cellY >= worldMap.TileHeight)
                {
                    return false;
                }

                segmentStart =
                    segmentEnd;

                continue;
            }

            if (terrainT <= unitT)
            {
                if (!ApplyTerrainImpact(
                        projectiles,
                        projectileIndex,
                        terrainMaterial,
                        terrainT,
                        terrainExitT,
                        start,
                        end,
                        out bool continues))
                {
                    Console.WriteLine(
                        $"[BLOCKED] projectile owner={projectiles.Owner[projectileIndex]} " +
                        $"faction={projectiles.FactionTag[projectileIndex]} " +
                        $"terrainMaterial={terrainMaterial} " +
                        $"pos={start + (end - start) * terrainT} " +
                        $"energy={projectiles.Energy[projectileIndex]:F2} " +
                        $"penetration={projectiles.Penetration[projectileIndex]:F2}");

                    return false;
                }

                if (!continues)
                    return false;

                segmentStart =
                    MathF.Max(
                        segmentStart +
                        TraceEpsilon,
                        terrainExitT +
                        TraceEpsilon);

                if (segmentStart >= segmentEnd)
                {
                    if (segmentEnd >= 1f)
                        return true;

                    if (tMaxX < tMaxY)
                    {
                        cellX += stepX;
                        tMaxX += tDeltaX;
                    }
                    else if (tMaxY < tMaxX)
                    {
                        cellY += stepY;
                        tMaxY += tDeltaY;
                    }
                    else
                    {
                        cellX += stepX;
                        cellY += stepY;
                        tMaxX += tDeltaX;
                        tMaxY += tDeltaY;
                    }

                    if (cellX < 0 ||
                        cellY < 0 ||
                        cellX >= worldMap.TileWidth ||
                        cellY >= worldMap.TileHeight)
                    {
                        return false;
                    }

                    segmentStart =
                        segmentEnd;
                }

                continue;
            }

            UnitId target =
                units.GetId(
                    hitUnit);

            DamageEvent damageEvent =
                new DamageEvent(
                    projectiles.Owner[
                        projectileIndex],
                    target,
                    bestHit.Part,
                    bestHit.Position,
                    Normalize(
                        projectiles.Velocity[
                            projectileIndex]),
                    projectiles.BaseDamage[
                        projectileIndex],
                    projectiles.Energy[
                        projectileIndex],
                    projectiles.InitialEnergy[
                        projectileIndex],
                    projectiles.Penetration[
                        projectileIndex]);

            ProjectileDamageResult damage =
                _damageSystem.ApplyProjectile(
                    units,
                    inventory,
                    health,
                    healthSystem,
                    damageEvent);

            projectiles.Energy[
                projectileIndex] =
                damage.RemainingEnergy;

            projectiles.Penetration[
                projectileIndex] =
                damage.RemainingPenetration;

            projectiles.RegisterHit(
                target,
                bestHit.Part,
                bestHit.Position);

            Console.WriteLine(
                $"[HIT] projectile owner={projectiles.Owner[projectileIndex]} " +
                $"faction={projectiles.FactionTag[projectileIndex]} " +
                $"target={target} " +
                $"part={bestHit.Part} " +
                $"damage={damage.AppliedDamage:F2} " +
                $"energyLeft={damage.RemainingEnergy:F2} " +
                $"penetrationLeft={damage.RemainingPenetration:F2} " +
                $"pos={bestHit.Position} " +
                $"stopped={damage.ProjectileStopped}");

            ignoredUnit =
                hitUnit;

            ignoredPart =
                bestHit.Part;

            if (damage.ProjectileStopped)
                return false;

            segmentStart =
                MathF.Max(
                    segmentStart +
                    TraceEpsilon,
                    bestHit.T +
                    TraceEpsilon);

            if (segmentStart >= segmentEnd)
            {
                if (segmentEnd >= 1f)
                    return true;

                if (tMaxX < tMaxY)
                {
                    cellX += stepX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxY < tMaxX)
                {
                    cellY += stepY;
                    tMaxY += tDeltaY;
                }
                else
                {
                    cellX += stepX;
                    cellY += stepY;
                    tMaxX += tDeltaX;
                    tMaxY += tDeltaY;
                }

                if (cellX < 0 ||
                    cellY < 0 ||
                    cellX >= worldMap.TileWidth ||
                    cellY >= worldMap.TileHeight)
                {
                    return false;
                }

                segmentStart =
                    segmentEnd;
            }
        }

        return true;
    }

    private static bool ApplyTerrainImpact(
        ProjectileStore projectiles,
        int projectileIndex,
        ushort materialId,
        float entryT,
        float exitT,
        Vector3 start,
        Vector3 end,
        out bool continues)
    {
        GetTerrainResistance(
            materialId,
            out float penetrationLoss,
            out float energyLoss);

        float pathLength =
            MathF.Max(
                0f,
                (exitT - entryT) *
                (end - start).Length());

        float thickness =
            MathF.Max(
                0.25f,
                pathLength);

        float penetrationCost =
            penetrationLoss *
            thickness;

        float energyCost =
            energyLoss *
            thickness;

        float energy =
            projectiles.Energy[
                projectileIndex];

        float penetration =
            projectiles.Penetration[
                projectileIndex];

        if (penetration <= penetrationCost ||
            energy <= energyCost)
        {
            continues = false;
            return false;
        }

        projectiles.Penetration[
            projectileIndex] =
            penetration -
            penetrationCost;

        projectiles.Energy[
            projectileIndex] =
            energy -
            energyCost;

        continues = true;
        return true;
    }

    private static void GetTerrainResistance(
        ushort materialId,
        out float penetrationLoss,
        out float energyLoss)
    {
        switch (materialId)
        {
            case 2:
                penetrationLoss = 30f;
                energyLoss = 1100f;
                break;

            case 1:
                penetrationLoss = 6f;
                energyLoss = 300f;
                break;

            default:
                penetrationLoss = 12f;
                energyLoss = 600f;
                break;
        }
    }

    private static bool TryGetTerrainInterval(
        WorldMap worldMap,
        int cellX,
        int cellY,
        Vector3 start,
        Vector3 end,
        float minT,
        float maxT,
        out float entryT,
        out float exitT,
        out ushort materialId)
    {
        entryT = 0f;
        exitT = 0f;
        materialId = 0;

        float surface =
            worldMap.GetSurfaceHeight(
                cellX,
                cellY);

        if (surface <= 0f)
            return false;

        float dz =
            end.Z - start.Z;

        float lower = minT;
        float upper = maxT;

        if (MathF.Abs(dz) < 0.000001f)
        {
            float z =
                start.Z;

            if (z < 0f ||
                z > surface)
            {
                return false;
            }
        }
        else
        {
            float t0 =
                (0f - start.Z) /
                dz;

            float t1 =
                (surface - start.Z) /
                dz;

            if (t0 > t1)
                (t0, t1) = (t1, t0);

            lower =
                MathF.Max(
                    lower,
                    t0);

            upper =
                MathF.Min(
                    upper,
                    t1);

            if (upper < lower)
                return false;
        }

        entryT = lower;
        exitT = upper;

        float sampleT =
            (entryT + exitT) *
            0.5f;

        float sampleZ =
            start.Z +
            dz *
            sampleT;

        materialId =
            GetTerrainMaterial(
                worldMap,
                cellX,
                cellY,
                sampleZ);

        return true;
    }

    private static ushort GetTerrainMaterial(
        WorldMap worldMap,
        int x,
        int y,
        float z)
    {
        ReadOnlySpan<TileRange> ranges =
            worldMap.GetTileRanges(
                x,
                y);

        for (int i = 0;
             i < ranges.Length;
             i++)
        {
            ref readonly TileRange range =
                ref ranges[i];

            if (range.State !=
                WorldMap.StateSolid)
            {
                continue;
            }

            float start =
                range.StartZ *
                0.1f;

            float end =
                range.EndZ *
                0.1f;

            if (z >= start &&
                z <= end)
            {
                return range.MaterialId;
            }
        }

        return 1;
    }

    private static void IntegrateVelocity(
        ProjectileStore projectiles,
        int index,
        float deltaTime)
    {
        Vector3 velocity =
            projectiles.Velocity[index];

        float mass =
            MathF.Max(
                0.000001f,
                projectiles.MassKg[index]);

        float diameter =
            MathF.Max(
                0.0001f,
                projectiles.DiameterM[index]);

        float drag =
            MathF.Max(
                0f,
                projectiles.DragCoefficient[index]);

        ProjectileBallistics.Integrate(
            ref velocity,
            mass,
            diameter,
            drag,
            deltaTime);

        projectiles.Velocity[index] = velocity;
    }

    private static Vector3 Normalize(
        Vector3 value)
    {
        float lengthSquared =
            value.LengthSquared();

        if (lengthSquared <
            0.000001f)
        {
            return new Vector3(
                1f,
                0f,
                0f);
        }

        return value /
            MathF.Sqrt(
                lengthSquared);
    }
}
