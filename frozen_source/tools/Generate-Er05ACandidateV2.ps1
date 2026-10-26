param(
    [Parameter(Mandatory = $true)]
    [string]$SourceV1Directory,
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory,
    [Parameter(Mandatory = $true)]
    [string]$Er05ASourceSnapshotSha256,
    [Parameter(Mandatory = $true)]
    [string]$DotnetPath,
    [Parameter(Mandatory = $true)]
    [string]$ExperimentsAssemblyPath
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$source = [System.IO.Path]::GetFullPath($SourceV1Directory)
$destination = [System.IO.Path]::GetFullPath($DestinationDirectory)

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Source V1 directory does not exist: $source"
}
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) {
    throw "Destination directory must be absent or empty: $destination"
}
if ($Er05ASourceSnapshotSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
    throw 'ER-05A source snapshot SHA-256 is invalid.'
}
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "dotnet host does not exist: $DotnetPath"
}
if (-not (Test-Path -LiteralPath $ExperimentsAssemblyPath -PathType Leaf)) {
    throw "Experiments assembly does not exist: $ExperimentsAssemblyPath"
}

[System.IO.Directory]::CreateDirectory($destination) | Out-Null
[System.IO.Directory]::CreateDirectory((Join-Path $destination 'artifacts')) | Out-Null

function Read-Json([string]$name) {
    Get-Content -LiteralPath (Join-Path $source $name) -Raw | ConvertFrom-Json
}

