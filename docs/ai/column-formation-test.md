# Unit movement modes

The player-facing movement commands are deliberately limited to three behaviours.
There are no line, wedge, perimeter, rigid-slot or neighbour-following modes exposed.

## Commands

- **ПКМ по земле** — move each selected unit independently. Each one receives its own reachable destination cell and follows its own terrain-aware A* route.
- **Shift+ПКМ по земле** — opens the movement menu:
  - **Переместить по отдельности** — the same independent behaviour as normal ПКМ.
  - **Переместить толпой** — all units share the clicked objective. Each unit keeps its own A* route, with a soft correction toward the moving group centre. There are no fixed slots, neighbour links or shared speed limit.
  - **Переместить колонной** — each unit gets a footprint-valid destination slot along the column axis, using the historical independent A* slot approach. There is no predecessor-following force.
- **V** — opens or closes the information-layer panel.
- **A* — путь выбранных юнитов** — toggles route drawing for selected units. It is off by default. Blue is Move, red is Attack, green is Stealth Move.

## Navigation policy

A* remains the terrain-aware pathfinder. It accounts for blocked cells, unit footprint and height transitions. Routes are cached per unit and rebuilt only when invalidated, when a local route chunk ends, or when a refresh is due.

For long moves, each search is a bounded local chunk. Among the nodes reached by A*, the hot-path policy selects the reachable frontier with the greatest forward progress toward the objective, penalizing excessive sideways drift and detour cost. If the best forward frontier would require moving away from the objective, the previous heuristic frontier remains the fallback. The unit follows this chunk and requests the next one as it advances.

The shared squad Flow Field is not used by movement. The actual movement controller follows the individual A* route, then applies the crowd or column steering correction if that mode was requested.
