# RimClone AI Lab

The lab is a separate, headless experiment runner. It uses the real Core.Unit.UnitSimulation update loop, normal perception memory, movement, weapons, projectiles, damage, deaths, and the existing AiBalanceProfile. The renderer is not required.

## Start here

From the repository root:

~~~powershell
dotnet run --project Experiments/AiLab/AiLab.csproj -- --objective combat --population 16 --generations 10 --workers 4
~~~

Objectives:

| Objective | Purpose | Optimized genes |
|---|---|---|
| combat | General combat performance | Engage, flank, cover, retreat, prone, suppressive fire, squad-contact preference |
| hide | Minimize visual exposure to enemy perception | Engage, cover, retreat, prone |
| break-contact | Break visual contact after being seen | Engage, cover, retreat, prone |
| weapon | Specialize tactical preferences for a selected weapon | Engage, flank, cover, retreat, suppressive fire |
| squad | Evaluate a shared candidate profile across a coordinated squad | Engage, flank, cover, retreat, suppressive fire, squad-contact preference |

Examples:

~~~powershell
# Run the narrow break-contact experiment
dotnet run --project Experiments/AiLab/AiLab.csproj -- --objective break-contact --population 24 --generations 20 --workers 4

# Train tactics for a selected weapon
dotnet run --project Experiments/AiLab/AiLab.csproj -- --objective weapon --weapon rifle --population 24 --generations 15 --team-size 6 --workers 4

# Tune a squad-shared doctrine profile
dotnet run --project Experiments/AiLab/AiLab.csproj -- --objective squad --population 24 --generations 15 --team-size 6 --workers 4
~~~

Use --help for options. --repeats N repeats each scenario with the same scenario seeds for each candidate; every repeat runs both side assignments. Training and holdout use different root seeds. To continue from an existing balance profile, supply --seed-profile path.json.

## Architecture

- One experiment definition per objective. Each definition declares the scenarios and the only genome indices it may mutate. Narrow tasks do not randomly perturb unrelated behavior.
- Parallel isolated simulation. --workers bounds simultaneous candidate evaluations. Each episode creates its own UnitSimulation and WorldMap; there is no shared simulation state and no rendering work.
- Paired comparison. Candidate and baseline sides are swapped on the same scenario seed.
- Purpose-specific fitness. Combat, break-contact and remain-undetected optimize different measurements. Group-scale fitness includes the battle result and a small intent-diversity signal.
- MAP-Elites-style archive. The archive keeps the strongest candidate in each cell of activity versus avoidance of visual exposure. A small number of archived genomes can seed later generations.
- Reproducibility. Training seed, scenario seeds, genome, per-generation CSV, archive, and holdout result are saved. Random numbers are local to each independent episode or to the sequential evolution loop.
- Holdout validation. The best candidate is tested against the starting profile with paired scenarios generated from a separate seed. The profile file is compatible with the game's ai-balance-profile startup option, but is not automatically applied to the active game.

## Outputs

For output path Experiments/AiLab/output/combat-best.json the lab writes:

- combat-best.json: the best AiBalanceProfile, directly loadable by the game.
- combat-best.history.csv: per-candidate results for every generation.
- combat-best.archive.json: behavior-diverse archive.
- combat-best.validation.json: trained-vs-baseline holdout comparison.
- combat-best.json.run.json: run settings, current best, and generation summary.

Generated outputs should remain local and are not intended to be committed.

## Important current-stage boundaries

- hide and break-contact read actual visual contacts from enemy TacticalContactMemory, rather than using distance as a substitute for detection. Their current actions are the actions the existing Utility AI can perform: cover, retreat, prone, flank, engage, and suppression. A future dedicated stealth action (quiet movement, concealment choice, explicit break-contact intent) must be added to gameplay before the trainer can discover that new capability.
- squad evaluates the existing delayed contact sharing with a single tactical profile shared by the candidate faction. This first stage does not invent role assignment or new group commands. Next, introduce explicit role/doctrine genes and connect them to SquadTacticalCoordinator and squad order execution.
- The genome intentionally starts with seven high-impact, already-consumed preference values. The old forty-gene offline evolution remains available. Expanding the genome should be evidence-driven and kept aligned with the actual mechanics.
- The holdout result is a report. Do not accept a profile solely because its training fitness rose; inspect per-objective and per-scenario results.
- No renderer or simulation is shared between workers. The default worker count is capped at eight to keep memory use controlled; tune it after measuring the target machine.
