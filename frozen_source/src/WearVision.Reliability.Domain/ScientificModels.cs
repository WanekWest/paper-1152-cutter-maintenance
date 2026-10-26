namespace WearVision.Reliability.Domain;

public enum StreamPurpose : int
{
    InitialState = 1,
    PosteriorDraw = 2,
    DegradationInnovation = 3,
    RoutineObservation = 4,
    MonitorObservation = 5,
    InspectionObservation = 6,
    ReplacementReset = 7,
    EventJump = 8,
    InformationBranchInnerPath = 9,
    BootstrapResample = 10,
    OracleOnly = 11
}

public enum MaintenanceActionKind
{
    Continue,
    IntensifiedMonitoring,
    TargetedInspection,
    PlannedReplacement
}

public enum ObservationKind
{
    Routine,
    IntensifiedMonitoring,
    TargetedInspection
}

public enum InspectionExposureMode
{
    ContinuedOperation,
    Paused
}

public enum ConsequenceComponentKind
{
    Failure,
    PlannedReplacement,
    IntensifiedMonitoring,
    TargetedInspection,
    Downtime
}

public enum WeightProfileStatus
{
    Calibrated,
    Elicited,
    MethodologicalStress,
    TestFixtureOnly
}

public enum FeasibilityClassification
{
    ProvenFeasible,
    ProvenInfeasible,
    BoundaryUncertified,
    Unresolved
}

public enum ActionAdmissibilityReason
{
    Admissible,
    ActionDisabledByProtocol,
    MissingObservationKernel,
    MissingDuration,
    IncompatibleModel,
    InsufficientHorizon,
    MissingResetModel,
    MissingConsequenceProfile,
    InvalidExposureMode
}

public enum RunStage
{
    T0,
    T1,
    T2,
    T3,
    T4,
    T5,
    T6
}

public enum ConfigurationProfileKind
{
    TestFixtureOnly,
    T5Pilot,
    T6Scientific
}

public enum DataPartition
{
    Training,
    Calibration,
    Design,
    LockedTest,
    T5PilotOnly
}

public enum CensoringKind
{
    None,
    ExactFailure,
    RightCensored,
    IntervalCensored,
    PlannedReplacement,
    Administrative,
    ResetNewEpisode
}

public sealed record StateSpaceDescriptor(string Id, string Version, string UnitMetadata);
public sealed record UnitDescriptor(string Id, string Description);
public sealed record ParametricBeliefDescriptor(string FamilyId, IReadOnlyDictionary<string, double> Parameters);
public readonly record struct WeightedState<TState>(TState State, double Weight);

public sealed record BeliefValidationResult(EvaluationStatus Status, double Mass, IReadOnlyList<string> Issues)
{
    public bool IsValid => Status == EvaluationStatus.Success;
}

public sealed record BeliefUpdateResult<TState>(EvaluationStatus Status, IDegradationBelief<TState>? Posterior, string Reason);

public readonly record struct ExposureInterval(Exposure Start, Exposure End)
{
    public double Length => End.Value - Start.Value;
}

public sealed record PhysicalActionContext(MaintenanceActionKind Action, Exposure Duration, InspectionExposureMode? InspectionMode);
public sealed record ObservedOperatingContext(string ContextId, IReadOnlyDictionary<string, double> Values);
public sealed record ObservationContext(string QualityId, bool Missing, ObservedOperatingContext? OperatingContext);
public sealed record ResetContext(ComponentEpisodeId PreviousEpisode, ComponentEpisodeId NewEpisode, Exposure Exposure);
public sealed record TransitionCapabilities(bool SupportsDensity, bool SupportsRefinement, bool EventJumpsEnabled);
public readonly record struct StateCheckpoint<TState>(Exposure Exposure, TState State);
public sealed record TransitionSegment<TState>(IReadOnlyList<StateCheckpoint<TState>> Checkpoints, bool CanRefine, string MethodId);
public sealed record TransitionDensityQuery<TState>(TState From, TState To, ExposureInterval Interval);

