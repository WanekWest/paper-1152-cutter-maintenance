using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Experiments;

public readonly record struct ScalarState(double Value);
public readonly record struct BinaryObservation(int Value);

public sealed class ScalarLimitState : IFunctionalLimitState<ScalarState>
{
    private readonly double _failureBoundary;
    public ScalarLimitState(double failureBoundary, string fixtureId)
    {
        _failureBoundary = failureBoundary;
        Version = new($"limit.{fixtureId}");
        CalibrationEvidenceId = $"{fixtureId}:TEST_FIXTURE_ONLY";
    }
    public LimitStateVersionId Version { get; }
    public UnitDescriptor MarginUnit { get; } = new("fixture-margin", "TEST_FIXTURE_ONLY dimensionless margin");
    public string CalibrationEvidenceId { get; }
    public double Margin(ScalarState state) => _failureBoundary - state.Value;
}

public sealed class DeterministicScalarTransition : IStateTransitionKernel<ScalarState>
{
    private readonly Func<ScalarState, ExposureInterval, IReadOnlyList<StateCheckpoint<ScalarState>>> _path;
    public DeterministicScalarTransition(string fixtureId, Func<ScalarState, ExposureInterval, IReadOnlyList<StateCheckpoint<ScalarState>>> path)
    {
        _path = path;
        Version = new($"transition.{fixtureId}");
    }
    public ModelVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; } = FixtureCatalog.ScalarStateSpace;
    public TransitionCapabilities Capabilities { get; } = new(false, true, false);
    public TransitionSegment<ScalarState> SampleSegment(ScalarState initialState, ExposureInterval interval, PhysicalActionContext actionContext, ObservedOperatingContext? operatingContext, IRandomStream stream) => new(_path(initialState, interval), true, "fixture-deterministic-segment");
    public bool TryEvaluateLogDensity(TransitionDensityQuery<ScalarState> query, out double logDensity) { logDensity = double.NaN; return false; }
}

public sealed class StochasticScalarTransition : IStateTransitionKernel<ScalarState>
{
    private readonly double _incrementScale;
    public StochasticScalarTransition(double incrementScale) => _incrementScale = incrementScale;
    public ModelVersionId Version { get; } = new("transition.fixture.stochastic");
    public StateSpaceDescriptor StateSpace { get; } = FixtureCatalog.ScalarStateSpace;
    public TransitionCapabilities Capabilities { get; } = new(false, false, false);
    public TransitionSegment<ScalarState> SampleSegment(ScalarState initialState, ExposureInterval interval, PhysicalActionContext actionContext, ObservedOperatingContext? operatingContext, IRandomStream stream)
    {
        var increment = _incrementScale * stream.NextUnitDouble();
        return new(new[] { new StateCheckpoint<ScalarState>(interval.End, new(initialState.Value + increment)) }, false, "fixture-stochastic-segment");
    }
    public bool TryEvaluateLogDensity(TransitionDensityQuery<ScalarState> query, out double logDensity) { logDensity = double.NaN; return false; }
}

public sealed class PointResetKernel : IResetKernel<ScalarState>
{
    private readonly ScalarState _state;
    public PointResetKernel(ScalarState state) => _state = state;
    public ModelVersionId Version { get; } = new("reset.fixture.point");
    public StateSpaceDescriptor StateSpace { get; } = FixtureCatalog.ScalarStateSpace;
    public ScalarState SampleReset(ResetContext context, IRandomStream stream) => _state;
}

public sealed class TwoPointResetKernel : IResetKernel<ScalarState>
{
    private readonly ScalarState _first;
    private readonly ScalarState _second;
    private readonly double _firstProbability;
    public TwoPointResetKernel(ScalarState first, ScalarState second, double firstProbability)
    {
        _first = first; _second = second; _firstProbability = firstProbability;
    }
    public ModelVersionId Version { get; } = new("reset.fixture.two-point");
    public StateSpaceDescriptor StateSpace { get; } = FixtureCatalog.ScalarStateSpace;
    public ScalarState SampleReset(ResetContext context, IRandomStream stream) => stream.NextUnitDouble() < _firstProbability ? _first : _second;
}

public sealed class BinaryObservationKernel : IObservationKernel<ScalarState, BinaryObservation>
{
    private readonly Func<ScalarState, ObservationContext, double> _probabilityOfOne;
    public BinaryObservationKernel(string fixtureId, ObservationKind kind, Func<ScalarState, ObservationContext, double> probabilityOfOne)
    {
        Version = new($"observation.{fixtureId}"); Kind = kind; _probabilityOfOne = probabilityOfOne;
    }
    public ModelVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; } = FixtureCatalog.ScalarStateSpace;
    public ObservationKind Kind { get; }
    public BinaryObservation Sample(ScalarState state, ObservationContext context, IRandomStream stream) => new(stream.NextUnitDouble() < _probabilityOfOne(state, context) ? 1 : 0);
    public double LogLikelihood(BinaryObservation observation, ScalarState state, ObservationContext context)
    {
        if (context.Missing) return context.QualityId == "informative-missing" ? Math.Log(state.Value >= 0.5 ? 0.8 : 0.2) : 0d;
        var p = Math.Clamp(_probabilityOfOne(state, context), 0d, 1d);
        var likelihood = observation.Value == 1 ? p : 1d - p;
        return likelihood <= 0d ? double.NegativeInfinity : Math.Log(likelihood);
    }
}

public sealed class FixedTerminalRule : IConstrainedTerminalRule<BinaryObservation>
{
    public MaintenanceActionKind Select(BinaryObservation observation) => observation.Value == 1 ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue;
}

public static class FixtureCatalog
{
    public const string Label = "SUPPORTING TEST/EXPERIMENT INFRASTRUCTURE — NOT 1152 NOVELTY";
    public static StateSpaceDescriptor ScalarStateSpace { get; } = new("scalar-fixture-state", "fixture-v1", "TEST_FIXTURE_ONLY");
    public static ArtifactHash Hash(string text) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));

    public static ImmutableWeightedSampleBelief<ScalarState> TwoPointBelief(double firstWeight = 0.5) => new(
        new("posterior.fixture.two-point"),
        ScalarStateSpace,
        Hash($"two-point:{firstWeight:R}"),
        new[] { new WeightedState<ScalarState>(new(0.2), firstWeight), new WeightedState<ScalarState>(new(0.8), 1d - firstWeight) });

    public static IReadOnlyList<string> RequiredFixtureIds { get; } = new[]
    {
        "FX-NO-FAILURE", "FX-CERTAIN-FAILURE", "FX-CROSS-RECOVER", "FX-ANALYTIC-FIRST-PASSAGE",
        "FX-TWO-POINT-BELIEF", "FX-HAND-BAYES", "FX-UNINFORMATIVE-OBS", "FX-INFORMATIVE-OBS",
        "FX-MISSING-OBS", "FX-PERFECT-QUALITY-LIMIT", "FX-VARIABLE-RESET", "FX-ALL-FEASIBLE",
        "FX-INFEASIBLE-LOW-J", "FX-EMPTY-FEASIBLE", "FX-NUMERICAL-TIE", "FX-EVSI-ZERO",
        "FX-EVSI-NEGATIVE-BURDEN", "FX-BRANCH-INFEASIBLE", "FX-ORACLE-FIREWALL"
    };
}
