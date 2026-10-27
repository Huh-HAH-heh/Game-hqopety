# RimClone — Physics-Driven Movement Refactor Plan

**Status:** approved design scope; implementation is intentionally not included in this commit  
**Base branch:** `feature/squad-behaviac-orders`  
**Base commit:** `3a6697b4a731fa47ac372256f8a440a036d106ef`  
**Working branch:** `feature/physics-driven-movement-plan`

## 1. Goal

Replace direct waypoint-following translation with a compact, physics-driven movement model. Keep the existing gameplay command/API and terrain-aware route planning. A waypoint expresses *where a unit wants to go*; it must not directly write the unit's position.

The same motion state must ultimately support ground locomotion, falls/impulses, collisions, and movement in zero gravity. Ground movement uses surface contact and active simplified gait. In zero gravity, acceleration and course correction depend on the unit's equipment.

This is a staged change, not a full physics-engine rewrite.

## 2. Audit of the current movement path

Audited on `feature/squad-behaviac-orders` at base commit above.

### `Core/Unit/UnitMovementSystem.cs`

- Reads the current target and asks `UnitNavigationSystem.TryGetWaypoint` for a 2D waypoint.
- Moves by `position.X/Y += direction * (MoveSpeed * deltaTime)`; this sets an immediate, constant speed rather than integrating acceleration.
- When the remaining distance is smaller than the frame step, assigns the exact waypoint coordinates. This is a positional snap.
- Replaces `Position.Z` with sampled surface height during movement, so a future airborne state cannot preserve vertical motion.
- The overlap resolver performs positional pushes and then sets both units' velocities to zero. Contact therefore destroys momentum rather than resolving it physically.
- Collision checks currently use horizontal distance; pose-dependent footprint and vertical separation are not part of the collision response.

### `Core/Unit/UnitStore.cs` and `Core/Unit/UnitDefinition.cs`

- `UnitStore` already uses structure-of-arrays for position, velocity, target, dimensions, posture, and movement speed. Keep that data-oriented pattern; do not add per-unit heap objects to the hot update loop.
- `UnitDefinition` already exposes `BodyMassKg`, but the mass is not currently copied into a movement-state array in `UnitStore`.
- Orientation is currently represented mainly by horizontal `BodyNormal` / `HeadNormal`. There is no full 3D rigid-body orientation or angular-velocity state in the movement store.

### `Core/Unit/UnitNavigationSystem.cs`

- Owns terrain-aware routes, waypoint cursors, route reuse and a per-update route-build budget.
- It is not the system that causes the constant-speed stepping. Keep its route-building policy and the existing `MoveTo`/target API intact for the first movement stages.
- Navigation continues to supply the next waypoint; movement integrates the actual position and asks for the next waypoint from that actual position.

### `Core/Unit/UnitBodySystem.cs`

- Computes body-part animation from current position, horizontal velocity, posture and a global sine-based animation time.
- Walking/crawling currently animates the rig but does not contribute traction or balance to the movement model.
- The new movement state must become the source of gait phase and physical body orientation; the rig remains a visual representation, not the hit model.

### `Core/Unit/UnitSimulation.cs`

- Current phase order is AI -> movement -> weapons -> body animation -> melee/projectile processing.
- Keep movement updated before body visuals and projectile tracing. Do not create a new generic framework or replace the main simulation loop unless implementation proves it necessary.

## 3. Fixed design decisions

