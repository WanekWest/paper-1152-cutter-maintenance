using System.Globalization;

using System.Text.Json.Serialization;

namespace WearVision.Reliability.Domain;

internal static class ValueGuards
{
    public static string Id(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
        {
            throw new ArgumentException($"{name} contains an unregistered character.", name);
        }

        return value;
    }

    public static string Sha256(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException($"{name} must be a 64-character SHA-256 hexadecimal value.", name);
        }

        return value.ToUpperInvariant();
    }

    public static double Finite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        }

        return value;
    }

    public static double UnitInterval(double value, string name)
    {
        Finite(value, name);
        if (value is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(name, "Value must be in [0,1].");
        }

        return value;
    }
}

public readonly record struct ComponentEpisodeId
{
    public ComponentEpisodeId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct DecisionId
{
    public DecisionId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct TrajectoryId
{
    public TrajectoryId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct ExperimentId
{
    [JsonConstructor]
    public ExperimentId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct ScenarioId
{
    [JsonConstructor]
    public ScenarioId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PolicyId
{
    public PolicyId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct ActionId
{
    public ActionId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct ModelVersionId
{
    public ModelVersionId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct PosteriorVersionId
{
    public PosteriorVersionId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct LimitStateVersionId
{
    public LimitStateVersionId(string value) => Value = ValueGuards.Id(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct ConfigurationHash
{
    public ConfigurationHash(string value) => Sha256Hex = ValueGuards.Sha256(value, nameof(value));
    public string Sha256Hex { get; }
    public override string ToString() => Sha256Hex;
}

public readonly record struct ArtifactHash
{
    public ArtifactHash(string value) => Sha256Hex = ValueGuards.Sha256(value, nameof(value));
    public string Sha256Hex { get; }
    public override string ToString() => Sha256Hex;
}

public readonly record struct Exposure
{
    public Exposure(double value)
    {
        ValueGuards.Finite(value, nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public double Value { get; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public readonly record struct Horizon
{
    public Horizon(double value)
    {
        ValueGuards.Finite(value, nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        Value = value;
    }

    public double Value { get; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public readonly record struct Probability
{
    public Probability(double value) => Value = ValueGuards.UnitInterval(value, nameof(value));
    public double Value { get; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public readonly record struct FailureProbability
{
    public FailureProbability(double value) => Value = ValueGuards.UnitInterval(value, nameof(value));
    public double Value { get; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public readonly record struct Reliability
{
    public Reliability(double value) => Value = ValueGuards.UnitInterval(value, nameof(value));
    public double Value { get; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public readonly record struct Disutility
{
    public Disutility(double value) => Value = ValueGuards.UnitInterval(value, nameof(value));
    public double Value { get; }
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public readonly record struct NumericalStandardError
{
    public NumericalStandardError(double value)
    {
        ValueGuards.Finite(value, nameof(value));
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public double Value { get; }
}

public enum EvaluationStatus
{
    Success,
    SuccessNumericalTie,
    NotEvaluable,
    MissingRequiredInput,
    IncompatibleModel,
    NumericalNonConvergence,
    BoundaryUncertified,
    ActionNotAdmissible,
    InvalidPosterior,
    OracleLeakageDetected,
    RetiredArtifactDetected,
    ProtocolViolation
}

public enum DecisionStatus
{
    Success,
    SuccessNumericalTie,
    ConstraintBreach,
    NotEvaluable,
    NumericalNonConvergence,
    ProtocolViolation
}