function Write-Text([string]$relativePath, [string]$content) {
    $path = Join-Path $destination $relativePath
    $parent = Split-Path -Parent $path
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

function Write-Json([string]$relativePath, $value) {
    Write-Text $relativePath ($value | ConvertTo-Json -Depth 100 -Compress)
}

function Get-FileHashHex([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-TextHash([string]$value) {
    $bytes = $utf8NoBom.GetBytes($value)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Format-Double([double]$value) {
    $value.ToString('R', $culture)
}

function New-RuntimeValue($value, [string]$ownerId, [string]$evidenceId, [string]$status = 'MethodologicalPilotNotApproved') {
    [ordered]@{ Value = $value; Status = $status; OwnerId = $ownerId; EvidenceId = $evidenceId }
}

$registeredArtifacts = New-Object System.Collections.Generic.List[object]
function New-ArtifactIdentity(
    [string]$relativePath,
    [string]$artifactId,
    [string]$version,
    [string]$payload,
    [string]$ownerId,
    [string]$evidenceId,
    [string]$role
) {
    Write-Text $relativePath $payload
    $sha = Get-FileHashHex (Join-Path $destination $relativePath)
    $registeredArtifacts.Add([ordered]@{ RelativePath = $relativePath.Replace('\', '/'); Role = $role })
    [ordered]@{
        ArtifactId = $artifactId
        Version = $version
        Sha256 = $sha
        Status = 'MethodologicalPilotNotApproved'
        OwnerId = $ownerId
        EvidenceId = $evidenceId
    }
}

$modelV1 = Read-Json 'model_config.json'
$reliabilityV1 = Read-Json 'reliability_config.json'
$actionsV1 = Read-Json 'action_config.json'
$consequencesV1 = Read-Json 'consequence_config.json'
$numericalV1 = Read-Json 'numerical_config.json'
$baselineV1 = Read-Json 'baseline_config.json'
$outputV1 = Read-Json 'output_config.json'
$compatibilityV1 = Read-Json 'compatibility_manifest.json'
$retirementV1 = Read-Json 'retirement_denylist.json'
$reviewV1 = Read-Json 'candidate_review_manifest.json'

$stateSpaceId = 'scalar-reference-state'
$exposureClockId = [string]$modelV1.ExposureClockId.Value
$artifactEvidence = 'ER06A_DRAFT_CANDIDATE_NOT_APPROVED'
$boundary = [double]$reviewV1.ModelParameterBindings.FunctionalLimitBoundary
$beliefStates = @($reviewV1.ModelParameterBindings.InitialSupportStates | ForEach-Object { [double]$_ })
$beliefWeights = @($reviewV1.ModelParameterBindings.InitialSupportWeights | ForEach-Object { [double]$_ })
$transitionRate = [double]$reviewV1.ModelParameterBindings.StochasticIncrementScale
$routineProbabilities = @($reviewV1.ModelParameterBindings.RoutineProbabilityOfOneLowHigh | ForEach-Object { [double]$_ })
$monitorProbabilities = @($reviewV1.ModelParameterBindings.MonitorProbabilityOfOneLowHigh | ForEach-Object { [double]$_ })
$inspectionProbabilities = @($reviewV1.ModelParameterBindings.InspectionProbabilityOfOneLowHigh | ForEach-Object { [double]$_ })
$missingnessRate = [double]$reviewV1.ModelParameterBindings.ModerateMissingnessRate
$resetStates = @($reviewV1.ModelParameterBindings.ResetStates | ForEach-Object { [double]$_ })
$resetFirstProbability = [double]$reviewV1.ModelParameterBindings.ResetFirstProbability
$resetProbabilities = @($resetFirstProbability, (1.0 - $resetFirstProbability))

$limitVersion = [string]$modelV1.LimitStateVersion.Value
$limitPayload = "AffineScalarLimitStateV1|$(Format-Double $boundary)|$stateSpaceId|$exposureClockId|normalized-margin|$limitVersion"
$limitArtifact = New-ArtifactIdentity 'artifacts/limit_state.artifact' 'PAR-001-LIMIT-STATE' $limitVersion $limitPayload 'limit-state/scientific owner' $artifactEvidence 'model-artifact'
$beliefVersion = [string]$modelV1.PosteriorVersion.Value
$beliefPayload = "WeightedSupportBeliefV1|$(($beliefStates | ForEach-Object { Format-Double $_ }) -join ',')|$(($beliefWeights | ForEach-Object { Format-Double $_ }) -join ',')|$stateSpaceId|$beliefVersion"
$beliefArtifact = New-ArtifactIdentity 'artifacts/weighted_belief.artifact' 'PAR-002-005-WEIGHTED-BELIEF' $beliefVersion $beliefPayload 'posterior/scientific owner' $artifactEvidence 'model-artifact'
$transitionVersion = 'transition.t5-exposure-scaled-uniform-rate-candidate-v2'
$transitionSemantics = 'UNIFORM_RATE_PER_SEGMENT_TIMES_DELTA_EXPOSURE'
$transitionPayload = "ExposureScaledUniformRateV1|$(Format-Double $transitionRate)|$transitionSemantics|$exposureClockId|$stateSpaceId|$transitionVersion"
$transitionArtifact = New-ArtifactIdentity 'artifacts/exposure_transition.artifact' 'PAR-006-009-EXPOSURE-TRANSITION' $transitionVersion $transitionPayload 'degradation/scientific owner' $artifactEvidence 'model-artifact'

function New-ObservationBinding(
    [string]$relativePath,
    [string]$artifactId,
    [string]$version,
    [bool]$enabled,
    [string]$observationKind,
    [string]$actionKind,
    [double]$low,
    [double]$high,
    [string]$missingMode,
    [double]$missingLow,
    [double]$missingHigh,
    [string]$ownerId
) {
    $payload = "StateConditionedBinaryObservationV1|$($enabled.ToString())|$observationKind|$actionKind|0.5|$(Format-Double $low)|$(Format-Double $high)|$missingMode|$(Format-Double $missingLow)|$(Format-Double $missingHigh)|$stateSpaceId|$version"
    $artifact = New-ArtifactIdentity $relativePath $artifactId $version $payload $ownerId $artifactEvidence 'model-artifact'
    [ordered]@{
        FactoryTypeId = 'StateConditionedBinaryObservationV1'
        Enabled = $enabled
        ObservationKind = $observationKind
        ActionKind = $actionKind
        StateThreshold = 0.5
        ProbabilityOneLowState = $low
        ProbabilityOneHighState = $high
        Missingness = [ordered]@{ Mode = $missingMode; ProbabilityLowState = $missingLow; ProbabilityHighState = $missingHigh; QualityId = 'ER06A-METHODOLOGICAL-MISSINGNESS-CANDIDATE' }
        StateSpaceId = $stateSpaceId
        Artifact = $artifact
    }
}

$routineObservation = New-ObservationBinding 'artifacts/routine_observation.artifact' 'PAR-010-011-ROUTINE-OBSERVATION' ([string]$modelV1.RoutineObservationVersion.Value) $true 'Routine' 'Continue' $routineProbabilities[0] $routineProbabilities[1] 'None' 0.0 0.0 'sensing/scientific owner'
$monitorObservation = New-ObservationBinding 'artifacts/monitor_observation.artifact' 'PAR-012-013-015-MONITOR-OBSERVATION' ([string]$modelV1.MonitorObservationVersion.Value) $true 'IntensifiedMonitoring' 'IntensifiedMonitoring' $monitorProbabilities[0] $monitorProbabilities[1] 'NonInformative' $missingnessRate $missingnessRate 'monitoring/scientific owner'
$inspectionObservation = New-ObservationBinding 'artifacts/inspection_observation.artifact' 'PAR-014-015-INSPECTION-OBSERVATION' ([string]$modelV1.InspectionObservationVersion.Value) $false 'TargetedInspection' 'TargetedInspection' $inspectionProbabilities[0] $inspectionProbabilities[1] 'None' 0.0 0.0 'inspection/scientific owner'

$resetVersion = [string]$modelV1.ResetVersion.Value
$resetPayload = "TwoPointScalarResetV1|$(($resetStates | ForEach-Object { Format-Double $_ }) -join ',')|$(($resetProbabilities | ForEach-Object { Format-Double $_ }) -join ',')|$stateSpaceId|$resetVersion"
$resetArtifact = New-ArtifactIdentity 'artifacts/reset_distribution.artifact' 'PAR-017-019-RESET' $resetVersion $resetPayload 'maintenance/scientific owner' $artifactEvidence 'model-artifact'

$model = [ordered]@{
    SchemaVersion = 'model-v2'
    ConfigId = 'model.t5-candidate.er05a.v2'
    ProfileKind = 'T5Pilot'
    StateSpaceId = $stateSpaceId
    StateSpaceVersion = 't5-methodological-scalar-state-v2'
    StateUnitMetadata = 'dimensionless-normalized-state; no physical calibration claim'
    ExposureClockId = New-RuntimeValue $exposureClockId 'operations/scientific owner' $artifactEvidence
    ExposureUnit = New-RuntimeValue ([string]$modelV1.ExposureUnit.Value) 'operations/scientific owner' $artifactEvidence
    EventJumpsEnabled = $false
    LimitState = [ordered]@{ FactoryTypeId = 'AffineScalarLimitStateV1'; Boundary = $boundary; MarginUnitId = 'normalized-margin'; MarginUnitDescription = 'dimensionless methodological margin'; StateSpaceId = $stateSpaceId; ExposureClockId = $exposureClockId; Artifact = $limitArtifact }
    Belief = [ordered]@{ FactoryTypeId = 'WeightedSupportBeliefV1'; SupportStates = $beliefStates; Weights = $beliefWeights; StateSpaceId = $stateSpaceId; Artifact = $beliefArtifact }
    Transition = [ordered]@{ FactoryTypeId = 'ExposureScaledUniformRateV1'; MaximumIncrementRatePerUnitExposure = $transitionRate; ExposureScalingSemantics = $transitionSemantics; ExposureClockId = $exposureClockId; StateSpaceId = $stateSpaceId; Artifact = $transitionArtifact }
    RoutineObservation = $routineObservation
    MonitorObservation = $monitorObservation
    InspectionObservation = $inspectionObservation
    Reset = [ordered]@{ FactoryTypeId = 'TwoPointScalarResetV1'; SupportStates = $resetStates; Probabilities = $resetProbabilities; StateSpaceId = $stateSpaceId; Artifact = $resetArtifact }
}

$reliability = [ordered]@{
    SchemaVersion = 'reliability-v2'
    ConfigId = 'reliability.t5-candidate.er05a.v2'
    Horizon = New-RuntimeValue ([double]$reliabilityV1.Horizon.Value) 'operations/scientific owner' 'PAR-018_ER06A_DRAFT'
    Epsilon = New-RuntimeValue ([double]$reliabilityV1.Epsilon.Value) 'reliability owner' 'PAR-019_ER06A_DRAFT'
    EpsilonRemaining = New-RuntimeValue ([double]$reliabilityV1.EpsilonRemaining.Value) 'reliability owner' 'PAR-019_ER06A_DRAFT'
    FailureConfirmationRuleId = New-RuntimeValue ([string]$reliabilityV1.FailureConfirmationRuleId.Value) 'limit-state/statistics owner' 'ER03_FIRST_PASSAGE_AUTHORITY'
    CensoringStrategyId = New-RuntimeValue ([string]$reliabilityV1.CensoringStrategyId.Value) 'statistics owner' 'ER06A_DRAFT'
    ExposureClockId = $exposureClockId
}

$actionArtifactPayload = 'R4-ACTIONS|Continue|IntensifiedMonitoring|TargetedInspection|PlannedReplacement|T5Pilot|v2'
$actionArtifact = New-ArtifactIdentity 'artifacts/action_admissibility.artifact' 'ER04-R4-ACTION-ADMISSIBILITY' 'er04-v1' $actionArtifactPayload 'protocol owner' 'ER04_ACTION_AUTHORITY' 'protocol-artifact'
$actions = [ordered]@{
    SchemaVersion = 'action-v2'
    ConfigId = 'actions.t5-candidate.er05a.v2'
    AdmissibilityRule = $actionArtifact
    Actions = @(
        [ordered]@{ Action = 'Continue'; Enabled = [bool]$actionsV1.Enabled.Continue; Duration = New-RuntimeValue ([double]$actionsV1.Durations.Continue.Value) 'operations owner' 'ER06A_DRAFT'; ExposureClockId = $exposureClockId; ExposureMode = $null; BurdenComponentId = 'Continue' },
        [ordered]@{ Action = 'IntensifiedMonitoring'; Enabled = [bool]$actionsV1.Enabled.IntensifiedMonitoring; Duration = New-RuntimeValue ([double]$actionsV1.Durations.IntensifiedMonitoring.Value) 'operations owner' 'ER06A_DRAFT'; ExposureClockId = $exposureClockId; ExposureMode = $null; BurdenComponentId = 'IntensifiedMonitoring' },
        [ordered]@{ Action = 'TargetedInspection'; Enabled = [bool]$actionsV1.Enabled.TargetedInspection; Duration = New-RuntimeValue ([double]$actionsV1.Durations.TargetedInspection.Value) 'operations owner' 'ER06A_DRAFT'; ExposureClockId = $exposureClockId; ExposureMode = [string]$actionsV1.InspectionMode.Value; BurdenComponentId = 'TargetedInspection' },
        [ordered]@{ Action = 'PlannedReplacement'; Enabled = [bool]$actionsV1.Enabled.PlannedReplacement; Duration = New-RuntimeValue ([double]$actionsV1.Durations.PlannedReplacement.Value) 'operations owner' 'ER06A_DRAFT'; ExposureClockId = $exposureClockId; ExposureMode = $null; BurdenComponentId = 'PlannedReplacement' }
    )
}

$weights = [ordered]@{
    Failure = [double]$consequencesV1.Weights.Value.Failure
    PlannedReplacement = [double]$consequencesV1.Weights.Value.PlannedReplacement
    IntensifiedMonitoring = [double]$consequencesV1.Weights.Value.IntensifiedMonitoring
    TargetedInspection = [double]$consequencesV1.Weights.Value.TargetedInspection
    Downtime = [double]$consequencesV1.Weights.Value.Downtime
}
$weightPayload = "Failure=$(Format-Double $weights.Failure)|PlannedReplacement=$(Format-Double $weights.PlannedReplacement)|IntensifiedMonitoring=$(Format-Double $weights.IntensifiedMonitoring)|TargetedInspection=$(Format-Double $weights.TargetedInspection)|Downtime=$(Format-Double $weights.Downtime)"
$weightHash = Get-TextHash $weightPayload
$consequenceComponents = @(
    [ordered]@{ Component = 'Failure'; Enabled = $true; Definition = 'First-passage failure indicator'; NormalizationMethod = 'BinaryIndicator'; Normalizer = New-RuntimeValue 1.0 'decision/stakeholder owner' 'ER06A_DRAFT'; UnitOrNormalizationDescriptor = 'dimensionless [0,1]' },
    [ordered]@{ Component = 'PlannedReplacement'; Enabled = $true; Definition = 'Planned-replacement indicator'; NormalizationMethod = 'BinaryIndicator'; Normalizer = New-RuntimeValue 1.0 'decision/stakeholder owner' 'ER06A_DRAFT'; UnitOrNormalizationDescriptor = 'dimensionless [0,1]' },
    [ordered]@{ Component = 'IntensifiedMonitoring'; Enabled = $true; Definition = 'Monitoring-acquisition indicator'; NormalizationMethod = 'BinaryIndicator'; Normalizer = New-RuntimeValue 1.0 'decision/stakeholder owner' 'ER06A_DRAFT'; UnitOrNormalizationDescriptor = 'dimensionless [0,1]' },
    [ordered]@{ Component = 'TargetedInspection'; Enabled = $true; Definition = 'Inspection-acquisition indicator'; NormalizationMethod = 'BinaryIndicator'; Normalizer = New-RuntimeValue 1.0 'decision/stakeholder owner' 'ER06A_DRAFT'; UnitOrNormalizationDescriptor = 'dimensionless [0,1]' },
    [ordered]@{ Component = 'Downtime'; Enabled = $true; Definition = 'Interruption duration divided by configured horizon'; NormalizationMethod = 'DurationOverHorizon'; Normalizer = New-RuntimeValue ([double]$reliabilityV1.Horizon.Value) 'decision/stakeholder owner' 'ER06A_DRAFT'; UnitOrNormalizationDescriptor = 'dimensionless [0,1]' }
)
$consequences = [ordered]@{
    SchemaVersion = 'consequence-v2'
    ConfigId = 'consequences.t5-candidate.er05a.v2'
    DefinitionVersion = [string]$consequencesV1.DefinitionVersion.Value
    Components = $consequenceComponents
    WeightProfileId = [string]$consequencesV1.WeightProfileId.Value
    WeightProfileVersion = 'er05a-v2'
    WeightProfileStatus = 'MethodologicalStress'
    Weights = New-RuntimeValue $weights 'decision/stakeholder owner' 'ER06A_DRAFT'
    WeightProfileSha256 = $weightHash
}

$numerical = [ordered]@{
    SchemaVersion = 'numerical-v2'
    ConfigId = 'numerical.er04-general-t5-candidate.er05a.v2'
    FloatingPointType = 'Binary64'
    RngAlgorithmId = [string]$numericalV1.RngSchemeId
    MasterSeedUInt64 = [UInt64]$numericalV1.MasterSeed
    McMinimumPaths = [int]$numericalV1.Settings.MinimumPaths
    McBatchSize = [int]$numericalV1.Settings.BatchSize
    McMaximumPaths = [int]$numericalV1.Settings.MaximumPaths
    StableBatchCount = [int]$numericalV1.Settings.RequiredStableBatches
    FailureIntervalMethod = 'WilsonOneSided99'
    ObjectiveConvergenceRuleId = 'paired-objective-halfwidth-0p10-delta-star-v1'
    CrossingRefinementRuleId = 'two-consecutive-stable-refinements-v1'
    QuadratureAbsoluteTolerance = 1e-10
    QuadratureRelativeTolerance = 1e-8
    NormalizationToleranceId = 'TOL-NORM'
    ProbabilityToleranceId = 'TOL-PROB-COMP'
    WeightSimplexToleranceId = 'TOL-WEIGHT-SIMPLEX'
    DeterministicTieToleranceId = 'TOL-TIE-ULP'
    LogScoreClippingId = 'TOL-LOG-CLIP'
    MaximumDegreeOfParallelism = [int]$numericalV1.MaximumDegreeOfParallelism
    ParallelismLevel = 'TrajectoryOnly'
    StableReductionVersion = 'canonical-private-accumulator-stable-order-v1'
    CancellationCheckInterval = [int]$numericalV1.Settings.BatchSize
    DiagnosticVerbosity = 'T5_PLANNING_ONLY'
    ToleranceRegistrySha256 = [string]$numericalV1.ToleranceRegistryHash
}

function New-BaselineArtifact([string]$id, [string]$payload) {
    $safe = $id.ToLowerInvariant().Replace('-', '_')
    New-ArtifactIdentity "artifacts/$safe.design-artifact" "ER06A-DESIGN-$id" 'candidate-v1' $payload 'baseline/scientific owner' 'ER06A_DRAFT_PILOT_FIXED' 'baseline-design-artifact'
}

$b1Interval = [double]$baselineV1.TuningParameters.'B1.DesignInterval'.Value
$b3Threshold = [double]$baselineV1.TuningParameters.'B3.MeanThreshold'.Value
$b4Quantile = [double]$baselineV1.TuningParameters.'B4.QuantileLevel'.Value
$b4Threshold = [double]$baselineV1.TuningParameters.'B4.Threshold'.Value
$b5Threshold = [double]$baselineV1.TuningParameters.'B5.FirstPassageQuantileThreshold'.Value
$baselineArtifacts = [ordered]@{}
foreach ($item in @(
    @('B1-PERIODIC', "interval=$(Format-Double $b1Interval)|clock=$exposureClockId"),
    @('B2-OBSERVED-PROXY', 'NOT_APPLICABLE|no registered deployable scalar proxy'),
    @('B3-POSTERIOR-MEAN', "DISABLED|mapping=failure-oriented-state-v1|threshold=$(Format-Double $b3Threshold)"),
    @('B4-POSTERIOR-QUANTILE', "quantile=$(Format-Double $b4Quantile)|threshold=$(Format-Double $b4Threshold)|mapping=failure-oriented-state-v1"),
    @('B5-FIRST-PASSAGE-QUANTILE', "DISABLED|quantile=$(Format-Double $b5Threshold)"))) {
    $baselineArtifacts[$item[0]] = New-BaselineArtifact $item[0] $item[1]
}

function New-Baseline(
    [string]$id,
    [string]$applicability,
    [string]$reason,
    [Nullable[double]]$interval,
    [string]$mapping,
    [Nullable[double]]$meanThreshold,
    [Nullable[double]]$quantile,
    [Nullable[double]]$quantileThreshold,
    [Nullable[double]]$firstPassageQuantile
) {
    $artifact = if ($baselineArtifacts.Contains($id)) { $baselineArtifacts[$id] } else { $null }
    [ordered]@{
        BaselineId = $id
        Applicability = $applicability
        ImplementationVersion = 'er05a-typed-baseline-v2'
        TuningPartition = 'Design'
        DesignArtifactId = if ($artifact) { $artifact.ArtifactId } else { 'NOT_REQUIRED' }
        DesignArtifactSha256 = if ($artifact) { $artifact.Sha256 } else { Get-TextHash "$id|NO_NUMERIC_TUNING" }
        AllowedInformationFields = @('DEPLOYABLE_BELIEF_ONLY')
        ApplicabilityReason = $reason
        Interval = $interval
        ProxyThreshold = $null
        ScalarMappingId = $mapping
        MeanThreshold = $meanThreshold
        QuantileLevel = $quantile
        QuantileThreshold = $quantileThreshold
        FirstPassageQuantile = $firstPassageQuantile
        FirstPassageHorizonThreshold = $null
    }
}

$baselines = [ordered]@{
    SchemaVersion = 'baseline-v2'
    ConfigId = 'baselines.t5-candidate.er05a.v2'
    ExposureClockId = $exposureClockId
    ConsequenceDefinitionVersion = [string]$consequencesV1.DefinitionVersion.Value
    ReliabilityModelVersion = 'er05a-reference-runtime-v2'
    Baselines = @(
        New-Baseline 'B0-RUN-TO-FAILURE' 'Disabled' 'Not in the minimum T5 policy set.' $null $null $null $null $null $null
        New-Baseline 'B1-PERIODIC' 'Enabled' 'PILOT_FIXED; owner approval required.' $b1Interval $null $null $null $null $null
        New-Baseline 'B2-OBSERVED-PROXY' 'NotApplicable' 'No registered deployable scalar proxy exists.' $null $null $null $null $null $null
        New-Baseline 'B3-POSTERIOR-MEAN' 'Disabled' 'Predeclared disabled candidate value.' $null 'failure-oriented-state-v1' $b3Threshold $null $null $null
        New-Baseline 'B4-POSTERIOR-QUANTILE' 'Enabled' 'PILOT_FIXED; owner approval required.' $null 'failure-oriented-state-v1' $null $b4Quantile $b4Threshold $null
        New-Baseline 'B5-FIRST-PASSAGE-QUANTILE' 'Disabled' 'Predeclared disabled candidate value.' $null $null $null $null $null $b5Threshold
        New-Baseline 'B6-UNCONSTRAINED-BAYES' 'Enabled' 'Constraint-behavior diagnostic only; no chance constraint.' $null $null $null $null $null $null
        New-Baseline 'B7-CONSTRAINED-ONE-STAGE' 'Enabled' 'Primary no-information constrained ablation using common J, models and epsilon.' $null $null $null $null $null $null
    )
}

$workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rawSchemaPath = Join-Path $workspaceRoot 'schemas/raw-schema-registry.csv'
$experimentMatrixPath = Join-Path $workspaceRoot 'schemas/experiment-matrix.csv'
$semanticMapPath = Join-Path $workspaceRoot 'schemas/semantic-test-map.csv'
$tolerancePath = Join-Path $workspaceRoot 'schemas/numerical-tolerance-registry.csv'
$output = [ordered]@{
    SchemaVersion = 'output-v2'
    ConfigId = 'output.t5-planning-only.er05a.v2'
    ApprovedRoot = ([string]$outputV1.DestinationRoot).Replace('WearVision_ER06B_T5_PENDING_AUTHORIZATION', 'WearVision_ER06B_T5_PENDING_AUTHORIZATION')
    RunIdPattern = '{experiment_id}/{scenario_id}/{authorization_id}/{run_id}'
    RefuseExisting = $true
    RawFormat = [string]$outputV1.RawFormat
    ManifestFormat = [string]$outputV1.ManifestFormat
    LogFormat = 'NDJSON'
    SchemaRegistryVersion = 'ER04-raw-schema-registry-v1+ER05A-T5-planning-v1'
    SchemaRegistrySha256 = Get-FileHashHex $rawSchemaPath
    Culture = 'InvariantCulture'
    Encoding = 'UTF8_NO_BOM'
    NewlinePolicy = 'LF'
    CompressionEnabled = [bool]$outputV1.CompressionEnabled
    RetainRaw = $true
    RetainBatches = $true
    CompletionManifestLast = $true
    HashAlgorithm = 'SHA-256'
    StableRecordOrderVersion = 'canonical-tuple-er04-v1'
    ManuscriptRenderingEnabled = $false
}

$compatibilityHashes = [ordered]@{
    er05_source_snapshot = [string]$compatibilityV1.ComponentHashes.er05_source_snapshot
    er05a_source_snapshot = $Er05ASourceSnapshotSha256.ToUpperInvariant()
    experiment_matrix = Get-FileHashHex $experimentMatrixPath
    numerical_tolerance_registry = Get-FileHashHex $tolerancePath
    raw_schema_registry = Get-FileHashHex $rawSchemaPath
    semantic_test_map = Get-FileHashHex $semanticMapPath
}
$compatibility = [ordered]@{
    SchemaVersion = 'compatibility-v2'
    ConfigId = 'compatibility.er05a.t5-candidate.v2'
    ArchitectureVersion = [string]$compatibilityV1.ArchitectureVersion
    MathematicalModelVersion = [string]$compatibilityV1.MathematicalModelVersion
    ProtocolVersion = [string]$compatibilityV1.ProtocolVersion
    TargetFramework = 'net8.0'
    SdkVersion = '8.0.424'
    ComponentHashes = $compatibilityHashes
}

$oldCandidateZipSha256 = '245FF84EFE40F5B3E10505C8B395E94E73E58F8DC91288C3A7CDD75164F79C01'
$assignments = New-Object System.Collections.Generic.List[object]
foreach ($experimentId in @('T5-001', 'T5-002')) {
    $scenarioId = if ($experimentId -eq 'T5-001') { 'PILOT-NUMERICAL' } else { 'PILOT-PRECISION' }
    for ($index = 1; $index -le 128; $index++) {
        $episodeId = '{0}-EP-{1:D4}' -f $experimentId, $index
        $assignments.Add([ordered]@{
            EpisodeId = $episodeId
            SourceDatasetId = 'ER06A-MODEL-BASED-CANDIDATE-NO-DATASET'
            Partition = 'T5PilotOnly'
            GroupingKey = $episodeId
            Stratum = $scenarioId
            Reason = 'T5_PLANNING_ONLY_NOT_T6_NOT_MANUSCRIPT'
            ManifestVersion = 'split-er05a-t5-candidate-v2'
            SourceHash = $oldCandidateZipSha256
        })
    }
}
$split = [ordered]@{ SchemaVersion = 'split-v2'; ConfigId = 'split.t5-candidate.256.er05a.v2'; Assignments = $assignments }
$retirement = [ordered]@{ SchemaVersion = 'retirement-v2'; ConfigId = 'retirement.er05a.t5-candidate.v2'; PathFragments = @($retirementV1.PathFragments); Sha256Hashes = @($retirementV1.Sha256Hashes) }
$ownerDecisions = New-Object System.Collections.Generic.List[object]
foreach ($index in 1..12) {
    $id = 'A-{0:D2}' -f $index
    $notes = if ($index -eq 11) { 'ER-05A implementation addendum completed; this is not owner approval of pilot values.' } elseif ($index -eq 12) { 'NO / NOT AUTHORIZED.' } else { 'Owner decision required.' }
    $ownerDecisions.Add([ordered]@{ DecisionId = $id; State = 'NotApproved'; Notes = $notes })
}
$ownerApproval = [ordered]@{ SchemaVersion = 'owner-approval-v2'; ConfigId = 'owner-approval.er05a.draft.v2'; Decisions = $ownerDecisions; OverallState = 'NotApproved' }
$resources = [ordered]@{
    SchemaVersion = 'resources-v2'
    ConfigId = 'resources.er05a.owner-decision-required.v2'
    MaximumWallClockSeconds = New-RuntimeValue $null 'resource owner' 'OWNER_DECISION_REQUIRED'
    MaximumStorageBytes = New-RuntimeValue $null 'resource owner' 'OWNER_DECISION_REQUIRED'
    MaximumDegreeOfParallelism = New-RuntimeValue ([int]$numericalV1.MaximumDegreeOfParallelism) 'resource owner' 'ER06A_CANDIDATE_NOT_APPROVED'
    AllowEr04MaximumPaths = New-RuntimeValue $null 'resource owner' 'OWNER_DECISION_REQUIRED'
}

$coreFiles = [ordered]@{
    'model_config_v2.json' = $model
    'reliability_config_v2.json' = $reliability
    'action_config_v2.json' = $actions
    'consequence_config_v2.json' = $consequences
    'numerical_config_v2.json' = $numerical
    'baseline_config_v2.json' = $baselines
    'output_policy_v2.json' = $output
    'compatibility_manifest_v2.json' = $compatibility
    'split_manifest_v2.json' = $split
    'retirement_denylist.json' = $retirement
    'owner_approval_manifest.json' = $ownerApproval
    'resource_ceiling_config.json' = $resources
}
foreach ($item in $coreFiles.GetEnumerator()) { Write-Json $item.Key $item.Value }

$splitHash = Get-FileHashHex (Join-Path $destination 'split_manifest_v2.json')
$matrixHash = Get-FileHashHex $experimentMatrixPath
function New-EffectiveExperiment([string]$experimentId, [string]$scenarioId, [string[]]$factorIds, [string]$outputSubroot) {
    [ordered]@{
        SchemaVersion = 'effective-experiment-v2'
        ConfigId = "effective.$experimentId.er05a.v2"
        ExperimentId = [ordered]@{ Value = $experimentId }
        ScenarioId = [ordered]@{ Value = $scenarioId }
        Stage = 'T5'
        ProfileKind = 'T5Pilot'
        ProtocolVersion = 'ER04-2026-08-20'
        ExperimentMatrixSha256 = $matrixHash
        FactorLevelSetIds = $factorIds
        FactorLevelStatus = 'MethodologicalStress'
        PolicyIds = @('P-R4-TWO-STAGE', 'B7-CONSTRAINED-ONE-STAGE', 'B4-POSTERIOR-QUANTILE', 'B1-PERIODIC', 'B6-UNCONSTRAINED-BAYES')
        BaselineIds = @('B7-CONSTRAINED-ONE-STAGE', 'B4-POSTERIOR-QUANTILE', 'B1-PERIODIC', 'B6-UNCONSTRAINED-BAYES')
        AblationIds = @()
        PrimaryContrastIds = @('P-R4-vs-B7', 'P-R4-vs-B4', 'P-R4-vs-B1')
        SplitManifestId = 'split.t5-candidate.256.er05a.v2'
        SplitManifestSha256 = $splitHash
        EpisodeCount = 128
        EpisodeCountStatus = 'T5PrecisionPilot'
        Partition = 'T5PilotOnly'
        CalibrationStrategyId = 'methodological-pilot-no-calibration-claims'
        CensoringStrategyId = [string]$reliabilityV1.CensoringStrategyId.Value
        BootstrapAnalysisId = 'trajectory-cluster-bootstrap-planning-only-v1'
        MultiplicityAnalysisId = 'holm-primary-three-planning-only-v1'
        CommonRandomNumberCouplingVersion = 'sha256-xoshiro256starstar-crn-v1'
        SyntheticOrModelBasedLabel = 'MODEL_BASED_T5_PLANNING_ONLY_NOT_SCIENTIFIC_RESULT'
        RealDataAdapterId = $null
        OutputSubroot = $outputSubroot
        AuthorizationEligible = $true
        PracticalEffectMargin = New-RuntimeValue $null 'stakeholder/statistics owner' 'T6_ONLY_INPUT_NOT_REQUIRED_FOR_T5'
        RareEventHalfWidth = New-RuntimeValue $null 'statistics owner' 'T6_ONLY_INPUT_NOT_REQUIRED_FOR_T5'
    }
}

$effective1 = New-EffectiveExperiment 'T5-001' 'PILOT-NUMERICAL' @('MINIMUM-T5', 'INFORMATIVE-MONITOR', 'NUMERICAL-CONVERGENCE-PLANNING') 'T5-001-PILOT-NUMERICAL'
$effective2 = New-EffectiveExperiment 'T5-002' 'PILOT-PRECISION' @('MINIMUM-T5', 'MODERATE-NONINFORMATIVE-MISSINGNESS', 'PAIRED-PRECISION-PREVALENCE-PLANNING') 'T5-002-PILOT-PRECISION'
Write-Json 'T5-001_effective_config.json' $effective1
Write-Json 'T5-002_effective_config.json' $effective2
$effectiveHash1 = Get-FileHashHex (Join-Path $destination 'T5-001_effective_config.json')
$effectiveHash2 = Get-FileHashHex (Join-Path $destination 'T5-002_effective_config.json')
$experimentSet = [ordered]@{
    SchemaVersion = 'experiment-set-v2'
    ConfigId = 'experiment-set.t5-candidate.er05a.v2'
    Stage = 'T5'
    ProfileKind = 'T5Pilot'
    ProtocolVersion = 'ER04-2026-08-20'
    Runs = @(
        [ordered]@{ ExperimentId = [ordered]@{ Value = 'T5-001' }; ScenarioId = [ordered]@{ Value = 'PILOT-NUMERICAL' }; EffectiveConfigPath = 'T5-001_effective_config.json'; EffectiveConfigSha256 = $effectiveHash1; OutputSubroot = 'T5-001-PILOT-NUMERICAL'; AuthorizationEligible = $true },
        [ordered]@{ ExperimentId = [ordered]@{ Value = 'T5-002' }; ScenarioId = [ordered]@{ Value = 'PILOT-PRECISION' }; EffectiveConfigPath = 'T5-002_effective_config.json'; EffectiveConfigSha256 = $effectiveHash2; OutputSubroot = 'T5-002-PILOT-PRECISION'; AuthorizationEligible = $true }
    )
}
Write-Json 'experiment_set_manifest.json' $experimentSet

$roles = [ordered]@{
    'model_config_v2.json' = 'model'
    'reliability_config_v2.json' = 'reliability'
    'action_config_v2.json' = 'actions'
    'consequence_config_v2.json' = 'consequences'
    'numerical_config_v2.json' = 'numerical'
    'baseline_config_v2.json' = 'baselines'
    'output_policy_v2.json' = 'output-policy'
    'compatibility_manifest_v2.json' = 'compatibility'
    'split_manifest_v2.json' = 'split'
    'retirement_denylist.json' = 'retirement'
    'owner_approval_manifest.json' = 'owner-approval'
    'resource_ceiling_config.json' = 'resources'
    'experiment_set_manifest.json' = 'experiment-set'
    'T5-001_effective_config.json' = 'effective-experiment'
    'T5-002_effective_config.json' = 'effective-experiment'
}
foreach ($artifact in $registeredArtifacts) { $roles[$artifact.RelativePath] = $artifact.Role }
$fileReferences = New-Object System.Collections.Generic.List[object]
foreach ($relativePath in ($roles.Keys | Sort-Object)) {
    $fileReferences.Add([ordered]@{ RelativePath = $relativePath; Sha256 = Get-FileHashHex (Join-Path $destination $relativePath); Role = $roles[$relativePath] })
}
$manifest = [ordered]@{
    SchemaVersion = 'config-set-v2'
    ConfigSetId = 'ER05A-1152-T5-PILOT-CANDIDATE-V2'
    ProfileKind = 'T5Pilot'
    ScientificUse = 'METHODOLOGICAL_PILOT_CONFIGURATION_NOT_APPROVED'
    AuthorizedStage = 'T5'
    CanonicalizationVersion = 'canonical-json-v1-utf8-no-bom'
    ApprovalStatus = 'NotApproved'
    OwnerId = 'OWNER_APPROVAL_REQUIRED'
    CreatedById = 'ER05A_ADMIN_CONVERSION'
    UnresolvedPlaceholders = @('A-01', 'A-02', 'A-03', 'A-04', 'A-05', 'A-06', 'A-07', 'A-08', 'A-09', 'A-10', 'MAX_WALL_CLOCK', 'MAX_STORAGE', 'ALLOW_ER04_N_MAX', 'T5_AUTHORIZATION')
    Files = $fileReferences
}
Write-Json 'config_set_manifest.json' $manifest
$configSetHash = Get-FileHashHex (Join-Path $destination 'config_set_manifest.json')
$outputPolicyHash = Get-FileHashHex (Join-Path $destination 'output_policy_v2.json')

$grant = [ordered]@{
    SchemaVersion = 't5-authorization-v1'
    AuthorizationId = 'ER05A-DRAFT-T5-AUTHORIZATION-NOT-APPROVED'
    State = 'Draft'
    ConfigSetSha256 = $configSetHash
    Experiments = @(
        [ordered]@{ ExperimentId = [ordered]@{ Value = 'T5-001' }; EffectiveConfigSha256 = $effectiveHash1 },
        [ordered]@{ ExperimentId = [ordered]@{ Value = 'T5-002' }; EffectiveConfigSha256 = $effectiveHash2 }
    )
    ProtocolVersion = 'ER04-2026-08-20'
    Stage = 'T5'
    OutputPolicySha256 = $outputPolicyHash
    ApproverId = 'OWNER_APPROVER_REQUIRED'
    ExpiresAtUtc = $null
}
Write-Json 't5_authorization_manifest.json' $grant

$review = [ordered]@{
    SchemaVersion = 'er05a-t5-candidate-review-v2'
    CandidateStatus = 'DRAFT_OWNER_APPROVAL_REQUIRED_NOT_AUTHORIZED_FOR_EXECUTION'
    SourceEr06ACandidateZipSha256 = $oldCandidateZipSha256
    Er05ASourceSnapshotSha256 = $Er05ASourceSnapshotSha256.ToUpperInvariant()
    ConfigSetSha256 = $configSetHash
    ProfileKind = 'T5Pilot'
    Stage = 'T5'
    EffectiveRows = @('T5-001/PILOT-NUMERICAL', 'T5-002/PILOT-PRECISION')
    EpisodeCount = [ordered]@{ T5_001 = 128; T5_002 = 128; Total = 256; Status = 'T5_PLANNING_ONLY_NOT_T6_N' }
    RuntimeValueStatus = 'MethodologicalPilotNotApproved'
    OwnerApproval = 'NOT_APPROVED'
    ResourceCeilings = 'OWNER_DECISION_REQUIRED'
    AuthorizationGrant = 'DRAFT_NOT_APPROVED'
    ImplementationGapBlockers = @()
    T5PilotExecuted = $false
    ScientificResultGenerated = $false
    T6ReuseAllowed = $false
    ManuscriptEligible = $false
}
Write-Json 'candidate_review_manifest.json' $review

$validateOutput = @(& $DotnetPath $ExperimentsAssemblyPath config validate $destination 2>&1)
$validateExit = $LASTEXITCODE
if ($validateExit -ne 0) {
    throw "Candidate config validate failed with exit $validateExit`: $($validateOutput -join [Environment]::NewLine)"
}
$preflightOutput = @(& $DotnetPath $ExperimentsAssemblyPath preflight $destination 2>&1)
$preflightExit = $LASTEXITCODE
if ($preflightExit -ne 4) {
    throw "Candidate preflight did not stop at the expected gate. Exit=$preflightExit Output=$($preflightOutput -join [Environment]::NewLine)"
}
$preflightJson = ($preflightOutput -join '')
$preflightReport = $preflightJson | ConvertFrom-Json
if (-not $preflightReport.RuntimeSchemaCompatible -or -not $preflightReport.ModelBindingCompatible -or -not $preflightReport.BaselineBindingCompatible -or -not $preflightReport.MultiRowBindingCompatible -or -not $preflightReport.PlanningHarnessCompatible -or $preflightReport.ExecutionStarted) {
    throw 'Candidate preflight contains an implementation compatibility failure or execution start.'
}
$issueCodes = @($preflightReport.Issues | ForEach-Object { $_.Code })
$expectedIssues = @('OWNER_APPROVAL_REQUIRED', 'RESOURCE_CEILING_OWNER_DECISION_REQUIRED', 'T5_AUTHORIZATION_NOT_APPROVED')
if (@($issueCodes | Where-Object { $_ -notin $expectedIssues }).Count -ne 0 -or @($expectedIssues | Where-Object { $_ -notin $issueCodes }).Count -ne 0) {
    throw "Candidate preflight blocker set is not exact: $($issueCodes -join ',')"
}
Write-Text 'STATIC_PREFLIGHT.json' $preflightJson

$hashRows = New-Object System.Collections.Generic.List[string]
$hashRows.Add('relative_path,sha256,bytes,manifest_scope')
foreach ($file in (Get-ChildItem -LiteralPath $destination -File -Recurse | Where-Object { $_.Name -ne 'package_hash_manifest.csv' } | Sort-Object FullName)) {
    $relative = [System.IO.Path]::GetRelativePath($destination, $file.FullName).Replace('\', '/')
    $hashRows.Add("$relative,$(Get-FileHashHex $file.FullName),$($file.Length),ALL_FILES_EXCEPT_SELF")
}
Write-Text 'package_hash_manifest.csv' ($hashRows -join "`n")

$result = [ordered]@{
    Destination = $destination
    ConfigSetSha256 = $configSetHash
    ValidateExitCode = $validateExit
    PreflightExitCode = $preflightExit
    PreflightIssueCodes = $issueCodes
    FileCount = (Get-ChildItem -LiteralPath $destination -File -Recurse).Count
    T5PilotExecuted = $false
}
$result | ConvertTo-Json -Compress
