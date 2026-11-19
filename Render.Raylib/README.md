# Raylib terrain renderer

This project is the isolated presentation path for RimClone. It is intentionally
separate from the current SFML renderer so the migration can be validated before
the legacy path is removed.

## Data ownership

- `Core/Map` remains the authoritative owner of terrain and world-region state.
- `WorldTerrainSnapshot` is a compact, read-only render input. Uniform regions
  are represented by one height/material entry; tile samples are allocated only
  for regions that have become non-uniform.
- `TerrainChunkMeshBuilder` is CPU-only. It reads the snapshot and produces
  plain vertex/normal/color arrays. It does not reference Raylib or GPU resources.
- `RaylibTerrainRenderer` owns GPU meshes and the default material. GPU resource
  creation, drawing, and disposal happen on the window/render thread.
- `OrbitCameraController` owns presentation camera state and has no dependency
  on world storage.

## Render batching

- A render chunk groups up to 4 x 4 world regions.
- Uniform regions produce one top patch and only the exposed outer walls.
- Non-uniform regions expand to tile-resolution geometry.
- CPU mesh generation runs in parallel before the window starts.
- GPU upload happens once during initialization.
- The frame loop draws cached chunk meshes and performs chunk-frustum culling.
  It does not rebuild terrain geometry or allocate vertex buffers.

## Camera controls

- Right mouse drag: orbit around the target.
- Middle mouse drag: pan.
- Mouse wheel: zoom.
- WASD / arrow keys: move across the world.
- Q / E: move the camera target vertically.
- Shift: faster movement.
- Ctrl: slower movement.
- Home: reset camera.

## Current scope and known boundary

This first slice captures the generated terrain once when the viewer starts.
It is not yet connected to live terrain edits or the simulation tick. The snapshot
already records per-region revisions and can be extended with dirty-chunk refresh
without coupling the geometry builder to WorldMap or the render backend.

The horizontal and vertical display scales are presentation-only constants in
`TerrainChunkMeshBuilder`; they do not change simulation coordinates or terrain data.
