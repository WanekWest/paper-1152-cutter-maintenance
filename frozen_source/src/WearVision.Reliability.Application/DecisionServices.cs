using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public sealed class ActionAdmissibilityEvaluator : IActionAdmissibilityEvaluator
{
    public ActionAdmissibilityResult Evaluate(MaintenanceActionKind action, ActionAdmissibilityContext context)
    {
        if (!context.EnabledActions.Contains(action)) return new(false, ActionAdmissibilityReason.ActionDisabledByProtocol);
        if (!context.ModelsCompatible) return new(false, ActionAdmissibilityReason.IncompatibleModel);
        if (!context.ConsequenceProfileAvailable) return new(false, ActionAdmissibilityReason.MissingConsequenceProfile);
        if (!context.Durations.TryGetValue(action, out var duration)) return new(false, ActionAdmissibilityReason.MissingDuration);
        if (duration.Value > context.Horizon.Value) return new(false, ActionAdmissibilityReason.InsufficientHorizon);
        if (action == MaintenanceActionKind.PlannedReplacement && !context.ResetModelAvailable) return new(false, ActionAdmissibilityReason.MissingResetModel);
        if (action is MaintenanceActionKind.IntensifiedMonitoring or MaintenanceActionKind.TargetedInspection)
        {
            var kind = action == MaintenanceActionKind.IntensifiedMonitoring ? ObservationKind.IntensifiedMonitoring : ObservationKind.TargetedInspection;
            if (!context.AvailableObservationKernels.Contains(kind)) return new(false, ActionAdmissibilityReason.MissingObservationKernel);
        }

        if (action == MaintenanceActionKind.TargetedInspection && context.InspectionMode is null) return new(false, ActionAdmissibilityReason.InvalidExposureMode);
        return new(true, ActionAdmissibilityReason.Admissible);
    }
}

public sealed class BoundedConsequenceEvaluator : IConsequenceEvaluator
{
    public ConsequenceVector Evaluate(PathOutcome outcome, ConsequenceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var downtime = Math.Clamp(outcome.DowntimeBurden, 0d, 1d);
        return new(new Dictionary<ConsequenceComponentKind, double>
        {
            [ConsequenceComponentKind.Failure] = outcome.FailureOccurred ? 1d : 0d,
            [ConsequenceComponentKind.PlannedReplacement] = outcome.PlannedReplacementOccurred ? 1d : 0d,
            [ConsequenceComponentKind.IntensifiedMonitoring] = outcome.IntensifiedMonitoringOccurred ? 1d : 0d,
            [ConsequenceComponentKind.TargetedInspection] = outcome.TargetedInspectionOccurred ? 1d : 0d,
            [ConsequenceComponentKind.Downtime] = downtime
        });
    }
}

public static class ConsequenceWeightValidator
{
    public static BeliefValidationResult Validate(ConsequenceWeightProfile profile)
    {
        var issues = new List<string>();
        var sum = 0d;
        foreach (var kind in Enum.GetValues<ConsequenceComponentKind>())
        {
            if (!profile.Weights.TryGetValue(kind, out var weight)) { issues.Add($"MISSING:{kind}"); continue; }
            if (!double.IsFinite(weight) || weight < 0d) issues.Add($"INVALID:{kind}"); else sum += weight;
        }

        if (Math.Abs(sum - 1d) > 1e-12) issues.Add("NOT_SIMPLEX:TOL-WEIGHT-SIMPLEX");
        return new(issues.Count == 0 ? EvaluationStatus.Success : EvaluationStatus.ProtocolViolation, sum, issues);
    }
}

public sealed class ExpectedDisutilityEvaluator : IExpectedDisutilityEvaluator
{
    public ExpectedDisutilityEvaluation Evaluate(IReadOnlyList<ConsequenceVector> outcomes, ConsequenceWeightProfile profile)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var validation = ConsequenceWeightValidator.Validate(profile);
        if (!validation.IsValid || outcomes.Count == 0)
            return new(EvaluationStatus.NotEvaluable, null, new Dictionary<ConsequenceComponentKind, double>(), new(0), 0, "INVALID_PROFILE_OR_EMPTY_OUTCOMES");

