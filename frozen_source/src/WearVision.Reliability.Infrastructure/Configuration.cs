using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed record ScientificPlaceholder<T>(bool HasValue, T Value, string Status, string Owner, RunStage RequiredStage, string EvidenceReference)
{
    public bool IsResolved => HasValue && !Status.Contains("REQUIRED", StringComparison.OrdinalIgnoreCase) && !Status.Contains("CALIBRATION", StringComparison.OrdinalIgnoreCase);
}

public sealed class ModelConfig
{
    public required string SchemaVersion { get; init; }
    public required ConfigurationProfileKind ProfileKind { get; init; }
    public required string StateSpaceId { get; init; }
    public required ScientificPlaceholder<string> ExposureClockId { get; init; }
    public required ScientificPlaceholder<string> ExposureUnit { get; init; }
    public required ScientificPlaceholder<string> PosteriorVersion { get; init; }
    public required ScientificPlaceholder<string> TransitionVersion { get; init; }
    public required ScientificPlaceholder<string> RoutineObservationVersion { get; init; }
    public required ScientificPlaceholder<string> MonitorObservationVersion { get; init; }
    public required ScientificPlaceholder<string> InspectionObservationVersion { get; init; }
    public required ScientificPlaceholder<string> LimitStateVersion { get; init; }
    public required ScientificPlaceholder<string> ResetVersion { get; init; }
    public required bool EventJumpsEnabled { get; init; }
}

public sealed class ReliabilityConfig
{
    public required string SchemaVersion { get; init; }
    public required ScientificPlaceholder<double> Horizon { get; init; }
    public required ScientificPlaceholder<double> Epsilon { get; init; }
    public required ScientificPlaceholder<double> EpsilonRemaining { get; init; }
    public required ScientificPlaceholder<string> FailureConfirmationRuleId { get; init; }
    public required ScientificPlaceholder<string> CensoringStrategyId { get; init; }
}

public sealed class ActionConfig
{
    public required string SchemaVersion { get; init; }
    public required IReadOnlyDictionary<MaintenanceActionKind, bool> Enabled { get; init; }
    public required IReadOnlyDictionary<MaintenanceActionKind, ScientificPlaceholder<double>> Durations { get; init; }
    public required ScientificPlaceholder<InspectionExposureMode> InspectionMode { get; init; }
}

public sealed class ConsequenceConfig
{
    public required string SchemaVersion { get; init; }
    public required ScientificPlaceholder<string> DefinitionVersion { get; init; }
    public required ScientificPlaceholder<string> WeightProfileId { get; init; }
    public required ScientificPlaceholder<IReadOnlyDictionary<ConsequenceComponentKind, double>> Weights { get; init; }
}

public sealed class NumericalConfig
{
    public required string SchemaVersion { get; init; }
    public required string ToleranceRegistryHash { get; init; }
    public required string RngSchemeId { get; init; }
    public required ulong MasterSeed { get; init; }
    public required NumericalSettings Settings { get; init; }
    public required int MaximumDegreeOfParallelism { get; init; }
}

public sealed class ExperimentConfig
{
    public required string SchemaVersion { get; init; }
    public required ExperimentId ExperimentId { get; init; }
    public required ScenarioId ScenarioId { get; init; }
    public required RunStage Stage { get; init; }
    public required ConfigurationProfileKind ProfileKind { get; init; }
    public required string AuthorizationReference { get; init; }
    public required ScientificPlaceholder<double> PracticalEffectMargin { get; init; }
    public required ScientificPlaceholder<double> RareEventHalfWidth { get; init; }
}

public sealed class BaselineConfig
{
    public required string SchemaVersion { get; init; }
    public required IReadOnlyList<string> EnabledBaselineIds { get; init; }
    public required IReadOnlyDictionary<string, ScientificPlaceholder<double>> TuningParameters { get; init; }
    public required DataPartition TuningPartition { get; init; }
}

public sealed class OutputConfig
{
    public required string SchemaVersion { get; init; }
    public required string DestinationRoot { get; init; }
    public required string RawFormat { get; init; }
    public required string ManifestFormat { get; init; }
    public required bool CompressionEnabled { get; init; }
}

public sealed class CompatibilityManifest
{
    public required string SchemaVersion { get; init; }
    public required string ArchitectureVersion { get; init; }
    public required string MathematicalModelVersion { get; init; }
    public required string ProtocolVersion { get; init; }
    public required IReadOnlyDictionary<string, string> ComponentHashes { get; init; }
}

