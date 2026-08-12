using System.Text.Json;
using SharpNinja.Valhalla.Generation;
using SharpNinja.Valhalla.Generation.Differential;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Differential;

public sealed class UpstreamOracleContractTests
{
    [Fact]
    public void PinnedValhalla383Oracle_IsCompleteAndImmutable()
    {
        using JsonDocument document = LoadFixture("Oracle", "valhalla-3.8.3.json");
        JsonElement upstream = document.RootElement.GetProperty("upstream");

        Assert.Equal("3.8.3", upstream.GetProperty("release").GetString());
        Assert.Equal(
            "a60c7cbfc83e073f50887cd27e0109d02e6b64e5",
            upstream.GetProperty("commit").GetString());
        Assert.Equal(
            "sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f",
            upstream.GetProperty("containerDigest").GetString());
        Assert.True(
            document.RootElement
                .GetProperty("verification")
                .GetProperty("tagMatchedCommit")
                .GetBoolean());
        Assert.True(
            document.RootElement
                .GetProperty("verification")
                .GetProperty("installedImageMatchedDigest")
                .GetBoolean());
    }

    private static JsonDocument LoadFixture(params string[] pathSegments)
    {
        string path = Path.Combine([AppContext.BaseDirectory, "Fixtures", .. pathSegments]);
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }
}

public sealed class NashvilleBaselineContractTests
{
    [Fact]
    public void Baseline_PreservesMeasuredInputsAndQualificationThresholds()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Baselines",
            "nashville-tennessee-20260808.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement workload = document.RootElement.GetProperty("workload");
        JsonElement target = document.RootElement.GetProperty("qualificationTargets");

        Assert.Equal(187116535, workload.GetProperty("inputLengthBytes").GetInt64());
        Assert.Equal(
            "6AD6323EF76D47D13F8889F974968F04DE72F56EFCFBD9070446C18E4FFB172B",
            workload.GetProperty("inputSha256").GetString());
        Assert.Equal(269, workload.GetProperty("outputGraphTileCount").GetInt32());
        Assert.Equal(
            20.0,
            target.GetProperty("minimumManagedMedianSpeedupPercent").GetDouble());
        Assert.Equal(
            1.25,
            target.GetProperty("maximumManagedToOfficialPeakMemoryRatio").GetDouble());
        Assert.Equal(5, target.GetProperty("requiredMeasuredRuns").GetInt32());
    }
}

public sealed class Valhalla383GraphHeaderDifferentialTests
{
    [Fact]
    public void HeaderComparator_NormalizesBuildMetadataAndPreservesFormatFields()
    {
        ValhallaSemanticGraphSnapshot expected = Snapshot(
            graphFormatVersion: "3.8",
            datasetId: 42,
            inputIdentity: "input-a",
            buildId: 100,
            createdAtUtc: DateTimeOffset.Parse("2026-08-08T00:00:00Z"),
            outputChecksum: "official-checksum");
        ValhallaSemanticGraphSnapshot actual = Snapshot(
            graphFormatVersion: "3.8",
            datasetId: 42,
            inputIdentity: "input-a",
            buildId: 200,
            createdAtUtc: DateTimeOffset.Parse("2026-08-08T01:00:00Z"),
            outputChecksum: "managed-checksum");

        IReadOnlyList<ValhallaSemanticDifference> differences =
            new ValhallaSemanticGraphComparator().Compare(expected, actual);

        Assert.Empty(differences);

        actual = actual with { DatasetId = 43 };
        differences = new ValhallaSemanticGraphComparator().Compare(expected, actual);
        ValhallaSemanticDifference difference = Assert.Single(differences);
        Assert.Equal("$.datasetId", difference.Path);
    }

    internal static ValhallaSemanticGraphSnapshot Snapshot(
        string graphFormatVersion = "3.8",
        ulong datasetId = 42,
        string inputIdentity = "input-a",
        ulong buildId = 100,
        DateTimeOffset? createdAtUtc = null,
        string outputChecksum = "checksum",
        IReadOnlyList<ValhallaSemanticTileSnapshot>? tiles = null)
    {
        return new ValhallaSemanticGraphSnapshot(
            graphFormatVersion,
            datasetId,
            inputIdentity,
            buildId,
            createdAtUtc ?? DateTimeOffset.Parse("2026-08-08T00:00:00Z"),
            outputChecksum,
            tiles ??
            [
                new ValhallaSemanticTileSnapshot(
                    "2/123",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["density"] = "7",
                    },
                    new Dictionary<string, IReadOnlyList<ValhallaSemanticRecord>>(StringComparer.Ordinal)
                    {
                        ["directedEdges"] =
                        [
                            new ValhallaSemanticRecord(
                                "edge-1",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["access"] = "truck|auto",
                                    ["speed"] = "65",
                                }),
                        ],
                    }),
            ]);
    }
}

