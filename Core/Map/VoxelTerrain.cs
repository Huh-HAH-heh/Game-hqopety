using System;
using System.Collections.Generic;

namespace Core.Map;

/// <summary>
/// One independent voxel terrain object placed in shared world coordinates.
/// Every instance owns a separate WorldMap, voxel layer set and volume octree.
/// OriginX/OriginY are measured in one-metre world cells.
/// </summary>
public sealed class VoxelTerrain
{
    public int Id { get; }
    public string Name { get; }
    public WorldMap Map { get; }

    public int Width => Map.TileWidth;
    public int Height => Map.TileHeight;
    public int LayerCount => Map.LayerCount;

    public float OriginX { get; private set; }
    public float OriginY { get; private set; }
    public bool Visible { get; set; } = true;

    /// <summary>Highest local voxel layer available to the renderer.</summary>
    public int VisibleMaxLayer { get; set; }

    internal VoxelTerrain(
        int id,
        string name,
        float originX,
        float originY,
        int regionsX,
        int regionsY,
        int layerCount)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Terrain name is required.", nameof(name));
        ValidateOrigin(originX, nameof(originX));
        ValidateOrigin(originY, nameof(originY));

        Id = id;
        Name = name;
        OriginX = originX;
        OriginY = originY;

        // A terrain owns its own map storage. It is not a slice or alias of
        // the gameplay map, so edits cannot mutate another terrain instance.
        Map = new WorldMap(regionsX, regionsY, layerCount);
        Map.EnableDiscreteBlockTerrain();
        VisibleMaxLayer = layerCount - 1;
    }

    public void MoveTo(float originX, float originY)
    {
        ValidateOrigin(originX, nameof(originX));
        ValidateOrigin(originY, nameof(originY));
        OriginX = originX;
        OriginY = originY;
    }

    /// <summary>
    /// Sets one 1 x 1 x 0.1 m voxel in local terrain coordinates.
    /// Material ID zero removes the voxel.
    /// </summary>
    public void SetVoxel(int x, int y, int zLayer, ushort materialId) =>
        Map.SetMaterialId(x, y, zLayer, materialId);

    public ushort GetVoxel(int x, int y, int zLayer) =>
        Map.GetMaterialId(x, y, zLayer);

    /// <summary>
    /// Fills one cuboid. X/Y dimensions use one-metre cells;
    /// Z and height use metres. The material is local to this terrain.
    /// </summary>
    public void FillVolume(
        int x,
        int y,
        int zMeters,
        int width,
        int depth,
        int heightMeters,
        ushort materialId)
    {
        if (materialId == 0)
            throw new ArgumentOutOfRangeException(nameof(materialId), "Use CutVolume to remove terrain.");
        Map.SetBlockVolume(x, y, zMeters, width, depth, heightMeters, materialId);
    }

    /// <summary>Removes a cuboid without changing any other terrain instance.</summary>
    public void CutVolume(
        int x,
        int y,
        int zMeters,
        int width,
        int depth,
        int heightMeters) =>
        Map.SetBlockVolume(x, y, zMeters, width, depth, heightMeters, 0);

    public void SetBlock(int x, int y, int zMeters, ushort materialId) =>
        Map.SetBlock(x, y, zMeters, materialId);

    public void RemoveBlock(int x, int y, int zMeters) =>
        Map.RemoveBlock(x, y, zMeters);

    private static void ValidateOrigin(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// Owns any number of spatially independent voxel terrains.
/// Instances share world placement only; voxel data and edits stay isolated.
/// </summary>
public sealed class VoxelTerrainSystem
{
    private readonly List<VoxelTerrain> _terrains = new();
    private readonly IReadOnlyList<VoxelTerrain> _readOnlyTerrains;
    private int _nextId = 1;

    public IReadOnlyList<VoxelTerrain> Terrains => _readOnlyTerrains;
    public int Count => _terrains.Count;

    public VoxelTerrainSystem()
    {
        _readOnlyTerrains = _terrains.AsReadOnly();
    }

    public VoxelTerrain Create(
        string name,
        float originX,
        float originY,
        int regionsX = 1,
        int regionsY = 1,
        int layerCount = 300)
    {
        if (regionsX <= 0) throw new ArgumentOutOfRangeException(nameof(regionsX));
        if (regionsY <= 0) throw new ArgumentOutOfRangeException(nameof(regionsY));
        if (layerCount <= 0 || layerCount > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(layerCount));
        if (_nextId == int.MaxValue)
            throw new InvalidOperationException("Voxel terrain ID space is exhausted.");

        var terrain = new VoxelTerrain(
            _nextId++,
            name,
            originX,
            originY,
            regionsX,
            regionsY,
            layerCount);
        _terrains.Add(terrain);
        return terrain;
    }

    public bool TryGet(int id, out VoxelTerrain? terrain)
    {
        for (int i = 0; i < _terrains.Count; i++)
        {
            if (_terrains[i].Id != id)
                continue;

            terrain = _terrains[i];
            return true;
        }

        terrain = null;
        return false;
    }

    public bool Remove(int id)
    {
        for (int i = 0; i < _terrains.Count; i++)
        {
            if (_terrains[i].Id != id)
                continue;

            _terrains.RemoveAt(i);
            return true;
        }

        return false;
    }

    public void Clear() => _terrains.Clear();
}
