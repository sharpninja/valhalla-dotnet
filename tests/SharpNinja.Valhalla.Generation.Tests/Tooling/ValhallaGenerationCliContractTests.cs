using System.Text.Json;
using SharpNinja.Valhalla.Generation.Tool;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Tooling;

public sealed class ValhallaGenerationCliContractTests : IDisposable
{
    private readonly string scratch =
        Path.Combine(Path.GetTempPath(), $"valhalla-generation-cli-{Guid.NewGuid():N}");

    [Fact]
    public void RequiredCommands_AreAvailable()
    {
        Assert.Equal(
            new[]
            {
                "build-admins",
                "build-timezones",
                "build-elevation-index",
                "build-transit",
                "build-bss",
                "build-tiles",
                "build-extract",
                "validate",
                "benchmark",
            },
            ValhallaGenerationCli.Commands);
    }

    [Fact]
    public void ConfigAndOverrides_ResolveDeterministically()
    {
        Directory.CreateDirectory(scratch);
        string configPath = Path.Combine(scratch, "build-admins.json");
        File.WriteAllText(
            configPath,
            """
            {
              "schemaVersion": 1,
              "command": "build-admins",
              "options": {
                "pbf": ["first.osm.pbf", "second.osm.pbf"],
                "working-directory": "from-config",
                "output": "admins.sqlite",
                "memory-budget-bytes": "1048576"
              }
            }
            """);

        ValhallaGenerationInvocation invocation =
            ValhallaGenerationCli.ResolveInvocation(
            [
                "build-admins",
                "--config",
                configPath,
                "--working-directory",
                "from-command-line",
                "--memory-budget-bytes",
                "2097152",
                "--dry-run",
            ]);

        Assert.Equal("build-admins", invocation.Command);
        Assert.True(invocation.DryRun);
        Assert.Equal(
            ["first.osm.pbf", "second.osm.pbf"],
            invocation.Options["pbf"]);
        Assert.Equal(
            ["from-command-line"],
            invocation.Options["working-directory"]);
        Assert.Equal(["2097152"], invocation.Options["memory-budget-bytes"]);
        Assert.Equal(["admins.sqlite"], invocation.Options["output"]);
    }

