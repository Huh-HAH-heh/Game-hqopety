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
- Middle mouse button + drag: screen-space pan with a tilt-aware ground scale
- Mouse wheel: zoom toward the cursor position when the focus-plane intersection is valid
- Left Shift / Right Shift: faster movement
- Q / E: move the camera focus and cut plane vertically
- PageUp / PageDown: move the cut plane by exactly one metre
- T: return to the fitted top-down view
- Escape: exit

## Static multi-level cave and volumetric cutaway

`WorldGenerator.GenerateSmooth` builds two enclosed underground passages inside
the mesa. Neither passage opens to the surface or to the outside edge of the hill:

- lower tunnel: 64 m long, 8 m wide, 5 m high at 14 m depth;
- lower chamber: 20 x 20 m, 8 m high at 12 m depth;
- upper tunnel: 56 m long, 8 m wide, 5 m high at 26 m depth;
- upper chamber: 20 x 20 m, 8 m high at 24 m depth;
- internal connector: 6 x 6 m, joining the two cave levels between 18 m and 26 m.

Two additional solid material bands at 8-10 m and 26-29 m demonstrate that
different materials can coexist inside the same terrain volume. These are real
volume materials, not surface-colour overlays.

`WorldMap.CarveStaticVolume` writes empty overrides into the sparse 3D
`BlockVolumeTree`. The underlying terrain remains an implicit solid column;
the generator does not materialize the whole 500-layer map into individual
voxels. The render snapshot extracts only internal empty intervals at the
4 x 4 m presentation scale. The GPU mesh builds cave side walls from those
intervals and removes shared interior walls between connected empty cells.

World generation checks both passages at 16 m and 28 m, their solid floors and
roofs, sealed tunnel ends, the internal connector, the unbroken surface and
both geological bands. It throws if any of these checks fail.

Press **C** to enable the visual cutaway. The horizontal section follows the
camera focus height. **Q/E** move it continuously; **PageUp/PageDown** step it
by one metre. Terrain above the selected plane is hidden. Exposed solid sections
are hatched, while cave cells reveal the real floor and vertical side walls.
Move the plane to 9 m or 27 m to inspect the two material bands. Press **C**
again to restore the full surface.

The camera is adjusted for this inspection mode: it can zoom to 4 m, tilt down
to a low oblique angle, and pan with a direct screen-to-ground scale that does
not stop when the pointer crosses the horizon.

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
