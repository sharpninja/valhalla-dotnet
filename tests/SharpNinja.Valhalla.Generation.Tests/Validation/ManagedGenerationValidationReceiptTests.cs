using System.Security.Cryptography;
using SharpNinja.Valhalla.Generation.Validation;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Validation;

public sealed class ManagedGenerationValidationReceiptTests
{
    [Fact]
    public async Task ValidGraph_ProducesCompleteDeterministicReceipt()
    {
        string first = NewScratch();
        string second = NewScratch();

        try
        {
            CopyOfficialGraph(first);
            CopyOfficialGraph(second);
            var validator = new ManagedValhallaGenerationValidator();

            ValhallaGenerationValidationResult firstResult =
                await ValidateAsync(
                    validator,
                    first,
                    "monaco-validation",
                    TestContext.Current.CancellationToken);
            ValhallaGenerationValidationResult secondResult =
                await ValidateAsync(
                    validator,
                    second,
                    "monaco-validation",
                    TestContext.Current.CancellationToken);

            Assert.True(firstResult.IsValid);
            Assert.True(secondResult.IsValid);
            Assert.Empty(firstResult.Failures);
            Assert.NotNull(firstResult.Receipt);
            Assert.NotNull(secondResult.Receipt);
            Assert.Equal(4, firstResult.Receipt.Statistics.TileCount);
            Assert.True(firstResult.Receipt.Statistics.NodeCount > 0);
            Assert.True(firstResult.Receipt.Statistics.DirectedEdgeCount > 0);
            Assert.Equal(4, firstResult.Receipt.TileSha256.Count);
            Assert.Equal(
                firstResult.Receipt.Statistics.TileCount,
                firstResult.Receipt.TileSha256.Count);
            Assert.Equal("3.8.3+a60c7cb", firstResult.Receipt.UpstreamCompatibilityVersion);

            string firstReceipt = Path.Combine(
                first,
                ManagedValhallaGenerationValidator.ReceiptRelativePath);
            string secondReceipt = Path.Combine(
                second,
                ManagedValhallaGenerationValidator.ReceiptRelativePath);
            Assert.True(File.Exists(firstReceipt));
            Assert.True(File.Exists(secondReceipt));
            Assert.Equal(
                await File.ReadAllBytesAsync(
                    firstReceipt,
                    TestContext.Current.CancellationToken),
                await File.ReadAllBytesAsync(
                    secondReceipt,
                    TestContext.Current.CancellationToken));
            byte[] firstReceiptBytes = await File.ReadAllBytesAsync(
                firstReceipt,
                TestContext.Current.CancellationToken);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(firstReceiptBytes)),
                firstResult.ReceiptSha256);
            Assert.Equal(firstReceiptBytes.LongLength, firstResult.ReceiptLength);
        }
        finally
        {
            DeleteScratch(first);
            DeleteScratch(second);
        }
    }

    [Fact]
    public async Task CorruptGraph_FailsClosedWithoutPublishingReceipt()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            string tile = Directory
                .EnumerateFiles(scratch, "*.gph", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .First();
            byte[] bytes = await File.ReadAllBytesAsync(
                tile,
                TestContext.Current.CancellationToken);
            bytes[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(
                tile,
                bytes,
                TestContext.Current.CancellationToken);

            var validator = new ManagedValhallaGenerationValidator();
            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    validator,
                    scratch,
                    "corrupt-monaco",
                    TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
            Assert.Null(result.Receipt);
            Assert.Null(result.ReceiptSha256);
            Assert.Contains(
                result.Failures,
                failure => failure.Code == ValhallaGenerationFailureCode.Validation
                    && failure.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase));
            Assert.False(
                File.Exists(
                    Path.Combine(
                        scratch,
                        ManagedValhallaGenerationValidator.ReceiptRelativePath)));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task Cancellation_DoesNotPublishReceipt()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            var validator = new ManagedValhallaGenerationValidator();
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ValidateAsync(
                    validator,
                    scratch,
                    "cancelled-monaco",
                    cancellation.Token));

            Assert.False(
                File.Exists(
                    Path.Combine(
                        scratch,
                        ManagedValhallaGenerationValidator.ReceiptRelativePath)));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task TileStatistics_ReconcileWithValidatedFiles()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            var validator = new ManagedValhallaGenerationValidator();
            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    validator,
                    scratch,
                    "statistics-monaco",
                    TestContext.Current.CancellationToken);

            ValhallaGenerationGraphStatistics statistics =
                Assert.IsType<ValhallaGenerationGraphStatistics>(
                    result.Receipt?.Statistics);
            string[] tilePaths = Directory
                .EnumerateFiles(scratch, "*.gph", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(tilePaths.Sum(path => new FileInfo(path).Length), statistics.TileBytes);
            Assert.Equal(tilePaths.Length, statistics.TileCount);
            Assert.Equal(statistics.TileCount, statistics.TilesByLevel.Values.Sum());
            Assert.Equal(
                statistics.PossibleDuplicateEdgesByLevel.Count,
                statistics.DensityByLevel.Count);
            Assert.All(
                result.Receipt!.TileSha256,
                pair => Assert.Matches("^[A-F0-9]{64}$", pair.Value));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task TruncatedGraph_FailsClosedWithoutPublishingReceipt()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            string tile = GetFirstTile(scratch);
            byte[] bytes = await File.ReadAllBytesAsync(
                tile,
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                tile,
                bytes[..(SharpNinja.Valhalla.Baldr.GraphTileHeader.HeaderSize - 1)],
                TestContext.Current.CancellationToken);

            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    new ManagedValhallaGenerationValidator(),
                    scratch,
                    "truncated-monaco",
                    TestContext.Current.CancellationToken);

            AssertValidationFailure(result);
            Assert.False(File.Exists(GetReceiptPath(scratch)));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task MixedBuildIdentity_FailsClosedBeforeGraphMutation()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            string tile = GetFirstTile(scratch);
            byte[] bytes = await File.ReadAllBytesAsync(
                tile,
                TestContext.Current.CancellationToken);
            SharpNinja.Valhalla.Baldr.GraphTileHeader header =
                SharpNinja.Valhalla.Baldr.GraphTileHeader.FromBytes(bytes);
            ushort differentBuildId = unchecked((ushort)(header.BuildId() + 1));
            header.SetRawChecksum(
                ((ulong)differentBuildId
                    << SharpNinja.Valhalla.Baldr.GraphTileHeader.TileHashBits)
                | header.TileChecksum());
            header.AsSpan().CopyTo(bytes);
            await File.WriteAllBytesAsync(
                tile,
                bytes,
                TestContext.Current.CancellationToken);

            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    new ManagedValhallaGenerationValidator(),
                    scratch,
                    "mixed-build-monaco",
                    TestContext.Current.CancellationToken);

            AssertValidationFailure(result);
            Assert.Contains(
                result.Failures,
                failure => failure.Message.Contains(
                    "build identity",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task TilePathGraphIdentityMismatch_FailsClosed()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            string tile = GetFirstTile(scratch);
            string mismatchedPath = Path.Combine(
                Path.GetDirectoryName(tile)!,
                "999999.gph");
            File.Move(tile, mismatchedPath);

            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    new ManagedValhallaGenerationValidator(),
                    scratch,
                    "path-mismatch-monaco",
                    TestContext.Current.CancellationToken);

            AssertValidationFailure(result);
            Assert.Contains(
                result.Failures,
                failure => failure.Message.Contains(
                    "graph identity",
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task EmptyGraph_FailsClosedWithoutPublishingReceipt()
    {
        string scratch = NewScratch();

        try
        {
            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    new ManagedValhallaGenerationValidator(),
                    scratch,
                    "empty-graph",
                    TestContext.Current.CancellationToken);

            AssertValidationFailure(result);
            Assert.Contains(
                result.Failures,
                failure => failure.Message.Contains(
                    "does not contain graph tiles",
                    StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(GetReceiptPath(scratch)));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task FailedRevalidation_RemovesStaleReceipt()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            var validator = new ManagedValhallaGenerationValidator();
            ValhallaGenerationValidationResult valid =
                await ValidateAsync(
                    validator,
                    scratch,
                    "stale-receipt-monaco",
                    TestContext.Current.CancellationToken);
            Assert.True(valid.IsValid);
            Assert.True(File.Exists(GetReceiptPath(scratch)));

            string tile = GetFirstTile(scratch);
            byte[] bytes = await File.ReadAllBytesAsync(
                tile,
                TestContext.Current.CancellationToken);
            bytes[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(
                tile,
                bytes,
                TestContext.Current.CancellationToken);

            ValhallaGenerationValidationResult invalid =
                await ValidateAsync(
                    validator,
                    scratch,
                    "stale-receipt-monaco",
                    TestContext.Current.CancellationToken);

            AssertValidationFailure(invalid);
            Assert.False(File.Exists(GetReceiptPath(scratch)));
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    [Fact]
    public async Task Receipt_IsPathIndependentAndContainsNoUnsafeUrl()
    {
        string scratch = NewScratch();

        try
        {
            CopyOfficialGraph(scratch);
            ValhallaGenerationValidationResult result =
                await ValidateAsync(
                    new ManagedValhallaGenerationValidator(),
                    scratch,
                    "secret-safe-monaco",
                    TestContext.Current.CancellationToken);

            Assert.True(result.IsValid);
            string json = await File.ReadAllTextAsync(
                GetReceiptPath(scratch),
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(scratch, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "https://",
                json,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "authorization",
                json,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static void AssertValidationFailure(
        ValhallaGenerationValidationResult result)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Receipt);
        Assert.Null(result.ReceiptSha256);
        Assert.Contains(
            result.Failures,
            failure => failure.Code == ValhallaGenerationFailureCode.Validation);
    }

    private static string GetFirstTile(string root) =>
        Directory
            .EnumerateFiles(root, "*.gph", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First();

    private static string GetReceiptPath(string root) =>
        Path.Combine(
            root,
            ManagedValhallaGenerationValidator.ReceiptRelativePath);

    private static async Task<ValhallaGenerationValidationResult> ValidateAsync(
        ManagedValhallaGenerationValidator validator,
        string stagingDirectory,
        string requestIdentity,
        CancellationToken cancellationToken = default)
    {
        using var resources = new ValhallaGenerationResourceBudget(
            256 * 1024 * 1024,
            512 * 1024 * 1024,
            4);
        var request = new ValhallaGenerationBuildRequest(
            [],
            ValhallaGenerationInputSet.Empty,
            Path.Combine(stagingDirectory, "..", "work"),
            Path.Combine(stagingDirectory, "..", "output"),
            new ValhallaGenerationBuildOptions(
                ValhallaGenerationProfile.Full,
                IntermediateStorageMode.Auto,
                ResumePolicy.Disabled,
                4,
                256 * 1024 * 1024,
                512 * 1024 * 1024,
                DatasetId: 0,
                BuildId: 0,
                DeterministicOutput: true));
        var context = new ValhallaGenerationStageContext(
            request,
            requestIdentity,
            stagingDirectory,
            resources);
        return await validator.ValidateAsync(context, cancellationToken);
    }

    private static string NewScratch()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "valhalla-generation-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CopyOfficialGraph(string destination)
    {
        string source = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles");
        foreach (string sourcePath in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            string destinationPath = Path.Combine(
                destination,
                Path.GetRelativePath(source, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private static void DeleteScratch(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
