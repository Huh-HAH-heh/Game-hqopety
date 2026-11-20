# Raylib terrain renderer

An isolated presentation path for RimClone. It does not reference the legacy SFML
renderer and does not own world-generation or simulation state.

## Responsibilities

- `TerrainRenderSnapshot` stores compact surface data and resolves exact vertical
  solid/air intervals for voxel columns as visible chunks are built.
- `RaylibTerrainRenderer` owns GPU resources, draws cached volumetric batches and
  clips voxel terrain with a world-space GPU cut plane.
- `RegionMeshBuilder` builds exposed top, bottom and side faces from vertical intervals.
- `OrbitCameraController` owns RTS orbit, pan, zoom and cut-plane controls.
- `RaylibTerrainViewer` owns the window and frame loop.

## Terrain region layout

The primary **Voxel** mode follows the native world-cell dimensions: 1x1 m in X/Y
and 0.1 m per vertical layer. Face generation and internal-face culling are symmetric
in all six directions; the renderer does not invent horizontal detail below the
world's 1 m storage resolution. A true 0.1 m cube grid would require 10x horizontal
subdivision on both axes and a substantially denser mesh.

The mesher reads the real solid/air intervals for each visible column and emits only
exposed faces in all six directions. This is a volumetric voxel surface, not an
interpolated height field.

## World model and material descriptions

WorldMap owns one authoritative SparseVoxelStore for geometry and occupancy:
1 m horizontal cells, 0.1 m vertical layers, solid intervals, air gaps and material
IDs. The store groups XY cells into 16x16 chunks. A normal column is represented by
one compact solid run from the floor; a column with caves, overhangs or multiple
materials switches to an arbitrary list of vertical solid runs. Air remains implicit.
There is no separate heightfield or second volume tree backing the rendered terrain.
TerrainLayer and TerrainRegion are lazy compatibility views; they do not own a
second copy of voxel occupancy. TerrainRenderSnapshot is a presentation cache only.

The mathematical model and the descriptive material model are joined by MaterialId:
a solid cell refers to a material definition, while the definition is shared by all
cells using that ID. This avoids storing a large property object per voxel. The
current code stores material IDs and occupancy, but does not yet provide a complete
canonical material catalogue with density, hardness, porosity, heat capacity,
conductivity, and permeability. Those physical values should live in shared material
definitions and be consumed by simulation systems; the renderer should only read
them when visual variation is needed.

The logical simulation region remains 192x192 m; the voxel backing itself is divided
into 16x16 XY chunks and compressed by vertical runs. The current world is 2304x2304 m
with 500 vertical layers at 0.1 m per layer. Complex run lists are queried only when
a visible mesh needs them. Mesh creation uses bounded submeshes and recursively
splits unusually dense areas.

The smooth world generator samples its surface at 1 m XY spacing, giving the base
surface the same horizontal resolution as the voxel store.

The renderer caches a 4x4 m voxel overview and exact 1x1 m detail meshes. It changes
to detail within 220 m of the camera focus and returns to overview beyond 290 m.
Only the display resolution changes: the source store always retains its 1x1x0.1 m
cell resolution and all cave/strata intervals.


The old **Heightmap fallback** remains available from the *Terrain and cuts*
settings section. It keeps the previous 4x4-tile sampling path and can be selected
without changing world data.

## Volumetric geometry

The renderer no longer treats terrain as only a top-height surface. Every sampled
column is described by ordered vertical intervals of solid material and air.
Ordinary terrain columns remain one compact vertical solid run. Columns containing
strata, carved caves, overhangs or disconnected solid masses retain arbitrary ordered
solid intervals. This is compression of voxel occupancy, not a separate heightfield.

The mesh builder emits:

- upper and lower voxel faces exposed to air;
- vertical walls in both horizontal axes wherever solid material borders an air interval;
- no internal faces between adjacent solid cells, including across storage-region boundaries;
- cutaway faces with subdued geology hatching
- side faces along ordinary terrain height differences;
- distinct cut faces and material-specific hatching at the current cut plane.

Adjacent solid columns do not emit internal walls, so the result is a continuous
volume. Empty intervals remain true holes: they are not painted as dark floor tiles.

## Data flow

```text
WorldMap / SparseVoxelStore
        |
        v
TerrainRenderSnapshot
  compact surface + vertical solid/air intervals
        |
        v
RegionMeshBuilder
  4x4 render cells -> bounded volumetric submeshes
        |
        v
RaylibTerrainRenderer
  cached GPU batches + frustum culling
        |
        v
Raylib window
```