    [Fact]
    public async Task InvalidConfiguration_FailsBeforeMutation()
    {
        Directory.CreateDirectory(scratch);
        string outputPath = Path.Combine(scratch, "must-not-exist.sqlite");
        string configPath = Path.Combine(scratch, "invalid.json");
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 999,
                    command = "build-admins",
                    options = new Dictionary<string, string>
                    {
                        ["output"] = outputPath,
                    },
                }));

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await ValhallaGenerationCli.RunAsync(
            ["build-admins", "--config", configPath],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ValhallaGenerationCliExitCodes.ConfigurationFailure,
            exitCode);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ExitCodeMatrix_IsStable()
    {
        Assert.Equal(0, ValhallaGenerationCliExitCodes.Success);
        Assert.Equal(1, ValhallaGenerationCliExitCodes.UnexpectedFailure);
        Assert.Equal(2, ValhallaGenerationCliExitCodes.ConfigurationFailure);
        Assert.Equal(3, ValhallaGenerationCliExitCodes.ValidationFailure);
        Assert.Equal(4, ValhallaGenerationCliExitCodes.Cancellation);
        Assert.Equal(5, ValhallaGenerationCliExitCodes.ResourceExhaustion);

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        int exitCode = await ValhallaGenerationCli.RunAsync(
            ["validate", "--graph-directory", scratch],
            output,
            error,
            cancellation.Token);

        Assert.Equal(ValhallaGenerationCliExitCodes.Cancellation, exitCode);
    }

    [Fact]
    public async Task ValidationFailure_ReportsSecretSafeDiagnostic()
    {
        string invalidGraphDirectory = Path.Combine(scratch, "invalid-graph");
        Directory.CreateDirectory(invalidGraphDirectory);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await ValhallaGenerationCli.RunAsync(
        [
            "validate",
            "--graph-directory",
            invalidGraphDirectory,
            "--working-directory",
            scratch,
        ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ValhallaGenerationCliExitCodes.ValidationFailure,
            exitCode);
        Assert.Contains(
            "The staged graph does not contain graph tiles.",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            invalidGraphDirectory,
            output.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void TransitBuild_RequiresExplicitBuildDate()
    {
        ValhallaGenerationCliConfigurationException exception =
            Assert.Throws<ValhallaGenerationCliConfigurationException>(
                () => ValhallaGenerationCli.ResolveInvocation(
                [
                    "build-transit",
                    "--feed",
                    Path.Combine(scratch, "feed"),
                    "--output",
                    Path.Combine(scratch, "transit"),
                ]));

        Assert.Contains("--build-date", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTiles_StagesOutputBesideDestinationForCrossVolumeAtomicPromotion()
    {
        string outputDirectory = Path.Combine(scratch, "output-volume", "tiles");
        System.Reflection.MethodInfo? method = typeof(ValhallaGenerationCli).GetMethod(
            "CreateStagingDirectoryPath",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        string stagingDirectory = Assert.IsType<string>(
            method.Invoke(null, [outputDirectory, Guid.Empty]));

        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath(outputDirectory)),
            Path.GetDirectoryName(stagingDirectory));
        Assert.StartsWith(
            ".tiles.incoming-",
            Path.GetFileName(stagingDirectory),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildTiles_UsesManagedSinglePassComposition()
    {
        Directory.CreateDirectory(scratch);
        string pbfPath = FindRepositoryArtifact(
            "artifacts",
            "monaco.osm.pbf");
        string outputDirectory = Path.Combine(scratch, "managed-tiles");
        string workingDirectory = Path.Combine(scratch, "managed-work");
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await ValhallaGenerationCli.RunAsync(
        [
            "build-tiles",
            "--pbf",
            pbfPath,
            "--output",
            outputDirectory,
            "--working-directory",
            workingDirectory,
            "--storage-mode",
            "MemoryMapped",
            "--memory-budget-bytes",
            (64 * 1024 * 1024).ToString(),
            "--scratch-budget-bytes",
            (512 * 1024 * 1024).ToString(),
        ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(ValhallaGenerationCliExitCodes.Success, exitCode);
        Assert.Empty(error.ToString());
        string receiptLine = output
            .ToString()
            .Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)[^1];
        using JsonDocument receipt = JsonDocument.Parse(receiptLine);
        JsonElement data = receipt.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("tileCount").GetInt32() > 0);
        Assert.Equal(
            data.GetProperty("pbfDataBlockCount").GetInt32(),
            data.GetProperty("pbfDecompressionCount").GetInt32() - 1);
        Assert.True(
            data.GetProperty("peakIntermediateMemoryBytes").GetInt64() >= 0);
        Assert.True(
            data.GetProperty("pbfIngestionDurationMilliseconds").GetDouble() > 0);
        Assert.True(
            data.GetProperty("semanticParsingDurationMilliseconds").GetDouble() > 0);
        Assert.True(
            data.GetProperty("tileConstructionDurationMilliseconds").GetDouble() > 0);
        JsonElement semanticStages =
            data.GetProperty("semanticStageDurationsMilliseconds");
        Assert.True(semanticStages.GetProperty("ways").GetDouble() > 0);
        Assert.True(semanticStages.GetProperty("nodes").GetDouble() > 0);
        Assert.True(semanticStages.GetProperty("relations").GetDouble() > 0);
        JsonElement tileStages =
            data.GetProperty("tileStageDurationsMilliseconds");
        Assert.True(tileStages.GetProperty("constructEdges").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("build").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("enhance").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("flush").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("hierarchy").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("shortcuts").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("restrictions").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tiles").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tweeners").GetDouble() >= 0);
        Assert.True(tileStages.GetProperty("validate.checksums").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tile.deserialize").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tile.edges").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tile.binning").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tile.update").GetDouble() > 0);
        Assert.True(tileStages.GetProperty("validate.tile.add-bins").GetDouble() >= 0);
        JsonElement enhancerOperations = data.GetProperty("enhancerOperationCounts");
        Assert.True(enhancerOperations.GetProperty("secondPassEdges").GetUInt64() > 0);
        Assert.True(
            enhancerOperations.GetProperty("nameConsistencyChecks").GetUInt64() >=
            enhancerOperations.GetProperty("secondPassEdges").GetUInt64());
        Assert.Equal(
            enhancerOperations.GetProperty("secondPassEdges").GetUInt64(),
            enhancerOperations.GetProperty("internalIntersectionChecks").GetUInt64());
        Assert.Equal(
            enhancerOperations.GetProperty("secondPassEdges").GetUInt64(),
            enhancerOperations.GetProperty("stopYieldChecks").GetUInt64());
        Assert.True(
            enhancerOperations.GetProperty("notThruNodeExpansions").GetUInt64() >=
            enhancerOperations.GetProperty("notThruChecks").GetUInt64());
        ulong notThruScratchAllocations =
            enhancerOperations.GetProperty("notThruScratchAllocations").GetUInt64();
        Assert.InRange(
            notThruScratchAllocations,
            1UL,
            enhancerOperations.GetProperty("notThruChecks").GetUInt64());
        Assert.NotEmpty(
            Directory.GetFiles(
                outputDirectory,
                "*.gph",
                SearchOption.AllDirectories));
    }

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Repository artifact was not found.",
            Path.Combine(parts));
    }

    public void Dispose()
    {
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}

public sealed class ValhallaGenerationCliTelemetryTests
{
    [Fact]
    public async Task Output_IsMachineReadableAndSecretSafe()
    {
        const string secret = "never-print-this-api-key";
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await ValhallaGenerationCli.RunAsync(
        [
            "build-admins",
            "--unknown-option",
            $"https://user:{secret}@example.test/feed?api_key={secret}",
        ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ValhallaGenerationCliExitCodes.ConfigurationFailure,
            exitCode);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());

        string[] lines = output
            .ToString()
            .Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }

        using JsonDocument receipt = JsonDocument.Parse(lines[^1]);
        Assert.Equal(
            "receipt",
            receipt.RootElement.GetProperty("type").GetString());
        Assert.False(
            receipt.RootElement.GetProperty("success").GetBoolean());
    }
}
