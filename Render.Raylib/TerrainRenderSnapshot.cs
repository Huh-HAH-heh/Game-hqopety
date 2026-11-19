using Core.Map;

namespace RimClone.Render.Raylib;

/// <summary>
/// Presentation-owned, compact copy of the world surface.
/// The renderer reads only these flat arrays and never queries WorldMap while drawing.
/// </summary>
public sealed class TerrainRenderSnapshot
{
    private readonly float[] _heightsMeters;
    private readonly ushort[] _materialIds;
    private readonly long[] _regionVersions;

    public int RegionsX { get; }
    public int RegionsY { get; }
    public int RegionSize { get; }
    public int RegionCount => _heightsMeters.Length;

    public float WorldWidth => RegionsX * RegionSize;
    public float WorldDepth => RegionsY * RegionSize;
    public float MaximumHeightMeters { get; private set; }
    public long SourceWorldVersion { get; private set; }

    internal float[] HeightsMeters => _heightsMeters;
    internal ushort[] MaterialIds => _materialIds;
    internal long[] RegionVersions => _regionVersions;

    private TerrainRenderSnapshot(WorldMap worldMap)
    {
        RegionsX = worldMap.RegionsX;
        RegionsY = worldMap.RegionsY;
        RegionSize = TerrainRegion.TilesPerSide;

        int count = checked(RegionsX * RegionsY);
        _heightsMeters = new float[count];
        _materialIds = new ushort[count];
        _regionVersions = new long[count];

        Array.Fill(_regionVersions, long.MinValue);
        RefreshFromWorld(worldMap);
    }

    public static TerrainRenderSnapshot Create(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);
        return new TerrainRenderSnapshot(worldMap);
    }

    /// <summary>
    /// Copies only regions whose terrain revision changed.
    /// Call on the owning/game thread, never from the render worker.
    /// </summary>
    public bool RefreshFromWorld(WorldMap worldMap)
    {
        ArgumentNullException.ThrowIfNull(worldMap);

        if (worldMap.RegionsX != RegionsX ||
            worldMap.RegionsY != RegionsY)
        {
            throw new InvalidOperationException(
                "World topology changed. Create a new terrain snapshot.");
        }

        if (SourceWorldVersion == worldMap.TerrainVersion &&
            _regionVersions.All(version => version != long.MinValue))
        {
            return false;
        }

        bool changed = false;
        float maximumHeight = 0f;
        ReadOnlySpan<WorldRegion> regions = worldMap.WorldRegions;

        for (int i = 0; i < regions.Length; i++)
        {
            WorldRegion region = regions[i];
            if (_regionVersions[i] != region.TerrainVersion)
            {
                float heightMeters;
                ushort materialId;

                if (region.HasUniformSurface)
                {
                    heightMeters = region.UniformSurfaceHeightUnits * 0.1f;
                    materialId = region.UniformSurfaceMaterialId;
                }
                else
                {
                    int sampleX = region.TileOriginX + region.TileWidth / 2;
                    int sampleY = region.TileOriginY + region.TileHeight / 2;
                    ushort heightUnits = worldMap.GetSurfaceHeightUnits(sampleX, sampleY);

                    heightMeters = heightUnits * 0.1f;
                    materialId = 0;

                    if (heightUnits > 0)
                    {
                        int topLayer = Math.Clamp(
                            (heightUnits + WorldMap.LayerHeightUnits - 1) /
                            WorldMap.LayerHeightUnits - 1,
                            0,
                            worldMap.LayerCount - 1);

                        materialId = worldMap.GetMaterialId(
                            sampleX,
                            sampleY,
                            topLayer);
                    }
                }

                _heightsMeters[i] = heightMeters;
                _materialIds[i] = materialId;
                _regionVersions[i] = region.TerrainVersion;
                changed = true;
            }

            maximumHeight = MathF.Max(maximumHeight, _heightsMeters[i]);
        }

        MaximumHeightMeters = maximumHeight;
        SourceWorldVersion = worldMap.TerrainVersion;
        return changed;
    }
}
