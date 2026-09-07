using System;
using Core.Unit.Components;

namespace Core.AI;

public sealed class PureAStarContext
{
    public readonly PurePathNode[] OpenSet;
    public readonly byte[] ClosedSet;
    public readonly int[] ParentMap;
    public readonly float[] GScore;
    public readonly SpatialCoord[] TempPathBuffer;

    public int OpenCount;

    public PureAStarContext(
        int maxGridCells,
        int maxPathLength)
    {
        OpenSet =
            new PurePathNode[
                maxGridCells];

        ClosedSet =
            new byte[
                maxGridCells];

        ParentMap =
            new int[
                maxGridCells];

        GScore =
            new float[
                maxGridCells];

        TempPathBuffer =
            new SpatialCoord[
                maxPathLength];
    }

    public void Reset()
    {
        OpenCount = 0;

        Array.Clear(
            ClosedSet,
            0,
            ClosedSet.Length);

        Array.Fill(
            ParentMap,
            -1);

        Array.Fill(
            GScore,
            float.MaxValue);
    }
}