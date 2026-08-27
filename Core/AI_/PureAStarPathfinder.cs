using System;
using System.Runtime.InteropServices;
using Core.Map;
using Core.Structs;
using Core.Unit.Components;

namespace Core.AI
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
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
        private const int MaxGridSize = 768;
        private const int MaxGridCells = MaxGridSize * MaxGridSize;

        private static readonly PurePathNode[] OpenSet =
            new PurePathNode[MaxGridCells / 4];

        private static readonly byte[] ClosedSet =
            new byte[MaxGridCells];

        private static readonly int[] ParentMap =
            new int[MaxGridCells];

        private static readonly float[] GScore =
            new float[MaxGridCells];

        private static readonly SpatialCoord[] TempPathBuffer =
            new SpatialCoord[1024];

        private static int _openCount;

        public static bool FindRoute(
            MapLayer layer,
            SpatialCoord start,
            SpatialCoord target,
            SpatialCoord[] outPath,
            int maxPathLength,
            out int pathLength)
        {
            pathLength = 0;

            if (layer == null || outPath == null)
                return false;

            if (maxPathLength <= 0 || outPath.Length == 0)
                return false;

            if (!IsInsideBounds(start.X, start.Y))
                return false;

            if (!IsInsideBounds(target.X, target.Y))
                return false;

            if ((layer.GetMicroCell(start.X, start.Y).Flags & FLAG_WALKABLE) == 0)
                return false;

            if ((layer.GetMicroCell(target.X, target.Y).Flags & FLAG_WALKABLE) == 0)
                return false;

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

            int startHash = ToHash(start.X, start.Y);
            int targetHash = ToHash(target.X, target.Y);

            GScore[startHash] = 0f;

            PurePathNode startNode = new PurePathNode
            {
                HashIndex = startHash,
                X = start.X,
                Y = start.Y,
                G = 0f,
                H = GetHeuristic(start.X, start.Y, target.X, target.Y)
            };

            startNode.F = startNode.G + startNode.H;

            OpenSet[_openCount++] = startNode;

            ParentMap[startHash] = -1;

            while (_openCount > 0)
            {
                int bestIndex = FindBestOpenNodeIndex();
                PurePathNode current = OpenSet[bestIndex];

                if (current.HashIndex == targetHash)
                {
                    return BuildResultPath(
                        start,
                        target,
                        outPath,
                        maxPathLength,
                        out pathLength
                    );
                }

                OpenSet[bestIndex] = OpenSet[--_openCount];
                ClosedSet[current.HashIndex] = 1;

                TryProcessNeighbor(
                    layer,
                    current,
                    current.X,
                    current.Y - 1,
                    target,
                    0
                );

                TryProcessNeighbor(
                    layer,
                    current,
                    current.X,
                    current.Y + 1,
                    target,
                    1
                );

                TryProcessNeighbor(
                    layer,
                    current,
                    current.X - 1,
                    current.Y,
                    target,
                    2
                );

                TryProcessNeighbor(
                    layer,
                    current,
                    current.X + 1,
                    current.Y,
                    target,
                    3
                );
            }

            return false;
        }

        private static void TryProcessNeighbor(
            MapLayer layer,
            PurePathNode current,
            int nx,
            int ny,
            SpatialCoord target,
            int direction)
        {
            if (!IsInsideBounds(nx, ny))
                return;

            int neighborHash = ToHash(nx, ny);

            if (ClosedSet[neighborHash] != 0)
                return;

            if ((layer.GetMicroCell(nx, ny).Flags & FLAG_WALKABLE) == 0)
                return;

            float tentativeG =
                GScore[current.HashIndex] + 1f;

            if (tentativeG >= GScore[neighborHash])
                return;

            ParentMap[neighborHash] =
                current.HashIndex;

            GScore[neighborHash] =
                tentativeG;

            float h =
                GetHeuristic(
                    nx,
                    ny,
                    target.X,
                    target.Y
                );

            float f =
                tentativeG + h;

            for (int i = 0; i < _openCount; i++)
            {
                if (OpenSet[i].HashIndex != neighborHash)
                    continue;

                OpenSet[i].G = tentativeG;
                OpenSet[i].H = h;
                OpenSet[i].F = f;

                return;
            }

            if (_openCount >= OpenSet.Length)
                return;

            OpenSet[_openCount++] = new PurePathNode
            {
                HashIndex = neighborHash,
                X = nx,
                Y = ny,
                G = tentativeG,
                H = h,
                F = f
            };
        }

        private static bool BuildResultPath(
            SpatialCoord start,
            SpatialCoord target,
            SpatialCoord[] outPath,
            int maxPathLength,
            out int pathLength)
        {
            pathLength = 0;

            int targetHash =
                ToHash(target.X, target.Y);

            int currentHash = targetHash;

            int tempCount = 0;

            while (currentHash != -1)
            {
                if (tempCount >= TempPathBuffer.Length)
                    return false;

                if (tempCount >= maxPathLength)
                    return false;

                int x =
                    currentHash % MaxGridSize;

                int y =
                    currentHash / MaxGridSize;

                TempPathBuffer[tempCount++] =
                    new SpatialCoord(
                        x,
                        y,
                        start.Z
                    );

                currentHash =
                    ParentMap[currentHash];
            }

            if (tempCount <= 0)
                return false;

            int outputCount =
                Math.Min(
                    tempCount,
                    Math.Min(
                        maxPathLength,
                        outPath.Length
                    )
                );

            for (int i = 0; i < outputCount; i++)
            {
                outPath[i] =
                    TempPathBuffer[
                        tempCount - 1 - i
                    ];
            }

            pathLength = outputCount;

            return true;
        }

        private static int FindBestOpenNodeIndex()
        {
            int bestIndex = 0;
            float minF = OpenSet[0].F;

            for (int i = 1; i < _openCount; i++)
            {
                if (OpenSet[i].F < minF)
                    continue;

                if (OpenSet[i].F > minF)
                    continue;

                if (OpenSet[i].H >= OpenSet[bestIndex].H)
                    continue;

                bestIndex = i;
            }

            return bestIndex;
        }

        private static float GetHeuristic(
            int x,
            int y,
            int targetX,
            int targetY)
        {
            return Math.Abs(targetX - x) +
                   Math.Abs(targetY - y);
        }

        private static int ToHash(
            int x,
            int y)
        {
            return y * MaxGridSize + x;
        }

        private static bool IsInsideBounds(
            int x,
            int y)
        {
            return x >= 0 &&
                   x < MaxGridSize &&
                   y >= 0 &&
                   y < MaxGridSize;
        }
    }
}