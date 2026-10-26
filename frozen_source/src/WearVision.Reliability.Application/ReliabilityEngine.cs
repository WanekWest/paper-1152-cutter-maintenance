using WearVision.Reliability.Domain;
using ReliabilityValue = WearVision.Reliability.Domain.Reliability;

namespace WearVision.Reliability.Application;

public sealed class FirstPassageDetector<TState>
{
    public FailureEvent Detect(TState initialState, Exposure initialExposure, TransitionSegment<TState> segment, IFunctionalLimitState<TState> limitState)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(limitState);
        if (limitState.Margin(initialState) <= 0d)
            return new(true, initialExposure, initialExposure, initialExposure, CensoringKind.ExactFailure, "INITIAL_STATE_FAILED");

        Exposure? previous = initialExposure;
        foreach (var checkpoint in segment.Checkpoints.OrderBy(c => c.Exposure.Value))
        {
            if (limitState.Margin(checkpoint.State) <= 0d)
                return new(true, checkpoint.Exposure, previous, checkpoint.Exposure, CensoringKind.ExactFailure, "FIRST_PASSAGE");
            previous = checkpoint.Exposure;
        }

        var end = segment.Checkpoints.Count == 0 ? initialExposure : segment.Checkpoints.MaxBy(c => c.Exposure.Value).Exposure;
        return new(false, null, end, null, CensoringKind.Administrative, "NO_FAILURE_THROUGH_HORIZON");
    }
}

public static class WilsonInterval
{
    public const double OneSided99Z = 2.3263478740408408d;

    public static (double Lower, double Upper) Calculate(long successes, long trials, double z = OneSided99Z)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trials);
        if (successes < 0 || successes > trials) throw new ArgumentOutOfRangeException(nameof(successes));
        var n = (double)trials;
        var p = successes / n;
        var z2 = z * z;
        var denominator = 1d + z2 / n;
        var centre = (p + z2 / (2d * n)) / denominator;
        var radius = z * Math.Sqrt((p * (1d - p) / n) + z2 / (4d * n * n)) / denominator;
        return (Math.Max(0d, centre - radius), Math.Min(1d, centre + radius));
    }
}

public sealed record NumericalBatchSnapshot(
    DecisionId DecisionId,
    PolicyId PolicyId,
    int BatchIndex,
    long PathIndexStart,
    long PathIndexEnd,
    long BatchFailureCount,
    long CumulativeFailureCount,
    long CumulativePathCount,
    double FailureEstimate,
    double FailureLowerBound,
    double FailureUpperBound,
    FeasibilityClassification Classification,
    int StableBatchCount);

public sealed class PosteriorPredictiveFirstPassageEstimator<TState> : IFirstPassageReliabilityEstimator<TState>
{
    private static readonly string[] MaximumPathWarnings = ["MAX_PATHS_WITHOUT_STABLE_CLASSIFICATION"];
    private readonly IRandomStreamFactory _streams;
    private readonly FirstPassageDetector<TState> _detector;
    private readonly Action<NumericalBatchSnapshot>? _batchObserver;
    private readonly IDeterministicReliabilityFallback<TState>? _deterministicFallback;
    private readonly int? _fallbackEscalationPaths;

    public PosteriorPredictiveFirstPassageEstimator(
        IRandomStreamFactory streams,
        FirstPassageDetector<TState>? detector = null,
        Action<NumericalBatchSnapshot>? batchObserver = null,
        IDeterministicReliabilityFallback<TState>? deterministicFallback = null,
        int? fallbackEscalationPaths = null)
    {
        _streams = streams;
        _detector = detector ?? new FirstPassageDetector<TState>();
        _batchObserver = batchObserver;
        if ((deterministicFallback is null) != (fallbackEscalationPaths is null))
            throw new ArgumentException("Deterministic fallback and escalation threshold must be configured together.");
        if (fallbackEscalationPaths is <= 0)
            throw new ArgumentOutOfRangeException(nameof(fallbackEscalationPaths));
        _deterministicFallback = deterministicFallback;
        _fallbackEscalationPaths = fallbackEscalationPaths;
    }

