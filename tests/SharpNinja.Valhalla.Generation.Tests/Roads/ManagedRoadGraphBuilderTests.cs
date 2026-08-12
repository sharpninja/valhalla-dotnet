using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class ManagedRoadGraphBuilderTests
{
    [Fact]
    public async Task BuildAsync_DecodesEachPbfBlockOnceAndProducesGraph()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-road-builder-" + Guid.NewGuid().ToString("N"));
        string workingDirectory = Path.Combine(root, "work");
        string outputDirectory = Path.Combine(root, "tiles");
        Directory.CreateDirectory(root);

        try
        {
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult result = await builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    workingDirectory,
                    outputDirectory,
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 64 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 512 * 1024 * 1024,
                    TileBuilderConfig: new TileBuilderConfig
                    {
                        Hierarchy = true,
                        Shortcuts = true,
                    }),
                TestContext.Current.CancellationToken);

            Assert.True(result.TileBuilderResult.Success);
            Assert.True(result.TileBuilderResult.TileCount > 0);
            Assert.True(result.TileBuilderResult.WayCount > 0);
            Assert.All(
                new[] { "deserialize", "first-pass", "density", "second-pass", "serialize" },
                stage => Assert.Contains(
                    $"enhance.tile.{stage}",
                    result.TileBuilderResult.StageDurations));
            StreamingOsmPbfBlockReceipt[] dataBlocks = result.PbfMetrics
                .BlockReceipts
                .Where(receipt => receipt.BlobType == "OSMData")
                .ToArray();
            Assert.Equal(result.PbfMetrics.DataBlockCount, dataBlocks.Length);
            Assert.All(
                dataBlocks,
                receipt => Assert.Equal(1, receipt.DecompressionCount));
            Assert.True(result.PbfMetrics.DataBlockCount > 0);
            Assert.NotEmpty(
                Directory.GetFiles(
                    outputDirectory,
                    "*.gph",
                    SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_ParallelTileConstructionMatchesSerialOutput()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-road-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string serialOutput = Path.Combine(root, "serial");
            string parallelOutput = Path.Combine(root, "parallel");
            ManagedRoadGraphBuildResult serial = await BuildAsync(
                pbfPath,
                root,
                serialOutput,
                maxDegreeOfParallelism: 1);
            ManagedRoadGraphBuildResult parallel = await BuildAsync(
                pbfPath,
                root,
                parallelOutput,
                maxDegreeOfParallelism: 4);

            Assert.Equal(1, serial.TileBuilderResult.EnhancerStats?.MaximumConcurrency);
            Assert.InRange(
                parallel.TileBuilderResult.EnhancerStats?.MaximumConcurrency ?? 0,
                2,
                4);

            string[] serialFiles = Directory.GetFiles(
                    serialOutput,
                    "*.gph",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(serialOutput, path))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] parallelFiles = Directory.GetFiles(
                    parallelOutput,
                    "*.gph",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(parallelOutput, path))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(serialFiles, parallelFiles);
            foreach (string relativePath in serialFiles)
            {
                Assert.Equal(
                    await File.ReadAllBytesAsync(
                        Path.Combine(serialOutput, relativePath),
                        TestContext.Current.CancellationToken),
                    await File.ReadAllBytesAsync(
                        Path.Combine(parallelOutput, relativePath),
                        TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task BuildAsync_PooledFrontier_ComposesBoundedProductionPipeline()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-road-pooled-composition-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string outputDirectory = Path.Combine(root, "tiles");
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult result = await builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    Path.Combine(root, "work"),
                    outputDirectory,
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 256 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 4L * 1024 * 1024 * 1024,
                    TileBuilderConfig: new TileBuilderConfig
                    {
                        GridDivisions = 8,
                        Hierarchy = false,
                        Shortcuts = false,
                        MaxDegreeOfParallelism = 1,
                    })
                {
                    Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.TileBuilderResult.Success);
            Assert.True(result.TileBuilderResult.TileCount > 0);
            Assert.NotNull(result.FrontierMetrics);
            Assert.True(result.FrontierMetrics.GraphAnchorsCreated > 0);
            ManagedRoadGraphResourceMetrics resourceMetrics =
                Assert.IsType<ManagedRoadGraphResourceMetrics>(
                    result.ResourceMetrics);
            Assert.Equal(
                resourceMetrics.MemoryHighWaterMarkBytes,
                result.PeakIntermediateMemoryBytes);
            Assert.Equal(
                resourceMetrics.ScratchHighWaterMarkBytes,
                result.ScratchDiskHighWaterMarkBytes);
            Assert.Equal(
                new[]
                {
                    resourceMetrics.IngestionMemoryPeakBytes,
                    resourceMetrics.SemanticPhaseMemoryPeakBytes,
                    resourceMetrics.GraphAndTilePhaseMemoryPeakBytes,
                    resourceMetrics.RestrictionPhaseMemoryPeakBytes,
                }.Max(),
                result.PeakIntermediateMemoryBytes);
            Assert.Equal(
                new[]
                {
                    resourceMetrics.IngestionScratchPeakBytes,
                    resourceMetrics.SemanticPhaseScratchPeakBytes,
                    resourceMetrics.GraphAndTilePhaseScratchPeakBytes,
                    resourceMetrics.RestrictionPhaseScratchPeakBytes,
                }.Max(),
                result.ScratchDiskHighWaterMarkBytes);
            Assert.True(
                resourceMetrics.GraphAndTilePhaseScratchPeakBytes >=
                result.FrontierMetrics.MappedStorageHighWaterMarkBytes);
            Assert.True(
                resourceMetrics.RestrictionPhaseScratchPeakBytes > 0);
            Assert.True(result.ScratchDiskHighWaterMarkBytes > 0);
            Assert.Contains(
                "pooled.restrictions",
                result.TileBuilderResult.StageDurations);
            Assert.NotEmpty(
                Directory.GetFiles(
                    outputDirectory,
                    "*.gph",
                    SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }



    private static async Task<ManagedRoadGraphBuildResult> BuildAsync(
        string pbfPath,
        string root,
        string outputDirectory,
        int maxDegreeOfParallelism)
    {
        var builder = new ManagedRoadGraphBuilder();
        ManagedRoadGraphBuildResult result = await builder.BuildAsync(
            new ManagedRoadGraphBuildRequest(
                [pbfPath],
                Path.Combine(root, "work-" + maxDegreeOfParallelism),
                outputDirectory,
                IntermediateStorageMode.MemoryMapped,
                MemoryBudgetBytes: 64 * 1024 * 1024,
                ScratchDiskBudgetBytes: 512 * 1024 * 1024,
                TileBuilderConfig: new TileBuilderConfig
                {
                    Hierarchy = true,
                    Shortcuts = true,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                }),
            TestContext.Current.CancellationToken);

        Assert.True(result.TileBuilderResult.Success);
        return result;
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
}
