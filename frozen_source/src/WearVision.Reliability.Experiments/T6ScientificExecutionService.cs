using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Experiments;

public sealed record T6ProductionExecutionResultV1(
    int ExitCode,
    string Status,
    string RunRoot,
    IReadOnlyList<string> ExecutedRows,
    int AccountedEpisodes,
    string? CompletionManifestSha256,
    string ErrorCode,
    bool AuthorizationConsumed,
    bool ExecutionStarted,
    bool RealT6Executed);

public sealed class T6ScientificExecutionService
{
    private readonly IT5ProductionEpisodeGenerator _episodeGenerator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IT6ResourceProbe _resourceProbe;
    private readonly Func<string, bool>? _writerFailureInjector;

    public T6ScientificExecutionService(
        IT5ProductionEpisodeGenerator? episodeGenerator = null,
        Func<DateTimeOffset>? clock = null,
        IT6ResourceProbe? resourceProbe = null,
        Func<string, bool>? writerFailureInjector = null)
    {
        _episodeGenerator = episodeGenerator ?? new T5ProductionEpisodeGenerator();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _resourceProbe = resourceProbe ?? new T6ProcessResourceProbe();
        _writerFailureInjector = writerFailureInjector;
    }

    public T6ValidationReportV1 Validate(
        string runtimeConfigSetPath,
        string authorizationPath,
        string sourceSnapshotPath,
        string executablePath,
        string runId)
    {
        using var package = T6PackageMaterialization.Open(runtimeConfigSetPath);
        var authorization = ReadAuthorization(authorizationPath);
        var identity = T6ExecutionIdentityV1.Capture(sourceSnapshotPath, executablePath);
        var report = new T6AuthorizationValidatorV1().Validate(package, authorization, identity, runId);
        var issues = report.Issues.ToList();
        var runRoot = Path.Combine(package.Configuration.Output.ApprovedRoot, runId);
        if (Directory.Exists(runRoot) || File.Exists(runRoot)) issues.Add("T6_OUTPUT_DIRECTORY_EXISTS");
        var storeRoot = Path.Combine(package.Configuration.Output.ApprovedRoot, ".authorization-consumption");
        if (T6AuthorizationConsumptionStoreV1.IsConsumed(storeRoot, authorizationPath, authorization.AuthorizationId))
            issues.Add("T6_AUTHORIZATION_ALREADY_CONSUMED");
        ValidateRuntimeConfiguration(package, issues);
        return new(issues.Count == 0, false, issues);
    }

    public T6ValidationReportV1 ValidateConfiguration(string runtimeConfigSetPath)
    {
        using var package = T6PackageMaterialization.Open(runtimeConfigSetPath);
        var issues = new List<string>();
        ValidateRuntimeConfiguration(package, issues);
        if (!package.Bridge.RuntimeCommandPresent || package.Bridge.RuntimeCommand != "t6 execute") issues.Add("T6_RUNTIME_COMMAND_MISSING");
        if (package.Bridge.SourceLeafFieldCount <= 0 || package.Bridge.SourceLeafFieldCount != package.Bridge.BindingEntryCount) issues.Add("T6_BRIDGE_MAPPING_INCOMPLETE");
        try
        {
            T6RuntimeSetGateV1.Validate(package.Configuration.ExperimentSet.Runs.Select(x => x.ExperimentId.Value).ToArray(), package.Configuration.EffectiveExperiments.Values.Sum(x => x.EpisodeCount), package.Bridge.FixtureOnly);
        }
        catch (InvalidDataException exception)
        {
            issues.Add(exception.Message);
        }
        return new(issues.Count == 0, false, issues);
    }

