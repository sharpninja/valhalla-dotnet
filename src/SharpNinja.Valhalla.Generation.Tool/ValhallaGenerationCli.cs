using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpNinja.Valhalla.Generation.Admin;
using SharpNinja.Valhalla.Generation.BikeShare;
using SharpNinja.Valhalla.Generation.Elevation;
using SharpNinja.Valhalla.Generation.Extracts;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Generation.TimeZones;
using SharpNinja.Valhalla.Generation.Transit;
using SharpNinja.Valhalla.Generation.Validation;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Tool;

public static partial class ValhallaGenerationCli
{
    private const int ConfigurationSchemaVersion = 1;
    private const long DefaultMemoryBudgetBytes = 2L * 1024 * 1024 * 1024;
    private const long DefaultScratchBudgetBytes = 20L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly IReadOnlyDictionary<string, CommandSpecification> Specifications =
        CreateSpecifications();

    public static IReadOnlyList<string> Commands { get; } =
    [
        "build-admins",
        "build-timezones",
        "build-elevation-index",
        "build-transit",
        "build-bss",
        "build-tiles",
        "build-extract",
        "validate",
        "benchmark",
    ];

    public static async ValueTask<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var stopwatch = Stopwatch.StartNew();
        string? command = args.Length > 0 ? SafeCommand(args[0]) : null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValhallaGenerationInvocation invocation = ResolveInvocation(args);
            command = invocation.Command;

            await WriteJsonLineAsync(
                    output,
                    new
                    {
                        type = "progress",
                        schemaVersion = 1,
                        command,
                        state = invocation.DryRun ? "validated" : "started",
                    })
                .ConfigureAwait(false);

