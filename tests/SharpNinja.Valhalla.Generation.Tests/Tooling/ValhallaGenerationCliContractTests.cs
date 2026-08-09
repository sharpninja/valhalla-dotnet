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