    public ReliabilityEvaluation Estimate(ReliabilityEvaluationRequest<TState> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Math.Abs(request.Policy.Horizon.Value - request.Horizon.Value) > request.Numerical.ProbabilityTolerance)
            return Failure(EvaluationStatus.ProtocolViolation, request.Numerical, "POLICY_HORIZON_MISMATCH");
        var beliefValidation = request.Belief.Validate();
        if (!beliefValidation.IsValid) return Failure(EvaluationStatus.InvalidPosterior, request.Numerical, "INVALID_POSTERIOR");
        if (request.Belief.StateSpace.Id != request.TransitionKernel.StateSpace.Id || request.Belief.StateSpace.Id != request.ResetKernel.StateSpace.Id)
            return Failure(EvaluationStatus.IncompatibleModel, request.Numerical, "STATE_SPACE_MISMATCH");

        long failures = 0;
        var total = 0;
        var batches = 0;
        var stable = 0;
        FeasibilityClassification? previous = null;
        var lower = 0d;
        var upper = 1d;
        var converged = false;

        while (total < request.Numerical.MaximumPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = total == 0 ? request.Numerical.MinimumPaths : Math.Min(request.Numerical.BatchSize, request.Numerical.MaximumPaths - total);
            long batchFailures = 0;
            var pathIndexStart = total;
            for (var local = 0; local < target; local++)
            {
                var pathIndex = checked((ulong)(total + local));
                var initial = request.Belief.Sample(_streams.Create(new(request.Streams, StreamPurpose.PosteriorDraw, pathIndex)));
                var pathInitial = request.Policy.FirstAction == MaintenanceActionKind.PlannedReplacement
                    ? request.ResetKernel.SampleReset(new(new("episode-before"), new("episode-after"), new Exposure(0d)), _streams.Create(new(request.Streams, StreamPurpose.ReplacementReset, pathIndex)))
                    : initial;
                var interval = new ExposureInterval(new Exposure(0d), new Exposure(request.Horizon.Value));
                var action = new PhysicalActionContext(request.Policy.FirstAction, new Exposure(0d), null);
                var segment = request.TransitionKernel.SampleSegment(pathInitial, interval, action, null, _streams.Create(new(request.Streams, StreamPurpose.DegradationInnovation, pathIndex)));
                if (_detector.Detect(pathInitial, new Exposure(0d), segment, request.LimitState).Failed)
                {
                    failures++;
                    batchFailures++;
                }
            }

            total += target;
            batches++;
            (lower, upper) = WilsonInterval.Calculate(failures, total);
            if (total < request.Numerical.MinimumPaths) continue;
            var classification = request.ConstraintTolerance is null
                ? FeasibilityClassification.ProvenFeasible
                : upper <= request.ConstraintTolerance.Value.Value
                    ? FeasibilityClassification.ProvenFeasible
                    : lower > request.ConstraintTolerance.Value.Value
                        ? FeasibilityClassification.ProvenInfeasible
                        : FeasibilityClassification.Unresolved;
            stable = classification != FeasibilityClassification.Unresolved && classification == previous ? stable + 1 : classification == FeasibilityClassification.Unresolved ? 0 : 1;
            previous = classification;
            _batchObserver?.Invoke(new(
                request.DecisionId,
                request.Policy.Id,
                batches,
                pathIndexStart,
                total - 1L,
                batchFailures,
                failures,
                total,
                failures / (double)total,
                lower,
                upper,
                classification,
                stable));
            if (stable >= request.Numerical.RequiredStableBatches)
            {
                converged = true;
                break;
            }
            if (_deterministicFallback is not null && total >= _fallbackEscalationPaths!.Value)
            {
                var deterministic = _deterministicFallback.TryEvaluate(request, total, batches);
                if (deterministic is not null) return deterministic;
            }
        }

        var p = failures / (double)total;
        var se = Math.Sqrt(Math.Max(0d, p * (1d - p) / total));
        var diagnostics = new NumericalDiagnostics(
            "posterior-predictive-first-passage-v1",
            total,
            batches,
            new(se),
            new(0d),
            new Probability(lower),
            new Probability(upper),
            converged,
            request.Numerical.ProfileId,
            converged ? Array.Empty<string>() : MaximumPathWarnings);
        return new(
            converged ? EvaluationStatus.Success : EvaluationStatus.NumericalNonConvergence,
            new FailureProbability(p),
            new ReliabilityValue(1d - p),
            new Probability(lower),
            new Probability(upper),
            diagnostics,
            null);
    }

    private static ReliabilityEvaluation Failure(EvaluationStatus status, NumericalSettings settings, string warning) => new(
        status,
        null,
        null,
        null,
        null,
        new("posterior-predictive-first-passage-v1", 0, 0, new(0), new(0), null, null, false, settings.ProfileId, new[] { warning }),
        null);
}

