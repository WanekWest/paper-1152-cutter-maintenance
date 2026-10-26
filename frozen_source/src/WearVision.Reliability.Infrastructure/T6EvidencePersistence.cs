using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed record T6AuthorizationConsumptionRecordV1(
    string SchemaVersion,
    string AuthorizationId,
    string AuthorizationSha256,
    string RuntimeConfigSetSha256,
    string SourceSnapshotSha256,
    string ExecutableSha256,
    string RunId,
    DateTimeOffset ConsumedAtUtc,
    string State,
    string StoreKey);

public sealed class T6AuthorizationConsumptionStoreV1
{
    private readonly Func<DateTimeOffset> _clock;

    public T6AuthorizationConsumptionStoreV1(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public T6AuthorizationConsumptionRecordV1 Consume(
        string storeRoot,
        string authorizationPath,
        T6AuthorizationManifestV1 authorization,
        string runId)
    {
        var root = Path.GetFullPath(storeRoot);
        Directory.CreateDirectory(root);
        var authorizationSha = ConfigurationSetLoaderV2.HashFile(authorizationPath);
        var storeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization.AuthorizationId + "|" + authorizationSha)));
        var path = Path.Combine(root, storeKey + ".consumed.json");
        var record = new T6AuthorizationConsumptionRecordV1(
            "t6-authorization-consumption-v1",
            authorization.AuthorizationId,
            authorizationSha,
            authorization.RuntimeConfigSetSha256,
            authorization.SourceSnapshotSha256,
            authorization.ExecutableSha256,
            runId,
            _clock().ToUniversalTime(),
            "CONSUMED",
            storeKey);
        var bytes = CanonicalJson.Serialize(record);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException exception) when (File.Exists(path))
        {
            throw new InvalidOperationException("T6_AUTHORIZATION_ALREADY_CONSUMED", exception);
        }
        return record;
    }

    public static bool IsConsumed(string storeRoot, string authorizationPath, string authorizationId)
    {
        if (!Directory.Exists(storeRoot) || !File.Exists(authorizationPath)) return false;
        var authorizationSha = ConfigurationSetLoaderV2.HashFile(authorizationPath);
        var storeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorizationId + "|" + authorizationSha)));
        return File.Exists(Path.Combine(storeRoot, storeKey + ".consumed.json"));
    }
}

public interface IT6ResourceProbe
{
    double ElapsedSeconds { get; }
    long PeakWorkingSetBytes { get; }
}

public sealed class T6ProcessResourceProbe : IT6ResourceProbe
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;
    public long PeakWorkingSetBytes => Process.GetCurrentProcess().PeakWorkingSet64;
}

public sealed record T6ResourceCheckRecordV1(
    string SchemaVersion,
    string Boundary,
    long Sequence,
    double MaximumWallClockSeconds,
    long MaximumStorageBytes,
    bool WallClockWithinLimit,
    bool StorageWithinLimit,
    bool WithinLimits);

public sealed class T6ResourceGuardV1
{
    private readonly IT6ResourceProbe _probe;
    private readonly Func<long> _storageBytes;
    private long _sequence;

    public T6ResourceGuardV1(
        double maximumWallClockSeconds,
        long maximumStorageBytes,
        IT6ResourceProbe probe,
        Func<long> storageBytes)
    {
        if (maximumWallClockSeconds != T6FrozenConstants.MaximumWallClockSeconds)
            throw new InvalidDataException("T6_WALL_CLOCK_CEILING_MUST_EQUAL_21600");
        if (maximumStorageBytes != T6FrozenConstants.MaximumStorageBytes)
            throw new InvalidDataException("T6_STORAGE_CEILING_MUST_EQUAL_4294967296");
        _probe = probe;
        _storageBytes = storageBytes;
        MaximumWallClockSeconds = maximumWallClockSeconds;
        MaximumStorageBytes = maximumStorageBytes;
    }

    public double MaximumWallClockSeconds { get; }
    public long MaximumStorageBytes { get; }

