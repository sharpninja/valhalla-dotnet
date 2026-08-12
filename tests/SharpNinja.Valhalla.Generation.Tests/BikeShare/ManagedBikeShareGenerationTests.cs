using SharpNinja.Valhalla.Generation.BikeShare;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.BikeShare;

public sealed class ManagedBikeShareGenerationTests
{
    [Fact]
    public async Task RepeatedBuilds_AreDeterministicAcrossConcurrencyValues()
    {
        string fixtureRoot = FixtureRoot();
        string scratch = NewScratch();

        try
        {
            BikeShareTileBuildResult first = await BuildAsync(
                fixtureRoot,
                Path.Combine(scratch, "first"),
                maxDegreeOfParallelism: 1);
            BikeShareTileBuildResult second = await BuildAsync(
                fixtureRoot,
                Path.Combine(scratch, "second"),
                maxDegreeOfParallelism: 8);
            BikeShareTileBuildResult third = await BuildAsync(
                fixtureRoot,
                Path.Combine(scratch, "third"),
                maxDegreeOfParallelism: 4);

            Assert.Equal(first.OutputSha256, second.OutputSha256);
            Assert.Equal(first.OutputSha256, third.OutputSha256);
            Assert.Single(first.OutputSha256);
            Assert.Equal(46, first.StationCount);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task ExistingOutput_IsPreservedWhenPublicationIsRejected()
    {
        string scratch = NewScratch();
        string input = Path.Combine(scratch, "input");
        string output = Path.Combine(scratch, "output");
        CopyDirectory(
            Path.Combine(FixtureRoot(), "OfficialValhalla383ParisBase"),
            input);
        Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "sentinel.txt");
        await File.WriteAllTextAsync(
            sentinel,
            "published-generation",
            TestContext.Current.CancellationToken);

        try
        {
            var builder = new ManagedBikeShareTileBuilder();
            BikeShareTileBuildException exception =
                await Assert.ThrowsAsync<BikeShareTileBuildException>(
                    () => builder.BuildAsync(
                        Request(
                            input,
                            Path.Combine(FixtureRoot(), "ParisBss", "paris_bss.osm.pbf"),
                            Path.Combine(scratch, "work"),
                            output,
                            maxDegreeOfParallelism: 1),
                        TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(BikeShareTileBuildFailureCode.InvalidConfiguration, exception.Code);
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

    internal static BikeShareTileBuildRequest Request(
        string graphTileDirectory,
        string pbfPath,
        string work,
        string output,
        int maxDegreeOfParallelism,
        long memoryBudgetBytes = 64 * 1024 * 1024,
        long scratchDiskBudgetBytes = 256 * 1024 * 1024)
        => new(
            graphTileDirectory,
            [pbfPath],
            work,
            output,
            new BikeShareTileBuildOptions(
                maxDegreeOfParallelism,
                memoryBudgetBytes,
                scratchDiskBudgetBytes,
                DeterministicOutput: true));

    internal static string FixtureRoot()
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "BikeShare");

    internal static string NewScratch()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "valhalla-bikeshare-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string sourcePath in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, sourcePath);
            string destinationPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    internal static void DeleteScratch(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<BikeShareTileBuildResult> BuildAsync(
        string fixtureRoot,
        string root,
        int maxDegreeOfParallelism)
    {
        string input = Path.Combine(root, "input");
        CopyDirectory(
            Path.Combine(fixtureRoot, "OfficialValhalla383ParisBase"),
            input);
        var builder = new ManagedBikeShareTileBuilder();
        return await builder.BuildAsync(
            Request(
                input,
                Path.Combine(fixtureRoot, "ParisBss", "paris_bss.osm.pbf"),
                Path.Combine(root, "work"),
                Path.Combine(root, "output"),
                maxDegreeOfParallelism),
            TestContext.Current.CancellationToken);
    }
}