public sealed class WeightedSupportFirstPassageEstimator<TState> : IFirstPassageReliabilityEstimator<TState>
{
    private sealed class ZeroStream : IRandomStream
    {
        public ulong NextUInt64() => 0UL;
        public double NextUnitDouble() => 0d;
    }

    private readonly FirstPassageDetector<TState> _detector = new();

    public ReliabilityEvaluation Estimate(ReliabilityEvaluationRequest<TState> request, CancellationToken cancellationToken)
    {
        if (Math.Abs(request.Policy.Horizon.Value - request.Horizon.Value) > request.Numerical.ProbabilityTolerance)
            return new(EvaluationStatus.ProtocolViolation, null, null, null, null, new("weighted-support-exact-v1", 0, 0, new(0), new(0), null, null, false, "TOL-ANALYTIC", ["POLICY_HORIZON_MISMATCH"]), null);
        if (!request.Belief.TryGetWeightedSupport(out var support) || !request.Belief.Validate().IsValid)
            return new(EvaluationStatus.NotEvaluable, null, null, null, null, new("weighted-support-exact-v1", 0, 0, new(0), new(0), null, null, false, "TOL-ANALYTIC", ["WEIGHTED_SUPPORT_REQUIRED"]), null);
        var failedMass = 0d;
        var stream = new ZeroStream();
        foreach (var item in support)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = request.Policy.FirstAction == MaintenanceActionKind.PlannedReplacement
                ? request.ResetKernel.SampleReset(new(new("episode-before"), new("episode-after"), new Exposure(0d)), stream)
                : item.State;
            var segment = request.TransitionKernel.SampleSegment(
                state,
                new(new Exposure(0d), new Exposure(request.Horizon.Value)),
                new(request.Policy.FirstAction, new Exposure(0d), null),
                null,
                stream);
            if (_detector.Detect(state, new Exposure(0d), segment, request.LimitState).Failed) failedMass += item.Weight;
        }
        var p = Math.Clamp(failedMass, 0d, 1d);
        return new(
            EvaluationStatus.Success,
            new(p),
            new ReliabilityValue(1d - p),
            new(p),
            new(p),
            new("weighted-support-exact-v1", support.Count, 1, new(0), new(0), new(p), new(p), true, "TOL-ANALYTIC", Array.Empty<string>()),
            null);
    }
}

public sealed class ReliabilityFeasibilityEvaluator : IReliabilityFeasibilityEvaluator
{
    public FeasibilityResult Evaluate(ReliabilityEvaluation reliability, FailureProbability tolerance)
    {
        if (reliability.Status == EvaluationStatus.BoundaryUncertified)
            return new(FeasibilityClassification.BoundaryUncertified, reliability.FailureProbability?.Value - tolerance.Value ?? double.NaN, "BOUNDARY_UNCERTIFIED");
        if (reliability.Status == EvaluationStatus.NumericalNonConvergence)
            return new(FeasibilityClassification.Unresolved, double.NaN, "NUMERICAL_NONCONVERGENCE");
        if (reliability.FailureProbability is null || reliability.FailureLowerNumericalBound is null || reliability.FailureUpperNumericalBound is null)
            return new(FeasibilityClassification.Unresolved, double.NaN, "MISSING_NUMERICAL_INTERVAL");
        if (reliability.FailureUpperNumericalBound.Value.Value <= tolerance.Value)
            return new(FeasibilityClassification.ProvenFeasible, reliability.FailureProbability.Value.Value - tolerance.Value, "UCB_WITHIN_TOLERANCE");
        if (reliability.FailureLowerNumericalBound.Value.Value > tolerance.Value)
            return new(FeasibilityClassification.ProvenInfeasible, reliability.FailureProbability.Value.Value - tolerance.Value, "LCB_ABOVE_TOLERANCE");
        return new(FeasibilityClassification.Unresolved, reliability.FailureProbability.Value.Value - tolerance.Value, "INTERVAL_STRADDLES_TOLERANCE");
    }
}
