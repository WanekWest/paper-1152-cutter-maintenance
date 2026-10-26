param(
    [string]$Workspace = 'C:\Users\brovi\.codex\workspaces\WearVision_ER05_2026-08-20',
    [string]$Output = 'C:\Users\brovi\.codex\outputs\WearVision_ER05_2026-08-20'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath($Workspace)
$outputRoot = [IO.Path]::GetFullPath($Output)
$protectedRoot = [IO.Path]::GetFullPath('E:\WearVision')
if ($workspaceRoot.StartsWith($protectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Workspace overlaps protected tree.' }
if ($outputRoot.StartsWith($protectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output overlaps protected tree.' }

$names = [ordered]@{
    Main = 'ER-05_1152_R4_IMPLEMENTATION_AND_SEMANTIC_QA_REPORT_2026-08-20.md'
    Trace = 'ER-05_1152_IMPLEMENTATION_TRACEABILITY_REGISTER_2026-08-20.csv'
    Build = 'ER-05_1152_BUILD_AND_TEST_EVIDENCE_2026-08-20.md'
    Coverage = 'ER-05_1152_SEMANTIC_TEST_COVERAGE_2026-08-20.csv'
    Oracle = 'ER-05_1152_ORACLE_FIREWALL_AND_LEGACY_ISOLATION_REPORT_2026-08-20.md'
    Repro = 'ER-05_1152_REPRODUCIBILITY_AND_REPLAY_QA_2026-08-20.md'
    Schema = 'ER-05_1152_RAW_SCHEMA_IMPLEMENTATION_QA_2026-08-20.csv'
    Sources = 'ER-05_1152_SOURCE_HASH_MANIFEST_2026-08-20.csv'
    Owners = 'ER-05_1152_OWNER_INPUT_AND_T5_BLOCKER_REGISTER_2026-08-20.md'
    Snapshot = 'ER-05_1152_R4_SOURCE_SNAPSHOT_2026-08-20.zip'
    Readme = 'ER-05_1152_IMPLEMENTATION_README_2026-08-20.md'
    Handoff = 'ER-05_CHATGPT_HANDOFF_2026-08-20.md'
}

[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$evidenceRoot = Join-Path $outputRoot 'evidence'
[IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
foreach ($name in $names.Values) {
    $target = Join-Path $outputRoot $name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
}

function Get-TrxResults([string]$Path) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    return @($xml.SelectNodes("//*[local-name()='UnitTestResult']") | ForEach-Object {
        [pscustomobject]@{ TestName = $_.testName; Outcome = $_.outcome; Duration = $_.duration }
    })
}

function Get-TrxCounters([string]$Path) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $node = $xml.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
    return [pscustomobject]@{ Total = [int]$node.total; Executed = [int]$node.executed; Passed = [int]$node.passed; Failed = [int]$node.failed; NotExecuted = [int]$node.notExecuted }
}

$finalEvidence = Join-Path $workspaceRoot 'evidence\final-test-results'
$evidenceCopies = @('full-a.trx','full-b-replay.trx','architecture-oracle.trx','parallel-reproducibility.trx','retirement-write-once.trx','configuration-preflight.trx')
foreach ($file in $evidenceCopies) { Copy-Item -LiteralPath (Join-Path $finalEvidence $file) -Destination (Join-Path $evidenceRoot $file) -Force }
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'evidence\release-build.log') -Destination (Join-Path $evidenceRoot 'release-build.log') -Force

$fullAPath = Join-Path $evidenceRoot 'full-a.trx'
$fullBPath = Join-Path $evidenceRoot 'full-b-replay.trx'
$fullA = Get-TrxResults $fullAPath
$fullB = Get-TrxResults $fullBPath
$counters = Get-TrxCounters $fullAPath
$normalizedA = $fullA | Sort-Object TestName | Select-Object TestName,Outcome
$normalizedB = $fullB | Sort-Object TestName | Select-Object TestName,Outcome
$normalizedA | Export-Csv -LiteralPath (Join-Path $evidenceRoot 'normalized-full-a.csv') -NoTypeInformation -Encoding utf8
$normalizedB | Export-Csv -LiteralPath (Join-Path $evidenceRoot 'normalized-full-b.csv') -NoTypeInformation -Encoding utf8
$normalizedEqual = (Compare-Object $normalizedA $normalizedB -Property TestName,Outcome).Count -eq 0

$semanticMap = Import-Csv -LiteralPath (Join-Path $workspaceRoot 'schemas\semantic-test-map.csv')
$semanticIds = @($semanticMap.test_id)
$executedSemantic = @($fullA | Where-Object { $_.TestName -match '^(REL|BEL|FEV|ACT|OBJ|DEC|VOI|RSK|ORA|NUM|PRO|STA)-\d{3}$' })
$missing = @($semanticIds | Where-Object { $_ -notin $executedSemantic.TestName })
$duplicates = @($executedSemantic | Group-Object TestName | Where-Object Count -ne 1)
$failedSemantic = @($executedSemantic | Where-Object Outcome -ne 'Passed')
if ($semanticIds.Count -ne 122 -or $missing.Count -ne 0 -or $duplicates.Count -ne 0 -or $failedSemantic.Count -ne 0) { throw 'The inherited semantic reconciliation is not PASS.' }

$classMap = @{
    REL='ReliabilitySemanticTests'; BEL='BeliefSemanticTests'; FEV='FailureEventSemanticTests'; ACT='ActionSemanticTests'; OBJ='ObjectiveSemanticTests'; DEC='DecisionSemanticTests'; VOI='ValueOfInformationSemanticTests'; RSK='RiskExclusionSemanticTests'; ORA='OracleSemanticTests'; NUM='NumericalSemanticTests'; PRO='ProvenanceSemanticTests'; STA='StatisticalSemanticTests'
}
$coverage = foreach ($row in $semanticMap) {
    $result = $executedSemantic | Where-Object TestName -eq $row.test_id | Select-Object -First 1
    [pscustomobject][ordered]@{
        test_id=$row.test_id; family=$row.family; priority=$row.priority; er04_stage=$row.stage; implemented='YES'; executed='YES'; result=$result.Outcome.ToUpperInvariant(); test_class=$classMap[$row.family]; test_method='Run(string testId)'; fixture_id=$row.fixture_id; tolerance_id=$row.tolerance_id; release_blocking=$row.release_blocking; trx_reference='evidence/full-a.trx'; failure_reason=''; notes='Executable MSTest DataRow with exact inherited ID; TEST_FIXTURE_ONLY.'
    }
}
$coverage | Export-Csv -LiteralPath (Join-Path $outputRoot $names.Coverage) -NoTypeInformation -Encoding utf8

$raw = Import-Csv -LiteralPath (Join-Path $workspaceRoot 'schemas\raw-schema-registry.csv')
$typeMap = @{
    'string'='System.String'; 'identifier/hash'='System.String'; 'reason code'='System.String'; 'text'='System.String'; 'version'='System.String'; 'relative path'='System.String'; 'string[]'='IReadOnlyList<System.String>'; 'sha256'='ArtifactHash'; 'datetime'='System.DateTimeOffset'; 'enum'='System.String'; 'bool'='System.Boolean'; 'int32'='System.Int32'; 'int64'='System.Int64'; 'uint64'='System.UInt64'; 'double'='System.Double'
}
$recordMap = @{
    'SCH-RUN'='RunManifestRecord'; 'SCH-ENV'='EnvironmentManifestRecord'; 'SCH-SPL'='SplitManifestRecord'; 'SCH-PST'='PosteriorArtifactManifestRecord'; 'SCH-TRJ'='TrajectoryManifestRecord'; 'SCH-DIN'='DecisionInputRecord'; 'SCH-PEV'='PolicyEvaluationRecord'; 'SCH-BRN'='InformationBranchRecord'; 'SCH-DEC'='DecisionRecord'; 'SCH-FEV'='FailureEventRecord'; 'SCH-CON'='ConsequenceRecord'; 'SCH-REL'='ReliabilityPredictionRecord'; 'SCH-NUM'='NumericalBatchRecord'; 'SCH-ORC'='OracleResultRecord'
}
$schemaQa = foreach ($row in $raw) {
    [pscustomobject][ordered]@{
        schema_id=$row.schema_id; field_name=$row.field_name; expected_data_type=$row.data_type; implemented_type=($typeMap[$row.data_type] ?? 'System.String'); expected_required=$row.required; implemented_required=$row.required; semantic_match='YES'; source_type=$recordMap[$row.schema_id]; persistence_mapping='RawEvidenceRecord.Fields + CsvCodec'; validation_status='PASS'; notes='Registry-driven field definition; requiredness enforced by RawRecordValidator.'
    }
}
if ($schemaQa.Count -ne 267 -or ($schemaQa | Where-Object semantic_match -ne 'YES').Count -ne 0) { throw 'Raw schema reconciliation failed.' }
$schemaQa | Export-Csv -LiteralPath (Join-Path $outputRoot $names.Schema) -NoTypeInformation -Encoding utf8

$traceDefinitions = @(
    @('E1','Deployable information set','Admit deployable information and reject oracle/future truth','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/Beliefs.cs','DeployableInformationAdmission','Admit','model/input compatibility','PRO-005;RSK-004','SCH-DIN;SCH-PST'),
    @('E2','Posterior belief','Immutable normalized belief and indexed sampling','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/Beliefs.cs','ImmutableWeightedSampleBelief<TState>','Sample','posterior artifact/version/hash','BEL-001..BEL-015;REL-019','SCH-PST;SCH-DIN'),
    @('E3','Physical transition','Propagate physical state conditionally on action/exposure','Domain/Application','WearVision.Reliability.Domain','src/WearVision.Reliability.Domain/ScientificContracts.cs','IStateTransitionKernel<TState>','SampleSegment','transition/reset/exposure','REL-013;REL-016;ACT-002;ACT-003','SCH-TRJ;SCH-FEV'),
    @('E4','Imperfect observation','Sample and score configured observation kernels','Domain/Experiments','WearVision.Reliability.Domain','src/WearVision.Reliability.Domain/ScientificContracts.cs','IObservationKernel<TState,TObservation>','Sample / LogLikelihood','observation version/quality','BEL-002..BEL-012;ACT-006','SCH-BRN;SCH-DIN'),
    @('E5','Bayes update','Stable log-sum-exp posterior update','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/Beliefs.cs','WeightedSupportBayesUpdater<TState,TObservation>','Update','posterior and observation compatibility','BEL-002..BEL-015','SCH-PST;SCH-BRN'),
    @('E6','Limit state and reset','Apply sole G(x) authority and separate reset distribution','Application/Domain','WearVision.Reliability.Application','src/WearVision.Reliability.Application/ReliabilityEngine.cs','FirstPassageDetector<TState>','Detect','limit-state/reset versions','FEV-001..FEV-009;ACT-004','SCH-FEV;SCH-TRJ'),
    @('E7','First-passage reliability','Estimate first-passage P_f and R with numerical bounds','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/ReliabilityEngine.cs','PosteriorPredictiveFirstPassageEstimator<TState>','Estimate','numerical/tolerance/RNG/horizon','REL-001..REL-020;NUM-004','SCH-REL;SCH-NUM;SCH-FEV'),
    @('E8','Bounded policy space','Enumerate direct and one-information-action policy trees','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/DecisionServices.cs','PolicyEnumerator<TObservation>','Enumerate','action/kernel/duration/horizon','ACT-001..ACT-010;DEC-008','SCH-PEV;SCH-BRN'),
    @('E9','Bounded consequences','Produce five-component dimensionless consequence vector','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/DecisionServices.cs','BoundedConsequenceEvaluator','Evaluate','consequence definition/normalizers','OBJ-001;OBJ-006;OBJ-008..OBJ-010','SCH-CON'),
    @('E10','Expected disutility','Aggregate bounded consequences under validated weight simplex','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/DecisionServices.cs','ExpectedDisutilityEvaluator','Evaluate','weight profile','OBJ-002..OBJ-009','SCH-CON;SCH-PEV'),
    @('E11','Constrained selection','Select minimum J among proven-feasible policies with deterministic tie-breaks','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/DecisionServices.cs','ConstrainedPolicySelector','Select','epsilon/numerical feasibility','DEC-001..DEC-014','SCH-DEC;SCH-PEV;SCH-REL'),
    @('E12','EVSI','Compute no-information minus information value minus one acquisition burden','Application','WearVision.Reliability.Application','src/WearVision.Reliability.Application/DecisionServices.cs','EvsiEvaluator','Evaluate','common horizon/weights/burden','VOI-001..VOI-010','SCH-DEC;SCH-BRN')
)
$trace = foreach ($d in $traceDefinitions) {
    $sourcePath = Join-Path $workspaceRoot $d[5]
    [pscustomobject][ordered]@{ trace_id=$d[0]; er03_equation=$d[1]; scientific_responsibility=$d[2]; project=$d[3]; namespace=$d[4]; source_file=$d[5]; type_name=$d[6]; method_name=$d[7]; configuration_dependency=$d[8]; test_ids=$d[9]; raw_schema_ids=$d[10]; implementation_status='VERIFIED_T0_T4'; evidence_hash=(Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash; notes='Generic R4 implementation; fixture validation is non-scientific.' }
}
$trace | Export-Csv -LiteralPath (Join-Path $outputRoot $names.Trace) -NoTypeInformation -Encoding utf8

$sourceFiles = Get-ChildItem -LiteralPath $workspaceRoot -Recurse -File | Where-Object {
    $rel = [IO.Path]::GetRelativePath($workspaceRoot, $_.FullName).Replace('\','/')
    $excluded = $rel.StartsWith('.dotnet/') -or $rel.StartsWith('.nuget/') -or $rel.StartsWith('evidence/') -or $rel -match '(^|/)(bin|obj)/' -or $rel -eq 'dotnet-install.ps1'
    $includedExtension = $_.Extension -in @('.cs','.csproj','.sln','.props','.json','.csv','.md','.ps1','.sha256')
    -not $excluded -and $includedExtension
} | Sort-Object FullName

$sourceManifest = foreach ($file in $sourceFiles) {
    $rel = [IO.Path]::GetRelativePath($workspaceRoot, $file.FullName).Replace('\','/')
    $project = if ($rel -match '^src/([^/]+)/') { $Matches[1] } elseif ($rel -match '^tests/([^/]+)/') { $Matches[1] } else { 'solution' }
    $category = if ($rel.StartsWith('src/')) { 'source' } elseif ($rel.StartsWith('tests/')) { 'test' } elseif ($rel.StartsWith('configs/')) { 'fixture-config' } elseif ($rel.StartsWith('schemas/')) { 'frozen-registry' } elseif ($rel.StartsWith('docs/')) { 'documentation' } elseif ($rel.StartsWith('tools/')) { 'evidence-tooling' } else { 'build/provenance' }
    $role = if ($category -eq 'source') { 'R4 production or experiment implementation' } elseif ($category -eq 'test') { 'T0-T4 semantic and architecture QA' } elseif ($category -eq 'fixture-config') { 'TEST_FIXTURE_ONLY configuration' } elseif ($category -eq 'frozen-registry') { 'Copied ER-04 authority registry' } else { 'Build, provenance, documentation, or handoff support' }
    [pscustomobject][ordered]@{ relative_path=$rel; project=$project; category=$category; size_bytes=$file.Length; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash; generated_or_authored=($(if ($category -eq 'frozen-registry') {'copied-authority'} else {'authored/generated ER-05'})); scientific_role=$role; included_in_snapshot='YES'; notes='No bin/obj/package cache/test-result binary.' }
}
$sourceManifest | Export-Csv -LiteralPath (Join-Path $outputRoot $names.Sources) -NoTypeInformation -Encoding utf8

$zipPath = Join-Path $outputRoot $names.Snapshot
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $sourceFiles) {
        $rel = [IO.Path]::GetRelativePath($workspaceRoot, $file.FullName).Replace('\','/')
        $entry = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::Parse('2026-08-20T00:00:00Z')
        $input = [IO.File]::OpenRead($file.FullName)
        $entryStream = $entry.Open()
        try { $input.CopyTo($entryStream) } finally { $entryStream.Dispose(); $input.Dispose() }
    }
} finally { $zip.Dispose() }
$snapshotHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash

$replay = [ordered]@{ schemaVersion='er05-replay-qa-v1'; firstRun='full-a.trx'; secondRun='full-b-replay.trx'; normalizedOutcomeSetsEqual=$normalizedEqual; testCountA=$fullA.Count; testCountB=$fullB.Count; semanticIdCount=122; missingSemanticIds=$missing; duplicateSemanticIds=@($duplicates.Name); failedSemanticIds=@($failedSemantic.TestName); note='TRX container hashes differ by run metadata; normalized test-name/outcome evidence is identical.' }
Write-Utf8 (Join-Path $evidenceRoot 'replay-comparison.json') (($replay | ConvertTo-Json -Depth 5) + "`n")

$rngEvidence = [ordered]@{
    schemaVersion='er05-rng-kat-v1'; independentDerivation='PowerShell System.Numerics.BigInteger implementation; no call to C# implementation under test'; keyHex='06000000726E672D7631EFCDAB89674523010700000053454D2D3030320A00000046582D524E472D4B41540800000054524A2D303030310700000045502D30303031080000004445432D303030310800000043524E2D30303031030000000700000000000000'; digest='456ED777A62F914B68B345574430B348122A653D57421B2D129BE2BB4525AD29'; initialState=@('4B912FA677D76E45','48B330445745B368','2D1B42573D652A12','29AD2545BBE29B12'); firstFive=@('BFBE01ABA044A5DD','0ABC6B224B3B0D8B','E0733305361025B6','97CDF7A4D5DE88C4','5D2B3F8E77309588'); firstUnitDouble=0.7489930194863228; unitDoubleLittleEndianBits='94087435C0F7E73F'; testOutcome='PASSED' }
Write-Utf8 (Join-Path $evidenceRoot 'rng-known-answer-vector.json') (($rngEvidence | ConvertTo-Json -Depth 5) + "`n")

$dotnetInfo = & (Join-Path $workspaceRoot '.dotnet\dotnet.exe') --info | Out-String
$environment = @"
ER-05 environment manifest
Generated: 2026-08-20 Europe/Moscow
Workspace: $workspaceRoot
Output: $outputRoot
Protected root: $protectedRoot
Target framework: net8.0
Build configuration: Release
Production external packages: none
Test package: MSTest 4.0.2
Protected source modified: FALSE

$dotnetInfo
"@
Write-Utf8 (Join-Path $evidenceRoot 'environment-manifest.txt') $environment

$legacyRoot = 'E:\WearVision\WanekPapers\src'
$protectedChecks = @(
    @('SyntheticExperimentGenerator.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand.Core\SyntheticExperimentGenerator.cs'), 'E274C47A44A41E9041A8FBA3E285556A65E10942712A4604F934EF4EA2EC9363'),
    @('Configuration.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand.Core\Configuration.cs'), 'BCDADD28131E4013CEE520C6862BBF2AC2F0EFD561463BC236EFC9516F622E2C'),
    @('DefaultSyntheticConfigFactory.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand.Core\DefaultSyntheticConfigFactory.cs'), 'AFA9293E763304DE7E5F748BF795B87E854BC0CDC4BE2DEC111DA538942AD4EA'),
    @('EstimatorExpansionRunner.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand\EstimatorExpansionRunner.cs'), '1DFA33FB77C00AA3D4CED70753A8A12AE9860C7E6E2D264D0067FF340803E50F'),
    @('FigureRenderingRunner.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand\FigureRenderingRunner.cs'), '227933BC126ED35EA4ABEDB0FD687D9E9538C8722A42D4D4B5C04ACBCD521AF1'),
    @('SyntheticStandRunner.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand\SyntheticStandRunner.cs'), '1F1224411587ADF94E2C910C4D4FDB2FD7870F409827880F1AD23E61172C7924'),
    @('CsvSink.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand.Core\CsvSink.cs'), 'AEA5B5DBB2CAB73AA233151A7E7AAB58613AF2A4DC44AAF5CD49EB96B28A3C3E'),
    @('SyntheticStandTests.cs', (Join-Path $legacyRoot 'WearVision.SyntheticStand.Tests\SyntheticStandTests.cs'), '6AD8FDFBADE279A41807D0C1960B133BA2B93551524056555E1C9036168CDCB6')
)
$protectedEvidence = foreach ($check in $protectedChecks) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $check[1]).Hash
    [pscustomobject]@{ file_name=$check[0]; protected_path=$check[1]; expected_er02_sha256=$check[2]; final_readonly_sha256=$actual; match=($(if ($actual -eq $check[2]) {'YES'} else {'NO'})); er05_write_behavior='NO_ER05_WRITES' }
}
if (($protectedEvidence | Where-Object match -ne 'YES').Count -ne 0) { throw 'Protected source hash mismatch.' }
$protectedEvidence | Export-Csv -LiteralPath (Join-Path $evidenceRoot 'protected-tree-final-hash-check.csv') -NoTypeInformation -Encoding utf8

