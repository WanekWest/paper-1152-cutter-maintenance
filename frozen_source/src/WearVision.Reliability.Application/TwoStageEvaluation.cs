using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public sealed record DelayPropagationRequest<TState>(
    IDegradationBelief<TState> Belief,
    MaintenanceActionKind InformationAction,
    Exposure ActionDuration,
    Exposure PhysicalExposure,
    InspectionExposureMode? InspectionMode);

public sealed record DelayPropagationResult<TState>(
    EvaluationStatus Status,
    FailureProbability FailureProbability,
    IDegradationBelief<TState>? SurvivorBelief,
    Disutility FailureDisutility,
    IReadOnlyDictionary<ConsequenceComponentKind, double> FailureComponents,
    NumericalDiagnostics Diagnostics,
    string Reason);

public interface IInformationDelayPropagator<TState>
{
    DelayPropagationResult<TState> Propagate(DelayPropagationRequest<TState> request, CancellationToken cancellationToken);
}

public sealed class WeightedSupportDelayPropagator<TState> : IInformationDelayPropagator<TState>
{
    private readonly IStateTransitionKernel<TState> _transition;
    private readonly IFunctionalLimitState<TState> _limitState;
    private readonly IRandomStreamFactory _streams;
    private readonly RandomStreamContext _streamContext;
    private readonly Func<IReadOnlyList<WeightedState<TState>>, ArtifactHash> _contentHasher;
    private readonly FirstPassageDetector<TState> _detector = new();

    public WeightedSupportDelayPropagator(
        IStateTransitionKernel<TState> transition,
        IFunctionalLimitState<TState> limitState,
        IRandomStreamFactory streams,
        RandomStreamContext streamContext,
        Func<IReadOnlyList<WeightedState<TState>>, ArtifactHash> contentHasher)
    {
        _transition = transition;
        _limitState = limitState;
        _streams = streams;
        _streamContext = streamContext;
        _contentHasher = contentHasher;
    }

    public DelayPropagationResult<TState> Propagate(DelayPropagationRequest<TState> request, CancellationToken cancellationToken)
    {
        if (!request.Belief.TryGetWeightedSupport(out var support) || !request.Belief.Validate().IsValid)
            return Failure(EvaluationStatus.InvalidPosterior, request.Belief, "WEIGHTED_SUPPORT_REQUIRED");
        if (request.Belief.StateSpace.Id != _transition.StateSpace.Id)
            return Failure(EvaluationStatus.IncompatibleModel, request.Belief, "STATE_SPACE_MISMATCH");
        var survivors = new List<WeightedState<TState>>();
        var failedMass = 0d;
        for (var index = 0; index < support.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = support[index];
            var interval = new ExposureInterval(new(0d), request.PhysicalExposure);
            var context = new PhysicalActionContext(request.InformationAction, request.ActionDuration, request.InspectionMode);
            var segment = _transition.SampleSegment(item.State, interval, context, null, _streams.Create(new(_streamContext, StreamPurpose.InformationBranchInnerPath, checked((ulong)index))));
            if (_detector.Detect(item.State, new(0d), segment, _limitState).Failed)
            {
                failedMass += item.Weight;
                continue;
            }
            var state = segment.Checkpoints.Count == 0 ? item.State : segment.Checkpoints.MaxBy(x => x.Exposure.Value).State;
            survivors.Add(new(state, item.Weight));
        }
        var survivalMass = 1d - failedMass;
        IDegradationBelief<TState>? survivorBelief = null;
        if (survivalMass > 1e-12)
        {
            var normalized = survivors.Select(x => new WeightedState<TState>(x.State, x.Weight / survivalMass)).ToArray();
            survivorBelief = new ImmutableWeightedSampleBelief<TState>(new($"{request.Belief.Version.Value}.delay"), request.Belief.StateSpace, _contentHasher(normalized), normalized);
        }
        var components = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, x => x == ConsequenceComponentKind.Failure ? 1d : 0d);
        return new(
            EvaluationStatus.Success,
            new(Math.Clamp(failedMass, 0d, 1d)),
            survivorBelief,
            new(1d),
            components,
            new("weighted-support-delay-v1", support.Count, 1, new(0d), new(0d), new(failedMass), new(failedMass), true, "TEST_FIXTURE_OR_DISCRETE_SUPPORT", Array.Empty<string>()),
            failedMass >= 1d - 1e-12 ? "FAILURE_BEFORE_INFORMATION" : "DELAY_PROPAGATED");
    }

    private static DelayPropagationResult<TState> Failure(EvaluationStatus status, IDegradationBelief<TState> belief, string reason) => new(
        status,
        new(0d),
        belief,
        new(0d),
        new Dictionary<ConsequenceComponentKind, double>(),
        new("weighted-support-delay-v1", 0, 0, new(0d), new(0d), null, null, false, "NONE", [reason]),
        reason);
}