public sealed class SplitManifest
{
    public required string SchemaVersion { get; init; }
    public required IReadOnlyList<SplitAssignment> Assignments { get; init; }
}

public sealed class RetirementDenyList
{
    public required string SchemaVersion { get; init; }
    public required IReadOnlyList<string> PathFragments { get; init; }
    public required IReadOnlyList<string> Sha256Hashes { get; init; }
}

public sealed class ConfigSetManifest
{
    public required string SchemaVersion { get; init; }
    public required ConfigurationProfileKind ProfileKind { get; init; }
    public required string ScientificUse { get; init; }
    public required IReadOnlyDictionary<string, string> FileSha256 { get; init; }
}

public sealed record EffectiveRunConfiguration(
    string SchemaVersion,
    ModelConfig Model,
    ReliabilityConfig Reliability,
    ActionConfig Actions,
    ConsequenceConfig Consequences,
    NumericalConfig Numerical,
    ExperimentConfig Experiment,
    BaselineConfig Baselines,
    OutputConfig Output,
    CompatibilityManifest Compatibility,
    SplitManifest Split,
    RetirementDenyList Retirement);

public sealed class ConfigurationSetLoader
{
    public EffectiveRunConfiguration Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        var manifest = Read<ConfigSetManifest>(root, "config_set_manifest.json");
        if (manifest.ProfileKind != ConfigurationProfileKind.TestFixtureOnly || !manifest.ScientificUse.Equals("TEST_FIXTURE_ONLY", StringComparison.Ordinal))
            throw new InvalidDataException("ER-05 fixture configuration set has an invalid profile boundary.");
        foreach (var pair in manifest.FileSha256)
        {
            var path = Contained(root, pair.Key);
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!actual.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Configuration file hash mismatch: {pair.Key}");
        }
        var model = Read<ModelConfig>(root, "model_config.json");
        if (model.ProfileKind != manifest.ProfileKind) throw new InvalidDataException("Configuration profile mismatch.");
        return new(
            manifest.SchemaVersion,
            model,
            Read<ReliabilityConfig>(root, "reliability_config.json"),
            Read<ActionConfig>(root, "action_config.json"),
            Read<ConsequenceConfig>(root, "consequence_config.json"),
            Read<NumericalConfig>(root, "numerical_config.json"),
            Read<ExperimentConfig>(root, "experiment_config.json"),
            Read<BaselineConfig>(root, "baseline_config.json"),
            Read<OutputConfig>(root, "output_config.json"),
            Read<CompatibilityManifest>(root, "compatibility_manifest.json"),
            Read<SplitManifest>(root, "split_manifest.json"),
            Read<RetirementDenyList>(root, "retirement_denylist.json"));
    }

    private static T Read<T>(string root, string relativePath) where T : notnull => CanonicalJson.DeserializeStrict<T>(File.ReadAllText(Contained(root, relativePath), Encoding.UTF8));

    private static string Contained(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Configuration manifest paths must be relative.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Configuration path escapes the set root.");
        return path;
    }
}

public static class CanonicalJson
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T DeserializeStrict<T>(string json) where T : notnull =>
        JsonSerializer.Deserialize<T>(json, DeserializeOptions) ?? throw new JsonException("Configuration deserialized to null.");

    public static byte[] Serialize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, DeserializeOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(element, writer);
        return stream.ToArray();
    }

    public static ConfigurationHash Hash<T>(T value) => new(Convert.ToHexString(SHA256.HashData(Serialize(value))));

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public sealed class CompatibilityValidator
{
    public IReadOnlyList<ValidationIssue> Validate(CompatibilityManifest actual, CompatibilityManifest expected)
    {
        var issues = new List<ValidationIssue>();
        if (actual.SchemaVersion != expected.SchemaVersion || actual.ArchitectureVersion != expected.ArchitectureVersion || actual.MathematicalModelVersion != expected.MathematicalModelVersion || actual.ProtocolVersion != expected.ProtocolVersion)
            issues.Add(new("COMPATIBILITY_VERSION_MISMATCH", EvaluationStatus.IncompatibleModel, "Compatibility tuple mismatch."));
        foreach (var pair in expected.ComponentHashes)
            if (!actual.ComponentHashes.TryGetValue(pair.Key, out var hash) || !string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("COMPONENT_HASH_MISMATCH", EvaluationStatus.IncompatibleModel, pair.Key));
        return issues;
    }
}

