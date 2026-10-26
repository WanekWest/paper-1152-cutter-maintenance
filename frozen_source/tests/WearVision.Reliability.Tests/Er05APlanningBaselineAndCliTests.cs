using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class Er05ABaselineFactoryTests
{
    [TestMethod]
    public void GAP07_factory_consumes_B1_B4_B6_B7_and_preserves_B2_NotApplicable()
    {
        var factory = new TypedBaselineFactory<double>(new ConstrainedPolicySelector(), new Dictionary<string, Func<double, double>> { ["failure-oriented-state-v1"] = x => x });
        var set = factory.Bind(Er05ATestSupport.Baselines());
        Assert.IsNotNull(set.Get("B1-PERIODIC").Policy);
        Assert.IsNotNull(set.Get("B4-POSTERIOR-QUANTILE").Policy);
        Assert.IsNotNull(set.Get("B6-UNCONSTRAINED-BAYES").Policy);
        Assert.IsNotNull(set.Get("B7-CONSTRAINED-ONE-STAGE").Policy);
        Assert.AreEqual(BaselineApplicabilityStatus.NotApplicable, set.Get("B2-OBSERVED-PROXY").Applicability);
        Assert.IsNull(set.Get("B2-OBSERVED-PROXY").Policy);
    }

    [TestMethod]
    public void GAP07_B1_uses_configured_interval()
    {
        var set = Factory().Bind(Er05ATestSupport.Baselines());
        var input = new BaselineDecisionInput<double>(new(0.7), null, null, null, null, [], false);
        Assert.AreEqual(MaintenanceActionKind.PlannedReplacement, set.Get("B1-PERIODIC").Policy!.Evaluate(input).Action);
    }

    [TestMethod]
    public void GAP07_B4_uses_configured_quantile_and_threshold()
    {
        var set = Factory().Bind(Er05ATestSupport.Baselines());
        var stateSpace = new StateSpaceDescriptor("s", "v", "u");
        var belief = new ImmutableWeightedSampleBelief<double>(new("p"), stateSpace, ReferenceArtifactHasher.HashText("p"), [new(0.2, 0.25), new(0.8, 0.75)]);
        var input = new BaselineDecisionInput<double>(new(0), belief, null, x => x, null, [], false);
        Assert.AreEqual(MaintenanceActionKind.PlannedReplacement, set.Get("B4-POSTERIOR-QUANTILE").Policy!.Evaluate(input).Action);
    }

    [TestMethod]
    public void GAP07_disabled_B3_and_B5_have_no_runnable_policy()
    {
        var set = Factory().Bind(Er05ATestSupport.Baselines());
        Assert.IsNull(set.Get("B3-POSTERIOR-MEAN").Policy);
        Assert.IsNull(set.Get("B5-FIRST-PASSAGE-QUANTILE").Policy);
    }

    [TestMethod]
    public void GAP07_missing_design_artifact_fails_closed()
    {
        var config = Er05ATestSupport.Baselines();
        var items = config.Baselines.Select(x => x.BaselineId == "B1-PERIODIC" ? x with { DesignArtifactSha256 = string.Empty } : x).ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() => Factory().Bind(config with { Baselines = items }));
    }

    [TestMethod]
    public void GAP07_B6_ignores_constraint_while_B7_uses_constrained_selector()
    {
        var set = Factory().Bind(Er05ATestSupport.Baselines());
        var infeasibleLowJ = TestSupport.PolicyEvaluation("P-CONTINUE", 0.5, 0.4, 0.6, 0.1, FeasibilityClassification.ProvenInfeasible);
        var feasible = TestSupport.PolicyEvaluation("P-REPLACE", 0.05, 0.01, 0.1, 0.3, FeasibilityClassification.ProvenFeasible, action: MaintenanceActionKind.PlannedReplacement);
        var input = new BaselineDecisionInput<double>(new(0), null, null, null, null, [infeasibleLowJ, feasible], false);
        Assert.AreEqual(MaintenanceActionKind.Continue, set.Get("B6-UNCONSTRAINED-BAYES").Policy!.Evaluate(input).Action);
        Assert.AreEqual(MaintenanceActionKind.PlannedReplacement, set.Get("B7-CONSTRAINED-ONE-STAGE").Policy!.Evaluate(input).Action);
    }

    private static TypedBaselineFactory<double> Factory() => new(new ConstrainedPolicySelector(), new Dictionary<string, Func<double, double>> { ["failure-oriented-state-v1"] = x => x });
}

