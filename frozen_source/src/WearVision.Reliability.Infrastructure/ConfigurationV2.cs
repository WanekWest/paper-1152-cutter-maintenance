using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public enum RuntimeValueStatus
{
    TestFixtureOnly,
    MethodologicalPilotNotApproved,
    OwnerApproved,
    Calibrated
}

public enum OwnerApprovalState
{
    NotApproved,
    Approved,
    Rejected
}

public enum AuthorizationGrantState
{
    Draft,
    Approved,
    Revoked
}

public enum EpisodeCountStatus
{
    FixtureFixed,
    T5PrecisionPilot,
    T6ProspectivelyFrozen
}

public enum FactorLevelStatus
{
    TestFixtureOnly,
    MethodologicalStress,
    OwnerDefined,
    Calibrated
}

public enum BaselineApplicabilityStatus
{
    Enabled,
    Disabled,
    NotApplicable
}

public sealed record RuntimeValue<T>(T Value, RuntimeValueStatus Status, string OwnerId, string EvidenceId);
public sealed record ArtifactIdentityV2(string ArtifactId, string Version, string Sha256, RuntimeValueStatus Status, string OwnerId, string EvidenceId);
public sealed record ConfigFileReferenceV2(string RelativePath, string Sha256, string Role);

public sealed record ConfigSetManifestV2(
    string SchemaVersion,
    string ConfigSetId,
    ConfigurationProfileKind ProfileKind,
    string ScientificUse,
    RunStage AuthorizedStage,
    string CanonicalizationVersion,
    OwnerApprovalState ApprovalStatus,
    string OwnerId,
    string CreatedById,
    IReadOnlyList<string> UnresolvedPlaceholders,
    IReadOnlyList<ConfigFileReferenceV2> Files);

public sealed record AffineLimitStateBindingConfigV2(
    string FactoryTypeId,
    double Boundary,
    string MarginUnitId,
    string MarginUnitDescription,
    string StateSpaceId,
    string ExposureClockId,
    ArtifactIdentityV2 Artifact);

public sealed record WeightedSupportBeliefBindingConfigV2(
    string FactoryTypeId,
    IReadOnlyList<double> SupportStates,
    IReadOnlyList<double> Weights,
    string StateSpaceId,
    ArtifactIdentityV2 Artifact);

public sealed record ExposureAwareTransitionBindingConfigV2(
    string FactoryTypeId,
    double MaximumIncrementRatePerUnitExposure,
    string ExposureScalingSemantics,
    string ExposureClockId,
    string StateSpaceId,
    ArtifactIdentityV2 Artifact);

public sealed record MissingnessBindingConfigV2(
    MissingObservationMode Mode,
    double ProbabilityLowState,
    double ProbabilityHighState,
    string QualityId);

public sealed record BinaryObservationBindingConfigV2(
    string FactoryTypeId,
    bool Enabled,
    ObservationKind ObservationKind,
    MaintenanceActionKind ActionKind,
    double StateThreshold,
    double ProbabilityOneLowState,
    double ProbabilityOneHighState,
    MissingnessBindingConfigV2 Missingness,
    string StateSpaceId,
    ArtifactIdentityV2 Artifact);

public sealed record TwoPointResetBindingConfigV2(
    string FactoryTypeId,
    IReadOnlyList<double> SupportStates,
    IReadOnlyList<double> Probabilities,
    string StateSpaceId,
    ArtifactIdentityV2 Artifact);

public sealed record RuntimeModelConfigV2(
    string SchemaVersion,
    string ConfigId,
    ConfigurationProfileKind ProfileKind,
    string StateSpaceId,
    string StateSpaceVersion,
    string StateUnitMetadata,
    RuntimeValue<string> ExposureClockId,
    RuntimeValue<string> ExposureUnit,
    bool EventJumpsEnabled,
    AffineLimitStateBindingConfigV2 LimitState,
    WeightedSupportBeliefBindingConfigV2 Belief,
    ExposureAwareTransitionBindingConfigV2 Transition,
    BinaryObservationBindingConfigV2 RoutineObservation,
    BinaryObservationBindingConfigV2 MonitorObservation,
    BinaryObservationBindingConfigV2 InspectionObservation,
    TwoPointResetBindingConfigV2 Reset);

public sealed record ReliabilityConfigV2(
    string SchemaVersion,
    string ConfigId,
    RuntimeValue<double> Horizon,
    RuntimeValue<double> Epsilon,
    RuntimeValue<double> EpsilonRemaining,
    RuntimeValue<string> FailureConfirmationRuleId,
    RuntimeValue<string> CensoringStrategyId,
    string ExposureClockId);

public sealed record ActionDefinitionConfigV2(
    MaintenanceActionKind Action,
    bool Enabled,
    RuntimeValue<double> Duration,
    string ExposureClockId,
    InspectionExposureMode? ExposureMode,
    string BurdenComponentId);