public sealed class ValhallaSemanticGraphComparatorTests
{
    [Fact]
    public void RoutingRelevantFieldDifference_IsReported()
    {
        ValhallaSemanticGraphSnapshot expected =
            Valhalla383GraphHeaderDifferentialTests.Snapshot();
        ValhallaSemanticTileSnapshot tile = expected.Tiles[0];
        ValhallaSemanticRecord changedEdge = tile.Sections["directedEdges"][0] with
        {
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["access"] = "truck|auto",
                ["speed"] = "55",
            },
        };
        ValhallaSemanticGraphSnapshot actual = expected with
        {
            Tiles =
            [
                tile with
                {
                    Sections = new Dictionary<string, IReadOnlyList<ValhallaSemanticRecord>>(
                        StringComparer.Ordinal)
                    {
                        ["directedEdges"] = [changedEdge],
                    },
                },
            ],
        };

        IReadOnlyList<ValhallaSemanticDifference> differences =
            new ValhallaSemanticGraphComparator().Compare(expected, actual);

        ValhallaSemanticDifference difference = Assert.Single(differences);
        Assert.Contains("speed", difference.Path, StringComparison.Ordinal);
        Assert.Equal("65", difference.Expected);
        Assert.Equal("55", difference.Actual);
    }

    [Fact]
    public void EverySectionAndRecord_IsCompared()
    {
        ValhallaSemanticGraphSnapshot expected =
            Valhalla383GraphHeaderDifferentialTests.Snapshot();
        ValhallaSemanticTileSnapshot tile = expected.Tiles[0];
        ValhallaSemanticGraphSnapshot actual = expected with
        {
            Tiles =
            [
                tile with
                {
                    Sections = new Dictionary<string, IReadOnlyList<ValhallaSemanticRecord>>(
                        StringComparer.Ordinal)
                    {
                        ["directedEdges"] =
                        [
                            .. tile.Sections["directedEdges"],
                            new ValhallaSemanticRecord(
                                "edge-2",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["access"] = "truck",
                                    ["speed"] = "45",
                                }),
                        ],
                    },
                },
            ],
        };

        IReadOnlyList<ValhallaSemanticDifference> differences =
            new ValhallaSemanticGraphComparator().Compare(expected, actual);

        Assert.Contains(
            differences,
            difference => difference.Path.Contains("edge-2", StringComparison.Ordinal));
    }
}

public sealed class OfficialRouteDifferentialHarnessTests
{
    [Fact]
    public void RouteMatrixComparator_AppliesDocumentedMetricTolerances()
    {
        ValhallaRouteMatrixEntry expected =
            new("truck-bna-downtown", true, 17_100, 840, ["101", "102"]);
        ValhallaRouteMatrixEntry withinTolerance =
            new("truck-bna-downtown", true, 17_110, 845, ["101", "102"]);
        ValhallaRouteMetricTolerances tolerances = new(25, 10, 0.01);

        IReadOnlyList<ValhallaSemanticDifference> differences =
            new ValhallaRouteMatrixComparator().Compare(
                [expected],
                [withinTolerance],
                tolerances);

        Assert.Empty(differences);

        ValhallaRouteMatrixEntry outsideTolerance =
            withinTolerance with { DurationSeconds = 900 };
        differences = new ValhallaRouteMatrixComparator().Compare(
            [expected],
            [outsideTolerance],
            tolerances);

        Assert.Contains(
            differences,
            difference => difference.Path.EndsWith(".durationSeconds", StringComparison.Ordinal));
    }
}

