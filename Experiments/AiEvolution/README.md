# RimClone AI Evolution Trainer

Offline evolutionary trainer running the real `Core.Unit.UnitSimulation` loop: perception, Utility AI, movement, ranged and melee weapon handling, projectile/damage and death processing. It runs without a renderer window. Each team contains both ranged fighters and a knife user, and the trainer registers both teams as squads so delayed contact-sharing behavior is exercised.

## Run from the repository root

```powershell
dotnet run --project Experiments/AiEvolution/AiEvolution.csproj -- --population 8 --generations 4 --battles 8 --ticks 600 --team-size 4 --seed 20261024
```

Options: `--population`, `--generations`, `--battles`, `--validation-battles`, `--ticks`, `--team-size`, `--seed`, `--output`, `--seed-profile`. At least eight battles per candidate covers open-field, wall/barrier, close-start and generated-canyon scenarios from both sides. The `--seed` value changes formations, equipment assignment and barrier gaps; all candidates still face the same layout per match index, making comparisons fair. The canyon uses the same `WorldGenerator` as normal gameplay. Higher settings mean more simulation work.

The run writes a directly deserializable `AiBalanceProfile` to `Experiments/AiEvolution/output/ai-balance-best.json` and per-generation results to `Experiments/AiEvolution/output/ai-balance-best.history.csv`. The CSV includes overall score, win/draw rate, survivors, health, kills, key genes, scenario scores/win rates, average shots and rounds, navigation routes built/failed, route-failure rate, navigation searches, and Utility AI decisions. At the end, a separate holdout run uses a different seed to compare the trained profile with the default profile; its result is saved as `ai-balance-best.validation.json` and includes the same scenario breakdown plus score/win-rate and operational metrics. A candidate must be judged on both overall and scenario-specific results; one strong average can hide a weak canyon or barrier matchup, and a high score with frequent path failures is a warning sign. That comparison is a report, not automatic adoption. Generated outputs are excluded from Git. To resume from a previous profile:

```powershell
dotnet run --project Experiments/AiEvolution/AiEvolution.csproj -- --seed-profile .\Experiments\AiEvolution\output\ai-balance-best.json --generations 4 --population 8
```

Without `--seed-profile`, the exact default profile is in the candidate pool. With `--seed-profile`, evolution starts from that saved profile instead of creating a hybrid of its values and default genes. The opponent remains the current default profile. To load a trained profile into normal gameplay:

```powershell
dotnet run --project App/App.csproj -- --ai-balance-profile=.\Experiments\AiEvolution\output\ai-balance-best.json
```

The game loads the file once at startup. If the file cannot be read or deserialized, it logs the problem and keeps the default balance; it never silently adopts a training result.

The 40-gene profile evolves action preferences, preferred weapon ranges, cover-search distance, range-relative near/far flank orbits and flank waypoint geometry, intent persistence, investigation thresholds, cover/flank response and tactical-distance scoring. Flank candidates are ranked by target range, travel distance and actual terrain line of sight; the genome evolves range-error, travel-cost and line-of-sight weights as well as backoff/lateral proportions. Default geometry keeps them near the weapon's working range. Cover search rejects blocked destinations and ranks remaining occluded positions by threat separation versus travel cost, both of which are trainable. Fitness considers outcome, health remaining, enemy damage and survivors. This stage optimizes the current action/tactical surface and now evaluates all legal flank points by weapon working range, travel distance and actual line of sight. Genome controls include the near/far flank orbit fractions, preferred firing distance, cover-search radius and flank waypoint geometry. It still cannot invent a capability that does not exist in game code. The next stage should improve the candidate generator with scored firing-position candidates, cover slots, flank lanes, line-of-fire estimates and navigation costs. Weapon physics, perception evidence, path validity, injury rules and emergency gates stay outside the genome.


## Watching and controlling a training run

Start the trainer from a real PowerShell/terminal window to use interactive keys. The console reports generation, candidate, battle/scenario, tick progress, elapsed time and an estimated time remaining. Each completed battle reports its result; the CSV keeps per-generation and per-scenario scores.

- `P` or `Space`: pause/resume at a safe point between simulation ticks.
- `Esc`: request a graceful stop; `Ctrl+C` also stops and preserves the best-profile checkpoint.
- The trainer is headless and runs as fast as the CPU allows. It has no playback slow motion because there is no rendered battle to watch. Use the saved profile in the game to inspect live behavior.

## In-game time controls and performance readout

When the profile is loaded in the game:

- `F7`: pause/resume simulation while rendering continues.
- `F4`: advance one simulation step while paused.
- `Shift+F12`: slow simulation down (`1x → 0.5x → 0.25x`).
- `F12`: speed simulation up (`1x → 2x`).
- `F11`: return to `1x`.

The game title and `[PERF]` line now show `Speed`, `SimRate`, and `UPS`. `SimRate` means game-seconds advanced per real second; `UPS` means simulation updates per second. The existing `Sim=...ms` value is the wall-clock cost of one simulation update, while `FPS` describes rendering. This separates a smooth window from a simulation thread that cannot keep up with real time.
