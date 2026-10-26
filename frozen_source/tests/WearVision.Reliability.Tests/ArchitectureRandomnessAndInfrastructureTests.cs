using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class RandomnessKnownAnswerTests
{
    private static readonly byte[] ExpectedKeyBytes = Convert.FromHexString("06000000726E672D7631EFCDAB89674523010700000053454D2D3030320A00000046582D524E472D4B41540800000054524A2D303030310700000045502D30303031080000004445432D303030310800000043524E2D30303031030000000700000000000000");
    private static readonly byte[] ExpectedDigest = Convert.FromHexString("456ED777A62F914B68B345574430B348122A653D57421B2D129BE2BB4525AD29");
    private static readonly ulong[] ExpectedSequence = [0xBFBE01ABA044A5DDUL, 0x0ABC6B224B3B0D8BUL, 0xE0733305361025B6UL, 0x97CDF7A4D5DE88C4UL, 0x5D2B3F8E77309588UL];

    [TestMethod]
    public void Canonical_key_digest_state_sequence_and_unit_conversion_match_independent_vector()
    {
        var factory = new Sha256XoshiroRandomStreamFactory();
        var key = new RandomStreamKey(TestSupport.StreamContext("0001") with
        {
            ExperimentId = new("SEM-002"), ScenarioId = new("FX-RNG-KAT"), TrajectoryId = new("TRJ-0001"),
            ComponentEpisodeId = new("EP-0001"), DecisionId = new("DEC-0001"), CouplingGroupId = "CRN-0001"
        }, StreamPurpose.DegradationInnovation, 7UL);
        CollectionAssert.AreEqual(ExpectedKeyBytes, factory.EncodeKey(key));
        CollectionAssert.AreEqual(ExpectedDigest, factory.DeriveDigest(key));
        var state = Xoshiro256StarStar.DecodeInitialState(ExpectedDigest);
        Assert.AreEqual(0x4B912FA677D76E45UL, state.S0);
        Assert.AreEqual(0x48B330445745B368UL, state.S1);
        Assert.AreEqual(0x2D1B42573D652A12UL, state.S2);
        Assert.AreEqual(0x29AD2545BBE29B12UL, state.S3);
        var stream = factory.Create(key);
        foreach (var expected in ExpectedSequence) Assert.AreEqual(expected, stream.NextUInt64());
        var unitStream = factory.Create(key);
        Assert.AreEqual(0.7489930194863228d, unitStream.NextUnitDouble(), 0d);
    }

    [TestMethod]
    public void Same_key_repeats_and_every_changed_field_diverges()
    {
        var factory = new Sha256XoshiroRandomStreamFactory();
        var key = new RandomStreamKey(TestSupport.StreamContext(), StreamPurpose.PosteriorDraw, 0);
        CollectionAssert.AreEqual(factory.DeriveDigest(key), factory.DeriveDigest(key));
        var changed = new[]
        {
            key with { Purpose = StreamPurpose.DegradationInnovation },
            key with { SubstreamIndex = 1 },
            key with { Context = key.Context with { ExperimentId = new("SEM-003") } },
            key with { Context = key.Context with { ScenarioId = new("FX-other") } },
            key with { Context = key.Context with { TrajectoryId = new("TRJ-other") } },
            key with { Context = key.Context with { ComponentEpisodeId = new("EP-other") } },
            key with { Context = key.Context with { DecisionId = new("DEC-other") } },
            key with { Context = key.Context with { CouplingGroupId = "CRN-other" } }
        };
        Assert.IsTrue(changed.All(x => !factory.DeriveDigest(key).SequenceEqual(factory.DeriveDigest(x))));
    }
}

