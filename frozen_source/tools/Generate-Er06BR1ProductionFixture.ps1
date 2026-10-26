param(
    [Parameter(Mandatory = $true)]
    [string]$ApprovedConfigRoot,
    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot,
    [Parameter(Mandatory = $true)]
    [string]$FixtureOutputRoot
)

$ErrorActionPreference = 'Stop'

function Write-CanonicalJson([string]$Path, [object]$Value) {
    $json = $Value | ConvertTo-Json -Depth 100 -Compress
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function File-Hash([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Stable-Hash([string]$Value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

$source = [System.IO.Path]::GetFullPath($ApprovedConfigRoot)
$destination = [System.IO.Path]::GetFullPath($DestinationRoot)
if (Test-Path -LiteralPath $destination) {
    throw 'Fixture destination already exists.'
}

$manifest = Get-Content -LiteralPath (Join-Path $source 'config_set_manifest.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($entry in $manifest.Files) {
    $from = Join-Path $source $entry.RelativePath
    $to = Join-Path $destination $entry.RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
}

$modelPath = Join-Path $destination 'model_config_v2.json'
$model = Get-Content -LiteralPath $modelPath -Raw | ConvertFrom-Json
$model.ConfigId = 'model.er06b-r1.production-fixture.v1'
$model.ProfileKind = 'TestFixtureOnly'
Write-CanonicalJson $modelPath $model

$reliabilityPath = Join-Path $destination 'reliability_config_v2.json'
$reliability = Get-Content -LiteralPath $reliabilityPath -Raw | ConvertFrom-Json
$reliability.ConfigId = 'reliability.er06b-r1.production-fixture.v1'
$reliability.Epsilon.Value = 0.5
$reliability.EpsilonRemaining.Value = 0.5
Write-CanonicalJson $reliabilityPath $reliability

$numericalPath = Join-Path $destination 'numerical_config_v2.json'
$numerical = Get-Content -LiteralPath $numericalPath -Raw | ConvertFrom-Json
$numerical.ConfigId = 'numerical.er06b-r1.production-fixture.v1'
$numerical.McMinimumPaths = 8
$numerical.McBatchSize = 4
$numerical.McMaximumPaths = 32
$numerical.StableBatchCount = 2
$numerical.CancellationCheckInterval = 4
$numerical.DiagnosticVerbosity = 'TEST_FIXTURE_ONLY_PRODUCTION_PATH_QA'
Write-CanonicalJson $numericalPath $numerical

$resourcePath = Join-Path $destination 'resource_ceiling_config.json'
$resources = Get-Content -LiteralPath $resourcePath -Raw | ConvertFrom-Json
$resources.ConfigId = 'resources.er06b-r1.production-fixture.v1'
$resources.MaximumWallClockSeconds.Value = 60.0
$resources.MaximumStorageBytes.Value = 10485760
$resources.AllowEr04MaximumPaths.Value = $false
Write-CanonicalJson $resourcePath $resources

$outputPath = Join-Path $destination 'output_policy_v2.json'
$output = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
$output.ConfigId = 'output.er06b-r1.production-fixture.v1'
$output.ApprovedRoot = ([System.IO.Path]::GetFullPath($FixtureOutputRoot)).Replace('\', '/')
$output.RunIdPattern = '{fixture_run_id}'
Write-CanonicalJson $outputPath $output

$splitPath = Join-Path $destination 'split_manifest_v2.json'
$sourceHash = Stable-Hash 'ER06B-R1-TEST-FIXTURE-ONLY-NO-SCIENTIFIC-EVIDENCE'
$assignments = @()
foreach ($row in @('FX5-001', 'FX5-002')) {
    foreach ($index in 1..2) {
        $episode = '{0}-EP-{1:D4}' -f $row, $index
        $assignments += [ordered]@{
            EpisodeId = $episode
            SourceDatasetId = 'ER06B-R1-TEST-FIXTURE-ONLY-NO-DATASET'
            Partition = 'T5PilotOnly'
            GroupingKey = $episode
            Stratum = if ($row -eq 'FX5-001') { 'FIXTURE-NUMERICAL' } else { 'FIXTURE-PRECISION' }
            Reason = 'TEST_FIXTURE_ONLY_PRODUCTION_PATH_QA_NOT_SCIENTIFIC_EVIDENCE'
            ManifestVersion = 'split-er06b-r1-production-fixture-v1'
            SourceHash = $sourceHash
        }
    }
}
Write-CanonicalJson $splitPath ([ordered]@{ SchemaVersion = 'split-v2'; ConfigId = 'split.er06b-r1.production-fixture.v1'; Assignments = $assignments })
$splitHash = File-Hash $splitPath

$effectiveRows = @(
    @{ File = 'T5-001_effective_config.json'; Id = 'FX5-001'; Scenario = 'FIXTURE-NUMERICAL'; Subroot = 'FX5-001-FIXTURE-NUMERICAL' },
    @{ File = 'T5-002_effective_config.json'; Id = 'FX5-002'; Scenario = 'FIXTURE-PRECISION'; Subroot = 'FX5-002-FIXTURE-PRECISION' }
)
foreach ($row in $effectiveRows) {
    $path = Join-Path $destination $row.File
    $effective = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $effective.ConfigId = ('effective.{0}.er06b-r1.production-fixture.v1' -f $row.Id)
    $effective.ExperimentId.Value = $row.Id
    $effective.ScenarioId.Value = $row.Scenario
    $effective.Stage = 'T4'
    $effective.ProfileKind = 'TestFixtureOnly'
    $effective.ProtocolVersion = 'ER04-2026-08-20-TEST-FIXTURE-ONLY'
    $effective.FactorLevelSetIds = @('TEST_FIXTURE_ONLY', 'PRODUCTION_PATH_E2E')
    $effective.FactorLevelStatus = 'TestFixtureOnly'
    $effective.SplitManifestId = 'split.er06b-r1.production-fixture.v1'
    $effective.SplitManifestSha256 = $splitHash
    $effective.EpisodeCount = 2
    $effective.EpisodeCountStatus = 'FixtureFixed'
    $effective.CalibrationStrategyId = 'TEST_FIXTURE_ONLY_NO_CALIBRATION'
    $effective.CensoringStrategyId = 'TEST_FIXTURE_ONLY_DIRECT_KNOWN_TRUTH'
    $effective.BootstrapAnalysisId = 'TEST_FIXTURE_ONLY_NOT_EXECUTED'
    $effective.MultiplicityAnalysisId = 'TEST_FIXTURE_ONLY_NOT_EXECUTED'
    $effective.SyntheticOrModelBasedLabel = 'TEST_FIXTURE_ONLY_MODEL_BASED_QA_NOT_SCIENTIFIC_EVIDENCE'
    $effective.OutputSubroot = $row.Subroot
    Write-CanonicalJson $path $effective
}

$experimentPath = Join-Path $destination 'experiment_set_manifest.json'
$runs = foreach ($row in $effectiveRows) {
    [ordered]@{
        ExperimentId = [ordered]@{ Value = $row.Id }
        ScenarioId = [ordered]@{ Value = $row.Scenario }
        EffectiveConfigPath = $row.File
        EffectiveConfigSha256 = File-Hash (Join-Path $destination $row.File)
        OutputSubroot = $row.Subroot
        AuthorizationEligible = $true
    }
}
Write-CanonicalJson $experimentPath ([ordered]@{
    SchemaVersion = 'experiment-set-v2'
    ConfigId = 'experiment-set.er06b-r1.production-fixture.v1'
    Stage = 'T4'
    ProfileKind = 'TestFixtureOnly'
    ProtocolVersion = 'ER04-2026-08-20-TEST-FIXTURE-ONLY'
    Runs = @($runs)
})

$roles = @{}
foreach ($entry in $manifest.Files) {
    $roles[$entry.RelativePath] = $entry.Role
}
$fileEntries = foreach ($relativePath in ($roles.Keys | Sort-Object)) {
    [ordered]@{
        RelativePath = $relativePath.Replace('\', '/')
        Sha256 = File-Hash (Join-Path $destination $relativePath)
        Role = $roles[$relativePath]
    }
}
Write-CanonicalJson (Join-Path $destination 'config_set_manifest.json') ([ordered]@{
    SchemaVersion = 'config-set-v2'
    ConfigSetId = 'ER06B-R1-PRODUCTION-E2E-TEST-FIXTURE-ONLY-V1'
    ProfileKind = 'TestFixtureOnly'
    ScientificUse = 'TEST_FIXTURE_ONLY_PRODUCTION_PATH_QA_NOT_SCIENTIFIC_EVIDENCE'
    AuthorizedStage = 'T4'
    CanonicalizationVersion = 'canonical-json-v1-utf8-no-bom'
    ApprovalStatus = 'Approved'
    OwnerId = 'ER06B_R1_FIXTURE_QA'
    CreatedById = 'ER06B_R1_FIXTURE_GENERATOR'
    UnresolvedPlaceholders = @()
    Files = @($fileEntries)
})

[pscustomobject]@{
    Destination = $destination
    Files = (Get-ChildItem -LiteralPath $destination -Recurse -File).Count
    ConfigSetSha256 = File-Hash (Join-Path $destination 'config_set_manifest.json')
    Episodes = 4
    Profile = 'TEST_FIXTURE_ONLY'
}
