# Column movement comparison

This branch isolates five column movement models. Other formation choices are
intentionally hidden from the player-facing formation menu.

## Controls

1. Select at least two units.
2. Right-click one of the selected units to open the column test menu.
3. Choose a column variant, then right-click the ground to issue the move.
4. Press **J** to cycle the active variant without reopening the menu.

The active variant is written to the console when changed and whenever a move
order is issued.

## Variants

| Variant | Movement model | What it tests |
|---|---|---|
| Column · slots (19.10) | Each unit receives an individual, footprint-valid A* destination offset from the clicked group centre. No in-transit neighbour-follow force is added. | Historical independent-slot implementation from the 19 October control-group pass. |
| Column · virtual centre | Each unit receives a soft longitudinal preference relative to the predicted moving group centre. The target is not derived from another unit. | Whether weak centre-relative cohesion can preserve a readable column without a chain. |
| Column · staggered | Same centre-relative model, with alternating lateral offsets and a wider interval. | Whether slight lateral staggering reduces the visual rope effect. |
| Column · free corridor | No individual slot and no predecessor. Units retain their own A* route; a weak correction only discourages drifting far outside the shared corridor. | Whether a column-like direction/envelope is enough without explicit ordering. |
| Column · chain (control) | Existing predecessor-following steering and follower speed limiting. | Reference for the current tightly coupled behaviour. |

All variants retain per-unit A* navigation, physical acceleration/braking,
collision avoidance and terrain constraints. Only the column-cohesion method
changes. The historical slot mode is the one based on commit
`439b53fff312a47d1733abd221148fb0d504cfa8` (19 October 2026).

## Interpretation

Compare the same selected units, starting positions and destination for each
variant. Watch for visible rope-like coupling, unnecessary speed synchronization,
overshoot when the front unit turns or stops, width changes in narrow passages,
and whether the group remains recognizably a column without being rigidly attached.