    public async Task<T6ProductionExecutionResultV1> ExecuteAsync(
        string runtimeConfigSetPath,
        string authorizationPath,
        string sourceSnapshotPath,
        string executablePath,
        string runId,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var package = T6PackageMaterialization.Open(runtimeConfigSetPath);
        var authorization = ReadAuthorization(authorizationPath);
        var identity = T6ExecutionIdentityV1.Capture(sourceSnapshotPath, executablePath);
        var initialReport = new T6AuthorizationValidatorV1().Validate(package, authorization, identity, runId);
        var issues = initialReport.Issues.ToList();
        var configuration = package.Configuration;
        var runRoot = Path.Combine(configuration.Output.ApprovedRoot, runId);
        if (Directory.Exists(runRoot) || File.Exists(runRoot)) issues.Add("T6_OUTPUT_DIRECTORY_EXISTS");
        var storeRoot = Path.Combine(configuration.Output.ApprovedRoot, ".authorization-consumption");
        if (T6AuthorizationConsumptionStoreV1.IsConsumed(storeRoot, authorizationPath, authorization.AuthorizationId)) issues.Add("T6_AUTHORIZATION_ALREADY_CONSUMED");
        ValidateRuntimeConfiguration(package, issues);
        if (issues.Count != 0)
            return new(4, "PREFLIGHT_REJECTED", string.Empty, [], 0, null, issues[0], false, false, false);

        var consumption = new T6AuthorizationConsumptionStoreV1(_clock).Consume(storeRoot, authorizationPath, authorization, runId);
        var registryPath = Path.Combine(configuration.Root, T6PackageMaterialization.FindRole(configuration.Manifest, "raw-schema-registry"));
        var registry = RawSchemaRegistry.Load(registryPath);
        T6EvidenceWriteSessionV1? writer = null;
        var executedRows = new List<string>();
        var accountedEpisodes = new List<string>();
        var expectedEpisodes = ExpectedEpisodes(configuration);
        var protocolCalls = 0;
        var pairedInputs = 0;
        var bootstrapInputs = 0;
        var calibrationInputs = 0;
        var ablationInputs = 0;
        var fallbackCalls = 0;
        var start = _clock().ToUniversalTime();

        try
        {
            writer = new(configuration.Output.ApprovedRoot, runId, T6FrozenConstants.MaximumStorageBytes, registry, _writerFailureInjector);
            var guard = new T6ResourceGuardV1(T6FrozenConstants.MaximumWallClockSeconds, T6FrozenConstants.MaximumStorageBytes, _resourceProbe, () => writer.CommittedBytes);
            writer.WriteJson("authorization/authorization-consumption.json", consumption);
            writer.WriteJson("environment/environment-manifest.json", new
            {
                SchemaVersion = "t6-environment-v1",
                RunId = runId,
                Os = Environment.OSVersion.ToString(),
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                DotnetRuntime = Environment.Version.ToString(),
                TargetFramework = configuration.Compatibility.TargetFramework,
                Culture = configuration.Output.Culture,
                Timezone = TimeZoneInfo.Local.Id,
                MaximumDegreeOfParallelism = 1,
                FixtureOnly = package.Bridge.FixtureOnly
            });

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(T6FrozenConstants.MaximumWallClockSeconds));
            foreach (var run in configuration.ExperimentSet.Runs)
            {
                var rowCheck = guard.Check("ROW:" + run.ExperimentId.Value);
                writer.WriteJsonLine("resources/resource-checks.jsonl", rowCheck);
                var effective = configuration.EffectiveExperiments[run.ExperimentId];
                var binding = effective.T6Binding ?? throw new InvalidDataException("T6_ROW_BINDING_MISSING:" + run.ExperimentId.Value);
                var assignments = configuration.Split.Assignments
                    .Where(x => x.EpisodeId.StartsWith(run.ExperimentId.Value + "-", StringComparison.Ordinal))
                    .OrderBy(x => x.EpisodeId, StringComparer.Ordinal)
                    .ToArray();
                if (assignments.Length != effective.EpisodeCount) throw new InvalidOperationException("T6_ROW_EPISODE_ACCOUNTING_MISMATCH");
                var rowEpisodes = 0;
                foreach (var assignment in assignments)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var episodeCheck = guard.Check("EPISODE:" + assignment.EpisodeId);
                    writer.WriteJsonLine("resources/resource-checks.jsonl", episodeCheck);
                    var lineage = new T5PlanningLineage(effective.ExperimentId, run.EffectiveConfigSha256, runId, configuration.Manifest.ProfileKind, authorization.AuthorizationId, false, true, false);
                    T5EpisodeExecutionResult result;
                    T5EpisodeExecutionResult? control = null;
                    if (binding.Ablation.ExecutePairedControl)
                    {
                        var controlConfiguration = BindRowConfiguration(configuration, binding, ablatedVariant: false);
                        control = Generate(controlConfiguration, effective, assignment, lineage, runId, new("CONTROL", true, false, false, false), deadline.Token);
                        protocolCalls++;
                        fallbackCalls += WriteEpisodeEvidence(writer, guard, binding, assignment, control, "control", runId);
                        WriteProtocolCall(writer, binding, assignment.EpisodeId, "CONTROL", control, package.Bridge.FixtureOnly);
                    }
                    var variantConfiguration = BindRowConfiguration(configuration, binding, ablatedVariant: true);
                    var semantics = new T6EpisodeExecutionSemanticsV1(
                        binding.Ablation.AblationId ?? "MAIN",
                        true,
                        binding.Ablation.DisableEndogenousInformation,
                        binding.Ablation.DisableReliabilityConstraint,
                        binding.Ablation.DisableBranchwiseEpsilonRemaining);
                    result = Generate(variantConfiguration, effective, assignment, lineage, runId, semantics, deadline.Token);
                    protocolCalls++;
                    fallbackCalls += WriteEpisodeEvidence(writer, guard, binding, assignment, result, binding.Ablation.AblationId ?? "main", runId);
                    WriteProtocolCall(writer, binding, assignment.EpisodeId, semantics.VariantId, result, package.Bridge.FixtureOnly);

                    writer.WriteJsonLine("episodes/episode-records.jsonl", new
                    {
                        SchemaVersion = "t6-episode-record-v1",
                        RunId = runId,
                        binding.RowId,
                        ComponentEpisodeId = assignment.EpisodeId,
                        assignment.GroupingKey,
                        binding.Scenario.ScenarioId,
                        ProtocolVersion = binding.NumericalProtocol.ProtocolVersion,
                        NumericalConverged = result.PlanningInput.NumericalConverged,
                        Complete = result.PlanningInput.Complete,
                        FixtureOnly = package.Bridge.FixtureOnly
                    });
                    writer.WriteJsonLine("actions/action-records.jsonl", new
                    {
                        SchemaVersion = "t6-action-record-v1",
                        RunId = runId,
                        binding.RowId,
                        ComponentEpisodeId = assignment.EpisodeId,
                        VariantId = semantics.VariantId,
                        result.PlanningInput.ProposedPolicyId,
                        ActionHistory = result.PlanningInput.ProposedAction,
                        result.PlanningInput.DecisionEpochCount
                    });

                    if (binding.RowId == "T6-S0-PRIMARY")
                    {
                        pairedInputs += WritePrimaryContrasts(writer, runId, binding, assignment, result.PlanningInput);
                        bootstrapInputs += 3;
                    }
                    if (control is not null)
                    {
                        WriteAblationInput(writer, runId, binding, assignment, control.PlanningInput, result.PlanningInput);
                        pairedInputs++;
                        bootstrapInputs++;
                        ablationInputs++;
                    }
                    writer.WriteJsonLine("statistics/calibration-reliability-inputs.jsonl", new
                    {
                        SchemaVersion = "t6-calibration-input-v1",
                        RunId = runId,
                        binding.RowId,
                        ComponentEpisodeId = assignment.EpisodeId,
                        ClusterId = assignment.GroupingKey,
                        ObservedFirstPassage = result.PlanningInput.FailureEvent,
                        result.PlanningInput.CalibrationBin,
                        result.PlanningInput.ConstraintViolation,
                        result.PlanningInput.CensoringKind,
                        result.PlanningInput.Complete
                    });
                    calibrationInputs++;
                    accountedEpisodes.Add(assignment.EpisodeId);
                    rowEpisodes++;
                }
                writer.WriteJson("row-manifests/" + binding.RowId + ".json", new
                {
                    SchemaVersion = "t6-row-manifest-v1",
                    RunId = runId,
                    binding.RowId,
                    binding.Scenario,
                    AuthorizedEpisodeCount = binding.EpisodeCount,
                    AccountedEpisodeCount = rowEpisodes,
                    binding.PolicyBaselineAllowlist,
                    binding.Ablation,
                    binding.NumericalProtocol,
                    binding.Rng,
                    binding.Outcomes,
                    binding.StatisticalRole,
                    binding.ClaimRoles,
                    binding.ResourcePolicy,
                    binding.OutputSubroot,
                    binding.EvidenceClass,
                    binding.ParentHashes,
                    Completed = rowEpisodes == binding.EpisodeCount
                });
                executedRows.Add(binding.RowId);
            }

