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

- WASD: move in the camera's horizontal plane at a zoom-scaled RTS speed
- Right mouse button + drag: orbit; dragging right rotates the camera to the right
- Middle mouse button + drag: pan in screen space with tilt-aware ground scaling
- Mouse wheel: zoom toward the cursor position, keeping the pointed-at ground area anchored
- Left Shift / Right Shift: faster movement
- Q / E: move camera target vertically
- Escape: exit

## Static cave and tunnel proof

`WorldGenerator.GenerateSmooth` now carves a real underground system into the
static height-field terrain after generating the mesa:

- a 52 m horizontal tunnel, 12 m wide and 5 m high;
- a connected 20 x 20 m chamber, 8 m high;
- a 4 x 4 m shaft cut from the surface down to the chamber.

`WorldMap.CarveStaticVolume` writes empty overrides into the sparse 3D
`BlockVolumeTree`. It does not enable discrete block simulation and does not
materialize the full terrain into individual voxels. `GetMaterialId(x,y,z)` and
`GetTileRanges(x,y)` resolve the empty interval over the implicit solid column.
World generation checks that the tunnel is empty at 16 m, its floor remains solid,
the rock beside it remains solid, and the shaft lowers the surface.

Press **C** in the Raylib viewer to inspect the horizontal 16 m section. The renderer
samples actual 3D material occupancy into the 4 x 4 m presentation grid: dark rock
is drawn, empty tunnel cells are omitted, and white strokes mark the boundary
between rock and air. The camera focuses on the test mesa automatically. Press C
again to return to the normal height-map view.

## Current milestone

The viewer restores the earlier winding canyon, mesa and rimmed basin layout.
Heights are generated at the 4x4-tile render-patch scale and stored in compact
surface arrays; vertical voxel layers are not materialized during preview
generation. The render batches remain grouped by the larger 192x192 storage
regions.


## Top-down terrain style

The initial camera is nearly vertical, with a camera-relative up axis so the
view basis remains stable instead of becoming parallel to world-up. The camera
fits the whole map for the current window aspect ratio, pans at a zoom-aware
speed, and zooms toward the cursor. Surface patches use a black-to-charcoal
height palette. Cliff faces carry white elevation marks only at 5 m intervals;
their brightness alternates between bright white and muted gray. There is no
permanent tile grid.
