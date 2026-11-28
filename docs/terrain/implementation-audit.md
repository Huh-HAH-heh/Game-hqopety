# Voxel terrain implementation audit

Baseline: `feature/raylib-terrain-renderer` after the 3D density-field migration.

## Verdict

The Raylib terrain path now has a real sparse 3D voxel representation and derives its rendered surface from that field. It is no longer the previous vertical-interval/XY-column mesh labelled as voxel terrain.

This is a **compact procedural-base + adaptive 3D voxel-edit** design. The original canyon base is represented by flat 1 m X/Z height and material anchors; its density is evaluated procedurally. Edited areas materialize only changed 0.1 m samples and cell materials in 16³ storage bricks. Each data channel starts sparse and promotes to a flat dense array with bit mask only when override occupancy reaches 2048 entries; `Compact()` can demote it below 1024. This avoids paying for four full arrays for every edited brick.

The voxel volume can expand independently of the original tile/navigation grid. New space is empty and remains unallocated until edited. This is a first adaptive storage stage, not yet a full hierarchy of coarse-to-fine base voxels.

The voxel field, 3D mesher, sphere/capsule/box/ramp CSG, plane flattening, paint/smooth, SDF support-surface queries, portal-linked multi-level routing, vertical falling/landing, bounded Undo/Redo and Raylib edit persistence are implemented in source. Runtime visual acceptance and target-hardware performance remain unverified.

## Requirements audit

| Requirement | Current result | Status |
|---|---|---|
| 3D voxel coordinates | Isotropic 0.1 m X/Y/Z cell grid | Met |
| Per-voxel geometry | Signed-distance sample at voxel centres, quantized to signed 16-bit | Met |
| Per-voxel material | 16-bit MaterialId per cubic cell; material definitions are shared | Met |
| Sparse large-world storage | 16³ bricks only where edits exist; sparse entries per channel with dense promotion/demotion | Implemented in source; target memory measurement pending |
| Expandable voxel bounds | Field grows logically without allocating full volume; new space is air | Implemented; legacy tile/nav dimensions remain fixed |
| Save migration | Compact v3 overrides; v2 dense-brick saves accepted by loader | Implemented in source; existing files not locally exercised |
| Real 3D surface | Surface Nets isosurface from the signed-density field, not XY height extrusion | Met in source; visual seam acceptance remains pending |
| Smooth surface normals | Density-gradient normals | Met in source; visual verification pending |
| Overhangs and arches | Additive sphere/capsule/box CSG can create disconnected solid volumes and air underneath | Met in source |
| Tunnels and caves | Subtractive sphere/capsule/box CSG | Met in source |
| Material painting | PaintSphere writes per-cell material IDs | Met in source |
| Smoothing | Local six-neighbour density smoothing | Met in source |
| Flatten / plane | Plane projection with smooth radial falloff in the same signed-density field | Met in source |
| Ramp | Additive and subtractive sloped triangular-prism CSG brushes with material assignment | Met in source |
| Camera editing | Camera raycast into the same density field; F3 tool cycle and mouse application | Met in source |
| Horizontal cutaway | Temporarily disabled in the renderer acceptance path. Shader clipping is forced off; cut-cap build, upload and draw are not scheduled. Previous cap implementation remains dormant for later rework. | Intentionally disabled while validating full-field rendering |
| Local mesh rebuild | Field bounds dirty only intersecting cached 3D chunks and sampling neighbours | Met in source |
| Feature-preserving mesh decimation | Per-packet conservative QEM after Surface Nets. Only manifold interior edges are candidates; packet boundaries/non-manifold edges and per-vertex material/color seams are locked. Link condition, incident-face normal/area checks and a 0.02 m plane-deviation ceiling gate collapses. | Implemented in source; synthetic planar-hole/material-seam test and CI verification pending |
| Distance LOD | Temporarily disabled. Every chunk across the complete field bounds is built at fixed Step=4 (0.4 m sample interval), independent of camera distance. Camera distance only prioritizes build order; it does not cull chunks. | Fixed-step mode retained; compile/runtime verification belongs to the active CI run |
| Multi-level navigation | `VoxelNavigationSystem` samples multiple upward surfaces in edited regions and runs local 0.5 m A* on `(X,Y,floor)` nodes; portal-linked routes cross adjacent 128 m regions and local edits invalidate only affected routes | Partially met: portal corridor selection tries two Manhattan orders, not a global portal-graph optimum |
| Physics/collision from voxels | Movement in edited regions queries SDF support surfaces and a vertical capsule against the same 3D field; unsupported pawns integrate gravity and land on voxel floors | Partially met: pawn-pawn contact remains the horizontal crowd solver; no rigid-body/rolling terrain debris |
| Save/load of edited chunks | Compressed versioned chunk serializer plus atomic world-file save/load; Raylib viewer autosaves edits and restores them at startup | Met for the Raylib terrain viewer; campaign/world seed migration still needs integration |
| Undo/redo and edit history | Bounded 16-step / 64 MiB chunk-snapshot history; F7 undo and F8 redo in the Raylib editor | Met for in-session edit operations; history is intentionally not persisted between launches |
| Indexed mesh packets | `MaximumVerticesPerMesh=60,000`, `MaximumIndicesPerMesh=270,000`; Surface Nets reuses existing packet vertices before flushing; QEM can lower packet index payload without changing Step=4 | Source change committed; verify Raylib upload/draw and target-GPU behavior in CI/runtime |
| GPU page allocator | Region reserve reduced from 12.5% to about 3.125%; same-mesh cache/hash path retained; replacement appends new ranges before releasing old regions; frame diagnostics separate actual, reserved and physical counts | Implemented in source; index-space fragmentation and transactional replacement still need runtime measurements |
| Measured performance | Sparse storage and per-frame build budgets exist; no target-PC benchmark has been run | Unverified |

