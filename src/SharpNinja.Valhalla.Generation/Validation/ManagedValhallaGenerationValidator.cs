using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.IO;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Validation;

/// <summary>
/// Runs the managed graph-validator stage, verifies graph integrity, records deterministic
/// statistics, and atomically publishes a machine-readable validation receipt.
/// </summary>
public sealed class ManagedValhallaGenerationValidator : IValhallaGenerationValidator
{
    public const string ReceiptRelativePath = ".generation/validation-receipt.json";
    private const int ReceiptSchemaVersion = 1;
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ValueTask<ValhallaGenerationValidationResult> ValidateAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken) =>
        ValidateCoreAsync(
            context,
            prevalidatedStats: null,
            cancellationToken);

    /// <summary>
    /// Publishes an integrity receipt for graph tiles already mutated and structurally validated by
    /// the managed tile-build pipeline. The graph is still scanned once for tile identities,
    /// checksums, hashes, and aggregate statistics before publication.
    /// </summary>
    public ValueTask<ValhallaGenerationValidationResult> ValidatePrevalidatedAsync(
        ValhallaGenerationStageContext context,
        GraphValidator.ValidatorStats validatorStats,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validatorStats);
        return ValidateCoreAsync(
            context,
            validatorStats,
            cancellationToken);
    }

    private static async ValueTask<ValhallaGenerationValidationResult> ValidateCoreAsync(
        ValhallaGenerationStageContext context,
        GraphValidator.ValidatorStats? prevalidatedStats,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string stagingDirectory = Path.GetFullPath(context.StagingDirectory);
        string receiptPath = Path.Combine(
            stagingDirectory,
            ReceiptRelativePath.Replace('/', Path.DirectorySeparatorChar));
        TryDeleteReceipt(receiptPath);

        try
        {
            if (prevalidatedStats is null)
            {
                await InspectGraphAsync(
                        stagingDirectory,
                        validatorStats: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            GraphValidator.ValidatorStats validatorStats =
                prevalidatedStats ??
                await Task.Run(
                        () => GraphValidator.Validate(
                            new GraphReader.Config { TileDir = stagingDirectory },
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

            GraphSnapshot snapshot = await InspectGraphAsync(
                    stagingDirectory,
                    validatorStats,
                    cancellationToken)
                .ConfigureAwait(false);
            var receipt = new ValhallaGenerationValidationReceipt(
                ReceiptSchemaVersion,
                ValhallaGenerationBuilder.UpstreamCompatibilityVersion,
                context.RequestIdentity,
                snapshot.DatasetId,
                snapshot.BuildId,
                snapshot.OutputTreeSha256,
                snapshot.Statistics,
                snapshot.TileSha256);

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                receipt,
                ReceiptJsonOptions);
            string receiptSha256 = Convert.ToHexString(SHA256.HashData(json));
            await WriteReceiptAtomicallyAsync(
                    receiptPath,
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ValhallaGenerationValidationResult(
                true,
                [],
                receipt,
                receiptSha256,
                json.LongLength);
        }
        catch (OperationCanceledException)
        {
            TryDeleteReceipt(receiptPath);
            throw;
        }
        catch (Exception exception) when (IsValidationFailure(exception))
        {
            TryDeleteReceipt(receiptPath);
            return new ValhallaGenerationValidationResult(
                false,
                [
                    new ValhallaGenerationFailure(
                        ValhallaGenerationFailureCode.Validation,
                        SecretSafeMessage(exception),
                        ValhallaGenerationStage.ValidateGraph),
                ]);
        }
    }

    private static async ValueTask<GraphSnapshot> InspectGraphAsync(
        string tileDirectory,
        GraphValidator.ValidatorStats? validatorStats,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(tileDirectory))
        {
            throw new InvalidDataException(
                "The staged graph directory does not exist.");
        }

        string[] tilePaths = Directory
            .EnumerateFiles(tileDirectory, "*.gph", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (tilePaths.Length == 0)
        {
            throw new InvalidDataException(
                "The staged graph does not contain graph tiles.");
        }

        var tileSha256 = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var tileChecksums = new List<ulong>(tilePaths.Length);
        var datasetIds = new HashSet<ulong>();
        var buildIds = new HashSet<ushort>();
        var tilesByLevel = new SortedDictionary<byte, int>();
        long tileBytes = 0;
        long nodeCount = 0;
        long directedEdgeCount = 0;
        long transitionCount = 0;
        long predictedSpeedCount = 0;
        long transitDepartureCount = 0;
        long transitStopCount = 0;
        long transitRouteCount = 0;
        long transitScheduleCount = 0;
        long transitTransferCount = 0;
        long signCount = 0;
        long accessRestrictionCount = 0;
        long adminCount = 0;

        await using var reader = new GenerationGraphTileReader(
            new GenerationGraphTileReaderOptions(64 * 1024 * 1024));
        foreach (string tilePath in tilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GenerationGraphTileHeaderReadResult headerResult =
                await reader.ReadHeaderAsync(tilePath, cancellationToken)
                    .ConfigureAwait(false);
            GraphTileHeader header = headerResult.Header;
            string relativePath = NormalizeRelativePath(
                tileDirectory,
                tilePath);
            string expectedSuffix = GraphTile
                .FileSuffix(header.Graphid())
                .Replace('\\', '/');
            if (!string.Equals(
                    relativePath,
                    expectedSuffix,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A graph tile path does not match its graph identity.");
            }

            await using GenerationGraphTileLease lease =
                await reader.AcquireAsync(tilePath, cancellationToken)
                    .ConfigureAwait(false);
            ulong computedTileChecksum = GraphTileChecksum.ComputeTileHash(
                lease.Memory.Span[GraphTileHeader.HeaderSize..]);
            if (computedTileChecksum != header.TileChecksum())
            {
                throw new InvalidDataException(
                    "A graph tile checksum does not match its body.");
            }

            string fileSha256 = Convert.ToHexString(
                SHA256.HashData(lease.Memory.Span));
            tileSha256.Add(relativePath, fileSha256);
            tileChecksums.Add(computedTileChecksum);
            datasetIds.Add(header.DatasetId());
            buildIds.Add(header.BuildId());
            byte level = (byte)header.Graphid().Level();
            tilesByLevel[level] =
                tilesByLevel.TryGetValue(level, out int levelCount)
                    ? checked(levelCount + 1)
                    : 1;
            tileBytes = checked(tileBytes + headerResult.TileLength);
            nodeCount = checked(nodeCount + header.Nodecount());
            directedEdgeCount = checked(
                directedEdgeCount + header.Directededgecount());
            transitionCount = checked(
                transitionCount + header.Transitioncount());
            predictedSpeedCount = checked(
                predictedSpeedCount + header.PredictedspeedsCount());
            transitDepartureCount = checked(
                transitDepartureCount + header.Departurecount());
            transitStopCount = checked(
                transitStopCount + header.Stopcount());
            transitRouteCount = checked(
                transitRouteCount + header.Routecount());
            transitScheduleCount = checked(
                transitScheduleCount + header.Schedulecount());
            transitTransferCount = checked(
                transitTransferCount + header.Transfercount());
            signCount = checked(signCount + header.Signcount());
            accessRestrictionCount = checked(
                accessRestrictionCount + header.AccessRestrictionCount());
            adminCount = checked(adminCount + header.Admincount());
        }

        if (datasetIds.Count != 1)
        {
            throw new InvalidDataException(
                "Validated graph tiles do not share one dataset identity.");
        }

        if (buildIds.Count != 1)
        {
            throw new InvalidDataException(
                "Validated graph tiles do not share one build identity.");
        }

        ushort computedBuildId =
            GraphTileChecksum.ComputeTilesetBuildId(tileChecksums);
        ushort storedBuildId = buildIds.Single();
        if (computedBuildId != storedBuildId)
        {
            throw new InvalidDataException(
                "The graph tileset build identity does not match its tile checksums.");
        }

        IReadOnlyDictionary<byte, uint> possibleDuplicates =
            CreatePossibleDuplicateStatistics(validatorStats);
        IReadOnlyDictionary<byte, ValhallaGenerationDensityStatistics> densities =
            CreateDensityStatistics(validatorStats);
        var statistics = new ValhallaGenerationGraphStatistics(
            tilePaths.Length,
            tileBytes,
            nodeCount,
            directedEdgeCount,
            transitionCount,
            predictedSpeedCount,
            transitDepartureCount,
            transitStopCount,
            transitRouteCount,
            transitScheduleCount,
            transitTransferCount,
            signCount,
            accessRestrictionCount,
            adminCount,
            tilesByLevel,
            possibleDuplicates,
            densities);
        return new GraphSnapshot(
            datasetIds.Single(),
            storedBuildId,
            ComputeTreeSha256(tileSha256),
            statistics,
            tileSha256);
    }

    private static IReadOnlyDictionary<byte, uint> CreatePossibleDuplicateStatistics(
        GraphValidator.ValidatorStats? validatorStats)
    {
        var result = new SortedDictionary<byte, uint>();
        int levelCount = validatorStats?.Duplicates.Length ??
            TileHierarchy.GetTransitLevel().Level + 1;
        for (byte level = 0; level < levelCount; level++)
        {
            result.Add(
                level,
                validatorStats is null
                    ? 0
                    : validatorStats.Duplicates[level]);
        }

        return result;
    }

    private static IReadOnlyDictionary<byte, ValhallaGenerationDensityStatistics>
        CreateDensityStatistics(GraphValidator.ValidatorStats? validatorStats)
    {
        var result =
            new SortedDictionary<byte, ValhallaGenerationDensityStatistics>();
        int levelCount = validatorStats?.Densities.Length ??
            TileHierarchy.GetTransitLevel().Level + 1;
        for (byte level = 0; level < levelCount; level++)
        {
            IReadOnlyList<float> samples = validatorStats is null
                ? []
                : validatorStats.Densities[level];
            result.Add(
                level,
                samples.Count == 0
                    ? new ValhallaGenerationDensityStatistics(0, 0, 0, 0)
                    : new ValhallaGenerationDensityStatistics(
                        samples.Count,
                        samples.Min(),
                        samples.Max(),
                        samples.Average(value => (double)value)));
        }

        return result;
    }

    private static string ComputeTreeSha256(
        IReadOnlyDictionary<string, string> tileSha256)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string relativePath, string tileHash) in tileSha256)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(tileHash));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string NormalizeRelativePath(
        string root,
        string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A graph tile resolved outside the staged graph directory.");
        }

        return relative;
    }

    private static async ValueTask WriteReceiptAtomicallyAsync(
        string receiptPath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(receiptPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath =
            receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, receiptPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsValidationFailure(Exception exception) =>
        exception is InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or OverflowException;

    private static string SecretSafeMessage(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException =>
                "Graph validation could not access a required staged artifact.",
            IOException =>
                "Graph validation encountered an input/output failure.",
            _ => exception.Message,
        };

    private static void TryDeleteReceipt(string receiptPath)
    {
        if (File.Exists(receiptPath))
        {
            File.Delete(receiptPath);
        }
    }

    private sealed record GraphSnapshot(
        ulong DatasetId,
        ushort BuildId,
        string OutputTreeSha256,
        ValhallaGenerationGraphStatistics Statistics,
        IReadOnlyDictionary<string, string> TileSha256);
}
