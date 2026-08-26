// Path: Assets/Scripts/AI/PureAStarPathfinder.cs
using System;
using System.Runtime.InteropServices;
using Core.Map;
using Core.Structs;

namespace Core.AI
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PurePathNode
    {
        public int HashIndex; // Изменено: теперь это плоский индекс (Y * 768) + X
        public short X;
        public short Y;
        public float G;
        public float H;
        public float F;
    }

    public static class PureAStarPathfinder
    {
        private const ushort FLAG_WALKABLE = 0x0004;
        private const int MaxGridSize = 768;

        private static readonly PurePathNode[] OpenSet = new PurePathNode[MaxGridSize * MaxGridSize / 4];
        private static int _openCount = 0;

        private static readonly byte[] ClosedSet = new byte[MaxGridSize * MaxGridSize];
        private static readonly int[] ParentMap = new int[MaxGridSize * MaxGridSize];
        private static readonly float[] GScore = new float[MaxGridSize * MaxGridSize];

        private static readonly short[] TempPathBuffer = new short[1024 * 2];

        public static bool FindRoute(MapLayer layer, short startX, short startY, short targetX, short targetY, short[] outPath, int maxPathLength, out int pathLength)
        {
            pathLength = 0;
            if (layer == null || outPath == null) return false;

            if (startX < 0 || startX >= MaxGridSize || startY < 0 || startY >= MaxGridSize) return false;
            if (targetX < 0 || targetX >= MaxGridSize || targetY < 0 || targetY >= MaxGridSize) return false;

            if ((layer.GetMicroCell(startX, startY).Flags & FLAG_WALKABLE) == 0) return false;
            if ((layer.GetMicroCell(targetX, targetY).Flags & FLAG_WALKABLE) == 0) return false;

            if (startX == targetX && startY == targetY)
            {
                if (maxPathLength > 0)
                {
                    outPath[0] = startX;
                    outPath[1] = startY;
                    pathLength = 1;
                }
                return true;
            }

            _openCount = 0;
            Array.Clear(ClosedSet, 0, ClosedSet.Length);
            Array.Clear(ParentMap, 0, ParentMap.Length);
            for (int i = 0; i < GScore.Length; i++) GScore[i] = float.MaxValue;

            // ИСПРАВЛЕНО: Честный плоский индекс 1D массива, защищающий от IndexOutOfRangeException
            int startHash = (startY * MaxGridSize) + startX;
            int targetHash = (targetY * MaxGridSize) + targetX;

            GScore[startHash] = 0;

            PurePathNode startNode = new PurePathNode
            {
                HashIndex = startHash,
                X = startX,
                Y = startY,
                G = 0,
                H = Math.Abs(targetX - startX) + Math.Abs(targetY - startY)
            };
            startNode.F = startNode.H;

            OpenSet[_openCount++] = startNode;
            ParentMap[startHash] = -1;

            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { -1, 1, 0, 0 };

            while (_openCount > 0)
            {
                int bestIndex = 0;
                float minF = OpenSet[bestIndex].F;
                for (int i = 1; i < _openCount; i++)
                {
                    if (OpenSet[i].F < minF)
                    {
                        minF = OpenSet[i].F;
                        bestIndex = i;
                    }
                }

                PurePathNode current = OpenSet[bestIndex];

                if (current.HashIndex == targetHash)
                {
                    int currHash = current.HashIndex;
                    int count = 0;

                    while (currHash != -1 && count < maxPathLength && count < TempPathBuffer.Length / 2)
                    {
                        // ИСПРАВЛЕНО: Обратное декодирование плоского индекса в координаты
                        short cx = (short)(currHash % MaxGridSize);
                        short cy = (short)(currHash / MaxGridSize);

                        TempPathBuffer[count * 2] = cx;
                        TempPathBuffer[count * 2 + 1] = cy;
                        count++;

                        currHash = ParentMap[currHash];
                    }

                    int idx = 0;
                    for (int i = count - 1; i >= 0; i--)
                    {
                        outPath[idx * 2] = TempPathBuffer[i * 2];
                        outPath[idx * 2 + 1] = TempPathBuffer[i * 2 + 1];
                        idx++;
                    }

                    pathLength = count;
                    return true;
                }

                OpenSet[bestIndex] = OpenSet[--_openCount];
                ClosedSet[current.HashIndex] = 1;

                for (int d = 0; d < 4; d++)
                {
                    short nx = (short)(current.X + dx[d]);
                    short ny = (short)(current.Y + dy[d]);

                    if (nx < 0 || nx >= MaxGridSize || ny < 0 || ny >= MaxGridSize) continue;

                    int neighborHash = (ny * MaxGridSize) + nx;

                    if (ClosedSet[neighborHash] == 1) continue;
                    if ((layer.GetMicroCell(nx, ny).Flags & FLAG_WALKABLE) == 0) continue;

                    float tentativeG = GScore[current.HashIndex] + 1.0f;

                    if (tentativeG < GScore[neighborHash])
                    {
                        ParentMap[neighborHash] = current.HashIndex;
                        GScore[neighborHash] = tentativeG;

                        float h = Math.Abs(targetX - nx) + Math.Abs(targetY - ny);
                        float f = tentativeG + h;

                        bool found = false;
                        for (int i = 0; i < _openCount; i++)
                        {
                            if (OpenSet[i].HashIndex == neighborHash)
                            {
                                OpenSet[i].G = tentativeG;
                                OpenSet[i].F = f;
                                found = true;
                                break;
                            }
                        }

                        if (!found && _openCount < OpenSet.Length)
                        {
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
                    }
                }
            }

            return false;
        }
    }
}
