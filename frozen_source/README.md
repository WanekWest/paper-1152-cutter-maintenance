# WearVision Reliability R4 — ER-05A

This workspace contains the clean-room `.NET 8` implementation, T0–T4 semantic-QA harness, and ER-05A runtime-capability addendum for manuscript 1152. It is scientific-software infrastructure, not a manuscript, calibrated field model, pilot result, or scientific experiment result.

The solution contains exactly five projects: Domain, Application, Infrastructure, Experiments, and Tests. Dependencies point from Domain through Application and Infrastructure to Experiments; deployable projects do not reference Experiments or any legacy `WearVision.SyntheticStand.*` assembly. ER-05A adds V2 configuration binding, a bounded two-stage evaluator, typed B0–B7 baseline binding, T5-only planning schemas/harness, and gated CLI commands without changing the ER-03 mathematics.

Build and test from this directory with the workspace-local SDK:

```powershell
.\.dotnet\dotnet.exe restore .\WearVision.Reliability.R4.sln --packages .\.nuget\packages
.\.dotnet\dotnet.exe build .\WearVision.Reliability.R4.sln -c Release --no-restore
.\.dotnet\dotnet.exe test .\WearVision.Reliability.R4.sln -c Release --no-build
```

The V1 fixture configuration set under `configs/fixtures` remains hash-verified and explicitly `TEST_FIXTURE_ONLY`. Its numerical constants exist only to test algorithms. V2 is documented in `docs/ER05A_RUNTIME_CONFIGURATION_V2.md`; the manifest schema and field reconciliation are under `schemas`. Scientific inputs—including the functional limit state, exposure clock, horizon, chance constraint, calibrated posterior/transition/observation/reset models, durations, consequences, weights, and pilot precision inputs—remain owner-controlled placeholders.

The command surface is deliberately narrow:

```powershell
dotnet WearVision.Reliability.Experiments.dll config validate <config-set-directory>
dotnet WearVision.Reliability.Experiments.dll preflight <config-set-directory>
dotnet WearVision.Reliability.Experiments.dll t5 pilot <config-set-directory>
dotnet WearVision.Reliability.Experiments.dll t6
```

`config validate` performs schema/hash/runtime binding only. `preflight` adds owner, resource, and exact hash-bound grant gates. `t5 pilot` cannot call its execution delegate until every gate passes; no bypass flag or environment override exists. `t6` is always hard-blocked in ER-05A.

The `Experiments.Oracle` namespace holds current-state oracle truth behind a compile-time project boundary. Domain, Application, and Infrastructure expose no oracle DTO or truth field. Raw oracle evidence uses `SCH-ORC`, separate from deployable records.

ER-05A can statically load and bind a `T5Pilot` package without authorizing it. The regenerated candidate remains `DRAFT / OWNER APPROVAL REQUIRED / NOT AUTHORIZED`; ER-05A does not execute T5. T6 is hard-blocked. No manuscript file is created or edited. `E:\WearVision\` was used only for read-only provenance/hash checks.
