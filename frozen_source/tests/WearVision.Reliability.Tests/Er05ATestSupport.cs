using System.Runtime.CompilerServices;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

internal static class Er05ATestSupport
{
    public static string CreateV2Package(
        ConfigurationProfileKind profile = ConfigurationProfileKind.T5Pilot,
        RunStage stage = RunStage.T5,
        bool ownerApproved = false,
        bool resourcesApproved = false)
    {
        var root = Path.Combine(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("er05a-v2-package"));
        Directory.CreateDirectory(root);
        var status = profile == ConfigurationProfileKind.TestFixtureOnly ? RuntimeValueStatus.TestFixtureOnly : RuntimeValueStatus.MethodologicalPilotNotApproved;
        var model = Model(profile, status);
        var reliability = new ReliabilityConfigV2(
            "reliability-v2", "reliability.fixture.v2", V(1d, status), V(0.4d, status), V(0.4d, status), V("first-passage", status), V("direct", status), "normalized-exposure-v1");
        var actions = new ActionConfigV2(
            "action-v2",
            "actions.fixture.v2",
            Artifact("admissibility.fixture", "v2", status, "admissibility"),
            [
                new(MaintenanceActionKind.Continue, true, V(0d, status), "normalized-exposure-v1", null, "continue"),
                new(MaintenanceActionKind.IntensifiedMonitoring, true, V(0.1d, status), "normalized-exposure-v1", null, "monitor"),
                new(MaintenanceActionKind.TargetedInspection, false, V(0.2d, status), "normalized-exposure-v1", InspectionExposureMode.Paused, "inspection"),
                new(MaintenanceActionKind.PlannedReplacement, true, V(0.3d, status), "normalized-exposure-v1", null, "replacement")
            ]);
        var weights = new Dictionary<ConsequenceComponentKind, double>
        {
            [ConsequenceComponentKind.Failure] = 0.4,
            [ConsequenceComponentKind.PlannedReplacement] = 0.2,
            [ConsequenceComponentKind.IntensifiedMonitoring] = 0.1,
            [ConsequenceComponentKind.TargetedInspection] = 0.1,
            [ConsequenceComponentKind.Downtime] = 0.2
        };
        var consequences = new ConsequenceConfigV2(
            "consequence-v2", "consequences.fixture.v2", "five-component-v2",
            Enum.GetValues<ConsequenceComponentKind>().Select(x => new ConsequenceComponentConfigV2(x, true, x.ToString(), "bounded-test", V(1d, status), "dimensionless")).ToArray(),
            "weights.fixture", "v2", profile == ConfigurationProfileKind.TestFixtureOnly ? WeightProfileStatus.TestFixtureOnly : WeightProfileStatus.MethodologicalStress,
            V<IReadOnlyDictionary<ConsequenceComponentKind, double>>(weights, status), TestSupport.FixtureWeights(weights).ProvenanceHash.Sha256Hex);
        var numerical = new NumericalConfigV2(
            "numerical-v2", "numerical.fixture.v2", "Binary64", "sha256-xoshiro256starstar-v1", 1UL, 4, 2, 12, 2,
            "WilsonOneSided99", "objective-fixture", "crossing-fixture", 1e-12, 1e-12, "TOL-NORM", "TOL-PROB", "TOL-WEIGHT", "TOL-TIE", "TOL-LOG", 1,
            "TrajectoryOnly", "stable-v1", 1, "fixture", TestSupport.FixtureWeights().ProvenanceHash.Sha256Hex);
        var baselines = Baselines();
        var output = new OutputPolicyV2(
            "output-v2", "output.fixture.v2", Path.Combine(TestSupport.EvidenceRoot, "T5-ONLY-OUTPUT"), "{experiment}-{run}", true, "CSV", "JSON", "NDJSON", "raw-v1",
            TestSupport.FixtureWeights().ProvenanceHash.Sha256Hex, "InvariantCulture", "UTF8_NO_BOM", "LF", false, true, true, true, "SHA-256", "stable-order-v1", false);
        var compatibility = new CompatibilityManifestV2(
            "compatibility-v2", "compat.fixture.v2", "ER03-1152-R4", "ER03", "ER04", "net8.0", "8.0.424",
            new Dictionary<string, string> { ["architecture"] = TestSupport.FixtureWeights().ProvenanceHash.Sha256Hex });
        var split = new SplitManifestV2(
            "split-v2", "split.fixture.v2",
            [
                Split("T5-001-EP-0001"), Split("T5-001-EP-0002"), Split("T5-002-EP-0001"), Split("T5-002-EP-0002")
            ]);
        var retirement = new RetirementDenyListV2("retirement-v2", "retirement.fixture.v2", ["reproducible-synthetic-study"], [new string('A', 64)]);
        var decisions = Enumerable.Range(1, 12).Select(x => new OwnerDecisionStatusV2($"A-{x:D2}", ownerApproved ? OwnerApprovalState.Approved : OwnerApprovalState.NotApproved, "fixture")).ToArray();
        var owner = new OwnerApprovalManifestV2("owner-approval-v2", "owner.fixture.v2", decisions, ownerApproved ? OwnerApprovalState.Approved : OwnerApprovalState.NotApproved);
        var resourceStatus = resourcesApproved ? RuntimeValueStatus.OwnerApproved : status;
        var resources = new ResourceCeilingConfigV2(
            "resources-v2", "resources.fixture.v2",
            new(resourcesApproved ? 60d : null, resourceStatus, "owner", "fixture"),
            new(resourcesApproved ? 1_000_000L : null, resourceStatus, "owner", "fixture"),
            V(1, resourceStatus),
            new(resourcesApproved ? false : null, resourceStatus, "owner", "fixture"));

        Write(root, "model_config_v2.json", model);
        Write(root, "reliability_config_v2.json", reliability);
        Write(root, "action_config_v2.json", actions);
        Write(root, "consequence_config_v2.json", consequences);
        Write(root, "numerical_config_v2.json", numerical);
        Write(root, "baseline_config_v2.json", baselines);
        Write(root, "output_policy_v2.json", output);
        Write(root, "compatibility_manifest_v2.json", compatibility);
        Write(root, "split_manifest_v2.json", split);
        Write(root, "retirement_denylist.json", retirement);
        Write(root, "owner_approval_manifest.json", owner);
        Write(root, "resource_ceiling_config.json", resources);
        var splitHash = ConfigurationSetLoaderV2.HashFile(Path.Combine(root, "split_manifest_v2.json"));
        var matrixHash = ConfigurationSetLoaderV2.HashFile(Path.Combine(TestSupport.SchemaRoot, "experiment-matrix.csv"));
        var effective1 = Effective("T5-001", "PILOT-NUMERICAL", profile, stage, splitHash, matrixHash, "T5-001", status);
        var effective2 = Effective("T5-002", "PILOT-PRECISION", profile, stage, splitHash, matrixHash, "T5-002", status);
        Write(root, "T5-001_effective_config.json", effective1);
        Write(root, "T5-002_effective_config.json", effective2);
        var effectiveHash1 = ConfigurationSetLoaderV2.HashFile(Path.Combine(root, "T5-001_effective_config.json"));
        var effectiveHash2 = ConfigurationSetLoaderV2.HashFile(Path.Combine(root, "T5-002_effective_config.json"));
        var set = new ExperimentSetManifestV2(
            "experiment-set-v2", "experiment-set.fixture.v2", stage, profile, "ER04",
            [new(new("T5-001"), new("PILOT-NUMERICAL"), "T5-001_effective_config.json", effectiveHash1, "T5-001", true), new(new("T5-002"), new("PILOT-PRECISION"), "T5-002_effective_config.json", effectiveHash2, "T5-002", true)]);
        Write(root, "experiment_set_manifest.json", set);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model_config_v2.json"] = "model", ["reliability_config_v2.json"] = "reliability", ["action_config_v2.json"] = "actions",
            ["consequence_config_v2.json"] = "consequences", ["numerical_config_v2.json"] = "numerical", ["baseline_config_v2.json"] = "baselines",
            ["output_policy_v2.json"] = "output-policy", ["compatibility_manifest_v2.json"] = "compatibility", ["split_manifest_v2.json"] = "split",
            ["retirement_denylist.json"] = "retirement", ["owner_approval_manifest.json"] = "owner-approval", ["resource_ceiling_config.json"] = "resources",
            ["experiment_set_manifest.json"] = "experiment-set", ["T5-001_effective_config.json"] = "effective-experiment", ["T5-002_effective_config.json"] = "effective-experiment"
        };
        var files = roles.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new ConfigFileReferenceV2(x.Key, ConfigurationSetLoaderV2.HashFile(Path.Combine(root, x.Key)), x.Value)).ToArray();
        var manifest = new ConfigSetManifestV2(
            "config-set-v2", "config-set.fixture.v2", profile, profile == ConfigurationProfileKind.TestFixtureOnly ? "TEST_FIXTURE_ONLY" : "METHODOLOGICAL_PILOT_CONFIGURATION",
            stage, "canonical-json-v1", ownerApproved ? OwnerApprovalState.Approved : OwnerApprovalState.NotApproved, "fixture-owner", "ER05A_TEST",
            ownerApproved ? Array.Empty<string>() : ["A-01..A-10", "RESOURCE_CEILINGS", "T5_AUTHORIZATION"], files);
        Write(root, "config_set_manifest.json", manifest);
        return root;
    }

    public static T5AuthorizationGrantV2 ApprovedGrant(LoadedConfigurationSetV2 configuration) => new(
        "t5-authorization-v1", "AUTH-FIXTURE", AuthorizationGrantState.Approved, configuration.ConfigSetSha256,
        configuration.ExperimentSet.Runs.Select(x => new AuthorizedExperimentReferenceV2(x.ExperimentId, x.EffectiveConfigSha256)).ToArray(),
        configuration.ExperimentSet.ProtocolVersion, RunStage.T5,
        ConfigurationSetLoaderV2.HashFile(Path.Combine(configuration.Root, configuration.Manifest.Files.Single(x => x.Role == "output-policy").RelativePath)),
        "fixture-approver", DateTimeOffset.UtcNow.AddHours(1));

    public static RuntimeModelConfigV2 Model(ConfigurationProfileKind profile, RuntimeValueStatus status)
    {
        var limit = new AffineLimitStateBindingConfigV2("AffineScalarLimitStateV1", 0.7, "normalized-margin", "fixture", "scalar-reference-state", "normalized-exposure-v1", Artifact("limit.fixture", "v2", status, string.Empty));
        limit = limit with { Artifact = limit.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.LimitState(limit)) } };
        var belief = new WeightedSupportBeliefBindingConfigV2("WeightedSupportBeliefV1", [0.2, 0.8], [0.75, 0.25], "scalar-reference-state", Artifact("belief.fixture", "v2", status, string.Empty));
        belief = belief with { Artifact = belief.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Belief(belief)) } };
        var transition = new ExposureAwareTransitionBindingConfigV2("ExposureScaledUniformRateV1", 0.1, "UNIFORM_RATE_PER_SEGMENT_TIMES_DELTA_EXPOSURE", "normalized-exposure-v1", "scalar-reference-state", Artifact("transition.fixture", "v2", status, string.Empty));
        transition = transition with { Artifact = transition.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Transition(transition)) } };
        var routine = Observation("routine.fixture", ObservationKind.Routine, MaintenanceActionKind.Continue, true, 0.4, 0.6, MissingObservationMode.None, 0, 0, status);
        var monitor = Observation("monitor.fixture", ObservationKind.IntensifiedMonitoring, MaintenanceActionKind.IntensifiedMonitoring, true, 0.25, 0.75, MissingObservationMode.NonInformative, 0.1, 0.1, status);
        var inspection = Observation("inspection.fixture", ObservationKind.TargetedInspection, MaintenanceActionKind.TargetedInspection, false, 0.1, 0.9, MissingObservationMode.None, 0, 0, status);
        var reset = new TwoPointResetBindingConfigV2("TwoPointScalarResetV1", [0.05, 0.15], [0.75, 0.25], "scalar-reference-state", Artifact("reset.fixture", "v2", status, string.Empty));
        reset = reset with { Artifact = reset.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Reset(reset)) } };
        return new("model-v2", "model.fixture.v2", profile, "scalar-reference-state", "v2", "dimensionless", V("normalized-exposure-v1", status), V("dimensionless", status), false, limit, belief, transition, routine, monitor, inspection, reset);
    }

    public static BaselineConfigV2 Baselines()
    {
        BaselineDefinitionConfigV2 B(string id, BaselineApplicabilityStatus status, string artifact, double? interval = null, string? mapping = null, double? q = null, double? threshold = null) =>
            new(id, status, "v2", DataPartition.Design, artifact, TestSupport.FixtureWeights().ProvenanceHash.Sha256Hex, [], status.ToString(), interval, null, mapping, threshold, q, threshold, null, threshold);
        return new(
            "baseline-v2", "baselines.fixture.v2", "normalized-exposure-v1", "five-component-v2", "reference-model-v2",
            [
                B("B0-RUN-TO-FAILURE", BaselineApplicabilityStatus.Disabled, "none"),
                B("B1-PERIODIC", BaselineApplicabilityStatus.Enabled, "design-b1", 0.6),
                B("B2-OBSERVED-PROXY", BaselineApplicabilityStatus.NotApplicable, "design-b2"),
                B("B3-POSTERIOR-MEAN", BaselineApplicabilityStatus.Disabled, "design-b3", mapping: "failure-oriented-state-v1", threshold: 0.7),
                B("B4-POSTERIOR-QUANTILE", BaselineApplicabilityStatus.Enabled, "design-b4", mapping: "failure-oriented-state-v1", q: 0.75, threshold: 0.7),
                B("B5-FIRST-PASSAGE-QUANTILE", BaselineApplicabilityStatus.Disabled, "design-b5", threshold: 0.5),
                B("B6-UNCONSTRAINED-BAYES", BaselineApplicabilityStatus.Enabled, "none"),
                B("B7-CONSTRAINED-ONE-STAGE", BaselineApplicabilityStatus.Enabled, "none")
            ]);
    }

    public static IAsyncEnumerable<T5PlanningEpisodeInput> Episodes(T5PlanningLineage lineage, int count, bool missingPair = false, CancellationToken cancellationToken = default) => EpisodesCore(lineage, count, missingPair, cancellationToken);

    private static async IAsyncEnumerable<T5PlanningEpisodeInput> EpisodesCore(T5PlanningLineage lineage, int count, bool missingPair, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new(lineage, new($"EP-{i:D3}"), 4, i % 2 == 0, 0.1, 0.2 + i * 0.01, missingPair && i == count - 1 ? null : 0.3 + i * 0.01, i % 2 == 0, i % 3 == 0, i % 2 == 0 ? CensoringKind.ExactFailure : CensoringKind.Administrative, i % 3, true, 2);
        }
    }

    public static CompletePolicyEvaluation TerminalEvaluation(IDegradationBelief<double> belief, MaintenanceActionKind action, Horizon horizon, ConfigurationHash hash)
    {
        belief.TryGetWeightedSupport(out var support);
        var high = support.Where(x => x.State >= 0.5).Sum(x => x.Weight);
        var failure = action == MaintenanceActionKind.PlannedReplacement ? 0.05 : high;
        var disutility = action == MaintenanceActionKind.PlannedReplacement ? 0.35 : 0.1;
        var reliability = new ReliabilityEvaluation(EvaluationStatus.Success, new(failure), new(1d - failure), new(failure), new(failure), new("fixture-terminal", 1, 1, new(0), new(0), new(failure), new(failure), true, "TEST_FIXTURE_ONLY", []), null);
        var components = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, x => x == ConsequenceComponentKind.PlannedReplacement && action == MaintenanceActionKind.PlannedReplacement ? 1d : 0d);
        return new(new DirectPolicy(new($"terminal-{action}"), horizon, action), new(true, ActionAdmissibilityReason.Admissible), reliability, new(EvaluationStatus.Success, new(disutility), components, new(0), 1, "fixture"), new(FeasibilityClassification.ProvenFeasible, 0, "fixture"), null, action == MaintenanceActionKind.PlannedReplacement ? 1 : 0, hash, EvaluationStatus.Success, []);
    }

    private static EffectiveExperimentConfigV2 Effective(string id, string scenario, ConfigurationProfileKind profile, RunStage stage, string splitHash, string matrixHash, string subroot, RuntimeValueStatus status) => new(
        "effective-experiment-v2", $"effective.{id}", new(id), new(scenario), stage, profile, "ER04", matrixHash, ["MINIMUM-T5"], FactorLevelStatus.MethodologicalStress,
        ["P-R4-TWO-STAGE", "B7-CONSTRAINED-ONE-STAGE", "B4-POSTERIOR-QUANTILE", "B1-PERIODIC", "B6-UNCONSTRAINED-BAYES"],
        ["B7-CONSTRAINED-ONE-STAGE", "B4-POSTERIOR-QUANTILE", "B1-PERIODIC", "B6-UNCONSTRAINED-BAYES"], [], ["P-R4-vs-B7", "P-R4-vs-B4", "P-R4-vs-B1"],
        "split.fixture.v2", splitHash, 2, EpisodeCountStatus.T5PrecisionPilot, DataPartition.T5PilotOnly, "methodological-pilot", "direct", "cluster-bootstrap", "holm-primary-three", "crn-v1", "MODEL_BASED_T5_PLANNING_ONLY", null, subroot, true,
        new(null, status, "owner", "T6_ONLY"), new(null, status, "owner", "T6_ONLY"));

    private static BinaryObservationBindingConfigV2 Observation(string id, ObservationKind kind, MaintenanceActionKind action, bool enabled, double low, double high, MissingObservationMode mode, double missingLow, double missingHigh, RuntimeValueStatus status)
    {
        var value = new BinaryObservationBindingConfigV2("StateConditionedBinaryObservationV1", enabled, kind, action, 0.5, low, high, new(mode, missingLow, missingHigh, "fixture-quality"), "scalar-reference-state", Artifact(id, "v2", status, string.Empty));
        return value with { Artifact = value.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Observation(value)) } };
    }

    private static SplitAssignmentV2 Split(string id) => new(id, "synthetic-fixture", DataPartition.T5PilotOnly, id, "fixture", "T5_ONLY", "v2", TestSupport.FixtureWeights().ProvenanceHash.Sha256Hex);
    private static RuntimeValue<T> V<T>(T value, RuntimeValueStatus status) => new(value, status, "fixture-owner", "TEST_FIXTURE_ONLY");
    private static ArtifactIdentityV2 Artifact(string id, string version, RuntimeValueStatus status, string sha) => new(id, version, sha, status, "fixture-owner", "TEST_FIXTURE_ONLY");
    private static void Write<T>(string root, string name, T value) => File.WriteAllBytes(Path.Combine(root, name), CanonicalJson.Serialize(value));
}

internal sealed class DelegateTerminalEvaluator(Func<IDegradationBelief<double>, MaintenanceActionKind, Horizon, ConfigurationHash, CompletePolicyEvaluation> evaluate) : ITerminalActionEvaluator<double>
{
    public CompletePolicyEvaluation Evaluate(IDegradationBelief<double> belief, MaintenanceActionKind action, Horizon remainingHorizon, ConfigurationHash configurationHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return evaluate(belief, action, remainingHorizon, configurationHash);
    }
}

internal sealed class FixedDelayPropagator(IDegradationBelief<double>? survivor, double failureProbability = 0d) : IInformationDelayPropagator<double>
{
    public DelayPropagationRequest<double>? LastRequest { get; private set; }
    public DelayPropagationResult<double> Propagate(DelayPropagationRequest<double> request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        var components = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, x => x == ConsequenceComponentKind.Failure ? 1d : 0d);
        return new(EvaluationStatus.Success, new(failureProbability), survivor, new(1d), components, new("fixture-delay", 1, 1, new(0), new(0), new(failureProbability), new(failureProbability), true, "TEST_FIXTURE_ONLY", []), "fixture");
    }
}
