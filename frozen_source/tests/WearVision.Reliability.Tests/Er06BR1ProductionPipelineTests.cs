using System.Security.Cryptography;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class Er06BR1ProductionPipelineTests
{
    [TestMethod]
    public async Task R1_noop_delegate_cannot_report_success()
    {
        var root = Er05ATestSupport.CreateV2Package(ownerApproved: true, resourcesApproved: true);
        var loaded = new ConfigurationSetLoaderV2().Load(root);
        var result = await new T5CommandApplication().RunT5PilotAsync(root, Er05ATestSupport.ApprovedGrant(loaded), static (_, _) => Task.FromResult(0), default);
        Assert.AreEqual(5, result);
    }

    [TestMethod]
    public void R1_episode_generator_maps_actual_required_policy_evidence()
    {
        var loaded = LoadFixture();
        var run = loaded.ExperimentSet.Runs[0];
        var experiment = loaded.EffectiveExperiments[run.ExperimentId];
        var split = loaded.Split.Assignments.First(x => x.EpisodeId.StartsWith(run.ExperimentId.Value, StringComparison.Ordinal));
        var lineage = new T5PlanningLineage(experiment.ExperimentId, run.EffectiveConfigSha256, "UNIT-RUN", ConfigurationProfileKind.TestFixtureOnly, "AUTH-FIXTURE", true, false, false);
        var result = new T5ProductionEpisodeGenerator().Generate(new(loaded, experiment, split, lineage, "UNIT-RUN"), default);
        Assert.IsTrue(result.PlanningInput.ProductionMapped);
        Assert.IsNotNull(result.PlanningInput.ProposedOutcome);
        Assert.IsNotNull(result.PlanningInput.B7Outcome);
        Assert.IsNotNull(result.PlanningInput.B4Outcome);
        Assert.IsNotNull(result.PlanningInput.B1Outcome);
        Assert.IsNotNull(result.PlanningInput.B6Outcome);
        var policies = result.RawRecords.OfType<PolicyEvaluationRecord>().Select(x => (string)x.Fields["policy_id"]!).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(RequiredPolicies.All(policies.Contains));
    }

    [TestMethod]
    public void R1_episode_generator_is_byte_deterministic_and_crn_coupled()
    {
        var loaded = LoadFixture();
        var run = loaded.ExperimentSet.Runs[0];
        var experiment = loaded.EffectiveExperiments[run.ExperimentId];
        var split = loaded.Split.Assignments[0];
        var lineage = new T5PlanningLineage(experiment.ExperimentId, run.EffectiveConfigSha256, "DETERMINISTIC", ConfigurationProfileKind.TestFixtureOnly, "AUTH-FIXTURE", true, false, false);
        var request = new T5EpisodeGenerationRequest(loaded, experiment, split, lineage, "DETERMINISTIC");
        var first = new T5ProductionEpisodeGenerator().Generate(request, default);
        var second = new T5ProductionEpisodeGenerator().Generate(request, default);
        CollectionAssert.AreEqual(CanonicalJson.Serialize(first), CanonicalJson.Serialize(second));
        Assert.AreEqual(split.GroupingKey + "-CRN", first.PlanningInput.PairedCouplingId);
    }

    [TestMethod]
    public void R1_deployable_decision_input_has_zero_oracle_access_and_no_truth_field()
    {
        var result = GenerateFirstEpisode();
        var decisionInput = result.RawRecords.OfType<DecisionInputRecord>().Single();
        Assert.AreEqual(0, decisionInput.Fields["oracle_access_count"]);
        Assert.IsFalse(decisionInput.Fields.Keys.Any(x => x.Contains("true", StringComparison.OrdinalIgnoreCase) || x.Contains("dangerous", StringComparison.OrdinalIgnoreCase) || x.Contains("future", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task R1_positive_service_path_writes_raw_planning_lineage_and_completion_last()
    {
        var loaded = LoadFixture();
        var (authorization, identity) = IdentityAndAuthorization(loaded);
        var runId = TestSupport.NewFixtureRunId("er06b-r1-positive");
        var service = new T5PilotExecutionService(Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv"), clock: () => DateTimeOffset.UnixEpoch);
        var result = await service.ExecuteAsync(loaded, authorization, identity, runId, default);
        Assert.AreEqual(0, result.ExitCode, result.ErrorCode);
        Assert.AreEqual(4L, result.AccountedEpisodes);
        Assert.IsTrue(File.Exists(Path.Combine(result.RunRoot, "completion-manifest.json")));
        Assert.IsFalse(File.Exists(Path.Combine(result.RunRoot, "RUN_INCOMPLETE")));
        Assert.IsTrue(File.Exists(Path.Combine(result.RunRoot, "lineage", "execution-lineage.json")));
        foreach (var schema in RequiredProductionSchemas)
            Assert.IsTrue(File.Exists(Path.Combine(result.RunRoot, "raw", schema + ".csv")), schema);
        var completionTime = File.GetLastWriteTimeUtc(Path.Combine(result.RunRoot, "completion-manifest.json"));
        Assert.IsFalse(Directory.GetFiles(result.RunRoot, "*", SearchOption.AllDirectories).Where(x => !x.EndsWith("completion-manifest.json", StringComparison.Ordinal)).Any(x => File.GetLastWriteTimeUtc(x) > completionTime));
    }

    [TestMethod]
    public async Task R1_cancelled_service_retains_incomplete_evidence_without_completion()
    {
        var loaded = LoadFixture();
        var (authorization, identity) = IdentityAndAuthorization(loaded);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new T5PilotExecutionService(Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv"))
            .ExecuteAsync(loaded, authorization, identity, TestSupport.NewFixtureRunId("er06b-r1-cancel"), cancellation.Token);
        Assert.AreEqual(5, result.ExitCode);
        Assert.IsTrue(File.Exists(Path.Combine(result.RunRoot, "RUN_INCOMPLETE")));
        Assert.IsFalse(File.Exists(Path.Combine(result.RunRoot, "completion-manifest.json")));
    }

    [TestMethod]
    public void R1_completion_gate_rejects_zero_missing_duplicate_and_wrong_rows()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionCompletionGate.Validate(["FX5-001", "FX5-002"], [], ["A", "B"], []));
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionCompletionGate.Validate(["FX5-001", "FX5-002"], ["FX5-001", "FX5-002"], ["A", "B"], ["A"]));
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionCompletionGate.Validate(["FX5-001", "FX5-002"], ["FX5-001", "FX5-002"], ["A", "B"], ["A", "A"]));
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionCompletionGate.Validate(["FX5-001", "FX5-002"], ["FX5-001", "FX5-999"], ["A", "B"], ["A", "B"]));
    }

    [TestMethod]
    public void R1_storage_gate_stops_before_ceiling_and_retains_incomplete_marker()
    {
        var root = Path.Combine(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("er06b-r1-storage"));
        Directory.CreateDirectory(root);
        using var writer = new T5ProductionWriteSession(root, "run", 64, RawSchemaRegistry.Load(Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv")));
        Assert.ThrowsExactly<IOException>(() => writer.WriteJson("large.json", new { Value = new string('x', 256) }));
        Assert.IsTrue(File.Exists(Path.Combine(writer.Root, "RUN_INCOMPLETE")));
        Assert.IsFalse(File.Exists(Path.Combine(writer.Root, "completion-manifest.json")));
    }

    [TestMethod]
    public void R1_raw_writer_rejects_incomplete_frozen_schema_record()
    {
        var root = Path.Combine(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("er06b-r1-raw-invalid"));
        Directory.CreateDirectory(root);
        using var writer = new T5ProductionWriteSession(root, "run", 1_000_000, RawSchemaRegistry.Load(Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv")));
        Assert.ThrowsExactly<InvalidDataException>(() => writer.WriteRaw(new DecisionInputRecord().Set("run_id", "R")));
        Assert.IsFalse(File.Exists(Path.Combine(writer.Root, "completion-manifest.json")));
    }

    [TestMethod]
    public void R1_lineage_and_mandatory_artifacts_are_independent_completion_gates()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionEvidenceClosureGate.Validate(true, true, true, false, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionEvidenceClosureGate.Validate(true, true, false, true, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => T5ProductionEvidenceClosureGate.Validate(true, true, true, true, false));
        T5ProductionEvidenceClosureGate.Validate(true, true, true, true, true);
    }

    [TestMethod]
    public void R1_source_config_and_executable_hash_mismatches_fail_closed()
    {
        var loaded = LoadFixture();
        var (authorization, identity) = IdentityAndAuthorization(loaded);
        var validator = new T5SourceBoundAuthorizationValidator();
        Assert.IsEmpty(validator.Validate(loaded, authorization, identity, DateTimeOffset.UnixEpoch));
        Assert.IsTrue(validator.Validate(loaded, authorization with { SourceSnapshotSha256 = new string('A', 64) }, identity, DateTimeOffset.UnixEpoch).Any(x => x.Code == "T5_SOURCE_SNAPSHOT_IDENTITY_MISMATCH"));
        Assert.IsTrue(validator.Validate(loaded, authorization with { ConfigSetSha256 = new string('B', 64) }, identity, DateTimeOffset.UnixEpoch).Any(x => x.Code == "T5_SOURCE_BOUND_CONFIG_HASH_MISMATCH"));
        Assert.IsTrue(validator.Validate(loaded, authorization with { ExecutableSha256 = new string('C', 64) }, identity, DateTimeOffset.UnixEpoch).Any(x => x.Code == "T5_EXECUTABLE_IDENTITY_MISMATCH"));
    }

    [TestMethod]
    public async Task R1_planning_harness_rejects_defaulted_production_mapping()
    {
        var loaded = LoadFixture();
        var run = loaded.ExperimentSet.Runs[0];
        var lineage = new T5PlanningLineage(run.ExperimentId, run.EffectiveConfigSha256, "RUN", ConfigurationProfileKind.TestFixtureOnly, "AUTH", true, false, false);
        var incomplete = new T5PlanningEpisodeInput(lineage, new("EP-1"), 1, true, 0d, 0.1, 0.2, false, false, CensoringKind.Administrative, 0, true, 1, ProductionMapped: true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new T5PlanningHarness().RunAsync(new(lineage, new(null, 1_000_000, 1, false), false), Enumerate(incomplete), default));
    }

    [TestMethod]
    public void R1_fixture_hash_is_distinct_from_approved_T5_hash()
    {
        var loaded = LoadFixture();
        Assert.AreNotEqual("1E614462F3148FA48C11D0DC2B689E17A078A942FCA1900107515DB10655790C", loaded.ConfigSetSha256);
        Assert.AreEqual(ConfigurationProfileKind.TestFixtureOnly, loaded.Manifest.ProfileKind);
        Assert.HasCount(4, loaded.Split.Assignments);
        Assert.AreEqual(32, loaded.Numerical.McMaximumPaths);
    }

    [TestMethod]
    public void R1_production_cli_source_constructs_service_generator_harness_writer_and_mapping()
    {
        var root = TestSupport.WorkspaceRoot;
        var experimentSource = File.ReadAllText(Path.Combine(root, "src", "WearVision.Reliability.Experiments", "ExperimentHarness.cs"));
        var serviceSource = File.ReadAllText(Path.Combine(root, "src", "WearVision.Reliability.Experiments", "T5PilotExecutionService.cs"));
        var persistenceSource = File.ReadAllText(Path.Combine(root, "src", "WearVision.Reliability.Infrastructure", "T5ProductionPersistence.cs"));
        Assert.DoesNotContain("Task.FromResult(0)", experimentSource, StringComparison.Ordinal);
        StringAssert.Contains(experimentSource, "T5PilotExecutionService");
        StringAssert.Contains(serviceSource, "new T5ProductionEpisodeGenerator()");
        StringAssert.Contains(serviceSource, "new T5PlanningHarness()");
        StringAssert.Contains(persistenceSource, "new T5PlanningWriteSession");
        StringAssert.Contains(serviceSource, "new T5PlanningEpisodeInput");
    }

    private static readonly string[] RequiredProductionSchemas =
    [
        "SCH-RUN", "SCH-ENV", "SCH-SPL", "SCH-PST", "SCH-TRJ", "SCH-DIN", "SCH-PEV",
        "SCH-BRN", "SCH-DEC", "SCH-FEV", "SCH-CON", "SCH-REL", "SCH-NUM"
    ];

    private static readonly string[] RequiredPolicies =
    [
        "P-R4-TWO-STAGE", "B7-CONSTRAINED-ONE-STAGE", "B4-POSTERIOR-QUANTILE", "B1-PERIODIC", "B6-UNCONSTRAINED-BAYES"
    ];

    private static LoadedConfigurationSetV2 LoadFixture() => new ConfigurationSetLoaderV2().Load(Path.Combine(TestSupport.WorkspaceRoot, "configs", "fixtures", "t5-production-e2e"));

    private static T5EpisodeExecutionResult GenerateFirstEpisode()
    {
        var loaded = LoadFixture();
        var run = loaded.ExperimentSet.Runs[0];
        var experiment = loaded.EffectiveExperiments[run.ExperimentId];
        var lineage = new T5PlanningLineage(experiment.ExperimentId, run.EffectiveConfigSha256, "UNIT-RUN", ConfigurationProfileKind.TestFixtureOnly, "AUTH-FIXTURE", true, false, false);
        return new T5ProductionEpisodeGenerator().Generate(new(loaded, experiment, loaded.Split.Assignments[0], lineage, "UNIT-RUN"), default);
    }

    private static (T5SourceBoundAuthorizationV2 Authorization, T5ExecutionIdentity Identity) IdentityAndAuthorization(LoadedConfigurationSetV2 loaded)
    {
        var snapshot = Path.Combine(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("er06b-r1-source") + ".zip");
        File.WriteAllBytes(snapshot, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("TEST_FIXTURE_ONLY_SOURCE_SNAPSHOT")));
        var executable = typeof(T5PilotExecutionService).Assembly.Location;
        var identity = T5ExecutionIdentity.Capture(snapshot, executable);
        var outputHash = ConfigurationSetLoaderV2.HashFile(Path.Combine(loaded.Root, loaded.Manifest.Files.Single(x => x.Role == "output-policy").RelativePath));
        var authorization = new T5SourceBoundAuthorizationV2(
            "t5-source-bound-authorization-v2",
            "ER06B-R1-TEST-FIXTURE-ONLY-AUTH",
            AuthorizationGrantState.Approved,
            T5ExecutionAuthorizationScope.TestFixtureOnly,
            loaded.ConfigSetSha256,
            loaded.ExperimentSet.Runs.Select(x => new AuthorizedExperimentReferenceV2(x.ExperimentId, x.EffectiveConfigSha256)).ToArray(),
            loaded.ExperimentSet.ProtocolVersion,
            loaded.Manifest.AuthorizedStage,
            outputHash,
            identity.SourceSnapshotSha256,
            Path.GetFileName(identity.SourceSnapshotPath),
            identity.ExecutableSha256,
            Path.GetFileName(identity.ExecutablePath),
            "ER06B_R1_FIXTURE_QA",
            "ER06A-R2-20260821-T5-001-T5-002-EXACT-HASH-APPROVED",
            null);
        return (authorization, identity);
    }

    private static async IAsyncEnumerable<T5PlanningEpisodeInput> Enumerate(T5PlanningEpisodeInput value)
    {
        await Task.Yield();
        yield return value;
    }
}