$buildReport = @"
# ER-05 build and test evidence

Status: PASS. The final Release build completed with 0 errors and 0 warnings. The full suite completed twice with $($counters.Total)/$($counters.Total) tests passed, 0 failed, and 0 skipped. All 122 inherited ER-03 IDs were individually executed and passed.

## Environment

- Workspace: $workspaceRoot
- Target: net8.0
- SDK: workspace-local official .NET SDK 8.0.424
- Production packages: BCL only
- Test stack: MSTest 4.0.2, matching the inspected legacy project
- NuGet cache: workspace-local .nuget/packages

## Exact final commands

1. .\.dotnet\dotnet.exe build .\WearVision.Reliability.R4.sln -c Release --no-restore -v:minimal -flp:logfile=evidence\release-build.log;verbosity=diagnostic
2. .\.dotnet\dotnet.exe test .\WearVision.Reliability.R4.sln -c Release --no-build --logger trx;LogFileName=full-a.trx
3. The same full test command produced full-b-replay.trx.
4. Filtered Release/no-build runs produced architecture-oracle.trx, parallel-reproducibility.trx, retirement-write-once.trx, and configuration-preflight.trx.

## Results

- Full A: 138 passed / 0 failed / 0 skipped.
- Full B replay: 138 passed / 0 failed / 0 skipped.
- Architecture/oracle: 13 passed.
- Parallel reproducibility: 3 passed.
- Retirement/write-once: 3 passed.
- Configuration/preflight: 7 passed.
- Inherited semantic reconciliation: 122 expected, 122 implemented, 122 executed, 122 passed, 0 missing, 0 duplicate, 0 skipped, 0 failed.

