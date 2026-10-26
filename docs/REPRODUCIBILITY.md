# Reproducibility and dependency instructions

## Verify without scientific execution

Run `python tools/verify_package.py` from the repository root. Optionally pass
`--release /path/to/1152_DATA_CODE_RELEASE_2026-10-21.zip`. This checks bytes only and writes no data.
The repository manifest excludes itself. `.gitattributes` disables automatic
line-ending conversion so that frozen source hashes survive Git checkout.

## C# build

Install the SDK version pinned in `frozen_source/global.json` (8.0.424).
From `frozen_source`, use:

```text
dotnet restore WearVision.Reliability.R4.sln
dotnet build WearVision.Reliability.R4.sln -c Release --no-restore
```

All project references are included. NuGet restoration and the external SDK
installation are required; SDK/package binaries are not vendored here.
The test project declares MSTest 4.0.2. No fresh build or scientific execution
is claimed for the 26 October source-packaging operation.

The expanded `runtime_config/` is the retained exact T6 runtime configuration,
while `frozen_source/configs/fixtures/` holds historical QA fixtures. Do not
interchange them. Configurations include exact source/executable/configuration
and authorization bindings. Recompilation produces a new artifact and does not
replace the historical retained executable or authorize a new T6 execution.

## Statistical replay from retained evidence

Extract the complete release into a **separate** directory. In that directory:

```text
python analysis/t6_analysis_frozen.py --run-root T6_evidence --output-dir analysis_replay
```

Use Python 3.10+; no third-party statistical packages are needed. The output
directory must not contain existing outputs. This replays the frozen analysis
with 9,999 bootstrap replicates; it does not generate new scientific episodes.
Do not write replay outputs over `retained_analysis/` or the original records.

## Optional figure utilities

`analysis/requirements-figures.txt` records Pillow 12.3.0, reportlab 4.4.9 and
python-docx 1.2.0. Install these into your own environment if needed.
The Figure 3 layout-correction script requires Arial/Arial Bold on Windows;
font files are not redistributed. The correction changes label wrapping only.

```text
python publication_preparation/fix_lineage_label.py --source publication_preparation/Figure_03_Evidence_Lineage_original.svg --output-dir figures_rebuilt
```

The complete historical release remains byte-identical and preserves its
original figure layout. The repository also carries the later corrected figure.
