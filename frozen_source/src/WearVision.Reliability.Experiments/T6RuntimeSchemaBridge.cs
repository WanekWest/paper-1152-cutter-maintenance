using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;

namespace WearVision.Reliability.Experiments;

public sealed record Er07AScenarioRowV1(
    string RowId,
    string ScenarioId,
    int EpisodeCount,
    double ProbabilityHigh,
    IReadOnlyList<double> PosteriorSupport,
    IReadOnlyList<double> PosteriorWeights,
    IReadOnlyList<string> Policies,
    string EvidenceClass);

public sealed record Er07ADesignAuthorityV1(
    string Root,
    IReadOnlyDictionary<string, string> MemberHashes,
    IReadOnlyList<Er07AScenarioRowV1> Rows,
    IReadOnlyDictionary<string, string> Documents,
    int LeafFieldCount);

public sealed class Er07ADesignPackageParserV1
{
    private static readonly IReadOnlyDictionary<string, string> Schemas = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ablation_and_robustness.json"] = "er07a-ablation-robustness-v1",
        ["authority_binding.json"] = "er07a-authority-binding-v1",
        ["authorization_allowlist.json"] = "er07a-allowlist-v1",
        ["claims.json"] = "er07a-claims-v1",
        ["config_set_manifest.json"] = "er07a-config-set-manifest-v1",
        ["numerical_protocol_v2.json"] = "er07a-numerical-binding-v1",
        ["output_policy.json"] = "er07a-output-v1",
        ["policies_actions_outcomes.json"] = "er07a-policy-outcome-v1",
        ["resource_ceiling.json"] = "er07a-resource-v1",
        ["rng_manifest.json"] = "er07a-rng-v1",
        ["sample_size_and_scenarios.json"] = "er07a-sample-scenario-v1",
        ["source_governance.json"] = "er07a-upd-source-governance-v1",
        ["statistical_protocol.json"] = "er07a-statistics-v1"
    };

    public Er07ADesignAuthorityV1 Load(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("ER07A_DESIGN_ROOT_MISSING");
        var documents = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var leafCount = 0;
        foreach (var pair in Schemas)
        {
            var path = Path.Combine(root, pair.Key);
            if (!File.Exists(path)) throw new InvalidDataException("ER07A_REQUIRED_ARTIFACT_MISSING:" + pair.Key);
            var text = File.ReadAllText(path, Encoding.UTF8);
            using var document = JsonDocument.Parse(text);
            var schema = RequiredString(document.RootElement, "schema_version", pair.Key);
            if (schema != pair.Value) throw new InvalidDataException($"ER07A_SCHEMA_INVALID:{pair.Key}:expected={pair.Value}:actual={schema}");
            documents.Add(pair.Key, text);
            leafCount += T6JsonLeafEnumerator.Enumerate(document.RootElement).Count;
        }

        using var manifestDocument = JsonDocument.Parse(documents["config_set_manifest.json"]);
        var manifest = manifestDocument.RootElement;
        if (RequiredString(manifest, "source_snapshot_sha256", "config_set_manifest.json") != T6FrozenConstants.ParentSourceSha256)
            throw new InvalidDataException("ER07A_PARENT_SOURCE_HASH_INVALID");
        if (RequiredString(manifest, "numerical_protocol_version", "config_set_manifest.json") != T6FrozenConstants.NumericalProtocol)
            throw new InvalidDataException("ER07A_NUMERICAL_PROTOCOL_INVALID");
        var memberHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in RequiredArray(manifest, "members", "config_set_manifest.json").EnumerateArray())
        {
            var relative = RequiredString(item, "relative_path", "config_set_manifest.json#members");
            var expectedHash = RequiredString(item, "sha256", "config_set_manifest.json#members");
            var expectedBytes = RequiredInt32(item, "bytes", "config_set_manifest.json#members");
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) throw new InvalidDataException("ER07A_MEMBER_MISSING:" + relative);
            if (new FileInfo(path).Length != expectedBytes) throw new InvalidDataException("ER07A_MEMBER_BYTE_COUNT_MISMATCH:" + relative);
            var actualHash = ConfigurationSetLoaderV2.HashFile(path);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ER07A_MEMBER_HASH_MISMATCH:" + relative);
            memberHashes.Add(relative, actualHash);
        }
        if (memberHashes.Count != 12 || memberHashes.Keys.Any(x => x == "config_set_manifest.json"))
            throw new InvalidDataException("ER07A_MEMBER_INVENTORY_INVALID");

        using var sampleDocument = JsonDocument.Parse(documents["sample_size_and_scenarios.json"]);
        var sample = sampleDocument.RootElement;
        RequireExact(sample, "delta_star", T6FrozenConstants.DeltaStar, "ER07A_DELTA_STAR_INVALID");
        RequireExact(sample, "h_star", T6FrozenConstants.HStar, "ER07A_H_STAR_INVALID");
        if (RequiredInt32(sample, "n_total", "sample_size_and_scenarios.json") != T6FrozenConstants.TotalEpisodes)
            throw new InvalidDataException("ER07A_EPISODE_TOTAL_INVALID");
        if (RequiredInt32(sample, "bootstrap_replicates", "sample_size_and_scenarios.json") != 9999)
            throw new InvalidDataException("ER07A_BOOTSTRAP_REPLICATES_INVALID");
        RequireExact(sample, "alpha_family", 0.05d, "ER07A_ALPHA_FAMILY_INVALID");
        var rows = new List<Er07AScenarioRowV1>();
        foreach (var row in RequiredArray(sample, "scenario_rows", "sample_size_and_scenarios.json").EnumerateArray())
        {
            var rowId = RequiredString(row, "row_id", "sample_size_and_scenarios.json#scenario_rows");
            var evidence = rowId switch
            {
                "T6-S0-PRIMARY" => "T6_MODEL_BASED_PRIMARY_SCIENTIFIC_EVIDENCE",
                "T6-S1-SEV050" or "T6-S2-SEV075" => "T6_MODEL_BASED_SECONDARY_ROBUSTNESS_EVIDENCE",
                _ => "T6_MODEL_BASED_SECONDARY_ABLATION_EVIDENCE"
            };
            rows.Add(new(
                rowId,
                RequiredString(row, "scenario", "sample_size_and_scenarios.json#scenario_rows"),
                RequiredInt32(row, "episode_count", "sample_size_and_scenarios.json#scenario_rows"),
                RequiredDouble(row, "p_high", "sample_size_and_scenarios.json#scenario_rows"),
                ParseNumberArrayString(RequiredString(row, "posterior_support", "sample_size_and_scenarios.json#scenario_rows")),
                ParseNumberArrayString(RequiredString(row, "posterior_weights", "sample_size_and_scenarios.json#scenario_rows")),
                RequiredString(row, "policies", "sample_size_and_scenarios.json#scenario_rows").Split(';'),
                evidence));
        }
        if (!rows.Select(x => x.RowId).SequenceEqual(T6FrozenConstants.Rows, StringComparer.Ordinal)) throw new InvalidDataException("ER07A_ROW_ORDER_INVALID");
        foreach (var row in rows)
        {
            if (row.EpisodeCount != T6FrozenConstants.EpisodeCounts[row.RowId]) throw new InvalidDataException("ER07A_ROW_EPISODE_COUNT_INVALID:" + row.RowId);
            if (row.PosteriorSupport.Count != 2 || row.PosteriorWeights.Count != 2 || Math.Abs(row.PosteriorWeights.Sum() - 1d) > 1e-12)
                throw new InvalidDataException("ER07A_SCENARIO_BELIEF_INVALID:" + row.RowId);
        }

        using var allowlistDocument = JsonDocument.Parse(documents["authorization_allowlist.json"]);
        var allowlist = allowlistDocument.RootElement;
        var allowedRows = RequiredArray(allowlist, "authorized_rows", "authorization_allowlist.json").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (!allowedRows.SequenceEqual(T6FrozenConstants.Rows, StringComparer.Ordinal)) throw new InvalidDataException("ER07A_ALLOWLIST_INVALID");
        if (RequiredBoolean(allowlist, "wildcard", "authorization_allowlist.json")) throw new InvalidDataException("ER07A_WILDCARD_FORBIDDEN");
        if (RequiredBoolean(allowlist, "execution_command_present", "authorization_allowlist.json")) throw new InvalidDataException("ER07A_PARENT_COMMAND_STATE_INVALID");

        using var numericalDocument = JsonDocument.Parse(documents["numerical_protocol_v2.json"]);
        var numerical = numericalDocument.RootElement;
        if (RequiredString(numerical, "protocol_version", "numerical_protocol_v2.json") != T6FrozenConstants.NumericalProtocol)
            throw new InvalidDataException("ER07A_NUMERICAL_PROTOCOL_INVALID");
        RequireExact(numerical, "epsilon", T6FrozenConstants.Epsilon, "ER07A_EPSILON_INVALID");
        RequireExact(numerical, "epsilon_remaining", T6FrozenConstants.EpsilonRemaining, "ER07A_EPSILON_REMAINING_INVALID");
        if (!RequiredBoolean(numerical, "fail_closed", "numerical_protocol_v2.json")) throw new InvalidDataException("ER07A_NUMERICAL_FAIL_CLOSED_REQUIRED");

        using var resourceDocument = JsonDocument.Parse(documents["resource_ceiling.json"]);
        var resources = resourceDocument.RootElement;
        RequireExact(resources, "approved_wall_clock_ceiling_seconds", T6FrozenConstants.MaximumWallClockSeconds, "ER07A_WALL_CLOCK_INVALID");
        if (resources.GetProperty("approved_storage_ceiling_bytes").GetInt64() != T6FrozenConstants.MaximumStorageBytes)
            throw new InvalidDataException("ER07A_STORAGE_INVALID");

        using var sourceDocument = JsonDocument.Parse(documents["source_governance.json"]);
        if (RequiredString(sourceDocument.RootElement, "gate", "source_governance.json") != "PASS") throw new InvalidDataException("ER07A_UPD_GATE_NOT_PASS");
        var metrics = sourceDocument.RootElement.GetProperty("metrics");
        if (RequiredInt32(metrics, "reference_count", "source_governance.json#metrics") != 38 ||
            !RequiredBoolean(metrics, "doi_edn_complete", "source_governance.json#metrics"))
            throw new InvalidDataException("ER07A_UPD_METRICS_INVALID");

        return new(root, memberHashes, rows, documents, leafCount);
    }

    private static string RequiredString(JsonElement element, string name, string context) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"ER07A_REQUIRED_STRING_MISSING:{context}:{name}");

    private static int RequiredInt32(JsonElement element, string name, string context) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException($"ER07A_REQUIRED_INT_MISSING:{context}:{name}");

    private static double RequiredDouble(JsonElement element, string name, string context) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"ER07A_REQUIRED_NUMBER_MISSING:{context}:{name}");

    private static bool RequiredBoolean(JsonElement element, string name, string context) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"ER07A_REQUIRED_BOOLEAN_MISSING:{context}:{name}");

    private static JsonElement RequiredArray(JsonElement element, string name, string context) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"ER07A_REQUIRED_ARRAY_MISSING:{context}:{name}");

    private static void RequireExact(JsonElement element, string name, double expected, string code)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetDouble(out var actual) || actual != expected)
            throw new InvalidDataException(code);
    }

    private static double[] ParseNumberArrayString(string value)
    {
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ER07A_STRING_ARRAY_INVALID");
        return document.RootElement.EnumerateArray().Select(x => x.GetDouble()).ToArray();
    }
}

