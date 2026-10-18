# Behaviac compatibility probe

This is stage 1 of evaluating Behaviac for RimClone. The game's current AI remains unchanged.

The test compiles the vendored C# runtime against .NET 10, loads Behaviac's upstream `demo_running` behavior tree from XML, executes it, and verifies the expected condition/action sequence.

## Run locally

From the repository root:

```powershell
dotnet run --project Experiments/Behaviac/Behaviac.Probe.csproj -c Release
```

Expected output: `Behaviac smoke test: PASS`.

The upstream C# runtime and original license notices are in `Vendor/BehaviacRuntime`. This is a compatibility test, not a benchmark; activation in the real unit AI will be a separate stage.
