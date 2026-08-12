using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.IO;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.IO;

public sealed class GraphTileIoHostileTests
{
    [Fact]
    public async Task CorruptDeclaredLength_DoesNotCreateLeaseOrCacheEntry()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "corrupt-length.gph");
        int actualLength = GraphTileHeader.HeaderSize + 1024;
        try
        {
            await WriteTileAsync(path, actualLength, actualLength + 1);
            await using var reader = CreateReader(8192);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.AcquireAsync(
                    path,
                    TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(0, reader.ActiveLeaseCount);
            Assert.Equal(0, reader.CachedBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CacheHit_ReusesBodyWithoutSecondFileRead()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "cache-hit.gph");
        int tileLength = GraphTileHeader.HeaderSize + 4096;
        try
        {
            await WriteTileAsync(path, tileLength, tileLength);
            await using var reader = CreateReader(16 * 1024);

            await using (GenerationGraphTileLease first = await reader.AcquireAsync(
                path,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(tileLength, first.Memory.Length);
            }

            long bytesAfterFirstRead = reader.TotalBytesRead;
            await using (GenerationGraphTileLease second = await reader.AcquireAsync(
                path,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(tileLength, second.Memory.Length);
            }

            Assert.Equal(tileLength, bytesAfterFirstRead);
            Assert.Equal(bytesAfterFirstRead, reader.TotalBytesRead);
            Assert.Equal(0, reader.ActiveLeaseCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveLease_PreventsCachedBufferEviction()
    {
        string directory = CreateTempDirectory();
        string firstPath = Path.Combine(directory, "active-first.gph");
        string secondPath = Path.Combine(directory, "active-second.gph");
        int tileLength = GraphTileHeader.HeaderSize + 1024;
        try
        {
            await WriteTileAsync(firstPath, tileLength, tileLength);
            await WriteTileAsync(secondPath, tileLength, tileLength);
            await using var reader = CreateReader(2048);

            await using GenerationGraphTileLease first = await reader.AcquireAsync(
                firstPath,
                TestContext.Current.CancellationToken);
            long cachedWhileFirstActive = reader.CachedBytes;
            await using GenerationGraphTileLease second = await reader.AcquireAsync(
                secondPath,
                TestContext.Current.CancellationToken);

            Assert.Equal(tileLength, first.Memory.Length);
            Assert.Equal(tileLength, second.Memory.Length);
            Assert.Equal(cachedWhileFirstActive, reader.CachedBytes);
            Assert.InRange(reader.CachedBytes, 1, 2048);
            Assert.Equal(2, reader.ActiveLeaseCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GenerationGraphTileReader CreateReader(long maxCachedBytes) =>
        new(new GenerationGraphTileReaderOptions(maxCachedBytes));

    private static async Task WriteTileAsync(
        string path,
        int actualLength,
        int declaredLength)
    {
        var bytes = new byte[actualLength];
        var header = new GraphTileHeader();
        header.SetGraphid(new GraphId(0, 2, 0));
        header.SetEndOffset((uint)declaredLength);
        header.AsSpan().CopyTo(bytes);
        await File.WriteAllBytesAsync(
            path,
            bytes,
            TestContext.Current.CancellationToken);
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-generation-tile-io-hostile",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
