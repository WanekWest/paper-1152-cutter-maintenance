using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WearVision.Reliability.Domain;

namespace WearVision.Reliability.Infrastructure;

public static class T6FrozenConstants
{
    public const string ParentSourceSha256 = "B5F8213FCBD5926D67C9FEA50F365C22F7BF6BB8A42201C1F1159C558B6E29CA";
    public const string ParentConfigZipSha256 = "C6E1B62886A41676B09DFDD0DAC0E4D836FB3FDFB6462FB9F4C90CDDCB589276";
    public const string ParentAuthorizationSha256 = "868F90F01EB658E0D7196CC07F3A0A3549E048494C6F2BA044FAE7607CEEB03C";
    public const string ParentAuthorizationId = "ER07A-1152-T6-20260821-EXACT-ROWS-V1";
    public const string UpdRegisterSha256 = "E14BA5021A705AB537D392E32B4A2CCEB6C09566E07019A8EB83BA693F98BF07";
    public const string NumericalProtocol = "WV-1152-CHANCE-CERT-2";
    public const double DeltaStar = 0.03d;
    public const double HStar = 0.015d;
    public const double Epsilon = 0.125d;
    public const double EpsilonRemaining = 0.125d;
    public const int TotalEpisodes = 2496;
    public const double MaximumWallClockSeconds = 21600d;
    public const long MaximumStorageBytes = 4_294_967_296L;

    public static IReadOnlyList<string> Rows { get; } =
    [
        "T6-S0-PRIMARY", "T6-S1-SEV050", "T6-S2-SEV075", "T6-ABL1",
        "T6-ABL2", "T6-ABL5", "T6-ABL6", "T6-ABL8"
    ];

    public static IReadOnlyDictionary<string, int> EpisodeCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["T6-S0-PRIMARY"] = 704,
        ["T6-S1-SEV050"] = 256,
        ["T6-S2-SEV075"] = 256,
        ["T6-ABL1"] = 256,
        ["T6-ABL2"] = 256,
        ["T6-ABL5"] = 256,
        ["T6-ABL6"] = 256,
        ["T6-ABL8"] = 256
    };
}

public sealed record T6ScenarioBindingV1(
    string ScenarioId,
    IReadOnlyList<double> PosteriorSupport,
    IReadOnlyList<double> PosteriorWeights,
    double ProbabilityHigh);

public sealed record T6AblationBindingV1(
    string? AblationId,
    string Semantics,
    bool ExecutePairedControl,
    bool DisableEndogenousInformation,
    bool MeanDegenerateBelief,
    bool DisableReliabilityConstraint,
    bool MeanMatchedPointReset,
    double? PointResetValue,
    bool DisableBranchwiseEpsilonRemaining);

public sealed record T6NumericalBindingV1(
    string ProtocolVersion,
    double Epsilon,
    double EpsilonRemaining,
    int McMinimumPaths,
    int McBatchSize,
    int McMaximumPaths,
    int StableBatchCount,
    int FallbackEscalationPathCount,
    string FailureIntervalMethod,
    double QuadratureAbsoluteTolerance,
    double QuadratureRelativeTolerance,
    double ProbabilityTolerance,
    bool FailClosed,
    bool DeterministicFallbackLoggingRequired,
    bool FixtureEscalationAtMaximumPaths);

public sealed record T6RngBindingV1(
    string AlgorithmId,
    string DerivationVersion,
    string CommonRandomNumberCoupling,
    string Literal,
    string LiteralSha256,
    ulong MasterSeedUInt64,
    IReadOnlyList<string> Fields,
    string BootstrapStreamName);

public sealed record T6ResourceBindingV1(
    double MaximumWallClockSeconds,
    long MaximumStorageBytes,
    long MaximumPeakWorkingSetBytes,
    string EnforcementBoundaries,
    string BreachSemantics);

public sealed record T6EffectiveRowBindingV1(
    string SchemaVersion,
    string RowId,
    T6ScenarioBindingV1 Scenario,
    int EpisodeCount,
    IReadOnlyList<string> PolicyBaselineAllowlist,
    T6AblationBindingV1 Ablation,
    T6NumericalBindingV1 NumericalProtocol,
    T6RngBindingV1 Rng,
    IReadOnlyDictionary<string, string> Outcomes,
    string StatisticalRole,
    IReadOnlyList<string> ClaimRoles,
    T6ResourceBindingV1 ResourcePolicy,
    string OutputSubroot,
    string EvidenceClass,
    IReadOnlyDictionary<string, string> ParentHashes,
    bool ScientificEligibility,
    bool ManuscriptEligibility);

