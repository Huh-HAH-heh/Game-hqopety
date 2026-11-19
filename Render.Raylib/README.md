# Raylib terrain renderer

An isolated presentation path for RimClone. It does not reference the legacy SFML
renderer and does not own world-generation or simulation state.

## Responsibilities

- `TerrainRenderSnapshot` copies terrain surface data into flat presentation arrays.
- `RaylibTerrainRenderer` owns GPU resources and draws cached storage-region batches.
- `RegionMeshBuilder` builds one GPU mesh per 192x192 storage region.
- `OrbitCameraController` owns presentation camera state and input.
- `RaylibTerrainViewer` owns the window and frame loop.

## Terrain region layout

The terrain presentation overlay is divided into **4x4-tile render regions**.
The existing world/simulation storage remains at 192x192 tiles for now; this keeps
the 500-level world data layout and the 2304x2304 world dimensions unchanged.

Each 192x192 storage region contains 48x48 render regions. Those small regions
are sampled independently into the presentation snapshot, then combined into one
GPU mesh per storage region. Adjacent patches with identical height and material
are merged into larger faces; height discontinuities keep their own cliff faces.
This keeps local terrain sampling fine-grained while avoiding hundreds of thousands
of GPU objects and draw calls.

## Data flow

```text
WorldMap / WorldRegion (192x192 storage)
        |
        v
TerrainRenderSnapshot (4x4 render regions, flat arrays + revisions)
        |
        v
RegionMeshBuilder (one batched mesh per storage region)
        |
        v
RaylibTerrainRenderer (GPU batches + frustum culling)
        |
        v
Raylib window
```

The renderer reads the snapshot only. It does not query WorldMap during drawing.
Storage-region revisions invalidate only affected GPU batches (plus adjacent
batches whose shared cliff edges may have changed). GPU resource creation,
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

The viewer restores the earlier winding canyon, mesa and rimmed basin layout.
Heights are generated at the 4x4-tile render-patch scale and stored in compact
surface arrays; vertical voxel layers are not materialized during preview
generation. The render batches remain grouped by the larger 192x192 storage
regions.


## Top-down terrain style

The initial camera is nearly vertical. Surface patches use a black-to-charcoal
height palette; thin white contour strokes appear only along height transitions.
There is no permanent white tile grid, so the lines identify relief details
instead of covering the map.
