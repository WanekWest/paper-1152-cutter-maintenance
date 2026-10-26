using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public enum MissingObservationMode
{
    None,
    NonInformative,
    Informative
}

public sealed record DiscreteObservation(string OutcomeId, int? BinaryValue, bool Missing);

internal static class RuntimeModelGuards
{
    public static double Finite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        return value;
    }

    public static double UnitInterval(double value, string name)
    {
        Finite(value, name);
        if (value is < 0d or > 1d) throw new ArgumentOutOfRangeException(name, "Value must be in [0,1].");
        return value;
    }

    public static string Id(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}

public sealed class AffineScalarLimitState : IFunctionalLimitState<double>
{
    private readonly double _boundary;

    public AffineScalarLimitState(double boundary, string version, UnitDescriptor marginUnit, string evidenceId)
    {
        _boundary = RuntimeModelGuards.Finite(boundary, nameof(boundary));
        Version = new(version);
        MarginUnit = marginUnit ?? throw new ArgumentNullException(nameof(marginUnit));
        CalibrationEvidenceId = RuntimeModelGuards.Id(evidenceId, nameof(evidenceId));
    }

    public LimitStateVersionId Version { get; }
    public UnitDescriptor MarginUnit { get; }
    public string CalibrationEvidenceId { get; }
    public double Boundary => _boundary;
    public double Margin(double state) => _boundary - RuntimeModelGuards.Finite(state, nameof(state));
}

public sealed class ExposureScaledUniformRateTransition : IStateTransitionKernel<double>
{
    private readonly double _maximumRatePerUnitExposure;

    public ExposureScaledUniformRateTransition(
        double maximumRatePerUnitExposure,
        string version,
        StateSpaceDescriptor stateSpace,
        string exposureClockId)
    {
        _maximumRatePerUnitExposure = RuntimeModelGuards.Finite(maximumRatePerUnitExposure, nameof(maximumRatePerUnitExposure));
        ArgumentOutOfRangeException.ThrowIfNegative(_maximumRatePerUnitExposure);
        Version = new(version);
        StateSpace = stateSpace ?? throw new ArgumentNullException(nameof(stateSpace));
        ExposureClockId = RuntimeModelGuards.Id(exposureClockId, nameof(exposureClockId));
    }

    public ModelVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; }
    public string ExposureClockId { get; }
    public double MaximumRatePerUnitExposure => _maximumRatePerUnitExposure;
    public TransitionCapabilities Capabilities { get; } = new(false, false, false);

    public TransitionSegment<double> SampleSegment(
        double initialState,
        ExposureInterval interval,
        PhysicalActionContext actionContext,
        ObservedOperatingContext? operatingContext,
        IRandomStream stream)
    {
        ArgumentNullException.ThrowIfNull(actionContext);
        ArgumentNullException.ThrowIfNull(stream);
        if (interval.Length < 0d) throw new ArgumentOutOfRangeException(nameof(interval), "Exposure interval must be ordered.");
        var physicalExposure = actionContext.Action == MaintenanceActionKind.TargetedInspection && actionContext.InspectionMode == InspectionExposureMode.Paused
            ? 0d
            : interval.Length;
        var rate = _maximumRatePerUnitExposure * stream.NextUnitDouble();
        var endState = RuntimeModelGuards.Finite(initialState, nameof(initialState)) + rate * physicalExposure;
        return new(
            [new StateCheckpoint<double>(interval.End, endState)],
            false,
            "uniform-rate-per-segment-exposure-scaled-v1");
    }

    public bool TryEvaluateLogDensity(TransitionDensityQuery<double> query, out double logDensity)
    {
        logDensity = double.NaN;
        return false;
    }
}

public sealed class TwoPointScalarResetKernel : IResetKernel<double>
{
    private readonly double _first;
    private readonly double _second;
    private readonly double _firstProbability;

    public TwoPointScalarResetKernel(
        double first,
        double second,
        double firstProbability,
        string version,
        StateSpaceDescriptor stateSpace)
    {
        _first = RuntimeModelGuards.Finite(first, nameof(first));
        _second = RuntimeModelGuards.Finite(second, nameof(second));
        _firstProbability = RuntimeModelGuards.UnitInterval(firstProbability, nameof(firstProbability));
        Version = new(version);
        StateSpace = stateSpace ?? throw new ArgumentNullException(nameof(stateSpace));
    }