            IReadOnlyDictionary<string, object?> data = invocation.DryRun
                ? new Dictionary<string, object?>
                {
                    ["dryRun"] = true,
                    ["optionNames"] = invocation.Options.Keys
                        .Order(StringComparer.Ordinal)
                        .ToArray(),
                }
                : await ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            await WriteReceiptAsync(
                    output,
                    command,
                    success: true,
                    ValhallaGenerationCliExitCodes.Success,
                    stopwatch.Elapsed,
                    data,
                    failureCode: null,
                    failureMessage: null)
                .ConfigureAwait(false);
            return ValhallaGenerationCliExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await WriteFailureReceiptAsync(
                    output,
                    command,
                    ValhallaGenerationCliExitCodes.Cancellation,
                    "canceled",
                    "The operation was canceled.",
                    stopwatch.Elapsed)
                .ConfigureAwait(false);
            return ValhallaGenerationCliExitCodes.Cancellation;
        }
        catch (ValhallaGenerationCliConfigurationException exception)
        {
            stopwatch.Stop();
            await WriteFailureReceiptAsync(
                    output,
                    command,
                    ValhallaGenerationCliExitCodes.ConfigurationFailure,
                    "configuration",
                    Redact(exception.Message),
                    stopwatch.Elapsed)
                .ConfigureAwait(false);
            return ValhallaGenerationCliExitCodes.ConfigurationFailure;
        }
        catch (ValhallaGenerationCliValidationException exception)
        {
            stopwatch.Stop();
            await WriteFailureReceiptAsync(
                    output,
                    command,
                    ValhallaGenerationCliExitCodes.ValidationFailure,
                    "validation",
                    Redact(exception.Message),
                    stopwatch.Elapsed)
                .ConfigureAwait(false);
            return ValhallaGenerationCliExitCodes.ValidationFailure;
        }
        catch (Exception exception) when (IsResourceExhaustion(exception))
        {
            stopwatch.Stop();
            await WriteFailureReceiptAsync(
                    output,
                    command,
                    ValhallaGenerationCliExitCodes.ResourceExhaustion,
                    "resource-exhaustion",
                    Redact(exception.Message),
                    stopwatch.Elapsed)
                .ConfigureAwait(false);
            return ValhallaGenerationCliExitCodes.ResourceExhaustion;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await WriteFailureReceiptAsync(
                    output,
                    command,
                    ValhallaGenerationCliExitCodes.UnexpectedFailure,
                    "unexpected",
                    Redact(exception.Message),
                    stopwatch.Elapsed)
                .ConfigureAwait(false);
            return ValhallaGenerationCliExitCodes.UnexpectedFailure;
        }
    }

    public static ValhallaGenerationInvocation ResolveInvocation(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            throw new ValhallaGenerationCliConfigurationException(
                "A generation command is required.");
        }

        string command = args[0].Trim().ToLowerInvariant();
        if (!Specifications.TryGetValue(command, out CommandSpecification? specification))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Unknown generation command '{SafeCommand(command)}'.");
        }

        string? configPath = null;
        bool dryRun = false;
        var commandLineOptions = new Dictionary<string, List<string>>(
            StringComparer.Ordinal);
        var overriddenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 1; index < args.Length; index++)
        {
            string token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                throw new ValhallaGenerationCliConfigurationException(
                    "Every command-line option must use the --name value form.");
            }

            string name = token[2..].ToLowerInvariant();
            if (name == "dry-run")
            {
                dryRun = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ValhallaGenerationCliConfigurationException(
                    $"Option '--{name}' requires a value.");
            }

            string value = args[++index];
            if (name == "config")
            {
                if (configPath is not null)
                {
                    throw new ValhallaGenerationCliConfigurationException(
                        "Only one --config file may be specified.");
                }

                configPath = value;
                continue;
            }

            if (!specification.Options.Contains(name))
            {
                throw new ValhallaGenerationCliConfigurationException(
                    $"Option '--{name}' is not valid for command '{command}'.");
            }

            if (overriddenOptions.Add(name))
            {
                commandLineOptions[name] = [];
            }

            commandLineOptions[name].Add(value);
        }

        Dictionary<string, string[]> options = configPath is null
            ? new Dictionary<string, string[]>(StringComparer.Ordinal)
            : LoadConfiguration(configPath, command, specification);

        foreach ((string name, List<string> values) in commandLineOptions)
        {
            options[name] = values.ToArray();
        }

        ApplyDefaults(options);
        foreach (string required in specification.RequiredOptions)
        {
            if (!options.TryGetValue(required, out string[]? values) ||
                values.Length == 0 ||
                values.Any(string.IsNullOrWhiteSpace))
            {
                throw new ValhallaGenerationCliConfigurationException(
                    $"Command '{command}' requires option '--{required}'.");
            }
        }

        return new ValhallaGenerationInvocation(command, options, dryRun);
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return invocation.Command switch
        {
            "build-admins" => await BuildAdminsAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "build-timezones" => await BuildTimeZonesAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "build-elevation-index" => await BuildElevationAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "build-transit" => await BuildTransitAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "build-bss" => await BuildBikeShareAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "build-tiles" => await BuildTilesAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "build-extract" => await BuildExtractAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "validate" => await ValidateAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            "benchmark" => await BenchmarkAsync(invocation, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ValhallaGenerationCliConfigurationException(
                $"Unknown generation command '{SafeCommand(invocation.Command)}'."),
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildAdminsAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var builder = new ManagedAdminDatabaseBuilder();
        AdminDatabaseBuildResult result = await builder.BuildAsync(
                new AdminDatabaseBuildRequest(
                    GetPaths(invocation, "pbf"),
                    GetPath(invocation, "working-directory"),
                    GetPath(invocation, "output"),
                    GetEnum<IntermediateStorageMode>(invocation, "storage-mode"),
                    GetPositiveInt64(invocation, "memory-budget-bytes"),
                    GetPositiveInt64(invocation, "scratch-budget-bytes")),
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["outputPath"] = result.DatabasePath,
            ["adminCount"] = result.AdminCount,
            ["accessOverrideCount"] = result.AccessOverrideCount,
            ["sha256"] = result.Sha256,
            ["bytesWritten"] = result.BytesWritten,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildTimeZonesAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var builder = new ManagedTimeZoneDatabaseBuilder();
        TimeZoneDatabaseBuildResult result = await builder.BuildAsync(
                new TimeZoneDatabaseBuildRequest(
                    GetPath(invocation, "source-shapefile"),
                    GetRequired(invocation, "source-version"),
                    GetPath(invocation, "working-directory"),
                    GetPath(invocation, "output"),
                    GetPositiveInt64(invocation, "scratch-budget-bytes")),
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["outputPath"] = result.DatabasePath,
            ["sourceVersion"] = result.SourceVersion,
            ["timeZoneCount"] = result.TimeZoneCount,
            ["sha256"] = result.Sha256,
            ["bytesWritten"] = result.BytesWritten,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildElevationAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var builder = new ManagedElevationDatasetBuilder();
        ElevationDatasetBuildResult result = await builder.BuildAsync(
                new ElevationDatasetBuildRequest(
                    GetPath(invocation, "graph-directory"),
                    GetPath(invocation, "elevation-directory"),
                    GetPositiveInt32(invocation, "max-degree-of-parallelism"),
                    GetPositiveInt64(invocation, "scratch-budget-bytes"),
                    GetBoolean(invocation, "deterministic-output")),
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["outputDirectory"] = result.GraphTileDirectory,
            ["tileCount"] = result.TileCount,
            ["nodeCount"] = result.NodeCount,
            ["peakConcurrency"] = result.PeakConcurrency,
            ["treeSha256"] = result.OutputTreeSha256,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildTransitAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var builder = new ManagedTransitTileBuilder();
        TransitTileBuildResult result = await builder.BuildAsync(
                new TransitTileBuildRequest(
                    GetPaths(invocation, "feed"),
                    GetPath(invocation, "working-directory"),
                    GetPath(invocation, "output"),
                    GetOptionalPath(invocation, "timezone-database"),
                    new TransitTileBuildOptions(
                        GetPositiveInt32(invocation, "max-degree-of-parallelism"),
                        GetPositiveInt64(invocation, "memory-budget-bytes"),
                        GetPositiveInt64(invocation, "scratch-budget-bytes"),
                        GetDate(invocation, "build-date"),
                        GetUInt32(invocation, "dataset-id"),
                        GetUInt64(invocation, "build-id"),
                        GetBoolean(invocation, "deterministic-output"))),
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["outputDirectory"] = result.OutputDirectory,
            ["feedCount"] = result.FeedCount,
            ["tileCount"] = result.TileCount,
            ["peakConcurrency"] = result.PeakConcurrency,
            ["stopCount"] = result.StopCount,
            ["routeCount"] = result.RouteCount,
            ["departureCount"] = result.DepartureCount,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildBikeShareAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var builder = new ManagedBikeShareTileBuilder();
        BikeShareTileBuildResult result = await builder.BuildAsync(
                new BikeShareTileBuildRequest(
                    GetPath(invocation, "graph-directory"),
                    GetPaths(invocation, "pbf"),
                    GetPath(invocation, "working-directory"),
                    GetPath(invocation, "output"),
                    new BikeShareTileBuildOptions(
                        GetPositiveInt32(invocation, "max-degree-of-parallelism"),
                        GetPositiveInt64(invocation, "memory-budget-bytes"),
                        GetPositiveInt64(invocation, "scratch-budget-bytes"),
                        GetBoolean(invocation, "deterministic-output"))),
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["outputDirectory"] = result.OutputDirectory,
            ["stationCount"] = result.StationCount,
            ["addedNodeCount"] = result.AddedNodeCount,
            ["addedDirectedEdgeCount"] = result.AddedDirectedEdgeCount,
            ["peakConcurrency"] = result.MaximumConcurrency,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildTilesAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        string workingDirectory = GetPath(invocation, "working-directory");
        string outputDirectory = GetPath(invocation, "output");
        EnsureNewOutput(outputDirectory);
        string? outputParent = Path.GetDirectoryName(outputDirectory);
        if (string.IsNullOrWhiteSpace(outputParent))
        {
            throw new ValhallaGenerationCliConfigurationException(
                "The requested output must have a parent directory.");
        }

        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(outputParent);

        string stagingDirectory = CreateStagingDirectoryPath(
            outputDirectory,
            Guid.NewGuid());
        string runWorkingDirectory = Path.Combine(
            workingDirectory,
            $".run-{Guid.NewGuid():N}");

        try
        {
            var roadBuilder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult build = await roadBuilder.BuildAsync(
                    new ManagedRoadGraphBuildRequest(
                        GetPaths(invocation, "pbf"),
                        runWorkingDirectory,
                        stagingDirectory,
                        GetEnum<IntermediateStorageMode>(
                            invocation,
                            "storage-mode"),
                        GetPositiveInt64(invocation, "memory-budget-bytes"),
                        GetPositiveInt64(invocation, "scratch-budget-bytes"),
                        new TileBuilderConfig
                        {
                            Hierarchy = true,
                            Shortcuts = true,
                            MaxDegreeOfParallelism = GetPositiveInt32(
                                invocation,
                                "max-degree-of-parallelism"),
                        })
                    {
                        Pipeline = GetEnum<ManagedRoadGraphPipeline>(
                            invocation,
                            "road-pipeline"),
                        TimeZoneDatabasePath = GetOptionalPath(
                            invocation,
                            "timezone-database"),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            TileBuilderResult result = build.TileBuilderResult;

            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success || result.TileCount == 0)
            {
                throw new ValhallaGenerationCliValidationException(
                    "Managed road-tile generation did not produce a valid graph.");
            }

            ValhallaGenerationValidationResult validation =
                await ValidateGraphDirectoryAsync(
                        invocation,
                        stagingDirectory,
                        cancellationToken,
                        result.ValidatorStats)
                    .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new ValhallaGenerationCliValidationException(
                    DescribeValidationFailure(
                        "Managed road-tile generation failed graph validation.",
                        validation));
            }

            Directory.Move(stagingDirectory, outputDirectory);
            return new Dictionary<string, object?>
            {
                ["outputDirectory"] = outputDirectory,
                ["tileCount"] = result.TileCount,
                ["wayCount"] = result.WayCount,
                ["wayNodeCount"] = result.WayNodeCount,
                ["pbfDataBlockCount"] = build.PbfMetrics.DataBlockCount,
                ["pbfDecompressionCount"] = build.PbfMetrics.DecompressionCount,
                ["peakIntermediateMemoryBytes"] =
                    build.PeakIntermediateMemoryBytes,
                ["scratchDiskHighWaterMarkBytes"] =
                    build.ScratchDiskHighWaterMarkBytes,
                ["pbfIngestionDurationMilliseconds"] =
                    build.PbfIngestionDuration.TotalMilliseconds,
                ["semanticParsingDurationMilliseconds"] =
                    build.SemanticParsingDuration.TotalMilliseconds,
                ["tileConstructionDurationMilliseconds"] =
                    build.TileConstructionDuration.TotalMilliseconds,
                ["semanticStageDurationsMilliseconds"] =
                    build.SemanticStageDurations.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value.TotalMilliseconds,
                        StringComparer.Ordinal),
                ["tileStageDurationsMilliseconds"] =
                    result.StageDurations.ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value.TotalMilliseconds,
                        StringComparer.Ordinal),
                ["enhancerOperationCounts"] = new Dictionary<string, ulong>(
                    StringComparer.Ordinal)
                {
                    ["secondPassEdges"] =
                        result.EnhancerStats?.SecondPassEdgeCount ?? 0,
                    ["nameConsistencyChecks"] =
                        result.EnhancerStats?.NameConsistencyCheckCount ?? 0,
                    ["internalIntersectionChecks"] =
                        result.EnhancerStats?.InternalIntersectionCheckCount ?? 0,
                    ["stopYieldChecks"] =
                        result.EnhancerStats?.StopYieldCheckCount ?? 0,
                    ["turnLaneChecks"] =
                        result.EnhancerStats?.TurnLaneCheckCount ?? 0,
                    ["notThruChecks"] =
                        result.EnhancerStats?.NotThruCheckCount ?? 0,
                    ["notThruNodeExpansions"] =
                        result.EnhancerStats?.NotThruNodeExpansionCount ?? 0,
                    ["notThruScratchAllocations"] =
                        result.EnhancerStats?.NotThruScratchAllocationCount ?? 0,
                },
                ["validationReceiptSha256"] = validation.ReceiptSha256,
            };
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            if (Directory.Exists(runWorkingDirectory))
            {
                Directory.Delete(runWorkingDirectory, recursive: true);
            }
        }
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BuildExtractAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var builder = new ManagedTileExtractBuilder();
        TileExtractBuildResult result = await builder.BuildAsync(
                new TileExtractBuildRequest(
                    GetPath(invocation, "graph-directory"),
                    GetPath(invocation, "output"),
                    GetRequired(invocation, "region-id"),
                    GetUInt32(invocation, "dataset-id"),
                    GetUInt64(invocation, "build-id"),
                    GetBoolean(invocation, "deterministic-output")),
                cancellationToken)
            .ConfigureAwait(false);

        return new Dictionary<string, object?>
        {
            ["outputPath"] = result.OutputPath,
            ["regionId"] = result.RegionId,
            ["tileCount"] = result.TileCount,
            ["byteLength"] = result.ByteLength,
            ["archiveSha256"] = result.ArchiveSha256,
            ["manifestSha256"] = result.ManifestSha256,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> ValidateAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        string graphDirectory = GetPath(invocation, "graph-directory");
        ValhallaGenerationValidationResult result =
            await ValidateGraphDirectoryAsync(
                    invocation,
                    graphDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw new ValhallaGenerationCliValidationException(
                DescribeValidationFailure(
                    "The graph failed managed validation.",
                    result));
        }

        return new Dictionary<string, object?>
        {
            ["graphDirectory"] = graphDirectory,
            ["validationReceiptSha256"] = result.ReceiptSha256,
            ["validationReceiptLength"] = result.ReceiptLength,
        };
    }

    private static async ValueTask<IReadOnlyDictionary<string, object?>> BenchmarkAsync(
        ValhallaGenerationInvocation invocation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, object?> build =
            await BuildTilesAsync(invocation, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        var result = new Dictionary<string, object?>(build, StringComparer.Ordinal)
        {
            ["wallTimeMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds,
            ["processorCount"] = Environment.ProcessorCount,
            ["workingSetBytes"] = Environment.WorkingSet,
        };
        return result;
    }

    private static async ValueTask<ValhallaGenerationValidationResult>
        ValidateGraphDirectoryAsync(
            ValhallaGenerationInvocation invocation,
            string graphDirectory,
            CancellationToken cancellationToken,
            GraphValidator.ValidatorStats? prevalidatedStats = null)
    {
        long memoryBudget = GetPositiveInt64(invocation, "memory-budget-bytes");
        long scratchBudget = GetPositiveInt64(invocation, "scratch-budget-bytes");
        int parallelism = GetPositiveInt32(
            invocation,
            "max-degree-of-parallelism");

        using var resources = new ValhallaGenerationResourceBudget(
            memoryBudget,
            scratchBudget,
            parallelism);
        var request = new ValhallaGenerationBuildRequest(
            [],
            ValhallaGenerationInputSet.Empty,
            GetPath(invocation, "working-directory"),
            graphDirectory,
            new ValhallaGenerationBuildOptions(
                ValhallaGenerationProfile.RoadOnly,
                IntermediateStorageMode.Auto,
                ResumePolicy.Disabled,
                parallelism,
                memoryBudget,
                scratchBudget,
                GetUInt32(invocation, "dataset-id"),
                GetUInt64(invocation, "build-id"),
                GetBoolean(invocation, "deterministic-output")));
        var context = new ValhallaGenerationStageContext(
            request,
            "cli-validation",
            graphDirectory,
            resources);
        var validator = new ManagedValhallaGenerationValidator();
        return prevalidatedStats is null
            ? await validator.ValidateAsync(context, cancellationToken)
                .ConfigureAwait(false)
            : await validator.ValidatePrevalidatedAsync(
                    context,
                    prevalidatedStats,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private static Dictionary<string, string[]> LoadConfiguration(
        string configPath,
        string command,
        CommandSpecification specification)
    {
        string absolutePath = ToLocalPath(configPath, "config");
        if (!File.Exists(absolutePath))
        {
            throw new ValhallaGenerationCliConfigurationException(
                "The configuration file does not exist.");
        }

        try
        {
            using FileStream stream = File.OpenRead(absolutePath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ValhallaGenerationCliConfigurationException(
                    "The configuration root must be a JSON object.");
            }

            var allowedRootProperties = new HashSet<string>(
                ["schemaVersion", "command", "options"],
                StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!allowedRootProperties.Contains(property.Name))
                {
                    throw new ValhallaGenerationCliConfigurationException(
                        $"Unknown configuration property '{property.Name}'.");
                }
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out int schemaVersion) ||
                schemaVersion != ConfigurationSchemaVersion)
            {
                throw new ValhallaGenerationCliConfigurationException(
                    $"Configuration schemaVersion must be {ConfigurationSchemaVersion}.");
            }

            if (!root.TryGetProperty("command", out JsonElement configuredCommand) ||
                configuredCommand.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    configuredCommand.GetString(),
                    command,
                    StringComparison.Ordinal))
            {
                throw new ValhallaGenerationCliConfigurationException(
                    "The configuration command must exactly match the invoked command.");
            }

            var options = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (!root.TryGetProperty("options", out JsonElement configuredOptions))
            {
                return options;
            }

            if (configuredOptions.ValueKind != JsonValueKind.Object)
            {
                throw new ValhallaGenerationCliConfigurationException(
                    "Configuration options must be a JSON object.");
            }

            foreach (JsonProperty property in configuredOptions.EnumerateObject())
            {
                string name = property.Name.ToLowerInvariant();
                if (!specification.Options.Contains(name))
                {
                    throw new ValhallaGenerationCliConfigurationException(
                        $"Unknown option '{property.Name}' for command '{command}'.");
                }

                options[name] = ReadOptionValues(property);
            }

            return options;
        }
        catch (JsonException exception)
        {
            throw new ValhallaGenerationCliConfigurationException(
                "The configuration file is not valid JSON.",
                exception);
        }
    }

    private static string[] ReadOptionValues(JsonProperty property)
    {
        if (property.Value.ValueKind == JsonValueKind.Array)
        {
            string[] values = property.Value
                .EnumerateArray()
                .Select(element => ReadScalar(property.Name, element))
                .ToArray();
            if (values.Length == 0)
            {
                throw new ValhallaGenerationCliConfigurationException(
                    $"Option '{property.Name}' cannot be an empty array.");
            }

            return values;
        }

        return [ReadScalar(property.Name, property.Value)];
    }

    private static string ReadScalar(string name, JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ??
                throw new ValhallaGenerationCliConfigurationException(
                    $"Option '{name}' cannot be null."),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => throw new ValhallaGenerationCliConfigurationException(
                $"Option '{name}' must be a scalar or an array of scalars."),
        };

    private static void ApplyDefaults(Dictionary<string, string[]> options)
    {
        AddDefault(
            options,
            "working-directory",
            Path.Combine(Environment.CurrentDirectory, ".valhalla-work"));
        AddDefault(
            options,
            "max-degree-of-parallelism",
            Math.Max(1, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture));
        AddDefault(
            options,
            "memory-budget-bytes",
            DefaultMemoryBudgetBytes.ToString(CultureInfo.InvariantCulture));
        AddDefault(
            options,
            "scratch-budget-bytes",
            DefaultScratchBudgetBytes.ToString(CultureInfo.InvariantCulture));
        AddDefault(options, "storage-mode", IntermediateStorageMode.Auto.ToString());
        AddDefault(
            options,
            "road-pipeline",
            ManagedRoadGraphPipeline.Legacy.ToString());
        AddDefault(options, "dataset-id", "0");
        AddDefault(options, "build-id", "0");
        AddDefault(options, "deterministic-output", bool.TrueString);
    }

    private static void AddDefault(
        IDictionary<string, string[]> options,
        string name,
        string value)
    {
        if (!options.ContainsKey(name))
        {
            options[name] = [value];
        }
    }

    private static IReadOnlyDictionary<string, CommandSpecification>
        CreateSpecifications()
    {
        string[] common =
        [
            "working-directory",
            "max-degree-of-parallelism",
            "memory-budget-bytes",
            "scratch-budget-bytes",
            "storage-mode",
            "dataset-id",
            "build-id",
            "deterministic-output",
            "build-date",
        ];

        CommandSpecification Spec(
            string[] specific,
            string[] required) =>
            new(
                new HashSet<string>(
                    common.Concat(specific),
                    StringComparer.Ordinal),
                required);

        return new Dictionary<string, CommandSpecification>(StringComparer.Ordinal)
        {
            ["build-admins"] = Spec(["pbf", "output"], ["pbf", "output"]),
            ["build-timezones"] = Spec(
                ["source-shapefile", "source-version", "output"],
                ["source-shapefile", "source-version", "output"]),
            ["build-elevation-index"] = Spec(
                ["graph-directory", "elevation-directory"],
                ["graph-directory", "elevation-directory"]),
            ["build-transit"] = Spec(
                ["feed", "output", "timezone-database"],
                ["feed", "output", "build-date"]),
            ["build-bss"] = Spec(
                ["graph-directory", "pbf", "output"],
                ["graph-directory", "pbf", "output"]),
            ["build-tiles"] = Spec(
                ["pbf", "output", "timezone-database", "road-pipeline"],
                ["pbf", "output"]),
            ["build-extract"] = Spec(
                ["graph-directory", "output", "region-id"],
                ["graph-directory", "output", "region-id"]),
            ["validate"] = Spec(["graph-directory"], ["graph-directory"]),
            ["benchmark"] = Spec(["pbf", "output"], ["pbf", "output"]),
        };
    }

    private static string GetRequired(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        if (!invocation.Options.TryGetValue(name, out string[]? values) ||
            values.Length != 1 ||
            string.IsNullOrWhiteSpace(values[0]))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' requires exactly one value.");
        }

        return values[0];
    }

    private static string GetPath(
        ValhallaGenerationInvocation invocation,
        string name) =>
        ToLocalPath(GetRequired(invocation, name), name);

    private static string? GetOptionalPath(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        if (!invocation.Options.TryGetValue(name, out string[]? values))
        {
            return null;
        }

        if (values.Length != 1)
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' requires exactly one value.");
        }

        return ToLocalPath(values[0], name);
    }

    private static string[] GetPaths(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        if (!invocation.Options.TryGetValue(name, out string[]? values) ||
            values.Length == 0)
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' requires at least one path.");
        }

        return values.Select(value => ToLocalPath(value, name)).ToArray();
    }

    private static string ToLocalPath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Contains('\0'))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must be a local path.");
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' is not a valid local path.",
                exception);
        }
    }

    private static int GetPositiveInt32(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        string value = GetRequired(invocation, name);
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int result) ||
            result <= 0)
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must be a positive 32-bit integer.");
        }

        return result;
    }

    private static long GetPositiveInt64(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        string value = GetRequired(invocation, name);
        if (!long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long result) ||
            result <= 0)
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must be a positive 64-bit integer.");
        }

        return result;
    }

    private static uint GetUInt32(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        string value = GetRequired(invocation, name);
        if (!uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint result))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must be an unsigned 32-bit integer.");
        }

        return result;
    }

    private static ulong GetUInt64(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        string value = GetRequired(invocation, name);
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong result))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must be an unsigned 64-bit integer.");
        }

        return result;
    }

    private static bool GetBoolean(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        string value = GetRequired(invocation, name);
        if (!bool.TryParse(value, out bool result))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must be true or false.");
        }

        return result;
    }

    private static DateOnly GetDate(
        ValhallaGenerationInvocation invocation,
        string name)
    {
        string value = GetRequired(invocation, name);
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly result))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' must use yyyy-MM-dd.");
        }

        return result;
    }

    private static TEnum GetEnum<TEnum>(
        ValhallaGenerationInvocation invocation,
        string name)
        where TEnum : struct, Enum
    {
        string value = GetRequired(invocation, name);
        if (!Enum.TryParse(value, ignoreCase: true, out TEnum result) ||
            !Enum.IsDefined(result))
        {
            throw new ValhallaGenerationCliConfigurationException(
                $"Option '--{name}' has an unsupported value.");
        }

        return result;
    }

    private static string CreateStagingDirectoryPath(
        string outputDirectory,
        Guid buildId)
    {
        string? parent = Path.GetDirectoryName(outputDirectory);
        string name = Path.GetFileName(outputDirectory);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
        {
            throw new ValhallaGenerationCliConfigurationException(
                "The requested output must identify a child directory.");
        }

        return Path.Combine(parent, $".{name}.incoming-{buildId:N}");
    }

    private static void EnsureNewOutput(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new ValhallaGenerationCliConfigurationException(
                "The requested output already exists; generation output is immutable.");
        }
    }

    private static bool IsResourceExhaustion(Exception exception) =>
        exception is ValhallaGenerationResourceLimitException ||
        exception is OutOfMemoryException ||
        exception is AdminDatabaseBuildException
        {
            FailureCode: AdminDatabaseFailureCode.ScratchDiskBudgetExceeded,
        } ||
        exception is TimeZoneDatabaseBuildException
        {
            FailureCode: TimeZoneDatabaseFailureCode.ScratchDiskBudgetExceeded,
        } ||
        exception is ElevationDatasetBuildException
        {
            FailureCode: ElevationDatasetFailureCode.ScratchDiskBudgetExceeded,
        } ||
        exception is TransitTileBuildException
        {
            Code: TransitTileBuildFailureCode.ResourceExhausted,
        } ||
        exception is BikeShareTileBuildException
        {
            Code: BikeShareTileBuildFailureCode.ResourceExhausted,
        };

    private static async ValueTask WriteFailureReceiptAsync(
        TextWriter output,
        string? command,
        int exitCode,
        string failureCode,
        string failureMessage,
        TimeSpan duration) =>
        await WriteReceiptAsync(
                output,
                command,
                success: false,
                exitCode,
                duration,
                data: null,
                failureCode,
                failureMessage)
            .ConfigureAwait(false);

    private static async ValueTask WriteReceiptAsync(
        TextWriter output,
        string? command,
        bool success,
        int exitCode,
        TimeSpan duration,
        IReadOnlyDictionary<string, object?>? data,
        string? failureCode,
        string? failureMessage) =>
        await WriteJsonLineAsync(
                output,
                new
                {
                    type = "receipt",
                    schemaVersion = 1,
                    command,
                    success,
                    exitCode,
                    durationMilliseconds = duration.TotalMilliseconds,
                    upstreamCompatibility = ValhallaGenerationBuilder.UpstreamCompatibilityVersion,
                    sourceCommit = ThisAssembly.GitCommit,
                    failure = failureCode is null
                        ? null
                        : new
                        {
                            code = failureCode,
                            message = failureMessage,
                        },
                    data,
                })
            .ConfigureAwait(false);

    private static async ValueTask WriteJsonLineAsync(
        TextWriter output,
        object value)
    {
        await output.WriteLineAsync(
                JsonSerializer.Serialize(value, OutputJsonOptions))
            .ConfigureAwait(false);
    }

    private static string DescribeValidationFailure(
        string summary,
        ValhallaGenerationValidationResult result)
    {
        string details = string.Join(
            "; ",
            result.Failures
                .Select(static failure => failure.Message)
                .Where(static message => !string.IsNullOrWhiteSpace(message)));
        return string.IsNullOrWhiteSpace(details)
            ? summary
            : $"{summary} {details}";
    }

    private static string SafeCommand(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : new string(
                value
                    .Where(character =>
                        char.IsAsciiLetterOrDigit(character) ||
                        character is '-' or '_')
                    .Take(64)
                    .ToArray());

    private static string Redact(string message)
    {
        string redacted = CredentialPairPattern().Replace(
            message,
            "$1=[REDACTED]");
        return UriUserInfoPattern().Replace(redacted, "1[REDACTED]@");
    }

    [GeneratedRegex(
        "(?i)(api[_-]?key|token|secret|authorization|password)=([^&\\s]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPairPattern();

    [GeneratedRegex(
        "(?i)(https?://)[^/\\s@]+@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoPattern();

    private sealed record CommandSpecification(
        IReadOnlySet<string> Options,
        IReadOnlyList<string> RequiredOptions);
}

public static class ValhallaGenerationCliExitCodes
{
    public const int Success = 0;
    public const int UnexpectedFailure = 1;
    public const int ConfigurationFailure = 2;
    public const int ValidationFailure = 3;
    public const int Cancellation = 4;
    public const int ResourceExhaustion = 5;
}

public sealed record ValhallaGenerationInvocation(
    string Command,
    IReadOnlyDictionary<string, string[]> Options,
    bool DryRun);

public sealed class ValhallaGenerationCliConfigurationException : Exception
{
    public ValhallaGenerationCliConfigurationException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class ValhallaGenerationCliValidationException : Exception
{
    public ValhallaGenerationCliValidationException(string message)
        : base(message)
    {
    }
}

internal static class ThisAssembly
{
    public static string GitCommit { get; } = GetMetadata("RepositoryCommit");

    private static string GetMetadata(string key) =>
        typeof(ThisAssembly).Assembly
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute =>
                string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value ?? "unknown";
}
