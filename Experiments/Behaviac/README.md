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

Approximately one quarter of unit slots can receive the flanker role, selected deterministically. Flanking is attempted for a healthy, unsuppressed unit with a visible hostile target 7–20 map units away; the unit receives an actual movement goal and uses existing RimClone movement/pathfinding. The goal is stable per target, tries alternate sides around blocked tiles, and is interrupted by suppression, low health or target loss. A completed maneuver transitions into the existing combat behavior rather than repeatedly oscillating.

The unit card reports localized AI states, including **Фланг**, so the new behavior is visible when selecting a unit.

## Run and compare

Interactive runs use the Behaviac AI by default on this experiment branch:

```powershell
Remove-Item Env:RIMCLONE_USE_BEHAVIAC_AI -ErrorAction SilentlyContinue
dotnet run --project App/App.csproj -c Release
```

To compare the old AI:

```powershell
$env:RIMCLONE_USE_BEHAVIAC_AI = "0"
dotnet run --project App/App.csproj -c Release
```

The environment variable applies only to the current PowerShell process and child processes. Deterministic simulation self-tests deliberately set it to `0` internally to preserve the existing regression baseline.

To experiment with priorities, edit `Core/AI/Behaviac/BehaviacData/rimclone_unit_decision.xml`. The `Selector` order is tactical priority; each `Sequence` contains a method-backed condition and its intent action. The content is copied to `BehaviacData/rimclone_unit_decision.xml` beside the built game.

## Validation

Run these commands from the repository root:

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-test
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-benchmark
```

The compatibility command keeps testing the upstream `demo_running` XML. Decision tests use the same XML tree asset that the live game loads and compare eleven tactical scenarios with the expected direct policy. The benchmark covers 800 agents × 100 ticks = 80,000 decisions and checks intents/checksums; timing is informational because the Behaviac node traversal costs more than simple direct conditions.

## Honest scope boundary

The gameplay decision policy is external XML, loaded and executed by Behaviac's real runtime. The predicates and actions it calls are registered C# bindings to RimClone state. This branch does not yet include a packaged Behaviac Designer workspace/project with a generated editor metadata pipeline and hot-reload workflow; for now the XML is directly editable and the game's runtime reads it at startup. `main` is unchanged and the PR remains a draft until latest CI and an in-game comparison are checked.