        var sums = Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, _ => new StableAccumulator());
        var total = new StableAccumulator();
        foreach (var outcome in outcomes)
        {
            var value = 0d;
            foreach (var kind in sums.Keys)
            {
                if (!outcome.Components.TryGetValue(kind, out var component) || !double.IsFinite(component) || component is < 0d or > 1d)
                    return new(EvaluationStatus.ProtocolViolation, null, new Dictionary<ConsequenceComponentKind, double>(), new(0), 0, $"INVALID_COMPONENT:{kind}");
                sums[kind].Add(component);
                value += component * profile.Weights[kind];
            }
            total.Add(value);
        }

        var means = sums.ToDictionary(x => x.Key, x => x.Value.Mean);
        return new(EvaluationStatus.Success, new Disutility(total.Mean), means, new NumericalStandardError(total.StandardError), outcomes.Count, "stable-online-v1");
    }
}

public sealed class StableAccumulator
{
    private long _count;
    private double _mean;
    private double _m2;
    public long Count => _count;
    public double Mean => _mean;
    public double StandardError => _count > 1 ? Math.Sqrt(_m2 / (_count - 1) / _count) : 0d;
    public void Add(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        _count++;
        var delta = value - _mean;
        _mean += delta / _count;
        _m2 += delta * (value - _mean);
    }
}

public sealed class ConstrainedPolicySelector : IConstrainedPolicySelector
{
    public DecisionResult Select(IReadOnlyList<CompletePolicyEvaluation> evaluations)
    {
        if (evaluations.Count == 0) return new(DecisionStatus.NotEvaluable, null, evaluations, "EMPTY_POLICY_SET");
        var admissible = evaluations.Where(x => x.Admissibility.IsAdmissible).ToArray();
        var feasible = admissible.Where(IsProvenFeasible).ToArray();
        var unresolved = admissible.Where(x => x.OverallFeasibility.Classification == FeasibilityClassification.Unresolved || x.BranchFeasibility?.Classification == FeasibilityClassification.Unresolved).ToArray();
        var boundaryUncertified = admissible.Where(x => x.OverallFeasibility.Classification == FeasibilityClassification.BoundaryUncertified || x.BranchFeasibility?.Classification == FeasibilityClassification.BoundaryUncertified).ToArray();
        if (feasible.Length == 0 && boundaryUncertified.Length > 0)
            return new(DecisionStatus.NumericalNonConvergence, null, evaluations, "NO_CERTIFIED_FEASIBLE_ACTION");
        if (feasible.Length == 0 && unresolved.Length > 0)
            return new(DecisionStatus.NumericalNonConvergence, null, evaluations, "UNRESOLVED_FEASIBILITY");

        if (feasible.Length > 0)
        {
            var ordered = feasible.OrderBy(x => x.Disutility.ExpectedDisutility!.Value.Value)
                .ThenBy(x => x.Reliability.FailureProbability!.Value.Value)
                .ThenBy(x => x.InterventionBurden)
                .ThenBy(x => x.Policy.Id.Value, StringComparer.Ordinal)
                .ToArray();
            var selected = ordered[0];
            var tie = ordered.Length > 1 && Math.Abs(ordered[1].Disutility.ExpectedDisutility!.Value.Value - selected.Disutility.ExpectedDisutility!.Value.Value) <= 1e-12;
            return new(tie ? DecisionStatus.SuccessNumericalTie : DecisionStatus.Success, selected, evaluations, tie ? "NUMERICAL_TIE_RESOLVED" : "MINIMUM_FEASIBLE_DISUTILITY");
        }

        var breach = admissible.Where(x => x.OverallFeasibility.Classification == FeasibilityClassification.ProvenInfeasible)
            .OrderBy(x => x.Reliability.FailureProbability?.Value ?? double.PositiveInfinity)
            .ThenBy(x => x.Disutility.ExpectedDisutility?.Value ?? double.PositiveInfinity)
            .ThenBy(x => x.Policy.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return breach is null
            ? new(DecisionStatus.NotEvaluable, null, evaluations, "NO_ADMISSIBLE_POLICY")
            : new(DecisionStatus.ConstraintBreach, breach, evaluations, "ALL_POLICIES_PROVEN_INFEASIBLE");
    }

    private static bool IsProvenFeasible(CompletePolicyEvaluation evaluation) =>
        evaluation.OverallFeasibility.Classification == FeasibilityClassification.ProvenFeasible &&
        (evaluation.BranchFeasibility is null || evaluation.BranchFeasibility.Classification == FeasibilityClassification.ProvenFeasible) &&
        evaluation.Disutility.ExpectedDisutility is not null && evaluation.Reliability.FailureProbability is not null;
}

public sealed class EvsiEvaluator : IEvsiEvaluator
{
    public EvsiEvaluation Evaluate(Disutility noInformationValue, Disutility informationActionValue, Disutility acquisitionBurden, Probability? decisionChangeProbability = null)
    {
        var evsi = noInformationValue.Value - informationActionValue.Value - acquisitionBurden.Value;
        var diagnostics = new NumericalDiagnostics("evsi-direct-v1", 1, 1, new(0), new(0), null, null, true, "TOL-FP-ARITH", Array.Empty<string>());
        return new(EvaluationStatus.Success, evsi, noInformationValue, informationActionValue, acquisitionBurden, decisionChangeProbability, diagnostics);
    }
}

public sealed class AcquisitionBurdenLedger
{
    private bool _charged;
    public void Charge()
    {
        if (_charged) throw new InvalidOperationException("ACQUISITION_BURDEN_DOUBLE_COUNT");
        _charged = true;
    }
    public bool IsCharged => _charged;
}

public static class EvsiCompatibilityValidator
{
    public static EvaluationStatus Validate(Horizon noInformationHorizon, Horizon informationHorizon, string noInformationProfile, string informationProfile)
        => Math.Abs(noInformationHorizon.Value - informationHorizon.Value) <= 1e-12 && string.Equals(noInformationProfile, informationProfile, StringComparison.Ordinal)
            ? EvaluationStatus.Success
            : EvaluationStatus.ProtocolViolation;
}

public static class NumericalSettingsValidator
{
    public static EvaluationStatus Validate(NumericalSettings settings)
    {
        if (settings.MinimumPaths <= 0 || settings.BatchSize <= 0 || settings.MaximumPaths < settings.MinimumPaths || settings.RequiredStableBatches <= 0)
            return EvaluationStatus.ProtocolViolation;
        if (settings.ConfidenceLevel is <= 0d or >= 1d || settings.ProbabilityTolerance <= 0d || settings.TieTolerance <= 0d)
            return EvaluationStatus.ProtocolViolation;
        return EvaluationStatus.Success;
    }
}

public static class ProbabilityMetrics
{
    public const double LogScoreClip = 1e-15;
    public static double LogScore(bool failed, FailureProbability probability)
    {
        var metricProbability = Math.Clamp(probability.Value, LogScoreClip, 1d - LogScoreClip);
        return failed ? -Math.Log(metricProbability) : -Math.Log(1d - metricProbability);
    }
}

public sealed class PolicyEnumerator<TObservation> : IPolicyEnumerator<TObservation>
{
    private readonly IActionAdmissibilityEvaluator _admissibility;
    private readonly IConstrainedTerminalRule<TObservation> _terminalRule;

