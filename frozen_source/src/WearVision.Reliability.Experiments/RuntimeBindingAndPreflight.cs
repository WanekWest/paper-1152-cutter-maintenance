using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Experiments;

public sealed record BoundBaselinePolicy<TState>(
    string BaselineId,
    BaselineApplicabilityStatus Applicability,
    IBaselinePolicy<TState>? Policy,
    string DesignArtifactId,
    string DesignArtifactSha256,
    string ImplementationVersion,
    string Reason);

public sealed record BoundBaselineSet<TState>(IReadOnlyDictionary<string, BoundBaselinePolicy<TState>> Items)
{
    public BoundBaselinePolicy<TState> Get(string id) => Items.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException(id);
}

public sealed class TypedBaselineFactory<TState>
{
    private readonly IConstrainedPolicySelector _selector;
    private readonly IReadOnlyDictionary<string, Func<TState, double>> _scalarMappings;

    public TypedBaselineFactory(IConstrainedPolicySelector selector, IReadOnlyDictionary<string, Func<TState, double>> scalarMappings)
    {
        _selector = selector;
        _scalarMappings = scalarMappings;
    }

    public BoundBaselineSet<TState> Bind(BaselineConfigV2 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Baselines is null) throw new InvalidDataException("Baseline inventory is missing.");
        var definitions = config.Baselines.ToDictionary(x => x.BaselineId, StringComparer.Ordinal);
        var expected = new[] { "B0-RUN-TO-FAILURE", "B1-PERIODIC", "B2-OBSERVED-PROXY", "B3-POSTERIOR-MEAN", "B4-POSTERIOR-QUANTILE", "B5-FIRST-PASSAGE-QUANTILE", "B6-UNCONSTRAINED-BAYES", "B7-CONSTRAINED-ONE-STAGE" };
        if (definitions.Count != expected.Length || expected.Any(x => !definitions.ContainsKey(x))) throw new InvalidDataException("Baseline configuration must explicitly declare B0 through B7.");
        var result = new Dictionary<string, BoundBaselinePolicy<TState>>(StringComparer.Ordinal);
        foreach (var definition in definitions.Values)
        {
            ValidateDesignArtifact(definition);
            IBaselinePolicy<TState>? policy = definition.Applicability == BaselineApplicabilityStatus.Enabled
                ? CreateEnabled(definition)
                : null;
            result.Add(definition.BaselineId, new(definition.BaselineId, definition.Applicability, policy, definition.DesignArtifactId, definition.DesignArtifactSha256, definition.ImplementationVersion, definition.ApplicabilityReason));
        }
        return new(result);
    }

    private IBaselinePolicy<TState> CreateEnabled(BaselineDefinitionConfigV2 config) => config.BaselineId switch
    {
        "B0-RUN-TO-FAILURE" => new RunToFailureBaseline<TState>(),
        "B1-PERIODIC" when config.Interval is > 0d => new PeriodicReplacementBaseline<TState>(config.Interval),
        "B2-OBSERVED-PROXY" => new ObservedProxyThresholdBaseline<TState>(config.ProxyThreshold),
        "B3-POSTERIOR-MEAN" when MappingExists(config) => new PosteriorMeanBaseline<TState>(config.MeanThreshold),
        "B4-POSTERIOR-QUANTILE" when MappingExists(config) && config.QuantileLevel is >= 0d and <= 1d && config.QuantileThreshold is not null => new PosteriorQuantileBaseline<TState>(config.QuantileLevel, config.QuantileThreshold),
        "B5-FIRST-PASSAGE-QUANTILE" when config.FirstPassageHorizonThreshold is not null => new FirstPassageQuantileBaseline<TState>(config.FirstPassageHorizonThreshold),
        "B6-UNCONSTRAINED-BAYES" => new UnconstrainedBayesBaseline<TState>(),
        "B7-CONSTRAINED-ONE-STAGE" => new ConstrainedBayesBaseline<TState>(_selector),
        _ => throw new InvalidDataException($"Enabled baseline is missing typed parameters: {config.BaselineId}")
    };

    private bool MappingExists(BaselineDefinitionConfigV2 config) => config.ScalarMappingId is not null && _scalarMappings.ContainsKey(config.ScalarMappingId);

    private static void ValidateDesignArtifact(BaselineDefinitionConfigV2 config)
    {
        if (config.BaselineId is "B1-PERIODIC" or "B2-OBSERVED-PROXY" or "B3-POSTERIOR-MEAN" or "B4-POSTERIOR-QUANTILE" or "B5-FIRST-PASSAGE-QUANTILE")
        {
            if (string.IsNullOrWhiteSpace(config.DesignArtifactId) || config.DesignArtifactSha256.Length != 64 || config.DesignArtifactSha256.Any(x => !Uri.IsHexDigit(x)))
                throw new InvalidDataException($"Baseline design artifact is missing or invalid: {config.BaselineId}");
        }
    }
}