1. **Preserve the command API.** Existing target assignment, `MoveTo` behavior and waypoint route requests remain valid. The movement command sets intent/target, not position.
2. **Force-based motion.** Position is integrated from velocity; velocity changes through bounded acceleration, braking, traction, impulses and collision response. No snap to waypoint.
3. **Unified 3D state.** The physical state is able to represent position, linear velocity, orientation and angular velocity in 3D. Ground contact constrains the unit to the surface; in air, height follows physics rather than the terrain sampler.
4. **Ground movement remains route-driven.** Existing map and 2D waypoint navigation remain. The grounded controller follows surface height/normals and honors route passability; airborne movement is a physical interruption, not a new pathfinding mode.
5. **Active simplified gait.** Per-unit gait phase, cadence and stride contribute to traction. Surface grip limits acceleration and sharp turns. The gait model is an approximation, not a simulation of every joint or muscle.
6. **Compact body; no full ragdoll.** A unit has mass, 3D orientation, linear/angular velocity, and a simplified collider. Standing, crouching and prone states use different approximate collider extents; the collider follows body orientation during a fall. The rig does not determine gameplay hits.
7. **Hybrid crowd handling.** Units steer away from one another before contact. Contacts exchange velocity/impulse and use limited positional correction; they must not zero velocity as the default response. Use the existing spatial-grid approach for broad-phase filtering.
8. **Control disruption is a motion state.** Strong impulses may put a unit into loss-of-control / recovery states. Movement stops overriding physical motion while control is lost. The AI then evaluates the situation and may keep or cancel its current order. Motion code does not directly decide combat tactics.
9. **Equipment changes mobility.** Zero-gravity thrust and rotation control depend on movement-capable equipment and the resulting mobility profile. No equipment means no powered arbitrary course correction; inertia and external impulses still apply.
10. **Physically constrained weapon aiming is a later consumer of motion state.** Movement exposes body orientation/angular velocity and stabilization capability; weapon-aim changes are not part of the first movement milestone.

## 4. Explicit non-goals

The movement refactor does **not** implement or redesign:

- HP, damage, trauma, injury, bleeding or medical mechanics.
- Ballistics, armor penetration, hit-region selection or the hit model.
- A full ragdoll or per-bone physics.
- A replacement for `UnitNavigationSystem`, A*, or the existing waypoint API.
- A revival of the archived group-movement implementation.
- A broad rewrite of Behaviac or the tactical AI.
- A new generic physics/framework layer unrelated to the game's current update path.

Existing health can continue to be consulted for the already-existing dead-unit guard. No new health-related behavior is introduced by this plan.

## 5. Implementation sequence

### Stage 1 — Replace constant-speed translation with a movement controller

**Files:**
- `Core/Unit/UnitMovementSystem.cs`
- `Core/Unit/UnitStore.cs`
- `Core/Unit/UnitDefinition.cs` only if the current mass definition needs a small movement-facing accessor
- `Core/Unit/UnitNavigationSystem.cs` only if a small interface adjustment is strictly required; route policy stays unchanged

**Changes:**
- Add data-oriented motion fields needed to evolve safely: stored mass, 3D orientation, angular velocity, ground/contact state, control state and gait phase. Initialize, clear and grow them alongside the current arrays.
- Convert waypoint error into a desired velocity, then into bounded acceleration. Limit deceleration so the unit begins braking before a waypoint/goal rather than arriving at full speed.
- Integrate `Velocity` then `Position` using bounded simulation substeps; handle a large `deltaTime` without tunnelling or unstable acceleration.
- Remove exact waypoint snapping. Reaching a waypoint means entering a tolerance, braking, then asking navigation for the next waypoint.
- While grounded, maintain contact with the sampled terrain. Do not unconditionally overwrite Z if the motion state is airborne.
- Keep current melee range stopping and dead-unit handling, but express them as controller constraints rather than allowing a direct position snap.

**Acceptance:**
- Existing target assignment and waypoint routing still work.
- Units visibly accelerate and brake; they cannot instantaneously switch from rest to full speed or reverse direction at full speed.
- There is no positional snap at a waypoint or goal.
- A unit continues from its actual position after route updates.
- The first stage adds no per-frame allocation per unit.

### Stage 2 — Ground contact, active gait and surface traction

