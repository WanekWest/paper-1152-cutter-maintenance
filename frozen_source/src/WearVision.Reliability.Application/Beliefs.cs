using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public sealed class ImmutableWeightedSampleBelief<TState> : IDegradationBelief<TState>
{
    private readonly IReadOnlyList<WeightedState<TState>> _support;

    public ImmutableWeightedSampleBelief(
        PosteriorVersionId version,
        StateSpaceDescriptor stateSpace,
        ArtifactHash contentHash,
        IReadOnlyList<WeightedState<TState>> support)
    {
        Version = version;
        StateSpace = stateSpace ?? throw new ArgumentNullException(nameof(stateSpace));
        ContentHash = contentHash;
        _support = support?.ToArray() ?? throw new ArgumentNullException(nameof(support));
    }

    public PosteriorVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; }
    public ArtifactHash ContentHash { get; }

    public BeliefValidationResult Validate()
    {
        var issues = new List<string>();
        if (_support.Count == 0) issues.Add("EMPTY_SUPPORT");
        var mass = 0d;
        foreach (var item in _support)
        {
            if (!double.IsFinite(item.Weight) || item.Weight < 0d) issues.Add("INVALID_WEIGHT");
            else mass += item.Weight;
            if (item.State is null) issues.Add("NULL_STATE");
        }

        if (!double.IsFinite(mass) || Math.Abs(mass - 1d) > 1e-12) issues.Add("MASS_NOT_ONE:TOL-BELIEF-NORM");
        return new(issues.Count == 0 ? EvaluationStatus.Success : EvaluationStatus.InvalidPosterior, mass, issues);
    }

    public TState Sample(IRandomStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var validation = Validate();
        if (!validation.IsValid) throw new InvalidOperationException("Cannot sample an invalid posterior.");
        var u = stream.NextUnitDouble();
        var cumulative = 0d;
        for (var i = 0; i < _support.Count; i++)
        {
            cumulative += _support[i].Weight;
            if (u < cumulative || i == _support.Count - 1) return _support[i].State;
        }

        throw new InvalidOperationException("Validated posterior sampling failed.");
    }

    public bool TryGetWeightedSupport(out IReadOnlyList<WeightedState<TState>> support)
    {
        support = _support;
        return true;
    }

    public bool TryGetParametricDescriptor(out ParametricBeliefDescriptor descriptor)
    {
        descriptor = null!;
        return false;
    }
}

public sealed class ParametricBeliefAdapter<TState> : IDegradationBelief<TState>
{
    private readonly Func<IRandomStream, TState> _sampler;

    public ParametricBeliefAdapter(
        PosteriorVersionId version,
        StateSpaceDescriptor stateSpace,
        ArtifactHash contentHash,
        ParametricBeliefDescriptor descriptor,
        Func<IRandomStream, TState> sampler)
    {
        Version = version;
        StateSpace = stateSpace;
        ContentHash = contentHash;
        Descriptor = descriptor;
        _sampler = sampler;
    }

    public PosteriorVersionId Version { get; }
    public StateSpaceDescriptor StateSpace { get; }
    public ArtifactHash ContentHash { get; }
    public ParametricBeliefDescriptor Descriptor { get; }
    public BeliefValidationResult Validate() => new(EvaluationStatus.Success, 1d, Array.Empty<string>());
    public TState Sample(IRandomStream stream) => _sampler(stream);
    public bool TryGetWeightedSupport(out IReadOnlyList<WeightedState<TState>> support) { support = Array.Empty<WeightedState<TState>>(); return false; }
    public bool TryGetParametricDescriptor(out ParametricBeliefDescriptor descriptor) { descriptor = Descriptor; return true; }
}

public sealed class WeightedSupportBayesUpdater<TState, TObservation> : IBeliefUpdater<TState, TObservation>
{
    private readonly Func<IReadOnlyList<WeightedState<TState>>, ArtifactHash> _contentHasher;

    public WeightedSupportBayesUpdater(Func<IReadOnlyList<WeightedState<TState>>, ArtifactHash> contentHasher) => _contentHasher = contentHasher;

    public BeliefUpdateResult<TState> Update(
        IDegradationBelief<TState> predictivePrior,
        TObservation observation,
        IObservationKernel<TState, TObservation> likelihood,
        ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(predictivePrior);
        ArgumentNullException.ThrowIfNull(likelihood);
        if (predictivePrior.StateSpace.Id != likelihood.StateSpace.Id)
            return new(EvaluationStatus.IncompatibleModel, null, "STATE_SPACE_MISMATCH");
        if (!predictivePrior.TryGetWeightedSupport(out var support) || !predictivePrior.Validate().IsValid)
            return new(EvaluationStatus.InvalidPosterior, null, "WEIGHTED_SUPPORT_REQUIRED");

        var logWeights = new double[support.Count];
        var maximum = double.NegativeInfinity;
        for (var i = 0; i < support.Count; i++)
        {
            var logLikelihood = likelihood.LogLikelihood(observation, support[i].State, context);
            logWeights[i] = support[i].Weight > 0d && double.IsFinite(logLikelihood)
                ? Math.Log(support[i].Weight) + logLikelihood
                : double.NegativeInfinity;
            maximum = Math.Max(maximum, logWeights[i]);
        }

        if (!double.IsFinite(maximum)) return new(EvaluationStatus.NotEvaluable, null, "ZERO_OR_NONFINITE_BAYES_SUPPORT");
        var sum = 0d;
        for (var i = 0; i < logWeights.Length; i++) if (double.IsFinite(logWeights[i])) sum += Math.Exp(logWeights[i] - maximum);
        if (!double.IsFinite(sum) || sum <= 0d) return new(EvaluationStatus.NotEvaluable, null, "INVALID_BAYES_DENOMINATOR");

        var normalized = new WeightedState<TState>[support.Count];
        for (var i = 0; i < support.Count; i++)
        {
            var weight = double.IsFinite(logWeights[i]) ? Math.Exp(logWeights[i] - maximum) / sum : 0d;
            normalized[i] = new(support[i].State, weight);
        }

        var version = new PosteriorVersionId($"{predictivePrior.Version.Value}.updated");
        var posterior = new ImmutableWeightedSampleBelief<TState>(version, predictivePrior.StateSpace, _contentHasher(normalized), normalized);
        return posterior.Validate().IsValid
            ? new(EvaluationStatus.Success, posterior, "UPDATED")
            : new(EvaluationStatus.InvalidPosterior, null, "POSTERIOR_NORMALIZATION_FAILED");
    }
}

public sealed class DeployableInformationAdmission : IDeployableInformationAdmission
{
    public AdmissionResult Admit<TObservationContext>(DeployableDecisionInput<TObservationContext> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.OracleAccessCount != 0) return new(EvaluationStatus.OracleLeakageDetected, "ORACLE_ACCESS_COUNT_NONZERO");
        if (input.Horizon.Value <= 0d) return new(EvaluationStatus.MissingRequiredInput, "INVALID_HORIZON");
        return new(EvaluationStatus.Success, "ADMITTED");
    }
}
