using System;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;

namespace Core.AI;

public struct PurePathNode
{
    public int HashIndex;
    public int X;
    public int Y;
    public float G;
    public float H;
    public float F;
}

public static class PureAStarPathfinder
{
    private const ushort FLAG_WALKABLE = 0x0004;

    private const int MaxUnitsPerTile = 3;
    private const int MaxHeightStep = 3;

    private const int MaxGridSize = 768;
    private const int MaxGridCells =
        MaxGridSize * MaxGridSize;

    private const int MaxPathLength =
        MaxGridCells;

    // Обычный шаг.
    private const float StraightCost = 1f;

    // ============================================================
    // TRAFFIC
    // ============================================================

    // Насколько сильно старые маршруты влияют на A*.
    private const float TrafficCostMultiplier = 2.5f;

    // ============================================================
    // OCCUPANCY
    // ============================================================

    private const float OneUnitOccupancyCost = 5f;
    private const float TwoUnitsOccupancyCost = 18f;

    // ============================================================
    // DIRECTIONS
    // ============================================================

    private static readonly int[] Dx =
    {
        0,
        0,
        -1,
        1
    };

    private static readonly int[] Dy =
    {
        -1,
        1,
        0,
        0
    };

    public static PureAStarContext CreateContext()
    {
        return new PureAStarContext(
            MaxGridCells,
            MaxPathLength);
    }

    public static bool FindRoute(
        PureAStarContext context,
        PathTrafficMemory traffic,
        int currentFrame,
        MapLayer layer,
        SpatialCoord start,
        SpatialCoord target,
        SpatialCoord[] outPath,
        int maxPathLength,
        out int pathLength)
    {
        pathLength = 0;

        if (context == null ||
            layer == null ||
            outPath == null ||
            maxPathLength <= 0)
        {
            return false;
        }

        if (start.Z != target.Z)
            return false;

        if (!IsInside(start.X, start.Y) ||
            !IsInside(target.X, target.Y))
        {
            return false;
        }

        if (!IsWalkable(
                layer,
                start.X,
                start.Y))
        {
            return false;
        }

        if (!IsWalkable(
                layer,
                target.X,
                target.Y))
        {
            return false;
        }

        int targetIndex =
            ToIndex(
                target.X,
                target.Y);

        // Полностью заполненная цель
        // недоступна.
        if (traffic != null &&
            traffic.GetOccupancy(targetIndex) >=
            MaxUnitsPerTile)
        {
            return false;
        }

        // Уже на месте.
        if (start.X == target.X &&
            start.Y == target.Y)
        {
            outPath[0] = start;
            pathLength = 1;
            return true;
        }

        context.Reset();

        int startIndex =
            ToIndex(
                start.X,
                start.Y);

        float startH =
            Heuristic(
                start.X,
                start.Y,
                target.X,
                target.Y);

        context.GScore[startIndex] = 0f;

        context.OpenSet[
            context.OpenCount++] =
            new PurePathNode
            {
                HashIndex = startIndex,
                X = start.X,
                Y = start.Y,
                G = 0f,
                H = startH,
                F = startH
            };

        while (context.OpenCount > 0)
        {
            int bestOpenIndex =
                FindBestOpenNode(context);

            PurePathNode current =
                context.OpenSet[
                    bestOpenIndex];

            if (current.HashIndex == targetIndex)
            {
                bool success =
                    BuildPath(
                        context,
                        start.Z,
                        targetIndex,
                        outPath,
                        maxPathLength,
                        out pathLength);

                // =================================================
                // ЗАПОМИНАЕМ МАРШРУТ
                // =================================================

                if (success &&
                    traffic != null &&
                    pathLength > 1)
                {
                    traffic.AddRoute(
                        outPath,
                        pathLength,
                        currentFrame);
                }

                return success;
            }

            RemoveOpenNode(
                context,
                bestOpenIndex);

            context.ClosedSet[
                current.HashIndex] = 1;

            for (int d = 0; d < 4; d++)
            {
                int nx =
                    current.X + Dx[d];

                int ny =
                    current.Y + Dy[d];

                if (!IsInside(nx, ny))
                    continue;

                int neighborIndex =
                    ToIndex(
                        nx,
                        ny);

                if (context.ClosedSet[
                        neighborIndex] != 0)
                {
                    continue;
                }

                if (!IsWalkable(
                        layer,
                        nx,
                        ny))
                {
                    continue;
                }

                if (!CanTraverseHeight(
                        layer,
                        current.X,
                        current.Y,
                        nx,
                        ny))
                {
                    continue;
                }

                // =================================================
                // OCCUPANCY
                // =================================================

                int occupancy = 0;

                if (traffic != null)
                {
                    occupancy =
                        traffic.GetOccupancy(
                            neighborIndex);

                    // 3/3 = физическая блокировка.
                    if (neighborIndex != startIndex &&
                        occupancy >= MaxUnitsPerTile)
                    {
                        continue;
                    }
                }

                float stepCost =
                    StraightCost;

                // =================================================
                // OCCUPANCY PENALTY
                // =================================================

                if (neighborIndex != startIndex)
                {
                    switch (occupancy)
                    {
                        case 1:
                            stepCost +=
                                OneUnitOccupancyCost;
                            break;

                        case 2:
                            stepCost +=
                                TwoUnitsOccupancyCost;
                            break;
                    }
                }

                // =================================================
                // OLD ROUTE PENALTY
                // =================================================

                if (traffic != null)
                {
                    float heat =
                        traffic.GetCost(
                            neighborIndex,
                            currentFrame);

                    stepCost +=
                        heat *
                        TrafficCostMultiplier;
                }

                float newG =
                    current.G +
                    stepCost;

                if (newG >=
                    context.GScore[
                        neighborIndex])
                {
                    continue;
                }

                context.ParentMap[
                    neighborIndex] =
                    current.HashIndex;

                context.GScore[
                    neighborIndex] =
                    newG;

                float h =
                    Heuristic(
                        nx,
                        ny,
                        target.X,
                        target.Y);

                UpdateOpenNode(
                    context,
                    neighborIndex,
                    nx,
                    ny,
                    newG,
                    h);
            }
        }

        return false;
    }

