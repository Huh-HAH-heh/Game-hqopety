# RimClone AI Evolution Trainer

Offline evolutionary trainer running the real `Core.Unit.UnitSimulation` loop: perception, Utility AI, movement, weapon handling, projectile/damage and death processing. It runs without a renderer window.

## Run from the repository root

```powershell
dotnet run --project Experiments/AiEvolution/AiEvolution.csproj -- --population 8 --generations 4 --battles 6 --ticks 400 --team-size 4 --seed 20261024
```

Options: `--population`, `--generations`, `--battles`, `--ticks`, `--team-size`, `--seed`, `--output`, `--seed-profile`. At least six battles per candidate covers open-field, wall/barrier, and close-start scenarios from both sides. Higher settings mean more simulation work.

The run writes a directly deserializable `AiBalanceProfile` to `ai-balance-best.json` and per-generation fitness to `ai-balance-best.history.csv`. To resume from a previous profile:

```powershell
dotnet run --project Experiments/AiEvolution/AiEvolution.csproj -- --seed-profile .\ai-balance-best.json --generations 4 --population 8
```

The default profile remains in the candidate pool and the opponent remains the default profile. Trained results are not loaded by the game automatically.

The 31-gene profile evolves action preferences, preferred weapon ranges, cover-search distance and flank waypoint geometry, intent persistence, investigation thresholds, cover/flank response and tactical-distance scoring. Fitness considers outcome, health remaining, enemy damage and survivors. This stage optimizes the current action/tactical surface; it cannot invent a capability that does not exist in game code. To avoid limiting future AI to a few high-level commands, the next stage should expose legal low-level affordances such as firing-position candidates, cover slots, flank lanes, line-of-fire estimates and navigation costs. Weapon physics, perception evidence, path validity, injury rules and emergency gates stay outside the genome.