The renderer reads the snapshot only. It does not query WorldMap while drawing.
GPU resource creation, upload, draw and disposal are restricted to the render thread.
Normal terrain edits invalidate affected storage batches. The voxel renderer maintains
separate 4m overview and 1m detail caches, with hysteresis to prevent rapid switching.
Complex coarse columns are queried only on demand. Moving the cut plane does not
rebuild base voxel meshes: a fragment shader clips the selected volume and only
visible horizontal caps are refreshed. Base mesh builds are limited to six coarse or
two fine regions per frame, with a separate twelve-region cap-update budget.

## Controls

- WASD: move the camera focus across the terrain
- Right mouse button + drag: orbit around the focus; dragging right rotates right
- Middle mouse button + drag: grab-and-pan using screen-ray intersections with the focus plane
- Mouse wheel: zoom toward the cursor when the ground anchor is visible
- Left Shift / Right Shift: faster WASD movement
- C: toggle cutaway mode and focus on the lower through-tunnel
- F1: focus on the lower through-tunnel (cut plane 17 m, inside the lower passage)
- F2: focus on the sealed upper passage (cut plane 28 m, inside the upper passage)
- Q / E: move the cut plane smoothly at 1.8 m/s; camera focus remains independent
- PageUp / PageDown: move the cut plane by exactly one 0.1 m voxel layer
- Shift + mouse wheel: move the cut plane by one 0.1 m voxel layer per wheel notch
- Shift + middle mouse + vertical drag: adjust camera speed on the fly (up = faster)
- Tab or the small right-side **СЛОИ** tab: open/close the horizontal section panel
- Drag its vertical slider to choose a layer; mouse wheel over the slider steps exactly 0.1 m at a time
- T: return to the fitted top-down view
- Escape: exit
- Pause menu > Terrain and cuts > Geometry: switch between Voxel (primary) and Heightmap fallback

## Static multi-level cave and volumetric cutaway

`WorldGenerator.GenerateSmooth` creates a real multi-level volume system inside the mesa:

- lower through-tunnel: 152 m long, normally 8 m wide and 5 m high, with occasional
  12 m pockets and 6 m clearances; it bends in plan and opens through both canyon flanks;
- upper passage: 32 m long, inside the mesa at 26 m depth, with both ends sealed;
- lower and upper chambers: 20 x 20 m, with floors at 12 m and 24 m;
- a rising internal connector joins the two elevations without piercing the surface;
- separate material bands occupy 8-10 m and 26-29 m where the original surface
  provides enough overburden.

The lower passage is a through-tunnel with two portals, not a blind adit. Its
centreline curves and its cross-section varies; the upper level remains enclosed.

`WorldMap.CarveStaticVolume` and `SetStaticVolume` edit empty/solid voxel runs in
`SparseVoxelStore`. The base surface is also represented by voxel runs; there is
no separate implicit height-field geometry store. `GetMaterialId(x,y,z)` and
`GetTileRanges(x,y)` resolve the actual solid and air intervals.
The generator verifies the lower portals, empty tunnel interiors, solid floors and
roofs, sealed upper passage, connector and geological bands.

Press **C** to enable the visual cutaway. Material above the camera focus plane is
clipped, not deleted from WorldMap. The section height is stored independently from the camera focus, clamped to the
world's vertical extent, and quantized to the 0.1 m voxel-layer grid. Only
camera-visible storage regions rebuild when the plane moves; off-screen batches
are refreshed when they enter view.

Cut faces are hatched. Base rock uses diagonal strokes, the 8-10 m stratum uses
horizontal bedding marks, and the 26-29 m stratum uses the opposite diagonal.
Tunnel floors, ceilings and vertical cave walls come from the actual 3D occupancy
intervals. Press C again to restore the full terrain volume.

## Camera

The inspection camera has a 45-degree field of view and can tilt down to 28.6 degrees
above the horizon, exposing vertical terrain and cave walls rather than forcing a
near-top-down view. WASD speed scales with camera distance (slow and precise at close
zoom, fast at overview distance) and is clamped to a useful range. Shift + middle-button
vertical drag adjusts an additional live speed multiplier; ordinary MMB remains pan.
Middle-button panning uses two screen-ray/plane intersections, avoiding the drift
caused by a hand-estimated world-units-per-pixel scale. Zoom anchoring is rejected
when a cursor ray hits the focus plane too far outside the visible area.

## Cheap directional lighting

All terrain faces share one dark neutral rock color. Volume is defined by a single
global directional light evaluated from the existing face normals, plus an ambient
term so surfaces turned away from the light remain readable. The voxel path evaluates
this in the lightweight terrain shader; the shader-free heightmap fallback bakes the
same flat-face shade into vertex colors. There are no per-object lights, shadow maps,
or shadow passes. Sun direction, intensity, and ambient strength are explicit palette
settings so a future world lighting/time-of-day system can update them, and gameplay
systems such as solar panels can query the same global sun direction.

The fitted top-down view remains available with **T**. The regular surface view uses
one neutral dark rock color; directional face lighting provides volume without a
permanent tile grid or shadow-map pass.