[TestClass]
public sealed class Er05APlanningHarnessTests
{
    [TestMethod]
    public async Task GAP09_fixture_harness_produces_all_planning_quantity_classes()
    {
        var lineage = Lineage();
        var result = await new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 4), default);
        Assert.AreEqual(T5PlanningSchemaIds.RunResourceSummary, result.Resources.SchemaId);
        Assert.AreEqual(16L, result.Numerical.TotalPathCount);
        Assert.AreEqual(2L, result.Numerical.ConvergedEpisodes);
        Assert.AreEqual(2L, result.Events.FailureCount);
        Assert.AreEqual(4L, result.Censoring.Counts.Values.Sum());
        Assert.AreEqual(4L, result.Occupancy.BinCounts.Values.Sum());
        Assert.HasCount(4, result.PairedDifferences);
        Assert.IsTrue(result.Manifest.Completed);
    }

    [TestMethod]
    public async Task GAP09_missing_pair_is_recorded_explicitly()
    {
        var lineage = Lineage();
        var result = await new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 3, missingPair: true), default);
        Assert.AreEqual("MISSING_PAIR_EXPLICIT", result.PairedDifferences.Single(x => !x.PairAvailable).Status);
    }

    [TestMethod]
    public async Task GAP09_repeated_decision_epochs_do_not_increase_independent_episode_denominator()
    {
        var lineage = Lineage();
        var result = await new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 3), default);
        Assert.AreEqual(3L, result.Manifest.IndependentEpisodeDenominator);
        Assert.AreEqual(3L, result.Manifest.RawEpisodeDenominator);
    }

    [TestMethod]
    public async Task GAP09_planning_manifest_is_rejected_for_T6_and_manuscript()
    {
        var lineage = Lineage();
        var result = await new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 1), default);
        Assert.AreEqual(EvaluationStatus.ProtocolViolation, T5PlanningArtifactFirewall.ValidateAsT6Input(result.Manifest));
        Assert.AreEqual(EvaluationStatus.ProtocolViolation, T5PlanningArtifactFirewall.ValidateAsManuscriptInput(result.Manifest));
    }

    [TestMethod]
    public async Task GAP09_cancelled_fixture_run_has_no_complete_manifest_status()
    {
        var lineage = Lineage();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 2, cancellationToken: cancellation.Token), cancellation.Token);
        Assert.IsFalse(result.Manifest.Completed);
        Assert.IsTrue(result.Resources.ResourceCancelled);
        Assert.AreEqual("INCOMPLETE_NOT_EVIDENCE", result.Manifest.Status);
    }

    [TestMethod]
    public async Task GAP09_storage_writer_completes_only_noneligible_planning_manifest()
    {
        var lineage = Lineage();
        var result = await new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 1), default);
        var root = Path.Combine(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("planning-storage"));
        Directory.CreateDirectory(root);
        var session = new T5PlanningWriteSession(root, "run", 1_000_000, ConfigurationProfileKind.TestFixtureOnly);
        session.WriteJson("resource.json", result.Resources);
        var hash = session.Complete(result.Manifest);
        Assert.AreEqual(64, hash.Sha256Hex.Length);
        Assert.IsFalse(File.Exists(Path.Combine(session.Root, "RUN_INCOMPLETE")));
        Assert.IsTrue(File.Exists(Path.Combine(session.Root, "completion-manifest.json")));
    }

    [TestMethod]
    public void GAP09_storage_ceiling_fails_cleanly_and_preserves_incomplete_marker()
    {
        var root = Path.Combine(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("planning-quota"));
        Directory.CreateDirectory(root);
        var session = new T5PlanningWriteSession(root, "run", 10, ConfigurationProfileKind.TestFixtureOnly);
        Assert.ThrowsExactly<IOException>(() => session.WriteJson("too-large.json", new { Value = new string('x', 100) }));
        Assert.IsTrue(File.Exists(Path.Combine(session.Root, "RUN_INCOMPLETE")));
        Assert.IsFalse(File.Exists(Path.Combine(session.Root, "completion-manifest.json")));
    }

    [TestMethod]
    public void GAP09_invalid_lineage_cannot_claim_T6_eligibility()
    {
        var lineage = Lineage() with { EligibleForT6 = true };
        Assert.ThrowsExactly<InvalidOperationException>(() => new T5PlanningHarness().RunAsync(Request(lineage), Er05ATestSupport.Episodes(lineage, 1), default).GetAwaiter().GetResult());
    }

    private static T5PlanningLineage Lineage() => new(new("T5-FIXTURE"), new string('A', 64), "RUN-FIXTURE", ConfigurationProfileKind.TestFixtureOnly, "AUTH-FIXTURE", true, false, false);
    private static T5PlanningRunRequest Request(T5PlanningLineage lineage) => new(lineage, new(null, 1_000_000, 1, false), true);
}

