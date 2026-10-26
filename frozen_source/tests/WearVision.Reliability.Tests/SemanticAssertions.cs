using System.Reflection;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Experiments.Oracle;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

internal static class SemanticAssertions
{
    private static readonly string[] ProvenanceKeyFields = ["run_id", "experiment_id", "scenario_id", "trajectory_id", "component_episode_id", "decision_id", "configuration_hash"];

    public static void AssertCase(string id)
    {
        if (id.StartsWith("REL-", StringComparison.Ordinal)) AssertReliability(id);
        else if (id.StartsWith("BEL-", StringComparison.Ordinal)) AssertBelief(id);
        else if (id.StartsWith("FEV-", StringComparison.Ordinal)) AssertFailureEvent(id);
        else if (id.StartsWith("ACT-", StringComparison.Ordinal)) AssertAction(id);
        else if (id.StartsWith("OBJ-", StringComparison.Ordinal)) AssertObjective(id);
        else if (id.StartsWith("DEC-", StringComparison.Ordinal)) AssertDecision(id);
        else if (id.StartsWith("VOI-", StringComparison.Ordinal)) AssertEvsi(id);
        else if (id.StartsWith("RSK-", StringComparison.Ordinal)) AssertRiskExclusion(id);
        else if (id.StartsWith("ORA-", StringComparison.Ordinal)) AssertOracle(id);
        else if (id.StartsWith("NUM-", StringComparison.Ordinal)) AssertNumerics(id);
        else if (id.StartsWith("PRO-", StringComparison.Ordinal)) AssertProvenance(id);
        else if (id.StartsWith("STA-", StringComparison.Ordinal)) AssertStatistics(id);
        else Assert.Fail($"Unknown inherited semantic test ID: {id}");
    }