public sealed record T5PreflightReport(
    bool RuntimeSchemaCompatible,
    bool ModelBindingCompatible,
    bool BaselineBindingCompatible,
    bool MultiRowBindingCompatible,
    bool PlanningHarnessCompatible,
    bool ExecutionStarted,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool AcceptedForExecution => RuntimeSchemaCompatible && ModelBindingCompatible && BaselineBindingCompatible && MultiRowBindingCompatible && PlanningHarnessCompatible && Issues.Count == 0;
}

public sealed class T5CandidatePreflight
{
    private readonly ScalarReferenceModelFactory _models = new();
    private readonly T5AuthorizationValidator _authorization = new();

    public T5PreflightReport Validate(LoadedConfigurationSetV2 configuration, T5AuthorizationGrantV2? grant)
    {
        var issues = new List<ValidationIssue>();
        var modelCompatible = true;
        var baselineCompatible = true;
        var multiRowCompatible = true;
        var planningCompatible = true;
        try { _models.Bind(configuration.Model); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NullReferenceException)
        {
            modelCompatible = false;
            issues.Add(new("MODEL_BINDING_INVALID", EvaluationStatus.IncompatibleModel, exception.Message));
        }
        try
        {
            ValidateConsequences(configuration.Consequences);
            var factory = new TypedBaselineFactory<double>(new ConstrainedPolicySelector(), new Dictionary<string, Func<double, double>>(StringComparer.Ordinal) { ["failure-oriented-state-v1"] = x => x });
            factory.Bind(configuration.Baselines);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NullReferenceException)
        {
            baselineCompatible = false;
            issues.Add(new("BASELINE_BINDING_INVALID", EvaluationStatus.IncompatibleModel, exception.Message));
        }
        try { ValidateMultiRow(configuration); }
        catch (InvalidDataException exception)
        {
            multiRowCompatible = false;
            issues.Add(new("MULTI_ROW_BINDING_INVALID", EvaluationStatus.ProtocolViolation, exception.Message));
        }
        try { ValidatePlanning(configuration); }
        catch (InvalidDataException exception)
        {
            planningCompatible = false;
            issues.Add(new("PLANNING_HARNESS_INCOMPATIBLE", EvaluationStatus.ProtocolViolation, exception.Message));
        }
        var requiredOwnerDecisions = Enumerable.Range(1, 10).Select(x => $"A-{x:D2}").ToHashSet(StringComparer.Ordinal);
        if (configuration.OwnerApproval.OverallState != OwnerApprovalState.Approved || configuration.OwnerApproval.Decisions.Where(x => requiredOwnerDecisions.Contains(x.DecisionId)).Any(x => x.State != OwnerApprovalState.Approved))
            issues.Add(new("OWNER_APPROVAL_REQUIRED", EvaluationStatus.MissingRequiredInput, "A-01 through A-10 remain unapproved."));
        if (configuration.Resources.MaximumWallClockSeconds.Value is null || configuration.Resources.MaximumStorageBytes.Value is null || configuration.Resources.AllowEr04MaximumPaths.Value is null ||
            configuration.Resources.MaximumWallClockSeconds.Status != RuntimeValueStatus.OwnerApproved || configuration.Resources.MaximumStorageBytes.Status != RuntimeValueStatus.OwnerApproved || configuration.Resources.AllowEr04MaximumPaths.Status != RuntimeValueStatus.OwnerApproved)
            issues.Add(new("RESOURCE_CEILING_OWNER_DECISION_REQUIRED", EvaluationStatus.MissingRequiredInput, "Wall clock, storage and Nmax permission must be owner-approved."));
        issues.AddRange(_authorization.Validate(configuration, grant, DateTimeOffset.UtcNow));
        return new(true, modelCompatible, baselineCompatible, multiRowCompatible, planningCompatible, false, issues);
    }

