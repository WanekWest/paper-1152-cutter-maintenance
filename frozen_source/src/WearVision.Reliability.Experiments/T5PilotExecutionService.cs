using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Experiments;

public sealed record T5ProductionExecutionResult(
    int ExitCode,
    string Status,
    string RunRoot,
    IReadOnlyList<string> ExecutedRows,
    long AccountedEpisodes,
    string? CompletionManifestSha256,
    string ErrorCode);

public sealed record T5EpisodeGenerationRequest(
    LoadedConfigurationSetV2 Configuration,
    EffectiveExperimentConfigV2 Experiment,
    SplitAssignmentV2 Split,
    T5PlanningLineage Lineage,
    string RunId,
    T6EpisodeExecutionSemanticsV1? T6Semantics = null);

public sealed record T6EpisodeExecutionSemanticsV1(
    string VariantId,
    bool UseNumericalProtocolV2,
    bool DisableEndogenousInformation,
    bool DisableReliabilityConstraint,
    bool DisableBranchwiseEpsilonRemaining);

public sealed class T6UnconstrainedPolicySelector : IConstrainedPolicySelector
{
    public DecisionResult Select(IReadOnlyList<CompletePolicyEvaluation> evaluations)
    {
        var selected = evaluations
            .Where(x => x.Admissibility.IsAdmissible && x.Disutility.ExpectedDisutility is not null && x.Disutility.Status == EvaluationStatus.Success)
            .OrderBy(x => x.Disutility.ExpectedDisutility!.Value.Value)
            .ThenBy(x => x.Policy.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return selected is null
            ? new(DecisionStatus.NumericalNonConvergence, null, evaluations, "ABL5_NO_EVALUABLE_POLICY")
            : new(DecisionStatus.Success, selected, evaluations, "ABL5_MINIMUM_DISUTILITY_WITHOUT_RELIABILITY_CONSTRAINT");
    }
}

public sealed record T5EpisodeExecutionResult(
    T5PlanningEpisodeInput PlanningInput,
    IReadOnlyList<RawEvidenceRecord> RawRecords);

public interface IT5ProductionEpisodeGenerator
{
    T5EpisodeExecutionResult Generate(T5EpisodeGenerationRequest request, CancellationToken cancellationToken);
}

public static class T5ProductionCompletionGate
{
    public static void Validate(
        IReadOnlyList<string> authorizedRows,
        IReadOnlyList<string> executedRows,
        IReadOnlyList<string> expectedEpisodeIds,
        IReadOnlyList<string> actualEpisodeIds)
    {
        if (!authorizedRows.SequenceEqual(executedRows, StringComparer.Ordinal))
            throw new InvalidOperationException("T5_EXECUTION_EVIDENCE_INCOMPLETE");
        if (actualEpisodeIds.Count != actualEpisodeIds.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("T5_DUPLICATE_COMPONENT_EPISODE_ID");
        if (!expectedEpisodeIds.SequenceEqual(actualEpisodeIds, StringComparer.Ordinal))
            throw new InvalidOperationException("T5_EXECUTION_EVIDENCE_INCOMPLETE");
    }
}

public sealed class T5PilotExecutionService
{
    private readonly string _rawSchemaRegistryPath;
    private readonly IT5ProductionEpisodeGenerator _episodeGenerator;
    private readonly Func<DateTimeOffset> _clock;

