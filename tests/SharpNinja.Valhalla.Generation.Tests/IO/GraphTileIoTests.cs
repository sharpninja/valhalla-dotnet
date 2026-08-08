using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.IO;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.IO;

public sealed class GraphReaderPerformanceContractTests
{
    [Fact]
    public async Task HeaderRead_DoesNotLoadTileBody()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "header-only.gph");
        try
        {
            int tileLength = GraphTileHeader.HeaderSize + (64 * 1024);
            await WriteTileAsync(path, tileLength);
            await using var reader = new GenerationGraphTileReader(
                new GenerationGraphTileReaderOptions(128 * 1024));

            GenerationGraphTileHeaderReadResult result =
                await reader.ReadHeaderAsync(
                    path,
                    TestContext.Current.CancellationToken);

            Assert.Equal(GraphTileHeader.HeaderSize, result.BytesRead);
            Assert.Equal(GraphTileHeader.HeaderSize, reader.TotalBytesRead);
            Assert.Equal(tileLength, result.TileLength);
            Assert.Equal((uint)tileLength, result.Header.EndOffset());
            Assert.Equal(0, reader.ActiveLeaseCount);
            Assert.Equal(0, reader.CachedBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteTileAsync(string path, int length)
    {
        var bytes = new byte[length];
        var header = new GraphTileHeader();
        header.SetGraphid(new GraphId(0, 2, 0));
        header.SetEndOffset((uint)length);
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
            "valhalla-generation-tile-io",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

public sealed class GraphTileSerializationTests
{
    [Fact]
    public async Task StoreTileData_DoesNotPerformWholeTileRoundTripCopy()
    {
        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));
        byte[] blob = builder.StoreTileData();
        GraphTile tile = GraphTile.Create(new GraphId(0, 2, 0), blob);

        Assert.Equal((uint)blob.Length, tile.Header().EndOffset());

        string sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SharpNinja.Valhalla",
            "Mjolnir",
            "GraphTileBuilder.cs");
        string source = await File.ReadAllTextAsync(
            sourcePath,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("inMem.ToArray()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Array.Copy(body", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.SizeOf<T>()", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SharpNinja.Valhalla.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

public sealed class GenerationGraphTileLeaseTests
{
    [Fact]
    public async Task Lease_RemainsValidUntilLeaseDisposalAfterReaderDisposal()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "leased.gph");
        GenerationGraphTileLease? lease = null;
        try
        {
            await WriteTileAsync(path, GraphTileHeader.HeaderSize + 4096);
            var reader = new GenerationGraphTileReader(
                new GenerationGraphTileReaderOptions(8192));
            lease = await reader.AcquireAsync(
                path,
                TestContext.Current.CancellationToken);

            await reader.DisposeAsync();

            Assert.False(lease.IsDisposed);
            Assert.Equal(GraphTileHeader.HeaderSize + 4096, lease.Memory.Length);
            Assert.Equal((byte)0, lease.Memory.Span[^1]);

            await lease.DisposeAsync();
            Assert.True(lease.IsDisposed);
            Assert.Equal(0, reader.ActiveLeaseCount);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_DoesNotRetainMoreThanConfiguredBudget()
    {
        string directory = CreateTempDirectory();
        string first = Path.Combine(directory, "first.gph");
        string second = Path.Combine(directory, "second.gph");
        int tileLength = GraphTileHeader.HeaderSize + 2048;
        try
        {
            await WriteTileAsync(first, tileLength);
            await WriteTileAsync(second, tileLength);
            await using var reader = new GenerationGraphTileReader(
                new GenerationGraphTileReaderOptions(tileLength));

            await using (GenerationGraphTileLease lease = await reader.AcquireAsync(
                first,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(tileLength, lease.Memory.Length);
            }

            await using (GenerationGraphTileLease lease = await reader.AcquireAsync(
                second,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(tileLength, lease.Memory.Length);
            }

            Assert.InRange(reader.CachedBytes, 0, tileLength);
            Assert.Equal(0, reader.ActiveLeaseCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledRead_DoesNotCreateLeaseOrCacheEntry()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "cancel.gph");
        try
        {
            await WriteTileAsync(path, GraphTileHeader.HeaderSize + 4096);
            await using var reader = new GenerationGraphTileReader(
                new GenerationGraphTileReaderOptions(8192));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.AcquireAsync(path, cancellation.Token).AsTask());
            Assert.Equal(0, reader.ActiveLeaseCount);
            Assert.Equal(0, reader.CachedBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteTileAsync(string path, int length)
    {
        var bytes = new byte[length];
        var header = new GraphTileHeader();
        header.SetGraphid(new GraphId(0, 2, 0));
        header.SetEndOffset((uint)length);
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
            "valhalla-generation-tile-lease",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