[TestClass]
public sealed class Er05ACliAndPreflightTests
{
    private static readonly string[] ExpectedPreflightCodes = ["OWNER_APPROVAL_REQUIRED", "RESOURCE_CEILING_OWNER_DECISION_REQUIRED", "T5_AUTHORIZATION_MISSING"];

    [TestMethod]
    public void GAP10_config_validate_reaches_runtime_compatibility_without_computation()
    {
        var report = new T5CommandApplication().ValidateConfiguration(Er05ATestSupport.CreateV2Package());
        Assert.IsTrue(report.RuntimeSchemaCompatible && report.ModelBindingCompatible && report.BaselineBindingCompatible && report.MultiRowBindingCompatible && report.PlanningHarnessCompatible);
        Assert.IsEmpty(report.Issues);
        Assert.IsFalse(report.ExecutionStarted);
    }

    [TestMethod]
    public void GAP10_unapproved_candidate_preflight_reports_only_owner_resource_authorization_gates()
    {
        var report = new T5CommandApplication().Preflight(Er05ATestSupport.CreateV2Package(), null);
        CollectionAssert.AreEquivalent(
            ExpectedPreflightCodes,
            report.Issues.Select(x => x.Code).ToArray());
        Assert.IsFalse(report.Issues.Any(x => x.Code.Contains("IMPLEMENTATION", StringComparison.Ordinal)));
        Assert.IsFalse(report.ExecutionStarted);
    }

    [TestMethod]
    public async Task GAP10_t5_pilot_block_does_not_invoke_execution_delegate()
    {
        var invoked = false;
        var code = await new T5CommandApplication().RunT5PilotAsync(Er05ATestSupport.CreateV2Package(), null, (_, _) => { invoked = true; return Task.FromResult(0); }, default);
        Assert.AreEqual(4, code);
        Assert.IsFalse(invoked);
    }

    [TestMethod]
    public void GAP10_t6_command_is_unconditionally_disabled()
    {
        Assert.AreEqual(6, new T5CommandApplication().RunT6());
    }

    [TestMethod]
    public void GAP10_no_semantic_bypass_flags_or_environment_switches_exist()
    {
        var source = File.ReadAllText(Path.Combine(TestSupport.WorkspaceRoot, "src", "WearVision.Reliability.Experiments", "ExperimentHarness.cs"));
        foreach (var bypass in new[] { "--force", "--ignore-approval", "--skip-preflight", "--allow-unapproved", "--use-fixtures-as-pilot", "--run-t6-anyway" })
            Assert.DoesNotContain(bypass, source, StringComparison.OrdinalIgnoreCase);
    }
}
