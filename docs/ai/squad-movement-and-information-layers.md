# Squad movement and information layers

## Goal

A squad is a collective task with autonomous physical members. It is not a large composite pawn, and members do not receive mandatory world-space slots. The squad supplies a shared objective and a dynamic spatial envelope. Each pawn keeps its own footprint-aware A* route and still owns perception, tactical decisions, combat, cover selection and physical movement.

The first implementation targets ground infantry on the existing tile surface. It preserves the current Move / waypoint contract and the force-driven movement controller.

## Runtime layers

### 1. Command and objective layer

A move order is issued to a complete squad selection. The squad stores one shared world objective and one movement profile. Every member navigates toward that shared objective; formation is produced by continuous collective steering during travel, not by assigning each pawn a separate destination slot.

The original objective stays active while members temporarily react to combat. It is not replaced by an enemy's current position.

### 2. Individual navigation layer

Squad members use their own footprint-aware A* routes. The Flow Field path is currently disabled on this branch; the shared movement layer does not replace navigation or issue copied waypoint lists. It only adjusts a safe look-ahead point when the spatial envelope, group velocity or member spacing requires a weak steering preference.

### 3. Formation-envelope layer

The moving group uses a virtual anchor (smoothed core center, average velocity,
heading and shared objective). A behavior-based controller builds a dynamic
adjacency graph once per movement update. Column members are initially ordered
across the compact starting group, then switch to forward ordering after enough
depth has formed. Each follower tracks a moving point behind its current
predecessor and adapts speed to the predecessor's gap and velocity. Line members
are ordered laterally; immediate left/right neighbors maintain intervals while
members align to one shared front. Free groups retain a rounded envelope.
Separation reuses the existing predictive-avoidance and physical-contact systems.
Graph buffers are reused per squad: no second spatial grid, no per-substep
rebuild, and no all-pairs member scan.

Formation profiles describe how the squad moves, not a set of coordinates:

| Profile | Movement behaviour |
|---|---|
| Группа (`Free`) | Loose rounded crowd; members keep breathing room without packing tightly |
| Колонна (`Column`) | Narrow moving centreline; members may stagger forward/back to avoid one another |
| Линия (`Line`) | Members tend toward a shared front perpendicular to the travel direction |
| Wedge / dispersed line / perimeter | Experimental profiles; currently use the generic group envelope without their own dynamic steering behaviour |

The group heading follows its smoothed collective velocity, falling back to the shared anchor objective while starting or turning. Column followers track the live trail point behind their current predecessor and adapt speed to preserve a longitudinal gap; the head follows its own A* route. Line neighbors maintain lateral intervals and equalize depth around the moving anchor. The loose group retains its rounded envelope. Separation remains in the existing predictive-avoidance and physical-contact systems. Neighbor relationships are rebuilt from current projections, not fixed world positions. In narrow passages, formation corrections that A* cannot validate are shortened or rejected; after the passage, the graph reforms. The motor retains acceleration, inertia and braking. A `Rejoining` member receives a weak bias toward the inner envelope; members executing their own combat action are not pulled back into the formation.

`SquadFormationRigidity` is an extension point: `Free` is the default and `Organized` slightly increases cohesion. `Strict` is reserved and currently behaves like `Organized`; strict slot locking is deliberately not implemented.

### 4. Individual decision and physical movement layer

The squad layer does not replace the pawn AI. The existing AI may choose combat, cover, flank, injury response or other local actions.

A pawn is temporarily detached only from soft collective steering while it has an attack order, an active combat target, panic, or an individual attack/cover/flank/injured state. Its membership and the shared squad objective remain intact. The squad does not cancel the other members' movement orders on contact. While a member handles a local combat action, its own A* route remains the primary direction; the envelope adds only a weak outer-boundary and neighbor-spacing bias. After the action, its member state transitions through rejoining before returning to the core. No position or velocity teleport is used.