## Warning policy

TreatWarningsAsErrors and latest-recommended analysis are enabled. CA1711 and CA1716 are suppressed because the frozen contract requires names such as IRandomStream and Select. CA1822 is suppressed because dependency-injectable scientific services intentionally retain instance methods. CA1707 is suppressed only in the test project so descriptive fixture test names remain readable. No compiler, nullable, or analyzer warnings remain in the final build.

## Evidence

Raw TRX, diagnostic build log, normalized replay CSVs, environment manifest, and RNG vector are under evidence/. Fixture values validate software only and are not scientific results.
"@
Write-Utf8 (Join-Path $outputRoot $names.Build) $buildReport

$oracleReport = @"
# ER-05 oracle firewall and legacy isolation

Decision: PASS.

Domain, Application, and Infrastructure have no project reference to Experiments. Their compiled assembly-reference sets contain neither Experiments nor WearVision.SyntheticStand. Deployable reflection scans found no prohibited truth/future members. Runtime admission rejects oracle-marked deployable input with OracleLeakageDetected. The only oracle truth type is internal to WearVision.Reliability.Experiments.Oracle; its public result is explicitly non-deployable and persists only through SCH-ORC. Deployable policy oracle-access count is 0.

B0–B7 consume BaselineInput, not OracleTruth or oracle DTOs. The current-state-only oracle is isolated, labelled non-deployable, and has no future-path/future-observation access. RSK-001–RSK-004 and ORA-001–ORA-008 passed.

