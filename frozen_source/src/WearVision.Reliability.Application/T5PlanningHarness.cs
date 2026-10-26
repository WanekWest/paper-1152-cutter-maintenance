using System.Diagnostics;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Application;

public static class T5PlanningSchemaIds
{
    public const string RunResourceSummary = "T5PLAN-RUN-RESOURCE-v1";
    public const string NumericalConvergenceSummary = "T5PLAN-NUMERICAL-CONVERGENCE-v1";
    public const string EpisodePlanningRecord = "T5PLAN-EPISODE-v1";
    public const string PairedDifferenceRecord = "T5PLAN-PAIRED-DIFFERENCE-v1";
    public const string EventPrevalenceSummary = "T5PLAN-EVENT-PREVALENCE-v1";
    public const string CensoringSummary = "T5PLAN-CENSORING-v1";
    public const string CalibrationOccupancySummary = "T5PLAN-CALIBRATION-OCCUPANCY-v1";
    public const string Manifest = "T5PLAN-MANIFEST-v1";
}

public sealed record T5PlanningLineage(
    ExperimentId ExperimentId,
    string ConfigHash,
    string SourceRunId,
    ConfigurationProfileKind ProfileKind,
    string AuthorizationId,
    bool T5Only,
    bool EligibleForT6,
    bool EligibleForManuscript);

public sealed record T5PlanningEpisodeInput(
    T5PlanningLineage Lineage,
    ComponentEpisodeId EpisodeId,
    long ReliabilityPathCount,
    bool NumericalConverged,
    double NumericalStandardError,
    double? ProposedOutcome,
    double? B7Outcome,
    bool FailureEvent,
    bool ConstraintViolation,
    CensoringKind CensoringKind,
    int CalibrationBin,
    bool Complete,
    int DecisionEpochCount,
    double? B4Outcome = null,
    double? B1Outcome = null,
    double? B6Outcome = null,
    string ProposedPolicyId = "",
    string ProposedAction = "",
    string ConstraintStatus = "",
    string PairedCouplingId = "",
    string NumericalStatus = "",
    bool ProductionMapped = false);

public sealed record T5RunResourceSummary(
    string SchemaId,
    T5PlanningLineage Lineage,
    long ElapsedMilliseconds,
    long? WorkingSetBytes,
    long? ManagedHeapBytes,
    long EpisodeCount,
    bool ResourceCancelled,
    string CancellationReason);

public sealed record T5NumericalConvergenceSummary(
    string SchemaId,
    T5PlanningLineage Lineage,
    long TotalPathCount,
    long ConvergedEpisodes,
    long NonConvergedEpisodes,
    double MeanNumericalStandardError);

public sealed record T5PairedDifferencePlanningRecord(
    string SchemaId,
    T5PlanningLineage Lineage,
    ComponentEpisodeId EpisodeId,
    bool PairAvailable,
    double? ProposedMinusB7,
    string Status);

public sealed record T5ComparatorPlanningRecord(
    string SchemaId,
    T5PlanningLineage Lineage,
    ComponentEpisodeId EpisodeId,
    string ComparatorId,
    bool PairAvailable,
    double? ProposedMinusComparator,
    string PairedCouplingId,
    string Status);

public sealed record T5EventPrevalenceSummary(
    string SchemaId,
    T5PlanningLineage Lineage,
    long EpisodeCount,
    long FailureCount,
    long ConstraintViolationCount,
    long IncompleteCount);

public sealed record T5CensoringSummary(
    string SchemaId,
    T5PlanningLineage Lineage,
    IReadOnlyDictionary<CensoringKind, long> Counts);

public sealed record T5CalibrationOccupancySummary(
    string SchemaId,
    T5PlanningLineage Lineage,
    IReadOnlyDictionary<int, long> BinCounts);

public sealed record T5PlanningManifest(
    string SchemaId,
    T5PlanningLineage Lineage,
    bool Completed,
    bool T6Eligible,
    bool ManuscriptEligible,
    long RawEpisodeDenominator,
    long IndependentEpisodeDenominator,
    string Status);

