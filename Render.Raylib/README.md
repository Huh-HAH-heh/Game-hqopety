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

The source field and edit operations retain **0.1 m voxel spacing**. The Raylib surface renderer now uses one fixed **0.4 m sampling step** at every camera distance. It does not switch mesh resolution when zooming, keep parallel LOD caches or prewarm another tier. This keeps the same geometry density during camera movement. The fixed render step is a presentation sampling choice; it does not change the authoritative 0.1 m field or the resolution of voxel edits.

Chunk meshes are cached in 3D coordinates. A local edit dirties only intersecting chunks and neighbours whose sampling halo overlaps the edit.

**Meshing pipeline:** the render thread copies only the sparse edited storage chunks needed by a render chunk into an immutable source snapshot. Density sampling, material capture, empty-chunk detection and surface extraction then run together on up to six cancellable CPU workers (the limit leaves two logical processors available to the application). Workers produce only vertex/normal/color/index arrays and never call Raylib or OpenGL. This removes the old 1.25 ms-per-frame capture stage that serialized the pipeline before workers could begin. Stale results are cancelled or discarded using per-chunk request revisions.

The render thread uploads completed mesh packets with up to 60,000 vertices per packet, reducing GPU buffer allocations and draw calls compared with the previous 3,000-vertex packets. A 3.0 ms upload target per frame (shared with detached-fragment meshes) prevents startup streaming from monopolizing the render thread. The old valid mesh remains visible until every replacement packet has been uploaded, then the renderer swaps the mesh set atomically. The pending queue holds up to 128 jobs and candidates are selected nearest the terrain focus. HUD reports snapshot-copy time, worker build time, GPU upload time, active workers and queued jobs. A GPU upload call cannot be preempted once submitted, so actual upload time must be checked on the target GPU.

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

### Fixed-resolution mesh cache and diagnostics

The active viewer mesh step is always 4 source voxels (0.4 m); zooming does not change it. The renderer selects at most 4,096 frustum candidates, retains up to 6,000 chunk entries and queues up to 128 mesh jobs. The view radius is bounded to keep candidate discovery and the local editable scene predictable. The geometry is sampled from the authoritative 0.1 m signed-distance field, and the edit field itself is never downsampled or rewritten.

The mesher test executable still compares multiple sampling steps as a quality regression tool. On the narrow-ridge fixture, maximum SDF error was 0.38 m at step 2, 0.98 m at step 4, 1.19 m at step 8 and 1.28 m at step 40. It also checks shared chunk-border samples and benchmarks CPU capture plus mesh extraction. These are test probes, not runtime camera LOD tiers.

Run locally:

```powershell
dotnet run --project Render.Raylib.Tests/Render.Raylib.Tests.csproj -c Release
```

The viewer writes asynchronous diagnostics to `%LOCALAPPDATA%\RimClone\logs\terrain-pipeline.log`. `MESH_BUILT` entries include LOD, chunk coordinates, source revision, worker milliseconds, queue wait, vertices and triangles. `REBUILD_REPEAT` marks a second build of the same chunk at the same source/palette revision. Other event types record cancellations, stale results and worker failures. A `FRAME_SUMMARY` is written every 120 frames with queue depth, worker count, snapshot/build/upload time and cumulative counters.

The CPU benchmark does not call the GPU. Actual GPU upload timing and packet counts are measured by the running viewer and printed in its HUD/log.

### Fixed-resolution mesh streaming

Every rendered terrain chunk uses the same 0.4 m sampling step. Camera zoom changes only the visible chunk set, not mesh density. There are no adjacent-tier prewarm jobs and no parent/child LOD fallback transitions.

Chunk source snapshots are created from the sparse edited-chunk store on the render thread, then workers perform density capture, material sampling, empty-chunk rejection and triangle extraction. Completed packets are uploaded on the render thread; the previous valid surface remains visible until a replacement is complete. The worker pool is sized from the available logical processor count, with a maximum of six workers.

Cutaway cap updates are independent from the base surface. If only the cut plane changes, the worker request has `surface=false, cutCap=true`; it extracts and uploads only the horizontal cap. The existing base terrain mesh is neither regenerated nor swapped. A voxel edit that affects the base surface requests `surface=true`, and requests a cap as well only if the edit intersects the active cut plane.

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
