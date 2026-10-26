using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public sealed class SplitManifestValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(IEnumerable<SplitAssignment> assignments)
    {
        var issues = new List<ValidationIssue>();
        foreach (var group in assignments.GroupBy(x => x.GroupId, StringComparer.Ordinal))
            if (group.Select(x => x.Partition).Distinct().Count() > 1)
                issues.Add(new("SPLIT_LEAKAGE", EvaluationStatus.ProtocolViolation, $"Grouping unit {group.Key} crosses partitions."));
        return issues;
    }
}

public sealed class DirectEventCensoringStrategy : ICensoringStrategy
{
    public CensoringEvaluation Evaluate(CensoringObservation observation) => observation.Kind switch
    {
        CensoringKind.ExactFailure => new(EvaluationStatus.Success, true, 1d, "EXACT_FAILURE"),
        CensoringKind.None => new(EvaluationStatus.Success, false, 1d, "OBSERVED_NO_FAILURE"),
        _ => new(EvaluationStatus.NotEvaluable, null, null, "APPROVED_CENSORING_METHOD_REQUIRED")
    };
}

public static class HolmProcedure
{
    public static IReadOnlyDictionary<string, bool> Reject(IReadOnlyDictionary<string, double> pValues, double alpha)
    {
        if (alpha is <= 0d or >= 1d) throw new ArgumentOutOfRangeException(nameof(alpha));
        if (pValues.Values.Any(p => !double.IsFinite(p) || p is < 0d or > 1d)) throw new ArgumentOutOfRangeException(nameof(pValues));
        var ordered = pValues.OrderBy(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
        var result = pValues.Keys.ToDictionary(x => x, _ => false, StringComparer.Ordinal);
        var continueRejecting = true;
        for (var i = 0; i < ordered.Length; i++)
        {
            var reject = continueRejecting && ordered[i].Value <= alpha / (ordered.Length - i);
            result[ordered[i].Key] = reject;
            if (!reject) continueRejecting = false;
        }
        return result;
    }
}

public sealed class DeterministicClusterBootstrap
{
    private readonly IRandomStreamFactory _streams;
    public DeterministicClusterBootstrap(IRandomStreamFactory streams) => _streams = streams;

    public BootstrapSample Resample(int replicate, IReadOnlyList<PairedObservation> observations, RandomStreamContext context)
    {
        var result = new List<PairedObservation>(observations.Count);
        foreach (var stratum in observations.GroupBy(x => x.Stratum, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var source = stratum.OrderBy(x => x.PairId, StringComparer.Ordinal).ToArray();
            var stream = _streams.Create(new(context, StreamPurpose.BootstrapResample, checked((ulong)replicate)));
            for (var i = 0; i < source.Length; i++) result.Add(source[(int)(stream.NextUInt64() % (ulong)source.Length)]);
        }
        return new(replicate, result);
    }
}

public static class DeterministicReduction
{
    public static double Sum(IEnumerable<double> values)
    {
        var sum = 0d;
        var compensation = 0d;
        foreach (var value in values)
        {
            var adjusted = value - compensation;
            var next = sum + adjusted;
            compensation = (next - sum) - adjusted;
            sum = next;
        }
        return sum;
    }
}
