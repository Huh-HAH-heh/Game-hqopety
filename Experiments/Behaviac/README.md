# Behaviac tactical AI integration for RimClone

RimClone uses the vendored upstream Behaviac C# runtime from Tencent/behaviac (see `Vendor/BehaviacRuntime/UPSTREAM.md` and its BSD 3-Clause license). The live decision policy is now loaded from an external Behaviac XML asset at `Core/AI/Behaviac/BehaviacData/rimclone_unit_decision.xml`; the game no longer selects its policy from a C#-constructed tree. Behaviac's own runtime `Workspace`, `Agent`, `Selector`, `Sequence`, `Condition` and `Action` classes load and execute the XML.

RimClone-specific facts and action bindings are registered in `BehaviacRuntimeHost`. They connect decisions to the existing game simulation; they do not reimplement movement, pathfinding, vision, weapons, ammo, damage or rendering.

## Live intents in the XML tree

The selector tests branches in priority order:

1. `Dead` — stop aiming/movement and mark the unit dead.
2. `SeekCover` — suppression cover overrides offensive action.
3. `RetreatToCover` — when health is below the existing threshold, use the current cover search before normal engagement.
4. `FlankTarget` — healthy, unsuppressed flankers move to a lateral waypoint around a visible enemy.
5. `EngageTarget` — hold a firing position and use the existing weapon/combat systems.
6. `SuppressiveFire` — when the target is no longer visible, use the existing suppression-fire path if the unit has a compatible weapon.
7. `InvestigateLastKnownTarget` — otherwise investigate the last known target position.
8. `Patrol` — continue the game's existing search-waypoint behavior if there is no known target.

Approximately one quarter of unit slots can receive the flanker role, selected deterministically. Flanking can start from 16 map units out to the lesser of 75 units or 85% of the unit's vision range; the previous 7–20 window activated too late in the mass-combat scenario. The goal is a lateral supporting-fire position rather than point-blank proximity, is stable per target, tries alternate sides around blocked tiles, and is interrupted by suppression, low health or target loss. A completed maneuver transitions into the existing combat behavior rather than repeatedly oscillating.

The unit card reports localized AI states, including **Фланг**, so the new behavior is visible when selecting a unit.

## Run and compare

Interactive runs use the Behaviac AI by default on this experiment branch. The mode is printed at startup, and the performance line keeps the last actual decision batch rather than sampling a frame where the 150 ms AI think timer did no work.

Compare in two separate runs:

```powershell
dotnet run --project App/App.csproj -c Release -- --ai=legacy
```

Close the game, then run:

```powershell
dotnet run --project App/App.csproj -c Release -- --ai=behaviac
```

The `--ai=legacy` and `--ai=behaviac` arguments override the environment variable for that process. You can also still use `RIMCLONE_USE_BEHAVIAC_AI=0` or `1`; do not set both values before launching and treat them as two tests, because only the final environment value is used. Deterministic simulation self-tests deliberately set it to `0` internally to preserve the existing regression baseline.

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
