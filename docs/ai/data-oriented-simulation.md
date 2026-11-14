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
TacticalHearingSystem uses per-observer arrays to accumulate footstep alertness and up to eight spatially distinct footstep hypotheses per update. Memory is written after event dispatch rather than once for every heard pulse. Gunshots retain their existing strongest-contact aggregation and urgent danger events remain immediate.

This is a throughput bound, not a global sound-drop rule: the eight retained footstep estimates are selected by confidence, and a new spatial hypothesis replaces the weakest only when it is stronger.

PerceptionMemoryStore owns one shared PerceptionContactStorage for all observers. TacticalContactMemory is a value view over an observer slice; it is not a per-unit heap object. Contact state is stored in structure-of-arrays buffers and exposed through readonly slot/generation handles. Each observer's contacts are linked by integer indices, while sensor evidence lives in one shared reusable node pool. Contacts keep at most four recent evidence records per sensory channel.

The custom contact/evidence views and their struct enumerators avoid per-contact lists and interface-backed enumeration in AI and hearing scans. Generation checks prevent cached handles from accidentally referring to a different contact after slot reuse.

The current observer-scoped lookup for known subjects and anonymous nearby contacts is still a linear scan. A spatial index for anonymous-contact correlation is a separate next optimization, to be justified against profiling. Shared pools intentionally retain their high-water capacity to avoid recurring allocations; their memory footprint still needs measurement under large battles.