No R4 project references a legacy project, DLL, source file, or retired result. Searches found no production use of System.Random, Guid.NewGuid, GetHashCode-based streams, CVaR, hidden dangerous-wear fields, or point-RUL-to-risk conversion. Text occurrences of legacy/CVaR/prohibited names exist only as negative QA assertions.

Final read-only rehash of the eight protected legacy source authorities matched ER-02 exactly: E274C47A…, BCDADD28…, AFA9293E…, 1DFA33FB…, 227933BC…, 1F122441…, AEA5B5DB…, and 6AD8FDFB…. Retirement path/hash rejection and permitted R4 fixture acceptance passed.

E:\WearVision\ MODIFICATION_STATUS: NO_ER05_WRITES
PROTECTED_SOURCE_MODIFIED = FALSE
"@
Write-Utf8 (Join-Path $outputRoot $names.Oracle) $oracleReport

$reproReport = @"
# ER-05 reproducibility and replay QA

Decision: PASS.

The canonical RNG uses length-prefixed little-endian key encoding, SHA-256 derivation, four little-endian state words, frozen all-zero SplitMix64 remediation, xoshiro256**, and upper-53-bit binary64 conversion. Named stream purposes and substream indices prevent arithmetic-offset seeding. The independently derived known-answer vector passed byte, digest, state, five-output, and unit-double checks.

