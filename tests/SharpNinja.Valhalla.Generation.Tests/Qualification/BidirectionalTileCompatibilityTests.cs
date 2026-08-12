using System.Security.Cryptography;
using System.Text.Json;
using SharpNinja.Valhalla.Generation.Qualification;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Qualification;

public sealed class BidirectionalTileCompatibilityTests
{
    private const string OfficialImage =
        "ghcr.io/valhalla/valhalla@sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f";

    [Fact]
    public async Task ManagedAndOfficialReaders_CrossReadTiles()
    {
        string sourcePbf = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string officialTiles = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles");
        string managedTiles = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-managed-monaco-{Guid.NewGuid():N}");

        Assert.True(File.Exists(sourcePbf), $"Missing Monaco PBF: {sourcePbf}");
        Assert.True(Directory.Exists(officialTiles), $"Missing official 3.8.3 fixture: {officialTiles}");

        try
        {
            TileBuilderResult build = TileBuilder.BuildTileSet(
                [sourcePbf],
                managedTiles,
                new TileBuilderConfig
                {
                    Hierarchy = true,
                    Shortcuts = true,
                });

            Assert.True(build.Success);
            Assert.True(build.TileCount > 0);

            ManagedValhallaTileSetReader managedReader = new();
            ValhallaTileSetReadReceipt managedReadingOfficial =
                await managedReader.ReadAsync(
                    officialTiles,
                    TestContext.Current.CancellationToken);

            OfficialValhallaContainerTileSetReader officialReader = new(
                new OfficialValhallaContainerTileSetReaderOptions(
                    OfficialImage,
                    TimeSpan.FromMinutes(2),
                    2L * 1024 * 1024 * 1024,
                    2,
                    16 * 1024 * 1024));
            OfficialValhallaTileSetReadReceipt officialReadingManaged =
                await officialReader.ReadAsync(
                    build.TileDir,
                    TestContext.Current.CancellationToken);

            Assert.Equal(4, managedReadingOfficial.TileCount);
            Assert.True(managedReadingOfficial.NodeCount > 0);
            Assert.True(managedReadingOfficial.DirectedEdgeCount > 0);
            Assert.True(managedReadingOfficial.AllHeaderGraphIdsMatchPaths);
            Assert.True(managedReadingOfficial.AllHeaderLengthsMatchFiles);
            Assert.True(managedReadingOfficial.AllTileChecksumsMatch);

            Assert.Equal("3.8.3", officialReadingManaged.ReaderVersion);
            Assert.True(officialReadingManaged.MatchedEdgeCount > 0);
            Assert.True(officialReadingManaged.ResponseBytes > 0);
            Assert.Matches("^[0-9A-F]{64}$", officialReadingManaged.ResponseSha256);
            Assert.DoesNotContain(
                "error",
                officialReadingManaged.SafeDiagnostics,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(managedTiles))
            {
                Directory.Delete(managedTiles, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ManagedReader_CorruptTileFailsIntegrityValidation()
    {
        string officialTiles = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles");
        string corruptTiles = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-corrupt-monaco-{Guid.NewGuid():N}");
        string source = Directory
            .EnumerateFiles(officialTiles, "*.gph", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .First();
        string relativePath = Path.GetRelativePath(officialTiles, source);
        string target = Path.Combine(corruptTiles, relativePath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            byte[] tile = await File.ReadAllBytesAsync(
                source,
                TestContext.Current.CancellationToken);
            tile[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(
                target,
                tile,
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(
                async () => await new ManagedValhallaTileSetReader().ReadAsync(
                    corruptTiles,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(corruptTiles))
            {
                Directory.Delete(corruptTiles, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("ghcr.io/valhalla/valhalla:3.8.3")]
    [InlineData("https://user:secret@example.test/valhalla@sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f")]
    [InlineData("valhalla@sha256:not-a-hash")]
    public void OfficialReaderOptions_MutableOrUnsafeImageReferenceFailsClosed(
        string imageReference)
    {
        OfficialValhallaContainerTileSetReaderOptions options = new(
            imageReference,
            TimeSpan.FromMinutes(2),
            2L * 1024 * 1024 * 1024,
            2,
            16 * 1024 * 1024);

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void OfficialReaderOptions_InvalidResourceBoundsFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OfficialValhallaContainerTileSetReaderOptions(
                OfficialImage,
                TimeSpan.Zero,
                2L * 1024 * 1024 * 1024,
                2,
                16 * 1024 * 1024).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OfficialValhallaContainerTileSetReaderOptions(
                OfficialImage,
                TimeSpan.FromMinutes(2),
                1,
                2,
                16 * 1024 * 1024).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OfficialValhallaContainerTileSetReaderOptions(
                OfficialImage,
                TimeSpan.FromMinutes(2),
                2L * 1024 * 1024 * 1024,
                0,
                16 * 1024 * 1024).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OfficialValhallaContainerTileSetReaderOptions(
                OfficialImage,
                TimeSpan.FromMinutes(2),
                2L * 1024 * 1024 * 1024,
                2,
                1).Validate());
    }

    [Fact]
    public async Task OfficialFixture_FilesMatchPinnedManifest()
    {
        string fixtureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383Monaco");
        await using FileStream manifestStream = File.OpenRead(
            Path.Combine(fixtureDirectory, "manifest.json"));
        using JsonDocument manifest = await JsonDocument.ParseAsync(
            manifestStream,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "3.8.3",
            manifest.RootElement.GetProperty("oracle").GetProperty("release").GetString());
        Assert.Equal(
            OfficialImage.Split('@')[1],
            manifest.RootElement.GetProperty("oracle").GetProperty("imageDigest").GetString());

        foreach (JsonElement file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string relativePath = file.GetProperty("path").GetString()!;
            string path = Path.Combine(
                fixtureDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            FileInfo info = new(path);
            Assert.True(info.Exists, $"Missing pinned official tile: {relativePath}");
            Assert.Equal(file.GetProperty("length").GetInt64(), info.Length);

            byte[] bytes = await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                file.GetProperty("sha256").GetString(),
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }
}