public sealed record ActionConfigV2(
    string SchemaVersion,
    string ConfigId,
    ArtifactIdentityV2 AdmissibilityRule,
    IReadOnlyList<ActionDefinitionConfigV2> Actions);

public sealed record ConsequenceComponentConfigV2(
    ConsequenceComponentKind Component,
    bool Enabled,
    string Definition,
    string NormalizationMethod,
    RuntimeValue<double> Normalizer,
    string UnitOrNormalizationDescriptor);

public sealed record ConsequenceConfigV2(
    string SchemaVersion,
    string ConfigId,
    string DefinitionVersion,
    IReadOnlyList<ConsequenceComponentConfigV2> Components,
    string WeightProfileId,
    string WeightProfileVersion,
    WeightProfileStatus WeightProfileStatus,
    RuntimeValue<IReadOnlyDictionary<ConsequenceComponentKind, double>> Weights,
    string WeightProfileSha256);

public sealed record NumericalConfigV2(
    string SchemaVersion,
    string ConfigId,
    string FloatingPointType,
    string RngAlgorithmId,
    ulong MasterSeedUInt64,
    int McMinimumPaths,
    int McBatchSize,
    int McMaximumPaths,
    int StableBatchCount,
    string FailureIntervalMethod,
    string ObjectiveConvergenceRuleId,
    string CrossingRefinementRuleId,
    double QuadratureAbsoluteTolerance,
    double QuadratureRelativeTolerance,
    string NormalizationToleranceId,
    string ProbabilityToleranceId,
    string WeightSimplexToleranceId,
    string DeterministicTieToleranceId,
    string LogScoreClippingId,
    int MaximumDegreeOfParallelism,
    string ParallelismLevel,
    string StableReductionVersion,
    int CancellationCheckInterval,
    string DiagnosticVerbosity,
    string ToleranceRegistrySha256);

public sealed record BaselineDefinitionConfigV2(
    string BaselineId,
    BaselineApplicabilityStatus Applicability,
    string ImplementationVersion,
    DataPartition TuningPartition,
    string DesignArtifactId,
    string DesignArtifactSha256,
    IReadOnlyList<string> AllowedInformationFields,
    string ApplicabilityReason,
    double? Interval,
    double? ProxyThreshold,
    string? ScalarMappingId,
    double? MeanThreshold,
    double? QuantileLevel,
    double? QuantileThreshold,
    double? FirstPassageQuantile,
    double? FirstPassageHorizonThreshold);

public sealed record BaselineConfigV2(
    string SchemaVersion,
    string ConfigId,
    string ExposureClockId,
    string ConsequenceDefinitionVersion,
    string ReliabilityModelVersion,
    IReadOnlyList<BaselineDefinitionConfigV2> Baselines);

public sealed record OutputPolicyV2(
    string SchemaVersion,
    string ConfigId,
    string ApprovedRoot,
    string RunIdPattern,
    bool RefuseExisting,
    string RawFormat,
    string ManifestFormat,
    string LogFormat,
    string SchemaRegistryVersion,
    string SchemaRegistrySha256,
    string Culture,
    string Encoding,
    string NewlinePolicy,
    bool CompressionEnabled,
    bool RetainRaw,
    bool RetainBatches,
    bool CompletionManifestLast,
    string HashAlgorithm,
    string StableRecordOrderVersion,
    bool ManuscriptRenderingEnabled);

public sealed record CompatibilityManifestV2(
    string SchemaVersion,
    string ConfigId,
    string ArchitectureVersion,
    string MathematicalModelVersion,
    string ProtocolVersion,
    string TargetFramework,
    string SdkVersion,
    IReadOnlyDictionary<string, string> ComponentHashes);

public sealed record SplitAssignmentV2(
    string EpisodeId,
    string SourceDatasetId,
    DataPartition Partition,
    string GroupingKey,
    string Stratum,
    string Reason,
    string ManifestVersion,
    string SourceHash);

public sealed record SplitManifestV2(string SchemaVersion, string ConfigId, IReadOnlyList<SplitAssignmentV2> Assignments);
public sealed record RetirementDenyListV2(string SchemaVersion, string ConfigId, IReadOnlyList<string> PathFragments, IReadOnlyList<string> Sha256Hashes);

public sealed record OwnerDecisionStatusV2(string DecisionId, OwnerApprovalState State, string Notes);
public sealed record OwnerApprovalManifestV2(string SchemaVersion, string ConfigId, IReadOnlyList<OwnerDecisionStatusV2> Decisions, OwnerApprovalState OverallState);

public sealed record ResourceCeilingConfigV2(
    string SchemaVersion,
    string ConfigId,
    RuntimeValue<double?> MaximumWallClockSeconds,
    RuntimeValue<long?> MaximumStorageBytes,
    RuntimeValue<int> MaximumDegreeOfParallelism,
    RuntimeValue<bool?> AllowEr04MaximumPaths);