The final motion still passes through `UnitMovementSystem`: acceleration, braking, inertia, look-ahead steering, predictive avoidance, terrain constraints and contact resolution remain authoritative.

### 5. Regroup and membership state layer

The movement layer tracks a coarse state per member:

- `Following`: participating in collective advance.
- `Engaged`: temporarily handling an individual tactical action.
- `Rejoining`: returning to the collective movement stream after that action.
- `Separated`: currently too far from the group's moving center to count as part of its cohesion center.

The squad exposes a phase, updated even when there is no active Move order:

- `Idle`
- `Advancing`
- `Contact`
- `Regrouping`
- `Arrived`

These are movement summaries, not replacements for the detailed per-pawn AI state machine. Tactical members are excluded from the cohesion center but remain in the squad. `Rejoining` is a tracked state, not an automatic new order: a member whose AI has replaced its original Move order needs a later regroup command or AI order restoration.

### 6. Tactical information and shared knowledge layer

Squad navigation must not become squad omniscience.

The existing `SquadTacticalCoordinator` remains the information-sharing path for observations:
- a member first detects a contact using its own senses;
- a report is delivered after a delay;
- the receiving member gets an estimated position with error radius and confidence;
- confidence decays and the report expires;
- investigation and flank claims prevent every member from duplicating the same local task.

The new movement snapshot is a separate information channel. `SquadMovementInformation` exposes the squad objective, movement phase, formation, rigidity, member-state counts, core velocity and the current envelope radii. `UnitSimulation.TryGetSquadMovementInformation` exposes the squad summary, while `UnitSimulation.TryGetSquadMemberMovementState` exposes a specific member's movement state for a HUD, debug overlay, telemetry or AI diagnostics.

This keeps the data domains distinct:

| Information domain | What it answers | What it must not do |
|---|---|---|
| Command | Where is the squad trying to go? | Decide every pawn's combat action |
| Navigation | Which way is traversable from here? | Reveal enemy locations |
| Formation | How should members move relative to the group? | Force exact slots or override navigation |
| Member state | Who is following, fighting or returning? | Replace individual AI state |
| Tactical contacts | What did this pawn or an ally report? | Provide perfect global truth |
| Diagnostics | What is the collective system currently doing? | Become a second source of gameplay truth |

## Player controls

Squad creation and assignment are enabled again. A selected squad can receive a shared move order through the existing context-menu command.

- The primary movement profiles are `Группа`, `Колонна` and `Линия`; the remaining profiles are experimental.
The always-visible `ИНФОСЛОИ` button beside the top-left menu opens the information panel. Independent checkboxes control vision, hearing, aiming links, AI state, suppression, pawn memory, Utility AI, unit velocity, motor force, Flow Field diagnostics, squad envelope, member states and shared tactical contacts. Flow Field movement is disabled; its diagnostics may remain empty. Select a squad to inspect its envelope and member states.

Velocity arrows show current horizontal velocity. Motor-force arrows show the last resultant drive/braking/lateral force calculated by the movement controller; they do not include gravity or collision impulses.
- Individual attack and cover behaviour remains active during a squad move.

## Current boundaries

This is the first playable collective-movement pass, not the final squad simulation.

Not implemented yet:
- automatic restoration of a member's original Move order after the combat AI replaces it;
- dedicated squad-wide attack/support roles;
- automatic tactical regroup orders after a prolonged firefight;
- explicit player-facing controls for rigidity;
- strict slot formations;
- visual HUD panels for every information channel;
- budgeted/asynchronous Flow Field construction and sharing fields across squads with identical destinations;
- dedicated flow-field movement for multi-floor/space navigation.

Collective arrival is anchor-based. For a column, the anchor target is offset behind the clicked objective by half the intended column depth, while the clicked objective remains unchanged for the HUD. A leader that reaches the clicked point remains in the neighbor graph as a stationary predecessor. When the anchor reaches its target and no members remain detached or rejoining, the cohort receives a Stop order so AI does not reissue the old Move order.

The current system is intended to be inspected in-game before those layers are expanded.
