using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class Er05AProfileAndAuthorizationTests
{
    [TestMethod]
    public void GAP01_T5Pilot_v2_package_loads_without_authorizing_execution()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package());
        Assert.AreEqual(ConfigurationProfileKind.T5Pilot, loaded.Manifest.ProfileKind);
        var report = new T5CandidatePreflight().Validate(loaded, null);
        Assert.IsTrue(report.RuntimeSchemaCompatible);
        Assert.IsTrue(report.Issues.Any(x => x.Code == "T5_AUTHORIZATION_MISSING"));
        Assert.IsFalse(report.ExecutionStarted);
    }

    [TestMethod]
    public void GAP01_fixture_profile_cannot_escalate_to_T5()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ConfigurationProfileKind.TestFixtureOnly, RunStage.T5));
        var issues = new T5AuthorizationValidator().Validate(loaded, null, DateTimeOffset.UtcNow);
        Assert.IsTrue(issues.Any(x => x.Code == "TEST_FIXTURE_PROFILE_ESCALATION"));
    }

    [TestMethod]
    public void GAP01_unknown_profile_fails_strict_deserialization()
    {
        var root = Er05ATestSupport.CreateV2Package();
        var path = Path.Combine(root, "config_set_manifest.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"ProfileKind\":\"T5Pilot\"", "\"ProfileKind\":\"UnknownPilot\"", StringComparison.Ordinal));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => new ConfigurationSetLoaderV2().Load(root));
    }

    [TestMethod]
    public void GAP01_v1_fixture_remains_loadable()
    {
        var loaded = new ConfigurationSetLoader().Load(Path.Combine(TestSupport.WorkspaceRoot, "configs", "fixtures"));
        Assert.AreEqual(ConfigurationProfileKind.TestFixtureOnly, loaded.Model.ProfileKind);
    }

    [TestMethod]
    public void GAP02_exact_approved_hash_bound_grant_passes_authorization_validation()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ownerApproved: true, resourcesApproved: true));
        var grant = Er05ATestSupport.ApprovedGrant(loaded);
        Assert.IsEmpty(new T5AuthorizationValidator().Validate(loaded, grant, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    [DataRow(AuthorizationGrantState.Draft, "T5_AUTHORIZATION_NOT_APPROVED")]
    [DataRow(AuthorizationGrantState.Revoked, "T5_AUTHORIZATION_NOT_APPROVED")]
    public void GAP02_nonapproved_grant_fails_closed(AuthorizationGrantState state, string expectedCode)
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ownerApproved: true, resourcesApproved: true));
        var grant = Er05ATestSupport.ApprovedGrant(loaded) with { State = state };
        Assert.IsTrue(new T5AuthorizationValidator().Validate(loaded, grant, DateTimeOffset.UtcNow).Any(x => x.Code == expectedCode));
    }

    [TestMethod]
    public void GAP02_wrong_config_hash_fails_closed()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ownerApproved: true, resourcesApproved: true));
        var grant = Er05ATestSupport.ApprovedGrant(loaded) with { ConfigSetSha256 = new string('B', 64) };
        Assert.IsTrue(new T5AuthorizationValidator().Validate(loaded, grant, DateTimeOffset.UtcNow).Any(x => x.Code == "T5_AUTHORIZATION_CONFIG_HASH_MISMATCH"));
    }

    [TestMethod]
    public void GAP02_wrong_experiment_allowlist_fails_closed()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ownerApproved: true, resourcesApproved: true));
        var grant = Er05ATestSupport.ApprovedGrant(loaded) with { Experiments = [new(new("T5-001"), loaded.ExperimentSet.Runs[0].EffectiveConfigSha256)] };
        Assert.IsTrue(new T5AuthorizationValidator().Validate(loaded, grant, DateTimeOffset.UtcNow).Any(x => x.Code == "T5_AUTHORIZATION_EXPERIMENT_ALLOWLIST_MISMATCH"));
    }

    [TestMethod]
    public void GAP02_expired_grant_fails_closed()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ownerApproved: true, resourcesApproved: true));
        var grant = Er05ATestSupport.ApprovedGrant(loaded) with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.IsTrue(new T5AuthorizationValidator().Validate(loaded, grant, DateTimeOffset.UtcNow).Any(x => x.Code == "T5_AUTHORIZATION_EXPIRED"));
    }

    [TestMethod]
    public void GAP02_T6_is_hard_blocked_even_with_syntactically_valid_grant()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package(ConfigurationProfileKind.T6Scientific, RunStage.T6, true, true));
        var grant = Er05ATestSupport.ApprovedGrant(loaded) with { Stage = RunStage.T6 };
        Assert.IsTrue(new T5AuthorizationValidator().Validate(loaded, grant, DateTimeOffset.UtcNow).Any(x => x.Code == "T6_HARD_BLOCK_ER05A"));
    }
}

