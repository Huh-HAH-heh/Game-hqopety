using System;

namespace Core.Map;

public enum BlockTransitionType : byte
{
    None = 0,
    Ramp = 1,
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
/// Region-compressed surface data for the discrete block world.
/// Uniform regions store one surface level and material. Per-tile arrays
/// are allocated only when a region receives a local surface or transition edit.
/// Solid/air occupancy is stored in the unified SparseVoxelStore.
/// </summary>
public sealed class BlockTerrain
{
    private sealed class RegionData
    {
        private int _uniformSurfaceLevel;
        private ushort _uniformMaterialId;

        private int[]? _surfaceLevels;
        private ushort[]? _materialIds;
        private BlockTransitionType[]? _transitionTypes;
        private BlockDirection[]? _transitionDirections;

        public int Width { get; }
        public int Height { get; }
        private int CellCount => Width * Height;

        public RegionData(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int GetSurfaceLevel(int index) =>
            _surfaceLevels != null
                ? _surfaceLevels[index]
                : _uniformSurfaceLevel;

        public ushort GetMaterialId(int index) =>
            _materialIds != null
                ? _materialIds[index]
                : _uniformMaterialId;

        public BlockTransitionType GetTransitionType(int index) =>
            _transitionTypes != null
                ? _transitionTypes[index]
                : BlockTransitionType.None;

        public BlockDirection GetTransitionDirection(int index) =>
            _transitionDirections != null
                ? _transitionDirections[index]
                : BlockDirection.North;

        public void SetSurfaceLevel(
            int index,
            int level,
            ushort materialId)
        {
            level = Math.Max(0, level);
            materialId = level > 0 ? materialId : (ushort)0;

            if (_surfaceLevels == null &&
                level == _uniformSurfaceLevel &&
                materialId == _uniformMaterialId)
            {
                if (level <= 0)
                    ClearTransition(index);

                return;
            }

            EnsureSurfaceArrays();
            _surfaceLevels![index] = level;
            _materialIds![index] = materialId;

            if (level <= 0)
                ClearTransition(index);
        }

        public void SetUniformSurface(int level, ushort materialId)
        {
            _uniformSurfaceLevel = Math.Max(0, level);
            _uniformMaterialId =
                _uniformSurfaceLevel > 0 ? materialId : (ushort)0;

            _surfaceLevels = null;
            _materialIds = null;
            _transitionTypes = null;
            _transitionDirections = null;
        }

        public void SetTransition(
            int index,
            BlockTransitionType type,
            BlockDirection direction)
        {
            if (type == BlockTransitionType.None &&
                _transitionTypes == null)
            {
                return;
            }

            EnsureTransitionArrays();
            _transitionTypes![index] = type;
            _transitionDirections![index] = direction;
        }

        public void ClearTransitions()
        {
            _transitionTypes = null;
            _transitionDirections = null;
        }

        private void EnsureSurfaceArrays()
        {
            if (_surfaceLevels != null)
                return;

            _surfaceLevels = new int[CellCount];
            _materialIds = new ushort[CellCount];

            Array.Fill(_surfaceLevels, _uniformSurfaceLevel);
            Array.Fill(_materialIds, _uniformMaterialId);
        }

        private void EnsureTransitionArrays()
        {
            if (_transitionTypes != null)
                return;

            _transitionTypes = new BlockTransitionType[CellCount];
            _transitionDirections = new BlockDirection[CellCount];
        }

        private void ClearTransition(int index)
        {
            if (_transitionTypes == null)
                return;

            _transitionTypes[index] = BlockTransitionType.None;
            _transitionDirections![index] = BlockDirection.North;
        }
    }

    private readonly RegionData[] _regions;
    private readonly int _regionsX;
    private readonly int _regionsY;

    public int Width { get; }
    public int Height { get; }
    // Edits must pass through WorldMap so its navigation and render caches stay coherent.
    internal SparseVoxelStore Volumes { get; }

    public BlockTerrain(int width, int height, int depth = 500)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Width = width;
        Height = height;
        Volumes = new SparseVoxelStore(width, height, depth);

        int regionSize = TerrainRegion.TilesPerSide;
        _regionsX = (width + regionSize - 1) / regionSize;
        _regionsY = (height + regionSize - 1) / regionSize;
        _regions = new RegionData[checked(_regionsX * _regionsY)];

        for (int regionY = 0; regionY < _regionsY; regionY++)
        {
            for (int regionX = 0; regionX < _regionsX; regionX++)
            {
                int regionWidth = Math.Min(
                    regionSize,
                    width - regionX * regionSize);
                int regionHeight = Math.Min(
                    regionSize,
                    height - regionY * regionSize);

                _regions[regionX + regionY * _regionsX] =
                    new RegionData(regionWidth, regionHeight);
            }
        }
    }

    public int GetSurfaceLevel(int x, int y) =>
        IsInside(x, y)
            ? GetRegion(x, y).GetSurfaceLevel(GetLocalIndex(x, y))
            : -1;

    public ushort GetMaterialId(int x, int y) =>
        IsInside(x, y)
            ? GetRegion(x, y).GetMaterialId(GetLocalIndex(x, y))
            : (ushort)0;

    public BlockTransitionType GetTransitionType(int x, int y) =>
        IsInside(x, y)
            ? GetRegion(x, y).GetTransitionType(GetLocalIndex(x, y))
            : BlockTransitionType.None;

