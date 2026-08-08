using System.IO.Compression;
using SharpNinja.Valhalla.Generation.Transit;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Transit;

public sealed class TransitGenerationHostileTests
{
    [Fact]
    public async Task MalformedFeedMatrix_FailsSafely()
    {
        await AssertFailureAsync(
            feed => File.Delete(Path.Combine(feed, "stops.txt")),
            TransitTileBuildFailureCode.MissingRequiredFile);
        await AssertFailureAsync(
            feed => File.WriteAllText(
                Path.Combine(feed, "trips.txt"),
                "route_id,service_id,trip_id,trip_headsign,direction_id,block_id,shape_id,wheelchair_accessible,bikes_allowed" +
                Environment.NewLine +
                "MISSING,WKD,T1,Casino Square,0,B1,S1,1,1"),
            TransitTileBuildFailureCode.ReferentialIntegrity);
        await AssertFailureAsync(
            feed => File.WriteAllText(
                Path.Combine(feed, "routes.txt"),
                "route_id,agency_id,route_short_name,route_long_name,route_desc,route_type,route_color,route_text_color" +
                Environment.NewLine +
                "R1,A1,1,Unsupported,Unsupported route type,99,112233,FFFFFF"),
            TransitTileBuildFailureCode.UnsupportedFeed);
        await AssertFailureAsync(
            feed => File.WriteAllText(
                Path.Combine(feed, "stops.txt"),
                "stop_id,stop_name,stop_lat,stop_lon" +
                Environment.NewLine +
                "BROKEN,\"unterminated,43.7,7.4"),
            TransitTileBuildFailureCode.InvalidCsv);
    }

    [Fact]
    public async Task ExpandedFeedBeyondMemoryBudget_FailsBeforeGraphMutation()
    {
        string scratch = ManagedTransitGenerationTests.NewScratch();
        string feed = Path.Combine(scratch, "feed");
        CopyFeed(ManagedTransitGenerationTests.FixtureFeedPath(), feed);
        await File.AppendAllTextAsync(
            Path.Combine(feed, "agency.txt"),
            new string('x', (1024 * 1024) + 1),
            TestContext.Current.CancellationToken);
        string output = Path.Combine(scratch, "output");

        try
        {
            var builder = new ManagedTransitTileBuilder();
            TransitTileBuildException exception = await Assert.ThrowsAsync<TransitTileBuildException>(
                () => builder.BuildAsync(
                    ManagedTransitGenerationTests.Request(
                        feed,
                        Path.Combine(scratch, "work"),
                        output,
                        maxDegreeOfParallelism: 1,
                        memoryBudgetBytes: 1024 * 1024),
                    TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(TransitTileBuildFailureCode.ResourceExhausted, exception.Code);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task PreCancelledBuild_DoesNotCreateOutput()
    {
        string scratch = ManagedTransitGenerationTests.NewScratch();
        string output = Path.Combine(scratch, "output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var builder = new ManagedTransitTileBuilder();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => builder.BuildAsync(
                    ManagedTransitGenerationTests.Request(
                        ManagedTransitGenerationTests.FixtureFeedPath(),
                        Path.Combine(scratch, "work"),
                        output,
                        maxDegreeOfParallelism: 1),
                    cancellation.Token).AsTask());
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task NestedArchiveTables_AreRejectedAsMissingRatherThanTraversed()
    {
        string scratch = ManagedTransitGenerationTests.NewScratch();
        string archivePath = Path.Combine(scratch, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (string source in Directory.EnumerateFiles(
                         ManagedTransitGenerationTests.FixtureFeedPath(),
                         "*.txt"))
            {
                ZipArchiveEntry entry = archive.CreateEntry(
                    "../" + Path.GetFileName(source),
                    CompressionLevel.NoCompression);
                await using Stream destination = entry.Open();
                await using FileStream input = File.OpenRead(source);
                await input.CopyToAsync(destination, TestContext.Current.CancellationToken);
            }
        }

        try
        {
            var builder = new ManagedTransitTileBuilder();
            TransitTileBuildException exception = await Assert.ThrowsAsync<TransitTileBuildException>(
                () => builder.BuildAsync(
                    ManagedTransitGenerationTests.Request(
                        archivePath,
                        Path.Combine(scratch, "work"),
                        Path.Combine(scratch, "output"),
                        maxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(TransitTileBuildFailureCode.MissingRequiredFile, exception.Code);
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(scratch);
        }
    }

    private static async Task AssertFailureAsync(
        Action<string> mutate,
        TransitTileBuildFailureCode expectedCode)
    {
        string scratch = ManagedTransitGenerationTests.NewScratch();
        string feed = Path.Combine(scratch, "feed");
        CopyFeed(ManagedTransitGenerationTests.FixtureFeedPath(), feed);
        mutate(feed);
        string output = Path.Combine(scratch, "output");

        try
        {
            var builder = new ManagedTransitTileBuilder();
            TransitTileBuildException exception = await Assert.ThrowsAsync<TransitTileBuildException>(
                () => builder.BuildAsync(
                    ManagedTransitGenerationTests.Request(
                        feed,
                        Path.Combine(scratch, "work"),
                        output,
                        maxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(expectedCode, exception.Code);
            Assert.False(Directory.Exists(output));
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(scratch);
        }
    }

    private static void CopyFeed(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string sourcePath in Directory.EnumerateFiles(source, "*.txt"))
        {
            File.Copy(sourcePath, Path.Combine(destination, Path.GetFileName(sourcePath)));
        }
    }
}