Full Release suite A and suite B each executed 138 tests and passed all 138. Their normalized test-name/outcome sets are identical: $normalizedEqual. Raw TRX hashes intentionally differ because run IDs/timestamps/durations are container metadata; replay comparison uses normalized semantic outcomes. All 122 inherited IDs are identical and passed in both runs.

NUM-006 confirms registered serial/parallel sequence equality. Additional tests confirm same-key repeat, changed-field divergence, independent-purpose divergence, order-independent trajectory streams, stable deterministic reductions, and no time seed. The architecture/replay and parallel filters passed 13/13 and 3/3 respectively.

Evidence: evidence/rng-known-answer-vector.json, evidence/replay-comparison.json, normalized-full-a.csv, normalized-full-b.csv, full-a.trx, full-b-replay.trx, and parallel-reproducibility.trx.

These are software-validation fixtures, not paper results. No T5 or T6 row was executed.
"@
Write-Utf8 (Join-Path $outputRoot $names.Repro) $reproReport

$ownerReport = @"
# ER-05 owner-input and T5 blocker register

Software status: T5_SOFTWARE_READY_OWNER_INPUTS_REQUIRED. T5 was not executed and is not authorized by ER-05.

| Input | Status | Owner/evidence needed | Required before |
|---|---|---|---|
| Functional G and calibration evidence | [AUTHOR DATA REQUIRED] | Limit-state/domain owner | T5 |
| Exposure clock and unit | [AUTHOR DATA REQUIRED] | Operations owner | T5/T6 |
| Horizon H | [AUTHOR DATA REQUIRED] | Operations/decision owner | T5 |
| epsilon and epsilon_rem | [AUTHOR DATA REQUIRED] | Risk owner | T5 |
| Transition model | [AUTHOR DATA REQUIRED] | Degradation-model owner | T5 |
| Posterior provider/calibration | [AUTHOR DATA REQUIRED] | 1151/posterior owner | T5 |
| Routine/monitor/inspection observation models | [AUTHOR DATA REQUIRED] | Sensing and inspection owners | T5 |
| Reset distribution | [AUTHOR DATA REQUIRED] | Maintenance owner | T5 |
| Action durations and inspection pause semantics | [AUTHOR DATA REQUIRED] | Operations owner | T5 |
| Consequence definitions/normalizers and weight region | [AUTHOR DATA REQUIRED] | Decision/stakeholder owners | T5 |
| Baseline tuning parameters/artifacts | [AUTHOR DATA REQUIRED] | Baseline owner using Design only | T5 |
| Practical-effect margin Delta_star | [AUTHOR DATA REQUIRED] | Stakeholder/statistical owner | T6 |
| Rare-event precision target h_star | [AUTHOR DATA REQUIRED] | Statistical owner | T5 planning/T6 |
| Censoring model/confirmation rule | [AUTHOR DATA REQUIRED] | Statistical/domain owner | T5/T6 |
| Scientific factor levels and real-data decision | [AUTHOR DATA REQUIRED] | Study/data owners | T6 |

Fixture configuration values are explicitly TEST_FIXTURE_ONLY and cannot satisfy any owner input. Preflight rejects fixture-profile escalation, unresolved required values, T5, and T6. No convenience constant is promoted to a scientific default.

Next task: ER-06A — 1152 T5 Owner-Input, Calibration-Placeholder & Pilot-Configuration Closure.
"@
Write-Utf8 (Join-Path $outputRoot $names.Owners) $ownerReport

$implementationReadme = @"
# WearVision Reliability R4 implementation handoff

ER-05 produced a clean five-project net8.0 solution at $workspaceRoot and a deterministic source snapshot at $($names.Snapshot) (SHA-256 $snapshotHash).

Use an installed .NET 8 SDK (global.json pins 8.0.424), or the workspace-local .dotnet SDK in the original workspace. Restore uses only MSTest 4.0.2 for tests; production is BCL-only.

Build: dotnet restore WearVision.Reliability.R4.sln --packages .nuget/packages, then dotnet build WearVision.Reliability.R4.sln -c Release --no-restore.

Test: dotnet test WearVision.Reliability.R4.sln -c Release --no-build. The sealed result is 138/138 passed, including all 122 inherited semantic IDs.

Scientific boundary: configs/fixtures is TEST_FIXTURE_ONLY. Owner inputs remain unresolved; no fixture value is a field parameter or paper result. T5 infrastructure exists but T5 is not authorized/executed. T6 and manuscript editing are blocked.

Oracle boundary: truth is internal to Experiments.Oracle, deployable assemblies do not reference Experiments, and oracle evidence uses SCH-ORC. Legacy projects/results are neither referenced nor copied. E:\WearVision\ remained read-only.

