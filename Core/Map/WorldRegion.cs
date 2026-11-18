using System;

namespace Core.Map;

/// <summary>
/// Logical XY region and the unit used by world generation and simulation.
/// It groups all vertical terrain chunks for one 192 x 192 tile area.
/// </summary>
public sealed class WorldRegion
{
    public const int TilesPerSide = TerrainRegion.TilesPerSide;
    public const int TileCount = TerrainRegion.TotalTiles;

    private readonly TerrainRegion[] _terrainSlices;

    public int RegionX { get; }
    public int RegionY { get; }

    public int TileOriginX =>
        RegionX * TilesPerSide;

    public int TileOriginY =>
        RegionY * TilesPerSide;

    public int TileWidth =>
        TilesPerSide;

    public int TileHeight =>
        TilesPerSide;

    public int LayerCount =>
        _terrainSlices.Length;

    /// <summary>
    /// Changes whenever terrain inside this region is edited.
    /// </summary>
    public long TerrainVersion { get; private set; }

    public bool HasUniformSurface { get; private set; }
    public ushort UniformSurfaceHeightUnits { get; private set; }
    public ushort UniformSurfaceMaterialId { get; private set; }

    /// <summary>
    /// The region's tile grid. Tile metadata is allocated lazily by WorldMap.
    /// </summary>
    public ReadOnlySpan<TerrainRegion> TerrainSlices =>
        _terrainSlices;

    public ReadOnlySpan<Tile> Tiles =>
        GetTerrainSlice(0).Tiles;

    internal WorldRegion(
        int regionX,
        int regionY,
        TerrainLayer[] layers)
    {
        if (regionX < 0)
            throw new ArgumentOutOfRangeException(nameof(regionX));

        if (regionY < 0)
            throw new ArgumentOutOfRangeException(nameof(regionY));

        if (layers == null || layers.Length == 0)
            throw new ArgumentException(
                "WorldRegion requires at least one terrain layer.",
                nameof(layers));

        RegionX = regionX;
        RegionY = regionY;

        _terrainSlices = new TerrainRegion[layers.Length];

        for (int z = 0; z < layers.Length; z++)
        {
            _terrainSlices[z] = layers[z].GetRegion(regionX, regionY) ??
                throw new InvalidOperationException(
                    "Terrain layer does not contain the requested region.");
        }
    }

    public TerrainRegion GetTerrainSlice(int zLevel)
    {
        if (zLevel < 0 || zLevel >= _terrainSlices.Length)
            throw new ArgumentOutOfRangeException(nameof(zLevel));

        return _terrainSlices[zLevel];
    }

    public ushort GetMaterialId(
        int localX,
        int localY,
        int zLevel)
    {
        if (!ContainsLocalTile(localX, localY))
            return 0;

        return GetTerrainSlice(zLevel).GetMaterialId(localX, localY);
    }

    public ref Tile GetTile(
        int localX,
        int localY)
    {
        if (!ContainsLocalTile(localX, localY))
            throw new ArgumentOutOfRangeException(nameof(localX));

        return ref GetTerrainSlice(0).GetLocalTile(localX, localY);
    }

    public bool ContainsWorldTile(
        int globalX,
        int globalY)
    {
        return globalX >= TileOriginX &&
               globalY >= TileOriginY &&
               globalX < TileOriginX + TileWidth &&
               globalY < TileOriginY + TileHeight;
    }

    public bool TryGetLocalCoordinates(
        int globalX,
        int globalY,
        out int localX,
        out int localY)
    {
        localX = globalX - TileOriginX;
        localY = globalY - TileOriginY;

        return ContainsLocalTile(localX, localY);
    }

    internal void SetUniformSurface(
        ushort heightUnits,
        ushort materialId)
    {
        UniformSurfaceHeightUnits = heightUnits;
        UniformSurfaceMaterialId = heightUnits > 0 ? materialId : (ushort)0;
        HasUniformSurface = true;
        IncrementTerrainVersion();
    }

    internal void InvalidateUniformSurface()
    {
        HasUniformSurface = false;
        UniformSurfaceHeightUnits = 0;
        UniformSurfaceMaterialId = 0;
    }

    internal void MarkTerrainChanged()
    {
        InvalidateUniformSurface();
        IncrementTerrainVersion();
    }

    internal void MarkGeometryChanged()
    {
        IncrementTerrainVersion();
    }

    private void IncrementTerrainVersion()
    {
        unchecked
        {
            TerrainVersion++;
        }
    }

    private static bool ContainsLocalTile(
        int localX,
        int localY)
    {
        return localX >= 0 &&
               localY >= 0 &&
               localX < TilesPerSide &&
               localY < TilesPerSide;
    }
}
