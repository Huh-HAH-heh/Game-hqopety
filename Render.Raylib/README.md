# Raylib 3D voxel terrain

The default application entry point opens the isolated Raylib terrain viewer. The legacy SFML gameplay renderer remains available with `--legacy-sfml`.

## Authoritative voxel representation

`WorldMap.Voxels` is the authoritative 3D terrain field used by the Raylib voxel renderer and terrain editing tools.

- Native voxel spacing: **0.1 × 0.1 × 0.1 m** on X, Y and Z.
- Voxel volume: **0.001 m³**.
- Storage: sparse **16 × 16 × 16** voxel chunks (1.6 m per side).
- Geometry value: signed distance sampled at voxel centres and quantized into a signed 16-bit value. Positive is solid, negative is air, and zero is the surface.
- Material: a separate 16-bit MaterialId for every cubic voxel cell. Material zero is air.
- Material definitions are shared by ID and include mass density, hardness, compressive strength, porosity and thermal conductivity.

The world is 2304 × 2304 × 50 m. It is not allocated as a dense 3D array. Unedited terrain is evaluated procedurally as a 3D signed-distance field from the existing canyon/plateau base surface; only chunks touched by actual edits allocate voxel data. This sparse base-plus-voxel-edit design avoids allocating hundreds of billions of cells while allowing edits to create caves, arches, overhangs and disconnected volumes.

The initial canyon generator still supplies the broad base surface from its existing 1 m XY terrain data. The rendered surface, however, is extracted from the 3D density field, and all local sculpting operations update that same field. This is a sparse voxel terrain, not a height-column renderer relabelled as voxels.

## Surface extraction and rendering

`VoxelChunkMesher` extracts the visible 3D isosurface from density samples using marching tetrahedra. Normals come from the density gradient. The renderer does not create terrain by extruding the top of each XY column.

The same density field is used by:
- exposed-surface mesh generation;
- raycasting from the 3D camera;
- sphere, capsule and box CSG edits;
- material painting and local smoothing;
- horizontal cut-plane intersections and cut caps.

The data remains at 0.1 m resolution. The renderer changes only its sampling step with distance:

| Render step | Source-field samples |
|---|---:|
| Close | 0.1 m |
| Medium | 0.5 m |
| Far | 1.0 m |
| Very far | 4.0 m |

Chunk meshes are cached in 3D coordinates and each LOD tier is retained independently. A local edit dirties only intersecting chunks and neighbours whose sampling halo overlaps the edit.

**Meshing pipeline:** the render thread incrementally captures at most 0.25 ms of live voxel samples per frame. The immutable chunk snapshot is processed by up to two cancellable CPU workers; detached-fragment meshes use one additional serialized worker. Workers produce only vertex/normal/color/index arrays and never call Raylib or OpenGL. Stale results are cancelled or discarded using per-chunk request revisions.

The render thread uploads completed mesh packets in chunks of at most 3,000 vertices, with a target total GPU-upload budget of 0.55 ms per frame (shared with detached-fragment meshes). The old valid mesh remains visible until every replacement packet has been uploaded, then the renderer swaps the mesh set atomically. The pending queue is capped at 48 requests and prioritizes chunks nearest the camera. HUD reports CPU capture time, GPU upload time, active workers and queued jobs. The upload target is measured at runtime; a single GPU-driver upload call cannot be preempted once submitted, so the HUD is used to detect hardware-specific overruns.

## Terrain editing

The voxel field exposes these operations:

- `SetCell`, `FillBox`, `CarveBox`;
- `AddSphere`, `CarveSphere`;
- `AddCapsule`, `CarveCapsule`;
- `PaintSphere`;
- `SmoothSphere`.

These are CSG/density-field operations. They change the 3D volume rather than editing a surface-height number.

### Viewer controls

- **F3**: cycle edit mode — off, carve sphere, add sphere, paint sphere, smooth sphere, carve capsule, add capsule.
- **Left mouse**: apply a sphere/paint/smooth operation; for capsule tools, press to set the first endpoint and release to set the second.
- **F4**: cycle material ID.
- **F5 / F6**: decrease / increase brush radius.
- **F7 / F8**: undo / redo the last voxel edit (in-session history, bounded to 16 operations / 64 MiB).
- The F3 tool cycle includes carve/add sphere, paint, smooth, carve/add capsule, carve/add box, carve/add ramp and **Flatten Plane**, which levels the selected area toward the hit surface plane with a smooth radial falloff.
- **Shift + right mouse drag**: orbit the camera.
- **Middle mouse drag**: pan.
- **Mouse wheel**: zoom.
- **WASD**: move camera focus.
- **F1 / F2 / C**: focus the sample lower passage, upper passage or surface cutaway.
- **Q / E**, **PageUp / PageDown**: move the horizontal cut plane.

The separate Heightmap fallback remains available from the terrain settings.

### Automatic edit persistence

