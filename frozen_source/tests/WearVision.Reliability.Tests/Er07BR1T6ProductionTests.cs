using System.Text;
using System.Text.Json;
using WearVision.Reliability.Experiments;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Tests;

[TestClass]
public sealed class Er07BR1T6ProductionTests
{
    private static readonly string Workspace = Directory.GetParent(TestSupport.WorkspaceRoot)!.FullName;
    private static readonly string DesignRoot = Path.Combine(Workspace, "authorities", "er07a", "design");
    private static readonly string ParentConfigZip = Path.Combine(Workspace, "authorities", "er07a", "ER-07A_1152_T6_CONFIG_SET_2026-08-21.zip");
    private static readonly string SourceSnapshot = Path.Combine(Workspace, "authorities", "er06c", "ER-06C_1152_SOURCE_SNAPSHOT_2026-08-21.zip");
    private static readonly string UpdRegister = Path.Combine("C:\\Users\\brovi\\.codex\\outputs", "WearVision_ER07A_2026-08-21", "ER-07A_1152_UPD_SOURCE_GOVERNANCE_REGISTER_2026-08-21.csv");
    private static readonly string Executable = typeof(T6ScientificExecutionService).Assembly.Location;
    private static string _root = string.Empty;
    private static string _package = string.Empty;
    private static string _approvedOutput = string.Empty;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _root = Path.Combine(Path.GetTempPath(), "WearVision-ER07B-R1-Tests-" + Guid.NewGuid().ToString("N"));
        _approvedOutput = Path.Combine(_root, "runs");
        _package = Path.Combine(_root, "fixture-runtime.zip");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_approvedOutput);
        _ = BuildPackage(_package);
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void ER07A_Exact_Design_Parses_All_Fields_And_Rows()
    {
        var design = new Er07ADesignPackageParserV1().Load(DesignRoot);
        Assert.HasCount(12, design.MemberHashes);
        Assert.IsGreaterThan(0, design.LeafFieldCount);
        CollectionAssert.AreEqual(T6FrozenConstants.Rows.ToArray(), design.Rows.Select(x => x.RowId).ToArray());
        Assert.AreEqual(2496, design.Rows.Sum(x => x.EpisodeCount));
    }

    [TestMethod]
    public void Invalid_ER07A_Schema_Is_Rejected_With_Precise_Reason()
    {
        var copy = Path.Combine(_root, "invalid-design-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(DesignRoot, copy);
        var target = Path.Combine(copy, "claims.json");
        File.WriteAllText(target, File.ReadAllText(target, Encoding.UTF8).Replace("er07a-claims-v1", "invalid-schema", StringComparison.Ordinal), new UTF8Encoding(false));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => new Er07ADesignPackageParserV1().Load(copy));
        StringAssert.Contains(error.Message, "ER07A_SCHEMA_INVALID:claims.json");
    }

    [TestMethod]
    public void Missing_Bridge_Mapping_Is_Rejected() =>
        Assert.ThrowsExactly<InvalidDataException>(() => T6BridgeCoverageGateV1.Validate(100, 99));

    [TestMethod]
    public void Missing_Effective_Row_Is_Rejected() =>
        Assert.ThrowsExactly<InvalidDataException>(() => T6RuntimeSetGateV1.Validate(T6FrozenConstants.Rows.Take(7).ToArray(), 2496, false));

    [TestMethod]
    public void Wrong_Production_Episode_Total_Is_Rejected() =>
        Assert.ThrowsExactly<InvalidDataException>(() => T6RuntimeSetGateV1.Validate(T6FrozenConstants.Rows, 2495, false));

    [TestMethod]
    public void Runtime_Package_Has_Eight_Explicit_Effective_Rows()
    {
        using var package = T6PackageMaterialization.Open(_package);
        Assert.HasCount(8, package.Configuration.EffectiveExperiments);
        Assert.AreEqual(16, package.Configuration.EffectiveExperiments.Values.Sum(x => x.EpisodeCount));
        Assert.IsTrue(package.Configuration.EffectiveExperiments.Values.All(x => x.T6Binding is not null));
        Assert.AreEqual(package.Bridge.SourceLeafFieldCount, package.Bridge.BindingEntryCount);
        Assert.IsTrue(package.Configuration.OwnerApproval.Decisions.All(x => !x.Notes.Contains("no T6", StringComparison.OrdinalIgnoreCase)));
        Assert.HasCount(1, package.Configuration.Manifest.Files.Where(x => x.Role == "parent-owner-approval-lineage"));
    }

    [TestMethod]
    public void Bridge_Output_Is_Byte_Deterministic()
    {
        var second = Path.Combine(_root, "fixture-runtime-second.zip");
        _ = BuildPackage(second);
        Assert.AreEqual(ConfigurationSetLoaderV2.HashFile(_package), ConfigurationSetLoaderV2.HashFile(second));
    }

    [TestMethod]
    public void Authorization_Is_Bound_To_All_Exact_Hashes()
    {
        var (_, authorization, _) = Issue("ER07B-R1-FIXTURE-AUTH-BINDINGS");
        using var package = T6PackageMaterialization.Open(_package);
        Assert.AreEqual(package.PackageSha256, authorization.RuntimeConfigSetSha256);
        Assert.HasCount(T6FrozenConstants.Rows.Count, authorization.AuthorizedRows);
        Assert.AreEqual(16, authorization.EpisodeTotal);
        Assert.AreEqual("SUPERSEDED_UNCONSUMED_PARENT", authorization.ParentAuthorizationStatus);
        Assert.IsFalse(authorization.WildcardAllowed);
    }

    [TestMethod]
    public void Unauthorized_Row_Is_Rejected()
    {
        var (path, authorization, identity) = Issue("ER07B-R1-FIXTURE-UNAUTHORIZED-ROW");
        authorization = authorization with { AuthorizedRows = authorization.AuthorizedRows.Select((x, i) => i == 0 ? x with { RowId = "T6-UNAUTHORIZED" } : x).ToArray() };
        WriteAuthorization(path, authorization);
        using var package = T6PackageMaterialization.Open(_package);
        var report = new T6AuthorizationValidatorV1().Validate(package, authorization, identity, "ROW-REJECT");
        Assert.IsFalse(report.Accepted);
        CollectionAssert.Contains(report.Issues.ToArray(), "T6_AUTHORIZED_ROW_SET_MISMATCH");
    }

    [TestMethod]
    public void Source_Mismatch_Is_Rejected()
    {
        var (_, authorization, identity) = Issue("ER07B-R1-FIXTURE-SOURCE-MISMATCH");
        authorization = authorization with { SourceSnapshotSha256 = new string('A', 64) };
        using var package = T6PackageMaterialization.Open(_package);
        var report = new T6AuthorizationValidatorV1().Validate(package, authorization, identity, "SOURCE-REJECT");
        CollectionAssert.Contains(report.Issues.ToArray(), "T6_SOURCE_MISMATCH");
    }

    [TestMethod]
    public void Config_Mismatch_Is_Rejected()
    {
        var (_, authorization, identity) = Issue("ER07B-R1-FIXTURE-CONFIG-MISMATCH");
        authorization = authorization with { RuntimeConfigSetSha256 = new string('A', 64) };
        using var package = T6PackageMaterialization.Open(_package);
        var report = new T6AuthorizationValidatorV1().Validate(package, authorization, identity, "CONFIG-REJECT");
        CollectionAssert.Contains(report.Issues.ToArray(), "T6_CONFIG_SET_HASH_MISMATCH");
    }

    [TestMethod]
    public void Authorization_Can_Be_Consumed_Exactly_Once()
    {
        var (path, authorization, _) = Issue("ER07B-R1-FIXTURE-CONSUME-ONCE");
        var store = Path.Combine(_root, "consume-once");
        var subject = new T6AuthorizationConsumptionStoreV1(() => DateTimeOffset.UnixEpoch);
        _ = subject.Consume(store, path, authorization, "CONSUME-1");
        Assert.ThrowsExactly<InvalidOperationException>(() => subject.Consume(store, path, authorization, "CONSUME-2"));
    }

    [TestMethod]
    public async Task Existing_Output_Directory_Is_Rejected()
    {
        var (path, _, _) = Issue("ER07B-R1-FIXTURE-EXISTING-OUTPUT");
        var runId = "EXISTING-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_approvedOutput, runId));
        var result = await new T6ScientificExecutionService().ExecuteAsync(_package, path, SourceSnapshot, Executable, runId, CancellationToken.None);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual("T6_OUTPUT_DIRECTORY_EXISTS", result.ErrorCode);
        Assert.IsFalse(result.AuthorizationConsumed);
    }

    [TestMethod]
    public async Task Raw_Writer_Failure_Blocks_Completion()
    {
        var (path, _, _) = Issue("ER07B-R1-FIXTURE-WRITER-FAILURE");
        var runId = "WRITER-FAIL-" + Guid.NewGuid().ToString("N");
        var subject = new T6ScientificExecutionService(writerFailureInjector: relative => relative.StartsWith("raw", StringComparison.Ordinal));
        var result = await subject.ExecuteAsync(_package, path, SourceSnapshot, Executable, runId, CancellationToken.None);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsNull(result.CompletionManifestSha256);
        Assert.IsFalse(File.Exists(Path.Combine(_approvedOutput, runId, "completion-manifest.json")));
    }

    [TestMethod]
    public async Task Resource_Breach_Blocks_Completion()
    {
        var (path, _, _) = Issue("ER07B-R1-FIXTURE-RESOURCE-BREACH");
        var runId = "RESOURCE-FAIL-" + Guid.NewGuid().ToString("N");
        var subject = new T6ScientificExecutionService(resourceProbe: new ResourceBreachProbe());
        var result = await subject.ExecuteAsync(_package, path, SourceSnapshot, Executable, runId, CancellationToken.None);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.AreEqual("T6_WALL_CLOCK_CEILING_EXCEEDED", result.ErrorCode);
        Assert.IsFalse(File.Exists(Path.Combine(_approvedOutput, runId, "completion-manifest.json")));
    }

    [TestMethod]
    public void Missing_Statistical_Input_Blocks_Completion()
    {
        var state = ValidCompletionState() with { StatisticalInputsPresent = false, PairedContrastInputCount = 0 };
        Assert.ThrowsExactly<InvalidOperationException>(() => T6CompletionGateV1.Validate(state, ValidRawCounts()));
    }

    [TestMethod]
    public void Duplicate_Episode_Blocks_Completion()
    {
        var state = ValidCompletionState() with { AccountedEpisodeIds = ["EP-1", "EP-1"] };
        Assert.ThrowsExactly<InvalidOperationException>(() => T6CompletionGateV1.Validate(state, ValidRawCounts()));
    }

    [TestMethod]
    public void Missing_Authorization_Consumption_Blocks_Completion()
    {
        var state = ValidCompletionState() with { AuthorizationConsumptionPresent = false };
        Assert.ThrowsExactly<InvalidOperationException>(() => T6CompletionGateV1.Validate(state, ValidRawCounts()));
    }

    [TestMethod]
    public void Missing_Lineage_Blocks_Completion()
    {
        var state = ValidCompletionState() with { LineageComplete = false };
        Assert.ThrowsExactly<InvalidOperationException>(() => T6CompletionGateV1.Validate(state, ValidRawCounts()));
    }

    [TestMethod]
    public void Missing_Mandatory_Raw_Output_Blocks_Completion()
    {
        var counts = ValidRawCounts();
        counts["SCH-REL"] = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => T6CompletionGateV1.Validate(ValidCompletionState(), counts));
    }

    [TestMethod]
    public void T5_Config_Cannot_Execute_T6() =>
        Assert.ThrowsExactly<InvalidDataException>(() => T6PackageMaterialization.Open(Path.Combine(TestSupport.WorkspaceRoot, "configs", "fixtures", "t5-production-e2e")));

    [TestMethod]
    public void Old_ER07A_Authorization_Cannot_Execute_New_Source()
    {
        var old = Path.Combine(Workspace, "authorities", "er07a", "ER-07A_1152_T6_AUTHORIZATION_MANIFEST_2026-08-21.json");
        Assert.ThrowsExactly<JsonException>(() => CanonicalJson.DeserializeStrict<T6AuthorizationManifestV1>(File.ReadAllText(old, Encoding.UTF8)));
    }

    [TestMethod]
    public void Approved_Execute_Path_Does_Not_Call_Legacy_Hard_Block()
    {
        var source = File.ReadAllText(Path.Combine(TestSupport.WorkspaceRoot, "src", "WearVision.Reliability.Experiments", "ExperimentHarness.cs"), Encoding.UTF8);
        StringAssert.Contains(source, "new T6ScientificExecutionService().ExecuteAsync(args[2], args[3], args[4]");
        Assert.IsFalse(source.Contains("args.Length >= 2 && args[0] == \"t6\" && args[1] == \"execute\" ? RunT6()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Exact_Resource_Ceilings_Are_Enforced()
    {
        var guard = new T6ResourceGuardV1(T6FrozenConstants.MaximumWallClockSeconds, T6FrozenConstants.MaximumStorageBytes, new NormalProbe(), () => 0);
        Assert.AreEqual(21600d, guard.MaximumWallClockSeconds);
        Assert.AreEqual(4294967296L, guard.MaximumStorageBytes);
        Assert.ThrowsExactly<InvalidDataException>(() => new T6ResourceGuardV1(21599, 4294967296L, new NormalProbe(), () => 0));
        Assert.ThrowsExactly<InvalidDataException>(() => new T6ResourceGuardV1(21600, 4294967295L, new NormalProbe(), () => 0));
    }

    [TestMethod]
    public async Task Positive_Fixture_Uses_Production_Service_And_Is_Never_Manuscript_Eligible()
    {
        var (path, _, _) = Issue("ER07B-R1-FIXTURE-POSITIVE");
        var runId = "POSITIVE-" + Guid.NewGuid().ToString("N");
        var result = await new T6ScientificExecutionService().ExecuteAsync(_package, path, SourceSnapshot, Executable, runId, CancellationToken.None);
        Assert.AreEqual(0, result.ExitCode, result.ErrorCode);
        Assert.HasCount(8, result.ExecutedRows);
        Assert.AreEqual(16, result.AccountedEpisodes);
        Assert.IsTrue(result.AuthorizationConsumed);
        Assert.IsTrue(result.ExecutionStarted);
        Assert.IsFalse(result.RealT6Executed);
        var completionPath = Path.Combine(result.RunRoot, "completion-manifest.json");
        var completion = CanonicalJson.DeserializeStrict<T6CompletionManifestV1>(File.ReadAllText(completionPath, Encoding.UTF8));
        Assert.IsTrue(completion.Completed);
        Assert.IsFalse(completion.ScientificEligibility);
        Assert.IsFalse(completion.ManuscriptEligibility);
        StringAssert.Contains(completion.CompletionWriteRule, "written after all evidence writers close");
        Assert.IsGreaterThan(0, completion.ProtocolV2CallCount);
        Assert.IsGreaterThan(0, completion.PairedContrastInputCount);
        Assert.IsGreaterThan(0, completion.AblationInputCount);
    }

    private static string BuildPackage(string destination) => new T6RuntimeSchemaBridgeV1().Build(
        DesignRoot,
        ParentConfigZip,
        Path.Combine(TestSupport.WorkspaceRoot, "configs", "fixtures", "t5-production-e2e"),
        Path.Combine(TestSupport.SchemaRoot, "raw-schema-registry.csv"),
        SourceSnapshot,
        UpdRegister,
        destination,
        _approvedOutput,
        true);

    private static (string Path, T6AuthorizationManifestV1 Authorization, T6ExecutionIdentityV1 Identity) Issue(string id)
    {
        using var package = T6PackageMaterialization.Open(_package);
        var identity = T6ExecutionIdentityV1.Capture(SourceSnapshot, Executable);
        var authorization = T6AuthorizationIssuerV1.Issue(package, identity, id + "-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(_root, authorization.AuthorizationId + ".json");
        WriteAuthorization(path, authorization);
        return (path, authorization, identity);
    }

    private static void WriteAuthorization(string path, T6AuthorizationManifestV1 authorization) => File.WriteAllBytes(path, CanonicalJson.Serialize(authorization));

    private static T6CompletionStateV1 ValidCompletionState() => new(
        T6FrozenConstants.Rows,
        T6FrozenConstants.Rows,
        ["EP-1", "EP-2"],
        ["EP-1", "EP-2"],
        true,
        true,
        true,
        false,
        false,
        1,
        1,
        1,
        1,
        1);

    private static Dictionary<string, long> ValidRawCounts() => new(StringComparer.Ordinal)
    {
        ["SCH-SPL"] = 1,
        ["SCH-PST"] = 1,
        ["SCH-TRJ"] = 1,
        ["SCH-DIN"] = 1,
        ["SCH-PEV"] = 1,
        ["SCH-BRN"] = 1,
        ["SCH-DEC"] = 1,
        ["SCH-FEV"] = 1,
        ["SCH-CON"] = 1,
        ["SCH-REL"] = 1,
        ["SCH-NUM"] = 1
    };

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private sealed class ResourceBreachProbe : IT6ResourceProbe
    {
        public double ElapsedSeconds => 21601d;
        public long PeakWorkingSetBytes => 0L;
    }

    private sealed class NormalProbe : IT6ResourceProbe
    {
        public double ElapsedSeconds => 0d;
        public long PeakWorkingSetBytes => 0L;
    }
}