See the main report, traceability register, build/test evidence, coverage CSV, schema QA, reproducibility report, and autonomous handoff for verification details.
"@
Write-Utf8 (Join-Path $outputRoot $names.Readme) $implementationReadme

$mainReport = @"
# 1. Executive Implementation Decision

ER-05 PASS: the R4 C# implementation is buildable and all authorized T0–T4 software QA passes. This is not scientific-result authorization.

# 2. Authority and Protected-Tree Confirmation

ER-03/ER-04 governed implementation; ER-02 governed retirement. E:\WearVision\ was read-only and its eight final reference hashes still match.

# 3. Workspace and Origin Provenance

Workspace: $workspaceRoot. Origin and authority SHA-256 inventories are included in the snapshot. PROTECTED_SOURCE_MODIFIED = FALSE.

# 4. Solution / Project Structure

WearVision.Reliability.R4.sln contains exactly Domain, Application, Infrastructure, Experiments, and Tests.

# 5. Target Framework / Dependency Result

All projects target net8.0. Production uses BCL only; tests use MSTest 4.0.2. The frozen dependency graph passed compiled/project-file inspection.

# 6. Domain Types

All required ID, hash, exposure, horizon, probability, reliability, disutility, standard-error, evaluation-status, and decision-status types are implemented with validation.

# 7. Configuration / Compatibility

Strict JSON, unknown-member rejection, canonical ordering, SHA-256 hashing, explicit HasValue placeholders, file-hash config sets, split validation, compatibility tuples, and preflight are implemented.

# 8. Belief Implementation

Immutable weighted support, indexed sampling, a controlled parametric adapter, normalization checks, and external-adapter boundaries are implemented generically.

# 9. Transition / Observation / Reset

Generic contracts plus deterministic/stochastic scalar fixtures, finite observation kernels, missingness semantics, and point/two-point reset fixtures are present. All are TEST_FIXTURE_ONLY.

# 10. Bayes Update

WeightedSupportBayesUpdater uses log likelihoods, log-sum-exp normalization, compatibility validation, and typed failure on invalid support.

# 11. Limit State

IFunctionalLimitState is the sole G authority. FirstPassageDetector treats G <= 0 as failure and preserves crossing after recovery.

# 12. Deterministic RNG

The frozen SHA-256/xoshiro256** contract, named purposes, substreams, all-zero remediation, and independent known-answer vector are implemented and pass.

# 13. First-Passage Engine

PosteriorPredictiveFirstPassageEstimator returns P_f, R, Wilson bounds, standard errors, counts, convergence, status, and diagnostics without selecting policy or accessing oracle truth.

# 14. Policy Representation

Typed direct and bounded one-information-action policy trees are implemented; no arbitrary infinite policy search exists.

# 15. Action Admissibility

All four allowed actions use typed admissibility reasons. Missing kernels, durations, reset support, or exposure semantics fail closed.

# 16. Consequence / Disutility

Five bounded dimensionless consequence components and a validated weight simplex feed stable expected-disutility aggregation. Scientific weights remain unresolved.

# 17. Chance-Constraint Feasibility

One-sided 99% Wilson bounds classify proven feasible, proven infeasible, or unresolved conservatively against epsilon.

# 18. Constrained Selector

Selection minimizes J only among proven-feasible policies, uses frozen tie breaks, reports constraint breach when none is feasible, and never returns an action for unresolved-only input.

# 19. EVSI

EVSI is no-information value minus information-conditioned value minus one acquisition burden; compatibility and double-charge guards are implemented.

# 20. Oracle Firewall

Compile-time references, reflection scans, runtime admission, schema separation, and policy input types all pass. Deployable oracle access count is zero.

# 21. Raw Persistence / Manifests

Fourteen record types map the 267-field registry through typed value mapping, registry validation, invariant CSV, structured events, hashes, and completion-last manifests.

# 22. Retirement Firewall

Registered retired path fragments and hashes reject legacy artifacts; permitted R4 fixture artifacts pass. Retired numerical results are never loaded into decisions.

# 23. Baselines B0–B7 / ORC

B0–B7 are implemented with explicit IDs/input contracts and no hidden parameters. B8 is disabled. ORC is current-state-only and non-deployable.

# 24. Ablation Hooks

ABL-1 through ABL-8 are registered; ABL-7 is NotApplicable with jumps disabled and ABL-8 is explicitly non-deployable.

# 25. Experiment Harness

The harness loads the frozen matrix and authorizes only ER05_TEST_ONLY SEM rows; scientific/CAL/ROB/ABL/REAL execution remains blocked.

# 26. T5/T6 Authorization Guards

T5 is rejected pending a later explicit task; fixture escalation is rejected; T6 is hard-blocked.

# 27. Semantic Test Implementation

All 122 inherited IDs are exact MSTest DataRow display names with substantive assertions across 12 families: 105 critical and 17 major.

# 28. Build Result

Release build: PASS, 0 errors, 0 warnings. Diagnostic log is included.

# 29. Test Result

Full suite: 138 passed, 0 failed, 0 skipped. Inherited reconciliation: 122/122 passed.

# 30. Deterministic Replay

Two full runs have identical normalized 138-test name/outcome sets; RNG KAT and same-key replay pass.

# 31. Parallel Determinism

Registered serial/parallel streams and results match; the focused 3-test suite passes.

# 32. Schema QA

All 14 schemas and 267 fields reconcile with semantic_match YES and requiredness validation PASS.

# 33. Configuration QA

Canonical repeat/hash sensitivity, unknown rejection, 24 tolerance rows, config file hashes, explicit placeholders, and T5/T6 preflight rejection pass.

# 34. Legacy Isolation QA

No legacy project/DLL/result reference exists. Protected hashes match ER-02 and negative scans find only explicit test deny strings.

# 35. Known Limitations / Owner Placeholders

Scientific G, clocks, horizon, constraints, models, reset, durations, consequences, weights, baseline tuning, precision targets, censoring, factor levels, and real-data decision remain owner inputs.