    // ============================================================
    // WALKABLE
    // ============================================================

    private static bool IsWalkable(
        MapLayer layer,
        int x,
        int y)
    {
        MicroCell cell =
            layer.GetMicroCell(
                x,
                y);

        return
            (cell.Flags & FLAG_WALKABLE) != 0 &&
            cell.EdificeId == 0;
    }

    // ============================================================
    // HEIGHT
    // ============================================================

    private static bool CanTraverseHeight(
        MapLayer layer,
        int srcX,
        int srcY,
        int dstX,
        int dstY)
    {
        MicroCell src =
            layer.GetMicroCell(
                srcX,
                srcY);

        MicroCell dst =
            layer.GetMicroCell(
                dstX,
                dstY);

        return
            Math.Abs(
                (int)dst.Height -
                (int)src.Height)
            <= MaxHeightStep;
    }

    // ============================================================
    // OPEN SET
    // ============================================================

    private static void UpdateOpenNode(
        PureAStarContext context,
        int hashIndex,
        int x,
        int y,
        float g,
        float h)
    {
        float f =
            g + h;

        for (int i = 0;
             i < context.OpenCount;
             i++)
        {
            if (context.OpenSet[i]
                    .HashIndex != hashIndex)
            {
                continue;
            }

            context.OpenSet[i].G = g;
            context.OpenSet[i].H = h;
            context.OpenSet[i].F = f;

            return;
        }

        if (context.OpenCount >=
            context.OpenSet.Length)
        {
            return;
        }

        context.OpenSet[
            context.OpenCount++] =
            new PurePathNode
            {
                HashIndex = hashIndex,
                X = x,
                Y = y,
                G = g,
                H = h,
                F = f
            };
    }

    private static int FindBestOpenNode(
        PureAStarContext context)
    {
        int best = 0;

        for (int i = 1;
             i < context.OpenCount;
             i++)
        {
            if (context.OpenSet[i].F <
                context.OpenSet[best].F)
            {
                best = i;
                continue;
            }

            if (context.OpenSet[i].F ==
                    context.OpenSet[best].F &&
                context.OpenSet[i].H <
                    context.OpenSet[best].H)
            {
                best = i;
            }
        }

        return best;
    }

    private static void RemoveOpenNode(
        PureAStarContext context,
        int index)
    {
        context.OpenCount--;

        if (index ==
            context.OpenCount)
        {
            return;
        }

        context.OpenSet[index] =
            context.OpenSet[
                context.OpenCount];
    }

    // ============================================================
    // BUILD PATH
    // ============================================================

    private static bool BuildPath(
        PureAStarContext context,
        int z,
        int targetIndex,
        SpatialCoord[] outPath,
        int maxPathLength,
        out int pathLength)
    {
        pathLength = 0;

        int current =
            targetIndex;

        int tempCount = 0;

        while (current != -1)
        {
            if (tempCount >=
                context.TempPathBuffer.Length)
            {
                return false;
            }

            int x =
                current % MaxGridSize;

            int y =
                current / MaxGridSize;

            context.TempPathBuffer[
                tempCount++] =
                new SpatialCoord(
                    x,
                    y,
                    z);

            current =
                context.ParentMap[
                    current];
        }

        if (tempCount <= 0 ||
            tempCount > maxPathLength ||
            tempCount > outPath.Length)
        {
            return false;
        }

        for (int i = 0;
             i < tempCount;
             i++)
        {
            outPath[i] =
                context.TempPathBuffer[
                    tempCount - 1 - i];
        }

        pathLength =
            tempCount;

        return true;
    }

    // ============================================================
    // HEURISTIC
    // ============================================================

    private static float Heuristic(
        int x,
        int y,
        int targetX,
        int targetY)
    {
        return
            Math.Abs(
                targetX - x) +
            Math.Abs(
                targetY - y);
    }

    private static int ToIndex(
        int x,
        int y)
    {
        return
            y * MaxGridSize +
            x;
    }

    private static bool IsInside(
        int x,
        int y)
    {
        return
            x >= 0 &&
            x < MaxGridSize &&
            y >= 0 &&
            y < MaxGridSize;
    }
}