public sealed class RunPreflightValidator
{
    private readonly ExperimentAuthorizationGuard _authorization;
    private readonly SplitManifestValidatorAdapter _split;
    private readonly RetirementFirewall _retirement;

    public RunPreflightValidator(ExperimentAuthorizationGuard authorization, SplitManifestValidatorAdapter split, RetirementFirewall retirement)
    {
        _authorization = authorization;
        _split = split;
        _retirement = retirement;
    }

    public PreflightResult Validate(EffectiveRunConfiguration configuration, ExperimentMatrixRow row)
    {
        var issues = new List<ValidationIssue>();
        issues.AddRange(_authorization.Validate(configuration.Experiment, row));
        issues.AddRange(_split.Validate(configuration.Split));
        if (_retirement.IsRetiredPath(configuration.Output.DestinationRoot)) issues.Add(new("RETIRED_OUTPUT_PATH", EvaluationStatus.RetiredArtifactDetected, configuration.Output.DestinationRoot));
        if (configuration.Model.EventJumpsEnabled) issues.Add(new("EVENT_JUMPS_DISABLED", EvaluationStatus.ProtocolViolation, "Minimum R4 forbids event jumps."));
        if (configuration.Experiment.Stage >= RunStage.T5)
        {
            foreach (var unresolved in RequiredScientificPlaceholders(configuration).Where(x => !x.Resolved))
                issues.Add(new($"UNRESOLVED:{unresolved.Name}", EvaluationStatus.MissingRequiredInput, unresolved.Name));
        }
        return new(issues.Count == 0 ? EvaluationStatus.Success : issues[0].Status, issues);
    }

    private static IEnumerable<(string Name, bool Resolved)> RequiredScientificPlaceholders(EffectiveRunConfiguration c)
    {
        yield return ("exposure_clock", c.Model.ExposureClockId.IsResolved);
        yield return ("posterior", c.Model.PosteriorVersion.IsResolved);
        yield return ("transition", c.Model.TransitionVersion.IsResolved);
        yield return ("limit_state", c.Model.LimitStateVersion.IsResolved);
        yield return ("reset", c.Model.ResetVersion.IsResolved);
        yield return ("horizon", c.Reliability.Horizon.IsResolved);
        yield return ("epsilon", c.Reliability.Epsilon.IsResolved);
        yield return ("consequence_definition", c.Consequences.DefinitionVersion.IsResolved);
        yield return ("weight_profile", c.Consequences.WeightProfileId.IsResolved && c.Consequences.Weights.IsResolved);
    }
}

public sealed class ExperimentAuthorizationGuard
{
    public IReadOnlyList<ValidationIssue> Validate(ExperimentConfig config, ExperimentMatrixRow row)
    {
        var issues = new List<ValidationIssue>();
        if (config.ExperimentId != row.ExperimentId || config.Stage != row.Stage) issues.Add(new("EXPERIMENT_MATRIX_MISMATCH", EvaluationStatus.ProtocolViolation, "Experiment ID/stage mismatch."));
        if (config.ProfileKind == ConfigurationProfileKind.TestFixtureOnly && config.Stage >= RunStage.T5) issues.Add(new("TEST_FIXTURE_PROFILE_ESCALATION", EvaluationStatus.ProtocolViolation, "Fixture profile cannot enter T5/T6."));
        if (config.Stage == RunStage.T5) issues.Add(new("T5_NOT_AUTHORIZED_ER05", EvaluationStatus.ProtocolViolation, "T5 requires a later explicit authorization."));
        if (config.Stage == RunStage.T6) issues.Add(new("T6_HARD_BLOCK_ER05", EvaluationStatus.ProtocolViolation, "T6 is always blocked in ER-05."));
        if (config.Stage <= RunStage.T4 && !row.AuthorizationStatus.Equals("ER05_TEST_ONLY", StringComparison.Ordinal)) issues.Add(new("ER05_ROW_NOT_AUTHORIZED", EvaluationStatus.ProtocolViolation, row.AuthorizationStatus));
        return issues;
    }
}

public sealed class SplitManifestValidatorAdapter
{
    public IReadOnlyList<ValidationIssue> Validate(SplitManifest manifest)
    {
        var issues = new List<ValidationIssue>();
        foreach (var group in manifest.Assignments.GroupBy(x => x.GroupId, StringComparer.Ordinal))
            if (group.Select(x => x.Partition).Distinct().Count() > 1)
                issues.Add(new("SPLIT_LEAKAGE", EvaluationStatus.ProtocolViolation, group.Key));
        return issues;
    }
}