public sealed record T5PlanningRunResult(
    T5RunResourceSummary Resources,
    T5NumericalConvergenceSummary Numerical,
    IReadOnlyList<T5PairedDifferencePlanningRecord> PairedDifferences,
    T5EventPrevalenceSummary Events,
    T5CensoringSummary Censoring,
    T5CalibrationOccupancySummary Occupancy,
    T5PlanningManifest Manifest,
    IReadOnlyList<T5ComparatorPlanningRecord> ComparatorDifferences);

public sealed record T5PlanningResourceCeiling(
    TimeSpan? MaximumWallClock,
    long? MaximumStorageBytes,
    int MaximumDegreeOfParallelism,
    bool? AllowEr04MaximumPaths);

public sealed record T5PlanningRunRequest(
    T5PlanningLineage Lineage,
    T5PlanningResourceCeiling ResourceCeiling,
    bool CaptureMemoryMetric);

public interface IT5PlanningHarness
{
    Task<T5PlanningRunResult> RunAsync(
        T5PlanningRunRequest request,
        IAsyncEnumerable<T5PlanningEpisodeInput> episodes,
        CancellationToken cancellationToken);
}

public sealed class T5PlanningHarness : IT5PlanningHarness
{
    public async Task<T5PlanningRunResult> RunAsync(
        T5PlanningRunRequest request,
        IAsyncEnumerable<T5PlanningEpisodeInput> episodes,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var stopwatch = Stopwatch.StartNew();
        var all = new List<T5PlanningEpisodeInput>();
        var resourceCancelled = false;
        var cancellationReason = string.Empty;
        try
        {
            await foreach (var episode in episodes.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (request.ResourceCeiling.MaximumWallClock is { } maximum && stopwatch.Elapsed > maximum)
                    throw new OperationCanceledException("T5_WALL_CLOCK_CEILING", cancellationToken);
                ValidateEpisode(request.Lineage, episode);
                all.Add(episode);
            }
        }
        catch (OperationCanceledException exception)
        {
            resourceCancelled = true;
            cancellationReason = exception.Message;
        }
        finally
        {
            stopwatch.Stop();
        }

        var lineage = request.Lineage;
        var count = all.Count;
        var converged = all.LongCount(x => x.NumericalConverged);
        var standardError = count == 0 ? 0d : all.Average(x => x.NumericalStandardError);
        var paired = all.Select(x => new T5PairedDifferencePlanningRecord(
            T5PlanningSchemaIds.PairedDifferenceRecord,
            lineage,
            x.EpisodeId,
            x.ProposedOutcome is not null && x.B7Outcome is not null,
            x.ProposedOutcome is not null && x.B7Outcome is not null ? x.ProposedOutcome.Value - x.B7Outcome.Value : null,
            x.ProposedOutcome is not null && x.B7Outcome is not null ? "AVAILABLE" : "MISSING_PAIR_EXPLICIT")).ToArray();
        var comparatorDifferences = all.Where(x => x.ProductionMapped).SelectMany(x => new[]
        {
            Comparator(x, "B7-CONSTRAINED-ONE-STAGE", x.B7Outcome),
            Comparator(x, "B4-POSTERIOR-QUANTILE", x.B4Outcome),
            Comparator(x, "B1-PERIODIC", x.B1Outcome)
        }).ToArray();
        var process = Process.GetCurrentProcess();
        var resources = new T5RunResourceSummary(
            T5PlanningSchemaIds.RunResourceSummary,
            lineage,
            stopwatch.ElapsedMilliseconds,
            request.CaptureMemoryMetric ? process.WorkingSet64 : null,
            request.CaptureMemoryMetric ? GC.GetTotalMemory(false) : null,
            count,
            resourceCancelled,
            cancellationReason);
        var numerical = new T5NumericalConvergenceSummary(
            T5PlanningSchemaIds.NumericalConvergenceSummary,
            lineage,
            all.Sum(x => x.ReliabilityPathCount),
            converged,
            count - converged,
            standardError);
        var events = new T5EventPrevalenceSummary(
            T5PlanningSchemaIds.EventPrevalenceSummary,
            lineage,
            count,
            all.LongCount(x => x.FailureEvent),
            all.LongCount(x => x.ConstraintViolation),
            all.LongCount(x => !x.Complete));
        var censoring = new T5CensoringSummary(
            T5PlanningSchemaIds.CensoringSummary,
            lineage,
            Enum.GetValues<CensoringKind>().ToDictionary(x => x, x => all.LongCount(y => y.CensoringKind == x)));
        var occupancy = new T5CalibrationOccupancySummary(
            T5PlanningSchemaIds.CalibrationOccupancySummary,
            lineage,
            all.GroupBy(x => x.CalibrationBin).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.LongCount()));
        var completed = !resourceCancelled && all.All(x => x.Complete);
        var manifest = new T5PlanningManifest(
            T5PlanningSchemaIds.Manifest,
            lineage,
            completed,
            false,
            false,
            count,
            all.Select(x => x.EpisodeId).Distinct().LongCount(),
            completed ? "TEST_FIXTURE_OR_T5_PLANNING_COMPLETE" : "INCOMPLETE_NOT_EVIDENCE");
        return new(resources, numerical, paired, events, censoring, occupancy, manifest, comparatorDifferences);
    }

    private static void ValidateRequest(T5PlanningRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Lineage.T5Only || request.Lineage.EligibleForT6 || request.Lineage.EligibleForManuscript)
            throw new InvalidOperationException("T5_PLANNING_LINEAGE_FIREWALL");
        if (request.Lineage.ProfileKind is not (ConfigurationProfileKind.TestFixtureOnly or ConfigurationProfileKind.T5Pilot))
            throw new InvalidOperationException("T5_PLANNING_PROFILE_INVALID");
        if (request.ResourceCeiling.MaximumDegreeOfParallelism <= 0)
            throw new InvalidOperationException("INVALID_MAXIMUM_DEGREE_OF_PARALLELISM");
    }

    private static void ValidateEpisode(T5PlanningLineage expected, T5PlanningEpisodeInput episode)
    {
        if (episode.Lineage != expected) throw new InvalidOperationException("PLANNING_EPISODE_LINEAGE_MISMATCH");
        if (episode.ReliabilityPathCount < 0 || !double.IsFinite(episode.NumericalStandardError) || episode.NumericalStandardError < 0d)
            throw new InvalidOperationException("INVALID_PLANNING_NUMERICS");
        if (episode.DecisionEpochCount <= 0) throw new InvalidOperationException("INVALID_DECISION_EPOCH_COUNT");
        if (episode.ProductionMapped &&
            (episode.ProposedOutcome is null || episode.B7Outcome is null || episode.B4Outcome is null || episode.B1Outcome is null || episode.B6Outcome is null ||
             string.IsNullOrWhiteSpace(episode.ProposedPolicyId) || string.IsNullOrWhiteSpace(episode.ProposedAction) || string.IsNullOrWhiteSpace(episode.ConstraintStatus) ||
             string.IsNullOrWhiteSpace(episode.PairedCouplingId) || string.IsNullOrWhiteSpace(episode.NumericalStatus)))
            throw new InvalidOperationException("T5_PRODUCTION_PLANNING_MAPPING_INCOMPLETE");
    }

    private static T5ComparatorPlanningRecord Comparator(T5PlanningEpisodeInput episode, string comparatorId, double? value) => new(
        T5PlanningSchemaIds.PairedDifferenceRecord,
        episode.Lineage,
        episode.EpisodeId,
        comparatorId,
        episode.ProposedOutcome is not null && value is not null,
        episode.ProposedOutcome is not null && value is not null ? episode.ProposedOutcome.Value - value.Value : null,
        episode.PairedCouplingId,
        episode.ProposedOutcome is not null && value is not null ? "AVAILABLE" : "MISSING_PAIR_EXPLICIT");
}

public static class T5PlanningArtifactFirewall
{
    public static EvaluationStatus ValidateAsT6Input(T5PlanningManifest manifest) =>
        manifest.Lineage.T5Only || !manifest.T6Eligible || !manifest.Lineage.EligibleForT6
            ? EvaluationStatus.ProtocolViolation
            : EvaluationStatus.Success;

    public static EvaluationStatus ValidateAsManuscriptInput(T5PlanningManifest manifest) =>
        manifest.Lineage.T5Only || !manifest.ManuscriptEligible || !manifest.Lineage.EligibleForManuscript
            ? EvaluationStatus.ProtocolViolation
            : EvaluationStatus.Success;
}
