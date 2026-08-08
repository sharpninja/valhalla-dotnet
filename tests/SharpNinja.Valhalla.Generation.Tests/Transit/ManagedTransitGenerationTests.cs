using System.IO.Compression;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Transit;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Transit;

public sealed class ManagedTransitGenerationTests
{
    [Fact]
    public async Task DirectoryAndZipBuilds_AreDeterministic()
    {
        string feedPath = FixtureFeedPath();
        string scratch = NewScratch();
        string zipPath = Path.Combine(scratch, "MonacoBus.zip");

        try
        {
            ZipFile.CreateFromDirectory(feedPath, zipPath);
            TransitTileBuildResult directory = await BuildAsync(
                feedPath,
                Path.Combine(scratch, "directory"),
                maxDegreeOfParallelism: 1);
            TransitTileBuildResult archive = await BuildAsync(
                zipPath,
                Path.Combine(scratch, "archive"),
                maxDegreeOfParallelism: 4);
            TransitTileBuildResult repeated = await BuildAsync(
                feedPath,
                Path.Combine(scratch, "repeated"),
                maxDegreeOfParallelism: 2);

            Assert.Equal(directory.OutputSha256, archive.OutputSha256);
            Assert.Equal(directory.OutputSha256, repeated.OutputSha256);
            Assert.Single(directory.OutputSha256);

            string relativePath = GraphTile.FileSuffix(new GraphId(769709, 3, 0));
            byte[] bytes = await File.ReadAllBytesAsync(
                Path.Combine(directory.OutputDirectory, relativePath),
                TestContext.Current.CancellationToken);
            GraphTile tile = GraphTile.Create(new GraphId(769709, 3, 0), bytes);
            Assert.Equal(42ul, tile.Header().DatasetId());
            Assert.Equal((ushort)7, tile.Header().BuildId());
            Assert.Equal(4602u, tile.Header().DateCreated());
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task ExistingOutput_IsPreservedWhenPublicationFails()
    {
        string scratch = NewScratch();
        string output = Path.Combine(scratch, "output");
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "sentinel.txt");
        await File.WriteAllTextAsync(
            sentinel,
            "published-generation",
            TestContext.Current.CancellationToken);

        try
        {
            var builder = new ManagedTransitTileBuilder();
            TransitTileBuildException exception = await Assert.ThrowsAsync<TransitTileBuildException>(
                () => builder.BuildAsync(
                    Request(
                        FixtureFeedPath(),
                        Path.Combine(scratch, "work"),
                        output,
                        maxDegreeOfParallelism: 1),
                    TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(TransitTileBuildFailureCode.InvalidConfiguration, exception.Code);
            Assert.Equal(
                "published-generation",
                await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
            Assert.Single(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static async Task<TransitTileBuildResult> BuildAsync(
        string feedPath,
        string root,
        int maxDegreeOfParallelism)
    {
        var builder = new ManagedTransitTileBuilder();
        return await builder.BuildAsync(
            Request(
                feedPath,
                Path.Combine(root, "work"),
                Path.Combine(root, "output"),
                maxDegreeOfParallelism),
            TestContext.Current.CancellationToken);
    }

    internal static TransitTileBuildRequest Request(
        string feedPath,
        string work,
        string output,
        int maxDegreeOfParallelism,
        long memoryBudgetBytes = 64 * 1024 * 1024,
        long scratchDiskBudgetBytes = 256 * 1024 * 1024)
        => new(
            [feedPath],
            work,
            output,
            TimeZoneDatabasePath: null,
            new TransitTileBuildOptions(
                maxDegreeOfParallelism,
                memoryBudgetBytes,
                scratchDiskBudgetBytes,
                new DateOnly(2026, 8, 8),
                DatasetId: 42,
                BuildId: 7,
                DeterministicOutput: true));

    internal static string FixtureFeedPath()
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transit", "MonacoBus");

    internal static string NewScratch()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "valhalla-transit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void DeleteScratch(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