    public ModelVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; }
    public double SampleReset(ResetContext context, IRandomStream stream) => stream.NextUnitDouble() < _firstProbability ? _first : _second;

    public bool TryGetWeightedSupport(out IReadOnlyList<WeightedState<double>> support)
    {
        support =
        [
            new(_first, _firstProbability),
            new(_second, 1d - _firstProbability)
        ];
        return true;
    }
}

public sealed class StateConditionedBinaryObservationKernel : IObservationKernel<double, DiscreteObservation>
{
    private readonly double _stateThreshold;
    private readonly double _probabilityOneLow;
    private readonly double _probabilityOneHigh;
    private readonly double _missingProbabilityLow;
    private readonly double _missingProbabilityHigh;

    public StateConditionedBinaryObservationKernel(
        string version,
        ObservationKind kind,
        StateSpaceDescriptor stateSpace,
        double stateThreshold,
        double probabilityOneLow,
        double probabilityOneHigh,
        MissingObservationMode missingMode,
        double missingProbabilityLow,
        double missingProbabilityHigh)
    {
        Version = new(version);
        Kind = kind;
        StateSpace = stateSpace ?? throw new ArgumentNullException(nameof(stateSpace));
        _stateThreshold = RuntimeModelGuards.Finite(stateThreshold, nameof(stateThreshold));
        _probabilityOneLow = RuntimeModelGuards.UnitInterval(probabilityOneLow, nameof(probabilityOneLow));
        _probabilityOneHigh = RuntimeModelGuards.UnitInterval(probabilityOneHigh, nameof(probabilityOneHigh));
        MissingMode = missingMode;
        _missingProbabilityLow = RuntimeModelGuards.UnitInterval(missingProbabilityLow, nameof(missingProbabilityLow));
        _missingProbabilityHigh = RuntimeModelGuards.UnitInterval(missingProbabilityHigh, nameof(missingProbabilityHigh));
        if (missingMode == MissingObservationMode.None && (_missingProbabilityLow != 0d || _missingProbabilityHigh != 0d))
            throw new ArgumentException("Missing probabilities must be zero when missingness is disabled.");
        if (missingMode == MissingObservationMode.NonInformative && Math.Abs(_missingProbabilityLow - _missingProbabilityHigh) > 1e-12)
            throw new ArgumentException("Non-informative missingness requires equal state-conditioned probabilities.");
    }

    public ModelVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; }
    public ObservationKind Kind { get; }
    public MissingObservationMode MissingMode { get; }

    public DiscreteObservation Sample(double state, ObservationContext context, IRandomStream stream)
    {
        var missing = MissingProbability(state);
        var u = stream.NextUnitDouble();
        if (u < missing) return new("missing", null, true);
        var adjusted = missing >= 1d ? 0d : (u - missing) / (1d - missing);
        return adjusted < ProbabilityOfOne(state)
            ? new("one", 1, false)
            : new("zero", 0, false);
    }

    public double LogLikelihood(DiscreteObservation observation, double state, ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Missing)
        {
            if (MissingMode == MissingObservationMode.None) return double.NegativeInfinity;
            if (MissingMode == MissingObservationMode.NonInformative) return 0d;
            return LogProbability(MissingProbability(state));
        }

        if (observation.BinaryValue is not (0 or 1)) return double.NegativeInfinity;
        var observedProbability = 1d - MissingProbability(state);
        var one = ProbabilityOfOne(state);
        return LogProbability(observedProbability * (observation.BinaryValue == 1 ? one : 1d - one));
    }

    public IReadOnlyList<DiscreteObservation> Outcomes => MissingMode == MissingObservationMode.None
        ? [new("zero", 0, false), new("one", 1, false)]
        : [new("missing", null, true), new("zero", 0, false), new("one", 1, false)];

    public double OutcomeProbability(DiscreteObservation observation, double state)
    {
        if (observation.Missing) return MissingProbability(state);
        var observed = 1d - MissingProbability(state);
        return observed * (observation.BinaryValue == 1 ? ProbabilityOfOne(state) : 1d - ProbabilityOfOne(state));
    }

    private double ProbabilityOfOne(double state) => state < _stateThreshold ? _probabilityOneLow : _probabilityOneHigh;
    private double MissingProbability(double state) => MissingMode == MissingObservationMode.None ? 0d : state < _stateThreshold ? _missingProbabilityLow : _missingProbabilityHigh;
    private static double LogProbability(double value) => value <= 0d ? double.NegativeInfinity : Math.Log(value);
}

public static class ReferenceArtifactHasher
{
    public static ArtifactHash HashText(string value) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));
}