The Raylib viewer restores edited voxel chunks at startup and autosaves after a short idle period, with a final save on exit. Default file: `%LOCALAPPDATA%\\RimClone\\voxel-terrain.rvx`. Override it with `RIMCLONE_VOXEL_SAVE_PATH`. The file contains sparse edit chunks only and is versioned against a fingerprint of the generated base terrain; a mismatched base world is rejected before edits are applied.

### Dynamic detached rock

Carving sphere, capsule, box or ramp tools now runs a bounded local connected-component scan after the edit.

- Connectivity is checked on a conservative 0.2 m occupancy grid; accepted fragment geometry is copied from the exact 0.1 m density/material field.
- Components that touch the analysis-window boundary are treated as still attached and remain static.
- A detached component becomes one rigid body, not thousands of voxel bodies. The pool is limited to 16 active fragments, 64 mesh chunks per body and 32 MiB of fragment snapshots.
- A fragment snapshot stores signed density as `short` and material as `ushort` (4 bytes per sample/cell), and the mesher reads it directly through `IVoxelFieldSource`. No second `SparseVoxelField` is allocated for a moving rock.
- Moving-fragment render meshes use a 0.2 m sampling step. The static terrain keeps the full 0.1 m source field.
- Bodies receive gravity, angular motion, approximate terrain support and approximate body-to-body contacts. Slow grounded bodies sleep.
- A settled fragment is incrementally resampled back into the one authoritative `WorldMap.Voxels` grid. Its current position and rotation are inverted to find source samples; density is merged with an SDF union. The job processes at most 12,000 world-grid samples per frame and emits one terrain change/undo record when complete.
- The detached mesh remains visible until all visible overlapping static terrain meshes have been rebuilt, then its GPU meshes and snapshot are released.
- F7/F8 also deactivate/reactivate fragment bodies in the same in-session edit history as the voxel carve.

This is a bounded first implementation, not a full rigid-body fracture solver: there is no stress-based breakage, wall/edge collision or debris cascade. Dynamic bodies that have not settled are not yet included in the voxel edit save; once merged into the master field, their terrain is saved normally.

**Streaming boundary:** the renderer evicts distant GPU meshes from its bounded cache, but edited CPU voxel chunks are still retained by the main field in memory. A higher-level flat-planet region pager (active sector set around the camera/units, compressed inactive sector pages, and deterministic reload by global sector coordinates) is not implemented yet. The fragment-to-master-grid merge above is implemented independently of that future pager.

### LOD mesh cache and transition

The voxel renderer keeps a separate cached mesh for each chunk key and LOD step (1, 5, 10 and 40 source voxels). Changing camera zoom does not invalidate the other LOD tiers.

- The adjacent LOD tier is prewarmed around the camera target before the hysteresis threshold is crossed, at a maximum of two additional mesh builds per frame.
- During a transition, ready meshes from the previous tier remain drawable until replacement chunks are ready.
- The mesh cache has a target capacity of 1,800 entries. LRU eviction protects meshes currently being drawn; if the visible set itself exceeds the target, the cache may temporarily exceed it rather than remove visible geometry.
- HUD reports total cached voxel chunks, visible voxel chunks, adjacent-LOD prewarm builds and actual voxel mesh builds per frame.

CPU meshing and GPU upload still run on the render thread, but each frame has bounded build budgets. A worker-thread CPU mesh build / render-thread GPU upload pipeline remains a separate optimization.

### Cutaway rebuild behavior

Moving the horizontal slice still updates the GPU clip plane immediately. The cap mesh waits until the selected 0.1 m layer has remained unchanged for 8 frames, then rebuilds. This avoids rebuilding cut caps for every intermediate slider/key step. HUD mesh-build counters are per-frame counters.

## Important integration boundaries

The Raylib terrain viewer is still an isolated presentation path. Edited regions now use `VoxelNavigationSystem`: it extracts multiple upward-facing support surfaces at 0.5 m intervals and routes in `(X,Y,floor)` space. Movement keeps the selected floor and rejects steps where the SDF vertical capsule intersects solid terrain. Untouched terrain remains on the compact legacy XY route grid. Navigation between distinct 128 m voxel regions and vertical gravity/fall response are not complete. Pawn-to-pawn separation is still the horizontal crowd solver.

Voxel edit chunks are serialized with a compressed, versioned format. The viewer loads the edit file after deterministic base-world generation, autosaves changes and writes a final save on exit. The edit set supports sphere/capsule/box/ramp CSG, local smoothing, material paint and plane flattening. Voxel and detached-fragment undo/redo are in-session; campaign save-slot integration and persistence of dynamic body state are not yet implemented.

Performance budgets and sparse allocation are implemented in source, but the new renderer has not yet been benchmarked on target hardware. Do not treat the configured per-frame budgets as measured FPS.

## Build

From the repository root:

```powershell
dotnet restore .\RimClone.sln
dotnet build .\RimClone.sln --configuration Release
dotnet run --project .\App\App.csproj --configuration Release
```
