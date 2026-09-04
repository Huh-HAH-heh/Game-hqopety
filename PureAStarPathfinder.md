
# PureAStarPathfinder
```csharp
using System;
using Core.Map;
using Core.Unit.Components;

namespace Core.AI
{
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
        private const int MaxGridCells = MaxGridSize * MaxGridSize;
        private const int MaxPathLength = MaxGridCells;

        private static readonly PurePathNode[] OpenSet =
            new PurePathNode[MaxGridCells];

        private static readonly byte[] ClosedSet =
            new byte[MaxGridCells];

        private static readonly int[] ParentMap =
            new int[MaxGridCells];

        private static readonly float[] GScore =
            new float[MaxGridCells];

        private static readonly SpatialCoord[] TempPathBuffer =
            new SpatialCoord[MaxPathLength];

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

        private static int _openCount;
        private static readonly object SearchLock = new object();

        public static bool FindRoute(
            MapLayer layer,
            SpatialCoord start,
            SpatialCoord target,
            SpatialCoord[] outPath,
            int maxPathLength,
            out int pathLength)
        {
            return FindRoute(
                layer,
                start,
                target,
                outPath,
                maxPathLength,
                out pathLength,
                0u
            );
        }

        public static bool FindRoute(
            MapLayer layer,
            SpatialCoord start,
            SpatialCoord target,
            SpatialCoord[] outPath,
            int maxPathLength,
            out int pathLength,
            uint noiseSeed)
        {
            lock (SearchLock)
            {
                return FindRouteInternal(
                    layer,
                    start,
                    target,
                    outPath,
                    maxPathLength,
                    out pathLength,
                    noiseSeed
                );
            }
        }

        private static bool FindRouteInternal(
            MapLayer layer,
            SpatialCoord start,
            SpatialCoord target,
            SpatialCoord[] outPath,
            int maxPathLength,
            out int pathLength,
            uint noiseSeed)
        {
            pathLength = 0;

            if (layer == null || outPath == null || maxPathLength <= 0)
                return false;

            if (start.Z != target.Z)
                return false;

            if (!IsInside(start.X, start.Y) ||
                !IsInside(target.X, target.Y))
            {
                return false;
            }

            if (!IsWalkable(layer, start.X, start.Y) ||
                !IsWalkable(layer, target.X, target.Y))
            {
                return false;
            }

            if (start.X == target.X && start.Y == target.Y)
            {
                outPath[0] = start;
                pathLength = 1;
                return true;
            }

            _openCount = 0;

            Array.Clear(ClosedSet, 0, ClosedSet.Length);
            Array.Fill(ParentMap, -1);
            Array.Fill(GScore, float.MaxValue);

            int startIndex =
                ToIndex(start.X, start.Y);

            int targetIndex =
                ToIndex(target.X, target.Y);

            float startH =
                Heuristic(
                    start.X,
                    start.Y,
                    target.X,
                    target.Y
                );

            GScore[startIndex] = 0f;

            OpenSet[_openCount++] =
                new PurePathNode
                {
                    HashIndex = startIndex,
                    X = start.X,
                    Y = start.Y,
                    G = 0f,
                    H = startH,
                    F = startH
                };

            while (_openCount > 0)
            {
                int bestOpenIndex = FindBestOpenNode();
                PurePathNode current = OpenSet[bestOpenIndex];

                if (current.HashIndex == targetIndex)
                {
                    return BuildPath(
                        start.Z,
                        targetIndex,
                        outPath,
                        maxPathLength,
                        out pathLength
                    );
                }

                RemoveOpenNode(bestOpenIndex);
                ClosedSet[current.HashIndex] = 1;

                for (int d = 0; d < 8; d++)
                {
                    int nx = current.X + Dx[d];
                    int ny = current.Y + Dy[d];

                    if (!IsInside(nx, ny))
                        continue;

                    int neighborIndex = ToIndex(nx, ny);

                    if (ClosedSet[neighborIndex] != 0)
                        continue;

                    if (!IsWalkable(layer, nx, ny))
                        continue;

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
                            ny
                        ))
                    {
                        continue;
                    }

                    float stepCost = diagonal ? 1.41421356f : 1f;

                    // Маленький детерминированный шум.
                    // Базовая стоимость остаётся 1 / sqrt(2),
                    // поэтому A* сохраняет гарантированный поиск
                    // существующего маршрута, а из равных по длине
                    // вариантов разные юниты могут выбрать разные клетки.
                    stepCost += GetPathNoise(nx, ny, noiseSeed);

                    float newG = current.G + stepCost;

                    if (newG >= GScore[neighborIndex])
                        continue;

                    ParentMap[neighborIndex] = current.HashIndex;
                    GScore[neighborIndex] = newG;

                    float h =
                        Heuristic(
                            nx,
                            ny,
                            target.X,
                            target.Y
                        );

                    UpdateOpenNode(
                        neighborIndex,
                        nx,
                        ny,
                        newG,
                        h
                    );
                }
            }

            return false;
        }

        private static bool IsWalkable(
            MapLayer layer,
            int x,
            int y)
        {
            var cell = layer.GetMicroCell(x, y);

            return (cell.Flags & FLAG_WALKABLE) != 0 &&
                   cell.EdificeId <= 0;
        }

        private static bool CanTraverseHeight(
            MapLayer layer,
            int srcX,
            int srcY,
            int dstX,
            int dstY)
        {
            var src = layer.GetMicroCell(srcX, srcY);
            var dst = layer.GetMicroCell(dstX, dstY);

            return Math.Abs(dst.Height - src.Height) <= MaxHeightStep;
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

            if (!IsWalkable(layer, sideX, sideY))
                return false;

            if (!CanTraverseHeight(layer, srcX, srcY, sideX, sideY))
                return false;

            sideX = srcX;
            sideY = dstY;

            if (!IsWalkable(layer, sideX, sideY))
                return false;

            if (!CanTraverseHeight(layer, srcX, srcY, sideX, sideY))
                return false;

            if (!CanTraverseHeight(layer, srcX, srcY, dstX, dstY))
                return false;

            return true;
        }

        private static void UpdateOpenNode(
            int hashIndex,
            int x,
            int y,
            float g,
            float h)
        {
            float f = g + h;

            for (int i = 0; i < _openCount; i++)
            {
                if (OpenSet[i].HashIndex != hashIndex)
                    continue;

                OpenSet[i].G = g;
                OpenSet[i].H = h;
                OpenSet[i].F = f;
                return;
            }

            if (_openCount >= OpenSet.Length)
                return;

            OpenSet[_openCount++] =
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

        private static int FindBestOpenNode()
        {
            int best = 0;

            for (int i = 1; i < _openCount; i++)
            {
                if (OpenSet[i].F < OpenSet[best].F)
                {
                    best = i;
                    continue;
                }

                if (OpenSet[i].F == OpenSet[best].F &&
                    OpenSet[i].H < OpenSet[best].H)
                {
                    best = i;
                }
            }

            return best;
        }

        private static void RemoveOpenNode(int index)
        {
            _openCount--;

            if (index == _openCount)
                return;

            OpenSet[index] = OpenSet[_openCount];
        }

        private static bool BuildPath(
            int z,
            int targetIndex,
            SpatialCoord[] outPath,
            int maxPathLength,
            out int pathLength)
        {
            pathLength = 0;

            int current = targetIndex;
            int tempCount = 0;

            while (current != -1)
            {
                if (tempCount >= TempPathBuffer.Length)
                    return false;

                int x = current % MaxGridSize;
                int y = current / MaxGridSize;

                TempPathBuffer[tempCount++] =
                    new SpatialCoord(x, y, z);

                current = ParentMap[current];
            }

            if (tempCount <= 0 ||
                tempCount > maxPathLength ||
                tempCount > outPath.Length)
            {
                return false;
            }

            for (int i = 0; i < tempCount; i++)
            {
                outPath[i] =
                    TempPathBuffer[tempCount - 1 - i];
            }

            pathLength = tempCount;
            return true;
        }


        private static float GetPathNoise(
            int x,
            int y,
            uint seed)
        {
            if (seed == 0u)
                return 0f;

            uint h =
                seed ^
                ((uint)x * 374761393u) ^
                ((uint)y * 668265263u);

            h ^= h >> 13;
            h *= 1274126177u;
            h ^= h >> 16;

            float value =
                (h & 0x00FFFFFFu) / 16777216f;

            // Очень слабый шум: 0..0.055 стоимости клетки.
            return value * 0.055f;
        }
        private static float Heuristic(
            int x,
            int y,
            int targetX,
            int targetY)
        {
            int dx = Math.Abs(targetX - x);
            int dy = Math.Abs(targetY - y);

            int diagonal = Math.Min(dx, dy);
            int straight = Math.Max(dx, dy) - diagonal;

            return diagonal * 1.41421356f + straight;
        }

        private static int ToIndex(int x, int y)
        {
            return y * MaxGridSize + x;
        }

        private static bool IsInside(int x, int y)
        {
            return x >= 0 &&
                   x < MaxGridSize &&
                   y >= 0 &&
                   y < MaxGridSize;
        }
    }
}

```