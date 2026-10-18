# Behaviac integration experiment

The production `UnitAiSystem` remains unchanged. This experiment evaluates Behaviac in isolation before any live AI switch.

## Stage 1 — runtime compatibility

The test compiles the vendored C# runtime on .NET 10, loads Behaviac's upstream `demo_running` XML tree, runs it, and checks the expected condition/action sequence.

Run from the repository root:

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release
```

## Stage 2 — unit-intent adapter

`Core/AI/Behaviac` contains an opt-in runtime host and a minimal facts-in/intent-out decision agent. The behavior tree only chooses an intent (`Dead`, `SeekCover`, `EngageTarget`, `InvestigateLastKnownTarget`, or `Patrol`). Movement, vision, combat, health and rendering remain under existing RimClone systems.

## Stage 3 — deterministic checks and benchmark

Run the six deterministic scenarios:

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-test
```

Measure the decision layer with 800 pre-created agents and 100 ticks (80,000 decisions), after a short warm-up:

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release -- --decision-benchmark
```

The benchmark compares a direct decision policy against the Behaviac tree and reports elapsed time, nanoseconds per decision, time ratio, bytes allocated by the Behaviac pass, and semantic mismatches. Timing is informational rather than a hard pass/fail threshold because CI machine load varies; any incorrect intent or non-success status fails the test. Agent creation and initial tree loading are excluded from the measured loop.

The runtime is built with `BEHAVIAC_RELEASE` so node-level debug profiling and formatting do not pollute gameplay timings or per-decision allocations.

## Stage 4 — opt-in live integration

`UnitAiSystem` now routes high-level intent selection through the compiled Behaviac tree when enabled. The selected intent controls the existing branches for death, suppression cover, visible-target engagement, last-known-target investigation, and patrol fallback. Movement, pathfinding, vision, weapon handling, damage and rendering remain in their existing systems.

The feature remains off by default. Enable it for a local game run in PowerShell:

```powershell
$env:RIMCLONE_USE_BEHAVIAC_AI = "1"
dotnet run --project App/App.csproj -c Release
```

Remove the environment variable or set it to `0` to use the original AI path. Code can also toggle `UnitAiSystem.UseBehaviacDecisionLayer` directly. Agent instances are cached by unit slot and reused; the tree is not loaded per decision. The first opt-in update pays one-time initialization and agent-creation costs.

The next CI benchmark validates the post-optimization allocation and timing numbers. In-game FPS remains a separate measurement and is not inferred from the decision microbenchmark.
