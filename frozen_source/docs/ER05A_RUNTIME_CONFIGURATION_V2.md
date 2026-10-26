# ER-05A runtime configuration V2

V2 is an explicit T5-capable configuration schema. It does not authorize execution and it does not silently migrate a V1 fixture. `ConfigurationSetLoaderV2` validates every registered file hash, rejects unregistered package content, distinguishes `TestFixtureOnly`, `T5Pilot`, and `T6Scientific`, and constructs separate effective configurations for every experiment row.

The package root contains one `config_set_manifest.json`. Its `Files` entries bind each immutable file by relative path, SHA-256, and role. Required singleton roles are model, reliability, actions, consequences, numerical, baselines, output-policy, compatibility, split, retirement, owner-approval, resources, and experiment-set. Every `effective-experiment` role is also referenced by `experiment_set_manifest.json` with its exact hash.

Model binding is versioned through registered factory IDs. The ER-05A scalar methodological reference supports an affine limit state, a normalized weighted-support belief, an exposure-scaled uniform rate transition, imperfect binary observation kernels with explicit missingness, and a nondegenerate two-point reset. The artifact hash is calculated from the exact factory payload. No candidate numerical value is stored in C# source.

The exposure transition declares `UNIFORM_RATE_PER_SEGMENT_TIMES_DELTA_EXPOSURE`: one uniform rate is drawn for a segment and multiplied by nonnegative normalized exposure. Paused inspection supplies zero physical exposure while retaining the configured elapsed action duration; continued-operation inspection supplies its configured exposure. This is a methodological runtime semantics for the frozen abstract kernel `K_a(Delta)`, not a calibrated degradation law.

The hash-bound authorization artifact is `t5_authorization_manifest.json`. An approved T5 grant must match the config-set manifest hash, both authorized experiment IDs and effective-config hashes, protocol, T5 stage, output-policy hash, approver, state, and optional expiry. Draft, revoked, absent, expired, mismatched, or partial grants fail closed. A grant cannot approve owner values. T6 remains blocked regardless of any grant.

The candidate administration script `tools/Generate-Er05ACandidateV2.ps1` reads the ER-06A V1 draft as data, emits explicit V2 artifacts, calls only `config validate` and `preflight`, and requires the expected owner/resource/draft-authorization blocker set. It never invokes `t5 pilot`.

T5 planning records use dedicated schema IDs and always carry `T5Only=true`, `EligibleForT6=false`, and `EligibleForManuscript=false`. A cancelled or storage-limited run remains incomplete and receives no completion manifest.
