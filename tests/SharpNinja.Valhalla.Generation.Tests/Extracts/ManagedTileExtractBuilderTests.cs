using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using SharpNinja.Valhalla.Generation.Extracts;
using SharpNinja.Valhalla.Generation.Qualification;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Extracts;

public sealed class ManagedTileExtractBuilderTests
{
    private const string OfficialImage =
        "ghcr.io/valhalla/valhalla@sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f";

    [Fact]
    public async Task BuildsImmutableRegionAddressableExtract()
    {
        string tileDirectory = FindRepositoryArtifact(
            "tests",
            "SharpNinja.Valhalla.Generation.Tests",
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles");
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-managed-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            string firstPath = Path.Combine(root, "monaco-first.tar");
            string secondPath = Path.Combine(root, "monaco-second.tar");
            var request = new TileExtractBuildRequest(
                tileDirectory,
                firstPath,
                RegionId: "monaco",
                DatasetId: 17,
                BuildId: 20260808,
                DeterministicOutput: true);
            ITileExtractBuilder builder = new ManagedTileExtractBuilder();

            TileExtractBuildResult first = await builder.BuildAsync(
                request,
                TestContext.Current.CancellationToken);
            TileExtractBuildResult second = await builder.BuildAsync(
                request with { OutputPath = secondPath },
                TestContext.Current.CancellationToken);

            Assert.Equal("monaco", first.RegionId);
            Assert.Equal(4, first.TileCount);
            Assert.Equal(first.ArchiveSha256, second.ArchiveSha256);
            Assert.Equal(first.ByteLength, second.ByteLength);
            await using (FileStream hashStream = File.OpenRead(firstPath))
            {
                Assert.Equal(
                    first.ArchiveSha256,
                    Convert.ToHexString(await SHA256.HashDataAsync(
                        hashStream,
                        TestContext.Current.CancellationToken)));
            }

            using FileStream stream = File.OpenRead(firstPath);
            using var reader = new TarReader(stream);
            var entries = new List<string>();
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                entries.Add(entry.Name);
                if (entry.Name == "index.bin")
                {
                    Assert.Equal(4 * 16, entry.Length);
                }

                if (entry.Name == "manifest.json")
                {
                    using JsonDocument manifest = await JsonDocument.ParseAsync(
                        entry.DataStream!,
                        cancellationToken: TestContext.Current.CancellationToken);
                    Assert.Equal("monaco", manifest.RootElement.GetProperty("regionId").GetString());
                    Assert.Equal(17u, manifest.RootElement.GetProperty("datasetId").GetUInt32());
                    Assert.Equal(20260808ul, manifest.RootElement.GetProperty("buildId").GetUInt64());
                    Assert.Equal(4, manifest.RootElement.GetProperty("tiles").GetArrayLength());
                }
            }

            Assert.Equal(
                [
                    "index.bin",
                    "0/003/016.gph",
                    "1/048/067.gph",
                    "2/000/769/709.gph",
                    "2/000/771/149.gph",
                    "manifest.json",
                ],
                entries);

            var officialReader = new OfficialValhallaContainerTileSetReader(
                new OfficialValhallaContainerTileSetReaderOptions(
                    OfficialImage,
                    TimeSpan.FromMinutes(2),
                    2L * 1024 * 1024 * 1024,
                    2,
                    16 * 1024 * 1024));
            OfficialValhallaTileSetReadReceipt officialReceipt =
                await officialReader.ReadExtractAsync(
                    firstPath,
                    TestContext.Current.CancellationToken);
            Assert.Equal("3.8.3", officialReceipt.ReaderVersion);
            Assert.True(officialReceipt.MatchedEdgeCount > 0);

            TileExtractBuildException exception = await Assert.ThrowsAsync<TileExtractBuildException>(
                async () => await builder.BuildAsync(
                    request,
                    TestContext.Current.CancellationToken));
            Assert.Equal(TileExtractFailureCode.OutputAlreadyExists, exception.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
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
}
