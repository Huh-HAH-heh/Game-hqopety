using System;

namespace Core.Map;

public enum BlockTransitionType : byte
{
    None = 0,
    Stairs = 2
}

public enum BlockDirection : byte
{
    North = 0,
    East = 1,
    South = 2,
    West = 3
}

/// <summary>
/// Surface connectivity for discrete terrain. Generated terrain patches use
/// a 4 x 4 m horizontal footprint and 1 m vertical layers. Surface samples
/// stay on the 1 m navigation grid so units can still move inside each patch.
/// </summary>
public sealed class BlockTerrain
{
    public const int HorizontalBlockSize = 4;
    public const int VerticalBlockHeightMeters = 1;
    private readonly int[] _surfaceLevels;
    private readonly ushort[] _materialIds;
    private readonly BlockTransitionType[] _transitionTypes;
    private readonly BlockDirection[] _transitionDirections;

    public int Width { get; }
    public int Height { get; }

    public BlockTerrain(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        int count = checked(width * height);
        _surfaceLevels = new int[count];
        _materialIds = new ushort[count];
        _transitionTypes = new BlockTransitionType[count];
        _transitionDirections = new BlockDirection[count];
    }

    public int GetSurfaceLevel(int x, int y) =>
        IsInside(x, y) ? _surfaceLevels[x + y * Width] : -1;

    public ushort GetMaterialId(int x, int y) =>
        IsInside(x, y) ? _materialIds[x + y * Width] : (ushort)0;

    public BlockTransitionType GetTransitionType(int x, int y) =>
        IsInside(x, y) ? _transitionTypes[x + y * Width] : BlockTransitionType.None;

    public BlockDirection GetTransitionDirection(int x, int y) =>
        IsInside(x, y) ? _transitionDirections[x + y * Width] : BlockDirection.North;

    public bool IsTransitionCell(int x, int y) =>
        GetTransitionType(x, y) != BlockTransitionType.None;

    internal void SetSurfaceLevel(int x, int y, int level, ushort materialId)
    {
        if (!IsInside(x, y)) throw new ArgumentOutOfRangeException(nameof(x));
        int index = x + y * Width;
        _surfaceLevels[index] = Math.Max(0, level);
        _materialIds[index] = level > 0 ? materialId : (ushort)0;
        if (level <= 0)
        {
            _transitionTypes[index] = BlockTransitionType.None;
            _transitionDirections[index] = BlockDirection.North;
        }
    }

    internal void SetTransition(
        int x, int y, BlockTransitionType type, BlockDirection direction)
    {
        if (!IsInside(x, y)) throw new ArgumentOutOfRangeException(nameof(x));
        int index = x + y * Width;
        _transitionTypes[index] = type;
        _transitionDirections[index] = direction;
    }

    internal void ClearTransitions()
    {
        Array.Clear(_transitionTypes);
        Array.Clear(_transitionDirections);
    }

    /// <summary>Tests the explicit connector between adjacent surface columns.</summary>
    public bool CanTraverse(int fromX, int fromY, int toX, int toY)
    {
        if (!IsInside(fromX, fromY) || !IsInside(toX, toY))
            return false;

        int dx = toX - fromX;
        int dy = toY - fromY;
        if (Math.Abs(dx) + Math.Abs(dy) != 1)
            return false;

        int fromLevel = GetSurfaceLevel(fromX, fromY);
        int toLevel = GetSurfaceLevel(toX, toY);
        if (fromLevel <= 0 || toLevel <= 0)
            return false;

        // A horizontal move between equal-height block tops needs no connector.
        if (fromLevel == toLevel)
            return true;

        int lowerX = fromLevel < toLevel ? fromX : toX;
        int lowerY = fromLevel < toLevel ? fromY : toY;
        int upperX = fromLevel < toLevel ? toX : fromX;
        int upperY = fromLevel < toLevel ? toY : fromY;
        int rise = Math.Abs(toLevel - fromLevel);
        if (GetTransitionType(lowerX, lowerY) != BlockTransitionType.Stairs ||
            !DirectionMatches(
                GetTransitionDirection(lowerX, lowerY),
                upperX - lowerX,
                upperY - lowerY))
        {
            return false;
        }

        return rise is >= 1 and <= 3;
    }

    /// <summary>
    /// Only stair connectors change Z within a tile. Other block surfaces
    /// are perfectly level; the legacy bilinear terrain is not sampled here.
    /// </summary>
    public float SampleSurfaceHeight(float x, float y)
    {
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, Width - 1);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, Height - 1);
        float level = GetSurfaceLevel(tileX, tileY);
        if (level < 0f)
            return 0f;

        if (GetTransitionType(tileX, tileY) != BlockTransitionType.Stairs)
            return level;

        GetDirectionDelta(GetTransitionDirection(tileX, tileY), out int dx, out int dy);
        int rise = GetSurfaceLevel(tileX + dx, tileY + dy) - (int)level;
        if (rise is < 1 or > 3)
            return level;

        float localX = Math.Clamp(x - tileX, 0f, 1f);
        float localY = Math.Clamp(y - tileY, 0f, 1f);
        float progress = dx > 0 ? localX
            : dx < 0 ? 1f - localX
            : dy > 0 ? localY
            : 1f - localY;
        progress = Math.Clamp(progress, 0f, 1f);

        int steps = Math.Max(3, rise * 3);
        int completed = Math.Clamp((int)MathF.Floor(progress * steps + 0.0001f), 0, steps);
        return level + rise * (completed / (float)steps);
    }

    private bool IsInside(int x, int y) =>
        x >= 0 && y >= 0 && x < Width && y < Height;

    private static bool DirectionMatches(BlockDirection direction, int dx, int dy) =>
        direction switch
        {
            BlockDirection.North => dx == 0 && dy == -1,
            BlockDirection.East => dx == 1 && dy == 0,
            BlockDirection.South => dx == 0 && dy == 1,
            BlockDirection.West => dx == -1 && dy == 0,
            _ => false
        };

    private static void GetDirectionDelta(BlockDirection direction, out int dx, out int dy)
    {
        dx = direction switch
        {
            BlockDirection.East => 1,
            BlockDirection.West => -1,
            _ => 0
        };
        dy = direction switch
        {
            BlockDirection.South => 1,
            BlockDirection.North => -1,
            _ => 0
        };
    }
}