            writer.WriteJson("numerical/deterministic-fallback-summary.json", new
            {
                SchemaVersion = "t6-deterministic-fallback-summary-v1",
                ProtocolVersion = T6FrozenConstants.NumericalProtocol,
                FallbackMethodId = DeterministicScalarUniformFirstPassageFallback.MethodId,
                FallbackCallCount = fallbackCalls,
                SeparateCallLog = "numerical/deterministic-fallback-calls.jsonl",
                LoggingRequired = true
            });
            writer.WriteJson("lineage/result-lineage.json", new
            {
                SchemaVersion = "t6-result-lineage-v1",
                RunId = runId,
                AuthorizationId = authorization.AuthorizationId,
                ParentAuthorizationId = authorization.ParentAuthorizationId,
                ParentAuthorizationStatus = authorization.ParentAuthorizationStatus,
                RuntimeConfigSetSha256 = package.PackageSha256,
                RuntimeConfigManifestSha256 = configuration.ConfigSetSha256,
                identity.SourceSnapshotSha256,
                identity.ExecutableSha256,
                package.Bridge.ParentEr06CSourceSha256,
                package.Bridge.ParentEr07AConfigZipSha256,
                package.Bridge.UpdSourceGateRegisterSha256,
                Rows = executedRows,
                EpisodeCount = accountedEpisodes.Count,
                ScientificEligibility = authorization.ScientificEligibility,
                ManuscriptEligibility = false,
                FixtureOnly = package.Bridge.FixtureOnly
            });
            var end = _clock().ToUniversalTime();
            writer.WriteJson("run/run-manifest.json", new
            {
                SchemaVersion = "t6-run-manifest-v1",
                RunId = runId,
                ExecutionStage = "T6",
                AuthorizationId = authorization.AuthorizationId,
                StartUtc = start,
                EndUtc = end,
                CompletionStatus = "COMPLETE_PENDING_FINAL_CLOSURE",
                ProtocolVersion = T6FrozenConstants.NumericalProtocol,
                RuntimeConfigSetSha256 = package.PackageSha256,
                SourceSnapshotSha256 = identity.SourceSnapshotSha256,
                ExecutableSha256 = identity.ExecutableSha256,
                Rows = executedRows,
                EpisodeCount = accountedEpisodes.Count,
                AuthorizationConsumed = true,
                ScientificEligibility = authorization.ScientificEligibility,
                ManuscriptEligibility = false,
                FixtureOnly = package.Bridge.FixtureOnly
            });
            var finalCheck = guard.Check("COMPLETION_GATE");
            writer.WriteJsonLine("resources/resource-checks.jsonl", finalCheck);
            var completionState = new T6CompletionStateV1(
                authorization.AuthorizedRows.Select(x => x.RowId).ToArray(),
                executedRows,
                expectedEpisodes,
                accountedEpisodes,
                true,
                pairedInputs > 0 && bootstrapInputs > 0 && calibrationInputs > 0,
                true,
                false,
                false,
                protocolCalls,
                pairedInputs,
                bootstrapInputs,
                calibrationInputs,
                ablationInputs);
            var completion = writer.Complete(completionState, authorization.ScientificEligibility, false);
            return new(0, "T6_PRODUCTION_EXECUTION_COMPLETE", writer.Root, executedRows, accountedEpisodes.Count, completion.Sha256Hex, string.Empty, true, true, !package.Bridge.FixtureOnly);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException or ArgumentException or OperationCanceledException)
        {
            var code = string.IsNullOrWhiteSpace(exception.Message) ? "T6_EXECUTION_FAILED" : exception.Message;
            if (writer is not null)
            {
                try
                {
                    writer.WriteJson("failure/structured-failure.json", new
                    {
                        SchemaVersion = "t6-structured-failure-v1",
                        RunId = runId,
                        Status = "INCOMPLETE",
                        ErrorCode = code,
                        ExceptionType = exception.GetType().Name,
                        ExecutedRows = executedRows,
                        AccountedEpisodes = accountedEpisodes.Count,
                        AuthorizationConsumed = true,
                        CompletionManifestWritten = false
                    });
                }
                catch (Exception writeException) when (writeException is IOException or InvalidOperationException)
                {
                }
            }
            return new(5, "INCOMPLETE", writer?.Root ?? string.Empty, executedRows, accountedEpisodes.Count, null, code, true, true, false);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private T5EpisodeExecutionResult Generate(
        LoadedConfigurationSetV2 configuration,
        EffectiveExperimentConfigV2 effective,
        SplitAssignmentV2 assignment,
        T5PlanningLineage lineage,
        string runId,
        T6EpisodeExecutionSemanticsV1 semantics,
        CancellationToken cancellationToken) =>
        _episodeGenerator.Generate(new(configuration, effective, assignment, lineage, runId, semantics), cancellationToken);

    private static LoadedConfigurationSetV2 BindRowConfiguration(
        LoadedConfigurationSetV2 configuration,
        T6EffectiveRowBindingV1 binding,
        bool ablatedVariant)
    {
        var support = binding.Scenario.PosteriorSupport.ToArray();
        var weights = binding.Scenario.PosteriorWeights.ToArray();
        if (ablatedVariant && binding.Ablation.MeanDegenerateBelief)
        {
            support = [support.Zip(weights, (state, weight) => state * weight).Sum()];
            weights = [1d];
        }
        var beliefVersion = "t6-" + binding.RowId.ToLowerInvariant() + (ablatedVariant ? "-variant" : "-control") + "-belief-v1";
        var belief = configuration.Model.Belief with
        {
            SupportStates = support,
            Weights = weights,
            Artifact = configuration.Model.Belief.Artifact with { Version = beliefVersion }
        };
        belief = belief with { Artifact = belief.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Belief(belief)) } };
        var reset = configuration.Model.Reset;
        if (ablatedVariant && binding.Ablation.MeanMatchedPointReset)
        {
            var point = binding.Ablation.PointResetValue ?? throw new InvalidDataException("T6_ABL6_POINT_RESET_MISSING");
            reset = reset with
            {
                SupportStates = [point, point],
                Probabilities = [1d, 0d],
                Artifact = reset.Artifact with { Version = "t6-abl6-mean-matched-point-reset-v1" }
            };
            reset = reset with { Artifact = reset.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Reset(reset)) } };
        }
        var model = configuration.Model with { Belief = belief, Reset = reset };
        var actions = configuration.Actions;
        if (ablatedVariant && binding.Ablation.DisableEndogenousInformation)
        {
            actions = actions with
            {
                Actions = actions.Actions.Select(x => x.Action == MaintenanceActionKind.IntensifiedMonitoring ? x with { Enabled = false } : x).ToArray()
            };
        }
        return configuration with { Model = model, Actions = actions };
    }

    private static int WriteEpisodeEvidence(
        T6EvidenceWriteSessionV1 writer,
        T6ResourceGuardV1 guard,
        T6EffectiveRowBindingV1 binding,
        SplitAssignmentV2 assignment,
        T5EpisodeExecutionResult result,
        string variant,
        string runId)
    {
        var scope = binding.RowId + "--" + variant;
        var fallbackCalls = 0;
        foreach (var record in result.RawRecords)
        {
            if (binding.Ablation.AblationId is not null && IsDisallowedAblationPolicyRecord(record)) continue;
            if (record.SchemaId == "SCH-NUM")
            {
                var check = guard.Check("NUMERICAL_BATCH:" + binding.RowId + ":" + assignment.EpisodeId);
                writer.WriteJsonLine("resources/resource-checks.jsonl", check);
            }
            writer.WriteRaw(scope, record);
            if (record.SchemaId == "SCH-REL" && record.Fields.TryGetValue("method_id", out var method) &&
                string.Equals(method?.ToString(), DeterministicScalarUniformFirstPassageFallback.MethodId, StringComparison.Ordinal))
            {
                writer.WriteJsonLine("numerical/deterministic-fallback-calls.jsonl", new
                {
                    SchemaVersion = "t6-deterministic-fallback-call-v1",
                    RunId = runId,
                    binding.RowId,
                    ComponentEpisodeId = assignment.EpisodeId,
                    VariantId = variant,
                    MethodId = method,
                    ProtocolVersion = T6FrozenConstants.NumericalProtocol
                });
                fallbackCalls++;
            }
        }
        return fallbackCalls;
    }

    private static bool IsDisallowedAblationPolicyRecord(RawEvidenceRecord record)
    {
        if (record.SchemaId is not ("SCH-PEV" or "SCH-REL" or "SCH-FEV" or "SCH-CON")) return false;
        if (!record.Fields.TryGetValue("policy_id", out var value) || value is null) return false;
        return !value.ToString()!.StartsWith("P-R4", StringComparison.Ordinal);
    }

    private static void WriteProtocolCall(
        T6EvidenceWriteSessionV1 writer,
        T6EffectiveRowBindingV1 binding,
        string episodeId,
        string variantId,
        T5EpisodeExecutionResult result,
        bool fixtureOnly) =>
        writer.WriteJsonLine("numerical/protocol-v2-calls.jsonl", new
        {
            SchemaVersion = "t6-numerical-protocol-call-v1",
            binding.RowId,
            ComponentEpisodeId = episodeId,
            VariantId = variantId,
            ProtocolVersion = binding.NumericalProtocol.ProtocolVersion,
            binding.NumericalProtocol.Epsilon,
            binding.NumericalProtocol.EpsilonRemaining,
            DeterministicFallbackAvailable = true,
            result.PlanningInput.NumericalConverged,
            result.PlanningInput.ReliabilityPathCount,
            FixtureOnly = fixtureOnly
        });

    private static int WritePrimaryContrasts(
        T6EvidenceWriteSessionV1 writer,
        string runId,
        T6EffectiveRowBindingV1 binding,
        SplitAssignmentV2 assignment,
        T5PlanningEpisodeInput input)
    {
        var values = new[]
        {
            ("C-P-B7", "B7", input.B7Outcome),
            ("C-P-B4", "B4", input.B4Outcome),
            ("C-P-B1", "B1", input.B1Outcome)
        };
        foreach (var value in values)
        {
            if (input.ProposedOutcome is null || value.Item3 is null) throw new InvalidOperationException("T6_PAIRED_CONTRAST_INPUT_MISSING");
            var record = new
            {
                SchemaVersion = "t6-paired-contrast-input-v1",
                RunId = runId,
                binding.RowId,
                ComponentEpisodeId = assignment.EpisodeId,
                ClusterId = assignment.GroupingKey,
                ContrastId = value.Item1,
                ProposedPolicyId = "P-R4",
                BaselineId = value.Item2,
                ProposedLoss = input.ProposedOutcome.Value,
                BaselineLoss = value.Item3.Value,
                Difference = input.ProposedOutcome.Value - value.Item3.Value,
                DifferenceSign = "NEGATIVE_FAVOURS_P-R4"
            };
            writer.WriteJsonLine("statistics/paired-contrast-inputs.jsonl", record);
            writer.WriteJsonLine("statistics/bootstrap-inputs.jsonl", record);
        }
        return 3;
    }

    private static void WriteAblationInput(
        T6EvidenceWriteSessionV1 writer,
        string runId,
        T6EffectiveRowBindingV1 binding,
        SplitAssignmentV2 assignment,
        T5PlanningEpisodeInput control,
        T5PlanningEpisodeInput variant)
    {
        if (control.ProposedOutcome is null || variant.ProposedOutcome is null) throw new InvalidOperationException("T6_ABLATION_PAIRED_INPUT_MISSING");
        var record = new
        {
            SchemaVersion = "t6-ablation-input-v1",
            RunId = runId,
            binding.RowId,
            ComponentEpisodeId = assignment.EpisodeId,
            ClusterId = assignment.GroupingKey,
            AblationId = binding.Ablation.AblationId,
            binding.Ablation.Semantics,
            ControlLoss = control.ProposedOutcome.Value,
            AblatedLoss = variant.ProposedOutcome.Value,
            Difference = control.ProposedOutcome.Value - variant.ProposedOutcome.Value,
            Epsilon = binding.NumericalProtocol.Epsilon,
            EpsilonRemainingRegistered = binding.NumericalProtocol.EpsilonRemaining,
            BranchwiseEpsilonRemainingEnforced = !binding.Ablation.DisableBranchwiseEpsilonRemaining
        };
        writer.WriteJsonLine("ablations/ablation-inputs.jsonl", record);
        writer.WriteJsonLine("statistics/paired-contrast-inputs.jsonl", record);
        writer.WriteJsonLine("statistics/bootstrap-inputs.jsonl", record);
    }

    private static string[] ExpectedEpisodes(LoadedConfigurationSetV2 configuration) =>
        configuration.ExperimentSet.Runs.SelectMany(run => configuration.Split.Assignments
            .Where(x => x.EpisodeId.StartsWith(run.ExperimentId.Value + "-", StringComparison.Ordinal))
            .OrderBy(x => x.EpisodeId, StringComparer.Ordinal)
            .Select(x => x.EpisodeId)).ToArray();

    private static void ValidateRuntimeConfiguration(T6PackageMaterialization package, List<string> issues)
    {
        var configuration = package.Configuration;
        if (configuration.Resources.MaximumWallClockSeconds.Value != T6FrozenConstants.MaximumWallClockSeconds) issues.Add("T6_WALL_CLOCK_CEILING_INVALID");
        if (configuration.Resources.MaximumStorageBytes.Value != T6FrozenConstants.MaximumStorageBytes) issues.Add("T6_STORAGE_CEILING_INVALID");
        if (configuration.Resources.MaximumDegreeOfParallelism.Value != 1 || configuration.Numerical.MaximumDegreeOfParallelism != 1) issues.Add("T6_MDOP_MUST_EQUAL_ONE");
        if (configuration.Reliability.Epsilon.Value != T6FrozenConstants.Epsilon || configuration.Reliability.EpsilonRemaining.Value != T6FrozenConstants.EpsilonRemaining)
            issues.Add("T6_RELIABILITY_THRESHOLD_MISMATCH");
        if (configuration.ExperimentSet.ProtocolVersion != T6FrozenConstants.NumericalProtocol) issues.Add("T6_PROTOCOL_VERSION_MISMATCH");
        if (!configuration.Output.RefuseExisting || !configuration.Output.CompletionManifestLast || configuration.Output.ManuscriptRenderingEnabled)
            issues.Add("T6_OUTPUT_POLICY_INVALID");
        if (configuration.Output.ApprovedRoot.StartsWith("E:\\WearVision", StringComparison.OrdinalIgnoreCase)) issues.Add("T6_PROTECTED_TREE_OUTPUT_FORBIDDEN");
        if (configuration.Split.Assignments.Count != configuration.EffectiveExperiments.Values.Sum(x => x.EpisodeCount)) issues.Add("T6_SPLIT_EPISODE_TOTAL_MISMATCH");
        if (configuration.Split.Assignments.Select(x => x.EpisodeId).Distinct(StringComparer.Ordinal).Count() != configuration.Split.Assignments.Count)
            issues.Add("T6_DUPLICATE_COMPONENT_EPISODE");
        foreach (var effective in configuration.EffectiveExperiments.Values)
        {
            var binding = effective.T6Binding;
            if (binding is null) continue;
            if (binding.NumericalProtocol.Epsilon != T6FrozenConstants.Epsilon || binding.NumericalProtocol.EpsilonRemaining != T6FrozenConstants.EpsilonRemaining)
                issues.Add("T6_ROW_NUMERICAL_THRESHOLD_MISMATCH:" + effective.ExperimentId.Value);
            if (binding.ResourcePolicy.MaximumWallClockSeconds != T6FrozenConstants.MaximumWallClockSeconds || binding.ResourcePolicy.MaximumStorageBytes != T6FrozenConstants.MaximumStorageBytes)
                issues.Add("T6_ROW_RESOURCE_POLICY_MISMATCH:" + effective.ExperimentId.Value);
            if (binding.PolicyBaselineAllowlist.Count == 0 || binding.Outcomes.Count != 5 || string.IsNullOrWhiteSpace(binding.StatisticalRole) || string.IsNullOrWhiteSpace(binding.EvidenceClass))
                issues.Add("T6_ROW_REQUIRED_BINDING_MISSING:" + effective.ExperimentId.Value);
        }
    }

    private static T6AuthorizationManifestV1 ReadAuthorization(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("T6_AUTHORIZATION_MISSING", fullPath);
        return CanonicalJson.DeserializeStrict<T6AuthorizationManifestV1>(File.ReadAllText(fullPath, Encoding.UTF8));
    }
}