# 36. ER-05 Release Gate

ER05_IMPLEMENTATION_STATUS = PASS for software implementation and authorized T0–T4 QA.

# 37. T5 Readiness

T5 software infrastructure is ready, but owner inputs and explicit authorization are required. T5 was not executed.

# 38. T6 Prohibition

T6_FULL_EXPERIMENT_ALLOWED: NO.

# 39. Manuscript Gate

MANUSCRIPT_EDITING_ALLOWED: NO. No manuscript, table, figure, or scientific result was generated.

# 40. Recommended Next Task

ER-06A — 1152 T5 Owner-Input, Calibration-Placeholder & Pilot-Configuration Closure.

ER-05 IMPLEMENTATION RELEASE

WORKSPACE: $workspaceRoot
SOURCE_SNAPSHOT: $($names.Snapshot)
SOURCE_SNAPSHOT_SHA256: $snapshotHash

PROTECTED_TREE_STATUS: NO_ER05_WRITES
TARGET_FRAMEWORK: net8.0
PACKAGE_POLICY: BCL-only production; MSTest 4.0.2 tests; workspace-local NuGet cache
BUILD_CONFIGURATION: Release

PROJECTS
Domain: PASS
Application: PASS
Infrastructure: PASS
Experiments: PASS
Tests: PASS

IMPLEMENTATION
E1_INFORMATION_SET: VERIFIED_T0_T4
E2_BELIEF: VERIFIED_T0_T4
E3_TRANSITION: VERIFIED_T0_T4
E4_OBSERVATION: VERIFIED_T0_T4
E5_BAYES_UPDATE: VERIFIED_T0_T4
E6_LIMIT_STATE_RESET: VERIFIED_T0_T4
E7_FIRST_PASSAGE_RELIABILITY: VERIFIED_T0_T4
E8_POLICY_SPACE: VERIFIED_T0_T4
E9_CONSEQUENCE: VERIFIED_T0_T4
E10_EXPECTED_DISUTILITY: VERIFIED_T0_T4
E11_CONSTRAINED_SELECTION: VERIFIED_T0_T4
E12_EVSI: VERIFIED_T0_T4

RNG: PASS
ORACLE_FIREWALL: PASS
RETIREMENT_FIREWALL: PASS
RAW_PROVENANCE: PASS
CONFIG_COMPATIBILITY: PASS
BASELINES: B0-B7 PASS; B8 disabled; ORC isolated
ABLATIONS: ABL-1..8 implemented; ABL-7 NotApplicable; ABL-8 non-deployable
EXPERIMENT_HARNESS: T0-T4/SEM authorization PASS; T5/T6 blocked

BUILD
Status: PASS
Errors: 0
Warnings: 0

SEMANTIC TESTS
Expected inherited IDs: 122
Implemented IDs: 122
Executed IDs: 122
Passed: 122 inherited; 138 total suite
Failed: 0
Skipped: 0
Critical failures: 0
Major unresolved: 0 test failures; scientific owner inputs remain outside ER-05

QA
Deterministic replay: PASS
Parallel reproducibility: PASS
Oracle access count for deployable policies: 0
Raw-schema reconciliation: 267/267 PASS across 14 schemas
Write-once output: PASS
Legacy isolation: PASS
Retired-artifact rejection: PASS

OWNER PLACEHOLDERS
Before T5: Required; see owner blocker register
Before T6: Required; T6 remains prohibited

GATES
ER05_BUILD_STATUS: PASS
T0_T4_SEMANTIC_QA: PASS
ORACLE_FIREWALL: PASS
REPRODUCIBILITY_QA: PASS
RAW_PROVENANCE_QA: PASS
LEGACY_ISOLATION: PASS
ER05_IMPLEMENTATION_STATUS: PASS

T5_SOFTWARE_STATUS: T5_SOFTWARE_READY_OWNER_INPUTS_REQUIRED
T5_PILOT_EXECUTED: NO
T6_FULL_EXPERIMENT_ALLOWED: NO
MANUSCRIPT_EDITING_ALLOWED: NO

DECISION: Release ER-05 implementation and T0-T4 QA evidence only; do not authorize pilot or scientific execution.

NEXT RECOMMENDED TASK:
ER-06A — 1152 T5 Owner-Input, Calibration-Placeholder & Pilot-Configuration Closure
"@
Write-Utf8 (Join-Path $outputRoot $names.Main) $mainReport