[TestClass]
public sealed class Er05AModelAndMultiRowBindingTests
{
    [TestMethod]
    public void GAP03_config_binds_limit_belief_transition_observations_and_reset()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package());
        var bundle = new ScalarReferenceModelFactory().Bind(loaded.Model);
        Assert.AreEqual(0.5d, bundle.LimitState.Margin(0.2d), 1e-12);
        Assert.IsTrue(bundle.Belief.TryGetWeightedSupport(out var support));
        Assert.HasCount(2, support);
        Assert.AreEqual(ObservationKind.Routine, bundle.RoutineObservation.Kind);
        Assert.AreEqual(ObservationKind.IntensifiedMonitoring, bundle.MonitorObservation.Kind);
        Assert.IsNull(bundle.InspectionObservation);
        Assert.AreEqual("scalar-reference-state", bundle.Reset.StateSpace.Id);
    }

    [TestMethod]
    public void GAP03_artifact_hash_mismatch_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        model = model with { LimitState = model.LimitState with { Artifact = model.LimitState.Artifact with { Sha256 = new string('F', 64) } } };
        Assert.ThrowsExactly<InvalidDataException>(() => new ScalarReferenceModelFactory().Bind(model));
    }

    [TestMethod]
    public void GAP03_invalid_belief_probability_support_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        var belief = model.Belief with { Weights = [0.8, 0.8] };
        belief = belief with { Artifact = belief.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Belief(belief)) } };
        Assert.ThrowsExactly<InvalidDataException>(() => new ScalarReferenceModelFactory().Bind(model with { Belief = belief }));
    }

    [TestMethod]
    public void GAP03_state_space_mismatch_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        var reset = model.Reset with { StateSpaceId = "other-state-space" };
        reset = reset with { Artifact = reset.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Reset(reset)) } };
        Assert.ThrowsExactly<InvalidDataException>(() => new ScalarReferenceModelFactory().Bind(model with { Reset = reset }));
    }

    [TestMethod]
    public void GAP03_unknown_factory_id_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        Assert.ThrowsExactly<InvalidDataException>(() => new ScalarReferenceModelFactory().Bind(model with { LimitState = model.LimitState with { FactoryTypeId = "UnknownLimitState" } }));
    }

    [TestMethod]
    public void GAP03_invalid_observation_probability_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        var monitor = model.MonitorObservation with { ProbabilityOneHighState = 1.1 };
        monitor = monitor with { Artifact = monitor.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Observation(monitor)) } };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScalarReferenceModelFactory().Bind(model with { MonitorObservation = monitor }));
    }

    [TestMethod]
    public void GAP03_missing_binding_parameter_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        Assert.ThrowsExactly<InvalidDataException>(() => new ScalarReferenceModelFactory().Bind(model with { MonitorObservation = null! }));
    }

    [TestMethod]
    public void GAP03_exposure_clock_mismatch_fails_closed()
    {
        var model = Er05ATestSupport.Model(ConfigurationProfileKind.T5Pilot, RuntimeValueStatus.MethodologicalPilotNotApproved);
        var transition = model.Transition with { ExposureClockId = "other-clock" };
        transition = transition with { Artifact = transition.Artifact with { Sha256 = ArtifactPayloads.Hash(ArtifactPayloads.Transition(transition)) } };
        Assert.ThrowsExactly<InvalidDataException>(() => new ScalarReferenceModelFactory().Bind(model with { Transition = transition }));
    }

    [TestMethod]
    public void GAP05_zero_exposure_is_identity_and_nonzero_exposure_scales_increment()
    {
        var transition = new ExposureScaledUniformRateTransition(0.2, "v", new("s", "v", "u"), "clock");
        var zero = transition.SampleSegment(0.3, new(new(0), new(0)), new(MaintenanceActionKind.Continue, new(0), null), null, new FixedRandomStream(0.5));
        var one = transition.SampleSegment(0.3, new(new(0), new(1)), new(MaintenanceActionKind.Continue, new(1), null), null, new FixedRandomStream(0.5));
        var two = transition.SampleSegment(0.3, new(new(0), new(2)), new(MaintenanceActionKind.Continue, new(2), null), null, new FixedRandomStream(0.5));
        Assert.AreEqual(0.3, zero.Checkpoints[0].State, 1e-12);
        Assert.AreEqual(2d * (one.Checkpoints[0].State - 0.3), two.Checkpoints[0].State - 0.3, 1e-12);
    }

    [TestMethod]
    public void GAP05_negative_or_reversed_exposure_is_rejected()
    {
        var transition = new ExposureScaledUniformRateTransition(0.2, "v", new("s", "v", "u"), "clock");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => transition.SampleSegment(0.3, new(new(1), new(0)), new(MaintenanceActionKind.Continue, new(0), null), null, new FixedRandomStream(0.5)));
    }

    [TestMethod]
    public void GAP05_paused_inspection_does_not_apply_natural_increment()
    {
        var transition = new ExposureScaledUniformRateTransition(0.2, "v", new("s", "v", "u"), "clock");
        var paused = transition.SampleSegment(0.3, new(new(0), new(1)), new(MaintenanceActionKind.TargetedInspection, new(1), InspectionExposureMode.Paused), null, new FixedRandomStream(0.5));
        var continued = transition.SampleSegment(0.3, new(new(0), new(1)), new(MaintenanceActionKind.TargetedInspection, new(1), InspectionExposureMode.ContinuedOperation), null, new FixedRandomStream(0.5));
        Assert.AreEqual(0.3, paused.Checkpoints[0].State, 1e-12);
        Assert.IsGreaterThan(paused.Checkpoints[0].State, continued.Checkpoints[0].State);
    }

    [TestMethod]
    public void GAP06_two_effective_rows_have_distinct_hashes_and_output_roots()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package());
        Assert.HasCount(2, loaded.ExperimentSet.Runs);
        Assert.AreNotEqual(loaded.ExperimentSet.Runs[0].EffectiveConfigSha256, loaded.ExperimentSet.Runs[1].EffectiveConfigSha256);
        Assert.AreNotEqual(loaded.ExperimentSet.Runs[0].OutputSubroot, loaded.ExperimentSet.Runs[1].OutputSubroot);
        Assert.AreEqual("PILOT-NUMERICAL", loaded.EffectiveExperiments[new("T5-001")].ScenarioId.Value);
        Assert.AreEqual("PILOT-PRECISION", loaded.EffectiveExperiments[new("T5-002")].ScenarioId.Value);
    }

    [TestMethod]
    public void GAP06_effective_row_hash_mutation_is_detected()
    {
        var root = Er05ATestSupport.CreateV2Package();
        var path = Path.Combine(root, "T5-001_effective_config.json");
        File.AppendAllText(path, " ");
        Assert.ThrowsExactly<InvalidDataException>(() => new ConfigurationSetLoaderV2().Load(root));
    }

    [TestMethod]
    public void GAP08_v2_package_reconciles_runtime_fields_and_preflight_has_no_implementation_issue()
    {
        var loaded = new ConfigurationSetLoaderV2().Load(Er05ATestSupport.CreateV2Package());
        var report = new T5CandidatePreflight().Validate(loaded, null);
        Assert.IsTrue(report.RuntimeSchemaCompatible && report.ModelBindingCompatible && report.BaselineBindingCompatible && report.MultiRowBindingCompatible && report.PlanningHarnessCompatible);
        Assert.IsFalse(report.Issues.Any(x => x.Code.Contains("IMPLEMENTATION", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GAP08_unregistered_extra_file_fails_closed()
    {
        var root = Er05ATestSupport.CreateV2Package();
        File.WriteAllText(Path.Combine(root, "unregistered_scientific_config.json"), "{}");
        Assert.ThrowsExactly<InvalidDataException>(() => new ConfigurationSetLoaderV2().Load(root));
    }
}