public sealed record NumericalDiagnostics(
    string MethodId,
    long EvaluationCount,
    int BatchCount,
    NumericalStandardError FailureProbabilityStandardError,
    NumericalStandardError DisutilityStandardError,
    Probability? LowerNumericalBound,
    Probability? UpperNumericalBound,
    bool Converged,
    string ToleranceProfileId,
    IReadOnlyList<string> WarningCodes);

public sealed record NumericalSettings(
    string ProfileId,
    int MinimumPaths,
    int BatchSize,
    int MaximumPaths,
    int RequiredStableBatches,
    double ConfidenceLevel,
    double ProbabilityTolerance,
    double TieTolerance,
    bool TestFixtureOnly)
{
    public static NumericalSettings GeneralEr04 => new(
        "ER04-GENERAL", 4096, 2048, 1_048_576, 3, 0.99, 1e-12, 1e-12, false);
}

public sealed record RandomStreamContext(
    ulong MasterSeed,
    ExperimentId ExperimentId,
    ScenarioId ScenarioId,
    TrajectoryId TrajectoryId,
    ComponentEpisodeId ComponentEpisodeId,
    DecisionId DecisionId,
    string CouplingGroupId);

public sealed record RandomStreamKey(RandomStreamContext Context, StreamPurpose Purpose, ulong SubstreamIndex);

public abstract record PolicyTree(PolicyId Id, Horizon Horizon)
{
    public abstract MaintenanceActionKind FirstAction { get; }
}

public sealed record DirectPolicy(PolicyId Id, Horizon Horizon, MaintenanceActionKind Action) : PolicyTree(Id, Horizon)
{
    public override MaintenanceActionKind FirstAction => Action;
}

public sealed record InformationPolicy<TObservation>(
    PolicyId Id,
    Horizon Horizon,
    MaintenanceActionKind InformationAction,
    Exposure AcquisitionDuration,
    IConstrainedTerminalRule<TObservation> TerminalRule) : PolicyTree(Id, Horizon)
{
    public override MaintenanceActionKind FirstAction => InformationAction;
}

public sealed record ActionAdmissibilityContext(
    Horizon Horizon,
    IReadOnlySet<MaintenanceActionKind> EnabledActions,
    IReadOnlySet<ObservationKind> AvailableObservationKernels,
    IReadOnlyDictionary<MaintenanceActionKind, Exposure> Durations,
    bool ResetModelAvailable,
    bool ConsequenceProfileAvailable,
    bool ModelsCompatible,
    InspectionExposureMode? InspectionMode);

public sealed record ActionAdmissibilityResult(bool IsAdmissible, ActionAdmissibilityReason Reason);

public sealed record FailureEvent(bool Failed, Exposure? ExactExposure, Exposure? LowerExposure, Exposure? UpperExposure, CensoringKind Censoring, string Reason);

public sealed record ReliabilityEvaluation(
    EvaluationStatus Status,
    FailureProbability? FailureProbability,
    Reliability? Reliability,
    Probability? FailureLowerNumericalBound,
    Probability? FailureUpperNumericalBound,
    NumericalDiagnostics Diagnostics,
    ArtifactHash? BatchEvidenceHash);

public sealed record ReliabilityEvaluationRequest<TState>(
    DecisionId DecisionId,
    IDegradationBelief<TState> Belief,
    IStateTransitionKernel<TState> TransitionKernel,
    IResetKernel<TState> ResetKernel,
    IFunctionalLimitState<TState> LimitState,
    PolicyTree Policy,
    Horizon Horizon,
    NumericalSettings Numerical,
    RandomStreamContext Streams,
    FailureProbability? ConstraintTolerance = null);

public sealed record ConsequenceDefinition(string Version, IReadOnlyDictionary<ConsequenceComponentKind, string> Definitions);

public sealed record PathOutcome(
    bool FailureOccurred,
    bool PlannedReplacementOccurred,
    bool IntensifiedMonitoringOccurred,
    bool TargetedInspectionOccurred,
    double DowntimeBurden);

