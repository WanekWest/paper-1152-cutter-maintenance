using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Experiments;

public enum BaselineStatus { Success, NotEvaluable, NotApplicable, ProtocolViolation }

public sealed record BaselineDecisionInput<TState>(
    Exposure ExposureSinceReset,
    IDegradationBelief<TState>? Belief,
    double? DeployableObservedProxy,
    Func<TState, double>? ScalarMapping,
    double? FirstPassageQuantile,
    IReadOnlyList<CompletePolicyEvaluation> DirectPolicyEvaluations,
    bool LockedTest);

public sealed record BaselineDecision(BaselineStatus Status, MaintenanceActionKind? Action, string Reason, int OracleAccessCount = 0);

public interface IBaselinePolicy<TState>
{
    string BaselineId { get; }
    BaselineDecision Evaluate(BaselineDecisionInput<TState> input);
}

public sealed class RunToFailureBaseline<TState> : IBaselinePolicy<TState>
{
    public string BaselineId => "B0-RUN-TO-FAILURE";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input) => new(BaselineStatus.Success, MaintenanceActionKind.Continue, "PREFAILURE_CONTINUE");
}

public sealed class PeriodicReplacementBaseline<TState> : IBaselinePolicy<TState>
{
    private readonly double? _designInterval;
    public PeriodicReplacementBaseline(double? designInterval) => _designInterval = designInterval;
    public string BaselineId => "B1-PERIODIC";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        if (_designInterval is null || _designInterval <= 0d) return new(BaselineStatus.NotEvaluable, null, "MISSING_DESIGN_INTERVAL");
        return new(BaselineStatus.Success, input.ExposureSinceReset.Value >= _designInterval ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue, "DESIGN_INTERVAL_FROZEN");
    }
}

public sealed class ObservedProxyThresholdBaseline<TState> : IBaselinePolicy<TState>
{
    private readonly double? _designThreshold;
    public ObservedProxyThresholdBaseline(double? designThreshold) => _designThreshold = designThreshold;
    public string BaselineId => "B2-OBSERVED-PROXY";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        if (_designThreshold is null || input.DeployableObservedProxy is null) return new(BaselineStatus.NotApplicable, null, "DEPLOYABLE_PROXY_OR_THRESHOLD_UNAVAILABLE");
        return new(BaselineStatus.Success, input.DeployableObservedProxy >= _designThreshold ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue, "DEPLOYABLE_PROXY_ONLY");
    }
}

public sealed class PosteriorMeanBaseline<TState> : IBaselinePolicy<TState>
{
    private readonly double? _threshold;
    public PosteriorMeanBaseline(double? threshold) => _threshold = threshold;
    public string BaselineId => "B3-POSTERIOR-MEAN";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        if (_threshold is null || input.Belief is null || input.ScalarMapping is null || !input.Belief.TryGetWeightedSupport(out var support)) return new(BaselineStatus.NotEvaluable, null, "MAPPING_BELIEF_OR_THRESHOLD_MISSING");
        var mean = support.Sum(x => x.Weight * input.ScalarMapping(x.State));
        return new(BaselineStatus.Success, mean >= _threshold ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue, "POSTERIOR_MEAN_RULE");
    }
}

public sealed class PosteriorQuantileBaseline<TState> : IBaselinePolicy<TState>
{
    private readonly double? _quantileLevel;
    private readonly double? _threshold;
    public PosteriorQuantileBaseline(double? quantileLevel, double? threshold) { _quantileLevel = quantileLevel; _threshold = threshold; }
    public string BaselineId => "B4-POSTERIOR-QUANTILE";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        if (_quantileLevel is null or < 0d or > 1d || _threshold is null || input.Belief is null || input.ScalarMapping is null || !input.Belief.TryGetWeightedSupport(out var support)) return new(BaselineStatus.NotEvaluable, null, "QUANTILE_CONFIGURATION_MISSING");
        var ordered = support.Select(x => (Value: input.ScalarMapping(x.State), x.Weight)).OrderBy(x => x.Value).ToArray();
        var cumulative = 0d; var quantile = ordered[^1].Value;
        foreach (var item in ordered) { cumulative += item.Weight; if (cumulative >= _quantileLevel) { quantile = item.Value; break; } }
        return new(BaselineStatus.Success, quantile >= _threshold ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue, "POSTERIOR_QUANTILE_RULE");
    }
}

