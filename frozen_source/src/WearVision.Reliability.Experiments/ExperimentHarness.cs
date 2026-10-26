using WearVision.Reliability.Domain;
using WearVision.Reliability.Infrastructure;
using System.Text;

namespace WearVision.Reliability.Experiments;

public sealed class ProtocolAwareExperimentHarness
{
    private readonly ExperimentAuthorizationGuard _guard;
    public ProtocolAwareExperimentHarness(ExperimentAuthorizationGuard guard) => _guard = guard;

    public IReadOnlyList<ExperimentMatrixRow> LoadMatrix(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = CsvCodec.ParseLine(lines[0]);
        var required = new[] { "experiment_id", "experiment_family", "stage", "scenario_id", "paired_design", "authorization_status" };
        var indexes = required.ToDictionary(x => x, x => Array.IndexOf(header.ToArray(), x), StringComparer.Ordinal);
        if (indexes.Values.Any(x => x < 0)) throw new InvalidDataException("Experiment matrix header is incomplete.");
        return lines.Skip(1).Select(line =>
        {
            var c = CsvCodec.ParseLine(line);
            var stageToken = c[indexes["stage"]].Split('/').Last();
            var stage = Enum.Parse<RunStage>(stageToken, false);
            return new ExperimentMatrixRow(new(c[indexes["experiment_id"]]), c[indexes["experiment_family"]], stage, c[indexes["authorization_status"]], new(c[indexes["scenario_id"]]), c[indexes["paired_design"]].StartsWith("Paired", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }

    public PreflightResult Authorize(ExperimentConfig config, ExperimentMatrixRow row)
    {
        var issues = _guard.Validate(config, row);
        return new(issues.Count == 0 ? EvaluationStatus.Success : issues[0].Status, issues);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("WearVision commands: config validate; preflight; t5 pilot; er06c confirm; t6 design validate; t6 bridge; t6 config validate; t6 authorize; t6 preflight; t6 execute.");
            return 0;
        }
        try
        {
            var application = new T5CommandApplication();
            if (args.Length == 3 && args[0] == "config" && args[1] == "validate")
                return Print(application.ValidateConfiguration(args[2]), successWhenRuntimeCompatible: true);
            if (args.Length == 2 && args[0] == "preflight")
            {
                var grant = ReadDraftGrant(args[1]);
                return Print(application.Preflight(args[1], grant), successWhenRuntimeCompatible: false);
            }
            if (args.Length == 4 && args[0] == "preflight")
            {
                var authorization = ReadSourceBoundAuthorization(args[2]);
                var identity = T5ExecutionIdentity.Capture(args[3], typeof(Program).Assembly.Location);
                return Print(application.PreflightForExecution(args[1], authorization, identity), successWhenRuntimeCompatible: false);
            }
            if (args.Length == 6 && args[0] == "t5" && args[1] == "pilot")
            {
                var authorization = ReadSourceBoundAuthorization(args[3]);
                var identity = T5ExecutionIdentity.Capture(args[4], typeof(Program).Assembly.Location);
                var schema = FindWorkspaceFile("schemas", "raw-schema-registry.csv");
                var result = application.RunT5PilotAsync(args[2], authorization, identity, args[5], new T5PilotExecutionService(schema), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(result)));
                return result.ExitCode;
            }
            if (args.Length >= 2 && args[0] == "t5" && args[1] == "pilot")
            {
                Console.Error.WriteLine("T5_SOURCE_BOUND_AUTHORIZATION_REQUIRED");
                return 4;
            }
            if (args.Length == 6 && args[0] == "er06c" && args[1] == "confirm")
            {
                var result = new Er06CPlanningDiagnosticRunner().Run(
                    args[2],
                    args[3],
                    args[4],
                    args[5],
                    typeof(Program).Assembly.Location,
                    CancellationToken.None);
                Console.WriteLine(System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(result)));
                return 0;
            }
            if (args.Length >= 2 && args[0] == "er06c")
            {
                Console.Error.WriteLine("ER06C_SOURCE_BOUND_CONFIRMATION_ARGUMENTS_REQUIRED");
                return 4;
            }
            if (args.Length == 4 && args[0] == "t6" && args[1] == "design" && args[2] == "validate")
            {
                var design = new Er07ADesignPackageParserV1().Load(args[3]);
                Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(new
                {
                    SchemaVersion = "er07a-design-validation-v1",
                    Accepted = true,
                    RowCount = design.Rows.Count,
                    EpisodeTotal = design.Rows.Sum(x => x.EpisodeCount),
                    design.LeafFieldCount,
                    ExecutionStarted = false
                })));
                return 0;
            }
            if (args.Length == 10 && args[0] == "t6" && args[1] == "bridge")
            {
                var mode = args[9] switch
                {
                    "production" => false,
                    "fixture" => true,
                    _ => throw new InvalidDataException("T6_BRIDGE_MODE_MUST_BE_PRODUCTION_OR_FIXTURE")
                };
                var rawSchema = FindWorkspaceFile("schemas", "raw-schema-registry.csv");
                var packageHash = new T6RuntimeSchemaBridgeV1().Build(args[2], args[3], args[4], rawSchema, args[5], args[6], args[7], args[8], mode);
                Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(new
                {
                    SchemaVersion = "t6-bridge-result-v1",
                    RuntimeConfigSet = Path.GetFullPath(args[7]),
                    RuntimeConfigSetSha256 = packageHash,
                    FixtureOnly = mode,
                    ExecutionStarted = false
                })));
                return 0;
            }
            if (args.Length == 4 && args[0] == "t6" && args[1] == "config" && args[2] == "validate")
            {
                var report = new T6ScientificExecutionService().ValidateConfiguration(args[3]);
                Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(report)));
                return report.Accepted ? 0 : 3;
            }
            if (args.Length == 6 && args[0] == "t6" && args[1] == "authorize")
            {
                using var package = T6PackageMaterialization.Open(args[2]);
                var identity = T6ExecutionIdentityV1.Capture(args[3], typeof(Program).Assembly.Location);
                var authorization = T6AuthorizationIssuerV1.Issue(package, identity, args[5]);
                var outputPath = Path.GetFullPath(args[4]);
                if (File.Exists(outputPath) || Directory.Exists(outputPath)) throw new IOException("T6_AUTHORIZATION_DESTINATION_EXISTS");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(CanonicalJson.Serialize(authorization));
                    stream.Flush(flushToDisk: true);
                }
                Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(new
                {
                    SchemaVersion = "t6-authorization-issue-result-v1",
                    AuthorizationPath = outputPath,
                    AuthorizationSha256 = ConfigurationSetLoaderV2.HashFile(outputPath),
                    authorization.AuthorizationId,
                    authorization.State,
                    ExecutionStarted = false
                })));
                return 0;
            }
            if (args.Length == 6 && args[0] == "t6" && args[1] == "preflight")
            {
                var report = new T6ScientificExecutionService().Validate(args[2], args[3], args[4], typeof(Program).Assembly.Location, args[5]);
                Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(report)));
                return report.Accepted ? 0 : 4;
            }
            if (args.Length == 6 && args[0] == "t6" && args[1] == "execute")
            {
                var result = new T6ScientificExecutionService().ExecuteAsync(args[2], args[3], args[4], typeof(Program).Assembly.Location, args[5], CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(result)));
                return result.ExitCode;
            }
            if (args.Length >= 1 && args[0] == "t6") return application.RunT6();
            Console.Error.WriteLine("UNKNOWN_COMMAND");
            return 2;
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
    }

    private static T5AuthorizationGrantV2? ReadDraftGrant(string directory)
    {
        var path = Path.Combine(Path.GetFullPath(directory), "t5_authorization_manifest.json");
        return File.Exists(path) ? CanonicalJson.DeserializeStrict<T5AuthorizationGrantV2>(File.ReadAllText(path)) : null;
    }

    private static T5SourceBoundAuthorizationV2 ReadSourceBoundAuthorization(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Source-bound authorization is missing.", fullPath);
        return CanonicalJson.DeserializeStrict<T5SourceBoundAuthorizationV2>(File.ReadAllText(fullPath));
    }

    private static string FindWorkspaceFile(params string[] parts)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
                if (File.Exists(candidate)) return candidate;
                current = current.Parent;
            }
        }
        throw new FileNotFoundException("Required workspace file is missing.", Path.Combine(parts));
    }

    private static int Print(T5PreflightReport report, bool successWhenRuntimeCompatible)
    {
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(report)));
        if (successWhenRuntimeCompatible)
            return report.RuntimeSchemaCompatible && report.ModelBindingCompatible && report.BaselineBindingCompatible && report.MultiRowBindingCompatible && report.PlanningHarnessCompatible && report.Issues.Count == 0 ? 0 : 3;
        return report.AcceptedForExecution ? 0 : 4;
    }
}
