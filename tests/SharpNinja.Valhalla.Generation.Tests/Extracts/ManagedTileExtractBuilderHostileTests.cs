using SharpNinja.Valhalla.Generation.Extracts;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Extracts;

public sealed class ManagedTileExtractBuilderHostileTests
{
    [Theory]
    [InlineData("North-America")]
    [InlineData("../nashville")]
    [InlineData("-nashville")]
    [InlineData("nashville-")]
    public async Task InvalidRegion_IsRejectedBeforeOutput(string regionId)
    {
        string root = CreateRoot();
        try
        {
            TileExtractBuildRequest request = CreateRequest(
                FindOfficialTileDirectory(),
                Path.Combine(root, "invalid-region.tar"),
                regionId);
            var builder = new ManagedTileExtractBuilder();

            TileExtractBuildException exception =
                await Assert.ThrowsAsync<TileExtractBuildException>(
                    async () => await builder.BuildAsync(
                        request,
                        TestContext.Current.CancellationToken));

            Assert.Equal(TileExtractFailureCode.InvalidConfiguration, exception.Code);
            Assert.False(File.Exists(request.OutputPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OutputInsideTileDirectory_IsRejectedBeforeMutation()
    {
        string tileDirectory = FindOfficialTileDirectory();
        string outputPath = Path.Combine(tileDirectory, "unsafe-extract.tar");
        var builder = new ManagedTileExtractBuilder();

        TileExtractBuildException exception =
            await Assert.ThrowsAsync<TileExtractBuildException>(
                async () => await builder.BuildAsync(
                    CreateRequest(tileDirectory, outputPath),
                    TestContext.Current.CancellationToken));

        Assert.Equal(TileExtractFailureCode.UnsafePath, exception.Code);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task TruncatedTile_IsRejectedAndLeavesNoOutput()
    {
        string root = CreateRoot();
        try
        {
            string tiles = Path.Combine(root, "tiles");
            CopyGraphTiles(FindOfficialTileDirectory(), tiles);
            string truncated = Directory.EnumerateFiles(
                    tiles,
                    "*.gph",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .First();
            await using (FileStream stream = new(
                             truncated,
                             FileMode.Open,
                             FileAccess.Write,
                             FileShare.None))
            {
                stream.SetLength(16);
            }

            string outputPath = Path.Combine(root, "truncated.tar");
            var builder = new ManagedTileExtractBuilder();
            TileExtractBuildException exception =
                await Assert.ThrowsAsync<TileExtractBuildException>(
                    async () => await builder.BuildAsync(
                        CreateRequest(tiles, outputPath),
                        TestContext.Current.CancellationToken));

            Assert.Equal(TileExtractFailureCode.InvalidGraphTile, exception.Code);
            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CancelledBuild_LeavesNoPublishedOrTemporaryOutput()
    {
        string root = CreateRoot();
        try
        {
            string outputPath = Path.Combine(root, "cancelled.tar");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var builder = new ManagedTileExtractBuilder();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await builder.BuildAsync(
                    CreateRequest(FindOfficialTileDirectory(), outputPath),
                    cancellation.Token));

            Assert.False(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ConcurrentPublication_AllowsExactlyOneImmutableWriter()
    {
        string root = CreateRoot();
        try
        {
            string outputPath = Path.Combine(root, "concurrent.tar");
            TileExtractBuildRequest request = CreateRequest(
                FindOfficialTileDirectory(),
                outputPath);
            var builder = new ManagedTileExtractBuilder();
            Task<TileExtractBuildResult>[] tasks =
            [
                builder.BuildAsync(request, TestContext.Current.CancellationToken).AsTask(),
                builder.BuildAsync(request, TestContext.Current.CancellationToken).AsTask(),
            ];

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (TileExtractBuildException)
            {
            }

            Assert.Equal(1, tasks.Count(task => task.IsCompletedSuccessfully));
            Task<TileExtractBuildResult> rejected =
                Assert.Single(tasks, task => task.IsFaulted);
            TileExtractBuildException exception = Assert.IsType<TileExtractBuildException>(
                rejected.Exception!.InnerException);
            Assert.Equal(TileExtractFailureCode.OutputAlreadyExists, exception.Code);
            Assert.True(File.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static TileExtractBuildRequest CreateRequest(
        string tileDirectory,
        string outputPath,
        string regionId = "monaco") =>
        new(
            tileDirectory,
            outputPath,
            regionId,
            DatasetId: 17,
            BuildId: 20260808,
            DeterministicOutput: true);

    private static string FindOfficialTileDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string[] parts =
        [
            "tests",
            "SharpNinja.Valhalla.Generation.Tests",
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles",
        ];
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-extract-hostile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CopyGraphTiles(string sourceRoot, string destinationRoot)
    {
        foreach (string sourcePath in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.gph",
                     SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            string destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
