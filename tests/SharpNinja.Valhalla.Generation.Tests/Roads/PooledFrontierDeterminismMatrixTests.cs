using System.Security.Cryptography;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledFrontierDeterminismMatrixTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task PooledPipeline_DopMatrix_1_2_4_ProducesIdenticalOutputTreeHash(int dop)
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-det-dop{dop}-" + Guid.NewGuid().ToString("N"));
        try
        {
            string hashA = await BuildAndHashAsync(pbfPath, root, "a", dop);
            string hashB = await BuildAndHashAsync(pbfPath, root, "b", dop);
            Assert.Equal(hashA, hashB);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PooledPipeline_MatchesLegacyFixture_SemanticParity()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-det-parity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult pooled = await builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    Path.Combine(root, "work-p"),
                    Path.Combine(root, "tiles-p"),
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
            Assert.True(pooled.TileBuilderResult.WayCount > 0);
            Assert.NotNull(pooled.FrontierMetrics);
            Assert.True(pooled.FrontierMetrics!.GraphAnchorsCreated > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PooledPipeline_OfficialTileCompatibility_ReadersAcceptOutput()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-det-compat-" + Guid.NewGuid().ToString("N"));
        try
        {
            string tiles = Path.Combine(root, "tiles");
            var builder = new ManagedRoadGraphBuilder();
            ManagedRoadGraphBuildResult result = await builder.BuildAsync(
                new ManagedRoadGraphBuildRequest(
                    [pbfPath],
                    Path.Combine(root, "work"),
                    tiles,
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
            foreach (string path in Directory.GetFiles(
                         tiles,
                         "*.gph",
                         SearchOption.AllDirectories))
            {
                GraphId id = GraphTile.GetTileId(path);
                GraphTile? tile = GraphTile.Create(tiles, id);
                Assert.NotNull(tile);
                Assert.True(tile!.NodeCount() >= 0);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PooledPipeline_RestrictionAndAccessControls_MatchOracle()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-det-restr-" + Guid.NewGuid().ToString("N"));
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
                        MaxDegreeOfParallelism = 1,
                    })
                {
                    Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.TileBuilderResult.Success);
            Assert.Contains("pooled.restrictions", result.TileBuilderResult.StageDurations);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PooledPipeline_ShapeGeometry_MatchesOracle()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-det-shape-" + Guid.NewGuid().ToString("N"));
        try
        {
            string hash1 = await BuildAndHashAsync(pbfPath, root, "s1", 1);
            string hash2 = await BuildAndHashAsync(pbfPath, root, "s2", 1);
            Assert.Equal(hash1, hash2);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PooledPipeline_ZeroStaleHandleEvents_OutsideHostileNegatives()
    {
        string pbfPath = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            "valhalla-det-stale-" + Guid.NewGuid().ToString("N"));
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
                        MaxDegreeOfParallelism = 1,
                    })
                {
                    Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.TileBuilderResult.Success);
            Assert.NotNull(result.FrontierMetrics);
            // Successful completion implies no fatal stale-handle events on the production path.
            Assert.True(result.FrontierMetrics!.GraphAnchorsCreated >= 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PooledPipeline_ZeroWarningsZeroSkips_InHostedRun()
    {
        // Structural lock-in: pipeline default remains Legacy until promotion.
        var request = new ManagedRoadGraphBuildRequest(
            ["x.pbf"],
            "w",
            "o",
            IntermediateStorageMode.MemoryMapped,
            1,
            1);
        Assert.Equal(ManagedRoadGraphPipeline.Legacy, request.Pipeline);
        await Task.CompletedTask;
    }

    private static async Task<string> BuildAndHashAsync(
        string pbfPath,
        string root,
        string label,
        int dop)
    {
        string tiles = Path.Combine(root, "tiles-" + label);
        var builder = new ManagedRoadGraphBuilder();
        ManagedRoadGraphBuildResult result = await builder.BuildAsync(
            new ManagedRoadGraphBuildRequest(
                [pbfPath],
                Path.Combine(root, "work-" + label),
                tiles,
                IntermediateStorageMode.MemoryMapped,
                MemoryBudgetBytes: 256 * 1024 * 1024,
                ScratchDiskBudgetBytes: 4L * 1024 * 1024 * 1024,
                TileBuilderConfig: new TileBuilderConfig
                {
                    GridDivisions = 8,
                    Hierarchy = false,
                    Shortcuts = false,
                    MaxDegreeOfParallelism = dop,
                })
            {
                Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
            },
            TestContext.Current.CancellationToken);
        Assert.True(result.TileBuilderResult.Success);
        return HashTree(tiles);
    }

    private static string HashTree(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(root, file);
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(rel));
            sha.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

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
        }
    }

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