public sealed record ConsequenceVector(IReadOnlyDictionary<ConsequenceComponentKind, double> Components);

public sealed record ConsequenceWeightProfile(
    string ProfileId,
    string OwnerId,
    WeightProfileStatus Status,
    string Version,
    DateOnly? EffectiveDate,
    IReadOnlyDictionary<ConsequenceComponentKind, double> Weights,
    ArtifactHash ProvenanceHash);

public sealed record ExpectedDisutilityEvaluation(
    EvaluationStatus Status,
    Disutility? ExpectedDisutility,
    IReadOnlyDictionary<ConsequenceComponentKind, double> ComponentExpectations,
    NumericalStandardError StandardError,
    long SampleCount,
    string Diagnostics);

public sealed record FeasibilityResult(FeasibilityClassification Classification, double ViolationMargin, string Reason);

public sealed record CompletePolicyEvaluation(
    PolicyTree Policy,
    ActionAdmissibilityResult Admissibility,
    ReliabilityEvaluation Reliability,
    ExpectedDisutilityEvaluation Disutility,
    FeasibilityResult OverallFeasibility,
    FeasibilityResult? BranchFeasibility,
    double InterventionBurden,
    ConfigurationHash ConfigurationHash,
    EvaluationStatus Status,
    IReadOnlyList<string> Warnings);

public sealed record DecisionResult(
    DecisionStatus Status,
    CompletePolicyEvaluation? Selected,
    IReadOnlyList<CompletePolicyEvaluation> Competitors,
    string Reason);

public sealed record EvsiEvaluation(
    EvaluationStatus Status,
    double? Evsi,
    Disutility? NoInformationValue,
    Disutility? InformationActionValue,
    Disutility? AcquisitionBurden,
    Probability? DecisionChangeProbability,
    NumericalDiagnostics Diagnostics);

public sealed record AdmissionResult(EvaluationStatus Status, string Reason);
public sealed record CompatibilityManifestReference(ArtifactHash Hash);
public sealed record PosteriorArtifactReference(string ArtifactId, PosteriorVersionId Version, ArtifactHash Hash);
public sealed record ExecutedActionRecord(ActionId ActionId, MaintenanceActionKind Kind, Exposure Start, Exposure End);
public sealed record MaintenanceResetRecord(ComponentEpisodeId OldEpisode, ComponentEpisodeId NewEpisode, Exposure Exposure);

public sealed record DeployableDecisionInput<TObservationContext>(
    DecisionId DecisionId,
    ComponentEpisodeId ComponentEpisodeId,
    Exposure DecisionExposure,
    Horizon Horizon,
    PosteriorArtifactReference Posterior,
    TObservationContext ObservationContext,
    IReadOnlyList<ExecutedActionRecord> ActionHistory,
    IReadOnlyList<MaintenanceResetRecord> MaintenanceHistory,
    CompatibilityManifestReference Compatibility,
    ConfigurationHash ConfigurationHash,
    int OracleAccessCount = 0);

public sealed record SplitAssignment(string GroupId, DataPartition Partition);
public sealed record CensoringObservation(CensoringKind Kind, Exposure? Exact, Exposure? Lower, Exposure? Upper);
public sealed record CensoringEvaluation(EvaluationStatus Status, bool? Failure, double? Weight, string Reason);
public sealed record PairedObservation(string PairId, string Stratum, double Proposed, double Baseline);
public sealed record BootstrapSample(int Replicate, IReadOnlyList<PairedObservation> Observations);

public sealed record ToleranceDefinition(string Id, string Category, string Rule, bool Scientific);

public sealed record ExperimentMatrixRow(
    ExperimentId ExperimentId,
    string Family,
    RunStage Stage,
    string AuthorizationStatus,
    ScenarioId ScenarioId,
    bool PairedDesign);

public sealed record ValidationIssue(string Code, EvaluationStatus Status, string Message);

public sealed record PreflightResult(EvaluationStatus Status, IReadOnlyList<ValidationIssue> Issues)
{
    public bool Accepted => Status == EvaluationStatus.Success && Issues.Count == 0;
}
