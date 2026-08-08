using System.Security.Cryptography;
using System.Text;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.HistoricalSpeeds;
using Xunit;
using static SharpNinja.Valhalla.Generation.Tests.HistoricalSpeeds.ManagedHistoricalSpeedGenerationTests;

namespace SharpNinja.Valhalla.Generation.Tests.HistoricalSpeeds;

public sealed class ManagedHistoricalSpeedGenerationHostileTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-graph-id,50,30,")]
    [InlineData("{edge},not-a-speed,30,")]
    [InlineData("{edge},50,not-a-speed,")]
    [InlineData("{edge},50,30,not-base64")]
    [InlineData("{edge},50")]
    public async Task MalformedInputMatrix_FailsBeforeTileMutation(string row)
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string before = await HashTilesAsync(
            fixture.GraphDirectory,
            TestContext.Current.CancellationToken);
        row = row.Replace(
            "{edge}",
            fixture.FirstEdgeId.ToString(),
            StringComparison.Ordinal);
        fixture.WriteTrafficFile(fixture.FirstEdgeId.TileBase(), row);

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.InvalidTrafficRecord,
            exception.FailureCode);
        Assert.Equal(
            before,
            await HashTilesAsync(
                fixture.GraphDirectory,
                TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            fixture.Root,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateGraphId_FailsBeforeTileMutation()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string before = await HashTilesAsync(
            fixture.GraphDirectory,
            TestContext.Current.CancellationToken);
        string first = CreateRow(fixture.FirstEdgeId, 60, 30, 25.0f);
        string second = CreateRow(fixture.FirstEdgeId, 61, 31, 26.0f);
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            first,
            second);

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.DuplicateGraphId,
            exception.FailureCode);
        Assert.Equal(
            before,
            await HashTilesAsync(
                fixture.GraphDirectory,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrossTileGraphId_FailsBeforeTileMutation()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        GraphId wrongTileEdge = new(
            fixture.FirstEdgeId.Tileid() + 1,
            fixture.FirstEdgeId.Level(),
            0);
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(wrongTileEdge, 60, 30, 25.0f));

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.TileIdentityMismatch,
            exception.FailureCode);
    }

    [Fact]
    public async Task EdgeOutsideTile_FailsBeforeTileMutation()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        GraphId invalidEdge = new(
            fixture.FirstEdgeId.Tileid(),
            fixture.FirstEdgeId.Level(),
            GraphConstants.MaxGraphId);
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(invalidEdge, 60, 30, 25.0f));

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.EdgeNotFound,
            exception.FailureCode);
    }

    [Fact]
    public async Task MissingGraphTile_FailsBeforeMutation()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        GraphId missing = new(
            fixture.FirstEdgeId.Tileid() + 1,
            fixture.FirstEdgeId.Level(),
            0);
        fixture.WriteTrafficFile(
            missing.TileBase(),
            CreateRow(missing, 60, 30, 25.0f));

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.GraphTileNotFound,
            exception.FailureCode);
    }

    [Fact]
    public async Task MemoryBudgetExhaustion_FailsBeforeMutation()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string before = await HashTilesAsync(
            fixture.GraphDirectory,
            TestContext.Current.CancellationToken);
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(fixture.FirstEdgeId, 60, 30, 25.0f));

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(memoryBudgetBytes: 1),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded,
            exception.FailureCode);
        Assert.Equal(
            before,
            await HashTilesAsync(
                fixture.GraphDirectory,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScratchBudgetExhaustion_DoesNotReplaceTile()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string before = await HashTilesAsync(
            fixture.GraphDirectory,
            TestContext.Current.CancellationToken);
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(fixture.FirstEdgeId, 60, 30, 25.0f));

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(scratchDiskBudgetBytes: 1),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.ScratchDiskBudgetExceeded,
            exception.FailureCode);
        Assert.Equal(
            before,
            await HashTilesAsync(
                fixture.GraphDirectory,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NestedDirectoryReparsePoint_FailsClosed()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string outsideDirectory = Path.Combine(fixture.Root, "outside");
        string nestedDirectory = Path.Combine(outsideDirectory, "003");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllLinesAsync(
            Path.Combine(nestedDirectory, "016.csv"),
            [CreateRow(fixture.FirstEdgeId, 60, 30, 25.0f)],
            TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(
            Path.Combine(fixture.InputDirectory, "0"),
            outsideDirectory);

        HistoricalSpeedDatasetBuildException exception =
            await Assert.ThrowsAsync<HistoricalSpeedDatasetBuildException>(
                () => CreateBuilder().BuildAsync(
                    fixture.CreateRequest(),
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
            exception.FailureCode);
        Assert.Equal(
            "Historical-speed generation does not follow reparse points.",
            exception.Message);
    }

    [Fact]
    public async Task Cancellation_DoesNotReplaceTile()
    {
        using HistoricalSpeedFixture fixture = HistoricalSpeedFixture.Create();
        string before = await HashTilesAsync(
            fixture.GraphDirectory,
            TestContext.Current.CancellationToken);
        fixture.WriteTrafficFile(
            fixture.FirstEdgeId.TileBase(),
            CreateRow(fixture.FirstEdgeId, 60, 30, 25.0f));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateBuilder().BuildAsync(
                fixture.CreateRequest(),
                cancellation.Token).AsTask());

        Assert.Equal(
            before,
            await HashTilesAsync(
                fixture.GraphDirectory,
                TestContext.Current.CancellationToken));
    }

    private static ManagedHistoricalSpeedDataBuilder CreateBuilder() => new();

    private static string CreateRow(
        GraphId edgeId,
        byte freeFlowSpeed,
        byte constrainedFlowSpeed,
        float predictedSpeed)
    {
        var buckets = new float[PredictedSpeedConstants.BucketsPerWeek];
        Array.Fill(buckets, predictedSpeed);
        short[] coefficients =
            PredictedSpeedCompression.CompressSpeedBuckets(buckets);
        return $"{edgeId},{freeFlowSpeed},{constrainedFlowSpeed}," +
            PredictedSpeedCompression.EncodeCompressedSpeeds(coefficients);
    }

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
            string relative = Path
                .GetRelativePath(root, path)
                .Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(
                await File.ReadAllBytesAsync(path, cancellationToken));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