public sealed record FiniteObservationBranch<TObservation>(
    string BranchId,
    TObservation Observation,
    ObservationContext Context,
    Probability PredictiveProbability,
    bool SkipBayesUpdate);

public interface IFiniteObservationBranchProvider<TState, TObservation>
{
    IReadOnlyList<FiniteObservationBranch<TObservation>> Enumerate(
        IDegradationBelief<TState> predictiveBelief,
        ObservationContext baseContext);
}

public interface ITerminalActionEvaluator<TState>
{
    CompletePolicyEvaluation Evaluate(
        IDegradationBelief<TState> belief,
        MaintenanceActionKind action,
        Horizon remainingHorizon,
        ConfigurationHash configurationHash,
        CancellationToken cancellationToken);
}

public sealed record TwoStageBranchEvaluation<TObservation>(
    string BranchId,
    TObservation Observation,
    Probability Probability,
    EvaluationStatus PosteriorStatus,
    DecisionResult TerminalDecision,
    CompletePolicyEvaluation? SelectedTerminal,
    FeasibilityResult BranchFeasibility,
    string Reason,
    ArtifactHash? PosteriorHash = null);

public sealed record TwoStageEvaluationResult<TObservation>(
    EvaluationStatus Status,
    DecisionResult Decision,
    IReadOnlyList<CompletePolicyEvaluation> CompletePolicies,
    IReadOnlyList<TwoStageBranchEvaluation<TObservation>> Branches,
    EvsiEvaluation? Evsi,
    bool FailureBeforeObservationTerminatesElectiveDecision,
    int OracleAccessCount,
    string Reason);

public sealed record TwoStagePolicyEvaluationRequest<TState, TObservation>(
    IReadOnlyList<PolicyTree> Policies,
    IDegradationBelief<TState> Belief,
    ActionAdmissibilityContext Admissibility,
    IInformationDelayPropagator<TState> DelayPropagator,
    IFiniteObservationBranchProvider<TState, TObservation> BranchProvider,
    IObservationKernel<TState, TObservation> ObservationKernel,
    IBeliefUpdater<TState, TObservation> BeliefUpdater,
    ITerminalActionEvaluator<TState> TerminalEvaluator,
    ConsequenceWeightProfile ConsequenceWeights,
    ObservationContext ObservationContext,
    FailureProbability Epsilon,
    FailureProbability EpsilonRemaining,
    Disutility AcquisitionBurden,
    ConfigurationHash ConfigurationHash);

public sealed class TwoStagePolicyEvaluator<TState, TObservation>
{
    private readonly IActionAdmissibilityEvaluator _admissibility;
    private readonly IReliabilityFeasibilityEvaluator _feasibility;
    private readonly IConstrainedPolicySelector _selector;
    private readonly IEvsiEvaluator _evsi;

    public TwoStagePolicyEvaluator(
        IActionAdmissibilityEvaluator admissibility,
        IReliabilityFeasibilityEvaluator feasibility,
        IConstrainedPolicySelector selector,
        IEvsiEvaluator evsi)
    {
        _admissibility = admissibility;
        _feasibility = feasibility;
        _selector = selector;
        _evsi = evsi;
    }