public sealed record T6BridgeManifestV1(
    string SchemaVersion,
    string BridgeId,
    string ParentEr07AConfigZipSha256,
    string ParentEr07AConfigManifestSha256,
    string ParentEr07AAuthorizationId,
    string ParentEr07AAuthorizationSha256,
    string ParentAuthorizationStatus,
    string ParentEr06CSourceSha256,
    string NewSourceSnapshotSha256,
    string NewExecutableSha256,
    string UpdSourceGateRegisterSha256,
    string UpdSourceGateStatus,
    string NumericalProtocolVersion,
    string RuntimeCommand,
    bool RuntimeCommandPresent,
    bool FixtureOnly,
    IReadOnlyList<string> EffectiveRows,
    int EpisodeTotal,
    int SourceLeafFieldCount,
    int BindingEntryCount,
    string BindingRegisterSha256,
    IReadOnlyDictionary<string, string> PreservedDesignArtifacts);

public sealed record T6SchemaBindingEntry(
    string SourceArtifactPath,
    string SourceField,
    string RuntimeArtifactPath,
    string RuntimeField,
    string SemanticDefinition,
    string ValidationRule,
    string PreservationStatus,
    string HashParent);

public enum T6AuthorizationState
{
    ReadyNotConsumed,
    Consumed,
    Revoked
}

public enum T6AuthorizationScope
{
    ProductionScientific,
    TestFixtureOnly
}

public sealed record T6AuthorizedRowV1(string RowId, string EffectiveRowSha256, int EpisodeCount);

public sealed record T6AuthorizationManifestV1(
    string SchemaVersion,
    string AuthorizationId,
    T6AuthorizationState State,
    T6AuthorizationScope Scope,
    string RuntimeConfigSetSha256,
    string RuntimeConfigManifestSha256,
    string SourceSnapshotSha256,
    string SourceSnapshotFileName,
    string ExecutableSha256,
    string ExecutableFileName,
    IReadOnlyList<T6AuthorizedRowV1> AuthorizedRows,
    bool WildcardAllowed,
    int EpisodeTotal,
    string OutputPolicySha256,
    string StatisticalProtocolSha256,
    string NumericalProtocolV2Sha256,
    string UpdSourceGateParentSha256,
    string ParentAuthorizationId,
    string ParentAuthorizationSha256,
    string ParentAuthorizationStatus,
    bool ExecutionStarted,
    bool ScientificEligibility,
    bool ManuscriptEligibility,
    string ConsumptionStorePolicy);

public sealed record T6ExecutionIdentityV1(
    string SourceSnapshotPath,
    string SourceSnapshotSha256,
    string ExecutablePath,
    string ExecutableSha256)
{
    public static T6ExecutionIdentityV1 Capture(string sourceSnapshotPath, string executablePath)
    {
        var source = Path.GetFullPath(sourceSnapshotPath);
        var executable = Path.GetFullPath(executablePath);
        if (!File.Exists(source)) throw new FileNotFoundException("T6_SOURCE_SNAPSHOT_MISSING", source);
        if (!File.Exists(executable)) throw new FileNotFoundException("T6_EXECUTABLE_MISSING", executable);
        return new(source, ConfigurationSetLoaderV2.HashFile(source), executable, ConfigurationSetLoaderV2.HashFile(executable));
    }
}

public sealed record T6ValidationReportV1(bool Accepted, bool ExecutionStarted, IReadOnlyList<string> Issues);

public sealed class T6PackageMaterialization : IDisposable
{
    private readonly bool _ownsRoot;

    internal T6PackageMaterialization(string originalPath, string packageSha256, string root, bool ownsRoot)
    {
        OriginalPath = originalPath;
        PackageSha256 = packageSha256;
        Root = root;
        _ownsRoot = ownsRoot;
        Configuration = new ConfigurationSetLoaderV2().Load(root);
        var bridgePath = FindRole(Configuration.Manifest, "t6-bridge-manifest");
        Bridge = CanonicalJson.DeserializeStrict<T6BridgeManifestV1>(File.ReadAllText(Path.Combine(root, bridgePath), Encoding.UTF8));
    }

