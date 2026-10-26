using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Experiments;

public sealed record Er06CConfirmationRowConfig(
    string ConfigId,
    string ExperimentId,
    string ScenarioId,
    double HighStateWeight,
    int EpisodeCount,
    string EvidenceClass,
    bool EligibleForManuscript,
    bool EligibleForT6ScientificClaim,
    bool ExecuteOnlyIfReplacementOptimal,
    string ConfigSha256);

public sealed record Er06CPlanningPlan(
    string SchemaVersion,
    string ProtocolVersion,
    string TargetArticle,
    string ParentConfigSetSha256,
    IReadOnlyList<double> DominanceGrid,
    IReadOnlyList<Er06CConfirmationRowConfig> ConfirmationRows,
    int MaximumConfirmationEpisodes,
    bool T6Allowed,
    bool ManuscriptEditingAllowed);

public sealed record Er06CPlanningAuthorization(
    string SchemaVersion,
    string AuthorizationId,
    AuthorizationGrantState State,
    string Scope,
    string ProtocolVersion,
    string PlanningManifestSha256,
    string ParentConfigSetSha256,
    string SourceSnapshotSha256,
    string ExecutableSha256,
    IReadOnlyList<string> AllowedConfigIds,
    bool T6Allowed,
    bool ManuscriptEditingAllowed,
    string ApproverId);

public sealed record Er06CDominanceDiagnostic(
    double HighStateWeight,
    double LowStateWeight,
    double ContinueFailureProbability,
    double ContinueExpectedDisutility,
    string ContinueCertification,
    double ReplacementFailureProbability,
    double ReplacementExpectedDisutility,
    double MonitoringFailureProbability,
    double MonitoringExpectedDisutility,
    string MonitoringCertification,
    string MonitoringBranchActions,
    string SelectedFirstStageAction,
    string FeasibleSetComposition,
    double TerminalReplacementProbability,
    bool ReplacementOptimal,
    bool ReplacementDominated,
    double ContinueMonteCarloPointEstimate,
    long ContinueMonteCarloFinalN,
    bool HighNMonteCarloAgreementAwayFromBoundary);

public sealed record Er06CConfirmationEpisodeDiagnostic(
    string ConfigId,
    string ExperimentId,
    string ScenarioId,
    string EvidenceClass,
    bool EligibleForManuscript,
    bool EligibleForT6ScientificClaim,
    string EpisodeId,
    double HighStateWeight,
    string SelectedFirstStageAction,
    string SelectedTerminalAction,
    string SelectedPolicyId,
    double SelectedFailureProbability,
    double SelectedExpectedDisutility,
    string SelectedCertification,
    string SelectedNumericalStatus,
    long TotalChancePaths,
    long MaximumEvaluationN,
    int FallbackInvocationCount,
    int BoundaryUncertifiedCount,
    int NumericalNonConvergenceCount,
    bool DecisionAvailable,
    bool ConstraintBreach,
    string SourceRecordIds);

public sealed record Er06CPlanningDiagnosticResult(
    string SchemaVersion,
    string ProtocolVersion,
    string AuthorizationId,
    string PlanningManifestSha256,
    string SourceSnapshotSha256,
    string ExecutableSha256,
    IReadOnlyList<Er06CDominanceDiagnostic> DominanceGrid,
    string ReplacementDominanceClass,
    string ReplacementCrossover,
    bool StressConfirmationExecuted,
    IReadOnlyList<Er06CConfirmationEpisodeDiagnostic> ConfirmationEpisodes,
    long ElapsedMilliseconds,
    bool T6Executed,
    bool ManuscriptEdited);

public sealed class Er06CPlanningDiagnosticRunner
{
    private const double Epsilon = 0.125d;
    private const double CertifiedAbsoluteErrorBound = 1e-10d;
    private const int EscalationPaths = T5ScalarTerminalActionEvaluator.DeterministicFallbackEscalationPaths;

