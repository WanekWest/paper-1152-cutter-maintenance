# Paper 1152: source code and reproducibility materials

**Risk-Informed Maintenance Decision Support for Mining Machine Cutters under Imperfect Wear Monitoring and Prognostic Uncertainty**

Ivan V. Brovchenko, Evgeny B. Mazakov, Andrey V. Gurko and Kristina V. Matrokhina.

This repository contains the retained C# implementation, configuration files,
tests, Python statistical-analysis scripts and publication table sources.
The scientific evidence is model-based and concerns one indexed cutter;
it does not establish field effectiveness or whole-machine/personnel safety.

Prepared locally on 26 October 2026. Scientific data-release version:
`2026-10-21-audit1`; suggested GitHub release tag: `1152-2026-10-21-audit1`.
Repository: https://github.com/WanekWest/paper-1152-cutter-maintenance. The complete evidence is published as a separate Release asset; no DOI is assigned.
For upload instructions in Russian, see [README_RU.md](README_RU.md).

## Contents

| Directory | Contents |
|---|---|
| `frozen_source/` | Original .NET solution, C# source, schemas, fixtures and tests, unchanged |
| `runtime_config/` | Exact T6 configuration-set archive expanded without changes |
| `analysis/` | Frozen statistical replay and consequence-weight figure script |
| `retained_analysis/` | Retained result registers used for comparison |
| `publication_tables/` | Seven editable table sources and the workbook |
| `publication_preparation/` | Figure 3 layout correction and its source files |
| `tools/verify_package.py` | Read-only SHA-256 verification |
| `docs/` | Current reproducibility instructions and historical package records |

## Dependencies and integrity

Python statistical replay requires Python 3.10+ and only its standard library.
The frozen C# projects target .NET 8 and pin SDK **8.0.424** in
`frozen_source/global.json`. The test project declares **MSTest 4.0.2**.
Install the SDK and restore NuGet dependencies separately; these installers
and caches are not bundled. Optional figure utilities have pinned dependencies
in `analysis/requirements-figures.txt` and require the stated fonts.

Check this repository without running an experiment:

```text
python tools/verify_package.py
```

Build instructions and the distinction between statistical replay and scientific
simulation are in [docs/REPRODUCIBILITY.md](docs/REPRODUCIBILITY.md).
The README within `frozen_source/` is a preserved historical document; use the
current top-level instructions for this distribution.

## Full data and retained runtime: download the Release asset

Published release (26 October 2026): https://github.com/WanekWest/paper-1152-cutter-maintenance/releases/tag/1152-2026-10-21-audit1

Direct complete-data download: https://github.com/WanekWest/paper-1152-cutter-maintenance/releases/download/1152-2026-10-21-audit1/1152_DATA_CODE_RELEASE_2026-10-21.zip

The complete retained dataset is **not** inside this small source tree.
Download `1152_DATA_CODE_RELEASE_2026-10-21.zip` from the GitHub Release, together with
`COMPLETE_RELEASE.sha256`. The archive is **162,827,486 bytes** and includes
the complete 2,496-episode evidence, eight frozen rows, retained runtime,
configuration authorities, raw records and analysis/figure sources.

SHA-256: `11b577097cd937deb221c8b837b61ce7374d812b1fc7f48b0742329f5d61af21`.

```text
python tools/verify_package.py --release /path/to/1152_DATA_CODE_RELEASE_2026-10-21.zip
```

GitHub's automatically generated Source code ZIP/TAR contains the repository
tree only. Download the separately attached full data/code asset for complete
reproduction. After publishing the release, record its actual URL and version
in the manuscript and data statements. Do not substitute a guessed URL.

## Citation and rights

`CITATION.cff` contains the authors, title and scientific version. Add the actual
release URL or DOI only after publication/registration. Rights and licence
status are retained in [RIGHTS_AND_LICENSES.md](RIGHTS_AND_LICENSES.md);
this packaging operation does not grant a new software or data licence.
No new simulation or post-hoc refitting was performed to prepare this archive.