    public TwoStageEvaluationResult<TObservation> Evaluate(
        TwoStagePolicyEvaluationRequest<TState, TObservation> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var complete = new List<CompletePolicyEvaluation>();
        var branches = new List<TwoStageBranchEvaluation<TObservation>>();

        foreach (var direct in request.Policies.OfType<DirectPolicy>().OrderBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var admissibility = _admissibility.Evaluate(direct.Action, request.Admissibility);
            if (!admissibility.IsAdmissible)
            {
                complete.Add(NotEvaluable(direct, admissibility, request.ConfigurationHash, "DIRECT_ACTION_NOT_ADMISSIBLE"));
                continue;
            }
            complete.Add(request.TerminalEvaluator.Evaluate(request.Belief, direct.Action, direct.Horizon, request.ConfigurationHash, cancellationToken));
        }

        foreach (var policy in request.Policies.OfType<InformationPolicy<TObservation>>().OrderBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            complete.Add(EvaluateInformationPolicy(policy, request, branches, cancellationToken));
        }

        var decision = _selector.Select(complete);
        var directDecision = _selector.Select(complete.Where(x => x.Policy is DirectPolicy).ToArray());
        var selectedInformation = decision.Selected is not null && decision.Selected.Policy is InformationPolicy<TObservation>
            ? decision.Selected
            : complete.Where(x => x.Policy is InformationPolicy<TObservation> && x.Disutility.ExpectedDisutility is not null)
                .OrderBy(x => x.Disutility.ExpectedDisutility!.Value.Value).ThenBy(x => x.Policy.Id.Value, StringComparer.Ordinal).FirstOrDefault();
        EvsiEvaluation? evsi = null;
        if (directDecision.Selected?.Disutility.ExpectedDisutility is { } noInformation && selectedInformation?.Disutility.ExpectedDisutility is { } information)
        {
            var preAcquisition = new Disutility(Math.Clamp(information.Value - request.AcquisitionBurden.Value, 0d, 1d));
            evsi = _evsi.Evaluate(noInformation, preAcquisition, request.AcquisitionBurden);
        }

        var status = decision.Selected is null ? EvaluationStatus.NotEvaluable : EvaluationStatus.Success;
        return new(status, decision, complete, branches, evsi, branches.Count == 0 && complete.Any(x => x.Warnings.Contains("FAILURE_BEFORE_INFORMATION")), 0, decision.Reason);
    }