public sealed class BidirectionalTileCompatibilityHarnessTests
{
    [Fact]
    public async Task ManagedAndOfficialAdapters_MustCrossReadSameFixture()
    {
        ValhallaSemanticGraphSnapshot managed =
            Valhalla383GraphHeaderDifferentialTests.Snapshot(buildId: 1);
        ValhallaSemanticGraphSnapshot official =
            Valhalla383GraphHeaderDifferentialTests.Snapshot(buildId: 2);
        DictionarySemanticReader managedReader = new(
            new Dictionary<string, ValhallaSemanticGraphSnapshot>(StringComparer.Ordinal)
            {
                ["managed"] = managed,
                ["official"] = official,
            });
        DictionarySemanticReader officialReader = new(
            new Dictionary<string, ValhallaSemanticGraphSnapshot>(StringComparer.Ordinal)
            {
                ["managed"] = managed,
                ["official"] = official,
            });

        BidirectionalTileCompatibilityReport report =
            await new BidirectionalTileCompatibilityHarness().VerifyAsync(
                "managed",
                "official",
                managedReader,
                officialReader,
                TestContext.Current.CancellationToken);

        Assert.True(report.IsCompatible);
    }

    private sealed class DictionarySemanticReader(
        IReadOnlyDictionary<string, ValhallaSemanticGraphSnapshot> snapshots)
        : IValhallaSemanticGraphReader
    {
        public ValueTask<ValhallaSemanticGraphSnapshot> ReadAsync(
            string artifactPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshots[artifactPath]);
        }
    }
}

public sealed class GenerationOutputTreeHasherTests
{
    [Fact]
    public async Task FixedTree_ProducesStableHashAcrossEnumerationOrder()
    {
        string rootA = Path.Combine(Path.GetTempPath(), $"valhalla-tree-a-{Guid.NewGuid():N}");
        string rootB = Path.Combine(Path.GetTempPath(), $"valhalla-tree-b-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(rootA, "2"));
            Directory.CreateDirectory(Path.Combine(rootB, "2"));
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(
                Path.Combine(rootA, "manifest.json"),
                "manifest",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(rootA, "2", "tile.gph"),
                "tile",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(rootB, "2", "tile.gph"),
                "tile",
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(rootB, "manifest.json"),
                "manifest",
                cancellationToken);

            GenerationOutputTreeHasher hasher = new();
            string hashA = await hasher.ComputeSha256Async(rootA, cancellationToken);
            string hashB = await hasher.ComputeSha256Async(rootB, cancellationToken);

            Assert.Equal(hashA, hashB);
            Assert.Matches("^[A-F0-9]{64}$", hashA);
        }
        finally
        {
            if (Directory.Exists(rootA))
            {
                Directory.Delete(rootA, recursive: true);
            }

            if (Directory.Exists(rootB))
            {
                Directory.Delete(rootB, recursive: true);
            }
        }
    }
}

public sealed class GenerationStageReceiptTests
{
    [Fact]
    public void StageReceipt_TracksRequiredResourceCounters()
    {
        DateTimeOffset started = DateTimeOffset.Parse("2026-08-08T00:00:00Z");
        ValhallaGenerationStageReceipt receipt = new(
            ValhallaGenerationStage.IngestOsm,
            started,
            started.AddSeconds(2),
            "input",
            "output",
            RecordsProcessed: 100,
            BytesRead: 200,
            BytesWritten: 300,
            MaximumConcurrency: 4,
            AllocatedBytes: 500,
            PeakWorkingSetBytes: 600,
            ScratchDiskHighWaterMarkBytes: 700,
            CheckpointIdentity: "checkpoint",
            Warnings: [],
            Failures: [],
            OutputHashes: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["output"] = "ABCDEF",
            });

        Assert.Equal(TimeSpan.FromSeconds(2), receipt.Duration);
        Assert.Equal(100, receipt.RecordsProcessed);
        Assert.Equal(200, receipt.BytesRead);
        Assert.Equal(300, receipt.BytesWritten);
        Assert.Equal(4, receipt.MaximumConcurrency);
        Assert.Equal(500, receipt.AllocatedBytes);
        Assert.Equal(600, receipt.PeakWorkingSetBytes);
        Assert.Equal(700, receipt.ScratchDiskHighWaterMarkBytes);
        Assert.Equal("checkpoint", receipt.CheckpointIdentity);
        Assert.Equal("ABCDEF", receipt.OutputHashes["output"]);
    }
}