**Files:**
- `Core/Unit/UnitMovementSystem.cs`
- `Core/Unit/UnitStore.cs`
- `Core/Unit/UnitBodySystem.cs`
- Small map-surface query additions only if the existing `WorldMap.GetSurfaceHeight` API cannot provide enough information

**Changes:**
- Track whether the body is grounded, airborne or recovering contact.
- Derive a usable surface normal from nearby height samples; avoid requiring a whole new terrain system.
- Add a per-unit gait phase. Cadence and stride follow the requested locomotion intent and actual speed.
- Apply traction during stance portions of the simplified gait; cap horizontal acceleration by grip and the unit's locomotion profile. Swing portions do not create ground traction.
- Respect standing/crouching/prone locomotion limits without forcing a lying unit upright.
- Drive visual walk/crawl phase from this physical gait state instead of running a separate unrelated cycle.
- Keep the model approximate and cheap; do not add physical foot objects.

**Acceptance:**
- Standing, crouching and prone units have distinct movement limits and collider extents.
- The unit's legs animate from actual motion; the gait does not continue as if running while the body is stationary or uncontrolled.
- Traction/turning is weaker on a low-grip surface and cannot exceed the controller's traction limit.
- Grounded units follow terrain while airborne units retain their physical Z.

### Stage 3 — Impulses, falls, landing and loss of control

**Files:**
- `Core/Unit/UnitMovementSystem.cs`
- `Core/Unit/UnitStore.cs`
- `Core/AI/UnitAiSystem.cs` only for consuming a clear movement-state event/flag; do not rewrite decision logic

**Changes:**
- Add an explicit impulse entry point for movement producers: an impulse changes velocity according to mass; an off-centre impulse may change angular velocity.
- Add gravity integration while unsupported. A fall, jump or explosion displacement uses 3D integration; the ground sampler no longer teleports the unit vertically onto the terrain.
- Resolve ground contact and landing, then restore grounded constraints.
- Large physical disturbances can enter a temporary uncontrolled state. While uncontrolled, the normal waypoint controller must not overwrite physical velocity/position.
- Recovery depends on motion state (remaining velocity/rotation, contact and stabilization capability), not an arbitrary fixed animation alone.
- Publish the motion-state change for the AI to consider at its normal think cadence. The AI can decide to retain or cancel the order; movement does not clear the order on its own.

**Acceptance:**
- An impulse moves the unit by changing velocity, not by assigning a destination position.
- Different masses respond differently to the same impulse.
- Units can be pushed off a ledge, fall under gravity, collide with the ground and regain contact without snapping.
- A unit in an uncontrolled state cannot be driven back onto its route by the navigation controller.
- No HP/damage/trauma mechanics are added.

### Stage 4 — Crowd avoidance and momentum-aware contact

**Files:**
- `Core/Unit/UnitMovementSystem.cs`
- `Core/Unit/UnitStore.cs`

**Changes:**
- Keep a broad-phase spatial grid; use it to calculate simple anticipatory steering for nearby units before they overlap.
- Resolve pairs once per substep, with vertical separation considered.
- On contact, exchange a bounded impulse based on relative velocity and mass. Retain the current limited overlap correction only as a penetration-recovery aid.
- Remove the current default behavior of setting both units' velocity to zero after every overlap.
- Approximate body shape by posture and orientation without introducing per-limb colliders. Ignore contact between bodies that are clearly separated in Z.
- Keep CPU work bounded for large crowds; reuse buffers and avoid per-frame allocations.

**Acceptance:**
- Units usually make room before contact.
- A contact changes relative motion; it does not freeze both units.
- Coincident centers and dense crowds remain numerically stable without NaN, endless jitter or unbounded pair work.
- Performance metrics distinguish controller, avoidance and contact-resolution costs.

### Stage 5 — Equipment-driven zero-gravity movement on the same physics state