    private CompletePolicyEvaluation EvaluateInformationPolicy(
        InformationPolicy<TObservation> policy,
        TwoStagePolicyEvaluationRequest<TState, TObservation> request,
        List<TwoStageBranchEvaluation<TObservation>> branchEvidence,
        CancellationToken cancellationToken)
    {
        var admissibility = _admissibility.Evaluate(policy.InformationAction, request.Admissibility);
        if (!admissibility.IsAdmissible) return NotEvaluable(policy, admissibility, request.ConfigurationHash, "INFORMATION_ACTION_NOT_ADMISSIBLE");
        var physicalExposure = policy.InformationAction == MaintenanceActionKind.TargetedInspection && request.Admissibility.InspectionMode == InspectionExposureMode.Paused
            ? new Exposure(0d)
            : policy.AcquisitionDuration;
        var delay = request.DelayPropagator.Propagate(
            new(request.Belief, policy.InformationAction, policy.AcquisitionDuration, physicalExposure, request.Admissibility.InspectionMode),
            cancellationToken);
        if (delay.Status != EvaluationStatus.Success)
            return NotEvaluable(policy, admissibility, request.ConfigurationHash, delay.Reason);

        var remaining = new Horizon(Math.Max(double.Epsilon, policy.Horizon.Value - policy.AcquisitionDuration.Value));
        var delayFailure = delay.FailureProbability.Value;
        if (delayFailure >= 1d - 1e-12)
        {
            var reliability = ExactReliability(1d, "two-stage-delay-failure-v1");
            var disutility = new ExpectedDisutilityEvaluation(EvaluationStatus.Success, new(Math.Clamp(delay.FailureDisutility.Value + request.AcquisitionBurden.Value, 0d, 1d)), delay.FailureComponents, new(0d), 1, "failure-before-information");
            return new(policy, admissibility, reliability, disutility, _feasibility.Evaluate(reliability, request.Epsilon), new(FeasibilityClassification.ProvenInfeasible, 1d - request.EpsilonRemaining.Value, "FAILURE_BEFORE_INFORMATION"), request.AcquisitionBurden.Value, request.ConfigurationHash, EvaluationStatus.Success, ["FAILURE_BEFORE_INFORMATION"]);
        }

        if (delay.SurvivorBelief is null)
            return NotEvaluable(policy, admissibility, request.ConfigurationHash, "SURVIVOR_BELIEF_MISSING");

        var outcomes = request.BranchProvider.Enumerate(delay.SurvivorBelief, request.ObservationContext)
            .OrderBy(x => x.BranchId, StringComparer.Ordinal).ToArray();
        if (outcomes.Length == 0 || Math.Abs(outcomes.Sum(x => x.PredictiveProbability.Value) - 1d) > 1e-12)
            return NotEvaluable(policy, admissibility, request.ConfigurationHash, "INVALID_FINITE_OBSERVATION_SUPPORT");

        var branchFailure = 0d;
        var branchDisutility = 0d;
        var componentMeans = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, _ => 0d);
        var worstBranch = new FeasibilityResult(FeasibilityClassification.ProvenFeasible, double.NegativeInfinity, "ALL_BRANCHES_FEASIBLE");
        foreach (var outcome in outcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = outcome.SkipBayesUpdate
                ? new BeliefUpdateResult<TState>(EvaluationStatus.Success, delay.SurvivorBelief, "MISSING_NONINFORMATIVE_NO_UPDATE")
                : request.BeliefUpdater.Update(delay.SurvivorBelief, outcome.Observation, request.ObservationKernel, outcome.Context);
            if (update.Status != EvaluationStatus.Success || update.Posterior is null)
            {
                var bad = new FeasibilityResult(FeasibilityClassification.Unresolved, double.NaN, update.Reason);
                branchEvidence.Add(new(outcome.BranchId, outcome.Observation, outcome.PredictiveProbability, update.Status, new(DecisionStatus.NotEvaluable, null, [], update.Reason), null, bad, update.Reason));
                return NotEvaluable(policy, admissibility, request.ConfigurationHash, update.Reason);
            }
            var terminal = new[]
            {
                request.TerminalEvaluator.Evaluate(update.Posterior, MaintenanceActionKind.Continue, remaining, request.ConfigurationHash, cancellationToken),
                request.TerminalEvaluator.Evaluate(update.Posterior, MaintenanceActionKind.PlannedReplacement, remaining, request.ConfigurationHash, cancellationToken)
            };
            var branchCandidates = terminal.Select(x => x with { OverallFeasibility = _feasibility.Evaluate(x.Reliability, request.EpsilonRemaining) }).ToArray();
            var selected = _selector.Select(branchCandidates);
            var selectedTerminal = selected.Selected;
            var branchFeasibility = selectedTerminal is null
                ? new(FeasibilityClassification.Unresolved, double.NaN, selected.Reason)
                : _feasibility.Evaluate(selectedTerminal.Reliability, request.EpsilonRemaining);
            branchEvidence.Add(new(outcome.BranchId, outcome.Observation, outcome.PredictiveProbability, update.Status, selected, selectedTerminal, branchFeasibility, selected.Reason, update.Posterior.ContentHash));
            if (selectedTerminal?.Reliability.FailureProbability is null || selectedTerminal.Disutility.ExpectedDisutility is null)
                return NotEvaluable(policy, admissibility, request.ConfigurationHash, "TERMINAL_BRANCH_NOT_EVALUABLE");
            var probability = outcome.PredictiveProbability.Value;
            branchFailure += probability * selectedTerminal.Reliability.FailureProbability.Value.Value;
            branchDisutility += probability * selectedTerminal.Disutility.ExpectedDisutility.Value.Value;
            foreach (var kind in componentMeans.Keys)
                componentMeans[kind] += probability * selectedTerminal.Disutility.ComponentExpectations.GetValueOrDefault(kind);
            if (branchFeasibility.Classification != FeasibilityClassification.ProvenFeasible) worstBranch = branchFeasibility;
        }

