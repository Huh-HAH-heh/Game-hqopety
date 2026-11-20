# Raylib terrain renderer

An isolated presentation path for RimClone. It does not reference the legacy SFML
renderer and does not own world-generation or simulation state.

## Responsibilities

- `TerrainRenderSnapshot` stores compact surface data plus exact vertical solid/air
  intervals for columns with cave, stratum or local voxel overrides.
- `RaylibTerrainRenderer` owns GPU resources and draws cached volumetric batches.
- `RegionMeshBuilder` builds exposed top, bottom and side faces from vertical intervals.
- `OrbitCameraController` owns RTS orbit, pan, zoom and cut-plane controls.
- `RaylibTerrainViewer` owns the window and frame loop.

## Terrain region layout

The presentation grid uses **4x4-tile render cells**. World storage remains grouped
into 192x192-tile regions; the full map is still 2304x2304 metres with 500 vertical
layers at 0.1 m per layer.

Each storage region contains 48x48 render cells. For safe 16-bit GPU indices, the
mesher normally produces four 24x24-cell submeshes per storage region. Very dense
areas split recursively into smaller batches. This keeps terrain as a small number
of GPU meshes rather than one object per tile or voxel.

## Volumetric geometry

The renderer no longer treats terrain as only a top-height surface. Every sampled
column is described by ordered vertical intervals of solid material and air.
Simple terrain remains a compact height/material pair. Only columns containing
strata, carved caves or other vertical edits retain their full interval list.

The mesh builder emits:

- upper faces of solid ground and lower floors exposed beneath caves;
- undersides of cave roofs and ceilings;
- vertical walls wherever solid material borders an air interval;
- side faces along ordinary terrain height differences;
- distinct cut faces and material-specific hatching at the current cut plane.

Adjacent solid columns do not emit internal walls, so the result is a continuous
volume. Empty intervals remain true holes: they are not painted as dark floor tiles.

## Data flow

```text
WorldMap / sparse BlockVolumeTree
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
Normal terrain edits invalidate affected storage batches; moving the cut plane rebuilds
the visible volume at 0.5 m steps.

## Controls

- WASD: move the camera focus across the terrain
- Right mouse button + drag: orbit around the focus; dragging right rotates right
- Middle mouse button + drag: grab-and-pan using screen-ray intersections with the focus plane
- Mouse wheel: zoom toward the cursor when the ground anchor is visible
- Left Shift / Right Shift: faster WASD movement
- C: toggle cutaway mode and focus on the lower through-tunnel
- F1: focus on the lower through-tunnel (cut plane 17 m, inside the lower passage)
- F2: focus on the sealed upper passage (cut plane 28 m, inside the upper passage)
- Q / E: move the camera focus and cut plane vertically; focus can move above and below the 0-50 m terrain volume
- PageUp / PageDown: step the cut plane by exactly 1 metre
- Shift + mouse wheel: adjust the cut plane by 1 metre
- T: return to the fitted top-down view
- Escape: exit

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

`WorldMap.CarveStaticVolume` and `SetStaticVolume` write empty/solid overrides into
the sparse 3D `BlockVolumeTree`. The underlying terrain remains an implicit solid
column, so the generator does not materialize the whole 500-layer map into voxels.
`GetMaterialId(x,y,z)` and `GetTileRanges(x,y)` resolve actual solids and air gaps.
The generator verifies the lower portals, empty tunnel interiors, solid floors and
roofs, sealed upper passage, connector and geological bands.

Press **C** to enable the visual cutaway. Material above the camera focus plane is
clipped, not deleted from WorldMap. The section height follows `Camera.Target.Y`
and is clamped to the world's 50 m vertical extent. The underlying voxel resolution
remains 0.1 m; the displayed cut plane updates in 0.5 m increments. Only
camera-visible storage regions rebuild when the plane moves; off-screen batches
are refreshed when they enter view.

Cut faces are hatched. Base rock uses diagonal strokes, the 8-10 m stratum uses
horizontal bedding marks, and the 26-29 m stratum uses the opposite diagonal.
Tunnel floors, ceilings and vertical cave walls come from the actual 3D occupancy
intervals. Press C again to restore the full terrain volume.

## Camera

The inspection camera has a 45-degree field of view and can tilt down to 28.6 degrees
above the horizon, exposing vertical terrain and cave walls rather than forcing a
near-top-down view. The minimum angle keeps all screen rays aimed at the focus plane.
Middle-button panning uses two screen-ray/plane intersections, avoiding the drift
caused by a hand-estimated world-units-per-pixel scale. Zoom anchoring is rejected
when a cursor ray hits the focus plane too far outside the visible area.

The fitted top-down view remains available with **T**. The regular surface view keeps
the black-to-charcoal palette, with thin 5 m elevation marks alternating between
bright white and muted gray; there is no permanent tile grid.
