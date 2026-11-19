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

`WorldGenerator.GenerateSmooth` creates a real multi-level volume system inside the mesa:

- lower through-tunnel: 80 m long, normally 8 m wide and 5 m high, with occasional 12 m chambers and 6 m clearances; it bends in plan and opens through both canyon flanks;
- upper passage: 32 m long, inside the mesa at 26 m depth, with both ends sealed;
- lower and upper chambers: 20 x 20 m, with floors at 12 m and 24 m;
- a rising internal connector joins the two elevations without piercing the surface;
- separate material bands occupy 8-10 m and 26-29 m.

The lower passage is a through-tunnel with two portals, not a blind adit. Its
centreline curves and its cross-section varies; the upper level remains enclosed.
The connector is made from overlapping rising sections, not a vertical shaft.

`WorldMap.CarveStaticVolume` writes empty overrides into the sparse 3D
`BlockVolumeTree`. The underlying terrain remains an implicit solid column,
so the generator does not materialize the whole 500-layer map into voxels.
`GetMaterialId(x,y,z)` and `GetTileRanges(x,y)` resolve the actual solids and
air gaps. The generator verifies the lower portals, empty tunnel interiors,
solid floors and roofs, sealed upper passage, connector and geological bands.

Press **C** to enable the visual cutaway. Terrain above the camera focus plane
is hidden. The section height follows `Camera.Target.Y` at 0.1 m resolution
and is clamped to the world's actual 50 m vertical extent. **Q/E** move the
plane smoothly, **PageUp/PageDown** step by 1 m, and **Shift+Wheel** also moves
the plane by 1 m. Ordinary wheel input zooms the view.

Cut faces are hatched. The base material uses diagonal strokes, the 8-10 m
stratum uses horizontal bedding marks, and the 26-29 m stratum uses the
opposite diagonal. Tunnel floors and vertical cave walls are built from actual
voxel intervals; the cutaway wall builder avoids emitting duplicate faces.
Press C again to restore the full surface.

The inspection camera has a minimum 35-degree elevation above the horizon.
This keeps every screen ray pointed down at the cut plane, so cursor-anchored
zoom and middle-button panning remain usable across the viewport. The top-down
view remains available with **T**.

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
