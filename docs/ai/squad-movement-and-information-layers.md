# Squad movement and information layers

## Goal

A squad is a collective task with autonomous physical members. It is not a large composite pawn, and members do not receive mandatory world-space slots. The squad supplies a shared objective, a common navigation field and a preferred spatial envelope. Each pawn still owns perception, tactical decisions, combat, cover selection and physical movement.

The first implementation targets ground infantry on the existing tile surface. It preserves the current Move / waypoint contract and the force-driven movement controller.

## Runtime layers

### 1. Command and objective layer

A move order is issued to a complete squad selection. The squad stores one shared world objective and one formation profile. Member-specific terminal points are only soft preferences around the objective, so the group does not collapse into one destination.

The original objective stays active while members temporarily react to combat. It is not replaced by an enemy's current position.

### 2. Collective navigation layer

`SquadFlowField` builds a reverse weighted Dijkstra field from the shared objective over the map's walkable surface.

- Passability follows the navigation-blocked flag and existence of a surface.
- Adjacent cells respect the current 1.5 m step-height limit.
- Diagonal movement cannot cut across blocked orthogonal corners.
- Height differences add a small traversal cost.
- The field is cached against `WorldMap.TerrainVersion` and its destination.
- Each follower samples a local direction instead of receiving a copied A* waypoint list.

The field is common to squad members, while individual route navigation remains available for units outside squads and as a fallback when the field cannot be sampled or its short look-ahead segment is not traversable for that pawn's footprint.

The first implementation builds a squad field synchronously when its first follower requests steering. It does not yet have a background build budget or cross-squad field deduplication; those are future performance improvements if profiling shows a need.

### 3. Formation-envelope layer

Formation profiles describe where the group prefers to distribute itself. They are not enforced coordinates:

| Profile | Intended distribution |
|---|---|
| Free | Loose, organic spread; default |
| Wedge | Loose forward wedge |
| Dispersed line | Wide, staggered front |
| Column | Narrow depth-oriented spread |
| Perimeter | Ring-like distribution around the objective |
| Line | Shallow multi-row front |

The formation offsets influence steering weakly. Navigation, collision avoidance, blocked terrain, inertia and individual tactical decisions may override them. In particular, narrow passages can compress the group naturally; there is no requirement that all members preserve their nominal width.

`SquadFormationRigidity` is an extension point: `Free` is the default and `Organized` slightly increases cohesion. `Strict` is reserved and currently behaves like `Organized`; strict slot locking is deliberately not implemented.

### 4. Individual decision and physical movement layer

The squad layer does not replace the pawn AI. The existing AI may choose combat, cover, flank, injury response or other local actions.

A pawn is temporarily detached from collective steering while it has an attack order, an active combat target, or an individual attack/cover/flank/injured state. The shared squad objective remains intact. Once the individual action ends, the pawn resumes the group move order and blends back into the common flow. A separate per-pawn Move/StealthMove destination is not overwritten by the squad field. No position or velocity teleport is used.

The final motion still passes through `UnitMovementSystem`: acceleration, braking, inertia, look-ahead steering, predictive avoidance, terrain constraints and contact resolution remain authoritative.

### 5. Regroup and membership state layer

The movement layer tracks a coarse state per member:

- `Following`: participating in collective advance.
- `Engaged`: temporarily handling an individual tactical action.
- `Rejoining`: returning to the collective movement stream after that action.
- `Separated`: currently too far from the group's moving center to count as part of its cohesion center.

The squad exposes a phase:

- `Idle`
- `Advancing`
- `Contact`
- `Regrouping`
- `Arrived`

These are movement summaries, not replacements for the detailed per-pawn AI state machine. A separated pawn continues to navigate toward the squad's shared objective; it is not forcibly pulled or teleported toward a leader.

### 6. Tactical information and shared knowledge layer

Squad navigation must not become squad omniscience.

The existing `SquadTacticalCoordinator` remains the information-sharing path for observations:
- a member first detects a contact using its own senses;
- a report is delivered after a delay;
- the receiving member gets an estimated position with error radius and confidence;
- confidence decays and the report expires;
- investigation and flank claims prevent every member from duplicating the same local task.

The new movement snapshot is a separate information channel. `SquadMovementInformation` exposes the squad objective, movement phase, formation, rigidity, member-state counts, Flow Field readiness and terrain version. `UnitSimulation.TryGetSquadMovementInformation` exposes the squad summary, while `UnitSimulation.TryGetSquadMemberMovementState` exposes a specific member's movement state for a HUD, debug overlay, telemetry or AI diagnostics.

This keeps the data domains distinct:

| Information domain | What it answers | What it must not do |
|---|---|---|
| Command | Where is the squad trying to go? | Decide every pawn's combat action |
| Navigation | Which way is traversable from here? | Reveal enemy locations |
| Formation | How should members be distributed? | Force exact slots at all costs |
| Member state | Who is following, fighting or returning? | Replace individual AI state |
| Tactical contacts | What did this pawn or an ally report? | Provide perfect global truth |
| Diagnostics | What is the collective system currently doing? | Become a second source of gameplay truth |

## Player controls

Squad creation and assignment are enabled again. A selected squad can receive a shared move order through the existing context-menu command.

- `J` cycles the six formation profiles.
- `U` prints the selected squad's movement information snapshot to the debug console.
- Individual attack and cover behaviour remains active during a squad move.

## Current boundaries

This is the first playable collective-movement pass, not the final squad simulation.

Not implemented yet:
- dedicated squad-wide attack/support roles;
- automatic tactical regroup orders after a prolonged firefight;
- explicit player-facing controls for rigidity;
- strict slot formations;
- visual HUD panels for every information channel;
- budgeted/asynchronous Flow Field construction and sharing fields across squads with identical destinations;
- dedicated flow-field movement for multi-floor/space navigation.

The current system is intended to be inspected in-game before those layers are expanded.
