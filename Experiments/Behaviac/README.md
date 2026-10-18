# Behaviac live AI integration

RimClone uses the vendored upstream Behaviac C# runtime from Tencent/behaviac (see `Vendor/BehaviacRuntime/UPSTREAM.md` and its BSD 3-Clause license). Decision evaluation goes through Behaviac's `Agent`, `BehaviorTree`, `Selector`, `Sequence`, `Condition`, and `Action` runtime types. `Core/AI/Behaviac/BehaviacUnitDecisionTree.cs` is the current generated-style C# tree definition; it is not a tree exported from Behaviac Designer yet.

## What controls live gameplay now

On this experiment branch, interactive game runs use Behaviac by default. `UnitAiSystem` builds facts from RimClone's existing health, suppression, visibility, target-memory and tactical state; the Behaviac tree selects one of these intents:

- `Dead`: stop aiming and movement, mark the unit dead.
- `SeekCover`: prioritize available suppression cover over attacking.
- `FlankTarget`: move assigned flankers to a lateral position around the currently visible target.
- `EngageTarget`: hold a firing position and use the existing weapon/combat systems.
- `InvestigateLastKnownTarget`: pursue the remembered target location or apply the existing suppressive-fire policy.
- `Patrol`: use the existing search-waypoint behavior when there is no known target.

One in four unit slots can be a flanker. A healthy, unsuppressed flanker with a visible enemy 7–20 map units away receives a real movement goal around the target; the side is stable by unit index and a target is not repeatedly flanked after the maneuver completes. Suppression, low health, target loss, or a changed target interrupts the maneuver. Movement, pathfinding, vision, ammunition, weapons, damage and rendering remain in RimClone's systems—they are not reimplemented in the behavior tree.

This gives the branch an observable gameplay difference rather than just routing the old decisions through a wrapper.

## Run and compare

Run the Behaviac-driven AI (default on this branch):

```powershell
Remove-Item Env:RIMCLONE_USE_BEHAVIAC_AI -ErrorAction SilentlyContinue
dotnet run --project App/App.csproj -c Release
```

Run the legacy policy to compare:

```powershell
$env:RIMCLONE_USE_BEHAVIAC_AI = "0"
dotnet run --project App/App.csproj -c Release
```

The setting applies to that PowerShell process and its child processes. Deterministic simulation self-tests set it to `0` internally so this experiment does not silently rewrite the established test baseline.

## Validate the runtime and decision policy

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-test
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-benchmark
```

The first command loads and runs the upstream `demo_running` XML behavior. The decision tests compare eight deterministic input cases against the expected intents, including flank assignment and interruption priority. The benchmark measures 800 pre-created agents over 100 ticks (80,000 decisions) after warm-up; tree setup/agent creation are excluded. It reports elapsed time, time ratio, allocations, mismatches and checksums. Timing is informational; semantic mismatches fail the test.

The runtime is built with `BEHAVIAC_RELEASE` so Behaviac's node-level debug profiling/formatting does not dominate the measured loop.

## Scope boundary

This is a real Behaviac runtime integration and its selected tactics now change live movement. It is not yet a full Behaviac Designer content pipeline: the current unit tree is authored as generated-style C# under `Core/AI/Behaviac`, while the upstream XML tree is used by the compatibility smoke test. The next integration slice should move the unit tree into a Designer-authored data asset without moving pathfinding, vision, combat, or unit simulation into Behaviac.
