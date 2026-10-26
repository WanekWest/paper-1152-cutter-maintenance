using System.Security.Cryptography;
using System.Text;
using WearVision.Reliability.Application;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public sealed class T5PlanningWriteSession
{
    private readonly string _root;
    private readonly long _maximumBytes;
    private long _writtenBytes;
    private bool _completed;

    public T5PlanningWriteSession(string approvedRoot, string runId, long maximumBytes, ConfigurationProfileKind profileKind)
    {
        if (profileKind is not (ConfigurationProfileKind.TestFixtureOnly or ConfigurationProfileKind.T5Pilot)) throw new InvalidOperationException("PLANNING_PROFILE_INVALID");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        var approved = Path.GetFullPath(approvedRoot);
        _root = Path.GetFullPath(Path.Combine(approved, runId));
        if (!_root.StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Planning path escapes the approved root.");
        if (Directory.Exists(_root) || File.Exists(_root)) throw new IOException("Planning destination already exists.");
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "RUN_INCOMPLETE"), "T5_PLANNING_ONLY", new UTF8Encoding(false));
    }

    public string Root => _root;
    public long WrittenBytes => _writtenBytes;

    public void WriteJson<T>(string relativePath, T value)
    {
        if (_completed) throw new InvalidOperationException("Planning run is complete.");
        var bytes = CanonicalJson.Serialize(value);
        if (_writtenBytes + bytes.Length > _maximumBytes) throw new IOException("T5_STORAGE_CEILING_EXCEEDED");
        var path = Resolve(relativePath);
        if (File.Exists(path)) throw new IOException("Planning output overwrite is prohibited.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        _writtenBytes += bytes.Length;
    }

    public ArtifactHash Complete(T5PlanningManifest manifest)
    {
        if (!manifest.Completed || manifest.T6Eligible || manifest.ManuscriptEligible) throw new InvalidOperationException("Incomplete or eligible planning output cannot be completed.");
        WriteJson("t5_planning_manifest.json", manifest);
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(x => !x.EndsWith("RUN_INCOMPLETE", StringComparison.Ordinal))
            .OrderBy(x => Path.GetRelativePath(_root, x), StringComparer.Ordinal)
            .Select(x => new FileClosureEntry(Path.GetRelativePath(_root, x).Replace('\\', '/'), new FileInfo(x).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))))
            .ToArray();
        var completion = CanonicalJson.Serialize(new CompletionManifest("T5_PLANNING_ONLY_NOT_T6_NOT_MANUSCRIPT", files));
        if (_writtenBytes + completion.Length > _maximumBytes) throw new IOException("T5_STORAGE_CEILING_EXCEEDED");
        File.WriteAllBytes(Path.Combine(_root, "completion-manifest.json"), completion);
        File.Delete(Path.Combine(_root, "RUN_INCOMPLETE"));
        _writtenBytes += completion.Length;
        _completed = true;
        return new(Convert.ToHexString(SHA256.HashData(completion)));
    }

    private string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Planning path must be relative.");
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Planning path escapes the run root.");
        return path;
    }
}
