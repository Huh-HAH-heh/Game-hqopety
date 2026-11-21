# Voxel terrain implementation audit

Baseline: `feature/raylib-terrain-renderer` after the 3D density-field migration.

## Verdict

The Raylib terrain path now has a real sparse 3D voxel representation and derives its rendered surface from that field. It is no longer the previous vertical-interval/XY-column mesh labelled as voxel terrain.

This is a **sparse procedural-base + 3D voxel-edit** design. The large untouched canyon base is evaluated from the existing base-surface generator as a 3D signed-distance function. Edited areas materialize true cubic 0.1 m voxel data in 16³ chunks. This avoids a dense X×Y×Z world allocation while supporting 3D volume operations.

The source-level voxel representation and render path are in place. Runtime acceptance, target-hardware performance and integration into the full game simulation are not yet verified.

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
| Camera editing | Camera raycast into the same density field; F3 tool cycle and mouse application | Met in source |
| Horizontal cutaway | Cut cap clips/intersects the same density field | Implemented in source; flicker/seam test pending |
| Local mesh rebuild | Field bounds dirty only intersecting cached 3D chunks and sampling neighbours | Met in source |
| Distance LOD | 0.1 / 0.5 / 1.0 / 4.0 m sampling steps; source data remains 0.1 m | Met in source |
| Multi-level navigation | Existing UnitNavigationSystem remains an XY grid with one primary surface per XY cell | Not met |
| Physics/collision from voxels | Existing gameplay collision/physics is not yet backed by the new SDF field | Not met |
| Save/load of edited chunks | Chunk serialization and world-save integration are not yet implemented | Not met |
| Undo/redo and edit history | Not implemented | Not met |
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
6. Integrate SDF queries into collision and multi-level navigation if units must walk over bridges and through tunnels as distinct floors.
7. Add durable voxel edit serialization and undo/redo before relying on terrain edits in saved campaigns.

Until those runtime and gameplay-integration steps are completed, the terrain can accurately be called a **3D sparse voxel/SDF terrain renderer**, but not a fully integrated Space Engineers-style voxel gameplay system.
