# Terrain request implementation audit

Audit baseline: `feature/raylib-terrain-renderer`, after the HUD/camera/voxel-Lod work and before this audit note. This is a source review; it is not a claim that the game was visually tested.

## Verdict

The recent work improved the voxel-preview renderer and added a future architecture proposal, but it did **not** implement a Space Engineers-style editable voxel landscape. The word "Voxel" currently names an interval-column renderer, not a cubic 3D density terrain.

The current implementation is a useful prototype for vertical caves and horizontal cutaways. It is not ready to be called a full voxel terrain system.

## Findings against the request

| Requirement | Current result | Verdict |
|---|---|---|
| Landscape with real 3D editable volume | 1 m x 1 m XY columns with 0.1 m vertical runs | Not met |
| Overhangs, arches and arbitrary slopes | Only shapes expressible by neighbouring vertical columns; stair-stepped at 1 m XY | Not met to SE quality |
| Player-facing terrain editing | APIs for single cells and axis-aligned cuboids; no in-world brush/editor | Not met |
| Sphere/capsule/ramp/flatten tools | No density brushes or matching tool UI | Not met |
| Material painting independent of shape | Material ID exists, but no brush UI or terrain material renderer | Not met |
| Continuous 3D surface geometry | Axis-aligned top/bottom quads and cardinal side walls from column runs | Not met |
| Stable horizontal cutaway | GPU clip plane plus separate cut mask; boundary and replacement logic received fixes, but still needs runtime verification | Partial |
| Multi-level navigation | 0.5 m XY grid with one surface height per XY cell | Not met |
| Local navigation rebuild after terrain edits | Nav grid is cached by global `WorldMap.TerrainVersion`; next update rebuilds the full grid | Not met |
| Large-world memory budget | Sparse interval columns; no dense X*Y*Z array | Partial success |
| LOD | Separate 1 m / 4 m snapshots; coarse snapshot samples the centre column and can lose small features | Partial |

## Concrete code evidence

### Storage

`Core/Map/SparseVoxelStore.cs` uses 16x16 XY chunks. The common case is represented by `TopLayers[256]` plus `MaterialIds[256]`; edited columns are represented by `Dictionary<int, SolidRun[]>`. There is no Z chunk coordinate, no 3D sample array, and no signed density field.

Native spacing is 1 m along X/Y and 0.1 m along Z. This is a compressed set of vertical intervals, not 0.1 m cubic voxels.

### Base landscape and geometry

`World/WorldGenerator.cs` uses `GenerateSmooth` with `patchSize = 1`: one height per 1x1 m XY sample. It then creates the cave demo by carving axis-aligned volumes and a series of vertical-column runs.

`Render.Raylib/RegionMeshBuilder.cs` reads each column's solid/air intervals and emits horizontal faces and four cardinal side faces. Normals are axis-aligned. It does not extract an isosurface from a 3D field, so terrain cannot have smoothly sloped arbitrary surfaces inside a cell.

The coarse 4x4 m LOD uses the centre source column. Thin arches, small rocks, narrow passages and other features between sample points can disappear at distance.

### Materials and appearance

`Render.Raylib/RaylibViewerSettings.cs` returns the same `BaseRock` colour for the surface, cliffs and cut surfaces regardless of `materialId`. The current default is a very dark neutral colour. Material IDs 2 and 3 only change cut-hatching direction; they do not provide a real material/texture system. That is far from the layered, textured ground presentation of Space Engineers.

### Editing

`WorldMap` exposes `SetVoxel`, `SetVoxelVolume`, `SetStaticVolume` and `CarveStaticVolume`. These are useful low-level APIs, but they are a single-cell write or axis-aligned cuboid edit. There is no Sphere, Capsule, Ramp, Terrain Auto Level, preview gizmo, continuous brush stroke, undo stack or material-paint tool comparable to Voxel Hands.

### Navigation