public sealed record EffectiveExperimentConfigV2(
    string SchemaVersion,
    string ConfigId,
    ExperimentId ExperimentId,
    ScenarioId ScenarioId,
    RunStage Stage,
    ConfigurationProfileKind ProfileKind,
    string ProtocolVersion,
    string ExperimentMatrixSha256,
    IReadOnlyList<string> FactorLevelSetIds,
    FactorLevelStatus FactorLevelStatus,
    IReadOnlyList<string> PolicyIds,
    IReadOnlyList<string> BaselineIds,
    IReadOnlyList<string> AblationIds,
    IReadOnlyList<string> PrimaryContrastIds,
    string SplitManifestId,
    string SplitManifestSha256,
    int EpisodeCount,
    EpisodeCountStatus EpisodeCountStatus,
    DataPartition Partition,
    string CalibrationStrategyId,
    string CensoringStrategyId,
    string BootstrapAnalysisId,
    string MultiplicityAnalysisId,
    string CommonRandomNumberCouplingVersion,
    string SyntheticOrModelBasedLabel,
    string? RealDataAdapterId,
    string OutputSubroot,
    bool AuthorizationEligible,
    RuntimeValue<double?> PracticalEffectMargin,
    RuntimeValue<double?> RareEventHalfWidth,
    T6EffectiveRowBindingV1? T6Binding = null);

public sealed record ExperimentRunReferenceV2(
    ExperimentId ExperimentId,
    ScenarioId ScenarioId,
    string EffectiveConfigPath,
    string EffectiveConfigSha256,
    string OutputSubroot,
    bool AuthorizationEligible);

public sealed record ExperimentSetManifestV2(
    string SchemaVersion,
    string ConfigId,
    RunStage Stage,
    ConfigurationProfileKind ProfileKind,
    string ProtocolVersion,
    IReadOnlyList<ExperimentRunReferenceV2> Runs);

public sealed record AuthorizedExperimentReferenceV2(ExperimentId ExperimentId, string EffectiveConfigSha256);
public sealed record T5AuthorizationGrantV2(
    string SchemaVersion,
    string AuthorizationId,
    AuthorizationGrantState State,
    string ConfigSetSha256,
    IReadOnlyList<AuthorizedExperimentReferenceV2> Experiments,
    string ProtocolVersion,
    RunStage Stage,
    string OutputPolicySha256,
    string ApproverId,
    DateTimeOffset? ExpiresAtUtc);

public enum T5ExecutionAuthorizationScope
{
    TestFixtureOnly,
    Er06BRetryT5Only
}

public sealed record T5SourceBoundAuthorizationV2(
    string SchemaVersion,
    string AuthorizationId,
    AuthorizationGrantState State,
    T5ExecutionAuthorizationScope Scope,
    string ConfigSetSha256,
    IReadOnlyList<AuthorizedExperimentReferenceV2> Experiments,
    string ProtocolVersion,
    RunStage Stage,
    string OutputPolicySha256,
    string SourceSnapshotSha256,
    string SourceSnapshotFileName,
    string ExecutableSha256,
    string ExecutableFileName,
    string ApproverId,
    string ParentAuthorizationId,
    DateTimeOffset? ExpiresAtUtc);

public sealed record T5ExecutionIdentity(
    string SourceSnapshotPath,
    string SourceSnapshotSha256,
    string ExecutablePath,
    string ExecutableSha256)
{
    public static T5ExecutionIdentity Capture(string sourceSnapshotPath, string executablePath)
    {
        var source = Path.GetFullPath(sourceSnapshotPath);
        var executable = Path.GetFullPath(executablePath);
        if (!File.Exists(source)) throw new FileNotFoundException("Source snapshot is missing.", source);
        if (!File.Exists(executable)) throw new FileNotFoundException("Production executable is missing.", executable);
        return new(source, ConfigurationSetLoaderV2.HashFile(source), executable, ConfigurationSetLoaderV2.HashFile(executable));
    }
}

public sealed record LoadedConfigurationSetV2(
    string Root,
    string ConfigSetSha256,
    ConfigSetManifestV2 Manifest,
    RuntimeModelConfigV2 Model,
    ReliabilityConfigV2 Reliability,
    ActionConfigV2 Actions,
    ConsequenceConfigV2 Consequences,
    NumericalConfigV2 Numerical,
    BaselineConfigV2 Baselines,
    OutputPolicyV2 Output,
    CompatibilityManifestV2 Compatibility,
    SplitManifestV2 Split,
    RetirementDenyListV2 Retirement,
    OwnerApprovalManifestV2 OwnerApproval,
    ResourceCeilingConfigV2 Resources,
    ExperimentSetManifestV2 ExperimentSet,
    IReadOnlyDictionary<ExperimentId, EffectiveExperimentConfigV2> EffectiveExperiments);