    public T5PilotExecutionService(
        string rawSchemaRegistryPath,
        IT5ProductionEpisodeGenerator? episodeGenerator = null,
        Func<DateTimeOffset>? clock = null)
    {
        _rawSchemaRegistryPath = Path.GetFullPath(rawSchemaRegistryPath);
        _episodeGenerator = episodeGenerator ?? new T5ProductionEpisodeGenerator();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<T5ProductionExecutionResult> ExecuteAsync(
        LoadedConfigurationSetV2 configuration,
        T5SourceBoundAuthorizationV2 authorization,
        T5ExecutionIdentity identity,
        string runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ValidateRuntimeResources(configuration);
        var authorizationIssues = new T5SourceBoundAuthorizationValidator().Validate(configuration, authorization, identity, _clock());
        if (authorizationIssues.Count != 0)
            return new(4, "PREFLIGHT_REJECTED", string.Empty, [], 0L, null, authorizationIssues[0].Code);

        var maximumSeconds = configuration.Resources.MaximumWallClockSeconds.Value ?? throw new InvalidDataException("Maximum wall-clock ceiling is missing.");
        var maximumBytes = configuration.Resources.MaximumStorageBytes.Value ?? throw new InvalidDataException("Maximum storage ceiling is missing.");
        if (!ConfigurationSetLoaderV2.HashFile(_rawSchemaRegistryPath).Equals(configuration.Output.SchemaRegistrySha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("T5_RAW_SCHEMA_REGISTRY_HASH_MISMATCH");
        var registry = RawSchemaRegistry.Load(_rawSchemaRegistryPath);
        var start = _clock();
        var stopwatch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(maximumSeconds));
        using var writer = new T5ProductionWriteSession(configuration.Output.ApprovedRoot, runId, maximumBytes, registry);
        var executedRows = new List<string>();
        var actualEpisodes = new List<string>();
        var expectedRows = configuration.ExperimentSet.Runs.Select(x => x.ExperimentId.Value).ToArray();
        var expectedEpisodes = configuration.ExperimentSet.Runs.SelectMany(run =>
            configuration.Split.Assignments.Where(x => x.EpisodeId.StartsWith(run.ExperimentId.Value + "-", StringComparison.Ordinal)).Select(x => x.EpisodeId)).ToArray();
        var planningCompleted = 0;

        try
        {
            writer.WriteRaw(CreateEnvironmentRecord(runId, configuration, authorization, identity));
            foreach (var run in configuration.ExperimentSet.Runs)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!authorization.Experiments.Any(x => x.ExperimentId == run.ExperimentId && x.EffectiveConfigSha256.Equals(run.EffectiveConfigSha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("T5_UNAUTHORIZED_EXPERIMENT_ROW");
                var experiment = configuration.EffectiveExperiments[run.ExperimentId];
                var assignments = configuration.Split.Assignments
                    .Where(x => x.EpisodeId.StartsWith(run.ExperimentId.Value + "-", StringComparison.Ordinal))
                    .OrderBy(x => x.EpisodeId, StringComparer.Ordinal)
                    .ToArray();
                if (assignments.Length != experiment.EpisodeCount) throw new InvalidOperationException("T5_EXECUTION_EVIDENCE_INCOMPLETE");
                var lineage = new T5PlanningLineage(
                    experiment.ExperimentId,
                    run.EffectiveConfigSha256,
                    runId,
                    configuration.Manifest.ProfileKind,
                    authorization.AuthorizationId,
                    true,
                    false,
                    false);
                var planningInputs = new List<T5PlanningEpisodeInput>(assignments.Length);
                foreach (var assignment in assignments)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (stopwatch.Elapsed.TotalSeconds > maximumSeconds) throw new OperationCanceledException("T5_WALL_CLOCK_CEILING", deadline.Token);
                    var evidence = _episodeGenerator.Generate(new(configuration, experiment, assignment, lineage, runId), deadline.Token);
                    var episodeId = evidence.PlanningInput.EpisodeId.Value;
                    if (!episodeId.Equals(assignment.EpisodeId, StringComparison.Ordinal)) throw new InvalidOperationException("T5_COMPONENT_EPISODE_ID_MISMATCH");
                    if (actualEpisodes.Contains(episodeId, StringComparer.Ordinal)) throw new InvalidOperationException("T5_DUPLICATE_COMPONENT_EPISODE_ID");
                    foreach (var record in evidence.RawRecords) writer.WriteRaw(record);
                    planningInputs.Add(evidence.PlanningInput);
                    actualEpisodes.Add(episodeId);
                }

                var request = new T5PlanningRunRequest(
                    lineage,
                    new(TimeSpan.FromSeconds(maximumSeconds), maximumBytes - writer.CommittedBytes, configuration.Resources.MaximumDegreeOfParallelism.Value, configuration.Resources.AllowEr04MaximumPaths.Value),
                    true);
                var planning = await new T5PlanningHarness().RunAsync(request, Enumerate(planningInputs, deadline.Token), deadline.Token).ConfigureAwait(false);
                if (!planning.Manifest.Completed || planning.Manifest.RawEpisodeDenominator != assignments.Length || planning.Manifest.IndependentEpisodeDenominator != assignments.Length)
                    throw new InvalidOperationException("T5_EXECUTION_EVIDENCE_INCOMPLETE");
                writer.WritePlanning(planning, planningInputs);
                planningCompleted++;
                executedRows.Add(run.ExperimentId.Value);
            }

            T5ProductionCompletionGate.Validate(expectedRows, executedRows, expectedEpisodes, actualEpisodes);
            var end = _clock();
            writer.WriteRaw(CreateRunRecord(runId, configuration, authorization, identity, start, end, stopwatch.ElapsedMilliseconds));
            writer.WriteJson("lineage/execution-lineage.json", new
            {
                SchemaVersion = "t5-production-lineage-v1",
                RunId = runId,
                AuthorizationId = authorization.AuthorizationId,
                ParentAuthorizationId = authorization.ParentAuthorizationId,
                SourceSnapshotSha256 = identity.SourceSnapshotSha256,
                ExecutableSha256 = identity.ExecutableSha256,
                ConfigSetSha256 = configuration.ConfigSetSha256,
                Rows = executedRows,
                EpisodeCount = actualEpisodes.Count,
                T5Only = true,
                T6Eligible = false,
                ManuscriptEligible = false
            });
            var completion = writer.Complete(new(expectedRows, executedRows, expectedEpisodes.Length, actualEpisodes.Count, true, planningCompleted));
            stopwatch.Stop();
            return new(0, "T5_PRODUCTION_EXECUTION_COMPLETE", writer.Root, executedRows, actualEpisodes.Count, completion.Sha256Hex, string.Empty);
        }
        catch (OperationCanceledException exception)
        {
            stopwatch.Stop();
            WriteFailure(writer, "T5_WALL_CLOCK_OR_EXTERNAL_CANCELLATION", exception.Message, executedRows, actualEpisodes.Count);
            return new(5, "INCOMPLETE", writer.Root, executedRows, actualEpisodes.Count, null, "T5_WALL_CLOCK_OR_EXTERNAL_CANCELLATION");
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
        {
            stopwatch.Stop();
            var code = string.IsNullOrWhiteSpace(exception.Message) ? "T5_EXECUTION_FAILED" : exception.Message;
            WriteFailure(writer, code, exception.GetType().Name, executedRows, actualEpisodes.Count);
            return new(5, "INCOMPLETE", writer.Root, executedRows, actualEpisodes.Count, null, code);
        }
    }

    private static void ValidateRuntimeResources(LoadedConfigurationSetV2 configuration)
    {
        if (configuration.Resources.MaximumDegreeOfParallelism.Value != 1 || configuration.Numerical.MaximumDegreeOfParallelism != 1)
            throw new InvalidDataException("T5_MDOP_MUST_EQUAL_ONE");
        if (configuration.Numerical.McMaximumPaths > 1_048_576 || configuration.Numerical.McMaximumPaths <= 0)
            throw new InvalidDataException("T5_NMAX_PERMISSION_VIOLATION");
        if (configuration.Manifest.ProfileKind == ConfigurationProfileKind.T5Pilot && configuration.Resources.AllowEr04MaximumPaths.Value != true)
            throw new InvalidDataException("T5_NMAX_PERMISSION_REQUIRED");
        if (configuration.Resources.MaximumWallClockSeconds.Value is null or <= 0d || configuration.Resources.MaximumStorageBytes.Value is null or <= 0L)
            throw new InvalidDataException("T5_RESOURCE_CEILING_INVALID");
    }

    private static EnvironmentManifestRecord CreateEnvironmentRecord(
        string runId,
        LoadedConfigurationSetV2 configuration,
        T5SourceBoundAuthorizationV2 authorization,
        T5ExecutionIdentity identity)
    {
        var dependencyPath = Path.ChangeExtension(identity.ExecutablePath, ".deps.json");
        var dependencyHash = File.Exists(dependencyPath) ? ConfigurationSetLoaderV2.HashFile(dependencyPath) : Hash($"BCL_ONLY|{authorization.ExecutableSha256}").Sha256Hex;
        return (EnvironmentManifestRecord)new EnvironmentManifestRecord()
        .Set("run_id", runId)
        .Set("os", Environment.OSVersion.ToString())
        .Set("process_architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
        .Set("cpu_model", Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"))
        .Set("dotnet_sdk", configuration.Compatibility.SdkVersion)
        .Set("dotnet_runtime", Environment.Version.ToString())
        .Set("target_framework", configuration.Compatibility.TargetFramework)
        .Set("dependency_lock_hash", dependencyHash)
        .Set("culture", configuration.Output.Culture)
        .Set("timezone", TimeZoneInfo.Local.Id)
        .Set("max_parallelism", configuration.Resources.MaximumDegreeOfParallelism.Value)
        .Set("newline_policy", configuration.Output.NewlinePolicy);
    }

    private static RunManifestRecord CreateRunRecord(
        string runId,
        LoadedConfigurationSetV2 configuration,
        T5SourceBoundAuthorizationV2 authorization,
        T5ExecutionIdentity identity,
        DateTimeOffset start,
        DateTimeOffset end,
        long elapsedMilliseconds)
    {
        var compatibilityPath = Path.Combine(configuration.Root, configuration.Manifest.Files.Single(x => x.Role == "compatibility").RelativePath);
        var retirementPath = Path.Combine(configuration.Root, configuration.Manifest.Files.Single(x => x.Role == "retirement").RelativePath);
        var splitPath = Path.Combine(configuration.Root, configuration.Manifest.Files.Single(x => x.Role == "split").RelativePath);
        var modelBundleHash = Hash(string.Join('|', configuration.Compatibility.ComponentHashes.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{x.Value}")));
        return (RunManifestRecord)new RunManifestRecord()
            .Set("run_id", runId)
            .Set("execution_stage", configuration.Manifest.AuthorizedStage)
            .Set("authorization_reference", authorization.AuthorizationId)
            .Set("start_utc", start.ToUniversalTime())
            .Set("start_local", start.ToLocalTime())
            .Set("end_utc", end.ToUniversalTime())
            .Set("completion_status", "COMPLETE")
            .Set("architecture_version", configuration.Compatibility.ArchitectureVersion)
            .Set("mathematical_model_version", configuration.Compatibility.MathematicalModelVersion)
            .Set("protocol_version", configuration.Compatibility.ProtocolVersion)
            .Set("code_version", identity.SourceSnapshotSha256)
            .Set("model_bundle_hash", modelBundleHash)
            .Set("input_dataset_hash", null)
            .Set("target_framework", configuration.Compatibility.TargetFramework)
            .Set("effective_config_hash", Hash(string.Join('|', configuration.ExperimentSet.Runs.Select(x => x.EffectiveConfigSha256))))
            .Set("compatibility_manifest_hash", ConfigurationSetLoaderV2.HashFile(compatibilityPath))
            .Set("environment_manifest_hash", Hash($"{Environment.OSVersion}|{Environment.Version}|{configuration.Resources.MaximumDegreeOfParallelism.Value}"))
            .Set("seed_manifest_hash", Hash($"{configuration.Numerical.MasterSeedUInt64}|{configuration.Numerical.RngAlgorithmId}"))
            .Set("split_manifest_hash", ConfigurationSetLoaderV2.HashFile(splitPath))
            .Set("retirement_denylist_hash", ConfigurationSetLoaderV2.HashFile(retirementPath))
            .Set("semantic_test_evidence_version", "ER06B-R1-PRODUCTION-PIPELINE")
            .Set("enabled_baseline_ids", configuration.Baselines.Baselines.Where(x => x.Applicability == BaselineApplicabilityStatus.Enabled).Select(x => x.BaselineId).OrderBy(x => x, StringComparer.Ordinal).ToArray())
            .Set("enabled_ablation_ids", Array.Empty<string>())
            .Set("artifact_label", "MODEL_BASED_T5_METHODOLOGICAL_PILOT_ONLY_NOT_SCIENTIFIC_RESULT")
            .Set("total_elapsed_ms", elapsedMilliseconds)
            .Set("peak_working_set_bytes", Process.GetCurrentProcess().PeakWorkingSet64)
            .Set("output_hash_manifest", "completion-manifest.json")
            .Set("known_limitations", "T5 planning only; not T6; not manuscript; not industrial validation.");
    }

    private static async IAsyncEnumerable<T5PlanningEpisodeInput> Enumerate(
        IReadOnlyList<T5PlanningEpisodeInput> values,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return value;
        }
    }

    private static void WriteFailure(T5ProductionWriteSession writer, string code, string detail, IReadOnlyList<string> rows, long episodes)
    {
        try
        {
            writer.WriteJson("failure/incomplete-failure.json", new
            {
                SchemaVersion = "t5-production-incomplete-v1",
                Status = "INCOMPLETE",
                ErrorCode = code,
                Detail = detail,
                ExecutedRows = rows,
                AccountedEpisodes = episodes,
                CompletionManifestWritten = false
            });
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static ArtifactHash Hash(string value) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
}

internal sealed record T5NumericalBatchEvidence(
    NumericalBatchSnapshot Snapshot,
    double DisutilityMean,
    double DisutilityM2,
    ArtifactHash StreamContextHash);

internal sealed class T5ScalarTerminalActionEvaluator : ITerminalActionEvaluator<double>
{
    internal const int DeterministicFallbackEscalationPaths = 976_896;
    private readonly ScalarReferenceModelBundle _models;
    private readonly LoadedConfigurationSetV2 _configuration;
    private readonly RandomStreamContext _streamContext;
    private readonly Sha256XoshiroRandomStreamFactory _streams;
    private readonly ConsequenceWeightProfile _weights;
    private readonly IReadOnlyDictionary<MaintenanceActionKind, double> _durations;
    private readonly List<T5NumericalBatchEvidence> _batches;
    private readonly bool _useNumericalProtocolV2;

    public T5ScalarTerminalActionEvaluator(
        ScalarReferenceModelBundle models,
        LoadedConfigurationSetV2 configuration,
        RandomStreamContext streamContext,
        Sha256XoshiroRandomStreamFactory streams,
        ConsequenceWeightProfile weights,
        IReadOnlyDictionary<MaintenanceActionKind, double> durations,
        List<T5NumericalBatchEvidence> batches,
        bool useNumericalProtocolV2)
    {
        _models = models;
        _configuration = configuration;
        _streamContext = streamContext;
        _streams = streams;
        _weights = weights;
        _durations = durations;
        _batches = batches;
        _useNumericalProtocolV2 = useNumericalProtocolV2;
    }

    public CompletePolicyEvaluation Evaluate(
        IDegradationBelief<double> belief,
        MaintenanceActionKind action,
        Horizon remainingHorizon,
        ConfigurationHash configurationHash,
        CancellationToken cancellationToken)
    {
        var policy = new DirectPolicy(new($"terminal-{action}"), remainingHorizon, action);
        var numerical = new NumericalSettings(
            _configuration.Numerical.ConfigId,
            _configuration.Numerical.McMinimumPaths,
            _configuration.Numerical.McBatchSize,
            _configuration.Numerical.McMaximumPaths,
            _configuration.Numerical.StableBatchCount,
            0.99d,
            _configuration.Numerical.QuadratureAbsoluteTolerance,
            _configuration.Numerical.QuadratureRelativeTolerance,
            _configuration.Manifest.ProfileKind == ConfigurationProfileKind.TestFixtureOnly);
        var failureWeight = _weights.Weights[ConsequenceComponentKind.Failure];
        var burden = Burden(action, remainingHorizon.Value);
        var useV2 = _useNumericalProtocolV2 || _configuration.Manifest.ProfileKind is ConfigurationProfileKind.T5Pilot or ConfigurationProfileKind.T6Scientific;
        var fallback = useV2
            ? new DeterministicScalarUniformFirstPassageFallback(_configuration.Numerical.QuadratureAbsoluteTolerance)
            : null;
        var fallbackEscalationPaths = useV2
            ? _configuration.Manifest.ProfileKind == ConfigurationProfileKind.TestFixtureOnly
                ? _configuration.Numerical.McMaximumPaths
                : DeterministicFallbackEscalationPaths
            : (int?)null;
        var estimator = new PosteriorPredictiveFirstPassageEstimator<double>(_streams, batchObserver: batch =>
        {
            var mean = Math.Clamp(burden + failureWeight * batch.FailureEstimate, 0d, 1d);
            var m2 = failureWeight * failureWeight * batch.CumulativeFailureCount * (1d - batch.FailureEstimate);
            var streamHash = new ArtifactHash(Convert.ToHexString(SHA256.HashData(_streams.EncodeKey(new(_streamContext, StreamPurpose.DegradationInnovation, checked((ulong)batch.PathIndexStart))))));
            _batches.Add(new(batch, mean, m2, streamHash));
        }, deterministicFallback: fallback, fallbackEscalationPaths: fallbackEscalationPaths);
        var reliability = estimator.Estimate(new(
            _streamContext.DecisionId,
            belief,
            _models.Transition,
            _models.Reset,
            _models.LimitState,
            policy,
            remainingHorizon,
            numerical,
            _streamContext,
            new FailureProbability(_configuration.Reliability.Epsilon.Value)), cancellationToken);
        var components = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, _ => 0d);
        if (reliability.FailureProbability is { } failure) components[ConsequenceComponentKind.Failure] = failure.Value;
        if (action == MaintenanceActionKind.PlannedReplacement) components[ConsequenceComponentKind.PlannedReplacement] = 1d;
        if (action == MaintenanceActionKind.IntensifiedMonitoring) components[ConsequenceComponentKind.IntensifiedMonitoring] = 1d;
        if (action == MaintenanceActionKind.TargetedInspection) components[ConsequenceComponentKind.TargetedInspection] = 1d;
        components[ConsequenceComponentKind.Downtime] = Downtime(action, remainingHorizon.Value);
        var expected = reliability.FailureProbability is null
            ? new ExpectedDisutilityEvaluation(EvaluationStatus.NotEvaluable, null, components, new(0d), reliability.Diagnostics.EvaluationCount, "MISSING_FAILURE_PROBABILITY")
            : new ExpectedDisutilityEvaluation(
                EvaluationStatus.Success,
                new(Math.Clamp(components.Sum(x => x.Value * _weights.Weights[x.Key]), 0d, 1d)),
                components,
                new(failureWeight * reliability.Diagnostics.FailureProbabilityStandardError.Value),
                reliability.Diagnostics.EvaluationCount,
                "bounded-five-component-from-predictive-failure-v1");
        var feasibility = new ReliabilityFeasibilityEvaluator().Evaluate(reliability, new(_configuration.Reliability.Epsilon.Value));
        return new(
            policy,
            new(true, ActionAdmissibilityReason.Admissible),
            reliability,
            expected,
            feasibility,
            null,
            burden,
            configurationHash,
            reliability.Status,
            reliability.Diagnostics.WarningCodes);
    }

    private double Burden(MaintenanceActionKind action, double horizon) => action switch
    {
        MaintenanceActionKind.PlannedReplacement => _weights.Weights[ConsequenceComponentKind.PlannedReplacement] + _weights.Weights[ConsequenceComponentKind.Downtime] * Downtime(action, horizon),
        MaintenanceActionKind.IntensifiedMonitoring => _weights.Weights[ConsequenceComponentKind.IntensifiedMonitoring] + _weights.Weights[ConsequenceComponentKind.Downtime] * Downtime(action, horizon),
        MaintenanceActionKind.TargetedInspection => _weights.Weights[ConsequenceComponentKind.TargetedInspection] + _weights.Weights[ConsequenceComponentKind.Downtime] * Downtime(action, horizon),
        _ => 0d
    };

    private double Downtime(MaintenanceActionKind action, double horizon) => Math.Clamp(_durations.GetValueOrDefault(action) / Math.Max(double.Epsilon, horizon), 0d, 1d);
}

public sealed class T5ProductionEpisodeGenerator : IT5ProductionEpisodeGenerator
{
    public T5EpisodeExecutionResult Generate(T5EpisodeGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = request.Configuration;
        var experiment = request.Experiment;
        var episodeId = new ComponentEpisodeId(request.Split.EpisodeId);
        var trajectoryId = new TrajectoryId(request.Split.EpisodeId + "-TRJ");
        var decisionId = new DecisionId(request.Split.EpisodeId + "-D-0001");
        var couplingGroup = request.Split.GroupingKey + "-CRN";
        var streamContext = new RandomStreamContext(
            configuration.Numerical.MasterSeedUInt64,
            experiment.ExperimentId,
            experiment.ScenarioId,
            trajectoryId,
            episodeId,
            decisionId,
            couplingGroup);
        var streams = new Sha256XoshiroRandomStreamFactory();
        var models = new ScalarReferenceModelFactory().Bind(configuration.Model);
        var configurationHash = new ConfigurationHash(configuration.ExperimentSet.Runs.Single(x => x.ExperimentId == experiment.ExperimentId).EffectiveConfigSha256);
        var weights = WeightProfile(configuration.Consequences);
        var durations = configuration.Actions.Actions.ToDictionary(x => x.Action, x => x.Duration.Value);
        var numericalBatches = new List<T5NumericalBatchEvidence>();
        var semantics = request.T6Semantics;
        var terminal = new T5ScalarTerminalActionEvaluator(models, configuration, streamContext, streams, weights, durations, numericalBatches, semantics?.UseNumericalProtocolV2 == true);
        var policies = CreatePolicies(configuration, terminal, semantics?.DisableEndogenousInformation == true);
        var admissibility = CreateAdmissibility(configuration);
        var supportHasher = new Func<IReadOnlyList<WeightedState<double>>, ArtifactHash>(HashSupport);
        var delay = new WeightedSupportDelayPropagator<double>(models.Transition, models.LimitState, streams, streamContext, supportHasher);
        IConstrainedPolicySelector selector = semantics?.DisableReliabilityConstraint == true ? new T6UnconstrainedPolicySelector() : new ConstrainedPolicySelector();
        var twoStage = new TwoStagePolicyEvaluator<double, DiscreteObservation>(new ActionAdmissibilityEvaluator(), new ReliabilityFeasibilityEvaluator(), selector, new EvsiEvaluator());
        var acquisitionBurden = weights.Weights[ConsequenceComponentKind.IntensifiedMonitoring] + weights.Weights[ConsequenceComponentKind.Downtime] * durations[MaintenanceActionKind.IntensifiedMonitoring] / configuration.Reliability.Horizon.Value;
        var evaluation = twoStage.Evaluate(new(
            policies,
            models.Belief,
            admissibility,
            delay,
            models.MonitorBranchProvider,
            models.MonitorObservation,
            new WeightedSupportBayesUpdater<double, DiscreteObservation>(supportHasher),
            terminal,
            weights,
            new(configuration.Model.MonitorObservation.Missingness.QualityId, false, null),
            new(configuration.Reliability.Epsilon.Value),
            new(semantics?.DisableBranchwiseEpsilonRemaining == true ? 1d : configuration.Reliability.EpsilonRemaining.Value),
            new(Math.Clamp(acquisitionBurden, 0d, 1d)),
            configurationHash), cancellationToken);
        if (evaluation.Decision.Selected is null) throw new InvalidOperationException("T5_PROPOSED_POLICY_NOT_EVALUABLE");

        var direct = evaluation.CompletePolicies.Where(x => x.Policy is DirectPolicy).ToArray();
        var baselineFactory = new TypedBaselineFactory<double>(new ConstrainedPolicySelector(), new Dictionary<string, Func<double, double>>(StringComparer.Ordinal) { ["failure-oriented-state-v1"] = x => x });
        var baselines = baselineFactory.Bind(configuration.Baselines);
        var baselineInput = new BaselineDecisionInput<double>(new(0d), models.Belief, null, x => x, null, direct, false);
        var b7 = Require(baselines.Get("B7-CONSTRAINED-ONE-STAGE"), baselineInput);
        var b4 = Require(baselines.Get("B4-POSTERIOR-QUANTILE"), baselineInput);
        var b1 = Require(baselines.Get("B1-PERIODIC"), baselineInput);
        var b6 = Require(baselines.Get("B6-UNCONSTRAINED-BAYES"), baselineInput);
        var initialTruth = models.Belief.Sample(streams.Create(new(streamContext, StreamPurpose.InitialState, 0UL)));
        var proposed = RealizeProposed(initialTruth, evaluation, models, configuration, streamContext, streams, weights, durations, cancellationToken);
        var b7Prediction = DirectPrediction(direct, b7.Action!.Value);
        var b4Prediction = DirectPrediction(direct, b4.Action!.Value);
        var b1Prediction = DirectPrediction(direct, b1.Action!.Value);
        var b6Prediction = DirectPrediction(direct, b6.Action!.Value);
        var b7Realized = RealizeDirect("B7-CONSTRAINED-ONE-STAGE", initialTruth, b7.Action.Value, models, configuration, streamContext, streams, weights, durations, cancellationToken);
        var b4Realized = RealizeDirect("B4-POSTERIOR-QUANTILE", initialTruth, b4.Action.Value, models, configuration, streamContext, streams, weights, durations, cancellationToken);
        var b1Realized = RealizePeriodic(initialTruth, baselines.Get("B1-PERIODIC"), models, configuration, streamContext, streams, weights, durations, direct, cancellationToken);
        var b6Realized = RealizeDirect("B6-UNCONSTRAINED-BAYES", initialTruth, b6.Action.Value, models, configuration, streamContext, streams, weights, durations, cancellationToken);
        var predictions = new Dictionary<string, CompletePolicyEvaluation>(StringComparer.Ordinal)
        {
            ["P-R4-TWO-STAGE"] = evaluation.Decision.Selected,
            ["B7-CONSTRAINED-ONE-STAGE"] = b7Prediction,
            ["B4-POSTERIOR-QUANTILE"] = b4Prediction,
            ["B1-PERIODIC"] = b1Prediction,
            ["B6-UNCONSTRAINED-BAYES"] = b6Prediction
        };
        var realized = new[] { proposed, b7Realized, b4Realized, b1Realized, b6Realized };
        var selected = evaluation.Decision.Selected;
        var predictedFailure = selected.Reliability.FailureProbability?.Value ?? 0d;
        var planning = new T5PlanningEpisodeInput(
            request.Lineage,
            episodeId,
            selected.Reliability.Diagnostics.EvaluationCount,
            selected.Reliability.Diagnostics.Converged,
            selected.Reliability.Diagnostics.FailureProbabilityStandardError.Value,
            proposed.Loss,
            b7Realized.Loss,
            proposed.Failure.Failed,
            selected.OverallFeasibility.Classification != FeasibilityClassification.ProvenFeasible,
            proposed.Failure.Censoring,
            Math.Clamp((int)Math.Floor(predictedFailure * 10d), 0, 9),
            true,
            proposed.DecisionEpochCount,
            b4Realized.Loss,
            b1Realized.Loss,
            b6Realized.Loss,
            selected.Policy.Id.Value,
            proposed.ActionHistory,
            selected.OverallFeasibility.Classification.ToString(),
            couplingGroup,
            selected.Reliability.Status.ToString(),
            true);
        var records = BuildRawRecords(request, streamContext, models, configurationHash, evaluation, predictions, realized, numericalBatches, weights, durations);
        return new(planning, records);
    }

    private static List<PolicyTree> CreatePolicies(LoadedConfigurationSetV2 configuration, ITerminalActionEvaluator<double> terminal, bool disableEndogenousInformation)
    {
        _ = terminal;
        var horizon = new Horizon(configuration.Reliability.Horizon.Value);
        var monitor = configuration.Actions.Actions.Single(x => x.Action == MaintenanceActionKind.IntensifiedMonitoring);
        var policies = new List<PolicyTree>
        {
            new DirectPolicy(new("P-R4-DIRECT-CONTINUE"), horizon, MaintenanceActionKind.Continue),
            new DirectPolicy(new("P-R4-DIRECT-REPLACE"), horizon, MaintenanceActionKind.PlannedReplacement)
        };
        if (!disableEndogenousInformation)
            policies.Add(new InformationPolicy<DiscreteObservation>(new("P-R4-TWO-STAGE"), horizon, MaintenanceActionKind.IntensifiedMonitoring, new(monitor.Duration.Value), new BinaryTerminalRule()));
        return policies;
    }

    private static ActionAdmissibilityContext CreateAdmissibility(LoadedConfigurationSetV2 configuration)
    {
        var enabled = configuration.Actions.Actions.Where(x => x.Enabled).Select(x => x.Action).ToHashSet();
        var observations = new HashSet<ObservationKind>();
        if (configuration.Model.RoutineObservation.Enabled) observations.Add(ObservationKind.Routine);
        if (configuration.Model.MonitorObservation.Enabled) observations.Add(ObservationKind.IntensifiedMonitoring);
        if (configuration.Model.InspectionObservation.Enabled) observations.Add(ObservationKind.TargetedInspection);
        var durations = configuration.Actions.Actions.ToDictionary(x => x.Action, x => new Exposure(x.Duration.Value));
        return new(new(configuration.Reliability.Horizon.Value), enabled, observations, durations, true, true, true, configuration.Actions.Actions.Single(x => x.Action == MaintenanceActionKind.TargetedInspection).ExposureMode);
    }

    private static BaselineDecision Require(BoundBaselinePolicy<double> baseline, BaselineDecisionInput<double> input)
    {
        if (baseline.Applicability != BaselineApplicabilityStatus.Enabled || baseline.Policy is null) throw new InvalidOperationException("T5_REQUIRED_BASELINE_NOT_ENABLED");
        var decision = baseline.Policy.Evaluate(input);
        if (decision.Status != BaselineStatus.Success || decision.Action is null || decision.OracleAccessCount != 0) throw new InvalidOperationException("T5_REQUIRED_BASELINE_NOT_EVALUABLE");
        return decision;
    }

    private static CompletePolicyEvaluation DirectPrediction(IReadOnlyList<CompletePolicyEvaluation> direct, MaintenanceActionKind action) =>
        direct.Single(x => x.Policy.FirstAction == action);

    private static RealizedPolicyOutcome RealizeProposed(
        double initialState,
        TwoStageEvaluationResult<DiscreteObservation> evaluation,
        ScalarReferenceModelBundle models,
        LoadedConfigurationSetV2 configuration,
        RandomStreamContext context,
        Sha256XoshiroRandomStreamFactory streams,
        ConsequenceWeightProfile weights,
        IReadOnlyDictionary<MaintenanceActionKind, double> durations,
        CancellationToken cancellationToken)
    {
        var selected = evaluation.Decision.Selected ?? throw new InvalidOperationException("T5_PROPOSED_POLICY_NOT_EVALUABLE");
        if (selected.Policy is DirectPolicy direct)
            return RealizeDirect("P-R4-TWO-STAGE", initialState, direct.Action, models, configuration, context, streams, weights, durations, cancellationToken);
        if (selected.Policy is not InformationPolicy<DiscreteObservation> information)
            throw new InvalidOperationException("T5_PROPOSED_POLICY_TYPE_INVALID");
        cancellationToken.ThrowIfCancellationRequested();
        var detector = new FirstPassageDetector<double>();
        var delayEnd = new Exposure(information.AcquisitionDuration.Value);
        var delaySegment = models.Transition.SampleSegment(initialState, new(new(0d), delayEnd), new(information.InformationAction, information.AcquisitionDuration, null), null, streams.Create(new(context, StreamPurpose.DegradationInnovation, 0UL)));
        var delayFailure = detector.Detect(initialState, new(0d), delaySegment, models.LimitState);
        if (delayFailure.Failed)
            return Outcome("P-R4-TWO-STAGE", information.InformationAction.ToString(), new(true, delayFailure.ExactExposure, delayFailure.LowerExposure, delayFailure.UpperExposure, CensoringKind.ExactFailure, delayFailure.Reason), true, false, information.AcquisitionDuration.Value / configuration.Reliability.Horizon.Value, weights, 1);
        var stateAfterDelay = delaySegment.Checkpoints.Count == 0 ? initialState : delaySegment.Checkpoints[^1].State;
        var observationContext = new ObservationContext(configuration.Model.MonitorObservation.Missingness.QualityId, false, null);
        var observation = models.MonitorObservation.Sample(stateAfterDelay, observationContext, streams.Create(new(context, StreamPurpose.MonitorObservation, 0UL)));
        var branch = evaluation.Branches.Single(x => x.BranchId == observation.OutcomeId && x.SelectedTerminal is not null);
        var terminalAction = branch.SelectedTerminal!.Policy.FirstAction;
        var downtime = information.AcquisitionDuration.Value;
        if (terminalAction == MaintenanceActionKind.PlannedReplacement)
        {
            downtime += durations[MaintenanceActionKind.PlannedReplacement];
            return Outcome("P-R4-TWO-STAGE", $"{information.InformationAction}->{terminalAction}", new(false, null, delayEnd, null, CensoringKind.PlannedReplacement, "PLANNED_REPLACEMENT_CLOSES_EPISODE"), true, true, downtime / configuration.Reliability.Horizon.Value, weights, 2);
        }
        var horizon = configuration.Reliability.Horizon.Value;
        var remainingSegment = models.Transition.SampleSegment(stateAfterDelay, new(delayEnd, new(horizon)), new(MaintenanceActionKind.Continue, new(horizon - delayEnd.Value), null), null, streams.Create(new(context, StreamPurpose.DegradationInnovation, 1UL)));
        var failure = detector.Detect(stateAfterDelay, delayEnd, remainingSegment, models.LimitState);
        return Outcome("P-R4-TWO-STAGE", $"{information.InformationAction}->{terminalAction}", failure, true, false, downtime / horizon, weights, 2);
    }

    private static RealizedPolicyOutcome RealizeDirect(
        string policyId,
        double initialState,
        MaintenanceActionKind action,
        ScalarReferenceModelBundle models,
        LoadedConfigurationSetV2 configuration,
        RandomStreamContext context,
        Sha256XoshiroRandomStreamFactory streams,
        ConsequenceWeightProfile weights,
        IReadOnlyDictionary<MaintenanceActionKind, double> durations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action == MaintenanceActionKind.PlannedReplacement)
            return Outcome(policyId, action.ToString(), new(false, null, new(0d), null, CensoringKind.PlannedReplacement, "PLANNED_REPLACEMENT_CLOSES_EPISODE"), false, true, durations[action] / configuration.Reliability.Horizon.Value, weights, 1);
        var horizon = new Exposure(configuration.Reliability.Horizon.Value);
        var segment = models.Transition.SampleSegment(initialState, new(new(0d), horizon), new(action, horizon, null), null, streams.Create(new(context, StreamPurpose.DegradationInnovation, 0UL)));
        var failure = new FirstPassageDetector<double>().Detect(initialState, new(0d), segment, models.LimitState);
        return Outcome(policyId, action.ToString(), failure, action == MaintenanceActionKind.IntensifiedMonitoring, action == MaintenanceActionKind.PlannedReplacement, durations.GetValueOrDefault(action) / configuration.Reliability.Horizon.Value, weights, 1);
    }

    private static RealizedPolicyOutcome RealizePeriodic(
        double initialState,
        BoundBaselinePolicy<double> baseline,
        ScalarReferenceModelBundle models,
        LoadedConfigurationSetV2 configuration,
        RandomStreamContext context,
        Sha256XoshiroRandomStreamFactory streams,
        ConsequenceWeightProfile weights,
        IReadOnlyDictionary<MaintenanceActionKind, double> durations,
        IReadOnlyList<CompletePolicyEvaluation> direct,
        CancellationToken cancellationToken)
    {
        var interval = configuration.Baselines.Baselines.Single(x => x.BaselineId == "B1-PERIODIC").Interval ?? throw new InvalidOperationException("T5_B1_INTERVAL_MISSING");
        var exposure = Math.Min(interval, configuration.Reliability.Horizon.Value);
        var end = new Exposure(exposure);
        var segment = models.Transition.SampleSegment(initialState, new(new(0d), end), new(MaintenanceActionKind.Continue, end, null), null, streams.Create(new(context, StreamPurpose.DegradationInnovation, 0UL)));
        var failure = new FirstPassageDetector<double>().Detect(initialState, new(0d), segment, models.LimitState);
        if (failure.Failed) return Outcome("B1-PERIODIC", "Continue", failure, false, false, 0d, weights, 1);
        if (interval > configuration.Reliability.Horizon.Value)
            return Outcome("B1-PERIODIC", "Continue", failure, false, false, 0d, weights, 1);
        var decision = Require(baseline, new(new(interval), models.Belief, null, x => x, null, direct, false));
        if (decision.Action != MaintenanceActionKind.PlannedReplacement) throw new InvalidOperationException("T5_B1_PERIODIC_ACTION_INVALID");
        return Outcome("B1-PERIODIC", "Continue->PlannedReplacement", new(false, null, end, null, CensoringKind.PlannedReplacement, "PERIODIC_REPLACEMENT_CLOSES_EPISODE"), false, true, durations[MaintenanceActionKind.PlannedReplacement] / configuration.Reliability.Horizon.Value, weights, 2);
    }

    private static RealizedPolicyOutcome Outcome(
        string policyId,
        string actionHistory,
        FailureEvent failure,
        bool monitored,
        bool replaced,
        double downtime,
        ConsequenceWeightProfile weights,
        int decisionEpochCount)
    {
        var path = new PathOutcome(failure.Failed, replaced, monitored, false, Math.Clamp(downtime, 0d, 1d));
        var vector = new BoundedConsequenceEvaluator().Evaluate(path, new("bounded-five-component-binary-plus-normalized-downtime-t5-v1", Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, x => x.ToString())));
        var loss = new ExpectedDisutilityEvaluator().Evaluate([vector], weights);
        return new(policyId, actionHistory, failure, vector, loss.ExpectedDisutility?.Value ?? throw new InvalidOperationException("T5_REALIZED_CONSEQUENCE_NOT_EVALUABLE"), decisionEpochCount);
    }

    private static List<RawEvidenceRecord> BuildRawRecords(
        T5EpisodeGenerationRequest request,
        RandomStreamContext context,
        ScalarReferenceModelBundle models,
        ConfigurationHash configurationHash,
        TwoStageEvaluationResult<DiscreteObservation> evaluation,
        IReadOnlyDictionary<string, CompletePolicyEvaluation> predictions,
        IReadOnlyList<RealizedPolicyOutcome> outcomes,
        IReadOnlyList<T5NumericalBatchEvidence> batches,
        ConsequenceWeightProfile weights,
        IReadOnlyDictionary<MaintenanceActionKind, double> durations)
    {
        var records = new List<RawEvidenceRecord>();
        var runId = request.RunId;
        var experiment = request.Experiment;
        var episodeId = request.Split.EpisodeId;
        var trajectoryId = context.TrajectoryId.Value;
        var decisionId = context.DecisionId.Value;
        var streamHash = StreamHash(context, StreamPurpose.DegradationInnovation, 0UL);
        var actionHash = T5PilotExecutionService.Hash(string.Join('|', outcomes.Select(x => $"{x.PolicyId}:{x.ActionHistory}")));
        var admissionInput = new DeployableDecisionInput<string>(
            context.DecisionId,
            context.ComponentEpisodeId,
            new(0d),
            new(request.Configuration.Reliability.Horizon.Value),
            new("POST-" + episodeId, models.Belief.Version, models.Belief.ContentHash),
            request.Configuration.Model.RoutineObservation.Missingness.QualityId,
            [],
            [],
            new(T5PilotExecutionService.Hash(string.Join('|', request.Configuration.Compatibility.ComponentHashes.Values.OrderBy(x => x, StringComparer.Ordinal)))),
            configurationHash,
            0);
        var admission = new DeployableInformationAdmission().Admit(admissionInput);

        records.Add(new SplitManifestRecord()
            .Set("component_episode_id", episodeId)
            .Set("source_dataset_id", request.Split.SourceDatasetId)
            .Set("split", request.Split.Partition)
            .Set("grouping_key", request.Split.GroupingKey)
            .Set("stratum", request.Split.Stratum)
            .Set("reason", request.Split.Reason)
            .Set("manifest_version", request.Split.ManifestVersion)
            .Set("source_hash", request.Split.SourceHash));
        records.Add(new PosteriorArtifactManifestRecord()
            .Set("posterior_artifact_id", "POST-" + episodeId)
            .Set("decision_id", decisionId)
            .Set("component_episode_id", episodeId)
            .Set("posterior_version", models.Belief.Version.Value)
            .Set("state_space_id", models.Belief.StateSpace.Id)
            .Set("representation_kind", "WeightedSupport")
            .Set("support_count", models.Belief.TryGetWeightedSupport(out var support) ? support.Count : 0)
            .Set("content_path", "raw/SCH-PST.csv")
            .Set("content_hash", models.Belief.ContentHash)
            .Set("normalization_status", models.Belief.Validate().Status));
        var proposed = outcomes.Single(x => x.PolicyId == "P-R4-TWO-STAGE");
        records.Add(new TrajectoryManifestRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("scenario_id", experiment.ScenarioId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("coupling_group_id", context.CouplingGroupId)
            .Set("component_episode_id", episodeId)
            .Set("renewal_chain_id", episodeId + "-RENEWAL")
            .Set("partition", experiment.Partition)
            .Set("source_dataset_id", request.Split.SourceDatasetId)
            .Set("start_exposure", 0d)
            .Set("end_exposure", proposed.Failure.ExactExposure?.Value ?? proposed.Failure.LowerExposure?.Value ?? request.Configuration.Reliability.Horizon.Value)
            .Set("outcome_type", proposed.Failure.Failed ? "Failure" : proposed.Failure.Censoring.ToString())
            .Set("artifact_label", experiment.SyntheticOrModelBasedLabel)
            .Set("master_seed", request.Configuration.Numerical.MasterSeedUInt64)
            .Set("stream_context_hash", streamHash)
            .Set("trajectory_status", "COMPLETE"));
        records.Add(new DecisionInputRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("scenario_id", experiment.ScenarioId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("component_episode_id", episodeId)
            .Set("decision_id", decisionId)
            .Set("decision_exposure", 0d)
            .Set("horizon", request.Configuration.Reliability.Horizon.Value)
            .Set("posterior_artifact_id", admissionInput.Posterior.ArtifactId)
            .Set("posterior_version", admissionInput.Posterior.Version.Value)
            .Set("transition_version", models.Transition.Version.Value)
            .Set("routine_observation_version", models.RoutineObservation.Version.Value)
            .Set("monitor_observation_version", models.MonitorObservation.Version.Value)
            .Set("inspection_observation_version", models.InspectionObservation?.Version.Value)
            .Set("limit_state_version", models.LimitState.Version.Value)
            .Set("reset_version", models.Reset.Version.Value)
            .Set("consequence_profile_id", weights.ProfileId)
            .Set("quality_summary_id", request.Configuration.Model.MonitorObservation.Missingness.QualityId)
            .Set("missingness_summary_id", request.Configuration.Model.MonitorObservation.Missingness.Mode)
            .Set("action_history_hash", actionHash)
            .Set("observed_context_id", request.Configuration.Model.ExposureClockId.Value)
            .Set("oracle_access_count", admissionInput.OracleAccessCount)
            .Set("configuration_hash", configurationHash)
            .Set("admission_status", admission.Status)
            .Set("admission_reason", admission.Reason));

        foreach (var pair in predictions.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var prediction = pair.Value;
            records.Add(PolicyRecord(runId, experiment, trajectoryId, episodeId, decisionId, pair.Key, prediction, configurationHash));
            records.Add(ReliabilityRecord(runId, experiment, trajectoryId, decisionId, pair.Key, prediction, configurationHash, streamHash));
        }
        foreach (var branch in evaluation.Branches.OrderBy(x => x.BranchId, StringComparer.Ordinal))
            records.Add(BranchRecord(runId, experiment, trajectoryId, decisionId, branch, request.Configuration, streamHash));

        var selected = evaluation.Decision.Selected!;
        records.Add(new DecisionRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("component_episode_id", episodeId)
            .Set("decision_id", decisionId)
            .Set("decision_exposure", 0d)
            .Set("selected_policy_id", selected.Policy.Id.Value)
            .Set("selected_first_action", selected.Policy.FirstAction)
            .Set("decision_status", evaluation.Decision.Status)
            .Set("reason_code", evaluation.Decision.Reason)
            .Set("constraint_breach", evaluation.Decision.Status == DecisionStatus.ConstraintBreach)
            .Set("numerical_tie", evaluation.Decision.Status == DecisionStatus.SuccessNumericalTie)
            .Set("excluded_unresolved_policy_count", evaluation.CompletePolicies.Count(x => x.OverallFeasibility.Classification is FeasibilityClassification.Unresolved or FeasibilityClassification.BoundaryUncertified))
            .Set("policy_evaluation_count", evaluation.CompletePolicies.Count)
            .Set("observation_branch_count", evaluation.Branches.Count)
            .Set("evaluation_elapsed_ms", null)
            .Set("competitor_set_hash", T5PilotExecutionService.Hash(string.Join('|', evaluation.CompletePolicies.Select(x => x.Policy.Id.Value).OrderBy(x => x, StringComparer.Ordinal))))
            .Set("selected_failure_probability", selected.Reliability.FailureProbability?.Value)
            .Set("selected_expected_disutility", selected.Disutility.ExpectedDisutility?.Value)
            .Set("next_decision_exposure", request.Configuration.Reliability.Horizon.Value)
            .Set("evsi_record_id", evaluation.Evsi is null ? null : decisionId + "-EVSI")
            .Set("oracle_access_count", evaluation.OracleAccessCount)
            .Set("configuration_hash", configurationHash));

        foreach (var outcome in outcomes.OrderBy(x => x.PolicyId, StringComparer.Ordinal))
        {
            records.Add(FailureRecord(runId, experiment, trajectoryId, episodeId, outcome, models, request.T6Semantics is null ? "DIRECT_KNOWN_TRUTH_T5_ONLY" : "DIRECT_KNOWN_TRUTH_T6_MODEL_BASED"));
            records.Add(ConsequenceRecord(runId, experiment, trajectoryId, episodeId, decisionId, outcome, weights, request.Configuration.Consequences.DefinitionVersion));
        }
        foreach (var batch in batches)
            records.Add(NumericalRecord(runId, batch));
        return records;
    }

    private static PolicyEvaluationRecord PolicyRecord(string runId, EffectiveExperimentConfigV2 experiment, string trajectoryId, string episodeId, string decisionId, string policyId, CompletePolicyEvaluation value, ConfigurationHash hash) =>
        (PolicyEvaluationRecord)new PolicyEvaluationRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("scenario_id", experiment.ScenarioId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("component_episode_id", episodeId)
            .Set("decision_id", decisionId)
            .Set("policy_id", policyId)
            .Set("first_stage_action", value.Policy.FirstAction)
            .Set("information_action", value.Policy is InformationPolicy<DiscreteObservation> info ? info.InformationAction : null)
            .Set("terminal_rule_id", value.Policy is InformationPolicy<DiscreteObservation> ? "binary-observation-constrained-terminal-v1" : null)
            .Set("admissible", value.Admissibility.IsAdmissible)
            .Set("admissibility_reason", value.Admissibility.Reason)
            .Set("failure_probability", value.Reliability.FailureProbability?.Value)
            .Set("reliability", value.Reliability.Reliability?.Value)
            .Set("failure_lower_bound", value.Reliability.FailureLowerNumericalBound?.Value)
            .Set("failure_upper_bound", value.Reliability.FailureUpperNumericalBound?.Value)
            .Set("expected_disutility", value.Disutility.ExpectedDisutility?.Value)
            .Set("disutility_standard_error", value.Disutility.StandardError.Value)
            .Set("expected_failure_component", value.Disutility.ComponentExpectations.GetValueOrDefault(ConsequenceComponentKind.Failure))
            .Set("expected_replacement_component", value.Disutility.ComponentExpectations.GetValueOrDefault(ConsequenceComponentKind.PlannedReplacement))
            .Set("expected_monitor_component", value.Disutility.ComponentExpectations.GetValueOrDefault(ConsequenceComponentKind.IntensifiedMonitoring))
            .Set("expected_inspection_component", value.Disutility.ComponentExpectations.GetValueOrDefault(ConsequenceComponentKind.TargetedInspection))
            .Set("expected_downtime_component", value.Disutility.ComponentExpectations.GetValueOrDefault(ConsequenceComponentKind.Downtime))
            .Set("feasibility_status", value.OverallFeasibility.Classification)
            .Set("violation_margin", double.IsFinite(value.OverallFeasibility.ViolationMargin) ? value.OverallFeasibility.ViolationMargin : null)
            .Set("branch_feasibility_status", value.BranchFeasibility?.Classification.ToString() ?? "NotApplicable")
            .Set("path_count", value.Reliability.Diagnostics.EvaluationCount)
            .Set("batch_count", value.Reliability.Diagnostics.BatchCount)
            .Set("method_id", value.Reliability.Diagnostics.MethodId)
            .Set("numerical_status", value.Reliability.Status)
            .Set("diagnostics_id", value.Reliability.Diagnostics.ToleranceProfileId)
            .Set("configuration_hash", hash);

    private static ReliabilityPredictionRecord ReliabilityRecord(string runId, EffectiveExperimentConfigV2 experiment, string trajectoryId, string decisionId, string policyId, CompletePolicyEvaluation value, ConfigurationHash hash, ArtifactHash streamHash) =>
        (ReliabilityPredictionRecord)new ReliabilityPredictionRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("decision_id", decisionId)
            .Set("policy_id", policyId)
            .Set("horizon", value.Policy.Horizon.Value)
            .Set("failure_probability", value.Reliability.FailureProbability?.Value)
            .Set("reliability", value.Reliability.Reliability?.Value)
            .Set("failure_count", value.Reliability.FailureProbability is null ? null : (long?)Math.Round(value.Reliability.FailureProbability.Value.Value * value.Reliability.Diagnostics.EvaluationCount, MidpointRounding.AwayFromZero))
            .Set("path_count", value.Reliability.Diagnostics.EvaluationCount)
            .Set("failure_standard_error", value.Reliability.Diagnostics.FailureProbabilityStandardError.Value)
            .Set("failure_lcb", value.Reliability.FailureLowerNumericalBound?.Value)
            .Set("failure_ucb", value.Reliability.FailureUpperNumericalBound?.Value)
            .Set("method_id", value.Reliability.Diagnostics.MethodId)
            .Set("batch_count", value.Reliability.Diagnostics.BatchCount)
            .Set("converged", value.Reliability.Diagnostics.Converged)
            .Set("crossing_resolution_id", "first-passage-g-le-zero-v1")
            .Set("stream_context_hash", streamHash)
            .Set("prediction_status", value.Reliability.Status)
            .Set("configuration_hash", hash);

    private static InformationBranchRecord BranchRecord(string runId, EffectiveExperimentConfigV2 experiment, string trajectoryId, string decisionId, TwoStageBranchEvaluation<DiscreteObservation> branch, LoadedConfigurationSetV2 configuration, ArtifactHash streamHash) =>
        (InformationBranchRecord)new InformationBranchRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("decision_id", decisionId)
            .Set("policy_id", "P-R4-TWO-STAGE")
            .Set("branch_id", branch.BranchId)
            .Set("information_action", MaintenanceActionKind.IntensifiedMonitoring)
            .Set("observation_group_id", configuration.Model.MonitorObservation.Missingness.QualityId)
            .Set("branch_weight", branch.Probability.Value)
            .Set("branch_sample_count", branch.SelectedTerminal?.Reliability.Diagnostics.EvaluationCount ?? 0L)
            .Set("posterior_after_hash", branch.PosteriorHash ?? T5PilotExecutionService.Hash($"{experiment.ExperimentId.Value}|{branch.BranchId}|POSTERIOR_NOT_AVAILABLE"))
            .Set("terminal_continue_j", null)
            .Set("terminal_replace_j", null)
            .Set("terminal_action", branch.SelectedTerminal?.Policy.FirstAction)
            .Set("branch_failure_probability", branch.SelectedTerminal?.Reliability.FailureProbability?.Value)
            .Set("branch_failure_ucb", branch.SelectedTerminal?.Reliability.FailureUpperNumericalBound?.Value)
            .Set("branch_feasible", branch.BranchFeasibility.Classification == FeasibilityClassification.ProvenFeasible)
            .Set("acquisition_burden_charged", configuration.Consequences.Weights.Value[ConsequenceComponentKind.IntensifiedMonitoring])
            .Set("decision_changed", null)
            .Set("branch_status", branch.PosteriorStatus)
            .Set("stream_context_hash", streamHash);

    private static FailureEventRecord FailureRecord(string runId, EffectiveExperimentConfigV2 experiment, string trajectoryId, string episodeId, RealizedPolicyOutcome outcome, ScalarReferenceModelBundle models, string observationStatus) =>
        (FailureEventRecord)new FailureEventRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("scenario_id", experiment.ScenarioId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("component_episode_id", episodeId)
            .Set("policy_id", outcome.PolicyId)
            .Set("event_type", outcome.Failure.Failed ? "FIRST_PASSAGE_FAILURE" : outcome.Failure.Censoring.ToString())
            .Set("first_passage_detected", outcome.Failure.Failed)
            .Set("event_exposure_exact", outcome.Failure.ExactExposure?.Value)
            .Set("event_exposure_lower", outcome.Failure.LowerExposure?.Value)
            .Set("event_exposure_upper", outcome.Failure.UpperExposure?.Value)
            .Set("margin_before", null)
            .Set("margin_at_or_after", null)
            .Set("limit_state_version", models.LimitState.Version.Value)
            .Set("replacement_occurred", outcome.Consequence.Components[ConsequenceComponentKind.PlannedReplacement] > 0d)
            .Set("new_episode_id", outcome.Consequence.Components[ConsequenceComponentKind.PlannedReplacement] > 0d ? episodeId + "-RESET" : null)
            .Set("censoring_type", outcome.Failure.Censoring)
            .Set("outcome_observation_status", observationStatus)
            .Set("artifact_label", experiment.SyntheticOrModelBasedLabel);

    private static ConsequenceRecord ConsequenceRecord(string runId, EffectiveExperimentConfigV2 experiment, string trajectoryId, string episodeId, string decisionId, RealizedPolicyOutcome outcome, ConsequenceWeightProfile weights, string definitionVersion) =>
        (ConsequenceRecord)new ConsequenceRecord()
            .Set("run_id", runId)
            .Set("experiment_id", experiment.ExperimentId.Value)
            .Set("trajectory_id", trajectoryId)
            .Set("component_episode_id", episodeId)
            .Set("decision_id", decisionId)
            .Set("policy_id", outcome.PolicyId)
            .Set("failure_occurred", outcome.Failure.Failed)
            .Set("planned_replacement_burden", outcome.Consequence.Components[ConsequenceComponentKind.PlannedReplacement])
            .Set("monitor_burden", outcome.Consequence.Components[ConsequenceComponentKind.IntensifiedMonitoring])
            .Set("inspection_burden", outcome.Consequence.Components[ConsequenceComponentKind.TargetedInspection])
            .Set("downtime_burden", outcome.Consequence.Components[ConsequenceComponentKind.Downtime])
            .Set("realized_disutility", outcome.Loss)
            .Set("weight_profile_id", weights.ProfileId)
            .Set("component_definition_version", definitionVersion)
            .Set("corrective_response", outcome.Consequence.Components[ConsequenceComponentKind.PlannedReplacement] > 0d)
            .Set("consequence_status", "SUCCESS");

    private static NumericalBatchRecord NumericalRecord(string runId, T5NumericalBatchEvidence value) =>
        (NumericalBatchRecord)new NumericalBatchRecord()
            .Set("run_id", runId)
            .Set("decision_id", value.Snapshot.DecisionId.Value)
            .Set("policy_id", value.Snapshot.PolicyId.Value)
            .Set("branch_id", null)
            .Set("batch_index", value.Snapshot.BatchIndex)
            .Set("path_index_start", value.Snapshot.PathIndexStart)
            .Set("path_index_end", value.Snapshot.PathIndexEnd)
            .Set("batch_failure_count", value.Snapshot.BatchFailureCount)
            .Set("cumulative_failure_count", value.Snapshot.CumulativeFailureCount)
            .Set("cumulative_path_count", value.Snapshot.CumulativePathCount)
            .Set("failure_estimate", value.Snapshot.FailureEstimate)
            .Set("failure_lcb", value.Snapshot.FailureLowerBound)
            .Set("failure_ucb", value.Snapshot.FailureUpperBound)
            .Set("disutility_mean", value.DisutilityMean)
            .Set("disutility_m2", value.DisutilityM2)
            .Set("classification", value.Snapshot.Classification)
            .Set("stable_batch_count", value.Snapshot.StableBatchCount)
            .Set("batch_elapsed_ms", null)
            .Set("worker_count", 1)
            .Set("stream_context_hash", value.StreamContextHash);

    private static ConsequenceWeightProfile WeightProfile(ConsequenceConfigV2 configuration) => new(
        configuration.WeightProfileId,
        configuration.Weights.OwnerId,
        configuration.WeightProfileStatus,
        configuration.WeightProfileVersion,
        null,
        configuration.Weights.Value,
        new(configuration.WeightProfileSha256));

    private static ArtifactHash HashSupport(IReadOnlyList<WeightedState<double>> support) =>
        T5PilotExecutionService.Hash(string.Join('|', support.Select(x => FormattableString.Invariant($"{x.State:R}:{x.Weight:R}"))));

    private static ArtifactHash StreamHash(RandomStreamContext context, StreamPurpose purpose, ulong index)
    {
        var streams = new Sha256XoshiroRandomStreamFactory();
        return new(Convert.ToHexString(SHA256.HashData(streams.EncodeKey(new(context, purpose, index)))));
    }

    private sealed class BinaryTerminalRule : IConstrainedTerminalRule<DiscreteObservation>
    {
        public MaintenanceActionKind Select(DiscreteObservation observation) => observation.BinaryValue == 1 ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue;
    }

    private sealed record RealizedPolicyOutcome(
        string PolicyId,
        string ActionHistory,
        FailureEvent Failure,
        ConsequenceVector Consequence,
        double Loss,
        int DecisionEpochCount);
}
