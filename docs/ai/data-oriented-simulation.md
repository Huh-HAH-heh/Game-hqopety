# Data-oriented simulation path

## Rule

Gameplay state read or written in high-frequency simulation loops belongs in dense value storage, not a graph of short-lived objects.

Keep object-oriented code at orchestration and low-frequency boundaries (systems, authoring/configuration, UI, debug tools). Do not create one object, list, delegate, iterator, or wrapper per event or per entity inside a simulation tick.

## Preferred patterns

- Dense arrays / structure-of-arrays for per-unit state.
- Value-type event records and integer handles for transient events.
- Reusable buffers with explicit capacity growth and high-water trimming.
- Fixed per-listener scratch slots when the gameplay model has a defensible upper bound.
- Active-index spans instead of scanning unused capacity.
- One write-back per observer after a batch has been accumulated.
- Spatial broad-phase before exact distance/visibility tests.
- Explicit lifecycle cleanup and generation checks when slots are reused.

## Avoid in hot loops

- Allocation per event/listener.
- A List<T> owned by every contact or unit.
- LINQ, iterator blocks, closures and per-tick enumerators that allocate.
- Repeated linear scans for spatially nearby contacts.
- Interface dispatch for every candidate when a direct batch loop is possible.

## Migration order

1. Acoustic delivery and per-listener aggregation.
2. Tactical contact memory and anonymous-contact lookup.
3. Other measured high-frequency perception / combat allocations.
4. Only then consider lower-frequency systems; do not rewrite cold code solely to remove classes.

## Current implementation

AcousticEventStore owns value-type acoustic events and their spatial/time batches.
TacticalHearingSystem uses per-observer arrays to accumulate footstep alertness and up to four spatially distinct footstep hypotheses per update. Memory is written after event dispatch rather than once for every heard pulse. Gunshots retain their existing strongest-contact aggregation and urgent danger events remain immediate.

This is a throughput bound, not a global sound-drop rule: the four retained footstep estimates are selected by confidence, and a new spatial hypothesis replaces the weakest only when it is stronger.
