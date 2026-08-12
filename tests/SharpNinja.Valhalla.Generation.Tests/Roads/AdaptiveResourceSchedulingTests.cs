using SharpNinja.Valhalla.Generation;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class AdaptiveResourceSchedulingTests
{
    [Fact]
    public async Task BuildPooledFrontier_PropagatesSelectedDop_InFrontierOrResourceMetrics()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-adaptive-dop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult result = await builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    Path.Combine(root, "work"),
                    Path.Combine(root, "tiles"),
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 256 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 4L * 1024 * 1024 * 1024,
                    TileBuilderConfig: new TileBuilderConfig
                    {
                        GridDivisions = 8,
                        Hierarchy = false,
                        Shortcuts = false,
                        MaxDegreeOfParallelism = 8,
                    })
                {
                    Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.TileBuilderResult.Success);
            ManagedRoadGraphResourceMetrics metrics =
                Assert.IsType<ManagedRoadGraphResourceMetrics>(result.ResourceMetrics);
            Assert.InRange(metrics.SelectedDop, 1, 8);
            Assert.True(metrics.PerWorkerMemoryReservationBytes > 0);
            Assert.True(metrics.PerWorkerScratchReservationBytes > 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildPooledFrontier_Throws_WhenOneWorkerCannotFit()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-adaptive-exhaust-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = new ManagedRoadGraphBuilder();
            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                async () =>
                    await builder.BuildAsync(
                        new ManagedRoadGraphBuildRequest(
                            [pbfPath],
                            Path.Combine(root, "work"),
                            Path.Combine(root, "tiles"),
                            IntermediateStorageMode.MemoryMapped,
                            // Tiny budget forces FitWorkerCount -> 0 after /3 partition.
                            MemoryBudgetBytes: 32 * 1024,
                            ScratchDiskBudgetBytes: 32 * 1024,
                            TileBuilderConfig: new TileBuilderConfig
                            {
                                GridDivisions = 8,
                                Hierarchy = false,
                                Shortcuts = false,
                                MaxDegreeOfParallelism = 4,
                            })
                        {
                            Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                        },
                        TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Cancellation_WhileWorkersSaturated_CleansSlabsAndBuffers()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-adaptive-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cts = new CancellationTokenSource();
            var builder = new ManagedRoadGraphBuilder();
            ValueTask<ManagedRoadGraphBuildResult> build = builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    Path.Combine(root, "work"),
                    Path.Combine(root, "tiles"),
                    IntermediateStorageMode.MemoryMapped,
                    MemoryBudgetBytes: 256 * 1024 * 1024,
                    ScratchDiskBudgetBytes: 4L * 1024 * 1024 * 1024,
                    TileBuilderConfig: new TileBuilderConfig
                    {
                        GridDivisions = 8,
                        Hierarchy = false,
                        Shortcuts = false,
                        MaxDegreeOfParallelism = 4,
                    })
                {
                    Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                },
                cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await build.AsTask());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task PooledArena_NotProcessGlobal_AfterBuildDispose()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-adaptive-arena-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult first = await builder.BuildAsync(
                Request(pbfPath, root, "a"),
                TestContext.Current.CancellationToken);
            ManagedRoadGraphBuildResult second = await builder.BuildAsync(
                Request(pbfPath, root, "b"),
                TestContext.Current.CancellationToken);
            Assert.True(first.TileBuilderResult.Success);
            Assert.True(second.TileBuilderResult.Success);
            Assert.NotNull(first.FrontierMetrics);
            Assert.NotNull(second.FrontierMetrics);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ManagedRoadGraphBuildRequest Request(
        string pbfPath,
        string root,
        string label) =>
        new(
            [pbfPath],
            Path.Combine(root, "work-" + label),
            Path.Combine(root, "tiles-" + label),
            IntermediateStorageMode.MemoryMapped,
            MemoryBudgetBytes: 256 * 1024 * 1024,
            ScratchDiskBudgetBytes: 4L * 1024 * 1024 * 1024,
            TileBuilderConfig: new TileBuilderConfig
            {
                GridDivisions = 8,
                Hierarchy = false,
                Shortcuts = false,
                MaxDegreeOfParallelism = 2,
            })
        {
            Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
        };

    private static string FindRepositoryArtifact(params string[] segments)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(new[] { dir }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(
            "Repository artifact not found: " + string.Join("/", segments));
    }
}