        var survival = 1d - delayFailure;
        var totalFailure = Math.Clamp(delayFailure + survival * branchFailure, 0d, 1d);
        var postInformationDisutility = Math.Clamp(delayFailure * delay.FailureDisutility.Value + survival * branchDisutility, 0d, 1d);
        var totalDisutility = Math.Clamp(postInformationDisutility + request.AcquisitionBurden.Value, 0d, 1d);
        foreach (var kind in componentMeans.Keys)
            componentMeans[kind] = delayFailure * delay.FailureComponents.GetValueOrDefault(kind) + survival * componentMeans[kind];
        var reliabilityAggregate = ExactReliability(totalFailure, "two-stage-finite-enumeration-v1");
        var disutilityAggregate = new ExpectedDisutilityEvaluation(EvaluationStatus.Success, new(totalDisutility), componentMeans, new(0d), outcomes.Length, "finite-outcome-weighted-v1");
        return new(policy, admissibility, reliabilityAggregate, disutilityAggregate, _feasibility.Evaluate(reliabilityAggregate, request.Epsilon), worstBranch, request.AcquisitionBurden.Value, request.ConfigurationHash, EvaluationStatus.Success, Array.Empty<string>());
    }

    private static ReliabilityEvaluation ExactReliability(double failureProbability, string method) => new(
        EvaluationStatus.Success,
        new(failureProbability),
        new(1d - failureProbability),
        new(failureProbability),
        new(failureProbability),
        new(method, 1, 1, new(0d), new(0d), new(failureProbability), new(failureProbability), true, "TEST_FIXTURE_OR_FINITE_ENUMERATION", Array.Empty<string>()),
        null);

    private static CompletePolicyEvaluation NotEvaluable(PolicyTree policy, ActionAdmissibilityResult admissibility, ConfigurationHash hash, string reason) => new(
        policy,
        admissibility,
        new(EvaluationStatus.NotEvaluable, null, null, null, null, new("two-stage-not-evaluable", 0, 0, new(0d), new(0d), null, null, false, "NONE", [reason]), null),
        new(EvaluationStatus.NotEvaluable, null, new Dictionary<ConsequenceComponentKind, double>(), new(0d), 0, reason),
        new(FeasibilityClassification.Unresolved, double.NaN, reason),
        null,
        0d,
        hash,
        EvaluationStatus.NotEvaluable,
        [reason]);
}

public sealed class StateConditionedBinaryBranchProvider : IFiniteObservationBranchProvider<double, DiscreteObservation>
{
    private readonly StateConditionedBinaryObservationKernel _kernel;

    public StateConditionedBinaryBranchProvider(StateConditionedBinaryObservationKernel kernel) => _kernel = kernel;

    public IReadOnlyList<FiniteObservationBranch<DiscreteObservation>> Enumerate(
        IDegradationBelief<double> predictiveBelief,
        ObservationContext baseContext)
    {
        if (!predictiveBelief.TryGetWeightedSupport(out var support) || !predictiveBelief.Validate().IsValid)
            return Array.Empty<FiniteObservationBranch<DiscreteObservation>>();
        return _kernel.Outcomes.Select(outcome =>
        {
            var probability = support.Sum(item => item.Weight * _kernel.OutcomeProbability(outcome, item.State));
            var context = baseContext with { Missing = outcome.Missing };
            return new FiniteObservationBranch<DiscreteObservation>(outcome.OutcomeId, outcome, context, new Probability(Math.Clamp(probability, 0d, 1d)), outcome.Missing && _kernel.MissingMode == MissingObservationMode.NonInformative);
        }).Where(x => x.PredictiveProbability.Value > 0d).ToArray();
    }
}