    private static void ValidateConsequences(ConsequenceConfigV2 config)
    {
        if (config.Components is null || config.Weights is null || config.Weights.Value is null) throw new InvalidDataException("Consequence configuration is incomplete.");
        if (config.Components.Count != Enum.GetValues<ConsequenceComponentKind>().Length || config.Components.Select(x => x.Component).Distinct().Count() != config.Components.Count)
            throw new InvalidDataException("Consequence components must declare exactly the frozen five-component set.");
        if (config.Components.Any(x => x.Enabled && (!double.IsFinite(x.Normalizer.Value) || x.Normalizer.Value <= 0d)))
            throw new InvalidDataException("Consequence normalizers must be positive and finite.");
        var profile = new ConsequenceWeightProfile(config.WeightProfileId, config.Weights.OwnerId, config.WeightProfileStatus, config.WeightProfileVersion, null, config.Weights.Value, new(config.WeightProfileSha256));
        if (!ConsequenceWeightValidator.Validate(profile).IsValid) throw new InvalidDataException("Consequence weights are not a simplex.");
    }

    private static void ValidateMultiRow(LoadedConfigurationSetV2 configuration)
    {
        var runs = configuration.ExperimentSet.Runs;
        var fixture = configuration.Manifest.ProfileKind == ConfigurationProfileKind.TestFixtureOnly;
        if (runs.Count != 2) throw new InvalidDataException("Expected exactly two ordered T5 execution rows.");
        if (!fixture && (runs[0].ExperimentId.Value != "T5-001" || runs[1].ExperimentId.Value != "T5-002")) throw new InvalidDataException("Expected ordered T5-001/T5-002 run references.");
        if (fixture && (runs.Any(x => !x.ExperimentId.Value.StartsWith("FX5-", StringComparison.Ordinal)) || configuration.Manifest.AuthorizedStage >= RunStage.T5))
            throw new InvalidDataException("TEST_FIXTURE_ONLY rows must use the FX5 namespace below T5.");
        if (runs.Select(x => x.EffectiveConfigSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2) throw new InvalidDataException("Effective experiment hashes must be distinct.");
        if (runs.Select(x => x.OutputSubroot).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2) throw new InvalidDataException("Experiment output subroots collide.");
        var knownPolicies = new HashSet<string>(["P-R4-TWO-STAGE", "B1-PERIODIC", "B4-POSTERIOR-QUANTILE", "B6-UNCONSTRAINED-BAYES", "B7-CONSTRAINED-ONE-STAGE"], StringComparer.Ordinal);
        foreach (var run in configuration.EffectiveExperiments.Values)
        {
            var boundaryValid = run.Partition == DataPartition.T5PilotOnly &&
                (fixture
                    ? run.ProfileKind == ConfigurationProfileKind.TestFixtureOnly && run.Stage < RunStage.T5
                    : run.ProfileKind == ConfigurationProfileKind.T5Pilot && run.Stage == RunStage.T5);
            if (!boundaryValid) throw new InvalidDataException($"Invalid T5 row boundary: {run.ExperimentId}");
            if (run.PolicyIds.Any(x => !knownPolicies.Contains(x))) throw new InvalidDataException($"Unknown policy ID: {run.ExperimentId}");
            var episodeCount = configuration.Split.Assignments.Count(x => x.Partition == DataPartition.T5PilotOnly && x.EpisodeId.StartsWith(run.ExperimentId.Value + "-", StringComparison.Ordinal));
            if (episodeCount != run.EpisodeCount) throw new InvalidDataException($"Episode-count/split mismatch: {run.ExperimentId}");
        }
    }

    private static void ValidatePlanning(LoadedConfigurationSetV2 configuration)
    {
        if (configuration.Output.ManuscriptRenderingEnabled || configuration.Output.CompressionEnabled || !configuration.Output.RefuseExisting || !configuration.Output.CompletionManifestLast)
            throw new InvalidDataException("T5 output policy violates the planning firewall.");
        if (!configuration.Output.ApprovedRoot.Contains("T5", StringComparison.OrdinalIgnoreCase) || configuration.Output.ApprovedRoot.StartsWith("E:\\WearVision", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("T5 output root is not isolated.");
        if (configuration.Split.Assignments.Any(x => x.Partition != DataPartition.T5PilotOnly)) throw new InvalidDataException("T5 split contains a non-T5 partition.");
        if (configuration.Retirement.PathFragments.Any(x => configuration.Output.ApprovedRoot.Replace('\\', '/').Contains(x, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Output policy references a retired path.");
    }
}

public sealed class T5CommandApplication
{
    public T5PreflightReport ValidateConfiguration(string directory)
    {
        var loaded = new ConfigurationSetLoaderV2().Load(directory);
        var report = new T5CandidatePreflight().Validate(loaded, null);
        return report with { Issues = report.Issues.Where(x => x.Code is not ("OWNER_APPROVAL_REQUIRED" or "RESOURCE_CEILING_OWNER_DECISION_REQUIRED" or "T5_AUTHORIZATION_MISSING")).ToArray() };
    }

    public T5PreflightReport Preflight(string directory, T5AuthorizationGrantV2? grant)
    {
        var loaded = new ConfigurationSetLoaderV2().Load(directory);
        return new T5CandidatePreflight().Validate(loaded, grant);
    }

    public T5PreflightReport PreflightForExecution(
        string directory,
        T5SourceBoundAuthorizationV2? authorization,
        T5ExecutionIdentity? identity)
    {
        var loaded = new ConfigurationSetLoaderV2().Load(directory);
        var legacyGrant = authorization is null ? null : new T5AuthorizationGrantV2(
            "t5-authorization-v1",
            authorization.AuthorizationId,
            authorization.State,
            authorization.ConfigSetSha256,
            authorization.Experiments,
            authorization.ProtocolVersion,
            authorization.Stage,
            authorization.OutputPolicySha256,
            authorization.ApproverId,
            authorization.ExpiresAtUtc);
        var report = new T5CandidatePreflight().Validate(loaded, legacyGrant);
        var sourceIssues = new T5SourceBoundAuthorizationValidator().Validate(loaded, authorization, identity, DateTimeOffset.UtcNow);
        return report with { Issues = report.Issues.Concat(sourceIssues).ToArray() };
    }

    public async Task<int> RunT5PilotAsync(
        string directory,
        T5AuthorizationGrantV2? grant,
        Func<LoadedConfigurationSetV2, CancellationToken, Task<int>> execution,
        CancellationToken cancellationToken)
    {
        var loaded = new ConfigurationSetLoaderV2().Load(directory);
        var preflight = new T5CandidatePreflight().Validate(loaded, grant);
        if (!preflight.AcceptedForExecution) return 4;
        var result = await execution(loaded, cancellationToken).ConfigureAwait(false);
        return result == 0 ? 5 : result;
    }

    public async Task<T5ProductionExecutionResult> RunT5PilotAsync(
        string directory,
        T5SourceBoundAuthorizationV2 authorization,
        T5ExecutionIdentity identity,
        string runId,
        T5PilotExecutionService execution,
        CancellationToken cancellationToken)
    {
        var preflight = PreflightForExecution(directory, authorization, identity);
        if (!preflight.AcceptedForExecution)
            return new(4, "PREFLIGHT_REJECTED", string.Empty, [], 0L, null, preflight.Issues.Count == 0 ? "PREFLIGHT_REJECTED" : preflight.Issues[0].Code);
        var loaded = new ConfigurationSetLoaderV2().Load(directory);
        return await execution.ExecuteAsync(loaded, authorization, identity, runId, cancellationToken).ConfigureAwait(false);
    }

    public int RunT6() => 6;
}