    public PolicyEnumerator(IActionAdmissibilityEvaluator admissibility, IConstrainedTerminalRule<TObservation> terminalRule)
    {
        _admissibility = admissibility;
        _terminalRule = terminalRule;
    }

    public IReadOnlyList<PolicyTree> Enumerate(ActionAdmissibilityContext context, Horizon horizon)
    {
        var policies = new List<PolicyTree>();
        if (_admissibility.Evaluate(MaintenanceActionKind.Continue, context).IsAdmissible)
            policies.Add(new DirectPolicy(new("P-DIRECT-CONTINUE"), horizon, MaintenanceActionKind.Continue));
        if (_admissibility.Evaluate(MaintenanceActionKind.PlannedReplacement, context).IsAdmissible)
            policies.Add(new DirectPolicy(new("P-DIRECT-REPLACE"), horizon, MaintenanceActionKind.PlannedReplacement));
        foreach (var action in new[] { MaintenanceActionKind.IntensifiedMonitoring, MaintenanceActionKind.TargetedInspection })
        {
            if (_admissibility.Evaluate(action, context).IsAdmissible)
                policies.Add(new InformationPolicy<TObservation>(new PolicyId($"P-INFO-{action}"), horizon, action, context.Durations[action], _terminalRule));
        }
        return policies.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToArray();
    }
}