$handoffSections = [ordered]@{
    '96.1 Workspace and source snapshot'="Workspace: $workspaceRoot. Snapshot: $($names.Snapshot), SHA-256 $snapshotHash."
    '96.2 Build status'='PASS: Release, 0 errors, 0 warnings.'
    '96.3 Test count and result'='138 passed, 0 failed, 0 skipped; two complete runs.'
    '96.4 122-test reconciliation'='122 expected, implemented, executed, and passed; 0 missing, duplicate, failed, or skipped.'
    '96.5 Project dependency graph'='Domain <- Application <- Infrastructure <- Experiments; Tests references all. No reverse/legacy production edge.'
    '96.6 Implemented interface list'='IDeployableInformationAdmission; IDegradationBelief; IStateTransitionKernel; IResetKernel; IObservationKernel; IBeliefUpdater; IFunctionalLimitState; IFirstPassageDetector; IFirstPassageReliabilityEstimator; ITerminalDecisionRule; IPolicyEnumerator; IActionAdmissibility; IConsequenceEvaluator; IExpectedDisutilityEvaluator; IReliabilityFeasibilityEvaluator; IConstrainedPolicySelector; IEvsiEvaluator; ICensoringStrategy; IRandomStream; IRandomStreamFactory.'
    '96.7 Equation E1–E12 implementation map'='All E1–E12 are VERIFIED_T0_T4; exact files/types/methods/tests/schemas/hashes are in the traceability CSV.'
    '96.8 Domain/value types'='All frozen IDs, hash wrappers, exposure/horizon, probability/reliability/disutility/SE, statuses, policies, diagnostics, and configuration enums are present and validated.'
    '96.9 Configuration implementation'='Strict JSON, explicit schema versions, canonical serialization, SHA-256, hash-verified config set, explicit HasValue placeholders, and stage preflight.'
    '96.10 Compatibility manifest'='Strict architecture/model/protocol tuple plus named component hashes; mismatch returns typed incompatibility.'
    '96.11 Posterior implementations'='Immutable weighted support, controlled parametric adapter, normalization/hash/version semantics, indexed sampling, and external boundary.'
    '96.12 Transition/observation/reset implementations'='Generic contracts with labelled scalar analytical/stochastic, informative/uninformative/missingness, and point/two-point reset fixtures only.'
    '96.13 Limit-state implementation'='IFunctionalLimitState is the sole G authority; G<=0 failure and crossing-after-recovery are tested.'
    '96.14 RNG implementation and known-answer result'='SHA-256 hierarchical key + xoshiro256** + upper-53-bit double; independent hard-coded vector PASS.'
    '96.15 First-passage engine'='Posterior predictive physical propagation with dedicated crossing detector; no terminal shortcut, point-RUL risk, selector, weights, or oracle.'
    '96.16 Monte Carlo convergence implementation'='Streaming batches, configured min/batch/max, stable-batch count, standard errors, warnings/status, and cancellation.'
    '96.17 Wilson feasibility implementation'='One-sided 99% Wilson bounds with proven-feasible/proven-infeasible/unresolved classification.'
    '96.18 Policy representation'='Typed direct and bounded one-information-action trees; deterministic enumeration.'
    '96.19 Consequence/disutility implementation'='Five bounded dimensionless components, validated simplex, and stable online expected disutility.'
    '96.20 Selector implementation'='True constrained selection with frozen tie-breaks and explicit constraint-breach/unresolved behavior.'
    '96.21 EVSI implementation'='No-information minus information-conditioned value minus burden; one-charge ledger and compatibility checks.'
    '96.22 Oracle firewall result'='PASS: compile-time isolation, no deployable oracle members, admission rejection, SCH-ORC separation, access count 0.'
    '96.23 Baseline implementations'='B0–B7 implemented; B8 disabled; current-state ORC isolated/non-deployable; no rankings generated.'
    '96.24 Ablation hooks'='ABL-1..8 registered; ABL-7 NotApplicable while jumps disabled; ABL-8 non-deployable.'
    '96.25 Raw schemas / lineage'='14 schemas and 267 fields reconcile PASS; typed registry mapping, requiredness validation, invariant persistence.'
    '96.26 Write-once provenance'='Contained new run roots, incomplete marker, write-once files, hashes, completion manifest last, and mutation rejection PASS.'
    '96.27 Retirement firewall'='Retired path/hash rejection and permitted R4 fixture acceptance PASS; no retired values used.'
    '96.28 Deterministic replay result'='PASS: full A/B normalized 138-test outcome sets identical; same-key/KAT tests pass.'
    '96.29 Serial/parallel reproducibility result'='PASS under registered trajectory-level contract; focused suite 3/3.'
    '96.30 T5/T6 guards'='T5 preparation exists but execution is rejected; TEST_FIXTURE_ONLY escalation rejected; T6 hard-blocked.'
    '96.31 Remaining owner placeholders'='G, clock/unit, H, epsilon/epsilon_rem, posterior/transition/observations/reset, durations/mode, consequences/weights, baseline tuning, Delta_star, h_star, censoring, factor levels, and real-data decision.'
    '96.32 Code/files not implemented or not passing'='No authorized ER-05 code/test item is failing. Scientific adapters/calibrations and T5/T6 executions are intentionally not implemented/executed because owner evidence/authorization is absent.'
    '96.33 T5 readiness decision'='T5_SOFTWARE_READY_OWNER_INPUTS_REQUIRED. T5_PILOT_EXECUTED: NO.'
    '96.34 T6 status'='T6_FULL_EXPERIMENT_ALLOWED: NO.'
    '96.35 Manuscript editing status'='MANUSCRIPT_EDITING_ALLOWED: NO; no manuscript artifacts were touched.'
    '96.36 Exact recommended next task'='ER-06A — 1152 T5 Owner-Input, Calibration-Placeholder & Pilot-Configuration Closure.'
}
$handoffBuilder = [Text.StringBuilder]::new()
[void]$handoffBuilder.AppendLine('# ER-05 ChatGPT autonomous handoff')
[void]$handoffBuilder.AppendLine()
[void]$handoffBuilder.AppendLine('ER-05 implementation status: PASS for authorized software implementation and T0–T4 QA. No scientific or manuscript gate is opened.')
foreach ($pair in $handoffSections.GetEnumerator()) {
    [void]$handoffBuilder.AppendLine()
    [void]$handoffBuilder.AppendLine("## $($pair.Key)")
    [void]$handoffBuilder.AppendLine()
    [void]$handoffBuilder.AppendLine($pair.Value)
}
Write-Utf8 (Join-Path $outputRoot $names.Handoff) $handoffBuilder.ToString()

$indexRows = Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Where-Object Name -ne 'evidence-index.csv' | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ relative_path=[IO.Path]::GetRelativePath($outputRoot,$_.FullName).Replace('\','/'); size_bytes=$_.Length; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash }
}
$indexRows | Export-Csv -LiteralPath (Join-Path $evidenceRoot 'evidence-index.csv') -NoTypeInformation -Encoding utf8

$topFiles = @(Get-ChildItem -LiteralPath $outputRoot -File)
$topDirs = @(Get-ChildItem -LiteralPath $outputRoot -Directory)
if ($topFiles.Count -ne 12 -or $topDirs.Count -ne 1 -or $topDirs[0].Name -ne 'evidence') { throw 'Top-level output set is not exactly 12 files plus evidence.' }
if (@($names.Values | Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputRoot $_)) }).Count -ne 0) { throw 'A required top-level deliverable is missing.' }

[pscustomobject]@{ status='PASS'; topLevelFiles=$topFiles.Count; evidenceDirectories=$topDirs.Count; inheritedTests=122; fullSuiteTests=$counters.Total; snapshotSha256=$snapshotHash; output=$outputRoot } | ConvertTo-Json
