# Behaviac tactical AI integration for RimClone

RimClone uses the vendored upstream Behaviac C# runtime from Tencent/behaviac (see `Vendor/BehaviacRuntime/UPSTREAM.md` and its BSD 3-Clause license). The live decision policy is now loaded from an external Behaviac XML asset at `Core/AI/Behaviac/BehaviacData/rimclone_unit_decision.xml`; the game no longer selects its policy from a C#-constructed tree. Behaviac's own runtime `Workspace`, `Agent`, `Selector`, `Sequence`, `Condition` and `Action` classes load and execute the XML.

RimClone-specific facts and action bindings are registered in `BehaviacRuntimeHost`. They connect decisions to the existing game simulation; they do not reimplement movement, pathfinding, vision, weapons, ammo, damage or rendering.

## Live intents in the XML tree

The root selector tests high-priority safety branches first: `Dead`, suppression-driven `SeekCover`, then `RetreatToCover`. Visible enemies enter a nested **Combat Response** sequence and selector: try `FlankTarget` when the unit is eligible; otherwise fall through to `EngageTarget`. This is an actual nested Behaviac selector in both the runtime XML and Designer workspace, not just a flat list of C# if-statements.

If no visible-target branch succeeds, the tree tries `SuppressiveFire` against a remembered point, `InvestigateLastKnownTarget` through the blind terrain, and finally `Patrol` when there is no current target. The selected intent continues into RimClone's existing pathfinding, movement, weapon and damage systems.

One third of unit slots are eligible to be flankers, selected deterministically. Flanking starts at 12 map units and continues up to the lesser of 75 units or 85% of the unit's vision range. The lower threshold was deliberately widened because front-line units often acquire a clear target only after advancing through the terrain folds. The maneuver uses a lateral supporting-fire position, tries alternate sides around blocked tiles, stays stable for the same target and is interrupted by suppression, low health or target loss. On completion, the unit returns to normal combat instead of oscillating around the enemy.

## Watch the tactics in-game

The default launch creates the 1,600-unit battle on a more folded test landscape. The battle terrain has winding broken ridgelines, low drainage cuts, a shallow crater with a rim, and gaps/saddles through the ridges. The raised shoulders create line-of-sight blind zones, while the gentler slopes and gaps preserve alternative routes for A*.

- Press **I** to toggle AI tactical debug. Active flankers draw **purple lines and rings** to their flank goals; units seeking cover draw **blue lines and rings**. The overlay now shows all on-screen movers, not only the selected unit.
- Select a unit to inspect its own target/goal line and the **Фланг**, **Поиск**, **Атака** or **Укрытие** state in the unit card.
- Press **F8** to cycle the battle scale: 80, 400, then 800 units per faction. Start at 80 to watch individual maneuvers, then raise the scale to assess crowd behavior.
- Press **F6** to toggle vision; it is useful for checking how ridges and crater rims cut sight lines.

The status line still reports Behaviac decision counts per 150 ms think batch, including flank/cover/retreat/engage/investigate/patrol counts. The purple paths are a direct visual indicator that the tree's flank intent reached the movement layer rather than merely incrementing a decision counter.

## Run and compare

Behaviac is forced on by default for every ordinary game launch, independent of stale `RIMCLONE_USE_BEHAVIAC_AI` values left in PowerShell or an IDE launch profile. The application prints the selected policy at startup. On the first AI decision, the upstream runtime also prints the exact XML path it successfully loaded; a missing runtime tree now throws an explicit file error rather than failing silently. The performance line retains the last completed think batch instead of sampling a frame where the 150 ms AI think timer did no work.

Compare in two separate runs:

```powershell
dotnet run --project App/App.csproj -c Release -- --ai=legacy
```

Close the game, then launch the default Behaviac path:

```powershell
dotnet run --project App/App.csproj -c Release
```

Use `--ai=legacy` only when intentionally comparing the old policy. An ordinary launch always selects Behaviac; the old environment variable no longer overrides that default. Deterministic simulation self-tests still explicitly use the legacy policy to preserve the existing regression baseline.

To experiment with priorities, edit `Core/AI/Behaviac/BehaviacData/rimclone_unit_decision.xml`. The `Selector` order is tactical priority; each `Sequence` contains a method-backed condition and its intent action. The content is copied to `BehaviacData/rimclone_unit_decision.xml` beside the built game.

## Validation

Run these commands from the repository root:

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-test
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-benchmark
```

The compatibility command keeps testing the upstream `demo_running` XML. Decision tests use the same XML tree asset that the live game loads and compare eleven tactical scenarios with the expected direct policy. The benchmark covers 800 agents × 100 ticks = 80,000 decisions and checks intents/checksums. Latest successful Windows CI run #1372: 0 mismatches, matching checksums, 40 B measured allocation; direct policy 30.49 ms (381 ns/decision), XML Behaviac 240.66 ms (3,008 ns/decision), 7.89× slower. Timing is informational, but the ratio is a clear optimization target before calling this production-ready.

## Honest scope boundary

The gameplay decision policy is external XML, loaded and executed by Behaviac's real runtime. The predicates and actions it calls are registered C# bindings to RimClone state. An initial Behaviac Designer workspace, tree source, metadata file and export/install script are present in `Experiments/Behaviac/DesignerWorkspace`. However, actually opening that workspace in the Designer and round-tripping an edit into the game's runtime asset has not yet been manually verified; this is a separate acceptance criterion for the editor workflow. `main` is unchanged and the PR remains a draft until the Designer round-trip, in-game comparison and performance target are checked.