    public T6ResourceCheckRecordV1 Check(string boundary)
    {
        var wallOk = _probe.ElapsedSeconds <= MaximumWallClockSeconds;
        var storageOk = _storageBytes() <= MaximumStorageBytes;
        var record = new T6ResourceCheckRecordV1(
            "t6-resource-check-v1",
            boundary,
            ++_sequence,
            MaximumWallClockSeconds,
            MaximumStorageBytes,
            wallOk,
            storageOk,
            wallOk && storageOk);
        if (!wallOk) throw new IOException("T6_WALL_CLOCK_CEILING_EXCEEDED");
        if (!storageOk) throw new IOException("T6_STORAGE_CEILING_EXCEEDED");
        return record;
    }
}

public sealed record T6CompletionStateV1(
    IReadOnlyList<string> AuthorizedRows,
    IReadOnlyList<string> ExecutedRows,
    IReadOnlyList<string> ExpectedEpisodeIds,
    IReadOnlyList<string> AccountedEpisodeIds,
    bool AuthorizationConsumptionPresent,
    bool StatisticalInputsPresent,
    bool LineageComplete,
    bool ResourceBreach,
    bool SemanticFailure,
    int ProtocolV2CallCount,
    int PairedContrastInputCount,
    int BootstrapInputCount,
    int CalibrationInputCount,
    int AblationInputCount);

public sealed record T6CompletionManifestV1(
    string SchemaVersion,
    string ArtifactLabel,
    IReadOnlyList<string> AuthorizedRows,
    IReadOnlyList<string> ExecutedRows,
    int ExpectedEpisodes,
    int AccountedEpisodes,
    IReadOnlyDictionary<string, long> RawSchemaRecordCounts,
    int ProtocolV2CallCount,
    int PairedContrastInputCount,
    int BootstrapInputCount,
    int CalibrationInputCount,
    int AblationInputCount,
    bool AuthorizationConsumptionPresent,
    bool StatisticalInputsPresent,
    bool LineageComplete,
    bool ScientificEligibility,
    bool ManuscriptEligibility,
    bool Completed,
    string CompletionWriteRule,
    IReadOnlyList<FileClosureEntry> Files);

public static class T6CompletionGateV1
{
    private static readonly string[] MandatoryRawSchemas =
    [
        "SCH-SPL", "SCH-PST", "SCH-TRJ", "SCH-DIN", "SCH-PEV", "SCH-BRN",
        "SCH-DEC", "SCH-FEV", "SCH-CON", "SCH-REL", "SCH-NUM"
    ];

    public static void Validate(T6CompletionStateV1 state, IReadOnlyDictionary<string, long> rawCounts)
    {
        if (!state.AuthorizedRows.SequenceEqual(state.ExecutedRows, StringComparer.Ordinal))
            throw new InvalidOperationException("T6_EXECUTION_EVIDENCE_INCOMPLETE");
        if (state.AccountedEpisodeIds.Count != state.AccountedEpisodeIds.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("T6_DUPLICATE_COMPONENT_EPISODE");
        if (!state.ExpectedEpisodeIds.SequenceEqual(state.AccountedEpisodeIds, StringComparer.Ordinal))
            throw new InvalidOperationException("T6_EXECUTION_EVIDENCE_INCOMPLETE");
        if (MandatoryRawSchemas.Any(schema => rawCounts.GetValueOrDefault(schema) <= 0))
            throw new InvalidOperationException("T6_MANDATORY_RAW_WRITER_OUTPUT_MISSING");
        if (!state.AuthorizationConsumptionPresent) throw new InvalidOperationException("T6_AUTHORIZATION_CONSUMPTION_MISSING");
        if (!state.StatisticalInputsPresent || state.PairedContrastInputCount <= 0 || state.BootstrapInputCount <= 0 || state.CalibrationInputCount <= 0)
            throw new InvalidOperationException("T6_STATISTICAL_RAW_INPUT_MISSING");
        if (state.AuthorizedRows.Any(x => x.StartsWith("T6-ABL", StringComparison.Ordinal)) && state.AblationInputCount <= 0)
            throw new InvalidOperationException("T6_ABLATION_INPUT_MISSING");
        if (state.ProtocolV2CallCount <= 0) throw new InvalidOperationException("T6_NUMERICAL_PROTOCOL_V2_CALL_MISSING");
        if (!state.LineageComplete) throw new InvalidOperationException("T6_LINEAGE_MISSING");
        if (state.ResourceBreach) throw new InvalidOperationException("T6_RESOURCE_BREACH");
        if (state.SemanticFailure) throw new InvalidOperationException("T6_SEMANTIC_FAILURE");
    }
}

public sealed class T6EvidenceWriteSessionV1 : IDisposable
{
    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly RawSchemaRegistry _registry;
    private readonly RawRecordValidator _validator;
    private readonly Func<string, bool>? _failureInjector;
    private readonly Dictionary<string, StreamWriter> _rawWriters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamWriter> _jsonLineWriters = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _rawCounts = new(StringComparer.Ordinal);
    private long _committedBytes;
    private bool _completed;
    private bool _disposed;

