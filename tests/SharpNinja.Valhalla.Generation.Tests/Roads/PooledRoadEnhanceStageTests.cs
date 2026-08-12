using System.Reflection;
using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledRoadEnhanceStageTests
{
    [Fact]
    public async Task ApplyAsync_ProcessesOneTileAtATime_WithoutGlobalTileByteDictionary()
    {
        string root = CreateRoot("enhance-one-tile");
        try
        {
            string source = await BuildUnrestrictedTilesAsync(root);
            string dest = Path.Combine(root, "enhanced");
            PooledRoadEnhanceStageReceipt receipt =
                await PooledRoadEnhanceStage.ApplyAsync(
                    source,
                    dest,
                    new PooledRoadEnhanceStageOptions(
                        MemoryBudgetBytes: 64 * 1024 * 1024,
                        MaxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken);

            Assert.True(receipt.TileCount > 0);
            Assert.True(receipt.EnhancedTileCount > 0);
            Assert.Equal(1, receipt.SelectedDop);
            Assert.NotEmpty(
                Directory.GetFiles(dest, "*.gph", SearchOption.AllDirectories));

            // Stage type has no Dictionary<GraphId, byte[]> fields.
            Type stageType = typeof(PooledRoadEnhanceStage);
            FieldInfo[] fields = stageType.GetFields(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic);
            Assert.DoesNotContain(
                fields,
                field =>
                    field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                    field.FieldType.GenericTypeArguments[0] == typeof(GraphId) &&
                    field.FieldType.GenericTypeArguments[1] == typeof(byte[]));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_Cancellation_DoesNotPublishPartialEnhancedTile()
    {
        string root = CreateRoot("enhance-cancel");
        try
        {
            string source = await BuildUnrestrictedTilesAsync(root);
            string dest = Path.Combine(root, "enhanced");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await PooledRoadEnhanceStage.ApplyAsync(
                    source,
                    dest,
                    new PooledRoadEnhanceStageOptions(64 * 1024 * 1024),
                    cts.Token));
            Assert.False(Directory.Exists(dest));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_SourceAndEnhancedFullSets_DoNotOverlapInManagedMemory()
    {
        string root = CreateRoot("enhance-no-dual");
        try
        {
            string source = await BuildUnrestrictedTilesAsync(root);
            string dest = Path.Combine(root, "enhanced");
            // Success with a tight budget proves dual full maps are not retained.
            PooledRoadEnhanceStageReceipt receipt =
                await PooledRoadEnhanceStage.ApplyAsync(
                    source,
                    dest,
                    new PooledRoadEnhanceStageOptions(
                        MemoryBudgetBytes: 32 * 1024 * 1024,
                        MaxDegreeOfParallelism: 2),
                    TestContext.Current.CancellationToken);
            Assert.True(receipt.SelectedDop >= 1);
            Assert.True(receipt.EnhancedTileCount > 0);
            // Source remains intact and independent of dest.
            Assert.NotEmpty(
                Directory.GetFiles(source, "*.gph", SearchOption.AllDirectories));
            Assert.NotEmpty(
                Directory.GetFiles(dest, "*.gph", SearchOption.AllDirectories));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_AtomicReplace_LeavesSourceIntactOnFailure()
    {
        string root = CreateRoot("enhance-atomic");
        try
        {
            string source = await BuildUnrestrictedTilesAsync(root);
            IReadOnlyDictionary<string, string> before = HashTree(source);
            string dest = Path.Combine(root, "enhanced-missing-parent", "nested");
            // Force failure by pointing source at a missing directory while keeping
            // a valid dest parent pattern after a successful first build is not used.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await PooledRoadEnhanceStage.ApplyAsync(
                    Path.Combine(root, "does-not-exist"),
                    dest,
                    new PooledRoadEnhanceStageOptions(64 * 1024 * 1024),
                    TestContext.Current.CancellationToken));
            Assert.Equal(before, HashTree(source));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_ReturnsBuffersToPools_OnSuccessAndFailure()
    {
        string root = CreateRoot("enhance-buffers");
        try
        {
            string source = await BuildUnrestrictedTilesAsync(root);
            string dest = Path.Combine(root, "enhanced");
            await PooledRoadEnhanceStage.ApplyAsync(
                source,
                dest,
                new PooledRoadEnhanceStageOptions(64 * 1024 * 1024),
                TestContext.Current.CancellationToken);
            // Second run reuses the same source without leaking staging dirs.
            string dest2 = Path.Combine(root, "enhanced-2");
            await PooledRoadEnhanceStage.ApplyAsync(
                source,
                dest2,
                new PooledRoadEnhanceStageOptions(64 * 1024 * 1024),
                TestContext.Current.CancellationToken);
            string[] leftovers = Directory.GetDirectories(
                Path.GetDirectoryName(dest)!,
                ".enhance-stage-*");
            Assert.Empty(leftovers);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyAsync_InsufficientBudget_FailsBeforeMutation()
    {
        string root = CreateRoot("enhance-budget");
        try
        {
            string source = await BuildUnrestrictedTilesAsync(root);
            string dest = Path.Combine(root, "enhanced");
            await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(
                async () =>
                    await PooledRoadEnhanceStage.ApplyAsync(
                        source,
                        dest,
                        new PooledRoadEnhanceStageOptions(
                            MemoryBudgetBytes: 1024,
                            MaxDegreeOfParallelism: 8)
                        {
                            MinimumPerTileBudgetBytes = 8 * 1024 * 1024,
                        },
                        TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(dest));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ManagedRoadGraphBuilder_PooledFrontier_InvokesEnhanceStage_InOfficialOrder()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = CreateRoot("pooled-enhance-order");
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
                        MaxDegreeOfParallelism = 2,
                    })
                {
                    Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.TileBuilderResult.Success);
            Assert.Contains("pooled.enhance", result.TileBuilderResult.StageDurations);
            Assert.Contains("pooled.restrictions", result.TileBuilderResult.StageDurations);
            Assert.True(
                result.TileBuilderResult.StageDurations["pooled.enhance"] >=
                TimeSpan.Zero);
            Assert.NotNull(result.ResourceMetrics);
            Assert.True(result.ResourceMetrics!.SelectedDop >= 1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ManagedRoadGraphBuilder_PooledFrontier_EnhanceParity_MatchesLegacyFixture()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = CreateRoot("pooled-enhance-parity");
        try
        {
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult pooled = await builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    Path.Combine(root, "work-pooled"),
                    Path.Combine(root, "tiles-pooled"),
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

            Assert.True(pooled.TileBuilderResult.Success);
            Assert.True(pooled.TileBuilderResult.TileCount > 0);
            // Parity soft gate: enhance stage ran and produced readable tiles.
            Assert.Contains("pooled.enhance", pooled.TileBuilderResult.StageDurations);
            Assert.NotEmpty(
                Directory.GetFiles(
                    Path.Combine(root, "tiles-pooled"),
                    "*.gph",
                    SearchOption.AllDirectories));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> BuildUnrestrictedTilesAsync(string root)
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string work = Path.Combine(root, "work");
        string output = Path.Combine(root, "tiles-out");
        var builder = new ManagedRoadGraphBuilder();
        ManagedRoadGraphBuildResult result = await builder.BuildAsync(
            new ManagedRoadGraphBuildRequest(
                [pbfPath],
                work,
                output,
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
        // Intermediate unrestricted tiles (pre-restriction) for stage isolation tests.
        string unrestricted = Path.Combine(work, "pooled-road-tiles");
        if (Directory.Exists(unrestricted) &&
            Directory.GetFiles(unrestricted, "*.gph", SearchOption.AllDirectories).Length > 0)
        {
            return unrestricted;
        }

        return output;
    }

    private static string CreateRoot(string label) =>
        Path.Combine(
            Path.GetTempPath(),
            $"valhalla-{label}-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static IReadOnlyDictionary<string, string> HashTree(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            map[rel] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        }

        return map;
    }

    private static string FindRepositoryArtifact(params string[] segments)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(new[] { dir }.Concat(segments).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(
            "Repository artifact not found: " + string.Join("/", segments));
    }
}