    public BlockDirection GetTransitionDirection(int x, int y) =>
        IsInside(x, y)
            ? GetRegion(x, y).GetTransitionDirection(GetLocalIndex(x, y))
            : BlockDirection.North;

    public bool IsTransitionCell(int x, int y) =>
        GetTransitionType(x, y) != BlockTransitionType.None;

    internal void SetSurfaceRegion(
        int startX,
        int startY,
        int width,
        int height,
        int level,
        ushort materialId)
    {
        if (startX < 0 || startY < 0 ||
            width <= 0 || height <= 0 ||
            (long)startX + width > Width ||
            (long)startY + height > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int regionSize = TerrainRegion.TilesPerSide;
        if (startX % regionSize == 0 &&
            startY % regionSize == 0)
        {
            int regionX = startX / regionSize;
            int regionY = startY / regionSize;
            RegionData region = _regions[regionX + regionY * _regionsX];

            if (width == region.Width && height == region.Height)
            {
                region.SetUniformSurface(level, materialId);
                return;
            }
        }

        int normalizedLevel = Math.Max(0, level);
        ushort normalizedMaterial =
            normalizedLevel > 0 ? materialId : (ushort)0;

        for (int row = 0; row < height; row++)
        {
            int y = startY + row;
            for (int column = 0; column < width; column++)
            {
                int x = startX + column;
                SetSurfaceLevel(x, y, normalizedLevel, normalizedMaterial);
            }
        }
    }

    internal void SetSurfaceLevel(
        int x,
        int y,
        int level,
        ushort materialId)
    {
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));

        GetRegion(x, y).SetSurfaceLevel(
            GetLocalIndex(x, y),
            level,
            materialId);
    }

    internal void SetTransition(
        int x,
        int y,
        BlockTransitionType type,
        BlockDirection direction)
    {
        if (!IsInside(x, y))
            throw new ArgumentOutOfRangeException(nameof(x));

        GetRegion(x, y).SetTransition(
            GetLocalIndex(x, y),
            type,
            direction);
    }

    internal void ClearTransitions()
    {
        for (int i = 0; i < _regions.Length; i++)
            _regions[i].ClearTransitions();
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

        if (fromLevel == toLevel)
            return true;

        int lowerX = fromLevel < toLevel ? fromX : toX;
        int lowerY = fromLevel < toLevel ? fromY : toY;
        int upperX = fromLevel < toLevel ? toX : fromX;
        int upperY = fromLevel < toLevel ? toY : fromY;
        int rise = Math.Abs(toLevel - fromLevel);
        BlockTransitionType type = GetTransitionType(lowerX, lowerY);

        if (type == BlockTransitionType.None ||
            !DirectionMatches(
                GetTransitionDirection(lowerX, lowerY),
                upperX - lowerX,
                upperY - lowerY))
        {
            return false;
        }

        return type switch
        {
            BlockTransitionType.Ramp => rise == 1,
            BlockTransitionType.Stairs => rise is >= 1 and <= 3,
            _ => false
        };
    }

    /// <summary>
    /// Only ramp and stair tiles change Z within a tile. Other block surfaces
    /// are perfectly level; the legacy bilinear terrain is not sampled here.
    /// </summary>
    public float SampleSurfaceHeight(float x, float y)
    {
        int tileX = Math.Clamp((int)MathF.Floor(x), 0, Width - 1);
        int tileY = Math.Clamp((int)MathF.Floor(y), 0, Height - 1);
        float level = GetSurfaceLevel(tileX, tileY);
        if (level < 0f)
            return 0f;

        BlockTransitionType type = GetTransitionType(tileX, tileY);
        if (type == BlockTransitionType.None)
            return level;

        GetDirectionDelta(
            GetTransitionDirection(tileX, tileY),
            out int dx,
            out int dy);
        int rise = GetSurfaceLevel(tileX + dx, tileY + dy) - (int)level;
        if (rise <= 0 ||
            (type == BlockTransitionType.Ramp && rise != 1) ||
            (type == BlockTransitionType.Stairs && rise > 3))
        {
            return level;
        }

        float localX = Math.Clamp(x - tileX, 0f, 1f);
        float localY = Math.Clamp(y - tileY, 0f, 1f);
        float progress = dx > 0 ? localX
            : dx < 0 ? 1f - localX
            : dy > 0 ? localY
            : 1f - localY;
        progress = Math.Clamp(progress, 0f, 1f);

        if (type == BlockTransitionType.Ramp)
            return level + progress * rise;

        int steps = Math.Max(3, rise * 3);
        int completed = Math.Clamp(
            (int)MathF.Floor(progress * steps + 0.0001f),
            0,
            steps);
        return level + rise * (completed / (float)steps);
    }

    private RegionData GetRegion(int x, int y)
    {
        int regionX = x / TerrainRegion.TilesPerSide;
        int regionY = y / TerrainRegion.TilesPerSide;
        return _regions[regionX + regionY * _regionsX];
    }

    private int GetLocalIndex(int x, int y)
    {
        RegionData region = GetRegion(x, y);
        return x % TerrainRegion.TilesPerSide +
               y % TerrainRegion.TilesPerSide * region.Width;
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

    private static void GetDirectionDelta(
        BlockDirection direction,
        out int dx,
        out int dy)
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