    public T6EvidenceWriteSessionV1(
        string approvedRoot,
        string runId,
        long maximumBytes,
        RawSchemaRegistry registry,
        Func<string, bool>? failureInjector = null)
    {
        if (maximumBytes != T6FrozenConstants.MaximumStorageBytes) throw new InvalidDataException("T6_STORAGE_CEILING_MUST_EQUAL_4294967296");
        var approved = Path.GetFullPath(approvedRoot);
        _root = Path.GetFullPath(Path.Combine(approved, runId));
        if (!_root.StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("T6_OUTPUT_PATH_ESCAPE");
        if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("T6_OUTPUT_DIRECTORY_EXISTS");
        _maximumBytes = maximumBytes;
        _registry = registry;
        _validator = new(registry);
        _failureInjector = failureInjector;
        Directory.CreateDirectory(_root);
        WriteBytesCore("RUN_INCOMPLETE", Encoding.UTF8.GetBytes("MODEL_BASED_T6_INCOMPLETE"));
    }

    public string Root => _root;
    public long CommittedBytes => _committedBytes;
    public IReadOnlyDictionary<string, long> RawCounts => _rawCounts;

    public void WriteRaw(string scope, RawEvidenceRecord record)
    {
        EnsureOpen();
        if (string.IsNullOrWhiteSpace(scope) || scope.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("T6_RAW_SCOPE_INVALID", nameof(scope));
        var issues = _validator.Validate(record);
        if (issues.Count != 0) throw new InvalidDataException(string.Join(';', issues.Select(x => $"{x.Code}:{x.Message}")));
        var definitions = _registry.Fields.Where(x => x.SchemaId == record.SchemaId).ToArray();
        var values = definitions.Select(x => record.Fields.TryGetValue(x.FieldName, out var value) ? Normalize(value) : null).ToArray();
        var line = CsvCodec.Format(values) + "\n";
        var writerKey = scope + "|" + record.SchemaId;
        var relativePath = Path.Combine("raw", scope, record.SchemaId + ".csv");
        Inject(relativePath);
        if (!_rawWriters.TryGetValue(writerKey, out var writer))
        {
            var header = CsvCodec.Format(definitions.Select(x => x.FieldName)) + "\n";
            EnsureCapacity(Encoding.UTF8.GetByteCount(header) + Encoding.UTF8.GetByteCount(line));
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n" };
            writer.Write(header);
            _committedBytes += Encoding.UTF8.GetByteCount(header);
            _rawWriters.Add(writerKey, writer);
        }
        else
        {
            EnsureCapacity(Encoding.UTF8.GetByteCount(line));
        }
        writer.Write(line);
        writer.Flush();
        _committedBytes += Encoding.UTF8.GetByteCount(line);
        _rawCounts[record.SchemaId] = _rawCounts.GetValueOrDefault(record.SchemaId) + 1L;
    }

    public void WriteJson<T>(string relativePath, T value)
    {
        EnsureOpen();
        Inject(relativePath);
        var bytes = CanonicalJson.Serialize(value);
        EnsureCapacity(bytes.Length);
        WriteBytesCore(relativePath, bytes);
    }

    public void WriteJsonLine<T>(string relativePath, T value)
    {
        EnsureOpen();
        Inject(relativePath);
        var line = Encoding.UTF8.GetString(CanonicalJson.Serialize(value)) + "\n";
        EnsureCapacity(Encoding.UTF8.GetByteCount(line));
        if (!_jsonLineWriters.TryGetValue(relativePath, out var writer))
        {
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n" };
            _jsonLineWriters.Add(relativePath, writer);
        }
        writer.Write(line);
        writer.Flush();
        _committedBytes += Encoding.UTF8.GetByteCount(line);
    }

    public ArtifactHash Complete(T6CompletionStateV1 state, bool scientificEligibility, bool manuscriptEligibility)
    {
        EnsureOpen();
        CloseWriters();
        T6CompletionGateV1.Validate(state, _rawCounts);
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(x => !Path.GetRelativePath(_root, x).Equals("RUN_INCOMPLETE", StringComparison.Ordinal) &&
                !Path.GetRelativePath(_root, x).Equals("completion-manifest.json", StringComparison.Ordinal))
            .OrderBy(x => Path.GetRelativePath(_root, x), StringComparer.Ordinal)
            .Select(x => new FileClosureEntry(
                Path.GetRelativePath(_root, x).Replace('\\', '/'),
                new FileInfo(x).Length,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))))
            .ToArray();
        var manifest = new T6CompletionManifestV1(
            "t6-production-completion-v1",
            scientificEligibility ? "MODEL_BASED_T6_SCIENTIFIC_EVIDENCE" : "TEST_FIXTURE_ONLY_T6_E2E_NOT_SCIENTIFIC_NOT_MANUSCRIPT",
            state.AuthorizedRows,
            state.ExecutedRows,
            state.ExpectedEpisodeIds.Count,
            state.AccountedEpisodeIds.Count,
            new SortedDictionary<string, long>(_rawCounts, StringComparer.Ordinal),
            state.ProtocolV2CallCount,
            state.PairedContrastInputCount,
            state.BootstrapInputCount,
            state.CalibrationInputCount,
            state.AblationInputCount,
            state.AuthorizationConsumptionPresent,
            state.StatisticalInputsPresent,
            state.LineageComplete,
            scientificEligibility,
            manuscriptEligibility,
            true,
            "completion-manifest.json is written after all evidence writers close and after RUN_INCOMPLETE is removed",
            files);
        var bytes = CanonicalJson.Serialize(manifest);
        EnsureCapacity(bytes.Length);
        File.Delete(Path.Combine(_root, "RUN_INCOMPLETE"));
        WriteBytesCore("completion-manifest.json", bytes);
        _completed = true;
        return new(Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseWriters();
        _disposed = true;
    }

    private void CloseWriters()
    {
        foreach (var writer in _rawWriters.Values) writer.Dispose();
        foreach (var writer in _jsonLineWriters.Values) writer.Dispose();
        _rawWriters.Clear();
        _jsonLineWriters.Clear();
    }

    private void EnsureOpen()
    {
        if (_completed || _disposed || File.Exists(Path.Combine(_root, "completion-manifest.json")))
            throw new InvalidOperationException("T6_EVIDENCE_SESSION_CLOSED");
    }

    private void EnsureCapacity(long additionalBytes)
    {
        if (additionalBytes < 0 || CommittedBytes + additionalBytes > _maximumBytes)
            throw new IOException("T6_STORAGE_CEILING_EXCEEDED");
    }

    private void Inject(string relativePath)
    {
        if (_failureInjector?.Invoke(relativePath.Replace('\\', '/')) == true) throw new IOException("T6_RAW_WRITER_FAILURE_INJECTED");
    }

    private void WriteBytesCore(string relativePath, byte[] bytes)
    {
        var path = Resolve(relativePath);
        if (File.Exists(path)) throw new IOException("T6_EVIDENCE_OVERWRITE_FORBIDDEN");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        _committedBytes += bytes.Length;
    }

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("T6_EVIDENCE_PATH_MUST_BE_RELATIVE");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("T6_EVIDENCE_PATH_ESCAPE");
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
