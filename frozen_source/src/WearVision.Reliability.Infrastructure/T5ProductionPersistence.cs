using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed record T5ProductionCompletionState(
    IReadOnlyList<string> AuthorizedRows,
    IReadOnlyList<string> ExecutedRows,
    long ExpectedEpisodes,
    long AccountedEpisodes,
    bool LineageComplete,
    int PlanningRunsCompleted);

public sealed record T5ProductionCompletionManifest(
    string SchemaVersion,
    string ArtifactLabel,
    IReadOnlyList<string> AuthorizedRows,
    IReadOnlyList<string> ExecutedRows,
    long ExpectedEpisodes,
    long AccountedEpisodes,
    IReadOnlyDictionary<string, long> RawSchemaRecordCounts,
    int PlanningRunsCompleted,
    bool LineageComplete,
    bool Completed,
    IReadOnlyList<FileClosureEntry> Files);

public static class T5ProductionEvidenceClosureGate
{
    public static void Validate(
        bool rowsMatch,
        bool episodeCountsMatch,
        bool mandatoryRawArtifactsPresent,
        bool lineageComplete,
        bool planningComplete)
    {
        if (!rowsMatch || !episodeCountsMatch || !mandatoryRawArtifactsPresent || !lineageComplete || !planningComplete)
            throw new InvalidOperationException("T5_EXECUTION_EVIDENCE_INCOMPLETE");
    }
}

public sealed class T5ProductionWriteSession : IDisposable
{
    private static readonly string[] MandatoryRawSchemas =
    [
        "SCH-RUN", "SCH-ENV", "SCH-SPL", "SCH-PST", "SCH-TRJ", "SCH-DIN", "SCH-PEV",
        "SCH-BRN", "SCH-DEC", "SCH-FEV", "SCH-CON", "SCH-REL", "SCH-NUM"
    ];

    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly RawSchemaRegistry _registry;
    private readonly RawRecordValidator _validator;
    private readonly Dictionary<string, StreamWriter> _rawWriters = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _rawCounts = new(StringComparer.Ordinal);
    private bool _completed;
    private bool _disposed;

    public T5ProductionWriteSession(string approvedRoot, string runId, long maximumBytes, RawSchemaRegistry registry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentNullException.ThrowIfNull(registry);
        var approved = Path.GetFullPath(approvedRoot);
        _root = Path.GetFullPath(Path.Combine(approved, runId));
        if (!_root.StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("T5 production path escapes the approved root.");
        if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("T5 production destination already exists.");
        _maximumBytes = maximumBytes;
        _registry = registry;
        _validator = new(registry);
        Directory.CreateDirectory(_root);
        WriteBytesCore("RUN_INCOMPLETE", Encoding.UTF8.GetBytes("MODEL_BASED_T5_INCOMPLETE"));
    }

    public string Root => _root;
    public long CommittedBytes => Directory.Exists(_root)
        ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length)
        : 0L;
    public IReadOnlyDictionary<string, long> RawRecordCounts => _rawCounts;