    public string OriginalPath { get; }
    public string PackageSha256 { get; }
    public string Root { get; }
    public LoadedConfigurationSetV2 Configuration { get; }
    public T6BridgeManifestV1 Bridge { get; }

    public static T6PackageMaterialization Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            if (!Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("T6_RUNTIME_CONFIG_SET_MUST_BE_ZIP_OR_DIRECTORY");
            var root = Path.Combine(Path.GetTempPath(), "WearVision-T6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                ExtractSafely(fullPath, root);
                return new(fullPath, ConfigurationSetLoaderV2.HashFile(fullPath), root, true);
            }
            catch
            {
                Directory.Delete(root, true);
                throw;
            }
        }
        if (Directory.Exists(fullPath)) return new(fullPath, HashDirectory(fullPath), fullPath, false);
        throw new FileNotFoundException("T6_RUNTIME_CONFIG_SET_MISSING", fullPath);
    }

    public void Dispose()
    {
        if (_ownsRoot && Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    public static string FindRole(ConfigSetManifestV2 manifest, string role)
    {
        var items = manifest.Files.Where(x => x.Role == role).ToArray();
        if (items.Length != 1) throw new InvalidDataException($"T6_EXPECTED_ONE_ROLE:{role}");
        return items[0].RelativePath;
    }

    private static void ExtractSafely(string zipPath, string root)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries.OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(root, normalized));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("T6_CONFIG_ZIP_PATH_ESCAPE");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static string HashDirectory(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => Path.GetRelativePath(root, x), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var name = Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public static class T6PackageArchive
{
    public static string Create(string sourceDirectory, string destinationZip)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationZip);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("T6_PACKAGE_DESTINATION_EXISTS");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
        {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(x => Path.GetRelativePath(source, x), StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var input = File.OpenRead(file);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        return ConfigurationSetLoaderV2.HashFile(destination);
    }
}

public sealed class T6AuthorizationValidatorV1
{
    public T6ValidationReportV1 Validate(
        T6PackageMaterialization package,
        T6AuthorizationManifestV1 authorization,
        T6ExecutionIdentityV1 identity,
        string runId)
    {
        var issues = new List<string>();
        var configuration = package.Configuration;
        if (authorization.SchemaVersion != "t6-exact-hash-authorization-v1") issues.Add("T6_AUTHORIZATION_SCHEMA_INVALID");
        if (authorization.State != T6AuthorizationState.ReadyNotConsumed) issues.Add("T6_AUTHORIZATION_NOT_READY");
        if (authorization.ExecutionStarted) issues.Add("T6_AUTHORIZATION_ALREADY_STARTED");
        if (authorization.WildcardAllowed) issues.Add("T6_AUTHORIZATION_WILDCARD_FORBIDDEN");
        if (!authorization.RuntimeConfigSetSha256.Equals(package.PackageSha256, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_CONFIG_SET_HASH_MISMATCH");
        if (!authorization.RuntimeConfigManifestSha256.Equals(configuration.ConfigSetSha256, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_CONFIG_MANIFEST_HASH_MISMATCH");
        if (!authorization.SourceSnapshotSha256.Equals(identity.SourceSnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
            !authorization.SourceSnapshotFileName.Equals(Path.GetFileName(identity.SourceSnapshotPath), StringComparison.Ordinal)) issues.Add("T6_SOURCE_MISMATCH");
        if (!authorization.ExecutableSha256.Equals(identity.ExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !authorization.ExecutableFileName.Equals(Path.GetFileName(identity.ExecutablePath), StringComparison.Ordinal)) issues.Add("T6_EXECUTABLE_MISMATCH");
        if (!authorization.UpdSourceGateParentSha256.Equals(T6FrozenConstants.UpdRegisterSha256, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_UPD_SOURCE_GATE_MISMATCH");
        if (!authorization.ParentAuthorizationSha256.Equals(T6FrozenConstants.ParentAuthorizationSha256, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_PARENT_AUTHORIZATION_HASH_MISMATCH");
        if (!authorization.ParentAuthorizationStatus.Equals("SUPERSEDED_UNCONSUMED_PARENT", StringComparison.Ordinal)) issues.Add("T6_PARENT_AUTHORIZATION_STATUS_INVALID");
        if (string.IsNullOrWhiteSpace(runId)) issues.Add("T6_RUN_ID_MISSING");
        if (runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || runId is "." or "..") issues.Add("T6_RUN_ID_INVALID");

        var rows = configuration.ExperimentSet.Runs;
        var expectedRows = package.Bridge.EffectiveRows;
        if (!rows.Select(x => x.ExperimentId.Value).SequenceEqual(expectedRows, StringComparer.Ordinal)) issues.Add("T6_EFFECTIVE_ROW_SET_MISMATCH");
        if (!authorization.AuthorizedRows.Select(x => x.RowId).SequenceEqual(expectedRows, StringComparer.Ordinal)) issues.Add("T6_AUTHORIZED_ROW_SET_MISMATCH");
        foreach (var run in rows)
        {
            var authorized = authorization.AuthorizedRows.SingleOrDefault(x => x.RowId == run.ExperimentId.Value);
            if (authorized is null || !authorized.EffectiveRowSha256.Equals(run.EffectiveConfigSha256, StringComparison.OrdinalIgnoreCase))
                issues.Add("T6_AUTHORIZED_ROW_HASH_MISMATCH:" + run.ExperimentId.Value);
            var effective = configuration.EffectiveExperiments[run.ExperimentId];
            if (effective.T6Binding is null) issues.Add("T6_ROW_BINDING_MISSING:" + run.ExperimentId.Value);
            else if (effective.T6Binding.RowId != run.ExperimentId.Value || effective.T6Binding.EpisodeCount != effective.EpisodeCount)
                issues.Add("T6_ROW_BINDING_IDENTITY_MISMATCH:" + run.ExperimentId.Value);
        }
        var total = configuration.EffectiveExperiments.Values.Sum(x => x.EpisodeCount);
        if (authorization.EpisodeTotal != total || package.Bridge.EpisodeTotal != total) issues.Add("T6_EPISODE_TOTAL_MISMATCH");
        var expectedTotal = package.Bridge.FixtureOnly ? total : T6FrozenConstants.TotalEpisodes;
        if (total != expectedTotal) issues.Add("T6_PRODUCTION_EPISODE_TOTAL_INVALID");
        if (!package.Bridge.FixtureOnly && !expectedRows.SequenceEqual(T6FrozenConstants.Rows, StringComparer.Ordinal)) issues.Add("T6_PRODUCTION_ROW_ALLOWLIST_INVALID");
        if (!package.Bridge.RuntimeCommandPresent || package.Bridge.RuntimeCommand != "t6 execute") issues.Add("T6_RUNTIME_COMMAND_MISSING");
        if (package.Bridge.SourceLeafFieldCount <= 0 || package.Bridge.SourceLeafFieldCount != package.Bridge.BindingEntryCount) issues.Add("T6_BRIDGE_MAPPING_INCOMPLETE");

        var outputHash = HashRole(configuration, "output-policy");
        var statisticsHash = HashRole(configuration, "t6-statistical-protocol");
        var numericalHash = HashRole(configuration, "t6-numerical-protocol");
        if (!authorization.OutputPolicySha256.Equals(outputHash, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_OUTPUT_POLICY_HASH_MISMATCH");
        if (!authorization.StatisticalProtocolSha256.Equals(statisticsHash, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_STATISTICAL_PROTOCOL_HASH_MISMATCH");
        if (!authorization.NumericalProtocolV2Sha256.Equals(numericalHash, StringComparison.OrdinalIgnoreCase)) issues.Add("T6_NUMERICAL_PROTOCOL_HASH_MISMATCH");
        if (authorization.Scope == T6AuthorizationScope.ProductionScientific)
        {
            if (package.Bridge.FixtureOnly || configuration.Manifest.ProfileKind != ConfigurationProfileKind.T6Scientific || configuration.Manifest.AuthorizedStage != RunStage.T6)
                issues.Add("T6_PRODUCTION_SCOPE_INVALID");
            if (!authorization.ScientificEligibility) issues.Add("T6_PRODUCTION_SCIENTIFIC_ELIGIBILITY_MISSING");
        }
        else if (!package.Bridge.FixtureOnly || configuration.Manifest.ProfileKind != ConfigurationProfileKind.TestFixtureOnly || authorization.ScientificEligibility || authorization.ManuscriptEligibility)
        {
            issues.Add("T6_FIXTURE_SCOPE_INVALID");
        }
        return new(issues.Count == 0, false, issues);
    }

    public static string HashRole(LoadedConfigurationSetV2 configuration, string role) =>
        ConfigurationSetLoaderV2.HashFile(Path.Combine(configuration.Root, T6PackageMaterialization.FindRole(configuration.Manifest, role)));
}

public static class T6AuthorizationIssuerV1
{
    public static T6AuthorizationManifestV1 Issue(
        T6PackageMaterialization package,
        T6ExecutionIdentityV1 identity,
        string authorizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);
        var configuration = package.Configuration;
        if (!identity.SourceSnapshotSha256.Equals(package.Bridge.NewSourceSnapshotSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("T6_AUTHORIZATION_SOURCE_DOES_NOT_MATCH_BRIDGE");
        if (!identity.ExecutableSha256.Equals(package.Bridge.NewExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("T6_AUTHORIZATION_EXECUTABLE_DOES_NOT_MATCH_BRIDGE");
        var rows = configuration.ExperimentSet.Runs.Select(run =>
        {
            var effective = configuration.EffectiveExperiments[run.ExperimentId];
            return new T6AuthorizedRowV1(run.ExperimentId.Value, run.EffectiveConfigSha256, effective.EpisodeCount);
        }).ToArray();
        return new(
            "t6-exact-hash-authorization-v1",
            authorizationId,
            T6AuthorizationState.ReadyNotConsumed,
            package.Bridge.FixtureOnly ? T6AuthorizationScope.TestFixtureOnly : T6AuthorizationScope.ProductionScientific,
            package.PackageSha256,
            configuration.ConfigSetSha256,
            identity.SourceSnapshotSha256,
            Path.GetFileName(identity.SourceSnapshotPath),
            identity.ExecutableSha256,
            Path.GetFileName(identity.ExecutablePath),
            rows,
            false,
            rows.Sum(x => x.EpisodeCount),
            T6AuthorizationValidatorV1.HashRole(configuration, "output-policy"),
            T6AuthorizationValidatorV1.HashRole(configuration, "t6-statistical-protocol"),
            T6AuthorizationValidatorV1.HashRole(configuration, "t6-numerical-protocol"),
            package.Bridge.UpdSourceGateRegisterSha256,
            package.Bridge.ParentEr07AAuthorizationId,
            package.Bridge.ParentEr07AAuthorizationSha256,
            package.Bridge.ParentAuthorizationStatus,
            false,
            !package.Bridge.FixtureOnly,
            false,
            "ATOMIC_CREATE_NEW_BY_AUTHORIZATION_ID_AND_AUTHORIZATION_SHA256_V1");
    }
}

public static class T6JsonLeafEnumerator
{
    public static IReadOnlyList<(string Path, JsonValueKind Kind)> Enumerate(JsonElement root)
    {
        var result = new List<(string, JsonValueKind)>();
        Walk(root, "$", result);
        return result;
    }

    private static void Walk(JsonElement element, string path, List<(string, JsonValueKind)> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                Walk(property.Value, path + "." + property.Name, result);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) Walk(item, $"{path}[{index++}]", result);
            if (index == 0) result.Add((path, JsonValueKind.Array));
            return;
        }
        result.Add((path, element.ValueKind));
    }
}

public static class T6BridgeCoverageGateV1
{
    public static void Validate(int sourceLeafFieldCount, int bindingEntryCount)
    {
        if (sourceLeafFieldCount <= 0 || sourceLeafFieldCount != bindingEntryCount)
            throw new InvalidDataException("T6_BRIDGE_MAPPING_INCOMPLETE");
    }
}

public static class T6RuntimeSetGateV1
{
    public static void Validate(IReadOnlyList<string> rows, int episodeTotal, bool fixtureOnly)
    {
        if (rows.Count != 8 || !rows.SequenceEqual(T6FrozenConstants.Rows, StringComparer.Ordinal))
            throw new InvalidDataException("T6_EFFECTIVE_ROW_SET_MISMATCH");
        if (!fixtureOnly && episodeTotal != T6FrozenConstants.TotalEpisodes)
            throw new InvalidDataException("T6_PRODUCTION_EPISODE_TOTAL_INVALID");
        if (fixtureOnly && episodeTotal is < 8 or > 24)
            throw new InvalidDataException("T6_FIXTURE_EPISODE_TOTAL_INVALID");
    }
}
