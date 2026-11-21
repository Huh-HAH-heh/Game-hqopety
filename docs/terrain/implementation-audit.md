# Voxel terrain implementation audit

Baseline: `feature/raylib-terrain-renderer` after the 3D density-field migration.

## Verdict

The Raylib terrain path now has a real sparse 3D voxel representation and derives its rendered surface from that field. It is no longer the previous vertical-interval/XY-column mesh labelled as voxel terrain.

This is a **sparse procedural-base + 3D voxel-edit** design. The large untouched canyon base is evaluated from the existing base-surface generator as a 3D signed-distance function. Edited areas materialize true cubic 0.1 m voxel data in 16³ chunks. This avoids a dense X×Y×Z world allocation while supporting 3D volume operations.

The voxel field, 3D mesher, editable volumes, SDF support-surface queries, local multi-level routing and Raylib edit persistence are implemented in source. Runtime visual acceptance and target-hardware performance remain unverified.

## Requirements audit

| Requirement | Current result | Status |
|---|---|---|
| 3D voxel coordinates | Isotropic 0.1 m X/Y/Z cell grid | Met |
| Per-voxel geometry | Signed-distance sample at voxel centres, quantized to signed 16-bit | Met |
| Per-voxel material | 16-bit MaterialId per cubic cell; material definitions are shared | Met |
| Sparse large-world storage | 16³ chunks allocated only where edits require them; untouched base is procedural | Met |
| Real 3D surface | Marching-tetrahedra isosurface from eight density samples per source cube, not XY height extrusion | Met |
| Smooth surface normals | Density-gradient normals | Met in source; visual verification pending |
| Overhangs and arches | Additive sphere/capsule/box CSG can create disconnected solid volumes and air underneath | Met in source |
| Tunnels and caves | Subtractive sphere/capsule/box CSG | Met in source |
| Material painting | PaintSphere writes per-cell material IDs | Met in source |
| Smoothing | Local six-neighbour density smoothing | Met in source |
| Flatten / plane | Plane projection with smooth radial falloff in the same signed-density field | Met in source |
| Camera editing | Camera raycast into the same density field; F3 tool cycle and mouse application | Met in source |
| Horizontal cutaway | Cut cap clips/intersects the same density field | Implemented in source; flicker/seam test pending |
| Local mesh rebuild | Field bounds dirty only intersecting cached 3D chunks and sampling neighbours | Met in source |
| Distance LOD | 0.1 / 0.5 / 1.0 / 4.0 m sampling steps; source data remains 0.1 m | Met in source |
| Multi-level navigation | `VoxelNavigationSystem` samples multiple upward surfaces in edited regions and runs 0.5 m A* on `(X,Y,floor)` nodes; edits invalidate only intersecting cached regions and routes | Partially met: hierarchical links/portals across 128 m regions are not yet implemented |
| Physics/collision from voxels | Movement in edited regions queries SDF support surfaces and a vertical capsule against the same 3D field; unsupported pawns integrate gravity and land on voxel floors | Partially met: pawn-pawn contact remains the horizontal crowd solver; no rigid-body/rolling terrain debris |
| Save/load of edited chunks | Compressed versioned chunk serializer plus atomic world-file save/load; Raylib viewer autosaves edits and restores them at startup | Met for the Raylib terrain viewer; campaign/world seed migration still needs integration |
| Undo/redo and edit history | Bounded 16-step / 64 MiB chunk-snapshot history; F7 undo and F8 redo in the Raylib editor | Met for in-session edit operations; history is intentionally not persisted between launches |
| Measured performance | Sparse storage and per-frame build budgets exist; no target-PC benchmark has been run | Unverified |

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

`Render.Raylib/VoxelChunkMesher.cs` extracts isosurfaces from 3D density samples using marching tetrahedra and computes normals from density gradients. LOD skips source samples but does not modify the source field resolution.

`Render.Raylib/VoxelTerrainRenderer.cs` caches 3D chunk meshes, applies frustum/distance selection, clips with the horizontal cut plane and rebuilds dirty chunks within per-frame budgets.

### Editing

`RaylibTerrainViewer` raycasts from the active 3D camera into `WorldMap.Voxels`. The brush operations are CSG/density edits, not changes to a surface-height array. The field raises changed bounds so the world caches and renderer can invalidate only affected areas.

### Base-world compromise

The base canyon profile is still generated from the existing 1 m XY surface data, then evaluated as a 3D signed-distance field at 0.1 m sample spacing. This keeps the large world sparse. It provides a genuinely 3D edit field but does not give the untouched base arbitrary overhangs on its own; those require voxel additions/edits. This is an explicit hybrid design choice, not a claim that the entire 2304 × 2304 × 50 m volume is pre-materialized.

## Remaining acceptance work

1. Build the full .NET solution and resolve any compile/API issues.
2. Run the Raylib viewer at close, medium and far zoom. Check the visible 0.1 / 0.5 / 1 / 4 m mesh steps and transitions.
3. Sculpt a sphere, a long capsule, a box cut and an additive arch. Confirm a bridge/overhang and an air gap below it can exist at the same X/Z coordinates.
4. Move the cut plane through a carved cave and the upper/lower passages. Check for caps, holes, flicker and seams across 3D chunk boundaries.
5. Measure frame time and memory while moving the camera and editing at close range. Current per-frame budgets are source-level safeguards, not measured performance results.
6. Extend multi-level routes across 128 m navigation-region boundaries and integrate vertical pawn-to-pawn contacts.
7. Integrate the voxel edit file with the campaign save-slot/seed lifecycle.
8. Run the Raylib viewer on the target GPU and capture seam, cutaway, tunnel/bridge and memory/FPS evidence.

Current status: the terrain is a **3D sparse voxel/SDF renderer with editable volumes, plane flattening, persisted terrain edits, bounded undo/redo, multi-level pawn support and vertical falling/landing inside edited areas**. It is not yet a fully integrated Space Engineers-style simulation: cross-region level routing, campaign save slots and target-hardware visual/performance acceptance remain open.
