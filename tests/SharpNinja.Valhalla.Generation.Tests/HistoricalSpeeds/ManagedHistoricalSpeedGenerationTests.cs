using System.Security.Cryptography;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Differential;
using SharpNinja.Valhalla.Generation.HistoricalSpeeds;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.HistoricalSpeeds;

public sealed class ManagedHistoricalSpeedGenerationTests
{
    [Fact]
    public async Task OfficialCsvSemantics_AreAppliedToGraphTiles()
    {
        using var fixture = HistoricalSpeedFixture.Create();
        GraphId edgeId = fixture.FirstEdgeId;
        short[] coefficients = ConstantProfile(36.0f);
        fixture.WriteTrafficFile(
            edgeId.TileBase(),
            $"{edgeId},72,41,{PredictedSpeedCompression.EncodeCompressedSpeeds(coefficients)}");

        HistoricalSpeedDatasetBuildResult result =
            await CreateBuilder().BuildAsync(
                fixture.CreateRequest(),
                TestContext.Current.CancellationToken);

        GraphTile tile = LoadTile(fixture.GraphDirectory, edgeId.TileBase());
        DirectedEdge edge = tile.DirectedEdge((int)edgeId.Id());
        Assert.Equal(72u, edge.FreeFlowSpeed);
        Assert.Equal(41u, edge.ConstrainedFlowSpeed);
        Assert.True(edge.HasPredictedSpeed);
        Assert.Equal(36.0f, tile.PredictedSpeed(edgeId.Id(), 0), 0.5f);
        Assert.Equal(1, result.TileCount);
        Assert.Equal(1, result.UpdatedEdgeCount);
        Assert.Equal(1, result.PredictedProfileCount);
        Assert.Equal(1, result.FreeFlowSpeedCount);
        Assert.Equal(1, result.ConstrainedFlowSpeedCount);
        Assert.Matches("^[A-F0-9]{64}$", result.OutputTreeSha256);
    }