    public void WriteRaw(RawEvidenceRecord record)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(record);
        var issues = _validator.Validate(record);
        if (issues.Count != 0) throw new InvalidDataException(string.Join(';', issues.Select(x => $"{x.Code}:{x.Message}")));
        var definitions = _registry.Fields.Where(x => x.SchemaId == record.SchemaId).ToArray();
        var values = definitions.Select(x => record.Fields.TryGetValue(x.FieldName, out var value) ? Normalize(value) : null).ToArray();
        var line = CsvCodec.Format(values) + "\n";
        if (!_rawWriters.TryGetValue(record.SchemaId, out var writer))
        {
            var relativePath = Path.Combine("raw", record.SchemaId + ".csv");
            var header = CsvCodec.Format(definitions.Select(x => x.FieldName)) + "\n";
            EnsureCapacity(Encoding.UTF8.GetByteCount(header) + Encoding.UTF8.GetByteCount(line));
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer = new(stream, new UTF8Encoding(false), leaveOpen: false) { NewLine = "\n" };
            writer.Write(header);
            _rawWriters.Add(record.SchemaId, writer);
            _rawCounts.Add(record.SchemaId, 0L);
        }
        else
        {
            EnsureCapacity(Encoding.UTF8.GetByteCount(line));
        }
        writer.Write(line);
        writer.Flush();
        _rawCounts[record.SchemaId]++;
    }

    public void WriteJson<T>(string relativePath, T value)
    {
        EnsureOpen();
        var bytes = CanonicalJson.Serialize(value);
        EnsureCapacity(bytes.Length);
        WriteBytesCore(relativePath, bytes);
    }

    public ArtifactHash WritePlanning(
        T5PlanningRunResult result,
        IReadOnlyList<T5PlanningEpisodeInput> episodes)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(episodes);
        var remaining = _maximumBytes - CommittedBytes;
        if (remaining <= 0) throw new IOException("T5_STORAGE_CEILING_EXCEEDED");
        var planningRoot = Path.Combine(_root, "planning");
        var planning = new T5PlanningWriteSession(planningRoot, result.Manifest.Lineage.ExperimentId.Value, remaining, result.Manifest.Lineage.ProfileKind);
        planning.WriteJson("episodes.json", episodes);
        planning.WriteJson("run-resource.json", result.Resources);
        planning.WriteJson("numerical-convergence.json", result.Numerical);
        planning.WriteJson("paired-b7.json", result.PairedDifferences);
        planning.WriteJson("paired-primary-comparators.json", result.ComparatorDifferences);
        planning.WriteJson("event-prevalence.json", result.Events);
        planning.WriteJson("censoring.json", result.Censoring);
        planning.WriteJson("calibration-occupancy.json", result.Occupancy);
        var hash = planning.Complete(result.Manifest);
        if (CommittedBytes > _maximumBytes) throw new IOException("T5_STORAGE_CEILING_EXCEEDED");
        return hash;
    }

    public ArtifactHash Complete(T5ProductionCompletionState state)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(state);
        CloseRawWriters();
        var rowsMatch = state.AuthorizedRows.SequenceEqual(state.ExecutedRows, StringComparer.Ordinal);
        var countsMatch = state.ExpectedEpisodes == state.AccountedEpisodes;
        var mandatoryPresent = MandatoryRawSchemas.All(x => _rawCounts.GetValueOrDefault(x) > 0);
        var planningComplete = state.PlanningRunsCompleted == state.AuthorizedRows.Count;
        T5ProductionEvidenceClosureGate.Validate(rowsMatch, countsMatch, mandatoryPresent, state.LineageComplete, planningComplete);

        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(x => !x.EndsWith("RUN_INCOMPLETE", StringComparison.Ordinal) &&
                !Path.GetRelativePath(_root, x).Equals("completion-manifest.json", StringComparison.Ordinal))
            .OrderBy(x => Path.GetRelativePath(_root, x), StringComparer.Ordinal)
            .Select(x => new FileClosureEntry(
                Path.GetRelativePath(_root, x).Replace('\\', '/'),
                new FileInfo(x).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))))
            .ToArray();
        var manifest = new T5ProductionCompletionManifest(
            "t5-production-completion-v1",
            "MODEL_BASED_T5_METHODOLOGICAL_PILOT_ONLY_NOT_T6_NOT_MANUSCRIPT",
            state.AuthorizedRows,
            state.ExecutedRows,
            state.ExpectedEpisodes,
            state.AccountedEpisodes,
            new SortedDictionary<string, long>(_rawCounts, StringComparer.Ordinal),
            state.PlanningRunsCompleted,
            state.LineageComplete,
            true,
            files);
        var bytes = CanonicalJson.Serialize(manifest);
        EnsureCapacity(bytes.Length);
        WriteBytesCore("completion-manifest.json", bytes);
        File.Delete(Path.Combine(_root, "RUN_INCOMPLETE"));
        _completed = true;
        return new(Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseRawWriters();
        _disposed = true;
    }

    private void CloseRawWriters()
    {
        foreach (var writer in _rawWriters.Values) writer.Dispose();
        _rawWriters.Clear();
    }

    private void EnsureOpen()
    {
        if (_completed || _disposed) throw new InvalidOperationException("T5 production write session is closed.");
    }

    private void EnsureCapacity(long additionalBytes)
    {
        if (additionalBytes < 0 || CommittedBytes + additionalBytes > _maximumBytes)
            throw new IOException("T5_STORAGE_CEILING_EXCEEDED");
    }

    private void WriteBytesCore(string relativePath, byte[] bytes)
    {
        var path = Resolve(relativePath);
        if (File.Exists(path)) throw new IOException("T5 production output overwrite is prohibited.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("T5 production path must be relative.");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("T5 production path escapes the run root.");
        return path;
    }

    private static object? Normalize(object? value) => value switch
    {
        null => null,
        ArtifactHash hash => hash.Sha256Hex,
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        IEnumerable<string> strings => string.Join(';', strings),
        Enum enumeration => enumeration.ToString(),
        _ => value
    };
}