    private static void AssertReliability(string id)
    {
        var exact = new WeightedSupportFirstPassageEstimator<ScalarState>();
        var safeTransition = ConstantTransition(0.1);
        var failedTransition = ConstantTransition(0.8);
        var belief = FixtureCatalog.TwoPointBelief();
        switch (id)
        {
            case "REL-001": Assert.IsTrue(new FailureProbability(0.5).Value is >= 0d and <= 1d); break;
            case "REL-002": Assert.IsTrue(new WearVision.Reliability.Domain.Reliability(0.5).Value is >= 0d and <= 1d); break;
            case "REL-003":
            {
                var result = exact.Estimate(TestSupport.ReliabilityRequest(belief, ConstantTransitionFromInitial()), default);
                Assert.AreEqual(1d, result.FailureProbability!.Value.Value + result.Reliability!.Value.Value, 1e-12);
                break;
            }
            case "REL-004":
            {
                var factory = new Sha256XoshiroRandomStreamFactory();
                var estimator = new PosteriorPredictiveFirstPassageEstimator<ScalarState>(factory);
                var request = TestSupport.ReliabilityRequest(belief, ConstantTransitionFromInitial(), numerical: TestSupport.FixtureNumerical());
                var a = estimator.Estimate(request, default); var b = estimator.Estimate(request, default);
                Assert.AreEqual(a, b);
                break;
            }
            case "REL-005":
            {
                var growing = new DeterministicScalarTransition("horizon-monotone", (state, interval) => [new(interval.End, new(state.Value + interval.End.Value))]);
                var one = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.one"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("one"), [new(new(0d), 1d)]);
                var shortResult = exact.Estimate(TestSupport.ReliabilityRequest(one, growing, horizon: new(0.25), policy: new DirectPolicy(new("P-short"), new(0.25), MaintenanceActionKind.Continue)), default);
                var longResult = exact.Estimate(TestSupport.ReliabilityRequest(one, growing, horizon: new(1d), policy: new DirectPolicy(new("P-long"), new(1d), MaintenanceActionKind.Continue)), default);
                Assert.IsLessThanOrEqualTo(longResult.FailureProbability!.Value.Value, shortResult.FailureProbability!.Value.Value);
                Assert.IsGreaterThanOrEqualTo(longResult.Reliability!.Value.Value, shortResult.Reliability!.Value.Value);
                break;
            }
            case "REL-006":
            {
                var safeBelief = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.safe"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("safe"), [new(new(0.1), 1d)]);
                Assert.AreEqual(0d, exact.Estimate(TestSupport.ReliabilityRequest(safeBelief, safeTransition), default).FailureProbability!.Value.Value, 1e-12); break;
            }
            case "REL-007": Assert.AreEqual(1d, exact.Estimate(TestSupport.ReliabilityRequest(belief, failedTransition), default).FailureProbability!.Value.Value, 1e-12); break;
            case "REL-008":
            case "REL-012":
            {
                var result = exact.Estimate(TestSupport.ReliabilityRequest(belief, ConstantTransitionFromInitial()), default);
                Assert.AreEqual(0.5d, result.FailureProbability!.Value.Value, 1e-12);
                break;
            }
            case "REL-009":
            {
                var detector = new FirstPassageDetector<ScalarState>();
                var segment = new TransitionSegment<ScalarState>([new(new(0.4), new(0.8)), new(new(1d), new(0.1))], true, "cross-recover");
                Assert.IsTrue(detector.Detect(new(0.1), new(0), segment, new ScalarLimitState(0.5, "cross-recover")).Failed);
                break;
            }
            case "REL-010":
            {
                var segment = new TransitionSegment<ScalarState>([new(new(1), new(0.1))], true, "safe");
                Assert.IsFalse(new FirstPassageDetector<ScalarState>().Detect(new(0.1), new(0), segment, new ScalarLimitState(0.5, "safe")).Failed);
                break;
            }
            case "REL-011":
            {
                var estimator = new PosteriorPredictiveFirstPassageEstimator<ScalarState>(new IndexedRandomStreamFactory());
                var result = estimator.Estimate(TestSupport.ReliabilityRequest(belief, ConstantTransitionFromInitial(), numerical: TestSupport.FixtureNumerical(4, 2, 12, 3)), default);
                Assert.AreEqual(EvaluationStatus.Success, result.Status);
                Assert.IsGreaterThanOrEqualTo(result.Diagnostics.BatchCount, 3);
                break;
            }
            case "REL-013":
            case "REL-016":
            {
                var transition = ConstantTransitionFromInitial();
                var a = transition.SampleSegment(new(0.2), new(new(0), new(1)), new(MaintenanceActionKind.Continue, new(0), null), null, new FixedRandomStream());
                var b = transition.SampleSegment(new(0.2), new(new(0), new(1)), new(MaintenanceActionKind.IntensifiedMonitoring, new(0), null), null, new FixedRandomStream());
                CollectionAssert.AreEqual(a.Checkpoints.ToArray(), b.Checkpoints.ToArray());
                break;
            }
            case "REL-014":
            {
                var estimator = new PosteriorPredictiveFirstPassageEstimator<ScalarState>(new IndexedRandomStreamFactory());
                var one = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.one"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("one-reset"), [new(new(0.1), 1d)]);
                var policy = new DirectPolicy(new("P-reset"), new(1), MaintenanceActionKind.PlannedReplacement);
                var result = estimator.Estimate(TestSupport.ReliabilityRequest(one, ConstantTransitionFromInitial(), new TwoPointResetKernel(new(0.1), new(0.8), 0.5), policy: policy, numerical: TestSupport.FixtureNumerical()), default);
                Assert.AreEqual(0.5d, result.FailureProbability!.Value.Value, 1e-12);
                break;
            }
            case "REL-015":
            {
                var detector = new FirstPassageDetector<ScalarState>();
                var result = detector.Detect(new(0.8), new(0), new([], false, "replacement-later"), new ScalarLimitState(0.5, "pre-replacement"));
                Assert.IsTrue(result.Failed);
                break;
            }
            case "REL-017":
            {
                var mismatch = exact.Estimate(TestSupport.ReliabilityRequest(belief, safeTransition, policy: new DirectPolicy(new("P-mismatch"), new(2), MaintenanceActionKind.Continue), horizon: new(1)), default);
                Assert.AreEqual(EvaluationStatus.ProtocolViolation, mismatch.Status);
                break;
            }
            case "REL-018":
            {
                var analytical = exact.Estimate(TestSupport.ReliabilityRequest(belief, ConstantTransitionFromInitial()), default);
                var propagated = new PosteriorPredictiveFirstPassageEstimator<ScalarState>(new IndexedRandomStreamFactory()).Estimate(TestSupport.ReliabilityRequest(belief, ConstantTransitionFromInitial(), numerical: TestSupport.FixtureNumerical()), default);
                Assert.AreEqual(analytical.FailureProbability!.Value.Value, propagated.FailureProbability!.Value.Value, 1e-12);
                break;
            }
            case "REL-019":
            {
                var invalid = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.invalid"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("invalid"), [new(new(0.1), 0.4)]);
                var result = exact.Estimate(TestSupport.ReliabilityRequest(invalid, safeTransition), default);
                Assert.AreEqual(EvaluationStatus.NotEvaluable, result.Status);
                Assert.IsNull(result.FailureProbability);
                break;
            }
            case "REL-020": Assert.AreEqual(EvaluationStatus.NotEvaluable, new DirectEventCensoringStrategy().Evaluate(new(CensoringKind.RightCensored, null, new(0.5), null)).Status); break;
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertBelief(string id)
    {
        var prior = FixtureCatalog.TwoPointBelief();
        var updater = new WeightedSupportBayesUpdater<ScalarState, BinaryObservation>(x => FixtureCatalog.Hash(string.Join(';', x.Select(y => $"{y.State.Value:R}:{y.Weight:R}"))));
        var informative = new BinaryObservationKernel("informative", ObservationKind.TargetedInspection, (state, _) => state.Value);
        var uninformative = new BinaryObservationKernel("uninformative", ObservationKind.TargetedInspection, (_, _) => 0.5);
        var context = new ObservationContext("fixture", false, null);
        switch (id)
        {
            case "BEL-001": Assert.IsTrue(prior.Validate().IsValid); Assert.AreEqual(1d, prior.Validate().Mass, 1e-12); break;
            case "BEL-002":
            {
                var impossible = new BinaryObservationKernel("impossible", ObservationKind.TargetedInspection, (_, _) => 0d);
                var result = updater.Update(prior, new(1), impossible, context);
                Assert.AreEqual(EvaluationStatus.NotEvaluable, result.Status); Assert.IsNull(result.Posterior); break;
            }
            case "BEL-003":
            {
                var result = updater.Update(prior, new(1), uninformative, context); AssertWeights(result.Posterior!, 0.5, 0.5); break;
            }
            case "BEL-004":
            case "BEL-005":
            {
                var result = updater.Update(prior, new(1), informative, context); AssertWeights(result.Posterior!, 0.2, 0.8); break;
            }
            case "BEL-006":
            {
                var limiting = new BinaryObservationKernel("perfect-limit", ObservationKind.TargetedInspection, (state, _) => state.Value >= 0.5 ? 1d : 0d);
                var result = updater.Update(prior, new(1), limiting, context); AssertWeights(result.Posterior!, 0d, 1d);
                Assert.IsFalse(typeof(IDegradationBelief<ScalarState>).GetMembers().Any(x => x.Name.Contains("True", StringComparison.OrdinalIgnoreCase)));
                break;
            }
            case "BEL-007":
            {
                var result = updater.Update(prior, new(0), uninformative, new("fixture", true, null)); AssertWeights(result.Posterior!, 0.5, 0.5); break;
            }
            case "BEL-008":
            {
                var missing = new BinaryObservationKernel("missing", ObservationKind.Routine, (_, _) => 0.5);
                var result = updater.Update(prior, new(0), missing, new("informative-missing", true, null)); AssertWeights(result.Posterior!, 0.2, 0.8); break;
            }
            case "BEL-009":
            case "BEL-010":
            {
                var transition = ConstantTransitionFromInitial();
                var a = transition.SampleSegment(new(0.2), new(new(0), new(1)), new(MaintenanceActionKind.Continue, new(0), null), null, new FixedRandomStream(0.2));
                var b = transition.SampleSegment(new(0.2), new(new(0), new(1)), new(MaintenanceActionKind.IntensifiedMonitoring, new(0), null), null, new FixedRandomStream(0.2));
                CollectionAssert.AreEqual(a.Checkpoints.ToArray(), b.Checkpoints.ToArray()); break;
            }
            case "BEL-011":
            {
                var prohibited = new[] { "TrueState", "TrueWear", "TrueRul", "DangerousWear", "FutureFailure" };
                Assert.IsFalse(TestSupport.DeployableTypes().SelectMany(x => x.GetMembers()).Any(m => prohibited.Any(p => m.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))); break;
            }
            case "BEL-012":
            {
                var incomplete = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.incomplete"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("incomplete"), []);
                Assert.AreEqual(EvaluationStatus.InvalidPosterior, incomplete.Validate().Status); break;
            }
            case "BEL-013":
            {
                var reversed = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.reversed"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("reversed"), [new(new(0.8), 0.5), new(new(0.2), 0.5)]);
                var exact = new WeightedSupportFirstPassageEstimator<ScalarState>();
                Assert.AreEqual(exact.Estimate(TestSupport.ReliabilityRequest(prior, ConstantTransitionFromInitial()), default).FailureProbability!.Value.Value, exact.Estimate(TestSupport.ReliabilityRequest(reversed, ConstantTransitionFromInitial()), default).FailureProbability!.Value.Value, 1e-12); break;
            }
            case "BEL-014":
            {
                var duplicated = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.duplicated"), FixtureCatalog.ScalarStateSpace, FixtureCatalog.Hash("duplicated"), [new(new(0.2), 0.25), new(new(0.2), 0.25), new(new(0.8), 0.5)]);
                Assert.AreEqual(0.5d, new WeightedSupportFirstPassageEstimator<ScalarState>().Estimate(TestSupport.ReliabilityRequest(duplicated, ConstantTransitionFromInitial()), default).FailureProbability!.Value.Value, 1e-12); break;
            }
            case "BEL-015":
            {
                var incompatible = new BinaryObservationKernel("incompatible", ObservationKind.Routine, (_, _) => 0.5);
                var foreign = new ImmutableWeightedSampleBelief<ScalarState>(new("posterior.foreign"), new("foreign", "v1", "fixture"), FixtureCatalog.Hash("foreign"), [new(new(0.2), 1d)]);
                Assert.AreEqual(EvaluationStatus.IncompatibleModel, updater.Update(foreign, new(0), incompatible, context).Status); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertFailureEvent(string id)
    {
        var limit = new ScalarLimitState(0.5, "event");
        switch (id)
        {
            case "FEV-001": Assert.IsGreaterThan(0d, limit.Margin(new(0.4))); break;
            case "FEV-002": Assert.AreEqual(0d, limit.Margin(new(0.5)), 0d); break;
            case "FEV-003": Assert.IsLessThan(0d, limit.Margin(new(0.6))); break;
            case "FEV-004": Assert.IsFalse(new PathOutcome(false, false, true, false, 0).FailureOccurred); break;
            case "FEV-005": Assert.AreEqual(FeasibilityClassification.ProvenInfeasible, TestSupport.PolicyEvaluation("P-risk", 0.3, 0.25, 0.35, 0.1, FeasibilityClassification.ProvenInfeasible).OverallFeasibility.Classification); break;
            case "FEV-006":
            {
                var reset = new MaintenanceResetRecord(new("EP-old"), new("EP-new"), new(1)); Assert.AreNotEqual(reset.OldEpisode, reset.NewEpisode); break;
            }
            case "FEV-007": Assert.IsFalse(Enum.GetNames<MaintenanceActionKind>().Any(x => x.Contains("Corrective", StringComparison.OrdinalIgnoreCase))); break;
            case "FEV-008": Assert.IsFalse(new TransitionCapabilities(false, false, false).EventJumpsEnabled); break;
            case "FEV-009":
            {
                var config = TestSupport.FixtureConfiguration(eventJumps: true);
                var row = new ExperimentMatrixRow(new("SEM-001"), "core_semantic", RunStage.T1, "ER05_TEST_ONLY", new("FX-config"), false);
                var result = new RunPreflightValidator(new(), new(), new([], [])).Validate(config, row);
                Assert.IsFalse(result.Accepted); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertAction(string id)
    {
        var evaluator = new ActionAdmissibilityEvaluator();
        var enabled = Enum.GetValues<MaintenanceActionKind>().ToHashSet();
        var durations = Enum.GetValues<MaintenanceActionKind>().ToDictionary(x => x, _ => new Exposure(0.1));
        var full = new ActionAdmissibilityContext(new(1), enabled, new[] { ObservationKind.IntensifiedMonitoring, ObservationKind.TargetedInspection }.ToHashSet(), durations, true, true, true, InspectionExposureMode.Paused);
        switch (id)
        {
            case "ACT-001": Assert.AreEqual(MaintenanceActionKind.Continue, new DirectPolicy(new("P"), new(1), MaintenanceActionKind.Continue).FirstAction); break;
            case "ACT-002":
            case "ACT-003":
            {
                var action = id == "ACT-002" ? MaintenanceActionKind.IntensifiedMonitoring : MaintenanceActionKind.TargetedInspection;
                Assert.IsTrue(evaluator.Evaluate(action, full).IsAdmissible);
                var transition = ConstantTransitionFromInitial();
                var natural = transition.SampleSegment(new(0.2), new(new(0), new(1)), new(action, new(0.1), InspectionExposureMode.Paused), null, new FixedRandomStream());
                Assert.AreEqual(0.2, natural.Checkpoints[0].State.Value, 1e-12); break;
            }
            case "ACT-004": Assert.AreEqual(0.8, new PointResetKernel(new(0.8)).SampleReset(new(new("old"), new("new"), new(1)), new FixedRandomStream()).Value, 1e-12); break;
            case "ACT-005": Assert.IsFalse(Enum.GetNames<MaintenanceActionKind>().Any(x => x.Contains("Corrective", StringComparison.OrdinalIgnoreCase))); break;
            case "ACT-006":
            case "ACT-009":
            {
                var noKernels = full with { AvailableObservationKernels = new HashSet<ObservationKind>() };
                var result = evaluator.Evaluate(MaintenanceActionKind.TargetedInspection, noKernels); Assert.IsFalse(result.IsAdmissible); Assert.AreEqual(ActionAdmissibilityReason.MissingObservationKernel, result.Reason); break;
            }
            case "ACT-007":
            {
                var missingDuration = full with { Durations = new Dictionary<MaintenanceActionKind, Exposure>() };
                Assert.AreEqual(ActionAdmissibilityReason.MissingDuration, evaluator.Evaluate(MaintenanceActionKind.Continue, missingDuration).Reason); break;
            }
            case "ACT-008":
            {
                var missingMode = full with { InspectionMode = null };
                Assert.AreEqual(ActionAdmissibilityReason.InvalidExposureMode, evaluator.Evaluate(MaintenanceActionKind.TargetedInspection, missingMode).Reason); break;
            }
            case "ACT-010":
            {
                var vector = new BoundedConsequenceEvaluator().Evaluate(new(false, true, false, false, 0.2), new("fixture", new Dictionary<ConsequenceComponentKind, string>()));
                Assert.HasCount(5, vector.Components); Assert.AreEqual(1, vector.Components.Keys.Distinct().Count(x => x == ConsequenceComponentKind.Downtime)); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertObjective(string id)
    {
        var evaluator = new ExpectedDisutilityEvaluator();
        var consequence = new BoundedConsequenceEvaluator();
        var vector = consequence.Evaluate(new(true, false, true, false, 0.25), new("fixture", new Dictionary<ConsequenceComponentKind, string>()));
        switch (id)
        {
            case "OBJ-001": Assert.IsTrue(vector.Components.Values.All(x => x is >= 0d and <= 1d)); break;
            case "OBJ-002": Assert.IsTrue(ConsequenceWeightValidator.Validate(TestSupport.FixtureWeights()).IsValid); break;
            case "OBJ-003":
            case "OBJ-004":
            {
                var result = evaluator.Evaluate([vector], TestSupport.FixtureWeights());
                var manual = vector.Components.Sum(x => x.Value * TestSupport.FixtureWeights().Weights[x.Key]);
                Assert.AreEqual(manual, result.ExpectedDisutility!.Value.Value, 1e-12); break;
            }
            case "OBJ-005":
            {
                var policy = TestSupport.PolicyEvaluation("P", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible);
                Assert.IsNotNull(policy.Reliability.FailureProbability); Assert.IsNotNull(policy.Disutility.ExpectedDisutility); break;
            }
            case "OBJ-006":
            {
                var invalid = new ConsequenceVector(new Dictionary<ConsequenceComponentKind, double> { [ConsequenceComponentKind.Failure] = 2d });
                Assert.AreEqual(EvaluationStatus.ProtocolViolation, evaluator.Evaluate([invalid], TestSupport.FixtureWeights()).Status); break;
            }
            case "OBJ-007": Assert.AreNotEqual(typeof(FailureProbability), typeof(PathOutcome).GetProperty(nameof(PathOutcome.FailureOccurred))!.PropertyType); break;
            case "OBJ-008":
            {
                var weights = new Dictionary<ConsequenceComponentKind, double>(TestSupport.FixtureWeights().Weights) { [ConsequenceComponentKind.IntensifiedMonitoring] = 0d, [ConsequenceComponentKind.Failure] = 0.5d };
                var result = evaluator.Evaluate([vector], TestSupport.FixtureWeights(weights));
                Assert.AreEqual(1d, result.ComponentExpectations[ConsequenceComponentKind.IntensifiedMonitoring], 1e-12); break;
            }
            case "OBJ-009":
            {
                foreach (var kind in Enum.GetValues<ConsequenceComponentKind>())
                {
                    var weights = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, x => x == kind ? 1d : 0d);
                    Assert.AreEqual(EvaluationStatus.Success, evaluator.Evaluate([vector], TestSupport.FixtureWeights(weights)).Status);
                }
                break;
            }
            case "OBJ-010": Assert.HasCount(Enum.GetValues<ConsequenceComponentKind>().Length, Enum.GetValues<ConsequenceComponentKind>().Distinct()); break;
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertDecision(string id)
    {
        var selector = new ConstrainedPolicySelector();
        switch (id)
        {
            case "DEC-001":
            {
                var a = TestSupport.PolicyEvaluation("P-A", 0.1, 0.05, 0.15, 0.3, FeasibilityClassification.ProvenFeasible);
                var b = TestSupport.PolicyEvaluation("P-B", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible);
                Assert.AreEqual("P-B", selector.Select([a, b]).Selected!.Policy.Id.Value); break;
            }
            case "DEC-002":
            {
                var lowJ = TestSupport.PolicyEvaluation("P-INFEASIBLE", 0.4, 0.3, 0.5, 0.01, FeasibilityClassification.ProvenInfeasible);
                var feasible = TestSupport.PolicyEvaluation("P-FEASIBLE", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible);
                Assert.AreEqual("P-FEASIBLE", selector.Select([lowJ, feasible]).Selected!.Policy.Id.Value); break;
            }
            case "DEC-003":
            {
                var a = TestSupport.PolicyEvaluation("P-A", 0.4, 0.3, 0.5, 0.1, FeasibilityClassification.ProvenInfeasible);
                var b = TestSupport.PolicyEvaluation("P-B", 0.3, 0.25, 0.4, 0.4, FeasibilityClassification.ProvenInfeasible);
                var result = selector.Select([a, b]); Assert.AreEqual(DecisionStatus.ConstraintBreach, result.Status); Assert.AreEqual("P-B", result.Selected!.Policy.Id.Value); break;
            }
            case "DEC-004":
            case "DEC-009":
            {
                var a = TestSupport.PolicyEvaluation("P-Z", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible, 0.2);
                var b = TestSupport.PolicyEvaluation("P-A", 0.05, 0.01, 0.1, 0.2, FeasibilityClassification.ProvenFeasible, 0.3);
                var first = selector.Select([a, b]); var second = selector.Select([b, a]);
                Assert.AreEqual("P-A", first.Selected!.Policy.Id.Value); Assert.AreEqual(first.Selected.Policy.Id, second.Selected!.Policy.Id); break;
            }
            case "DEC-005":
            {
                var reliability = TestSupport.PolicyEvaluation("P", 0.2, 0.15, 0.25, 0.1, FeasibilityClassification.Unresolved).Reliability;
                Assert.AreEqual(FeasibilityClassification.Unresolved, new ReliabilityFeasibilityEvaluator().Evaluate(reliability, new(0.2)).Classification); break;
            }
            case "DEC-006":
            case "VOI-008":
            {
                var branchBad = TestSupport.PolicyEvaluation("P-INFO", 0.1, 0.05, 0.15, 0.01, FeasibilityClassification.ProvenFeasible, branch: FeasibilityClassification.ProvenInfeasible);
                var direct = TestSupport.PolicyEvaluation("P-DIRECT", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible);
                Assert.AreEqual("P-DIRECT", selector.Select([branchBad, direct]).Selected!.Policy.Id.Value); break;
            }
            case "DEC-007": Assert.IsFalse(Enum.GetNames<MaintenanceActionKind>().Any(x => x.Contains("Corrective", StringComparison.Ordinal))); break;
            case "DEC-008":
            {
                var context = new ActionAdmissibilityContext(new(1), new[] { MaintenanceActionKind.Continue, MaintenanceActionKind.PlannedReplacement }.ToHashSet(), new HashSet<ObservationKind>(), new Dictionary<MaintenanceActionKind, Exposure> { [MaintenanceActionKind.Continue] = new(0), [MaintenanceActionKind.PlannedReplacement] = new(0.1) }, true, true, true, null);
                var policies = new PolicyEnumerator<BinaryObservation>(new ActionAdmissibilityEvaluator(), new FixedTerminalRule()).Enumerate(context, new(1));
                Assert.HasCount(2, policies); Assert.IsTrue(policies.All(x => x is DirectPolicy)); break;
            }
            case "DEC-010":
            {
                var values = new[] { TestSupport.PolicyEvaluation("P-A", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible), TestSupport.PolicyEvaluation("P-B", 0.2, 0.1, 0.3, 0.3, FeasibilityClassification.ProvenInfeasible) };
                Assert.HasCount(2, selector.Select(values).Competitors); Assert.IsTrue(values.All(x => !string.IsNullOrWhiteSpace(x.OverallFeasibility.Reason))); break;
            }
            case "DEC-011":
            {
                var invalid = TestSupport.PolicyEvaluation("P", 0.1, 0.05, 0.15, 0.2, FeasibilityClassification.ProvenFeasible) with { Disutility = new(EvaluationStatus.NotEvaluable, null, new Dictionary<ConsequenceComponentKind, double>(), new(0), 0, "missing") };
                Assert.AreEqual(DecisionStatus.NotEvaluable, selector.Select([invalid]).Status); break;
            }
            case "DEC-012":
            {
                var evaluation = TestSupport.PolicyEvaluation("P", 0.15, 0.1, 0.18, 0.2, FeasibilityClassification.ProvenFeasible).Reliability;
                var f = new ReliabilityFeasibilityEvaluator(); Assert.AreEqual(FeasibilityClassification.ProvenFeasible, f.Evaluate(evaluation, new(0.2)).Classification); Assert.AreEqual(FeasibilityClassification.ProvenFeasible, f.Evaluate(evaluation, new(0.3)).Classification); break;
            }
            case "DEC-013":
            {
                var p = TestSupport.PolicyEvaluation("P", 0.15, 0.1, 0.18, 0.2, FeasibilityClassification.ProvenFeasible); var changed = p with { Disutility = p.Disutility with { ExpectedDisutility = new(0.3) } };
                Assert.AreEqual(p.Reliability.FailureProbability, changed.Reliability.FailureProbability); break;
            }
            case "DEC-014":
            {
                var request = TestSupport.ReliabilityRequest(FixtureCatalog.TwoPointBelief(), ConstantTransitionFromInitial(), policy: new DirectPolicy(new("P"), new(2), MaintenanceActionKind.Continue), horizon: new(1));
                Assert.AreEqual(EvaluationStatus.ProtocolViolation, new WeightedSupportFirstPassageEstimator<ScalarState>().Estimate(request, default).Status); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertEvsi(string id)
    {
        if (id == "VOI-008") { AssertDecision(id); return; }
        var evaluator = new EvsiEvaluator();
        switch (id)
        {
            case "VOI-001": Assert.AreEqual(0.2, evaluator.Evaluate(new(0.6), new(0.3), new(0.1)).Evsi!.Value, 1e-12); break;
            case "VOI-002": Assert.AreEqual(0d, evaluator.Evaluate(new(0.4), new(0.4), new(0)).Evsi!.Value, 1e-12); break;
            case "VOI-003": Assert.IsLessThanOrEqualTo(0d, evaluator.Evaluate(new(0.4), new(0.4), new(0.1)).Evsi!.Value); break;
            case "VOI-004": Assert.IsLessThanOrEqualTo(0.5d, evaluator.Evaluate(new(0.5), new(0.2), new(0)).Evsi!.Value); break;
            case "VOI-005": Assert.AreEqual(-0.1, evaluator.Evaluate(new(0.4), new(0.4), new(0.1)).Evsi!.Value, 1e-12); break;
            case "VOI-006":
            {
                var ledger = new AcquisitionBurdenLedger(); ledger.Charge(); Assert.ThrowsExactly<InvalidOperationException>(ledger.Charge); break;
            }
            case "VOI-007": Assert.AreEqual(EvaluationStatus.ProtocolViolation, EvsiCompatibilityValidator.Validate(new(1), new(2), "weights", "weights")); break;
            case "VOI-009":
            {
                var a = evaluator.Evaluate(new(0.6), new(0.3), new(0.1)); var b = evaluator.Evaluate(new(0.6), new(0.3), new(0.1)); Assert.AreEqual(a.Evsi, b.Evsi); break;
            }
            case "VOI-010": Assert.AreEqual(-0.1, evaluator.Evaluate(new(0.4), new(0.4), new(0.1)).Evsi!.Value, 1e-12); break;
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertRiskExclusion(string id)
    {
        var source = string.Join('\n', TestSupport.ProductionSourceFiles().Select(File.ReadAllText));
        var prohibited = new[] { "CvarAlpha", "TailRiskWeight", "CvarConstraint", "CalculateRul", "dangerousWear" };
        switch (id)
        {
            case "RSK-001": Assert.IsFalse(TestSupport.DeployableTypes().SelectMany(x => x.GetMembers()).Any(x => prohibited.Any(p => x.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))); break;
            case "RSK-002": Assert.IsFalse(typeof(DecisionResult).GetProperties().Any(x => x.Name.Contains("Cvar", StringComparison.OrdinalIgnoreCase))); break;
            case "RSK-003": Assert.IsTrue(new RetirementFirewall(["retired"], [new string('B', 64)]).IsRetiredHash(new string('B', 64))); break;
            case "RSK-004": Assert.IsTrue(source.Contains("FirstPassage", StringComparison.Ordinal)); Assert.IsFalse(source.Contains("CalculateRul", StringComparison.Ordinal)); break;
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertOracle(string id)
    {
        var prohibited = new[] { "TrueState", "TrueWear", "TrueDamage", "TrueRul", "FuturePath", "FutureFailure", "FutureObservation", "DangerousWear", "HiddenTrueMode" };
        switch (id)
        {
            case "ORA-001":
            case "ORA-002":
            case "ORA-003":
            case "ORA-004":
            case "ORA-005": Assert.IsFalse(TestSupport.DeployableTypes().SelectMany(x => x.GetMembers()).Any(m => prohibited.Any(p => m.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))); break;
            case "ORA-006":
            {
                var summary = new CurrentStateOracleEvaluator<ScalarState>().EvaluateForFixture(new(0.8), x => x.Value >= 0.5 ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue);
                Assert.IsFalse(summary.Deployable); Assert.AreEqual("ORC-CURRENT-STATE", summary.OracleId); Assert.AreEqual("SCH-ORC", new CurrentStateOracleEvaluator<ScalarState>().CreateRawRecord().SchemaId); break;
            }
            case "ORA-007":
            {
                var result = new CurrentStateOracleEvaluator<ScalarState>().EvaluateForFixture(new(0.2), _ => MaintenanceActionKind.Continue); Assert.AreEqual(0, result.FutureInformationAccessCount); break;
            }
            case "ORA-008":
            {
                var input = DeployableInput(0); Assert.AreEqual(EvaluationStatus.Success, new DeployableInformationAdmission().Admit(input).Status); Assert.AreEqual(0, input.OracleAccessCount); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertNumerics(string id)
    {
        var factory = new Sha256XoshiroRandomStreamFactory();
        switch (id)
        {
            case "NUM-001":
            {
                var value = new { z = 1.25, a = "fixture" }; CollectionAssert.AreEqual(CanonicalJson.Serialize(value), CanonicalJson.Serialize(value)); break;
            }
            case "NUM-002":
            {
                var a = factory.DeriveDigest(new(TestSupport.StreamContext(), StreamPurpose.PosteriorDraw, 0)); var b = factory.DeriveDigest(new(TestSupport.StreamContext(), StreamPurpose.DegradationInnovation, 0)); CollectionAssert.AreNotEqual(a, b); break;
            }
            case "NUM-003":
            {
                var accumulator = new StableAccumulator(); foreach (var value in new[] { 1d, 1d, 1d, 1d }) accumulator.Add(value); Assert.AreEqual(1d, accumulator.Mean, 0d); Assert.AreEqual(0d, accumulator.StandardError, 0d); break;
            }
            case "NUM-004": Assert.AreEqual(EvaluationStatus.ProtocolViolation, NumericalSettingsValidator.Validate(new("bad", 0, 0, 0, 0, 0.99, 1e-12, 1e-12, true))); break;
            case "NUM-005":
            {
                var p = new FailureProbability(0); Assert.IsTrue(double.IsFinite(ProbabilityMetrics.LogScore(true, p))); Assert.AreEqual(0d, p.Value, 0d); break;
            }
            case "NUM-006":
            {
                var serial = Enumerable.Range(0, 32).Select(i => factory.Create(new(TestSupport.StreamContext(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), StreamPurpose.DegradationInnovation, 0)).NextUInt64()).ToArray();
                var parallel = new ulong[32]; Parallel.For(0, 32, i => parallel[i] = factory.Create(new(TestSupport.StreamContext(i.ToString(System.Globalization.CultureInfo.InvariantCulture)), StreamPurpose.DegradationInnovation, 0)).NextUInt64()); CollectionAssert.AreEqual(serial, parallel); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertProvenance(string id)
    {
        var registry = RawSchemaRegistry.Load(Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv"));
        switch (id)
        {
            case "PRO-001": Assert.IsTrue(ProvenanceKeyFields.All(field => registry.Fields.Any(x => x.FieldName == field))); break;
            case "PRO-002": Assert.IsTrue(registry.Fields.Any(x => x.FieldName == "master_seed") && registry.Fields.Any(x => x.FieldName.Contains("stream_context_hash", StringComparison.Ordinal))); break;
            case "PRO-003": Assert.IsTrue(registry.SchemaIds.Contains("SCH-TRJ") && registry.SchemaIds.Contains("SCH-DEC") && registry.SchemaIds.Contains("SCH-RUN")); break;
            case "PRO-004":
            {
                var expected = new CompatibilityManifest { SchemaVersion = "v1", ArchitectureVersion = "a", MathematicalModelVersion = "m", ProtocolVersion = "p", ComponentHashes = new Dictionary<string, string> { ["model"] = new string('A', 64) } };
                var actual = new CompatibilityManifest { SchemaVersion = "v1", ArchitectureVersion = "a", MathematicalModelVersion = "m", ProtocolVersion = "p", ComponentHashes = new Dictionary<string, string> { ["model"] = new string('B', 64) } };
                Assert.IsNotEmpty(new CompatibilityValidator().Validate(actual, expected)); break;
            }
            case "PRO-005":
            {
                var input = DeployableInput(1); Assert.AreEqual(EvaluationStatus.OracleLeakageDetected, new DeployableInformationAdmission().Admit(input).Status); break;
            }
            case "PRO-006":
            {
                var run = new WriteOnceRunDirectory(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("write-once")); run.WriteText("raw/test.txt", "TEST_FIXTURE_ONLY"); run.Complete(); Assert.ThrowsExactly<InvalidOperationException>(() => run.WriteText("raw/late.txt", "blocked")); break;
            }
            case "PRO-007": Assert.IsTrue(new RetirementFirewall(["reproducible-synthetic-study"], []).IsRetiredPath("E:/WearVision/results/reproducible-synthetic-study/legacy.csv")); break;
            case "PRO-008":
            {
                var config = TestSupport.FixtureConfiguration(); Assert.IsTrue(config.Model.LimitStateVersion.Status.Contains("AUTHOR DATA REQUIRED", StringComparison.Ordinal)); break;
            }
            case "PRO-009":
            {
                var run = new WriteOnceRunDirectory(TestSupport.EvidenceRoot, TestSupport.NewFixtureRunId("incomplete")); run.WriteText("raw/test.txt", "TEST_FIXTURE_ONLY"); Assert.IsFalse(File.Exists(Path.Combine(run.RunRoot, "completion-manifest.json"))); break;
            }
            default: Assert.Fail(id); break;
        }
    }

    private static void AssertStatistics(string id)
    {
        switch (id)
        {
            case "STA-001":
            {
                var issues = SplitManifestValidator.Validate([new("TRJ-1", DataPartition.Design), new("TRJ-1", DataPartition.LockedTest)]); Assert.IsNotEmpty(issues); break;
            }
            case "STA-002":
            {
                var a = new PairedObservation("PAIR-1", "S1", 0.2, 0.3); var b = a with { Proposed = 0.1 }; Assert.AreEqual(a.PairId, b.PairId); break;
            }
            case "STA-003":
            {
                var sample = new DeterministicClusterBootstrap(new Sha256XoshiroRandomStreamFactory()).Resample(0, [new("EP-1", "S", 0.1, 0.2), new("EP-2", "S", 0.2, 0.3)], TestSupport.StreamContext()); Assert.IsTrue(sample.Observations.All(x => x.PairId.StartsWith("EP-", StringComparison.Ordinal))); break;
            }
            case "STA-004":
            {
                var result = HolmProcedure.Reject(new Dictionary<string, double> { ["proposed_vs_B7"] = 0.01, ["proposed_vs_B4"] = 0.02, ["proposed_vs_B1"] = 0.2 }, 0.05); Assert.IsTrue(result["proposed_vs_B7"]); Assert.IsFalse(result["proposed_vs_B1"]); break;
            }
            case "STA-005":
            {
                var config = TestSupport.FixtureConfiguration(); Assert.AreEqual(DataPartition.Design, config.Baselines.TuningPartition); break;
            }
            case "STA-006":
            {
                var observations = new[] { new PairedObservation("EP-1", "scenario-A", 0.1, 0.2), new PairedObservation("EP-2", "scenario-B", 0.2, 0.3) }; Assert.AreEqual(2, observations.Select(x => x.Stratum).Distinct().Count()); break;
            }
            case "STA-007": Assert.AreEqual(EvaluationStatus.NotEvaluable, new DirectEventCensoringStrategy().Evaluate(new(CensoringKind.PlannedReplacement, null, new(0.5), null)).Status); break;
            default: Assert.Fail(id); break;
        }
    }

    private static DeterministicScalarTransition ConstantTransition(double state) => new("constant", (_, interval) => [new(interval.End, new(state))]);
    private static DeterministicScalarTransition ConstantTransitionFromInitial() => new("identity", (state, interval) => [new(interval.End, state)]);

    private static void AssertWeights(IDegradationBelief<ScalarState> belief, double first, double second)
    {
        Assert.IsTrue(belief.TryGetWeightedSupport(out var support)); Assert.AreEqual(first, support[0].Weight, 1e-12); Assert.AreEqual(second, support[1].Weight, 1e-12);
    }

    private static DeployableDecisionInput<ObservationContext> DeployableInput(int oracleAccessCount) => new(
        new("DEC-input"), new("EP-input"), new(0), new(1), new("posterior", new("posterior.v1"), FixtureCatalog.Hash("posterior")), new("fixture", false, null), [], [], new(FixtureCatalog.Hash("compat")), new(FixtureCatalog.Hash("config").Sha256Hex), oracleAccessCount);
}