public sealed class T6RuntimeSchemaBridgeV1
{
    public string Build(
        string designDirectory,
        string parentConfigZipPath,
        string baseConfigDirectory,
        string rawSchemaRegistryPath,
        string newSourceSnapshotPath,
        string updRegisterPath,
        string destinationZip,
        string approvedOutputRoot,
        bool fixtureOnly)
    {
        var design = new Er07ADesignPackageParserV1().Load(designDirectory);
        if (!ConfigurationSetLoaderV2.HashFile(parentConfigZipPath).Equals(T6FrozenConstants.ParentConfigZipSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ER07A_CONFIG_ZIP_HASH_MISMATCH");
        if (!ConfigurationSetLoaderV2.HashFile(updRegisterPath).Equals(T6FrozenConstants.UpdRegisterSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("T6_UPD_REGISTER_HASH_MISMATCH");
        var baseConfiguration = new ConfigurationSetLoaderV2().Load(baseConfigDirectory);
        var sourceSha = ConfigurationSetLoaderV2.HashFile(newSourceSnapshotPath);
        var executablePath = typeof(T6RuntimeSchemaBridgeV1).Assembly.Location;
        var executableSha = ConfigurationSetLoaderV2.HashFile(executablePath);
        var destination = Path.GetFullPath(destinationZip);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("T6_PACKAGE_DESTINATION_EXISTS");
        var staging = Path.Combine(Path.GetTempPath(), "WearVision-T6-Bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            BuildDirectory(design, baseConfiguration, rawSchemaRegistryPath, sourceSha, executableSha, staging, approvedOutputRoot, fixtureOnly);
            _ = new ConfigurationSetLoaderV2().Load(staging);
            return T6PackageArchive.Create(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void BuildDirectory(
        Er07ADesignAuthorityV1 design,
        LoadedConfigurationSetV2 source,
        string rawSchemaRegistryPath,
        string newSourceSha,
        string executableSha,
        string root,
        string approvedOutputRoot,
        bool fixtureOnly)
    {
        var profile = fixtureOnly ? ConfigurationProfileKind.TestFixtureOnly : ConfigurationProfileKind.T6Scientific;
        var stage = fixtureOnly ? RunStage.T4 : RunStage.T6;
        var runtimeStatus = fixtureOnly ? RuntimeValueStatus.TestFixtureOnly : RuntimeValueStatus.OwnerApproved;
        var owner = fixtureOnly ? "ER07B_R1_TEST_FIXTURE_ONLY" : "ER07B_R1_EXACT_HASH_RECOVERY";
        var rows = design.Rows.Select(row => row with { EpisodeCount = fixtureOnly ? 2 : row.EpisodeCount }).ToArray();
        var files = new List<ConfigFileReferenceV2>();

        foreach (var reference in source.Manifest.Files.Where(x => x.Role.Contains("artifact", StringComparison.OrdinalIgnoreCase)))
        {
            var from = Path.Combine(source.Root, reference.RelativePath);
            var to = Path.Combine(root, reference.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            files.Add(reference);
        }

        var designHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in design.Documents)
        {
            var relative = "design-authority/" + pair.Key;
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pair.Value, new UTF8Encoding(false));
            var hash = ConfigurationSetLoaderV2.HashFile(path);
            designHashes.Add(pair.Key, hash);
            var role = pair.Key switch
            {
                "statistical_protocol.json" => "t6-statistical-protocol",
                "numerical_protocol_v2.json" => "t6-numerical-protocol",
                _ => "er07a-design-authority"
            };
            files.Add(new(relative, hash, role));
        }

        var model = source.Model with { ConfigId = fixtureOnly ? "model.er07b-r1.t6-fixture.v1" : "model.er07b-r1.t6-production.v1", ProfileKind = profile };
        var reliability = source.Reliability with
        {
            ConfigId = fixtureOnly ? "reliability.er07b-r1.t6-fixture.v1" : "reliability.er07b-r1.t6-production.v1",
            Epsilon = new(T6FrozenConstants.Epsilon, runtimeStatus, owner, "ER07A_NUMERICAL_PROTOCOL_V2"),
            EpsilonRemaining = new(T6FrozenConstants.EpsilonRemaining, runtimeStatus, owner, "ER07A_NUMERICAL_PROTOCOL_V2")
        };
        var numerical = source.Numerical with
        {
            ConfigId = fixtureOnly ? "numerical.er07b-r1.t6-fixture.v1" : "numerical.er07b-r1.t6-production.v1",
            MasterSeedUInt64 = 13_691_961_035_017_441_233UL,
            McMinimumPaths = fixtureOnly ? 8 : 4096,
            McBatchSize = fixtureOnly ? 4 : 2048,
            McMaximumPaths = fixtureOnly ? 32 : 1_048_576,
            StableBatchCount = fixtureOnly ? 2 : 3,
            FailureIntervalMethod = "WilsonOneSided99",
            QuadratureAbsoluteTolerance = 1e-10,
            QuadratureRelativeTolerance = 1e-8,
            MaximumDegreeOfParallelism = 1,
            ParallelismLevel = "TrajectoryOnly",
            DiagnosticVerbosity = fixtureOnly ? "TEST_FIXTURE_ONLY_T6_PROTOCOL_V2_E2E" : "T6_PRODUCTION_SCIENTIFIC_RAW_EVIDENCE"
        };
        var output = source.Output with
        {
            ConfigId = fixtureOnly ? "output.er07b-r1.t6-fixture.v1" : "output.er07b-r1.t6-production.v1",
            ApprovedRoot = Path.GetFullPath(approvedOutputRoot),
            RunIdPattern = fixtureOnly ? "ER07B-R1-T6-FIXTURE-[A-Z0-9-]+" : "ER07B-R2-T6-[A-Z0-9-]+",
            RefuseExisting = true,
            CompressionEnabled = false,
            RetainRaw = true,
            RetainBatches = true,
            CompletionManifestLast = true,
            ManuscriptRenderingEnabled = false
        };
        var compatibility = source.Compatibility with
        {
            ConfigId = fixtureOnly ? "compatibility.er07b-r1.t6-fixture.v1" : "compatibility.er07b-r1.t6-production.v1",
            ProtocolVersion = T6FrozenConstants.NumericalProtocol
        };
        var resources = source.Resources with
        {
            ConfigId = fixtureOnly ? "resources.er07b-r1.t6-fixture.v1" : "resources.er07b-r1.t6-production.v1",
            MaximumWallClockSeconds = new(T6FrozenConstants.MaximumWallClockSeconds, runtimeStatus, owner, "ER07A_RESOURCE_CEILING"),
            MaximumStorageBytes = new(T6FrozenConstants.MaximumStorageBytes, runtimeStatus, owner, "ER07A_RESOURCE_CEILING"),
            MaximumDegreeOfParallelism = new(1, runtimeStatus, owner, "ER07A_RESOURCE_CEILING"),
            AllowEr04MaximumPaths = new(true, runtimeStatus, owner, "ER07A_NUMERICAL_PROTOCOL_V2")
        };
        var activeApproval = new OwnerApprovalManifestV2(
            "owner-approval-v2",
            fixtureOnly ? "owner-approval.er07b-r1.t6-fixture.v1" : "owner-approval.er07b-r1.t6-production.v1",
            [
                new("T6-R1-01", OwnerApprovalState.Approved, "ER-06C exact source authority is preserved without scientific-model modification."),
                new("T6-R1-02", OwnerApprovalState.Approved, "ER-07A exact design authority and all member hashes are preserved by the versioned schema bridge."),
                new("T6-R1-03", OwnerApprovalState.Approved, fixtureOnly ? "TEST_FIXTURE_ONLY: eight authorized row identifiers with two episodes per row; never scientific or manuscript eligible." : "T6: exactly eight prospectively frozen rows and 2496 component episodes."),
                new("T6-R1-04", OwnerApprovalState.Approved, "WV-1152-CHANCE-CERT-2, epsilon=0.125 and epsilon_remaining=0.125 are exact ER-07A bindings."),
                new("T6-R1-05", OwnerApprovalState.Approved, "Write-once evidence, exact authorization consumption and 21600-second/4294967296-byte fail-closed ceilings are required."),
                new("T6-R1-06", OwnerApprovalState.Approved, "Parent T5 owner-approval bytes are retained as lineage and are not the active T6 approval authority.")
            ],
            OwnerApprovalState.Approved);

        WriteRegistered(root, files, "model_config_v2.json", "model", model);
        WriteRegistered(root, files, "reliability_config_v2.json", "reliability", reliability);
        WriteRegistered(root, files, "action_config_v2.json", "actions", source.Actions);
        WriteRegistered(root, files, "consequence_config_v2.json", "consequences", source.Consequences);
        WriteRegistered(root, files, "numerical_config_v2.json", "numerical", numerical);
        WriteRegistered(root, files, "baseline_config_v2.json", "baselines", source.Baselines);
        WriteRegistered(root, files, "output_policy_v2.json", "output-policy", output);
        WriteRegistered(root, files, "compatibility_manifest_v2.json", "compatibility", compatibility);
        WriteRegistered(root, files, "retirement_denylist.json", "retirement", source.Retirement);
        WriteRegistered(root, files, "lineage/er06c-parent-owner-approval.json", "parent-owner-approval-lineage", source.OwnerApproval);
        WriteRegistered(root, files, "owner_approval_manifest.json", "owner-approval", activeApproval);
        WriteRegistered(root, files, "resource_ceiling_config.json", "resources", resources);
        var registryRelative = "schemas/raw-schema-registry.csv";
        var registryTarget = Path.Combine(root, registryRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(registryTarget)!);
        File.Copy(rawSchemaRegistryPath, registryTarget);
        var registryHash = ConfigurationSetLoaderV2.HashFile(registryTarget);
        if (!registryHash.Equals(output.SchemaRegistrySha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("T6_RAW_SCHEMA_REGISTRY_HASH_MISMATCH");
        files.Add(new(registryRelative, registryHash, "raw-schema-registry"));

        var assignments = new List<SplitAssignmentV2>();
        foreach (var row in rows)
        {
            for (var index = 1; index <= row.EpisodeCount; index++)
            {
                var id = $"{row.RowId}-{index:D4}";
                assignments.Add(new(id, "ER07B-R1-MODEL-BASED-GENERATOR", DataPartition.LockedTest, $"CRN-{index:D4}", row.ScenarioId, "PROSPECTIVELY_FROZEN_T6_COMPONENT_EPISODE", "t6-split-v1", newSourceSha));
            }
        }
        var split = new SplitManifestV2("split-v2", fixtureOnly ? "split.er07b-r1.t6-fixture.v1" : "split.er07b-r1.t6-production.v1", assignments);
        WriteRegistered(root, files, "split_manifest_v2.json", "split", split);
        var splitHash = files.Single(x => x.Role == "split").Sha256;

        var runReferences = new List<ExperimentRunReferenceV2>();
        foreach (var row in rows)
        {
            var effective = CreateEffective(row, fixtureOnly, profile, stage, runtimeStatus, owner, split.ConfigId, splitHash, designHashes, newSourceSha, executableSha);
            var relative = "rows/" + row.RowId + ".json";
            WriteRegistered(root, files, relative, "effective-experiment", effective);
            var hash = files.Single(x => x.RelativePath == relative).Sha256;
            runReferences.Add(new(new(row.RowId), new(row.ScenarioId), relative, hash, effective.OutputSubroot, true));
        }
        var experimentSet = new ExperimentSetManifestV2(
            "experiment-set-v2",
            fixtureOnly ? "experiment-set.er07b-r1.t6-fixture.v1" : "experiment-set.er07b-r1.t6-production.v1",
            stage,
            profile,
            T6FrozenConstants.NumericalProtocol,
            runReferences);
        WriteRegistered(root, files, "experiment_set_manifest.json", "experiment-set", experimentSet);

        var bindingEntries = CreateBindingEntries(design, rows);
        var bindingRelative = "bridge/t6-schema-binding-register.csv";
        var bindingPath = Path.Combine(root, bindingRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(bindingPath)!);
        File.WriteAllText(bindingPath, FormatBindingCsv(bindingEntries), new UTF8Encoding(false));
        var bindingHash = ConfigurationSetLoaderV2.HashFile(bindingPath);
        files.Add(new(bindingRelative, bindingHash, "t6-binding-register"));
        var bridge = new T6BridgeManifestV1(
            "er07a-to-t6-runtime-bridge-v1",
            fixtureOnly ? "ER07B-R1-T6-FIXTURE-BRIDGE-V1" : "ER07B-R1-T6-PRODUCTION-BRIDGE-V1",
            T6FrozenConstants.ParentConfigZipSha256,
            designHashes["config_set_manifest.json"],
            T6FrozenConstants.ParentAuthorizationId,
            T6FrozenConstants.ParentAuthorizationSha256,
            "SUPERSEDED_UNCONSUMED_PARENT",
            T6FrozenConstants.ParentSourceSha256,
            newSourceSha,
            executableSha,
            T6FrozenConstants.UpdRegisterSha256,
            "PASS",
            T6FrozenConstants.NumericalProtocol,
            "t6 execute",
            true,
            fixtureOnly,
            rows.Select(x => x.RowId).ToArray(),
            rows.Sum(x => x.EpisodeCount),
            design.LeafFieldCount,
            bindingEntries.Count,
            bindingHash,
            designHashes);
        WriteRegistered(root, files, "bridge/t6-bridge-manifest.json", "t6-bridge-manifest", bridge);

        var manifest = new ConfigSetManifestV2(
            "config-set-v2",
            fixtureOnly ? "ER07B-R1-1152-T6-FIXTURE-RUNTIME-CONFIG-V1" : "ER07B-R1-1152-T6-EIGHT-ROWS-RUNTIME-CONFIG-V1",
            profile,
            fixtureOnly ? "TEST_FIXTURE_ONLY_T6_E2E_NOT_SCIENTIFIC_NOT_MANUSCRIPT" : "MODEL_BASED_T6_SCIENTIFIC_EXECUTION_RUNTIME",
            stage,
            "canonical-json-v1-utf8-no-bom",
            OwnerApprovalState.Approved,
            owner,
            "ER07B_R1_DETERMINISTIC_SCHEMA_BRIDGE",
            [],
            files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
        File.WriteAllBytes(Path.Combine(root, "config_set_manifest.json"), CanonicalJson.Serialize(manifest));
    }

    private static EffectiveExperimentConfigV2 CreateEffective(
        Er07AScenarioRowV1 row,
        bool fixtureOnly,
        ConfigurationProfileKind profile,
        RunStage stage,
        RuntimeValueStatus status,
        string owner,
        string splitId,
        string splitHash,
        IReadOnlyDictionary<string, string> designHashes,
        string newSourceSha,
        string executableSha)
    {
        var ablation = CreateAblation(row.RowId);
        var numerical = new T6NumericalBindingV1(
            T6FrozenConstants.NumericalProtocol,
            T6FrozenConstants.Epsilon,
            T6FrozenConstants.EpsilonRemaining,
            fixtureOnly ? 8 : 4096,
            fixtureOnly ? 4 : 2048,
            fixtureOnly ? 32 : 1_048_576,
            fixtureOnly ? 2 : 3,
            976_896,
            "WilsonOneSided99",
            1e-10,
            1e-8,
            1e-12,
            true,
            true,
            fixtureOnly);
        var rng = new T6RngBindingV1(
            "sha256-xoshiro256starstar-v1",
            "sha256-length-prefixed-fields-little-endian-v1",
            "sha256-xoshiro256starstar-crn-v1",
            "WEARVISION-1152-T6-2026-08-21",
            "D14FB4AA049E03BE7F055C53C086FF50614D38F99320FD2BB42E00A5BD721E3A",
            13_691_961_035_017_441_233UL,
            ["MasterSeed", "ExperimentId", "ScenarioId", "TrajectoryId", "DecisionEpoch", "PolicyOrActionId", "StreamName", "BatchIndex"],
            "BootstrapResample");
        var parents = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ER06C_SOURCE_SHA256"] = T6FrozenConstants.ParentSourceSha256,
            ["ER07A_CONFIG_ZIP_SHA256"] = T6FrozenConstants.ParentConfigZipSha256,
            ["ER07A_AUTHORIZATION_SHA256"] = T6FrozenConstants.ParentAuthorizationSha256,
            ["ER07A_NUMERICAL_PROTOCOL_SHA256"] = designHashes["numerical_protocol_v2.json"],
            ["ER07A_STATISTICAL_PROTOCOL_SHA256"] = designHashes["statistical_protocol.json"],
            ["ER07A_UPD_GOVERNANCE_SHA256"] = designHashes["source_governance.json"],
            ["NEW_SOURCE_SNAPSHOT_SHA256"] = newSourceSha,
            ["NEW_EXECUTABLE_SHA256"] = executableSha,
            ["UPD_SOURCE_GATE_REGISTER_SHA256"] = T6FrozenConstants.UpdRegisterSha256
        };
        var claims = row.RowId switch
        {
            "T6-S0-PRIMARY" => new[] { "CLAIM-1", "CLAIM-2", "CLAIM-3" },
            "T6-S1-SEV050" or "T6-S2-SEV075" => new[] { "CLAIM-3" },
            "T6-ABL1" => new[] { "CLAIM-2" },
            _ => Array.Empty<string>()
        };
        var role = row.RowId switch
        {
            "T6-S0-PRIMARY" => "PRIMARY_CONFIRMATORY_THREE_HOLM_CONTRASTS",
            "T6-S1-SEV050" or "T6-S2-SEV075" => "SECONDARY_PREDECLARED_SEVERITY_ROBUSTNESS",
            _ => "SECONDARY_PREDECLARED_PAIRED_ABLATION"
        };
        var binding = new T6EffectiveRowBindingV1(
            "t6-effective-row-binding-v1",
            row.RowId,
            new(row.ScenarioId, row.PosteriorSupport, row.PosteriorWeights, row.ProbabilityHigh),
            row.EpisodeCount,
            row.Policies,
            ablation,
            numerical,
            rng,
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["O1"] = "expected dimensionless disutility",
                ["O2"] = "failure/first-passage rate",
                ["O3"] = "chance-constraint satisfaction/breach",
                ["O4"] = "action frequency and information-action use",
                ["O5"] = "calibration/reliability diagnostics"
            },
            role,
            claims,
            new(T6FrozenConstants.MaximumWallClockSeconds, T6FrozenConstants.MaximumStorageBytes, 536_870_912L, "ROW;EPISODE;NUMERICAL_BATCH", "FAIL_CLOSED_NO_COMPLETION"),
            "rows/" + row.RowId,
            row.EvidenceClass,
            parents,
            !fixtureOnly,
            false);
        var policies = row.RowId.StartsWith("T6-ABL", StringComparison.Ordinal)
            ? new[] { "P-R4-TWO-STAGE", ablation.AblationId! }
            : new[] { "P-R4-TWO-STAGE", "B7-CONSTRAINED-ONE-STAGE", "B4-POSTERIOR-QUANTILE", "B1-PERIODIC", "B6-UNCONSTRAINED-BAYES" };
        var baselines = row.RowId.StartsWith("T6-ABL", StringComparison.Ordinal)
            ? Array.Empty<string>()
            : new[] { "B7-CONSTRAINED-ONE-STAGE", "B4-POSTERIOR-QUANTILE", "B1-PERIODIC", "B6-UNCONSTRAINED-BAYES" };
        var contrasts = row.RowId == "T6-S0-PRIMARY" ? new[] { "C-P-B7", "C-P-B4", "C-P-B1" } : Array.Empty<string>();
        return new(
            "effective-experiment-v2",
            "effective." + row.RowId.ToLowerInvariant() + ".v1",
            new(row.RowId),
            new(row.ScenarioId),
            stage,
            profile,
            T6FrozenConstants.NumericalProtocol,
            designHashes["sample_size_and_scenarios.json"],
            [row.ScenarioId, row.RowId],
            fixtureOnly ? FactorLevelStatus.TestFixtureOnly : FactorLevelStatus.OwnerDefined,
            policies,
            baselines,
            ablation.AblationId is null ? Array.Empty<string>() : new[] { ablation.AblationId },
            contrasts,
            splitId,
            splitHash,
            row.EpisodeCount,
            fixtureOnly ? EpisodeCountStatus.FixtureFixed : EpisodeCountStatus.T6ProspectivelyFrozen,
            DataPartition.LockedTest,
            "NO_POST_OUTCOME_CALIBRATION_T6_FROZEN",
            "ER07A_REGISTERED_CENSORING_PROTOCOL",
            "STRATIFIED_PAIRED_TRAJECTORY_CLUSTER_PERCENTILE_B9999",
            "HOLM_THREE_PRIMARY_CONTRASTS_ALPHA_0P05",
            "sha256-xoshiro256starstar-crn-v1",
            fixtureOnly ? "TEST_FIXTURE_ONLY_T6_E2E_NOT_SCIENTIFIC_NOT_MANUSCRIPT" : "MODEL_BASED_T6_SCIENTIFIC_EVIDENCE",
            null,
            "rows/" + row.RowId,
            true,
            new(fixtureOnly ? null : T6FrozenConstants.DeltaStar, status, owner, "ER07A_DELTA_STAR_FREEZE"),
            new(fixtureOnly ? null : T6FrozenConstants.HStar, status, owner, "ER07A_H_STAR_FREEZE"),
            binding);
    }

    private static T6AblationBindingV1 CreateAblation(string rowId) => rowId switch
    {
        "T6-ABL1" => new("ABL-1", "Disable endogenous information policies; paired against unchanged P-R4 control", true, true, false, false, false, null, false),
        "T6-ABL2" => new("ABL-2", "Replace full belief by its mean-preserving degenerate belief; paired against unchanged P-R4 control", true, false, true, false, false, null, false),
        "T6-ABL5" => new("ABL-5", "Disable reliability constraint in policy selection only; paired against unchanged P-R4 control", true, false, false, true, false, null, false),
        "T6-ABL6" => new("ABL-6", "Replace reset distribution by mean-matched point reset 0.0875; paired against unchanged P-R4 control", true, false, false, false, true, 0.0875d, false),
        "T6-ABL8" => new("ABL-8", "Remove branchwise epsilon-remaining rule only; retain epsilon and registered epsilon_remaining in lineage", true, false, false, false, false, null, true),
        _ => new(null, "NO_ABLATION", false, false, false, false, false, null, false)
    };

    private static List<T6SchemaBindingEntry> CreateBindingEntries(Er07ADesignAuthorityV1 design, IReadOnlyList<Er07AScenarioRowV1> rows)
    {
        var entries = new List<T6SchemaBindingEntry>();
        foreach (var pair in design.Documents.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(pair.Value);
            foreach (var leaf in T6JsonLeafEnumerator.Enumerate(document.RootElement))
            {
                var runtimeArtifact = RuntimeArtifact(pair.Key, leaf.Path, rows);
                var preservation = pair.Key is "claims.json" or "source_governance.json"
                    ? "PRESERVED_NON_RUNTIME_METADATA_AND_LINEAGE_BOUND"
                    : pair.Key == "config_set_manifest.json"
                        ? "HASH_LINEAGE_BOUND_AND_EXACT_COPY"
                        : "EXECUTION_BOUND_AND_EXACT_COPY";
                entries.Add(new(
                    "design-authority/" + pair.Key,
                    leaf.Path,
                    runtimeArtifact,
                    RuntimeField(pair.Key, leaf.Path),
                    "ER-07A " + leaf.Kind + " field preserved without default substitution; execution semantics are defined by the source field and bridge-v1 mapping.",
                    "BYTE_EXACT_SOURCE_COPY;SOURCE_MEMBER_SHA256;TYPED_FROZEN_VALUE_GATE;RUNTIME_HASH_CHAIN",
                    preservation,
                    design.MemberHashes.GetValueOrDefault(pair.Key, ConfigurationSetLoaderV2.HashFile(Path.Combine(design.Root, pair.Key)))));
            }
        }
        T6BridgeCoverageGateV1.Validate(design.LeafFieldCount, entries.Count);
        return entries;
    }

    private static string RuntimeArtifact(string file, string field, IReadOnlyList<Er07AScenarioRowV1> rows)
    {
        if (file == "sample_size_and_scenarios.json" && field.Contains("scenario_rows[", StringComparison.Ordinal))
        {
            var start = field.IndexOf("scenario_rows[", StringComparison.Ordinal) + "scenario_rows[".Length;
            var end = field.IndexOf(']', start);
            if (end > start && int.TryParse(field[start..end], NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < rows.Count)
                return "rows/" + rows[index].RowId + ".json";
        }
        return file switch
        {
            "numerical_protocol_v2.json" or "rng_manifest.json" or "resource_ceiling.json" or "policies_actions_outcomes.json" or "ablation_and_robustness.json" => "rows/<ALL-EIGHT>.json",
            "authorization_allowlist.json" or "output_policy.json" => "experiment_set_manifest.json",
            "authority_binding.json" or "source_governance.json" or "claims.json" or "statistical_protocol.json" => "bridge/t6-bridge-manifest.json",
            _ => "design-authority/" + file
        };
    }

    private static string RuntimeField(string file, string field) => file switch
    {
        "numerical_protocol_v2.json" => "$.T6Binding.NumericalProtocol" + field[1..],
        "rng_manifest.json" => "$.T6Binding.Rng" + field[1..],
        "resource_ceiling.json" => "$.T6Binding.ResourcePolicy" + field[1..],
        "sample_size_and_scenarios.json" => "$.T6Binding" + field[1..],
        "claims.json" => "$.PreservedDesignArtifacts.claims.json;$.T6Binding.ClaimRoles",
        "statistical_protocol.json" => "$.PreservedDesignArtifacts.statistical_protocol.json;$.T6Binding.StatisticalRole",
        _ => field
    };

    private static string FormatBindingCsv(IReadOnlyList<T6SchemaBindingEntry> entries)
    {
        var lines = new List<string>
        {
            CsvCodec.Format(["source_artifact_path", "source_field", "runtime_artifact_path", "runtime_field", "semantic_definition", "validation_rule", "preservation_status", "hash_parent"])
        };
        lines.AddRange(entries.Select(x => CsvCodec.Format([x.SourceArtifactPath, x.SourceField, x.RuntimeArtifactPath, x.RuntimeField, x.SemanticDefinition, x.ValidationRule, x.PreservationStatus, x.HashParent])));
        return string.Join("\n", lines) + "\n";
    }

    private static void WriteRegistered<T>(string root, List<ConfigFileReferenceV2> files, string relativePath, string role, T value)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CanonicalJson.Serialize(value));
        files.Add(new(relativePath.Replace('\\', '/'), ConfigurationSetLoaderV2.HashFile(path), role));
    }
}
