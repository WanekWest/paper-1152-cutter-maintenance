using System.Globalization;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public interface IDeterministicReliabilityFallback<TState>
{
    ReliabilityEvaluation? TryEvaluate(
        ReliabilityEvaluationRequest<TState> request,
        long monteCarloPaths,
        int monteCarloBatches);
}

public sealed class DeterministicScalarUniformFirstPassageFallback : IDeterministicReliabilityFallback<double>
{
    public const string ProtocolVersion = "WV-1152-CHANCE-CERT-2";
    public const string MethodId = "deterministic-scalar-uniform-first-passage-v2";

    private readonly double _certifiedAbsoluteErrorBound;

    public DeterministicScalarUniformFirstPassageFallback(double certifiedAbsoluteErrorBound)
    {
        if (!double.IsFinite(certifiedAbsoluteErrorBound) || certifiedAbsoluteErrorBound <= 0d)
            throw new ArgumentOutOfRangeException(nameof(certifiedAbsoluteErrorBound));
        _certifiedAbsoluteErrorBound = certifiedAbsoluteErrorBound;
    }

    public ReliabilityEvaluation? TryEvaluate(
        ReliabilityEvaluationRequest<double> request,
        long monteCarloPaths,
        int monteCarloBatches)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConstraintTolerance is null ||
            request.TransitionKernel is not ExposureScaledUniformRateTransition transition ||
            request.LimitState is not AffineScalarLimitState limitState)
            return null;

        IReadOnlyList<WeightedState<double>> support;
        if (request.Policy.FirstAction == MaintenanceActionKind.PlannedReplacement)
        {
            if (request.ResetKernel is not TwoPointScalarResetKernel reset || !reset.TryGetWeightedSupport(out support))
                return null;
        }
        else if (!request.Belief.TryGetWeightedSupport(out support))
        {
            return null;
        }

        var probability = 0d;
        foreach (var item in support)
        {
            if (!double.IsFinite(item.State) || !double.IsFinite(item.Weight) || item.Weight < 0d)
                return null;
            probability += item.Weight * ConditionalFailureProbability(
                item.State,
                limitState.Boundary,
                transition.MaximumRatePerUnitExposure,
                request.Horizon.Value);
        }

        probability = Math.Clamp(probability, 0d, 1d);
        var lower = Math.Max(0d, probability - _certifiedAbsoluteErrorBound);
        var upper = Math.Min(1d, probability + _certifiedAbsoluteErrorBound);
        var epsilon = request.ConstraintTolerance.Value.Value;
        var boundary = lower <= epsilon && epsilon < upper;
        var status = boundary ? EvaluationStatus.BoundaryUncertified : EvaluationStatus.Success;
        var warningCodes = boundary
            ? new[] { "DETERMINISTIC_BOUNDARY_UNCERTIFIED" }
            : new[] { "DETERMINISTIC_SCALAR_FALLBACK" };
        var toleranceId = FormattableString.Invariant(
            $"{ProtocolVersion};ABS_ERROR_BOUND={_certifiedAbsoluteErrorBound:R}");
        var diagnostics = new NumericalDiagnostics(
            MethodId,
            monteCarloPaths,
            monteCarloBatches,
            new(0d),
            new(0d),
            new(lower),
            new(upper),
            true,
            toleranceId,
            warningCodes);
        return new(
            status,
            new(probability),
            new(1d - probability),
            new(lower),
            new(upper),
            diagnostics,
            null);
    }

    private static double ConditionalFailureProbability(
        double initialState,
        double boundary,
        double maximumRatePerUnitExposure,
        double horizon)
    {
        if (initialState >= boundary) return 1d;
        var maximumIncrement = maximumRatePerUnitExposure * horizon;
        if (maximumIncrement <= 0d) return 0d;
        var normalizedCrossing = (boundary - initialState) / maximumIncrement;
        if (normalizedCrossing >= 1d) return 0d;
        if (normalizedCrossing <= 0d) return 1d;
        return 1d - normalizedCrossing;
    }
}
