# Raylib flat voxel terrain

The default application entry point opens the isolated Raylib terrain viewer. The legacy SFML gameplay renderer remains available with \`--legacy-sfml\`.

## Authoritative voxel field

\`WorldMap.Voxels\` is the authoritative editable 3D terrain field.

- Native voxel spacing: **0.1 × 0.1 × 0.1 m**.
- Storage and render chunk dimensions: **16 × 16 × 16 samples** (1.6 m per side).
- Density: signed distance sampled at voxel centres; positive is solid, negative is air.
- Material: a separate 16-bit MaterialId per cubic voxel cell. Material zero is air.
- Storage: sparse edited chunks over a procedural base sampler. The world is not allocated as one dense 3D array.

All static terrain chunks use the same **Step=1** sampling resolution. The renderer has no Step=2/4/8/16 tiers, parent/child chunk grid, LOD prewarm or scale-transition stitching. Camera distance changes only which fixed-size chunks are requested and rendered.

## Greedy surface meshing

\`VoxelChunkMesher\` converts the binary occupancy boundary (\`density > 0\`) into axis-aligned voxel faces.

For each of the three axes it sweeps the 17 boundary planes of a 16³ chunk, builds a 16×16 face mask, and greedily merges adjacent faces when both material ID and normal direction match. A merged rectangle is emitted as one indexed quad: **4 vertices and 6 indices**. Mesh packets are split before reaching the 16-bit index limit.

A one-sample density halo is captured on every side. The face-ownership rule assigns a boundary face to the chunk containing the solid sample, so adjacent chunks do not duplicate or omit the same face. Greedy geometry is intentionally block-aligned voxel geometry; it is not a smooth interpolated Surface Nets surface. The reduction in triangle count depends on how many coplanar faces can actually merge, so no fixed 10–50× reduction is assumed for every terrain shape.

The mesher is shared by static chunks and detached voxel fragments; both use Step=1.

## Flat streaming and worker pipeline

\`VoxelTerrainRenderer\` uses a single key: \`ChunkKey(X,Y,Z)\`. A chunk is 16³ samples everywhere.

1. The render thread discovers chunk AABBs intersecting the camera's view radius and frustum.
2. It copies a small immutable snapshot from the sparse voxel field and enqueues a build request.
3. Persistent worker threads wait on \`SemaphoreSlim\`, consume \`ConcurrentQueue\` requests and run Greedy Meshing. Workers never call Raylib/OpenGL.
4. Workers publish CPU mesh packets through a completion queue.
5. The render thread uploads packets with \`UploadMesh\` and draws the resident GPU meshes.

The queue is bounded to 128 pending jobs. The pool uses 2–6 persistent workers based on the available processor count. GPU uploads remain round-robin, with a 4-mesh / 3 ms per-frame budget shared with detached-fragment meshes. A generation token protects CPU completions; a separate upload generation protects queued GPU packets.

Chunks whose bounds move completely outside the configured view radius are removed from the cache and their GPU meshes are unloaded. Chunks that remain in range are not regenerated merely because the camera moves. Mesh hashes prevent an identical replacement from causing another GPU upload.

## CUT plane

The base terrain uses the same GPU clip plane as before. The horizontal cap is now generated at Step=1 and greedily merges adjacent solid cut cells of the same material into flat +Y quads. The cap is offset by 0.006 m to avoid z-fighting. The cut cap is a separate mesh product, so changing the slice does not require rebuilding an unchanged base surface.

The cut plane waits for 8 stable frames before requesting a new cap, avoiding one rebuild for every intermediate slider/key step. CUT cap geometry is generated from the same immutable chunk snapshot and is uploaded only on the render thread.

## Editing and detached fragments

The same authoritative density field backs voxel CSG edits, material painting, raycasts and navigation queries. Available edits include sphere/capsule/box carve and add, paint, smooth, ramp and plane flattening.

Detached components use a bounded pool of at most 16 rigid fragments, 64 Step=1 mesh chunks per body and 32 MiB of fragment snapshots. Fragment bodies receive gravity, angular motion, approximate terrain support and body-to-body contacts. Settled fragments are incrementally merged into the master voxel field; they remain visible until the affected static terrain is current.

This is a bounded first implementation, not a complete rigid-body fracture solver. Stress-based breakage, wall/edge collision and debris cascades are not implemented. Dynamic fragment state is not saved until it has merged into the master field.

## Viewer controls

- **F3**: cycle edit mode — off, carve sphere, add sphere, paint, smooth, carve/add capsule, carve/add box, carve/add ramp and flatten plane.
- **Left mouse**: apply the selected tool; capsule tools use press/release for endpoints.
- **F4**: cycle material ID.
- **F5 / F6**: decrease / increase brush radius.
- **F7 / F8**: undo / redo the last in-session voxel edit.
- **Shift + right mouse drag**: orbit the camera.
- **Middle mouse drag**: pan.
- **Mouse wheel**: zoom.
- **WASD**: move camera focus.
- **F1 / F2 / C**: focus the lower passage, upper passage or surface cutaway.
- **Q / E**, **PageUp / PageDown**: move the horizontal cut plane.

The separate Heightmap fallback remains available from the terrain settings. The Voxel mode uses the flat Step=1 renderer directly.

## Persistence

The Raylib viewer restores edited voxel chunks at startup and autosaves after a short idle period, with a final save on exit. Default file: \`%LOCALAPPDATA%\\RimClone\\voxel-terrain.rvx\`. Override it with \`RIMCLONE_VOXEL_SAVE_PATH\`. The file stores sparse edit chunks and is versioned against a fingerprint of the generated base terrain.

## Diagnostics and tests

The terrain pipeline log is written to \`%LOCALAPPDATA%\\RimClone\\logs\\terrain-pipeline.log\`. Frame summaries report fixed resolution, candidate/visible/cache counts, snapshot and worker time, queue wait, completion delivery, GPU upload time and cumulative upload/unload counts.

Run the CPU mesher checks locally:

\`\`\`powershell
dotnet run --project Render.Raylib.Tests/Render.Raylib.Tests.csproj -c Release
\`\`\`

The test executable checks shared Step=1 halo samples, Greedy rectangle reduction, material boundaries, flat CUT caps, exact adjacent-chunk surface boundaries and empty-chunk material-capture avoidance.

These code-path changes have not yet been benchmarked on target hardware. Triangle reduction and frame rate must be measured using the actual edited terrain and configured view radius; the configured upload budget is not a measured FPS result.

## Build

From the repository root:

\`\`\`powershell
dotnet restore .\\RimClone.sln
dotnet build .\\RimClone.sln --configuration Release
dotnet run --project .\\App\\App.csproj --configuration Release
\`\`\`