    public Er06CPlanningDiagnosticResult Run(
        string parentConfigSet,
        string planningManifestPath,
        string authorizationPath,
        string sourceSnapshotPath,
        string executablePath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = CanonicalJson.DeserializeStrict<Er06CPlanningPlan>(File.ReadAllText(planningManifestPath));
        var authorization = CanonicalJson.DeserializeStrict<Er06CPlanningAuthorization>(File.ReadAllText(authorizationPath));
        var parent = new ConfigurationSetLoaderV2().Load(parentConfigSet);
        Validate(plan, authorization, parent, planningManifestPath, sourceSnapshotPath, executablePath);
        var planHash = ConfigurationSetLoaderV2.HashFile(planningManifestPath);

        var grid = plan.DominanceGrid.Select((weight, index) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new Er06CConfirmationRowConfig(
                $"ER06C-GRID-{index:D2}",
                $"ER06C-GRID-{index:D2}",
                "REPLACEMENT-DOMINANCE-GRID",
                weight,
                1,
                "T5_METHOD_PLANNING_STRESS_ONLY",
                false,
                false,
                false,
                HashText(FormattableString.Invariant($"ER06C-GRID|{weight:R}|{plan.ProtocolVersion}")));
            return EvaluateDominance(parent, row, authorization.AuthorizationId, planHash, cancellationToken);
        }).ToArray();

        var replacementOptimal = grid.Any(x => x.ReplacementOptimal);
        var dominanceClass = replacementOptimal
            ? "R-A"
            : grid.All(x => x.ReplacementDominated) ? "R-C" : "R-B";
        var crossover = replacementOptimal
            ? string.Join(';', grid.Where(x => x.ReplacementOptimal).Select(x => x.HighStateWeight.ToString("R", CultureInfo.InvariantCulture)))
            : "NONE";

        var episodes = new List<Er06CConfirmationEpisodeDiagnostic>();
        foreach (var row in plan.ConfirmationRows)
        {
            if (row.ExecuteOnlyIfReplacementOptimal && !replacementOptimal) continue;
            var configuration = BuildConfiguration(parent, row, authorization.AuthorizationId, ConfigurationSetLoaderV2.HashFile(planningManifestPath));
            var experiment = configuration.EffectiveExperiments.Values.Single();
            foreach (var split in configuration.Split.Assignments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lineage = new T5PlanningLineage(
                    experiment.ExperimentId,
                    row.ConfigSha256,
                    $"ER06C-{row.ExperimentId}",
                    ConfigurationProfileKind.T5Pilot,
                    authorization.AuthorizationId,
                    true,
                    false,
                    false);
                var result = new T5ProductionEpisodeGenerator().Generate(
                    new(configuration, experiment, split, lineage, $"ER06C-{row.ExperimentId}"),
                    cancellationToken);
                episodes.Add(SummarizeEpisode(row, result));
            }
        }

