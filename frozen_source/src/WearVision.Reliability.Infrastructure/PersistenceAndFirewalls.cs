using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed class WriteOnceRunDirectory
{
    private readonly string _allowedRoot;
    private readonly string _runRoot;
    private bool _completed;

    public WriteOnceRunDirectory(string allowedRoot, string runId)
    {
        _allowedRoot = Path.GetFullPath(allowedRoot);
        _runRoot = Path.GetFullPath(Path.Combine(_allowedRoot, runId));
        if (!_runRoot.StartsWith(_allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Run path escapes the allowed root.");
        if (Directory.Exists(_runRoot) || File.Exists(_runRoot)) throw new IOException("Run destination already exists.");
        Directory.CreateDirectory(_runRoot);
        File.WriteAllText(Path.Combine(_runRoot, "RUN_INCOMPLETE"), "TEST_FIXTURE_ONLY", new UTF8Encoding(false));
    }

    public string RunRoot => _runRoot;

    public void WriteJson<T>(string relativePath, T value)
    {
        EnsureWritable(relativePath);
        var bytes = CanonicalJson.Serialize(value);
        File.WriteAllBytes(Resolve(relativePath), bytes);
    }

    public void WriteText(string relativePath, string content)
    {
        EnsureWritable(relativePath);
        File.WriteAllText(Resolve(relativePath), content, new UTF8Encoding(false));
    }

    public ArtifactHash Complete()
    {
        if (_completed) throw new InvalidOperationException("Run is already complete.");
        var incomplete = Path.Combine(_runRoot, "RUN_INCOMPLETE");
        File.Delete(incomplete);
        var entries = Directory.GetFiles(_runRoot, "*", SearchOption.AllDirectories)
            .OrderBy(x => Path.GetRelativePath(_runRoot, x), StringComparer.Ordinal)
            .Select(x => new FileClosureEntry(Path.GetRelativePath(_runRoot, x).Replace('\\', '/'), new FileInfo(x).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))))
            .ToArray();
        var manifestBytes = CanonicalJson.Serialize(new CompletionManifest("TEST_FIXTURE_ONLY", entries));
        var manifestPath = Path.Combine(_runRoot, "completion-manifest.json");
        File.WriteAllBytes(manifestPath, manifestBytes);
        _completed = true;
        return new(Convert.ToHexString(SHA256.HashData(manifestBytes)));
    }

    private void EnsureWritable(string relativePath)
    {
        if (_completed || File.Exists(Path.Combine(_runRoot, "completion-manifest.json"))) throw new InvalidOperationException("Completed run is immutable.");
        var target = Resolve(relativePath);
        if (File.Exists(target)) throw new IOException("Overwrite is prohibited.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    }

    private string Resolve(string relativePath)
    {
        var target = Path.GetFullPath(Path.Combine(_runRoot, relativePath));
        if (!target.StartsWith(_runRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Output path escapes the run root.");
        return target;
    }
}

public sealed record FileClosureEntry(string RelativePath, long SizeBytes, string Sha256);
public sealed record CompletionManifest(string ArtifactLabel, IReadOnlyList<FileClosureEntry> Files);

public sealed class RetirementFirewall
{
    private readonly string[] _pathFragments;
    private readonly HashSet<string> _hashes;

    public RetirementFirewall(IEnumerable<string> pathFragments, IEnumerable<string> sha256Hashes)
    {
        _pathFragments = pathFragments.Select(Normalize).ToArray();
        _hashes = sha256Hashes.Select(x => x.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
    }

    public bool IsRetiredPath(string path)
    {
        var normalized = Normalize(Path.GetFullPath(path));
        return _pathFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsRetiredHash(string sha256) => _hashes.Contains(sha256.ToUpperInvariant());

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

public sealed record ScientificEvent(
    string RunId,
    string? ExperimentId,
    string? ScenarioId,
    string? TrajectoryId,
    string? DecisionId,
    string Component,
    string Status,
    string? ConfigurationHash,
    string? CodeHash,
    string? ErrorCode);

public sealed class ScientificEventSink : IDisposable
{
    private readonly StreamWriter _writer;
    public ScientificEventSink(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new(path, false, new UTF8Encoding(false));
    }

    public void Write(ScientificEvent value)
    {
        var json = Encoding.UTF8.GetString(CanonicalJson.Serialize(value));
        _writer.WriteLine(json);
        _writer.Flush();
    }

    public void Dispose() => _writer.Dispose();
}
