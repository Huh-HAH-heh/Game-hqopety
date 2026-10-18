# Behaviac compatibility probe

This is stage 1 of evaluating Behaviac for RimClone. It does not alter or replace the game's current AI.

The probe compiles the C# runtime from the repository's archived `behaviac-master.zip` against .NET 10, loads the upstream `demo_running` behavior tree from XML, executes it, and checks that the expected condition/action sequence completes.

## Run locally (PowerShell)

From the repository root:

```powershell
$archive = "Интересные фреймворки/behaviac-master.zip"
$destination = "Experiments/Behaviac/.work"
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
$sourceRoot = (Resolve-Path "$destination/behaviac-master").Path
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release "-p:BehaviacSourceRoot=$sourceRoot"
```

Expected output contains `Behaviac smoke test: PASS`. Any build or runtime failure is a blocker to further integration and should be fixed before adding an adapter to the in-game AI.

The upstream runtime source is compiled directly for this experiment; it is not copied into RimClone's production projects. Preserve the upstream Behaviac license and third-party license notices when deciding on distribution.