## Temporary full-field acceptance mode

The renderer currently uses one fixed Step=4 sampling interval (0.4 m) for every chunk in the field. Adaptive subdivision, Step=16/64 fallback selection, parent/child replacement and LOD fading are bypassed. The candidate collector enumerates the complete field bounds; camera distance only sorts build priority. The fixed-step chunks are packed into indexed GPU pages and reused through the existing mesh hash/cache path.

Cutaway is also bypassed: the cut uniform is always disabled, and no cap build, upload or draw is requested. The cut implementation remains in the source but is dormant.

This is a diagnostic mode to establish stable full-map coverage before reintroducing optional features. Step=4 is the current finest configured whole-world mode, not the native 0.1 m voxel interval. Do not describe it as full native voxel resolution. LOD must not be re-enabled until mixed-step transition geometry is implemented and visually accepted. Cutaway must be reintroduced separately without rebuilding or re-uploading the outer terrain mesh.

## Architecture

### Authoritative data

`Core/Map/SparseVoxelField.cs` stores:
- a signed-distance sample for each materialized voxel centre;
- a separate MaterialId for each cubic voxel cell;
- sparse 16³ chunk allocations;
- procedural base density/material callbacks for untouched terrain;
- CSG operations for sphere, capsule and box addition/removal;
- material painting, local smoothing and field raycasting.

The signed-distance value is not the material's physical mass density. Physical material properties live in `VoxelMaterialCatalog` and are referenced through MaterialId.

### Geometry

`Render.Raylib/VoxelChunkMesher.cs` extracts the outer isosurface from the signed-density field using Surface Nets and computes normals from density gradients. In the temporary whole-field path, every chunk remains at fixed Step=4. Completed packet geometry passes through `VoxelMeshDecimator` before GPU upload: conservative QEM preserves open/non-manifold packet boundaries, material-color transitions and strong vertex-normal features, rejects invalid/flip-producing collapses, and enforces a 0.02 m incident-plane deviation ceiling. `MaximumIndicesPerMesh` is 270,000 while the per-mesh vertex limit remains 60,000.

`Render.Raylib/VoxelTerrainRenderer.cs` currently bypasses both cutaway and adaptive LOD. It enumerates the full field at a fixed Step=4, prioritizes nearby chunks for background construction without camera-culling other chunks, and packs indexed chunk regions into reusable GPU pages. Mesh hashes and the existing cache prevent unchanged terrain from being uploaded again. GPU page diagnostics report actual live geometry, reserved ranges, physical capacity and free-range fragmentation. Chunk replacement keeps the previous GPU regions until the new ranges have been appended successfully. The former cut-cap and adaptive-LOD implementation remains dormant while full-field rendering is validated.

### Editing

`RaylibTerrainViewer` raycasts from the active 3D camera into `WorldMap.Voxels`. The brush operations are CSG/density edits, not changes to a surface-height array. The field raises changed bounds so the world caches and renderer can invalidate only affected areas.

### Base-world compromise

The base canyon profile is still generated from the existing 1 m XY surface data, then evaluated as a 3D signed-distance field at 0.1 m sample spacing. This keeps the large world sparse. It provides a genuinely 3D edit field but does not give the untouched base arbitrary overhangs on its own; those require voxel additions/edits. This is an explicit hybrid design choice, not a claim that the entire 2304 × 2304 × 50 m volume is pre-materialized.

## Remaining acceptance work

1. Keep the full .NET solution build green and resolve compile/API regressions; confirm the new QEM synthetic planar-hole/material-seam test passes.
2. Run the Raylib viewer at close, medium and far zoom. Record frame time, number of surface candidates, capture time, mesh time and memory; inspect transitions among Step=64/16/8/4.
3. Sculpt a sphere, long capsule, box cut, additive arch and sloped ramp. Confirm a bridge/overhang and an air gap below it can exist at the same X/Z coordinates.
4. Move the cut plane through a carved cave and the upper/lower passages. Check the actual cap, holes, contour edge, flicker and seams across 3D chunk boundaries. Include features narrower than 0.4 m to determine whether local XZ refinement is needed.
5. Measure frame time and memory while moving the camera and editing at close range. Current per-frame budgets are source-level safeguards, not measured performance results.
6. Extend multi-level routes across 128 m navigation-region boundaries and integrate vertical pawn-to-pawn contacts.
7. Integrate the voxel edit file with the campaign save-slot/seed lifecycle.
8. Run the Raylib viewer on the target GPU and capture seam, cutaway, tunnel/bridge and memory/FPS evidence.

Current status: the world has a **single adaptive sparse/dense 3D edit store**, a procedural 1 m heightfield base, compact v3 saves with v2 migration, expandable voxel bounds, immutable worker snapshots and a vertical surface index. The outer renderer uses Surface Nets and adaptive sample steps; the section cap is a separate, versioned mesh from the same density field. Untouched base terrain is still not a hierarchy of fully materialized 3D voxels. Target-hardware performance and visual acceptance of the new cap remain unverified.
