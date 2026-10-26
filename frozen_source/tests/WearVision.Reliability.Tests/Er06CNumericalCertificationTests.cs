using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class Er06CNumericalCertificationTests
{
    private const double Epsilon = 0.125d;
    private const double ErrorBound = 1e-10d;

    [TestMethod]
    public void ER06C_001_exact_boundary_is_boundary_uncertified()
    {
        var result = Exact(0.125d);
        Assert.AreEqual(EvaluationStatus.BoundaryUncertified, result.Status);
        Assert.AreEqual(FeasibilityClassification.BoundaryUncertified, Feasibility(result));
        Assert.IsGreaterThan(Epsilon, result.FailureUpperNumericalBound!.Value.Value);
    }

    [TestMethod]
    public void ER06C_002_safely_feasible_probability_is_certified()
    {
        var result = Exact(0.10d);
        Assert.AreEqual(EvaluationStatus.Success, result.Status);
        Assert.AreEqual(FeasibilityClassification.ProvenFeasible, Feasibility(result));
    }

    [TestMethod]
    public void ER06C_003_safely_infeasible_probability_is_certified()
    {
        var result = Exact(0.15d);
        Assert.AreEqual(EvaluationStatus.Success, result.Status);
        Assert.AreEqual(FeasibilityClassification.ProvenInfeasible, Feasibility(result));
    }

    [TestMethod]
    public void ER06C_004_near_boundary_below_is_fail_closed()
    {
        var result = Exact(Epsilon - ErrorBound / 2d);
        Assert.AreEqual(EvaluationStatus.BoundaryUncertified, result.Status);
        Assert.AreNotEqual(FeasibilityClassification.ProvenFeasible, Feasibility(result));
    }

    [TestMethod]
    public void ER06C_005_near_boundary_above_is_fail_closed()
    {
        var result = Exact(Epsilon + ErrorBound / 2d);
        Assert.AreEqual(EvaluationStatus.BoundaryUncertified, result.Status);
        Assert.AreNotEqual(FeasibilityClassification.ProvenInfeasible, Feasibility(result));
    }

    [TestMethod]
    public void ER06C_006_analytical_two_point_fixture_equals_epsilon()
    {
        var stateSpace = StateSpace();
        var belief = Belief(stateSpace, [new(0.2d, 0.75d), new(0.8d, 0.25d)]);
        var request = Request(belief, stateSpace, 0.8375d, 0.075d, 1d);
        var result = new DeterministicScalarUniformFirstPassageFallback(ErrorBound).TryEvaluate(request, 976_896, 476)!;
        Assert.AreEqual(Epsilon, result.FailureProbability!.Value.Value, 1e-12d);
        Assert.AreEqual(EvaluationStatus.BoundaryUncertified, result.Status);
    }

    [TestMethod]
    public void ER06C_007_sequential_estimator_invokes_fallback_at_frozen_threshold_shape()
    {
        var stateSpace = StateSpace();
        var belief = Belief(stateSpace, [new(0.125d, 1d)]);
        var request = Request(belief, stateSpace, 1d, 1d, 1d) with
        {
            Numerical = new("ER06C-TEST", 4, 2, 100, 3, 0.99d, 1e-10d, 1e-12d, true)
        };
        var estimator = new PosteriorPredictiveFirstPassageEstimator<double>(
            new Sha256XoshiroRandomStreamFactory(),
            deterministicFallback: new DeterministicScalarUniformFirstPassageFallback(ErrorBound),
            fallbackEscalationPaths: 8);
        var result = estimator.Estimate(request, default);
        Assert.AreEqual(EvaluationStatus.BoundaryUncertified, result.Status);
        Assert.AreEqual(8L, result.Diagnostics.EvaluationCount);
        Assert.AreEqual(DeterministicScalarUniformFirstPassageFallback.MethodId, result.Diagnostics.MethodId);
    }

    [TestMethod]
    public void ER06C_008_boundary_candidate_never_enters_feasible_set()
    {
        var boundary = Candidate("P-BOUNDARY", MaintenanceActionKind.Continue, 0.125d, 0.01d, FeasibilityClassification.BoundaryUncertified);
        var replacement = Candidate("P-REPLACE", MaintenanceActionKind.PlannedReplacement, 0d, 0.25d, FeasibilityClassification.ProvenFeasible);
        var decision = new ConstrainedPolicySelector().Select([boundary, replacement]);
        Assert.AreEqual("P-REPLACE", decision.Selected!.Policy.Id.Value);
    }

    [TestMethod]
    public void ER06C_009_all_boundary_candidates_return_no_certified_feasible_action()
    {
        var decision = new ConstrainedPolicySelector().Select(
            [Candidate("P-BOUNDARY", MaintenanceActionKind.Continue, 0.125d, 0.01d, FeasibilityClassification.BoundaryUncertified)]);
        Assert.IsNull(decision.Selected);
        Assert.AreEqual("NO_CERTIFIED_FEASIBLE_ACTION", decision.Reason);
    }

    [TestMethod]
    public void ER06C_010_branchwise_epsilon_is_evaluated_independently()
    {
        var reliability = Reliability(0.12d, 0.11d, 0.121d, EvaluationStatus.Success);
        var evaluator = new ReliabilityFeasibilityEvaluator();
        Assert.AreEqual(FeasibilityClassification.ProvenFeasible, evaluator.Evaluate(reliability, new(0.125d)).Classification);
        Assert.AreEqual(FeasibilityClassification.Unresolved, evaluator.Evaluate(reliability, new(0.115d)).Classification);
    }

    [TestMethod]
    public void ER06C_011_all_infeasible_set_retains_fail_closed_breach_ordering()
    {
        var decision = new ConstrainedPolicySelector().Select(
        [
            Candidate("P-I1", MaintenanceActionKind.Continue, 0.30d, 0.1d, FeasibilityClassification.ProvenInfeasible),
            Candidate("P-I2", MaintenanceActionKind.PlannedReplacement, 0.20d, 0.2d, FeasibilityClassification.ProvenInfeasible)
        ]);
        Assert.AreEqual(DecisionStatus.ConstraintBreach, decision.Status);
        Assert.AreEqual("P-I2", decision.Selected!.Policy.Id.Value);
    }

    [TestMethod]
    public void ER06C_012_feasible_infeasible_boundary_mixture_selects_only_certified_feasible()
    {
        var decision = new ConstrainedPolicySelector().Select(
        [
            Candidate("P-F", MaintenanceActionKind.PlannedReplacement, 0.01d, 0.50d, FeasibilityClassification.ProvenFeasible),
            Candidate("P-I", MaintenanceActionKind.Continue, 0.20d, 0.10d, FeasibilityClassification.ProvenInfeasible),
            Candidate("P-B", MaintenanceActionKind.Continue, 0.125d, 0.01d, FeasibilityClassification.BoundaryUncertified)
        ]);
        Assert.AreEqual("P-F", decision.Selected!.Policy.Id.Value);
    }

    private static ReliabilityEvaluation Exact(double probability)
    {
        var stateSpace = StateSpace();
        var belief = Belief(stateSpace, [new(probability, 1d)]);
        return new DeterministicScalarUniformFirstPassageFallback(ErrorBound).TryEvaluate(
            Request(belief, stateSpace, 1d, 1d, 1d), 976_896, 476)!;
    }

    private static FeasibilityClassification Feasibility(ReliabilityEvaluation reliability) =>
        new ReliabilityFeasibilityEvaluator().Evaluate(reliability, new(Epsilon)).Classification;

    private static ReliabilityEvaluationRequest<double> Request(
        IDegradationBelief<double> belief,
        StateSpaceDescriptor stateSpace,
        double boundary,
        double maximumRate,
        double horizon) => new(
            new("ER06C-D-1"),
            belief,
            new ExposureScaledUniformRateTransition(maximumRate, "transition.er06c", stateSpace, "exposure.er06c"),
            new TwoPointScalarResetKernel(0d, 0d, 1d, "reset.er06c", stateSpace),
            new AffineScalarLimitState(boundary, "limit.er06c", new("margin", "dimensionless"), "ER06C"),
            new DirectPolicy(new("P-CONTINUE"), new(horizon), MaintenanceActionKind.Continue),
            new(horizon),
            new("ER06C", 4, 2, 1_048_576, 3, 0.99d, 1e-10d, 1e-12d, true),
            new(1UL, new("ER06C"), new("ER06C-S"), new("ER06C-T"), new("ER06C-E"), new("ER06C-D"), "ER06C-CRN"),
            new(Epsilon));

    private static StateSpaceDescriptor StateSpace() => new("scalar-reference-state", "er06c", "dimensionless");

    private static ImmutableWeightedSampleBelief<double> Belief(StateSpaceDescriptor stateSpace, IReadOnlyList<WeightedState<double>> support) =>
        new(new("posterior.er06c"), stateSpace, new(new string('A', 64)), support);

    private static CompletePolicyEvaluation Candidate(
        string id,
        MaintenanceActionKind action,
        double probability,
        double disutility,
        FeasibilityClassification classification)
    {
        var status = classification == FeasibilityClassification.BoundaryUncertified
            ? EvaluationStatus.BoundaryUncertified
            : EvaluationStatus.Success;
        var reliability = Reliability(probability, probability, probability, status);
        return new(
            new DirectPolicy(new(id), new(1d), action),
            new(true, ActionAdmissibilityReason.Admissible),
            reliability,
            new(EvaluationStatus.Success, new(disutility), new Dictionary<ConsequenceComponentKind, double>(), new(0d), 1, "ER06C"),
            new(classification, probability - Epsilon, classification.ToString()),
            null,
            action == MaintenanceActionKind.PlannedReplacement ? disutility : 0d,
            new(new string('B', 64)),
            status,
            Array.Empty<string>());
    }

    private static ReliabilityEvaluation Reliability(double probability, double lower, double upper, EvaluationStatus status) => new(
        status,
        new(probability),
        new(1d - probability),
        new(lower),
        new(upper),
        new("ER06C", 1, 1, new(0d), new(0d), new(lower), new(upper), true, "ER06C", Array.Empty<string>()),
        null);
}
