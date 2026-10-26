using System.Globalization;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed record RawSchemaFieldDefinition(
    string SchemaId,
    string RecordType,
    string FieldName,
    string DataType,
    bool Required,
    string SemanticDefinition,
    string UnitOrNormalization,
    string DeployableOrOracle,
    string SourceComponent,
    string ProvenanceRole,
    string AggregationRole,
    string NullableReason,
    string ValidationRule,
    string Notes);

public sealed record NumericalToleranceRegistryEntry(
    string ToleranceId,
    string Name,
    string Category,
    string ProposedValueOrRule,
    string Rationale,
    string AppliesTo,
    string Adjustable,
    string ApprovalOwner,
    string ScientificOrNumerical,
    string Notes)
{
    public ToleranceDefinition ToDomainDefinition() => new(ToleranceId, Category, ProposedValueOrRule, ScientificOrNumerical.Equals("scientific", StringComparison.OrdinalIgnoreCase));
}

public sealed class NumericalToleranceRegistry
{
    public NumericalToleranceRegistry(IReadOnlyList<NumericalToleranceRegistryEntry> entries)
    {
        Entries = entries;
        if (entries.GroupBy(x => x.ToleranceId, StringComparer.Ordinal).Any(x => x.Count() > 1)) throw new InvalidDataException("Duplicate numerical tolerance ID.");
    }

    public IReadOnlyList<NumericalToleranceRegistryEntry> Entries { get; }

    public static NumericalToleranceRegistry Load(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new InvalidDataException("Numerical tolerance registry is empty.");
        var expected = new[] { "tolerance_id", "name", "category", "proposed_value_or_rule", "rationale", "applies_to", "adjustable", "approval_owner", "scientific_or_numerical", "notes" };
        if (!CsvCodec.ParseLine(lines[0]).SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("Numerical tolerance registry header mismatch.");
        var entries = lines.Skip(1).Select(line => CsvCodec.ParseLine(line)).Select(c =>
        {
            if (c.Count != expected.Length) throw new InvalidDataException("Numerical tolerance registry row width mismatch.");
            return new NumericalToleranceRegistryEntry(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9]);
        }).ToArray();
        return new(entries);
    }
}

public sealed class RawSchemaRegistry
{
    public RawSchemaRegistry(IReadOnlyList<RawSchemaFieldDefinition> fields)
    {
        Fields = fields;
        var duplicates = fields.GroupBy(x => (x.SchemaId, x.FieldName)).Where(x => x.Count() > 1).ToArray();
        if (duplicates.Length > 0) throw new InvalidDataException("Duplicate raw schema fields.");
    }

    public IReadOnlyList<RawSchemaFieldDefinition> Fields { get; }
    public IReadOnlyList<string> SchemaIds => Fields.Select(x => x.SchemaId).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public static RawSchemaRegistry Load(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new InvalidDataException("Raw schema registry is empty.");
        var header = CsvCodec.ParseLine(lines[0]);
        var expected = new[] { "schema_id", "record_type", "field_name", "data_type", "required", "semantic_definition", "unit_or_normalization", "deployable_or_oracle", "source_component", "provenance_role", "aggregation_role", "nullable_reason", "validation_rule", "notes" };
        if (!header.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("Raw schema header mismatch.");
        var fields = new List<RawSchemaFieldDefinition>();
        foreach (var line in lines.Skip(1))
        {
            var c = CsvCodec.ParseLine(line);
            if (c.Count != expected.Length) throw new InvalidDataException("Raw schema row width mismatch.");
            fields.Add(new(c[0], c[1], c[2], c[3], c[4] == "YES", c[5], c[6], c[7], c[8], c[9], c[10], c[11], c[12], c[13]));
        }
        return new(fields);
    }

    public string ImplementedTypeFor(RawSchemaFieldDefinition field) => field.DataType switch
    {
        "string" or "identifier/hash" or "reason code" or "text" or "version" or "relative path" => "System.String",
        "string[]" => "System.Collections.Generic.IReadOnlyList<System.String>",
        "sha256" => "WearVision.Reliability.Domain.ArtifactHash",
        "datetime" => "System.DateTimeOffset",
        "enum" => "System.String",
        "bool" => "System.Boolean",
        "int32" => "System.Int32",
        "int64" => "System.Int64",
        "uint64" => "System.UInt64",
        "double" => "System.Double",
        _ => "System.String"
    };
}

public abstract class RawEvidenceRecord
{
    protected RawEvidenceRecord(string schemaId) => SchemaId = schemaId;
    public string SchemaId { get; }
    public SortedDictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    public RawEvidenceRecord Set(string field, object? value) { Fields[field] = value; return this; }
}

public sealed class RunManifestRecord() : RawEvidenceRecord("SCH-RUN");
public sealed class EnvironmentManifestRecord() : RawEvidenceRecord("SCH-ENV");
public sealed class SplitManifestRecord() : RawEvidenceRecord("SCH-SPL");
public sealed class PosteriorArtifactManifestRecord() : RawEvidenceRecord("SCH-PST");
public sealed class TrajectoryManifestRecord() : RawEvidenceRecord("SCH-TRJ");
public sealed class DecisionInputRecord() : RawEvidenceRecord("SCH-DIN");
public sealed class PolicyEvaluationRecord() : RawEvidenceRecord("SCH-PEV");
public sealed class InformationBranchRecord() : RawEvidenceRecord("SCH-BRN");
public sealed class DecisionRecord() : RawEvidenceRecord("SCH-DEC");
public sealed class FailureEventRecord() : RawEvidenceRecord("SCH-FEV");
public sealed class ConsequenceRecord() : RawEvidenceRecord("SCH-CON");
public sealed class ReliabilityPredictionRecord() : RawEvidenceRecord("SCH-REL");
public sealed class NumericalBatchRecord() : RawEvidenceRecord("SCH-NUM");
public sealed class OracleResultRecord() : RawEvidenceRecord("SCH-ORC");

public sealed class RawRecordValidator
{
    private readonly RawSchemaRegistry _registry;
    public RawRecordValidator(RawSchemaRegistry registry) => _registry = registry;
    public IReadOnlyList<ValidationIssue> Validate(RawEvidenceRecord record)
    {
        var definitions = _registry.Fields.Where(x => x.SchemaId == record.SchemaId).ToArray();
        if (definitions.Length == 0) return new[] { new ValidationIssue("UNKNOWN_SCHEMA", EvaluationStatus.ProtocolViolation, record.SchemaId) };
        var issues = new List<ValidationIssue>();
        foreach (var definition in definitions.Where(x => x.Required))
            if (!record.Fields.TryGetValue(definition.FieldName, out var value) || value is null)
                issues.Add(new("MISSING_REQUIRED_RAW_FIELD", EvaluationStatus.ProtocolViolation, $"{record.SchemaId}.{definition.FieldName}"));
        foreach (var extra in record.Fields.Keys.Where(x => definitions.All(d => d.FieldName != x)))
            issues.Add(new("UNKNOWN_RAW_FIELD", EvaluationStatus.ProtocolViolation, $"{record.SchemaId}.{extra}"));
        return issues;
    }
}

public static class CsvCodec
{
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        if (quoted) throw new InvalidDataException("Unclosed CSV quote.");
        fields.Add(current.ToString());
        return fields;
    }

    public static string Format(IEnumerable<object?> fields) => string.Join(',', fields.Select(FormatField));
    private static string FormatField(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
