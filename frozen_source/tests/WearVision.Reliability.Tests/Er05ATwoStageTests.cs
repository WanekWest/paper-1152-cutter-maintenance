using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class Er05ATwoStageEvaluatorTests
{
    [TestMethod]
    public void TS01_uninformative_zero_burden_monitor_matches_no_information_value()
    {
        var fixture = CreateFixture(0.5, 0.5, MissingObservationMode.None, 0, 0, 0d);
        var result = fixture.Evaluator.Evaluate(fixture.Request, default);
        Assert.AreEqual(EvaluationStatus.Success, result.Status);
        Assert.AreEqual(0d, result.Evsi!.Evsi!.Value, 1e-12);
        Assert.IsTrue(result.Branches.All(x => x.SelectedTerminal!.Policy.FirstAction == MaintenanceActionKind.Continue));
    }

    [TestMethod]
    public void TS02_informative_monitor_changes_terminal_action_on_one_exact_branch()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d);
        var result = fixture.Evaluator.Evaluate(fixture.Request, default);
        Assert.HasCount(2, result.Branches);
        Assert.AreEqual(MaintenanceActionKind.Continue, result.Branches.Single(x => x.BranchId == "zero").SelectedTerminal!.Policy.FirstAction);
        Assert.AreEqual(MaintenanceActionKind.PlannedReplacement, result.Branches.Single(x => x.BranchId == "one").SelectedTerminal!.Policy.FirstAction);
        Assert.AreEqual(1d, result.Branches.Sum(x => x.Probability.Value), 1e-12);
    }

    [TestMethod]
    public void TS03_information_burden_is_charged_once_in_EVSI()
    {
        var zero = CreateFixture(0.5, 0.5, MissingObservationMode.None, 0, 0, 0d).Evaluate();
        var burdened = CreateFixture(0.5, 0.5, MissingObservationMode.None, 0, 0, 0.1d).Evaluate();
        Assert.AreEqual(-0.1d, burdened.Evsi!.Evsi!.Value, 1e-12);
        var zeroInformation = zero.CompletePolicies.Single(x => x.Policy.Id.Value == "P-INFO").Disutility.ExpectedDisutility!.Value.Value;
        var burdenedInformation = burdened.CompletePolicies.Single(x => x.Policy.Id.Value == "P-INFO").Disutility.ExpectedDisutility!.Value.Value;
        Assert.AreEqual(0.1d, burdenedInformation - zeroInformation, 1e-12);
    }

    [TestMethod]
    public void TS04_failure_during_information_delay_terminates_without_terminal_action()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d, delayFailure: 1d);
        var result = fixture.Evaluate();
        Assert.IsEmpty(result.Branches);
        Assert.IsTrue(result.CompletePolicies.Single(x => x.Policy.Id.Value == "P-INFO").Warnings.Contains("FAILURE_BEFORE_INFORMATION"));
        Assert.IsTrue(result.FailureBeforeObservationTerminatesElectiveDecision);
    }

    [TestMethod]
    public void TS05_branch_epsilon_remaining_rejects_low_J_continue()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d);
        var highBranch = fixture.Evaluate().Branches.Single(x => x.BranchId == "one");
        Assert.AreEqual(MaintenanceActionKind.PlannedReplacement, highBranch.SelectedTerminal!.Policy.FirstAction);
        Assert.AreEqual(FeasibilityClassification.ProvenFeasible, highBranch.BranchFeasibility.Classification);
        Assert.IsTrue(highBranch.TerminalDecision.Competitors.Any(x => x.Policy.FirstAction == MaintenanceActionKind.Continue && x.OverallFeasibility.Classification == FeasibilityClassification.ProvenInfeasible));
    }

    [TestMethod]
    public void TS06_noninformative_missing_outcome_is_explicit_and_does_not_impute()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.NonInformative, 0.2, 0.2, 0d);
        var result = fixture.Evaluate();
        var missing = result.Branches.Single(x => x.BranchId == "missing");
        Assert.AreEqual(0.2d, missing.Probability.Value, 1e-12);
        Assert.AreEqual(MaintenanceActionKind.Continue, missing.SelectedTerminal!.Policy.FirstAction);
        Assert.HasCount(3, result.Branches);
    }

    [TestMethod]
    public void TS06_informative_missingness_updates_branch_decision_from_registered_likelihood()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.Informative, 0.05, 0.8, 0d);
        var result = fixture.Evaluate();
        var missing = result.Branches.Single(x => x.BranchId == "missing");
        Assert.AreEqual(MaintenanceActionKind.PlannedReplacement, missing.SelectedTerminal!.Policy.FirstAction);
    }

    [TestMethod]
    public void TS07_two_point_reset_remains_nondegenerate()
    {
        var reset = new TwoPointScalarResetKernel(0.1, 0.2, 0.75, "reset.v2", new("s", "v", "u"));
        Assert.AreEqual(0.1, reset.SampleReset(new(new("old"), new("new"), new(0)), new FixedRandomStream(0.5)), 1e-12);
        Assert.AreEqual(0.2, reset.SampleReset(new(new("old"), new("new"), new(0)), new FixedRandomStream(0.9)), 1e-12);
    }

    [TestMethod]
    public void TS08_policy_failure_probability_aggregates_delay_and_exact_branches()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d, delayFailure: 0.2);
        var result = fixture.Evaluate();
        var policy = result.CompletePolicies.Single(x => x.Policy.Id.Value == "P-INFO");
        var conditional = result.Branches.Sum(x => x.Probability.Value * x.SelectedTerminal!.Reliability.FailureProbability!.Value.Value);
        Assert.AreEqual(0.2 + 0.8 * conditional, policy.Reliability.FailureProbability!.Value.Value, 1e-12);
    }

    [TestMethod]
    public void TS09_EVSI_uses_complete_constrained_information_policy()
    {
        var result = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0.02).Evaluate();
        var direct = result.CompletePolicies.Where(x => x.Policy is DirectPolicy).Min(x => x.Disutility.ExpectedDisutility!.Value.Value);
        var information = result.CompletePolicies.Single(x => x.Policy.Id.Value == "P-INFO").Disutility.ExpectedDisutility!.Value.Value;
        Assert.AreEqual(direct - information, result.Evsi!.Evsi!.Value, 1e-12);
    }

    [TestMethod]
    public void TS10_policy_enumeration_order_does_not_change_result()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d);
        var first = fixture.Evaluate();
        var reversedRequest = fixture.Request with { Policies = fixture.Request.Policies.Reverse().ToArray() };
        var second = fixture.Evaluator.Evaluate(reversedRequest, default);
        Assert.AreEqual(first.Decision.Selected!.Policy.Id, second.Decision.Selected!.Policy.Id);
        CollectionAssert.AreEqual(first.Branches.Select(x => x.BranchId).ToArray(), second.Branches.Select(x => x.BranchId).ToArray());
    }

    [TestMethod]
    public void GAP05_monitor_duration_reaches_physical_delay_propagation()
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d, informationAction: MaintenanceActionKind.IntensifiedMonitoring);
        fixture.Evaluate();
        Assert.AreEqual(0.2d, fixture.Delay.LastRequest!.ActionDuration.Value, 1e-12);
        Assert.AreEqual(0.2d, fixture.Delay.LastRequest.PhysicalExposure.Value, 1e-12);
    }

    [TestMethod]
    [DataRow(InspectionExposureMode.Paused, 0d)]
    [DataRow(InspectionExposureMode.ContinuedOperation, 0.2d)]
    public void GAP05_inspection_mode_controls_physical_exposure_without_reset(InspectionExposureMode mode, double expectedPhysicalExposure)
    {
        var fixture = CreateFixture(0.25, 0.75, MissingObservationMode.None, 0, 0, 0d, informationAction: MaintenanceActionKind.TargetedInspection, inspectionMode: mode);
        fixture.Evaluate();
        Assert.AreEqual(expectedPhysicalExposure, fixture.Delay.LastRequest!.PhysicalExposure.Value, 1e-12);
        Assert.AreEqual(MaintenanceActionKind.TargetedInspection, fixture.Delay.LastRequest.InformationAction);
    }

    private static TwoStageFixture CreateFixture(
        double probabilityOneLow,
        double probabilityOneHigh,
        MissingObservationMode missingMode,
        double missingLow,
        double missingHigh,
        double burden,
        double delayFailure = 0d,
        MaintenanceActionKind informationAction = MaintenanceActionKind.IntensifiedMonitoring,
        InspectionExposureMode inspectionMode = InspectionExposureMode.Paused)
    {
        var stateSpace = new StateSpaceDescriptor("two-stage-fixture", "v1", "TEST_FIXTURE_ONLY");
        var belief = new ImmutableWeightedSampleBelief<double>(new("posterior.ts"), stateSpace, ReferenceArtifactHasher.HashText("ts-belief"), [new(0.2, 0.75), new(0.8, 0.25)]);
        var kind = informationAction == MaintenanceActionKind.IntensifiedMonitoring ? ObservationKind.IntensifiedMonitoring : ObservationKind.TargetedInspection;
        var kernel = new StateConditionedBinaryObservationKernel("observation.ts", kind, stateSpace, 0.5, probabilityOneLow, probabilityOneHigh, missingMode, missingLow, missingHigh);
        var updater = new WeightedSupportBayesUpdater<double, DiscreteObservation>(support => ReferenceArtifactHasher.HashText(string.Join(';', support.Select(x => $"{x.State:R}:{x.Weight:R}"))));
        var delay = new FixedDelayPropagator(delayFailure >= 1d ? null : belief, delayFailure);
        var duration = new Exposure(0.2);
        var policies = new PolicyTree[]
        {
            new DirectPolicy(new("P-CONTINUE"), new(1), MaintenanceActionKind.Continue),
            new DirectPolicy(new("P-REPLACE"), new(1), MaintenanceActionKind.PlannedReplacement),
            new InformationPolicy<DiscreteObservation>(new("P-INFO"), new(1), informationAction, duration, new DiscreteTerminalRule())
        };
        var enabled = new[] { MaintenanceActionKind.Continue, MaintenanceActionKind.PlannedReplacement, informationAction }.ToHashSet();
        var available = new[] { kind }.ToHashSet();
        var durations = new Dictionary<MaintenanceActionKind, Exposure>
        {
            [MaintenanceActionKind.Continue] = new(0),
            [MaintenanceActionKind.PlannedReplacement] = new(0.1),
            [informationAction] = duration
        };
        var admissibility = new ActionAdmissibilityContext(new(1), enabled, available, durations, true, true, true, informationAction == MaintenanceActionKind.TargetedInspection ? inspectionMode : null);
        var evaluator = new TwoStagePolicyEvaluator<double, DiscreteObservation>(new ActionAdmissibilityEvaluator(), new ReliabilityFeasibilityEvaluator(), new ConstrainedPolicySelector(), new EvsiEvaluator());
        var request = new TwoStagePolicyEvaluationRequest<double, DiscreteObservation>(
            policies, belief, admissibility, delay, new StateConditionedBinaryBranchProvider(kernel), kernel, updater,
            new DelegateTerminalEvaluator(Er05ATestSupport.TerminalEvaluation), TestSupport.FixtureWeights(), new("fixture-quality", false, null),
            new(0.9), new(0.4), new(burden), new(ReferenceArtifactHasher.HashText("ts-config").Sha256Hex));
        return new(evaluator, request, delay);
    }

    private sealed record TwoStageFixture(TwoStagePolicyEvaluator<double, DiscreteObservation> Evaluator, TwoStagePolicyEvaluationRequest<double, DiscreteObservation> Request, FixedDelayPropagator Delay)
    {
        public TwoStageEvaluationResult<DiscreteObservation> Evaluate() => Evaluator.Evaluate(Request, default);
    }

    private sealed class DiscreteTerminalRule : IConstrainedTerminalRule<DiscreteObservation>
    {
        public MaintenanceActionKind Select(DiscreteObservation observation) => observation.BinaryValue == 1 ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue;
    }
}