`Core/Unit/UnitNavigationSystem.cs` uses `NavigationCellsPerMeter = 2`, so the horizontal grid spacing is 0.5 m. Each XY cell has one `_navigationHeights` value and one passability value. It cannot store a bridge floor and a tunnel floor as separate nodes at the same XY position.

`EnsureNavigationGrid` invalidates against the global `WorldMap.TerrainVersion` and loops across the complete XY grid when it changes. This is not a local per-chunk rebuild, and it does not read a 3D density field.

## Space Engineers comparison

The target should be copied at the level of behaviour and player tools, not by assuming every Space Engineers version uses one universal implementation.

- Space Engineers Voxel Hands exposes Box, Capsule, Ramp, Sphere and Terrain Auto Level; it supports adding, removing and replacing voxel material. See https://spaceengineers.wiki.gg/wiki/Voxel_Hands
- The newer VRAGE3 planet work still describes a heightmap base, augmented by small voxel storages for overhangs and boulders. This is a practical reference for a large world: keep the cheap base terrain, then materialize editable 3D voxel volumes where the shape requires them. See https://blog.marekrosa.org/2025/08/mareks-dev-diary-august-21-2025/

The RimClone target should therefore be a **hybrid landscape**:
1. A cheap generated base surface at the current 1 m XY scale.
2. Local, genuine 3D voxel storages for tunnels, arches, overhangs, boulders and edits.
3. A single edit field (density + material) inside each storage. Render meshes, slice intersections, collision and navigation all consume that field.
4. Tools matching the reference behaviour: box, sphere, capsule, ramp/plane, flatten, smooth, carve, add and material paint.

This avoids allocating a cubic grid for the whole 2304 x 2304 x 50 m world while still supporting real 3D shapes where needed.

## Changes made during this audit

- Camera orbit was inconsistent: README promised right mouse, while the controller was still listening to Shift+left mouse. The controller, HUD and README now consistently use **Shift+right mouse drag**. MMB remains pan; Shift+MMB remains the temporary speed adjustment.
- Cut-cap geometry had two duplication paths: the cap lookup could return material at an exact natural top boundary, and the base mesher could also emit a top face at the cut plane while the separate cap mesh emitted the same face. The voxel base mesher now suppresses its own top face when the plane crosses a solid run; the cap lookup creates that face once. At an exact natural top boundary the base face remains and no separate cap is created. A plane exactly between two adjacent solid material runs still receives a cap using the material below the plane.
- Cut-cap meshes are built before replacing old ones. Region scanning is round-robin, and old base meshes remain visible while replacements are being prepared.
- Camera zoom documentation was also brought in line with the current axis zoom (no cursor-anchored focus shift).
- The architecture proposal now uses an explicit Space Engineers-style hybrid base-surface + local 3D voxel-storage target rather than implying a dense 3D volume across the whole map.

## Remaining acceptance work

The control and cut fixes are source-level changes and still need a runtime pass. The following scenarios are required before claiming the original request is complete:

1. Hold Shift+RMB and rotate in both axes; release either input and confirm orbit stops. Pan with MMB separately.
2. Hold Q/E while the cutaway is enabled; check slow movement and fast movement, then place the plane exactly on a natural top face and just inside a solid volume. No flickering seam or duplicate face should appear.
3. Move the cut plane across the full world while switching fine/coarse LOD. No missing caps, stale cut levels, black regions or visible chunk seams.
4. Edit a sphere, capsule and ramp through the intended editor; verify a true overhang, an arch and a cave can be created and then removed/painted.
5. Verify an agent can route on the upper surface, bridge, and lower tunnel at the same XY coordinates.
6. Edit one voxel storage and confirm only its mesh, collision and neighbouring nav chunks are rebuilt.

Until the 3D voxel storage, brush tools and layered navigation are implemented, acceptance of the Space Engineers-style terrain request is **failed / incomplete**.