[TestClass]
public sealed class ArchitectureAndOracleBoundaryTests
{
    [TestMethod]
    public void Production_project_references_follow_the_frozen_graph()
    {
        var projects = Directory.GetFiles(Path.Combine(TestSupport.WorkspaceRoot, "src"), "*.csproj", SearchOption.AllDirectories);
        foreach (var project in projects.Where(x => !x.Contains("Experiments", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(project);
            Assert.DoesNotContain("WearVision.Reliability.Experiments", text, StringComparison.Ordinal);
            Assert.DoesNotContain("WearVision.SyntheticStand", text, StringComparison.Ordinal);
        }
        var domain = File.ReadAllText(projects.Single(x => x.Contains("Domain", StringComparison.Ordinal)));
        Assert.DoesNotContain("ProjectReference", domain, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Deployable_assemblies_have_no_reference_to_experiments_or_legacy()
    {
        foreach (var assembly in new[] { typeof(Probability).Assembly, typeof(ConstrainedPolicySelector).Assembly, typeof(CanonicalJson).Assembly })
        {
            var names = assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
            Assert.IsFalse(names.Contains("WearVision.Reliability.Experiments", StringComparer.Ordinal));
            Assert.IsFalse(names.Any(x => x?.Contains("SyntheticStand", StringComparison.Ordinal) == true));
        }
    }

    [TestMethod]
    public void Deployable_types_have_no_prohibited_oracle_members()
    {
        var prohibited = new[] { "TrueState", "TrueWear", "TrueDamage", "TrueRul", "FuturePath", "FutureFailure", "FutureObservation", "DangerousWear", "HiddenTrueMode" };
        var findings = TestSupport.DeployableTypes().SelectMany(t => t.GetMembers().Select(m => $"{t.FullName}.{m.Name}"))
            .Where(name => prohibited.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert.IsEmpty(findings);
    }
}

[TestClass]
public sealed class SchemaConfigurationAndPersistenceTests
{
    [TestMethod]
    public void Frozen_numerical_registry_loads_all_24_typed_tolerances()
    {
        var registry = NumericalToleranceRegistry.Load(Path.Combine(TestSupport.SchemaRoot, "numerical-tolerance-registry.csv"));
        Assert.HasCount(24, registry.Entries);
        Assert.HasCount(24, registry.Entries.Select(x => x.ToleranceId).Distinct(StringComparer.Ordinal));
        Assert.IsTrue(registry.Entries.Any(x => x.ToleranceId == "TOL-PROB-COMP" && x.ToDomainDefinition().Category == "probability"));
    }

    [TestMethod]
    public void Fixture_configuration_set_is_hash_verified_and_explicitly_non_scientific()
    {
        var config = new ConfigurationSetLoader().Load(Path.Combine(TestSupport.WorkspaceRoot, "configs", "fixtures"));
        Assert.AreEqual(ConfigurationProfileKind.TestFixtureOnly, config.Model.ProfileKind);
        Assert.IsFalse(config.Reliability.Horizon.HasValue);
        Assert.IsFalse(config.Consequences.Weights.HasValue);
        Assert.AreEqual(RunStage.T4, config.Experiment.Stage);
    }

    private sealed record HashFixture(int Z, string A);
    private sealed record StrictFixture(int Value);

    [TestMethod]
    public void Raw_schema_registry_maps_all_14_schemas_and_267_fields()
    {
        var registry = RawSchemaRegistry.Load(Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv"));
        Assert.HasCount(267, registry.Fields);
        Assert.HasCount(14, registry.SchemaIds);
        Assert.IsTrue(registry.Fields.All(x => !string.IsNullOrWhiteSpace(registry.ImplementedTypeFor(x))));
        var recordTypes = new RawEvidenceRecord[]
        {
            new RunManifestRecord(), new EnvironmentManifestRecord(), new SplitManifestRecord(), new PosteriorArtifactManifestRecord(),
            new TrajectoryManifestRecord(), new DecisionInputRecord(), new PolicyEvaluationRecord(), new InformationBranchRecord(),
            new DecisionRecord(), new FailureEventRecord(), new ConsequenceRecord(), new ReliabilityPredictionRecord(),
            new NumericalBatchRecord(), new OracleResultRecord()
        };
        CollectionAssert.AreEquivalent(registry.SchemaIds.ToArray(), recordTypes.Select(x => x.SchemaId).ToArray());
    }

    [TestMethod]
    public void Canonical_json_is_repeatable_ordered_and_hash_sensitive()
    {
        var a = new HashFixture(2, "x");
        CollectionAssert.AreEqual(CanonicalJson.Serialize(a), CanonicalJson.Serialize(a));
        Assert.AreEqual("{\"A\":\"x\",\"Z\":2}", Encoding.UTF8.GetString(CanonicalJson.Serialize(a)));
        Assert.AreNotEqual(CanonicalJson.Hash(a), CanonicalJson.Hash(a with { Z = 3 }));
    }

    [TestMethod]
    public void Strict_json_rejects_unknown_members()
    {
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => CanonicalJson.DeserializeStrict<StrictFixture>("{\"Value\":1,\"Unknown\":2}"));
    }

    [TestMethod]
    public void T5_T6_and_fixture_profile_escalation_fail_closed()
    {
        var guard = new ExperimentAuthorizationGuard();
        foreach (var stage in new[] { RunStage.T5, RunStage.T6 })
        {
            var config = TestSupport.FixtureConfiguration(stage, ConfigurationProfileKind.TestFixtureOnly).Experiment;
            var row = new ExperimentMatrixRow(config.ExperimentId, "pilot", stage, stage == RunStage.T5 ? "AFTER_ER05_TEST_PASS" : "T6_NOT_AUTHORIZED", config.ScenarioId, true);
            Assert.IsNotEmpty(guard.Validate(config, row));
        }
    }

    [TestMethod]
    public void Write_once_completion_rejects_overwrite_and_path_escape()
    {
        var run = new WriteOnceRunDirectory(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("lifecycle"));
        run.WriteText("raw/one.csv", "TEST_FIXTURE_ONLY");
        Assert.ThrowsExactly<IOException>(() => run.WriteText("raw/one.csv", "overwrite"));
        Assert.ThrowsExactly<InvalidOperationException>(() => run.WriteText("../escape.txt", "escape"));
        var hash = run.Complete();
        Assert.AreEqual(64, hash.Sha256Hex.Length);
        Assert.IsTrue(File.Exists(Path.Combine(run.RunRoot, "completion-manifest.json")));
        Assert.ThrowsExactly<InvalidOperationException>(() => run.WriteText("raw/two.csv", "late"));
    }
}

[TestClass]
public sealed class BaselineAblationAndHarnessTests
{
    [TestMethod]
    public void Baseline_registry_implements_B0_through_B7_without_hidden_parameters()
    {
        var selector = new ConstrainedPolicySelector();
        var baselines = new IBaselinePolicy<ScalarState>[]
        {
            new RunToFailureBaseline<ScalarState>(), new PeriodicReplacementBaseline<ScalarState>(null), new ObservedProxyThresholdBaseline<ScalarState>(null),
            new PosteriorMeanBaseline<ScalarState>(null), new PosteriorQuantileBaseline<ScalarState>(null, null), new FirstPassageQuantileBaseline<ScalarState>(null),
            new UnconstrainedBayesBaseline<ScalarState>(), new ConstrainedBayesBaseline<ScalarState>(selector)
        };
        Assert.HasCount(8, baselines);
        Assert.HasCount(8, baselines.Select(x => x.BaselineId).Distinct().ToArray());
        var empty = new BaselineDecisionInput<ScalarState>(new(0), null, null, null, null, [], true);
        Assert.AreEqual(BaselineStatus.Success, baselines[0].Evaluate(empty).Status);
        Assert.IsTrue(baselines.Skip(1).Take(5).All(x => x.Evaluate(empty).Status is BaselineStatus.NotEvaluable or BaselineStatus.NotApplicable));
        Assert.IsTrue(baselines.All(x => x.Evaluate(empty).OracleAccessCount == 0));
    }

    [TestMethod]
    public void Ablation_registry_has_1_through_8_with_7_not_applicable_and_8_nondeployable()
    {
        Assert.HasCount(8, AblationRegistry.MinimumR4);
        Assert.IsFalse(AblationRegistry.MinimumR4.Single(x => x.Id == "ABL-7").Applicable);
        Assert.AreEqual("NotApplicable", AblationRegistry.MinimumR4.Single(x => x.Id == "ABL-7").Status);
        Assert.IsFalse(AblationRegistry.MinimumR4.Single(x => x.Id == "ABL-8").Deployable);
        Assert.IsFalse(new SequentialPolicyIntegrationPlaceholder().Enabled);
    }

    [TestMethod]
    public void Harness_loads_matrix_and_authorizes_only_ER05_semantic_rows()
    {
        var harness = new ProtocolAwareExperimentHarness(new ExperimentAuthorizationGuard());
        var rows = harness.LoadMatrix(Path.Combine(TestSupport.SchemaRoot, "experiment-matrix.csv"));
        Assert.HasCount(42, rows);
        var semantic = rows.First(x => x.ExperimentId.Value == "SEM-001");
        var config = TestSupport.FixtureConfiguration(stage: semantic.Stage, experimentId: semantic.ExperimentId.Value).Experiment;
        Assert.IsTrue(harness.Authorize(config, semantic).Accepted);
        var scientific = rows.First(x => x.ExperimentId.Value == "SCI-001");
        var blocked = TestSupport.FixtureConfiguration(stage: RunStage.T6, profile: ConfigurationProfileKind.T6Scientific, experimentId: scientific.ExperimentId.Value).Experiment;
        Assert.IsFalse(harness.Authorize(blocked, scientific).Accepted);
    }

    [TestMethod]
    public void All_19_frozen_fixture_IDs_are_present()
    {
        Assert.HasCount(19, FixtureCatalog.RequiredFixtureIds);
        Assert.HasCount(19, FixtureCatalog.RequiredFixtureIds.Distinct().ToArray());
        Assert.Contains("FX-ORACLE-FIREWALL", FixtureCatalog.RequiredFixtureIds);
    }
}