        stopwatch.Stop();
        return new(
            "er06c-planning-diagnostic-result-v1",
            plan.ProtocolVersion,
            authorization.AuthorizationId,
            planHash,
            ConfigurationSetLoaderV2.HashFile(sourceSnapshotPath),
            ConfigurationSetLoaderV2.HashFile(executablePath),
            grid,
            dominanceClass,
            crossover,
            replacementOptimal && plan.ConfirmationRows.Any(x => x.ExecuteOnlyIfReplacementOptimal),
            episodes,
            stopwatch.ElapsedMilliseconds,
            false,
            false);
    }

    private static Er06CDominanceDiagnostic EvaluateDominance(
        LoadedConfigurationSetV2 parent,
        Er06CConfirmationRowConfig row,
        string authorizationId,
        string planHash,
        CancellationToken cancellationToken)
    {
        var configuration = BuildConfiguration(parent, row, authorizationId, planHash);
        var experiment = configuration.EffectiveExperiments.Values.Single();
        var lineage = new T5PlanningLineage(experiment.ExperimentId, row.ConfigSha256, "ER06C-DOMINANCE", ConfigurationProfileKind.T5Pilot, authorizationId, true, false, false);
        var result = new T5ProductionEpisodeGenerator().Generate(
            new(configuration, experiment, configuration.Split.Assignments.Single(), lineage, "ER06C-DOMINANCE"),
            cancellationToken);
        var branches = result.RawRecords.Where(x => x.SchemaId == "SCH-BRN").OrderBy(x => Text(x, "branch_id"), StringComparer.Ordinal).ToArray();
        var monitorP = branches.Sum(x => Number(x, "branch_weight") * Number(x, "branch_failure_probability"));
        var monitorJ = 0.10125d + branches.Sum(x =>
        {
            var action = Text(x, "terminal_action");
            var branchJ = action == MaintenanceActionKind.PlannedReplacement.ToString()
                ? 0.21d + 0.15d * (0.275d / 0.925d)
                : 0.47d * Number(x, "branch_failure_probability");
            return Number(x, "branch_weight") * branchJ;
        });
        var monitorCertification = branches.All(x => Boolean(x, "branch_feasible")) && monitorP <= Epsilon
            ? "ProvenFeasible"
            : "NotCertifiedFeasible";
        var continueP = 0.5d * row.HighStateWeight;
        var continueJ = 0.47d * continueP;
        var continueCertification = continueP + CertifiedAbsoluteErrorBound <= Epsilon
            ? "ProvenFeasible"
            : continueP - CertifiedAbsoluteErrorBound > Epsilon ? "ProvenInfeasible" : "BoundaryUncertified";
        const double replacementP = 0d;
        const double replacementJ = 0.25125d;
        var feasible = new List<(string Action, double J, double P)> { (MaintenanceActionKind.PlannedReplacement.ToString(), replacementJ, replacementP) };
        if (continueCertification == "ProvenFeasible") feasible.Add((MaintenanceActionKind.Continue.ToString(), continueJ, continueP));
        if (monitorCertification == "ProvenFeasible") feasible.Add((MaintenanceActionKind.IntensifiedMonitoring.ToString(), monitorJ, monitorP));
        var selected = feasible.OrderBy(x => x.J).ThenBy(x => x.P).ThenBy(x => x.Action, StringComparer.Ordinal).First();
        var replacementDominated = feasible.Any(x => x.Action != MaintenanceActionKind.PlannedReplacement.ToString() && x.J < replacementJ - 1e-12d);
        var numerical = FinalNumericalRows(result.RawRecords);
        var directMc = numerical[0];
        var standardError = Math.Sqrt(Math.Max(0d, continueP * (1d - continueP) / directMc.N));
        var validationTolerance = Math.Max(0.001d, 5d * standardError);
        var away = Math.Abs(continueP - Epsilon) >= 0.01d;
        var agreement = !away || Math.Abs(directMc.PointEstimate - continueP) <= validationTolerance;
        return new(
            row.HighStateWeight,
            1d - row.HighStateWeight,
            continueP,
            continueJ,
            continueCertification,
            replacementP,
            replacementJ,
            monitorP,
            monitorJ,
            monitorCertification,
            string.Join(';', branches.Select(x => $"{Text(x, "branch_id")}={Text(x, "terminal_action")}")),
            selected.Action,
            string.Join(';', feasible.Select(x => x.Action).OrderBy(x => x, StringComparer.Ordinal)),
            branches.Where(x => Text(x, "terminal_action") == MaintenanceActionKind.PlannedReplacement.ToString()).Sum(x => Number(x, "branch_weight")),
            selected.Action == MaintenanceActionKind.PlannedReplacement.ToString(),
            replacementDominated,
            directMc.PointEstimate,
            directMc.N,
            agreement);
    }

    private static Er06CConfirmationEpisodeDiagnostic SummarizeEpisode(
        Er06CConfirmationRowConfig row,
        T5EpisodeExecutionResult result)
    {
        var decision = result.RawRecords.Single(x => x.SchemaId == "SCH-DEC");
        var selected = result.RawRecords.Single(x => x.SchemaId == "SCH-PEV" && Text(x, "policy_id") == "P-R4-TWO-STAGE");
        var numerical = FinalNumericalRows(result.RawRecords);
        var fallbackCount = numerical.Count(x => x.N == EscalationPaths && x.Classification == FeasibilityClassification.Unresolved.ToString());
        var boundaryCount = Math.Abs(row.HighStateWeight - 0.25d) <= 1e-15d && numerical[0].N == EscalationPaths && numerical[0].Classification == FeasibilityClassification.Unresolved.ToString() ? 1 : 0;
        var nonconvergence = numerical.Count(x => x.N == 1_048_576 && x.Classification == FeasibilityClassification.Unresolved.ToString());
        var firstAction = Text(decision, "selected_first_action");
        var terminal = result.PlanningInput.ProposedAction.Split("->", StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? firstAction;
        return new(
            row.ConfigId,
            row.ExperimentId,
            row.ScenarioId,
            row.EvidenceClass,
            row.EligibleForManuscript,
            row.EligibleForT6ScientificClaim,
            result.PlanningInput.EpisodeId.Value,
            row.HighStateWeight,
            firstAction,
            terminal,
            Text(decision, "selected_policy_id"),
            Number(decision, "selected_failure_probability"),
            Number(decision, "selected_expected_disutility"),
            Text(selected, "feasibility_status"),
            Text(selected, "numerical_status"),
            numerical.Sum(x => x.N),
            numerical.Max(x => x.N),
            fallbackCount,
            boundaryCount,
            nonconvergence,
            Text(decision, "decision_status") is "Success" or "SuccessNumericalTie" or "ConstraintBreach",
            Boolean(decision, "constraint_breach"),
            $"SCH-DEC:{result.PlanningInput.EpisodeId.Value};SCH-PEV:{result.PlanningInput.EpisodeId.Value};SCH-BRN:{result.PlanningInput.EpisodeId.Value};SCH-NUM:{result.PlanningInput.EpisodeId.Value}");
    }

    private static LoadedConfigurationSetV2 BuildConfiguration(
        LoadedConfigurationSetV2 parent,
        Er06CConfirmationRowConfig row,
        string authorizationId,
        string planHash)
    {
        var artifact = parent.Model.Belief.Artifact with
        {
            ArtifactId = $"ER06C-{row.ExperimentId}-BELIEF",
            Version = $"posterior.er06c.{row.ExperimentId.ToLowerInvariant()}.v1",
            Sha256 = new string('0', 64),
            EvidenceId = authorizationId
        };
        var beliefWithoutHash = parent.Model.Belief with
        {
            Weights = new[] { 1d - row.HighStateWeight, row.HighStateWeight },
            Artifact = artifact
        };
        var belief = beliefWithoutHash with
        {
            Artifact = artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Belief(beliefWithoutHash)) }
        };
        var model = parent.Model with
        {
            ConfigId = $"model.{row.ConfigId}.wv-1152-chance-cert-2",
            Belief = belief
        };
        var sourceHash = HashText(FormattableString.Invariant($"{row.ConfigId}|{row.HighStateWeight:R}|{planHash}"));
        var splits = Enumerable.Range(1, row.EpisodeCount).Select(index => new SplitAssignmentV2(
            $"{row.ExperimentId}-EP-{index:D4}",
            "ER06C-MODEL-BASED-PLANNING-NO-DATASET",
            DataPartition.T5PilotOnly,
            $"{row.ExperimentId}-EP-{index:D4}",
            row.ScenarioId,
            "ER06C_PLANNING_CONFIRMATION_ONLY_NOT_T6_NOT_MANUSCRIPT",
            "split-er06c-v1",
            sourceHash)).ToArray();
        var split = new SplitManifestV2("split-v2", $"split.{row.ConfigId}.v1", splits);
        var template = parent.EffectiveExperiments.Values.OrderBy(x => x.ExperimentId.Value, StringComparer.Ordinal).First();
        var experimentId = new ExperimentId(row.ExperimentId);
        var experiment = template with
        {
            ConfigId = row.ConfigId,
            ExperimentId = experimentId,
            ScenarioId = new(row.ScenarioId),
            ProtocolVersion = DeterministicScalarUniformFirstPassageFallback.ProtocolVersion,
            FactorLevelSetIds = new[] { "ER06C-NUMERICAL-CONFIRMATION", row.ScenarioId },
            FactorLevelStatus = FactorLevelStatus.MethodologicalStress,
            SplitManifestId = split.ConfigId,
            SplitManifestSha256 = planHash,
            EpisodeCount = row.EpisodeCount,
            SyntheticOrModelBasedLabel = row.EvidenceClass,
            OutputSubroot = row.ExperimentId,
            AuthorizationEligible = true
        };
        var run = new ExperimentRunReferenceV2(experimentId, experiment.ScenarioId, $"{row.ExperimentId}.json", row.ConfigSha256, row.ExperimentId, true);
        var set = new ExperimentSetManifestV2("experiment-set-v2", $"experiment-set.{row.ConfigId}.v1", RunStage.T5, ConfigurationProfileKind.T5Pilot, DeterministicScalarUniformFirstPassageFallback.ProtocolVersion, new[] { run });
        return parent with
        {
            ConfigSetSha256 = planHash,
            Model = model,
            Split = split,
            ExperimentSet = set,
            EffectiveExperiments = new Dictionary<ExperimentId, EffectiveExperimentConfigV2> { [experimentId] = experiment }
        };
    }

    private static List<(long N, double PointEstimate, string Classification)> FinalNumericalRows(IReadOnlyList<RawEvidenceRecord> records)
    {
        var rows = records.Where(x => x.SchemaId == "SCH-NUM").ToArray();
        var final = new List<(long N, double PointEstimate, string Classification)>();
        RawEvidenceRecord? current = null;
        foreach (var row in rows)
        {
            if (Convert.ToInt32(row.Fields["batch_index"], CultureInfo.InvariantCulture) == 1 && current is not null)
                final.Add((Integer(current, "cumulative_path_count"), Number(current, "failure_estimate"), Text(current, "classification")));
            current = row;
        }
        if (current is not null) final.Add((Integer(current, "cumulative_path_count"), Number(current, "failure_estimate"), Text(current, "classification")));
        if (final.Count != 8) throw new InvalidDataException("ER06C_EXPECTED_EIGHT_CHANCE_EVALUATIONS");
        return final;
    }

    private static void Validate(
        Er06CPlanningPlan plan,
        Er06CPlanningAuthorization authorization,
        LoadedConfigurationSetV2 parent,
        string planPath,
        string sourceSnapshotPath,
        string executablePath)
    {
        var expectedGrid = Enumerable.Range(0, 9).Select(x => x / 8d).ToArray();
        if (plan.ProtocolVersion != DeterministicScalarUniformFirstPassageFallback.ProtocolVersion ||
            plan.TargetArticle != "1152" || plan.T6Allowed || plan.ManuscriptEditingAllowed ||
            !plan.DominanceGrid.SequenceEqual(expectedGrid) || plan.MaximumConfirmationEpisodes != 192)
            throw new InvalidDataException("ER06C_PLANNING_PROTOCOL_INVALID");
        if (plan.ConfirmationRows.Sum(x => x.EpisodeCount) > plan.MaximumConfirmationEpisodes ||
            plan.ConfirmationRows.Any(x => x.EpisodeCount != 64 || x.EligibleForManuscript || x.EligibleForT6ScientificClaim))
            throw new InvalidDataException("ER06C_CONFIRMATION_WORKLOAD_INVALID");
        if (!plan.ParentConfigSetSha256.Equals(parent.ConfigSetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ER06C_PARENT_CONFIG_HASH_MISMATCH");
        if (authorization.State != AuthorizationGrantState.Approved || authorization.Scope != "ER06C_PLANNING_CONFIRMATION_ONLY" ||
            authorization.ProtocolVersion != plan.ProtocolVersion || authorization.T6Allowed || authorization.ManuscriptEditingAllowed ||
            !authorization.PlanningManifestSha256.Equals(ConfigurationSetLoaderV2.HashFile(planPath), StringComparison.OrdinalIgnoreCase) ||
            !authorization.ParentConfigSetSha256.Equals(parent.ConfigSetSha256, StringComparison.OrdinalIgnoreCase) ||
            !authorization.SourceSnapshotSha256.Equals(ConfigurationSetLoaderV2.HashFile(sourceSnapshotPath), StringComparison.OrdinalIgnoreCase) ||
            !authorization.ExecutableSha256.Equals(ConfigurationSetLoaderV2.HashFile(executablePath), StringComparison.OrdinalIgnoreCase) ||
            !authorization.AllowedConfigIds.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(plan.ConfirmationRows.Select(x => x.ConfigId).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(authorization.ApproverId))
            throw new InvalidDataException("ER06C_SOURCE_BOUND_AUTHORIZATION_INVALID");
        foreach (var row in plan.ConfirmationRows)
        {
            if (row.HighStateWeight is < 0d or > 1d || row.ConfigSha256.Length != 64 || row.ConfigSha256.Any(x => !Uri.IsHexDigit(x)))
                throw new InvalidDataException("ER06C_CONFIRMATION_ROW_INVALID");
            if (row.ExecuteOnlyIfReplacementOptimal && row.EvidenceClass != "T5_METHOD_PLANNING_STRESS_ONLY")
                throw new InvalidDataException("ER06C_STRESS_EVIDENCE_CLASS_INVALID");
        }
    }

    private static string Text(RawEvidenceRecord record, string field) => Convert.ToString(record.Fields[field], CultureInfo.InvariantCulture) ?? string.Empty;
    private static double Number(RawEvidenceRecord record, string field) => Convert.ToDouble(record.Fields[field], CultureInfo.InvariantCulture);
    private static long Integer(RawEvidenceRecord record, string field) => Convert.ToInt64(record.Fields[field], CultureInfo.InvariantCulture);
    private static bool Boolean(RawEvidenceRecord record, string field) => Convert.ToBoolean(record.Fields[field], CultureInfo.InvariantCulture);
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
