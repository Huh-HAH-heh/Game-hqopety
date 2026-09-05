using Core.Map;
using Core.Unit.Components;
using System;

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

    private const int MaxHeightStep = 3;
    private const int MaxGridSize = 768;
    private const int MaxGridCells =
        MaxGridSize * MaxGridSize;

    private const int MaxPathLength =
        MaxGridCells;

    private const float StraightCost = 1f;
    private const float DiagonalCost = 1.41421356f;

    /*
     * Максимальное дополнительное влияние
     * congestion на стоимость клетки.
     */
    private const float TrafficCostMultiplier = 0.20f;

    private static readonly int[] Dx =
    {
        0, 0, -1, 1,
        -1, 1, -1, 1
    };

    private static readonly int[] Dy =
    {
        -1, 1, 0, 0,
        -1, -1, 1, 1
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
        return FindRouteInternal(
            context,
            traffic,
            currentFrame,
            layer,
            start,
            target,
            outPath,
            maxPathLength,
            out pathLength);
    }

    private static bool FindRouteInternal(
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
                start.Y) ||
            !IsWalkable(
                layer,
                target.X,
                target.Y))
        {
            return false;
        }

        if (start.X == target.X &&
            start.Y == target.Y)
        {
            outPath[0] = start;
            pathLength = 1;
            return true;
        }

        context.Reset();

        int startIndex =
            ToIndex(start.X, start.Y);

        int targetIndex =
            ToIndex(target.X, target.Y);

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
                FindBestOpenNode(
                    context);

            PurePathNode current =
                context.OpenSet[bestOpenIndex];

            if (current.HashIndex ==
                targetIndex)
            {
                return BuildPath(
                    context,
                    start.Z,
                    targetIndex,
                    outPath,
                    maxPathLength,
                    out pathLength);
            }

            RemoveOpenNode(
                context,
                bestOpenIndex);

            context.ClosedSet[
                current.HashIndex] = 1;

            for (int d = 0; d < 8; d++)
            {
                int nx =
                    current.X + Dx[d];

                int ny =
                    current.Y + Dy[d];

                if (!IsInside(nx, ny))
                    continue;

                int neighborIndex =
                    ToIndex(nx, ny);

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

                bool diagonal =
                    Math.Abs(Dx[d]) == 1 &&
                    Math.Abs(Dy[d]) == 1;

                if (diagonal &&
                    !CanMoveDiagonally(
                        layer,
                        current.X,
                        current.Y,
                        nx,
                        ny))
                {
                    continue;
                }

                float stepCost =
                    diagonal
                        ? DiagonalCost
                        : StraightCost;

                /*
                 * Главное изменение:
                 *
                 * предыдущие маршруты делают
                 * использованные клетки временно
                 * менее привлекательными.
                 */
                if (traffic != null)
                {
                    float trafficCost =
                        traffic.GetCost(
                            neighborIndex,
                            currentFrame);

                    stepCost +=
                        trafficCost *
                        TrafficCostMultiplier;
                }

                float newG =
                    current.G + stepCost;

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

    private static bool IsWalkable(
        MapLayer layer,
        int x,
        int y)
    {
        var cell =
            layer.GetMicroCell(x, y);

        return
            (cell.Flags & FLAG_WALKABLE) != 0 &&
            cell.EdificeId <= 0;
    }

    private static bool CanTraverseHeight(
        MapLayer layer,
        int srcX,
        int srcY,
        int dstX,
        int dstY)
    {
        var src =
            layer.GetMicroCell(
                srcX,
                srcY);

        var dst =
            layer.GetMicroCell(
                dstX,
                dstY);

        return Math.Abs(
            dst.Height - src.Height)
            <= MaxHeightStep;
    }

    private static bool CanMoveDiagonally(
        MapLayer layer,
        int srcX,
        int srcY,
        int dstX,
        int dstY)
    {
        int sideX = dstX;
        int sideY = srcY;

        if (!IsWalkable(
                layer,
                sideX,
                sideY))
        {
            return false;
        }

        if (!CanTraverseHeight(
                layer,
                srcX,
                srcY,
                sideX,
                sideY))
        {
            return false;
        }

        sideX = srcX;
        sideY = dstY;

        if (!IsWalkable(
                layer,
                sideX,
                sideY))
        {
            return false;
        }

        if (!CanTraverseHeight(
                layer,
                srcX,
                srcY,
                sideX,
                sideY))
        {
            return false;
        }

        return CanTraverseHeight(
            layer,
            srcX,
            srcY,
            dstX,
            dstY);
    }

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
                context.ParentMap[current];
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

    private static float Heuristic(
        int x,
        int y,
        int targetX,
        int targetY)
    {
        int dx =
            Math.Abs(targetX - x);

        int dy =
            Math.Abs(targetY - y);

        int diagonal =
            Math.Min(dx, dy);

        int straight =
            Math.Max(dx, dy) -
            diagonal;

        return
            diagonal * DiagonalCost +
            straight;
    }

    private static int ToIndex(
        int x,
        int y)
    {
        return
            y * MaxGridSize + x;
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