**Files:**
- `Core/Unit/UnitStore.cs`
- `Core/Unit/UnitMovementSystem.cs`
- `Core/Unit/Inventory/UnitInventoryStore.cs` / `Core/Unit/UnitSimulation.cs` only for caching/recomputing mobility when equipment changes
- Small environment/gear data types near the movement code, if needed; avoid a generic engine abstraction

**Changes:**
- Add an environment input that supplies gravity and whether a supporting surface exists. Ground play uses the current map surface; zero-g uses no gravity and no support plane.
- Derive/cache a mobility profile from equipped movement gear: available translational thrust, rotation control/stabilization and any resource limits supported by existing equipment data.
- No powered mobility equipment means the unit cannot arbitrarily accelerate or brake in zero-g; current velocity persists unless external forces or contact change it.
- Equipped units can accelerate, brake and rotate up to their profile limits. More equipment is not automatically better in every dimension: added mass must be considered in the resulting acceleration.
- Retain waypoint as the navigation intent. The controller follows it by applying permitted forces; it does not teleport the unit or cancel inertia.

**Acceptance:**
- The same position/velocity/orientation state is used on ground and in zero-g.
- In zero-g, no-thrust units preserve inertia; equipped units can correct the path only within the profile's limits.
- Changing equipment refreshes mobility values without rebuilding navigation or introducing allocations every frame.
- Ground navigation behavior is unchanged.

### Stage 6 — Visual/body handoff and final integration

**Files:**
- `Core/Unit/UnitBodySystem.cs`
- `Core/Unit/UnitMovementSystem.cs`
- Only later, and as a separate narrowly scoped change, weapon-aim consumers of orientation/stabilization

**Changes:**
- Make the visual root follow the physical orientation and actual body state. Do not use the visual rig as the gameplay collision/hit model.
- Map grounded gait, fall, uncontrolled rotation and recovery to visuals without letting animation overwrite the physical state.
- Provide body orientation/angular velocity for later weapon stabilization; do not implement weapon behavior inside movement.
- Keep the existing simulation phase order unless a demonstrated dependency requires a minimal adjustment.

**Acceptance:**
- Animation never moves the gameplay position or corrects physics by itself.
- Facing, turning and fall visuals track the physical state.
- The weapon system can consume the published body-motion data later without reading the visual rig as the source of truth.

## 6. Main data-flow contract

```text
AI / player order
      |
      v
existing MoveTo target + UnitNavigationSystem waypoint
      |
      v
movement intent -> locomotion controller
      |
      v
bounded acceleration / traction / thrust
      |
      v
3D velocity + orientation integration
      |
      +--> ground contact / fall / landing
      |
      +--> predictive avoidance / contact impulse
      |
      v
actual UnitStore position and motion state
      |
      +--> UnitBodySystem visuals
      +--> AI reads control-disruption state at its next decision pass
      +--> weapons may consume body motion in a later separate task
```

## 7. Rollout and safety rules

- Implement **Stage 1 first**, then stop for a focused review before adding falls, impulses or zero-g. Do not land all stages in one large rewrite.
- Preserve the current route cache and navigation budget. A movement refactor must not silently increase A* request volume.
- Keep the hot loop data-oriented: SoA arrays, reusable spatial-grid buffers, no per-unit heap objects or LINQ in the update path.
- Preserve existing melee stop distance and dead-unit handling unless an implementation detail requires a documented adjustment.
- Keep the physical hit model and visual rig independent.
- No unrequested health/damage changes, no full ragdoll, no group-movement-manager revival.
- Each stage is complete only when its acceptance criteria are observed in a focused gameplay scenario and the timing counters show that its added cost is understood.

## 8. First implementation task

Only after this plan is accepted as the implementation contract:

1. Change the movement state storage as needed for the controller.
2. Replace direct `position += direction * speed * deltaTime` with acceleration-controlled integration and braking.
3. Remove waypoint/goal position snapping.
4. Make surface-height assignment conditional on grounded state.
5. Leave collision response, falling, gait traction and zero-g for the next stages rather than mixing them into the first patch.