public sealed class FirstPassageQuantileBaseline<TState> : IBaselinePolicy<TState>
{
    private readonly double? _designThreshold;
    public FirstPassageQuantileBaseline(double? designThreshold) => _designThreshold = designThreshold;
    public string BaselineId => "B5-FIRST-PASSAGE-QUANTILE";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        if (_designThreshold is null || input.FirstPassageQuantile is null) return new(BaselineStatus.NotEvaluable, null, "FIRST_PASSAGE_DISTRIBUTION_OR_THRESHOLD_MISSING");
        return new(BaselineStatus.Success, input.FirstPassageQuantile <= _designThreshold ? MaintenanceActionKind.PlannedReplacement : MaintenanceActionKind.Continue, "FIRST_PASSAGE_DISTRIBUTION_RULE");
    }
}

public sealed class UnconstrainedBayesBaseline<TState> : IBaselinePolicy<TState>
{
    public string BaselineId => "B6-UNCONSTRAINED-BAYES";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        var best = input.DirectPolicyEvaluations.Where(x => x.Disutility.ExpectedDisutility is not null).OrderBy(x => x.Disutility.ExpectedDisutility!.Value.Value).ThenBy(x => x.Policy.Id.Value, StringComparer.Ordinal).FirstOrDefault();
        return best is null ? new(BaselineStatus.NotEvaluable, null, "NO_DIRECT_EVALUATION") : new(BaselineStatus.Success, best.Policy.FirstAction, "MINIMUM_J_WITHOUT_CONSTRAINT");
    }
}

public sealed class ConstrainedBayesBaseline<TState> : IBaselinePolicy<TState>
{
    private readonly IConstrainedPolicySelector _selector;
    public ConstrainedBayesBaseline(IConstrainedPolicySelector selector) => _selector = selector;
    public string BaselineId => "B7-CONSTRAINED-ONE-STAGE";
    public BaselineDecision Evaluate(BaselineDecisionInput<TState> input)
    {
        var direct = input.DirectPolicyEvaluations.Where(x => x.Policy is DirectPolicy).ToArray();
        var selected = _selector.Select(direct);
        return selected.Selected is null ? new(BaselineStatus.NotEvaluable, null, selected.Reason) : new(BaselineStatus.Success, selected.Selected.Policy.FirstAction, selected.Reason);
    }
}

public sealed record AblationDefinition(string Id, string Mechanism, bool Applicable, bool Deployable, string Status);

public static class AblationRegistry
{
    public static IReadOnlyList<AblationDefinition> MinimumR4 { get; } = new AblationDefinition[]
    {
        new("ABL-1", "Disable endogenous information policies", true, false, "AVAILABLE"),
        new("ABL-2", "Replace full belief by mean-preserving degenerate belief", true, false, "AVAILABLE_WHEN_SCALAR_MAPPING_VALID"),
        new("ABL-3", "Remove observation-quality conditioning", true, false, "AVAILABLE_WHEN_QUALITY_MODELED"),
        new("ABL-4", "Remove informative missingness likelihood", true, false, "AVAILABLE_WHEN_INFORMATIVE_MISSINGNESS_MODELED"),
        new("ABL-5", "Disable reliability constraint", true, false, "AVAILABLE"),
        new("ABL-6", "Replace reset distribution by mean-matched point reset", true, false, "AVAILABLE"),
        new("ABL-7", "Toggle event-conditioned transition", false, false, "NotApplicable"),
        new("ABL-8", "Remove branchwise epsilon remaining rule only", true, false, "SEMANTIC_DIAGNOSTIC_ONLY")
    };
}

public sealed class SequentialPolicyIntegrationPlaceholder
{
    public string Id => "B8-DISABLED";
    public bool Enabled => false;
}
