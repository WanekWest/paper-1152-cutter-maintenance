namespace WearVision.Reliability.Domain;

public interface IRandomStream
{
    ulong NextUInt64();
    double NextUnitDouble();
}

public interface IRandomStreamFactory
{
    IRandomStream Create(RandomStreamKey key);
    byte[] EncodeKey(RandomStreamKey key);
    byte[] DeriveDigest(RandomStreamKey key);
}

public interface IDeployableInformationAdmission
{
    AdmissionResult Admit<TObservationContext>(DeployableDecisionInput<TObservationContext> input);
}

public interface IDegradationBelief<TState>
{
    PosteriorVersionId Version { get; }
    StateSpaceDescriptor StateSpace { get; }
    ArtifactHash ContentHash { get; }
    BeliefValidationResult Validate();
    TState Sample(IRandomStream stream);
    bool TryGetWeightedSupport(out IReadOnlyList<WeightedState<TState>> support);
    bool TryGetParametricDescriptor(out ParametricBeliefDescriptor descriptor);
}

public interface IStateTransitionKernel<TState>
{
    ModelVersionId Version { get; }
    StateSpaceDescriptor StateSpace { get; }
    TransitionCapabilities Capabilities { get; }
    TransitionSegment<TState> SampleSegment(TState initialState, ExposureInterval interval, PhysicalActionContext actionContext, ObservedOperatingContext? operatingContext, IRandomStream stream);
    bool TryEvaluateLogDensity(TransitionDensityQuery<TState> query, out double logDensity);
}

public interface IResetKernel<TState>
{
    ModelVersionId Version { get; }
    StateSpaceDescriptor StateSpace { get; }
    TState SampleReset(ResetContext context, IRandomStream stream);
}

public interface IObservationKernel<TState, TObservation>
{
    ModelVersionId Version { get; }
    StateSpaceDescriptor StateSpace { get; }
    ObservationKind Kind { get; }
    TObservation Sample(TState state, ObservationContext context, IRandomStream stream);
    double LogLikelihood(TObservation observation, TState state, ObservationContext context);
}

public interface IBeliefUpdater<TState, TObservation>
{
    BeliefUpdateResult<TState> Update(IDegradationBelief<TState> predictivePrior, TObservation observation, IObservationKernel<TState, TObservation> likelihood, ObservationContext context);
}

public interface IFunctionalLimitState<TState>
{
    LimitStateVersionId Version { get; }
    UnitDescriptor MarginUnit { get; }
    string CalibrationEvidenceId { get; }
    double Margin(TState state);
}

public interface IFirstPassageReliabilityEstimator<TState>
{
    ReliabilityEvaluation Estimate(ReliabilityEvaluationRequest<TState> request, CancellationToken cancellationToken);
}

public interface IConstrainedTerminalRule<in TObservation>
{
    MaintenanceActionKind Select(TObservation observation);
}

public interface IPolicyEnumerator<TObservation>
{
    IReadOnlyList<PolicyTree> Enumerate(ActionAdmissibilityContext context, Horizon horizon);
}

public interface IActionAdmissibilityEvaluator
{
    ActionAdmissibilityResult Evaluate(MaintenanceActionKind action, ActionAdmissibilityContext context);
}

public interface IConsequenceEvaluator
{
    ConsequenceVector Evaluate(PathOutcome outcome, ConsequenceDefinition definition);
}

public interface IExpectedDisutilityEvaluator
{
    ExpectedDisutilityEvaluation Evaluate(IReadOnlyList<ConsequenceVector> outcomes, ConsequenceWeightProfile profile);
}

public interface IReliabilityFeasibilityEvaluator
{
    FeasibilityResult Evaluate(ReliabilityEvaluation reliability, FailureProbability tolerance);
}

public interface IConstrainedPolicySelector
{
    DecisionResult Select(IReadOnlyList<CompletePolicyEvaluation> evaluations);
}

public interface IEvsiEvaluator
{
    EvsiEvaluation Evaluate(Disutility noInformationValue, Disutility informationActionValue, Disutility acquisitionBurden, Probability? decisionChangeProbability = null);
}

public interface ICensoringStrategy
{
    CensoringEvaluation Evaluate(CensoringObservation observation);
}
