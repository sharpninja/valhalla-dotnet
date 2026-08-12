using SharpNinja.Valhalla.Generation.BikeShare;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Tests.Pbf;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.BikeShare;

public sealed class ManagedBikeShareGenerationHostileTests
{
    [Fact]
    public async Task CancelledBuild_LeavesNoPublishedOutput()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();
        string input = Path.Combine(scratch, "input");
        string output = Path.Combine(scratch, "output");
        ManagedBikeShareGenerationTests.CopyDirectory(
            Path.Combine(
                ManagedBikeShareGenerationTests.FixtureRoot(),
                "OfficialValhalla383ParisBase"),
            input);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            var builder = new ManagedBikeShareTileBuilder();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => builder.BuildAsync(
                    ManagedBikeShareGenerationTests.Request(
                        input,
                        BikeSharePbfPath(),
                        Path.Combine(scratch, "work"),
                        output,
                        maxDegreeOfParallelism: 4),
                    cancellation.Token).AsTask());

            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task MemoryBudgetExhaustion_LeavesNoPublishedOutput()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string input = CopyGraphInput(scratch);
            string output = Path.Combine(scratch, "output");
            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    BikeSharePbfPath(),
                    Path.Combine(scratch, "work"),
                    output,
                    maxDegreeOfParallelism: 4,
                    memoryBudgetBytes: 1),
                BikeShareTileBuildFailureCode.ResourceExhausted);

            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task ScratchBudgetExhaustion_LeavesNoPublishedOutput()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string input = CopyGraphInput(scratch);
            string output = Path.Combine(scratch, "output");
            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    BikeSharePbfPath(),
                    Path.Combine(scratch, "work"),
                    output,
                    maxDegreeOfParallelism: 4,
                    scratchDiskBudgetBytes: 1),
                BikeShareTileBuildFailureCode.ResourceExhausted);

            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task MissingPbf_FailsBeforeMutation()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string input = CopyGraphInput(scratch);
            string work = Path.Combine(scratch, "work");
            string output = Path.Combine(scratch, "output");
            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    Path.Combine(scratch, "missing.osm.pbf"),
                    work,
                    output,
                    maxDegreeOfParallelism: 1),
                BikeShareTileBuildFailureCode.MissingInput);

            Assert.False(Directory.Exists(work));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task MalformedPbf_FailsSafely()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string input = CopyGraphInput(scratch);
            string malformed = Path.Combine(scratch, "malformed.osm.pbf");
            string output = Path.Combine(scratch, "output");
            await File.WriteAllBytesAsync(
                malformed,
                [0x7F, 0x00, 0x01, 0x02],
                TestContext.Current.CancellationToken);

            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    malformed,
                    Path.Combine(scratch, "work"),
                    output,
                    maxDegreeOfParallelism: 1),
                BikeShareTileBuildFailureCode.MalformedFeed);

            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task ValidPbfWithoutStations_ReturnsNoStations()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string input = CopyGraphInput(scratch);
            string pbf = Path.Combine(scratch, "no-stations.osm.pbf");
            string output = Path.Combine(scratch, "output");
            await File.WriteAllBytesAsync(
                pbf,
                TestOsmPbfFixtureBuilder.Create(OsmPbfCompressionKind.Raw),
                TestContext.Current.CancellationToken);

            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    pbf,
                    Path.Combine(scratch, "work"),
                    output,
                    maxDegreeOfParallelism: 1),
                BikeShareTileBuildFailureCode.NoStations);

            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task MissingGraphTileDirectory_FailsBeforeMutation()
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string work = Path.Combine(scratch, "work");
            string output = Path.Combine(scratch, "output");
            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    Path.Combine(scratch, "missing-graph"),
                    BikeSharePbfPath(),
                    work,
                    output,
                    maxDegreeOfParallelism: 1),
                BikeShareTileBuildFailureCode.GraphTileNotFound);

            Assert.False(Directory.Exists(work));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    [Theory]
    [InlineData(0, 64 * 1024 * 1024, 256 * 1024 * 1024)]
    [InlineData(1, 0, 256 * 1024 * 1024)]
    [InlineData(1, 64 * 1024 * 1024, 0)]
    public async Task InvalidResourceOptions_FailBeforeMutation(
        int maxDegreeOfParallelism,
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes)
    {
        string scratch = ManagedBikeShareGenerationTests.NewScratch();

        try
        {
            string input = CopyGraphInput(scratch);
            string work = Path.Combine(scratch, "work");
            string output = Path.Combine(scratch, "output");
            await AssertFailureAsync(
                ManagedBikeShareGenerationTests.Request(
                    input,
                    BikeSharePbfPath(),
                    work,
                    output,
                    maxDegreeOfParallelism,
                    memoryBudgetBytes,
                    scratchDiskBudgetBytes),
                BikeShareTileBuildFailureCode.InvalidConfiguration);

            Assert.False(Directory.Exists(work));
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedBikeShareGenerationTests.DeleteScratch(scratch);
        }
    }

    private static async Task AssertFailureAsync(
        BikeShareTileBuildRequest request,
        BikeShareTileBuildFailureCode expectedCode)
    {
        var builder = new ManagedBikeShareTileBuilder();
        BikeShareTileBuildException exception =
            await Assert.ThrowsAsync<BikeShareTileBuildException>(
                () => builder.BuildAsync(
                    request,
                    TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(expectedCode, exception.Code);
    }

    private static string CopyGraphInput(string scratch)
    {
        string input = Path.Combine(scratch, "input");
        ManagedBikeShareGenerationTests.CopyDirectory(
            Path.Combine(
                ManagedBikeShareGenerationTests.FixtureRoot(),
                "OfficialValhalla383ParisBase"),
            input);
        return input;
    }

    private static string BikeSharePbfPath()
        => Path.Combine(
            ManagedBikeShareGenerationTests.FixtureRoot(),
            "ParisBss",
            "paris_bss.osm.pbf");
}
