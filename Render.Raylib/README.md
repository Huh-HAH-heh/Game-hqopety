# Raylib terrain renderer

An isolated presentation path for RimClone. It does not reference the legacy SFML
renderer and does not own world-generation or simulation state.

## Responsibilities

- `TerrainRenderSnapshot` copies terrain surface data into flat presentation arrays.
- `RaylibTerrainRenderer` owns GPU resources and draws region meshes.
- `RegionMeshBuilder` generates one cached mesh per world region.
- `OrbitCameraController` owns presentation camera state and input.
- `RaylibTerrainViewer` owns the window and frame loop.

## Data flow

```text
WorldMap / WorldRegion
        |
        v
TerrainRenderSnapshot (flat arrays + region revisions)
        |
        v
RegionMeshBuilder (CPU mesh data)
        |
        v
RaylibTerrainRenderer (GPU meshes + frustum culling)
        |
        v
Raylib window
```

The renderer reads the snapshot only. It does not query WorldMap during drawing.
Region revisions invalidate only the affected GPU mesh. GPU resource creation,
upload, draw and disposal are restricted to the render thread.

## Controls

- WASD: move in the camera's horizontal plane
- Right mouse button + drag: smooth orbit rotation
- Middle mouse button + drag: pan across the terrain
- Mouse wheel: smooth zoom
- Left Shift / Right Shift: faster movement
- Q / E: move camera target vertically
- Escape: exit

## Current milestone

The current world generator produces one uniform surface height per region, so
each region is rendered as a cached top patch with vertical cliff faces at height
discontinuities. This deliberately avoids per-tile draw calls. Dense, locally
edited region surfaces and distance-based LOD are subsequent renderer milestones.
