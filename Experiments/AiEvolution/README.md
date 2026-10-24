# RimClone AI Evolution Trainer

Offline evolutionary trainer running the real `Core.Unit.UnitSimulation` loop: perception, Utility AI, movement, ranged and melee weapon handling, projectile/damage and death processing. It runs without a renderer window. Each team contains both ranged fighters and a knife user so range and melee positioning genes receive feedback.

## Run from the repository root

```powershell
dotnet run --project Experiments/AiEvolution/AiEvolution.csproj -- --population 8 --generations 4 --battles 8 --ticks 600 --team-size 4 --seed 20261024
```

Options: `--population`, `--generations`, `--battles`, `--ticks`, `--team-size`, `--seed`, `--output`, `--seed-profile`. At least eight battles per candidate covers open-field, wall/barrier, close-start and generated-canyon scenarios from both sides. The canyon uses the same `WorldGenerator` as normal gameplay. Higher settings mean more simulation work.

The run writes a directly deserializable `AiBalanceProfile` to `ai-balance-best.json` and per-generation fitness to `ai-balance-best.history.csv`. To resume from a previous profile:

```powershell
dotnet run --project Experiments/AiEvolution/AiEvolution.csproj -- --seed-profile .\ai-balance-best.json --generations 4 --population 8
```

The default profile remains in the candidate pool and the opponent remains the default profile. To load a trained profile into normal gameplay:

```powershell
dotnet run --project App/App.csproj -- --ai-balance-profile=.\ai-balance-best.json
```

The game loads the file once at startup. If the file cannot be read or deserialized, it logs the problem and keeps the default balance; it never silently adopts a training result.

The 31-gene profile evolves action preferences, preferred weapon ranges, cover-search distance and flank waypoint geometry, intent persistence, investigation thresholds, cover/flank response and tactical-distance scoring. Fitness considers outcome, health remaining, enemy damage and survivors. This stage optimizes the current action/tactical surface and now evolves low-level choices such as preferred firing distance, cover-search radius and flank waypoint geometry. It still cannot invent a capability that does not exist in game code. The next stage should improve the candidate generator with scored firing-position candidates, cover slots, flank lanes, line-of-fire estimates and navigation costs. Weapon physics, perception evidence, path validity, injury rules and emergency gates stay outside the genome.
