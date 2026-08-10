using System.Text.Json;

using SharpNinja.Valhalla.Generation.Benchmarks;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Qualification;

public sealed class ProcessGenerationBenchmarkHarnessTests
{
    [Fact]
    public void LoadConfiguration_RelativePathsResolveAgainstConfigurationDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-benchmark-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string inputPath = Path.Combine(root, "input.osm.pbf");
        string officialConfigPath = Path.Combine(root, "official-valhalla.json");
        File.WriteAllBytes(inputPath, [1, 2, 3, 4]);
        File.WriteAllText(officialConfigPath, "{}");

        try
        {
            var configuration = new
            {
                schemaVersion = 1,
                datasetName = "fixture",
                inputPath = "input.osm.pbf",
                managed = CreateCommand(
                    "{managed-image}",
                    new Dictionary<string, string>()),
                official = CreateCommand(
                    "{official-image}",
                    new Dictionary<string, string>
                    {
                        ["official-valhalla.json"] = "official-valhalla.json",
                    }),
                warmupRuns = 1,
                measuredRuns = 5,
            };
            string configurationPath = Path.Combine(root, "benchmark.json");
            File.WriteAllText(
                configurationPath,
                JsonSerializer.Serialize(configuration));

            GenerationBenchmarkConfiguration loaded =
                ProcessGenerationBenchmarkHarness.LoadConfiguration(configurationPath);

            Assert.Equal(Path.GetFullPath(inputPath), loaded.InputPath);
            Assert.Equal(
                Path.GetFullPath(officialConfigPath),
                loaded.Official.Docker!.AdditionalInputFiles["official-valhalla.json"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        "{\"CPUPerc\":\"125.50%\",\"MemUsage\":\"1.5GiB / 8GiB\"}",
        1_610_612_736L,
        125.5)]
    [InlineData(
        "{\"CPUPerc\":\"0.25%\",\"MemUsage\":\"768MiB / 8GiB\"}",
        805_306_368L,
        0.25)]
    [InlineData(
        "\u001b[H{\"CPUPerc\":\"1592.41%\",\"MemUsage\":\"3.795GiB / 8GiB\"}\u001b[K",
        4_074_850_222L,
        1592.41)]
    public void DockerStatsSampleParser_ParsesContainerMemoryAndCpu(
        string json,
        long expectedMemoryBytes,
        double expectedCpuPercentage)
    {
        bool parsed = DockerStatsSampleParser.TryParse(json, out DockerStatsSample sample);

        Assert.True(parsed);
        Assert.Equal(expectedMemoryBytes, sample.MemoryBytes);
        Assert.Equal(expectedCpuPercentage, sample.CpuPercentage, precision: 3);
    }

    [Fact]
    public void Measurement_RecordsHashAndRemovesDisposableAttemptTree()
    {
        string attemptDirectory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-benchmark-attempt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attemptDirectory);
        File.WriteAllBytes(Path.Combine(attemptDirectory, "tile.gph"), [1, 2, 3, 4]);

        try
        {
            System.Reflection.MethodInfo? method =
                typeof(ProcessGenerationBenchmarkHarness).GetMethod(
                    "MeasureAndRemoveAttemptOutput",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);

            Assert.NotNull(method);
            var measurement = Assert.IsType<ValueTuple<long, string?>>(
                method.Invoke(null, [attemptDirectory, true]));

            Assert.Equal(4, measurement.Item1);
            Assert.Matches("^[a-f0-9]{64}$", measurement.Item2);
            Assert.False(Directory.Exists(attemptDirectory));
        }
        finally
        {
            if (Directory.Exists(attemptDirectory))
            {
                Directory.Delete(attemptDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void NashvilleConfiguration_UsesPinnedNativeVolumeContainersAndEqualLimits()
    {
        string repositoryRoot = FindRepositoryRoot();
        string configurationPath = Path.Combine(
            repositoryRoot,
            "benchmarks",
            "config",
            "nashville-tennessee-3.8.3.json");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        JsonElement root = document.RootElement;
        JsonElement managed = root.GetProperty("managed").GetProperty("docker");
        JsonElement official = root.GetProperty("official").GetProperty("docker");

        Assert.Equal("{managed-image}", managed.GetProperty("image").GetString());
        Assert.Equal("{official-image}", official.GetProperty("image").GetString());
        Assert.Equal(
            managed.GetProperty("cpuLimit").GetDouble(),
            official.GetProperty("cpuLimit").GetDouble());
        Assert.Equal(
            managed.GetProperty("memoryLimitBytes").GetInt64(),
            official.GetProperty("memoryLimitBytes").GetInt64());
        Assert.True(managed.GetProperty("useNativeVolumes").GetBoolean());
        Assert.True(official.GetProperty("useNativeVolumes").GetBoolean());
        string[] managedArguments = root.GetProperty("managed")
            .GetProperty("arguments")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        int outputIndex = Array.IndexOf(managedArguments, "--output");
        Assert.Equal("/output/tiles", managedArguments[outputIndex + 1]);
        string officialConfiguration = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "benchmarks",
            "config",
            "official-valhalla-3.8.3.json"));
        Assert.Contains("\"tile_dir\": \"/output/tiles\"", officialConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "password",
            File.ReadAllText(configurationPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static object CreateCommand(
        string image,
        IReadOnlyDictionary<string, string> additionalInputFiles) =>
        new
        {
            id = image,
            fileName = "docker",
            arguments = Array.Empty<string>(),
            docker = new
            {
                image,
                entryPoint = "/bin/true",
                inputFileName = "input.osm.pbf",
                additionalInputFiles,
                cpuLimit = 2.0,
                memoryLimitBytes = 1_073_741_824L,
                pidsLimit = 256,
                useNativeVolumes = true,
            },
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpNinja.Valhalla.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
