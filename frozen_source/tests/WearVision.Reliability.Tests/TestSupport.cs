using System.Reflection;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

internal static class TestSupport
{
    private static int _runCounter;
    public static string WorkspaceRoot { get; } = FindWorkspaceRoot();
    public static string SchemaRoot => Path.Combine(WorkspaceRoot, "schemas");
    public static string EvidenceRoot
    {
        get
        {
            var path = Path.Combine(WorkspaceRoot, "evidence", "test-fixtures");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static RandomStreamContext StreamContext(string suffix = "base") => new(
        0x0123456789ABCDEFUL,
        new("SEM-002"),
        new("FX-RNG-KAT"),
        new($"TRJ-{suffix}"),
        new($"EP-{suffix}"),
        new($"DEC-{suffix}"),
        $"CRN-{suffix}");

    public static NumericalSettings FixtureNumerical(int min = 4, int batch = 2, int max = 12, int stable = 2) =>
        new("TEST_FIXTURE_ONLY", min, batch, max, stable, 0.99, 1e-12, 1e-12, true);

    public static ReliabilityEvaluationRequest<ScalarState> ReliabilityRequest(
        IDegradationBelief<ScalarState> belief,
        IStateTransitionKernel<ScalarState> transition,
        IResetKernel<ScalarState>? reset = null,
        IFunctionalLimitState<ScalarState>? limit = null,
        PolicyTree? policy = null,
        Horizon? horizon = null,
        NumericalSettings? numerical = null,
        FailureProbability? tolerance = null) => new(
            new("DEC-fixture"),
            belief,
            transition,
            reset ?? new PointResetKernel(new(0.1)),
            limit ?? new ScalarLimitState(0.5, "fixture"),
            policy ?? new DirectPolicy(new("P-fixture"), horizon ?? new Horizon(1d), MaintenanceActionKind.Continue),
            horizon ?? new Horizon(1d),
            numerical ?? FixtureNumerical(),
            StreamContext(),
            tolerance);

    public static CompletePolicyEvaluation PolicyEvaluation(
        string id,
        double failureProbability,
        double lower,
        double upper,
        double disutility,
        FeasibilityClassification classification,
        double burden = 0d,
        FeasibilityClassification? branch = null,
        MaintenanceActionKind action = MaintenanceActionKind.Continue)
    {
        var diagnostics = new NumericalDiagnostics("fixture", 10, 1, new(0), new(0), new(lower), new(upper), classification != FeasibilityClassification.Unresolved, "TEST_FIXTURE_ONLY", Array.Empty<string>());
        var reliability = new ReliabilityEvaluation(EvaluationStatus.Success, new(failureProbability), new WearVision.Reliability.Domain.Reliability(1d - failureProbability), new(lower), new(upper), diagnostics, null);
        var expected = new ExpectedDisutilityEvaluation(EvaluationStatus.Success, new(disutility), Enum.GetValues<ConsequenceComponentKind>().ToDictionary(x => x, _ => 0d), new(0), 10, "fixture");
        return new(
            new DirectPolicy(new(id), new(1d), action),
            new(true, ActionAdmissibilityReason.Admissible),
            reliability,
            expected,
            new(classification, failureProbability - 0.2, "fixture"),
            branch is null ? null : new(branch.Value, 0d, "fixture-branch"),
            burden,
            new(FixtureCatalog.Hash("config").Sha256Hex),
            EvaluationStatus.Success,
            Array.Empty<string>());
    }

    public static ConsequenceWeightProfile FixtureWeights(IReadOnlyDictionary<ConsequenceComponentKind, double>? weights = null) => new(
        "weights.fixture",
        "test-owner",
        WeightProfileStatus.TestFixtureOnly,
        "v1",
        null,
        weights ?? new Dictionary<ConsequenceComponentKind, double>
        {
            [ConsequenceComponentKind.Failure] = 0.4,
            [ConsequenceComponentKind.PlannedReplacement] = 0.2,
            [ConsequenceComponentKind.IntensifiedMonitoring] = 0.1,
            [ConsequenceComponentKind.TargetedInspection] = 0.1,
            [ConsequenceComponentKind.Downtime] = 0.2
        },
        FixtureCatalog.Hash("weights.fixture"));

    public static string NewFixtureRunId(string prefix) => $"{prefix}-{Environment.ProcessId}-{Interlocked.Increment(ref _runCounter):D4}";

    public static EffectiveRunConfiguration FixtureConfiguration(
        RunStage stage = RunStage.T1,
        ConfigurationProfileKind profile = ConfigurationProfileKind.TestFixtureOnly,
        string experimentId = "SEM-001",
        string authorization = "ER05_TEST_ONLY",
        bool eventJumps = false,
        bool resolveScientific = false)
    {
        ScientificPlaceholder<T> P<T>(T resolvedValue, string name, RunStage required) =>
            new(resolveScientific, resolveScientific ? resolvedValue : default!, resolveScientific ? "APPROVED_TEST_CONFIGURATION" : "[AUTHOR DATA REQUIRED]", name, required, "TEST_FIXTURE_ONLY");
        var model = new ModelConfig
        {
            SchemaVersion = "model-v1", ProfileKind = profile, StateSpaceId = "scalar-fixture-state",
            ExposureClockId = P("fixture-clock", "operations", RunStage.T5),
            ExposureUnit = P("fixture-unit", "operations", RunStage.T6),
            PosteriorVersion = P("posterior.fixture", "model", RunStage.T5),
            TransitionVersion = P("transition.fixture", "model", RunStage.T5),
            RoutineObservationVersion = P("observation.fixture", "sensing", RunStage.T5),
            MonitorObservationVersion = P("monitor.fixture", "sensing", RunStage.T5),
            InspectionObservationVersion = P("inspection.fixture", "sensing", RunStage.T5),
            LimitStateVersion = P("limit.fixture", "domain", RunStage.T5),
            ResetVersion = P("reset.fixture", "maintenance", RunStage.T5), EventJumpsEnabled = eventJumps
        };
        var reliability = new ReliabilityConfig
        {
            SchemaVersion = "reliability-v1", Horizon = P(1d, "operations", RunStage.T5), Epsilon = P(0.2d, "risk-owner", RunStage.T5),
            EpsilonRemaining = P(0.2d, "risk-owner", RunStage.T5), FailureConfirmationRuleId = P("fixture", "domain", RunStage.T6),
            CensoringStrategyId = P("direct", "statistics", RunStage.T4)
        };
        var actions = new ActionConfig
        {
            SchemaVersion = "action-v1", Enabled = Enum.GetValues<MaintenanceActionKind>().ToDictionary(x => x, _ => true),
            Durations = Enum.GetValues<MaintenanceActionKind>().ToDictionary(x => x, _ => P(0d, "operations", RunStage.T5)),
            InspectionMode = P(InspectionExposureMode.Paused, "operations", RunStage.T5)
        };
        var consequences = new ConsequenceConfig
        {
            SchemaVersion = "consequence-v1", DefinitionVersion = P("fixture", "decision", RunStage.T5),
            WeightProfileId = P("weights.fixture", "stakeholder", RunStage.T5),
            Weights = P<IReadOnlyDictionary<ConsequenceComponentKind, double>>(FixtureWeights().Weights, "stakeholder", RunStage.T5)
        };
        var numerical = new NumericalConfig { SchemaVersion = "numerical-v1", ToleranceRegistryHash = FixtureCatalog.Hash("tolerances").Sha256Hex, RngSchemeId = "sha256-xoshiro256starstar-v1", MasterSeed = 1UL, Settings = FixtureNumerical(), MaximumDegreeOfParallelism = 2 };
        var experiment = new ExperimentConfig { SchemaVersion = "experiment-v1", ExperimentId = new(experimentId), ScenarioId = new("FX-config"), Stage = stage, ProfileKind = profile, AuthorizationReference = authorization, PracticalEffectMargin = P(0d, "stakeholder", RunStage.T6), RareEventHalfWidth = P(0d, "statistics", RunStage.T6) };
        var baselines = new BaselineConfig { SchemaVersion = "baseline-v1", EnabledBaselineIds = ["B0-RUN-TO-FAILURE", "B7-CONSTRAINED-ONE-STAGE"], TuningParameters = new Dictionary<string, ScientificPlaceholder<double>>(), TuningPartition = DataPartition.Design };
        var output = new OutputConfig { SchemaVersion = "output-v1", DestinationRoot = EvidenceRoot, RawFormat = "CSV", ManifestFormat = "JSON", CompressionEnabled = false };
        var compat = new CompatibilityManifest { SchemaVersion = "compat-v1", ArchitectureVersion = "ER03-1152-R4", MathematicalModelVersion = "ER03", ProtocolVersion = "ER04", ComponentHashes = new Dictionary<string, string>() };
        var split = new SplitManifest { SchemaVersion = "split-v1", Assignments = [new("TRJ-config", DataPartition.Design)] };
        var retirement = new RetirementDenyList { SchemaVersion = "retire-v1", PathFragments = ["E:/WearVision/results/reproducible-synthetic-study"], Sha256Hashes = [new string('A', 64)] };
        return new("config-set-v1", model, reliability, actions, consequences, numerical, experiment, baselines, output, compat, split, retirement);
    }

    public static IReadOnlyList<string> ProductionSourceFiles() => Directory.GetFiles(Path.Combine(WorkspaceRoot, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<Type> DeployableTypes() => new[]
    {
        typeof(Probability).Assembly,
        typeof(ConstrainedPolicySelector).Assembly,
        typeof(CanonicalJson).Assembly
    }.SelectMany(x => x.GetTypes()).ToArray();

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WearVision.Reliability.R4.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("ER-05 workspace root was not found.");
    }
}

internal sealed class FixedRandomStream(double unitValue = 0d) : IRandomStream
{
    public ulong NextUInt64() => checked((ulong)(Math.Clamp(unitValue, 0d, 0.9999999999999999d) * ulong.MaxValue));
    public double NextUnitDouble() => unitValue;
}

internal sealed class IndexedRandomStreamFactory : IRandomStreamFactory
{
    public IRandomStream Create(RandomStreamKey key) => new FixedRandomStream(key.SubstreamIndex % 2UL == 0UL ? 0.25d : 0.75d);
    public byte[] EncodeKey(RandomStreamKey key) => BitConverter.GetBytes(key.SubstreamIndex);
    public byte[] DeriveDigest(RandomStreamKey key) => System.Security.Cryptography.SHA256.HashData(EncodeKey(key));
}
