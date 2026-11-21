using System;
using System.Collections.Generic;
using Core.Map;
using SFML.Graphics;
using SFML.System;

namespace RimClone.Render;

/// <summary>
/// Draws each independent voxel terrain with its own mesh cache.
/// The translated view places local terrain coordinates at its world origin.
/// </summary>
public sealed class IndependentVoxelTerrainRenderer : IDisposable
{
    private readonly Dictionary<int, MapRenderSystem> _renderers = new();
    private readonly List<int> _staleIds = new();

    public void Draw(
        RenderWindow window,
        View worldView,
        VoxelTerrainSystem terrainSystem,
        float tilePixelSize,
        float zoomLevel)
    {
        if (terrainSystem.Count == 0 || tilePixelSize <= 0f)
            return;

        try
        {
            IReadOnlyList<VoxelTerrain> terrains = terrainSystem.Terrains;
            for (int i = 0; i < terrains.Count; i++)
            {
                VoxelTerrain terrain = terrains[i];
                if (!terrain.Visible)
                    continue;

                if (!_renderers.TryGetValue(terrain.Id, out MapRenderSystem? renderer))
                {
                    renderer = new MapRenderSystem(tilePixelSize);
                    _renderers.Add(terrain.Id, renderer);
                }

                // Subtracting the object's world origin from the camera centre
                // makes local mesh coordinate (0,0) appear at that world origin.
                using var localView = new View(
                    new Vector2f(
                        worldView.Center.X - terrain.OriginX * tilePixelSize,
                        worldView.Center.Y - terrain.OriginY * tilePixelSize),
                    worldView.Size);
                localView.Rotation = worldView.Rotation;

                window.SetView(localView);
                renderer.Draw(
                    window,
                    terrain.Map,
                    localView,
                    tilePixelSize,
                    zoomLevel,
                    false,
                    Math.Clamp(terrain.VisibleMaxLayer, 0, terrain.Map.LayerCount - 1));
            }

            PruneRemovedTerrains(terrainSystem);
        }
        finally
        {
            window.SetView(worldView);
        }
    }

    public void InvalidateGraphicsResources()
    {
        foreach (MapRenderSystem renderer in _renderers.Values)
            renderer.InvalidateGraphicsResources();
    }

    private void PruneRemovedTerrains(VoxelTerrainSystem terrainSystem)
    {
        _staleIds.Clear();
        foreach (int id in _renderers.Keys)
        {
            bool found = false;
            IReadOnlyList<VoxelTerrain> terrains = terrainSystem.Terrains;
            for (int i = 0; i < terrains.Count; i++)
            {
                if (terrains[i].Id == id)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                _staleIds.Add(id);
        }

        for (int i = 0; i < _staleIds.Count; i++)
        {
            int id = _staleIds[i];
            if (_renderers.Remove(id, out MapRenderSystem? renderer))
                renderer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (MapRenderSystem renderer in _renderers.Values)
            renderer.Dispose();

        _renderers.Clear();
        _staleIds.Clear();
    }
}