public sealed class ConfigurationSetLoaderV2
{
    private static readonly HashSet<string> AllowedPackageMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "config_set_manifest.json",
        "candidate_review_manifest.json",
        "t5_authorization_manifest.json",
        "package_hash_manifest.csv",
        "STATIC_PREFLIGHT.json"
    };

    public LoadedConfigurationSetV2 Load(string directory)
    {
        try { return LoadCore(directory); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException) { throw; }
        catch (Exception exception) when (exception is NullReferenceException or ArgumentException)
        {
            throw new InvalidDataException("Required V2 configuration field is missing or invalid.", exception);
        }
    }

    private static LoadedConfigurationSetV2 LoadCore(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifestPath = Contained(root, "config_set_manifest.json");
        var manifest = Read<ConfigSetManifestV2>(manifestPath);
        if (manifest.SchemaVersion != "config-set-v2") throw new InvalidDataException("Unsupported V2 config-set schema.");
        if (manifest.ProfileKind is not (ConfigurationProfileKind.TestFixtureOnly or ConfigurationProfileKind.T5Pilot or ConfigurationProfileKind.T6Scientific))
            throw new InvalidDataException("Unknown profile kind.");
        if (manifest.ProfileKind == ConfigurationProfileKind.T5Pilot && manifest.AuthorizedStage != RunStage.T5)
            throw new InvalidDataException("T5Pilot profile must declare T5 as its stage.");
        var references = manifest.Files.ToDictionary(x => NormalizeRelative(x.RelativePath), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in references)
        {
            var path = Contained(root, pair.Key);
            if (!File.Exists(path)) throw new InvalidDataException($"Configuration file is missing: {pair.Key}");
            ValidateSha256(pair.Value.Sha256, $"manifest:{pair.Key}");
            if (!HashFile(path).Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Configuration file hash mismatch: {pair.Key}");
        }
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelative(Path.GetRelativePath(root, file));
            if (!references.ContainsKey(relative) && !AllowedPackageMetadata.Contains(relative))
                throw new InvalidDataException($"Unregistered package file: {relative}");
        }

        var model = ReadReferenced<RuntimeModelConfigV2>(root, references, "model");
        var reliability = ReadReferenced<ReliabilityConfigV2>(root, references, "reliability");
        var actions = ReadReferenced<ActionConfigV2>(root, references, "actions");
        var consequences = ReadReferenced<ConsequenceConfigV2>(root, references, "consequences");
        var numerical = ReadReferenced<NumericalConfigV2>(root, references, "numerical");
        var baselines = ReadReferenced<BaselineConfigV2>(root, references, "baselines");
        var output = ReadReferenced<OutputPolicyV2>(root, references, "output-policy");
        var compatibility = ReadReferenced<CompatibilityManifestV2>(root, references, "compatibility");
        var split = ReadReferenced<SplitManifestV2>(root, references, "split");
        var retirement = ReadReferenced<RetirementDenyListV2>(root, references, "retirement");
        var approval = ReadReferenced<OwnerApprovalManifestV2>(root, references, "owner-approval");
        var resources = ReadReferenced<ResourceCeilingConfigV2>(root, references, "resources");
        var experimentSet = ReadReferenced<ExperimentSetManifestV2>(root, references, "experiment-set");
        if (model.ProfileKind != manifest.ProfileKind || experimentSet.ProfileKind != manifest.ProfileKind || experimentSet.Stage != manifest.AuthorizedStage)
            throw new InvalidDataException("Configuration profile/stage mismatch.");
        var effective = new Dictionary<ExperimentId, EffectiveExperimentConfigV2>();
        foreach (var run in experimentSet.Runs)
        {
            var normalized = NormalizeRelative(run.EffectiveConfigPath);
            if (!references.TryGetValue(normalized, out var fileReference) || fileReference.Role != "effective-experiment")
                throw new InvalidDataException($"Effective experiment is not registered: {normalized}");
            if (!fileReference.Sha256.Equals(run.EffectiveConfigSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Effective experiment hash chain mismatch: {run.ExperimentId}");
            var item = Read<EffectiveExperimentConfigV2>(Contained(root, normalized));
            if (item.ExperimentId != run.ExperimentId || item.ScenarioId != run.ScenarioId || item.ProfileKind != manifest.ProfileKind || item.Stage != manifest.AuthorizedStage)
                throw new InvalidDataException($"Effective experiment identity mismatch: {run.ExperimentId}");
            if (!effective.TryAdd(item.ExperimentId, item)) throw new InvalidDataException($"Duplicate experiment ID: {item.ExperimentId}");
        }
        if (effective.Count == 0) throw new InvalidDataException("Experiment set is empty.");
        var setHash = HashFile(manifestPath);
        return new(root, setHash, manifest, model, reliability, actions, consequences, numerical, baselines, output, compatibility, split, retirement, approval, resources, experimentSet, effective);
    }

    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static T ReadReferenced<T>(string root, IReadOnlyDictionary<string, ConfigFileReferenceV2> references, string role) where T : notnull
    {
        var match = references.Values.Where(x => x.Role == role).ToArray();
        if (match.Length != 1) throw new InvalidDataException($"Expected exactly one configuration file for role {role}.");
        return Read<T>(Contained(root, match[0].RelativePath));
    }

    private static T Read<T>(string path) where T : notnull => CanonicalJson.DeserializeStrict<T>(File.ReadAllText(path, Encoding.UTF8));

    private static string Contained(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Configuration paths must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Configuration path escapes the set root.");
        return path;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/');

    private static void ValidateSha256(string value, string name)
    {
        if (value.Length != 64 || value.Any(x => !Uri.IsHexDigit(x))) throw new InvalidDataException($"Invalid SHA-256 for {name}.");
    }
}

public sealed record ScalarReferenceModelBundle(
    IFunctionalLimitState<double> LimitState,
    IDegradationBelief<double> Belief,
    IStateTransitionKernel<double> Transition,
    IObservationKernel<double, DiscreteObservation> RoutineObservation,
    IObservationKernel<double, DiscreteObservation> MonitorObservation,
    IObservationKernel<double, DiscreteObservation>? InspectionObservation,
    IResetKernel<double> Reset,
    StateConditionedBinaryBranchProvider MonitorBranchProvider,
    StateConditionedBinaryBranchProvider? InspectionBranchProvider);

public sealed class ScalarReferenceModelFactory
{
    public ScalarReferenceModelBundle Bind(RuntimeModelConfigV2 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.ExposureClockId is null || config.ExposureUnit is null || config.LimitState is null || config.Belief is null || config.Transition is null ||
            config.RoutineObservation is null || config.MonitorObservation is null || config.InspectionObservation is null || config.Reset is null ||
            config.LimitState.Artifact is null || config.Belief.Artifact is null || config.Transition.Artifact is null || config.RoutineObservation.Artifact is null ||
            config.MonitorObservation.Artifact is null || config.InspectionObservation.Artifact is null || config.Reset.Artifact is null ||
            config.RoutineObservation.Missingness is null || config.MonitorObservation.Missingness is null || config.InspectionObservation.Missingness is null)
            throw new InvalidDataException("Required reference-model binding field is missing.");
        if (config.EventJumpsEnabled) throw new InvalidDataException("Minimum R4 forbids event jumps.");
        var stateSpace = new StateSpaceDescriptor(config.StateSpaceId, config.StateSpaceVersion, config.StateUnitMetadata);
        ValidateStateSpace(config);
        ValidateArtifact(config.LimitState.Artifact, ArtifactPayloads.LimitState(config.LimitState));
        ValidateArtifact(config.Belief.Artifact, ArtifactPayloads.Belief(config.Belief));
        ValidateArtifact(config.Transition.Artifact, ArtifactPayloads.Transition(config.Transition));
        ValidateArtifact(config.RoutineObservation.Artifact, ArtifactPayloads.Observation(config.RoutineObservation));
        ValidateArtifact(config.MonitorObservation.Artifact, ArtifactPayloads.Observation(config.MonitorObservation));
        ValidateArtifact(config.InspectionObservation.Artifact, ArtifactPayloads.Observation(config.InspectionObservation));
        ValidateArtifact(config.Reset.Artifact, ArtifactPayloads.Reset(config.Reset));
        if (config.LimitState.FactoryTypeId != "AffineScalarLimitStateV1" || config.Belief.FactoryTypeId != "WeightedSupportBeliefV1" ||
            config.Transition.FactoryTypeId != "ExposureScaledUniformRateV1" || config.Reset.FactoryTypeId != "TwoPointScalarResetV1")
            throw new InvalidDataException("Unknown reference-model factory ID.");
        if (config.Transition.ExposureScalingSemantics != "UNIFORM_RATE_PER_SEGMENT_TIMES_DELTA_EXPOSURE")
            throw new InvalidDataException("Unknown exposure-scaling semantics.");
        ValidateSupport(config.Belief.SupportStates, config.Belief.Weights, "belief");
        ValidateSupport(config.Reset.SupportStates, config.Reset.Probabilities, "reset");
        if (config.Reset.SupportStates.Count != 2) throw new InvalidDataException("Two-point reset requires exactly two states.");
        var belief = new ImmutableWeightedSampleBelief<double>(
            new(config.Belief.Artifact.Version),
            stateSpace,
            new(config.Belief.Artifact.Sha256),
            config.Belief.SupportStates.Select((state, index) => new WeightedState<double>(state, config.Belief.Weights[index])).ToArray());
        if (!belief.Validate().IsValid) throw new InvalidDataException("Bound weighted belief is invalid.");
        var limit = new AffineScalarLimitState(config.LimitState.Boundary, config.LimitState.Artifact.Version, new(config.LimitState.MarginUnitId, config.LimitState.MarginUnitDescription), config.LimitState.Artifact.EvidenceId);
        var transition = new ExposureScaledUniformRateTransition(config.Transition.MaximumIncrementRatePerUnitExposure, config.Transition.Artifact.Version, stateSpace, config.Transition.ExposureClockId);
        var routine = CreateObservation(config.RoutineObservation, stateSpace);
        var monitor = CreateObservation(config.MonitorObservation, stateSpace);
        StateConditionedBinaryObservationKernel? inspection = config.InspectionObservation.Enabled ? CreateObservation(config.InspectionObservation, stateSpace) : null;
        var reset = new TwoPointScalarResetKernel(config.Reset.SupportStates[0], config.Reset.SupportStates[1], config.Reset.Probabilities[0], config.Reset.Artifact.Version, stateSpace);
        return new(limit, belief, transition, routine, monitor, inspection, reset, new(monitor), inspection is null ? null : new(inspection));
    }

    private static StateConditionedBinaryObservationKernel CreateObservation(BinaryObservationBindingConfigV2 config, StateSpaceDescriptor stateSpace)
    {
        if (config.FactoryTypeId != "StateConditionedBinaryObservationV1") throw new InvalidDataException("Unknown observation factory ID.");
        return new(config.Artifact.Version, config.ObservationKind, stateSpace, config.StateThreshold, config.ProbabilityOneLowState, config.ProbabilityOneHighState, config.Missingness.Mode, config.Missingness.ProbabilityLowState, config.Missingness.ProbabilityHighState);
    }

    private static void ValidateStateSpace(RuntimeModelConfigV2 config)
    {
        var ids = new[] { config.LimitState.StateSpaceId, config.Belief.StateSpaceId, config.Transition.StateSpaceId, config.RoutineObservation.StateSpaceId, config.MonitorObservation.StateSpaceId, config.InspectionObservation.StateSpaceId, config.Reset.StateSpaceId };
        if (ids.Any(x => !string.Equals(x, config.StateSpaceId, StringComparison.Ordinal))) throw new InvalidDataException("Reference-model state-space mismatch.");
        if (config.Transition.ExposureClockId != config.ExposureClockId.Value || config.LimitState.ExposureClockId != config.ExposureClockId.Value)
            throw new InvalidDataException("Reference-model exposure-clock mismatch.");
    }

    private static void ValidateSupport(IReadOnlyList<double> states, IReadOnlyList<double> weights, string name)
    {
        if (states.Count == 0 || states.Count != weights.Count || states.Any(x => !double.IsFinite(x)) || weights.Any(x => !double.IsFinite(x) || x < 0d) || Math.Abs(weights.Sum() - 1d) > 1e-12)
            throw new InvalidDataException($"Invalid {name} support.");
    }

    private static void ValidateArtifact(ArtifactIdentityV2 identity, string payload)
    {
        var expected = ArtifactPayloads.Hash(payload);
        if (!expected.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Artifact hash mismatch: {identity.ArtifactId}");
    }
}

public static class ArtifactPayloads
{
    public static string LimitState(AffineLimitStateBindingConfigV2 value) => FormattableString.Invariant($"AffineScalarLimitStateV1|{value.Boundary:R}|{value.StateSpaceId}|{value.ExposureClockId}|{value.MarginUnitId}|{value.Artifact.Version}");
    public static string Belief(WeightedSupportBeliefBindingConfigV2 value) => $"WeightedSupportBeliefV1|{Join(value.SupportStates)}|{Join(value.Weights)}|{value.StateSpaceId}|{value.Artifact.Version}";
    public static string Transition(ExposureAwareTransitionBindingConfigV2 value) => FormattableString.Invariant($"ExposureScaledUniformRateV1|{value.MaximumIncrementRatePerUnitExposure:R}|{value.ExposureScalingSemantics}|{value.ExposureClockId}|{value.StateSpaceId}|{value.Artifact.Version}");
    public static string Observation(BinaryObservationBindingConfigV2 value) => FormattableString.Invariant($"StateConditionedBinaryObservationV1|{value.Enabled}|{value.ObservationKind}|{value.ActionKind}|{value.StateThreshold:R}|{value.ProbabilityOneLowState:R}|{value.ProbabilityOneHighState:R}|{value.Missingness.Mode}|{value.Missingness.ProbabilityLowState:R}|{value.Missingness.ProbabilityHighState:R}|{value.StateSpaceId}|{value.Artifact.Version}");
    public static string Reset(TwoPointResetBindingConfigV2 value) => $"TwoPointScalarResetV1|{Join(value.SupportStates)}|{Join(value.Probabilities)}|{value.StateSpaceId}|{value.Artifact.Version}";
    public static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    private static string Join(IEnumerable<double> values) => string.Join(',', values.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
}

public sealed class T5AuthorizationValidator
{
    public IReadOnlyList<ValidationIssue> Validate(LoadedConfigurationSetV2 configuration, T5AuthorizationGrantV2? grant, DateTimeOffset now)
    {
        var issues = new List<ValidationIssue>();
        if (configuration.Manifest.AuthorizedStage == RunStage.T6 || configuration.Manifest.ProfileKind == ConfigurationProfileKind.T6Scientific)
            issues.Add(new("T6_HARD_BLOCK_ER05A", EvaluationStatus.ProtocolViolation, "T6 remains unavailable."));
        if (configuration.Manifest.ProfileKind == ConfigurationProfileKind.TestFixtureOnly && configuration.Manifest.AuthorizedStage >= RunStage.T5)
            issues.Add(new("TEST_FIXTURE_PROFILE_ESCALATION", EvaluationStatus.ProtocolViolation, "Fixture profile cannot enter T5/T6."));
        if (configuration.Manifest.AuthorizedStage != RunStage.T5) return issues;
        if (grant is null)
        {
            issues.Add(new("T5_AUTHORIZATION_MISSING", EvaluationStatus.MissingRequiredInput, "An approved hash-bound grant is required."));
            return issues;
        }
        if (grant.State != AuthorizationGrantState.Approved) issues.Add(new("T5_AUTHORIZATION_NOT_APPROVED", EvaluationStatus.ProtocolViolation, grant.State.ToString()));
        if (!grant.ConfigSetSha256.Equals(configuration.ConfigSetSha256, StringComparison.OrdinalIgnoreCase)) issues.Add(new("T5_AUTHORIZATION_CONFIG_HASH_MISMATCH", EvaluationStatus.ProtocolViolation, grant.ConfigSetSha256));
        if (grant.Stage != RunStage.T5) issues.Add(new("T5_AUTHORIZATION_STAGE_MISMATCH", EvaluationStatus.ProtocolViolation, grant.Stage.ToString()));
        if (grant.ProtocolVersion != configuration.ExperimentSet.ProtocolVersion) issues.Add(new("T5_AUTHORIZATION_PROTOCOL_MISMATCH", EvaluationStatus.ProtocolViolation, grant.ProtocolVersion));
        var outputHash = ConfigurationSetLoaderV2.HashFile(Path.Combine(configuration.Root, FindFile(configuration.Manifest, "output-policy")));
        if (!grant.OutputPolicySha256.Equals(outputHash, StringComparison.OrdinalIgnoreCase)) issues.Add(new("T5_AUTHORIZATION_OUTPUT_HASH_MISMATCH", EvaluationStatus.ProtocolViolation, grant.OutputPolicySha256));
        if (grant.ExpiresAtUtc is not null && grant.ExpiresAtUtc <= now) issues.Add(new("T5_AUTHORIZATION_EXPIRED", EvaluationStatus.ProtocolViolation, grant.ExpiresAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)));
        if (string.IsNullOrWhiteSpace(grant.ApproverId)) issues.Add(new("T5_AUTHORIZATION_APPROVER_MISSING", EvaluationStatus.MissingRequiredInput, "Approver ID is required."));
        var expected = configuration.ExperimentSet.Runs.Where(x => x.AuthorizationEligible).ToDictionary(x => x.ExperimentId, x => x.EffectiveConfigSha256);
        var actual = grant.Experiments.ToDictionary(x => x.ExperimentId, x => x.EffectiveConfigSha256);
        if (expected.Count != actual.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var hash) || !hash.Equals(x.Value, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("T5_AUTHORIZATION_EXPERIMENT_ALLOWLIST_MISMATCH", EvaluationStatus.ProtocolViolation, "Experiment IDs/hashes do not match."));
        return issues;
    }

    private static string FindFile(ConfigSetManifestV2 manifest, string role) => manifest.Files.Single(x => x.Role == role).RelativePath;
}

public sealed class T5SourceBoundAuthorizationValidator
{
    public IReadOnlyList<ValidationIssue> Validate(
        LoadedConfigurationSetV2 configuration,
        T5SourceBoundAuthorizationV2? authorization,
        T5ExecutionIdentity? identity,
        DateTimeOffset now)
    {
        var issues = new List<ValidationIssue>();
        if (authorization is null)
        {
            issues.Add(new("T5_SOURCE_BOUND_AUTHORIZATION_MISSING", EvaluationStatus.MissingRequiredInput, "A repaired-source authorization is required."));
            return issues;
        }

        if (identity is null)
        {
            issues.Add(new("T5_EXECUTION_IDENTITY_MISSING", EvaluationStatus.MissingRequiredInput, "Source snapshot and executable identity are required."));
            return issues;
        }

        if (authorization.SchemaVersion != "t5-source-bound-authorization-v2")
            issues.Add(new("T5_SOURCE_BOUND_AUTHORIZATION_SCHEMA_INVALID", EvaluationStatus.ProtocolViolation, authorization.SchemaVersion));
        if (authorization.State != AuthorizationGrantState.Approved)
            issues.Add(new("T5_SOURCE_BOUND_AUTHORIZATION_NOT_APPROVED", EvaluationStatus.ProtocolViolation, authorization.State.ToString()));
        if (authorization.ExpiresAtUtc is not null && authorization.ExpiresAtUtc <= now)
            issues.Add(new("T5_SOURCE_BOUND_AUTHORIZATION_EXPIRED", EvaluationStatus.ProtocolViolation, authorization.ExpiresAtUtc.Value.ToString("O", CultureInfo.InvariantCulture)));
        if (string.IsNullOrWhiteSpace(authorization.ApproverId))
            issues.Add(new("T5_SOURCE_BOUND_AUTHORIZATION_APPROVER_MISSING", EvaluationStatus.MissingRequiredInput, "Approver ID is required."));
        if (!authorization.ConfigSetSha256.Equals(configuration.ConfigSetSha256, StringComparison.OrdinalIgnoreCase))
            issues.Add(new("T5_SOURCE_BOUND_CONFIG_HASH_MISMATCH", EvaluationStatus.ProtocolViolation, authorization.ConfigSetSha256));
        if (authorization.Stage != configuration.Manifest.AuthorizedStage || authorization.Stage == RunStage.T6)
            issues.Add(new("T5_SOURCE_BOUND_STAGE_MISMATCH", EvaluationStatus.ProtocolViolation, authorization.Stage.ToString()));
        if (authorization.ProtocolVersion != configuration.ExperimentSet.ProtocolVersion)
            issues.Add(new("T5_SOURCE_BOUND_PROTOCOL_MISMATCH", EvaluationStatus.ProtocolViolation, authorization.ProtocolVersion));

        var outputHash = ConfigurationSetLoaderV2.HashFile(Path.Combine(configuration.Root, configuration.Manifest.Files.Single(x => x.Role == "output-policy").RelativePath));
        if (!authorization.OutputPolicySha256.Equals(outputHash, StringComparison.OrdinalIgnoreCase))
            issues.Add(new("T5_SOURCE_BOUND_OUTPUT_HASH_MISMATCH", EvaluationStatus.ProtocolViolation, authorization.OutputPolicySha256));

        var expected = configuration.ExperimentSet.Runs.Where(x => x.AuthorizationEligible).ToDictionary(x => x.ExperimentId, x => x.EffectiveConfigSha256);
        var actual = authorization.Experiments.ToDictionary(x => x.ExperimentId, x => x.EffectiveConfigSha256);
        if (expected.Count != actual.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var hash) || !hash.Equals(x.Value, StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("T5_SOURCE_BOUND_EXPERIMENT_ALLOWLIST_MISMATCH", EvaluationStatus.ProtocolViolation, "Experiment IDs/hashes do not match."));

        if (!authorization.SourceSnapshotSha256.Equals(identity.SourceSnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
            !authorization.SourceSnapshotFileName.Equals(Path.GetFileName(identity.SourceSnapshotPath), StringComparison.Ordinal))
            issues.Add(new("T5_SOURCE_SNAPSHOT_IDENTITY_MISMATCH", EvaluationStatus.ProtocolViolation, identity.SourceSnapshotSha256));
        if (!authorization.ExecutableSha256.Equals(identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !authorization.ExecutableFileName.Equals(Path.GetFileName(identity.ExecutablePath), StringComparison.Ordinal))
            issues.Add(new("T5_EXECUTABLE_IDENTITY_MISMATCH", EvaluationStatus.ProtocolViolation, identity.ExecutableSha256));

        if (authorization.Scope == T5ExecutionAuthorizationScope.TestFixtureOnly)
        {
            if (configuration.Manifest.ProfileKind != ConfigurationProfileKind.TestFixtureOnly || authorization.Stage >= RunStage.T5)
                issues.Add(new("TEST_FIXTURE_EXECUTION_SCOPE_INVALID", EvaluationStatus.ProtocolViolation, configuration.Manifest.ProfileKind.ToString()));
        }
        else
        {
            if (configuration.Manifest.ProfileKind != ConfigurationProfileKind.T5Pilot || authorization.Stage != RunStage.T5)
                issues.Add(new("ER06B_RETRY_EXECUTION_SCOPE_INVALID", EvaluationStatus.ProtocolViolation, configuration.Manifest.ProfileKind.ToString()));
            if (string.IsNullOrWhiteSpace(authorization.ParentAuthorizationId))
                issues.Add(new("PARENT_AUTHORIZATION_LINEAGE_MISSING", EvaluationStatus.MissingRequiredInput, "The repaired authorization must retain its parent authorization ID."));
        }

        return issues;
    }
}