    [Fact]
    public async Task RepeatedBuilds_ProduceIdenticalOutputTreeHash()
    {
        using var first = HistoricalSpeedFixture.Create();
        using var second = HistoricalSpeedFixture.Create();
        string row = CreateRow(first.FirstEdgeId, 68, 37, 28.0f);
        first.WriteTrafficFile(first.FirstEdgeId.TileBase(), row);
        second.WriteTrafficFile(second.FirstEdgeId.TileBase(), row);

        HistoricalSpeedDatasetBuildResult firstResult =
            await CreateBuilder().BuildAsync(
                first.CreateRequest(),
                TestContext.Current.CancellationToken);
        HistoricalSpeedDatasetBuildResult secondResult =
            await CreateBuilder().BuildAsync(
                second.CreateRequest(),
                TestContext.Current.CancellationToken);

        Assert.Equal(firstResult.OutputTreeSha256, secondResult.OutputTreeSha256);
        Assert.Equal(
            await HashTilesAsync(
                first.GraphDirectory,
                TestContext.Current.CancellationToken),
            await HashTilesAsync(
                second.GraphDirectory,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManagedIngestion_MatchesPinnedOfficial383Fixture()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string oracleRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383HistoricalSpeeds");
        string inputPath = Path.Combine(oracleRoot, "input", "0", "003", "016.csv");
        string[] rows = await File.ReadAllLinesAsync(
            inputPath,
            TestContext.Current.CancellationToken);
        fixture.WriteTrafficFile(fixture.FirstEdgeId.TileBase(), rows);

        await CreateBuilder().BuildAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);

        var tileId = new GraphId("0/3016/0");
        GraphTile managed = LoadTile(fixture.GraphDirectory, tileId);
        GraphTile official = GraphTile.Create(
            tileId,
            await File.ReadAllBytesAsync(
                Path.Combine(oracleRoot, "tiles", "0", "003", "016.gph"),
                TestContext.Current.CancellationToken));
        DirectedEdge managedEdge = managed.DirectedEdge(0);
        DirectedEdge officialEdge = official.DirectedEdge(0);
        Assert.Equal(officialEdge.FreeFlowSpeed, managedEdge.FreeFlowSpeed);
        Assert.Equal(officialEdge.ConstrainedFlowSpeed, managedEdge.ConstrainedFlowSpeed);
        Assert.Equal(officialEdge.HasPredictedSpeed, managedEdge.HasPredictedSpeed);
        foreach (uint secondOfWeek in new uint[] { 0, 86400, 302400, 604799 })
        {
            Assert.Equal(
                official.PredictedSpeed(0, secondOfWeek),
                managed.PredictedSpeed(0, secondOfWeek));
        }

        using JsonDocument manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(oracleRoot, "manifest.json"),
                TestContext.Current.CancellationToken));
        JsonElement root = manifest.RootElement;
        Assert.Equal(
            "a60c7cbfc83e073f50887cd27e0109d02e6b64e5",
            root.GetProperty("upstreamCommit").GetString());
        Assert.Equal(
            "sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f",
            root.GetProperty("containerDigest").GetString());
        Assert.Equal(
            root.GetProperty("inputSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                inputPath,
                TestContext.Current.CancellationToken))));
        Assert.Equal(
            root.GetProperty("outputTileSha256").GetString(),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Combine(oracleRoot, "tiles", "0", "003", "016.gph"),
                TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task StageReceipt_AccountsForAllTileIoAndScratch()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(fixture.FirstEdgeId, 65, 35, 31.0f));
        string inputPath = Path.Combine(
            fixture.InputDirectory,
            GraphTile.FileSuffix(fixture.FirstEdgeId.TileBase()));
        inputPath = Path.ChangeExtension(inputPath, ".csv");
        long inputBytes = new FileInfo(inputPath).Length;
        string targetPath = Path.Combine(
            fixture.GraphDirectory,
            GraphTile.FileSuffix(fixture.FirstEdgeId.TileBase()));
        long originalTargetBytes = new FileInfo(targetPath).Length;

        HistoricalSpeedDatasetBuildResult result = await CreateBuilder().BuildAsync(
            fixture.CreateRequest(),
            TestContext.Current.CancellationToken);

        string[] graphPaths = Directory
            .EnumerateFiles(fixture.GraphDirectory, "*.gph", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        long graphBytes = graphPaths.Sum(path => new FileInfo(path).Length);
        long targetBytes = new FileInfo(targetPath).Length;
        long treeBytes = Directory
            .EnumerateFiles(fixture.GraphDirectory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        long expectedRead =
            inputBytes +
            originalTargetBytes +
            (graphBytes * 2) +
            targetBytes +
            treeBytes;
        long expectedWritten = targetBytes + (graphBytes * 2);

        Assert.Equal(expectedRead, result.BytesRead);
        Assert.Equal(expectedWritten, result.BytesWritten);
        Assert.Equal(
            graphPaths.Max(path => new FileInfo(path).Length),
            result.ScratchDiskHighWaterBytes);
    }

    [Fact]
    public async Task StageExecutor_UsesConfiguredInputAndReportsReceipt()
    {
        using var fixture = HistoricalSpeedFixture.Create();
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(fixture.FirstEdgeId, 65, 35, 31.0f));
        var executor = new ManagedHistoricalSpeedStageExecutor(CreateBuilder());
        using var resources = new ValhallaGenerationResourceBudget(
            128 * 1024 * 1024,
            256 * 1024 * 1024,
            4);
        ValhallaGenerationBuildRequest request =
            fixture.CreateGenerationRequest();
        var context = new ValhallaGenerationStageContext(
            request,
            "historical-speed-stage",
            fixture.GraphDirectory,
            resources);

        ValhallaGenerationStageResult result = await executor.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ValhallaGenerationStage.ApplyPredictedSpeeds, executor.Stage);
        Assert.Equal(1, result.RecordsProcessed);
        Assert.True(result.BytesRead > 0);
        Assert.True(result.BytesWritten > 0);
        Assert.Contains("graph-tree", result.OutputHashes.Keys);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task MissingInput_StageIsDeterministicNoOp()
    {
        using var fixture = HistoricalSpeedFixture.Create();
        var executor = new ManagedHistoricalSpeedStageExecutor(CreateBuilder());
        using var resources = new ValhallaGenerationResourceBudget(
            128 * 1024 * 1024,
            256 * 1024 * 1024,
            4);
        ValhallaGenerationBuildRequest request =
            fixture.CreateGenerationRequest(includeHistoricalSpeeds: false);
        var context = new ValhallaGenerationStageContext(
            request,
            "historical-speed-skipped",
            fixture.GraphDirectory,
            resources);

        ValhallaGenerationStageResult result = await executor.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal("historical-speeds-skipped", result.OutputIdentity);
        Assert.Equal(0, result.RecordsProcessed);
        Assert.Empty(result.Failures);
    }

    private static ManagedHistoricalSpeedDataBuilder CreateBuilder() => new();

    private static string CreateRow(
        GraphId edgeId,
        byte freeFlowSpeed,
        byte constrainedFlowSpeed,
        float predictedSpeed) =>
        $"{edgeId},{freeFlowSpeed},{constrainedFlowSpeed}," +
        PredictedSpeedCompression.EncodeCompressedSpeeds(
            ConstantProfile(predictedSpeed));

    private static short[] ConstantProfile(float speed)
    {
        var buckets = new float[PredictedSpeedConstants.BucketsPerWeek];
        Array.Fill(buckets, speed);
        return PredictedSpeedCompression.CompressSpeedBuckets(buckets);
    }

    private static GraphTile LoadTile(string graphDirectory, GraphId tileId) =>
        GraphTile.Create(graphDirectory, tileId)
        ?? throw new InvalidDataException($"Could not load graph tile {tileId}.");

    private static async Task<string> HashTilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in Directory
                     .EnumerateFiles(root, "*.gph", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(
                await File.ReadAllBytesAsync(path, cancellationToken));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal sealed class HistoricalSpeedFixture : IDisposable
    {
        private HistoricalSpeedFixture(
            string root,
            string graphDirectory,
            string inputDirectory,
            GraphId firstEdgeId)
        {
            Root = root;
            GraphDirectory = graphDirectory;
            InputDirectory = inputDirectory;
            FirstEdgeId = firstEdgeId;
        }

        public string Root { get; }

        public string GraphDirectory { get; }

        public string InputDirectory { get; }

        public GraphId FirstEdgeId { get; }

        public static HistoricalSpeedFixture Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "valhalla-historical-speed-" + Guid.NewGuid().ToString("N"));
            string graphDirectory = Path.Combine(root, "graph");
            string inputDirectory = Path.Combine(root, "traffic");
            Directory.CreateDirectory(graphDirectory);
            Directory.CreateDirectory(inputDirectory);
            CopyOfficialGraph(graphDirectory);
            string firstTilePath = Directory
                .EnumerateFiles(
                    graphDirectory,
                    "*.gph",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .First();
            GraphId tileId = GraphTile.GetTileId(firstTilePath);
            GraphTile tile = LoadTile(graphDirectory, tileId);
            Assert.True(tile.Header().Directededgecount() > 0);
            return new HistoricalSpeedFixture(
                root,
                graphDirectory,
                inputDirectory,
                new GraphId(tileId.Tileid(), tileId.Level(), 0));
        }

        public HistoricalSpeedDatasetBuildRequest CreateRequest(
            long memoryBudgetBytes = 128 * 1024 * 1024,
            long scratchDiskBudgetBytes = 256 * 1024 * 1024) =>
            new(
                GraphDirectory,
                InputDirectory,
                MaxDegreeOfParallelism: 4,
                memoryBudgetBytes,
                scratchDiskBudgetBytes,
                DeterministicOutput: true);

        public ValhallaGenerationBuildRequest CreateGenerationRequest(
            bool includeHistoricalSpeeds = true) =>
            new(
                [],
                new ValhallaGenerationInputSet(
                    null,
                    null,
                    null,
                    [],
                    [],
                    includeHistoricalSpeeds ? InputDirectory : null),
                Path.Combine(Root, "work"),
                Path.Combine(Root, "output"),
                new ValhallaGenerationBuildOptions(
                    ValhallaGenerationProfile.Full,
                    IntermediateStorageMode.Auto,
                    ResumePolicy.Disabled,
                    4,
                    128 * 1024 * 1024,
                    256 * 1024 * 1024,
                    DatasetId: 0,
                    BuildId: 0,
                    DeterministicOutput: true));

        public void WriteTrafficFile(GraphId tileId, params string[] rows)
        {
            string tilePath = Path.Combine(
                InputDirectory,
                GraphTile.FileSuffix(tileId));
            string csvPath = Path.ChangeExtension(tilePath, ".csv");
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            File.WriteAllLines(csvPath, rows);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void CopyOfficialGraph(string destination)
        {
            string source = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "Official",
                "Valhalla383Monaco",
                "tiles");
            foreach (string sourcePath in Directory.EnumerateFiles(
                         source,
                         "*",
                         SearchOption.AllDirectories))
            {
                string destinationPath = Path.Combine(
                    destination,
                    Path.GetRelativePath(source, sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath);
            }
        }
    }
}
