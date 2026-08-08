using System.Globalization;
using System.Security.Cryptography;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Differential;
using SharpNinja.Valhalla.Generation.Parallel;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.HistoricalSpeeds;

/// <summary>
/// Bounded, deterministic implementation of Valhalla 3.8.3 historical-speed CSV ingestion.
/// </summary>
public sealed class ManagedHistoricalSpeedDataBuilder : IHistoricalSpeedDatasetBuilder
{
    private const long MinimumWorkItemBytes = 64 * 1024;

    public async ValueTask<HistoricalSpeedDatasetBuildResult> BuildAsync(
        HistoricalSpeedDatasetBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string graphDirectory = Path.GetFullPath(request.GraphTileDirectory);
        string inputDirectory = Path.GetFullPath(request.HistoricalSpeedDirectory);
        EnsureDirectoryExists(graphDirectory, "staged graph tile");
        EnsureDirectoryExists(inputDirectory, "historical-speed input");
        RejectReparsePoint(graphDirectory);
        RejectReparsePoint(inputDirectory);
        IReadOnlyList<string> initialGraphTilePaths = EnumerateFilesSafely(
            graphDirectory,
            "*.gph",
            cancellationToken);
        ValidateGraphTileResources(
            initialGraphTilePaths,
            request.MemoryBudgetBytes,
            request.ScratchDiskBudgetBytes);
        IReadOnlyList<TileTrafficInput> inputs = DiscoverInputs(
            graphDirectory,
            inputDirectory,
            request.MemoryBudgetBytes,
            cancellationToken);

        if (inputs.Count == 0)
        {
            IReadOnlyList<string> unchangedTreeFiles = EnumerateFilesSafely(
                graphDirectory,
                "*",
                cancellationToken);
            long treeBytes = SumFileLengths(unchangedTreeFiles);
            string unchangedTree = await new GenerationOutputTreeHasher()
                .ComputeSha256Async(graphDirectory, cancellationToken)
                .ConfigureAwait(false);
            return new HistoricalSpeedDatasetBuildResult(
                graphDirectory,
                0,
                0,
                0,
                0,
                0,
                treeBytes,
                0,
                0,
                0,
                unchangedTree,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var scheduler = new DeterministicGenerationScheduler(
            new GenerationParallelExecutionOptions(
                request.MaxDegreeOfParallelism,
                request.MemoryBudgetBytes,
                Math.Max(1, request.MaxDegreeOfParallelism)));
        using var writeGate = new SemaphoreSlim(1, 1);
        GenerationParallelMapResult<TileTrafficBuildResult> mapped;
        try
        {
            mapped = await scheduler.MapAsync(
                    inputs,
                    input => input.EstimatedMemoryBytes,
                    (input, token) => ProcessTileAsync(
                        graphDirectory,
                        input,
                        request.ScratchDiskBudgetBytes,
                        writeGate,
                        token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ValhallaGenerationResourceLimitException exception)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded,
                "A historical-speed tile exceeded the configured memory budget.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> refreshedGraphTilePaths = EnumerateFilesSafely(
            graphDirectory,
            "*.gph",
            cancellationToken);
        ValidateGraphTileResources(
            refreshedGraphTilePaths,
            request.MemoryBudgetBytes,
            request.ScratchDiskBudgetBytes);
        long refreshedGraphTileBytes = SumFileLengths(refreshedGraphTilePaths);
        long graphTileScratchHighWater = refreshedGraphTilePaths.Count == 0
            ? 0
            : refreshedGraphTilePaths.Max(path => new FileInfo(path).Length);
        GraphTileChecksum.RefreshTilesetFiles(
            graphDirectory,
            cancellationToken);

        var tileSha256 = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long tileShaBytesRead = 0;
        foreach (TileTrafficInput input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tilePath = Path.Combine(
                graphDirectory,
                GraphTile.FileSuffix(input.TileId));
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(tilePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.GraphTileReadFailed,
                    $"Updated graph tile {input.TileId} could not be read.",
                    exception);
            }

            tileShaBytesRead = checked(tileShaBytesRead + bytes.LongLength);
            tileSha256.Add(
                GraphTile.FileSuffix(input.TileId).Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(bytes)));
        }

        IReadOnlyList<string> treeFiles = EnumerateFilesSafely(
            graphDirectory,
            "*",
            cancellationToken);
        long treeBytesRead = SumFileLengths(treeFiles);
        string treeHash = await new GenerationOutputTreeHasher()
            .ComputeSha256Async(graphDirectory, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TileTrafficBuildResult> results = mapped.Results;
        long checksumBytes = checked(refreshedGraphTileBytes * 2);
        long bytesRead = checked(
            results.Sum(item => item.BytesRead) +
            checksumBytes +
            tileShaBytesRead +
            treeBytesRead);
        long bytesWritten = checked(
            results.Sum(item => item.BytesWritten) +
            checksumBytes);
        long scratchDiskHighWater = Math.Max(
            results.Max(item => item.ScratchDiskHighWaterBytes),
            graphTileScratchHighWater);
        return new HistoricalSpeedDatasetBuildResult(
            graphDirectory,
            results.Count,
            results.Sum(item => item.UpdatedEdgeCount),
            results.Sum(item => item.PredictedProfileCount),
            results.Sum(item => item.FreeFlowSpeedCount),
            results.Sum(item => item.ConstrainedFlowSpeedCount),
            bytesRead,
            bytesWritten,
            scratchDiskHighWater,
            mapped.Receipt.MaxObservedConcurrency,
            treeHash,
            tileSha256);
    }

    private static IReadOnlyList<TileTrafficInput> DiscoverInputs(
        string graphDirectory,
        string inputDirectory,
        long memoryBudgetBytes,
        CancellationToken cancellationToken)
    {
        var grouped = new SortedDictionary<
            GraphId,
            List<string>>();
        foreach (string path in EnumerateFilesSafely(
                     inputDirectory,
                     "*.csv",
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId tileId = ParseTileId(inputDirectory, path);
            string graphPath = Path.Combine(
                graphDirectory,
                GraphTile.FileSuffix(tileId));
            if (!File.Exists(graphPath))
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.GraphTileNotFound,
                    $"Historical-speed input targets missing graph tile {tileId}.");
            }

            if (!grouped.TryGetValue(tileId, out List<string>? paths))
            {
                paths = [];
                grouped.Add(tileId, paths);
            }

            paths.Add(path);
        }

        var result = new List<TileTrafficInput>(grouped.Count);
        foreach ((GraphId tileId, List<string> paths) in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string graphPath = Path.Combine(
                graphDirectory,
                GraphTile.FileSuffix(tileId));
            long inputBytes = paths.Sum(path => new FileInfo(path).Length);
            long tileBytes = new FileInfo(graphPath).Length;
            long estimate;
            try
            {
                estimate = checked(
                    Math.Max(
                        MinimumWorkItemBytes,
                        inputBytes + (tileBytes * 2)));
            }
            catch (OverflowException exception)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded,
                    $"Historical-speed input for tile {tileId} exceeds supported size.",
                    exception);
            }

            if (estimate > memoryBudgetBytes)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded,
                    $"Historical-speed input for tile {tileId} exceeds the configured memory budget.");
            }

            result.Add(
                new TileTrafficInput(
                    tileId,
                    paths.Order(StringComparer.Ordinal).ToArray(),
                    inputBytes,
                    estimate));
        }

        return result;
    }

    private static async ValueTask<TileTrafficBuildResult> ProcessTileAsync(
        string graphDirectory,
        TileTrafficInput input,
        long scratchDiskBudgetBytes,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<HistoricalSpeedRecord> records = await ParseRecordsAsync(
                input,
                cancellationToken)
            .ConfigureAwait(false);
        string graphPath = Path.Combine(
            graphDirectory,
            GraphTile.FileSuffix(input.TileId));
        long graphInputBytes = new FileInfo(graphPath).Length;
        GraphTile tile;
        try
        {
            tile = GraphTile.Create(graphDirectory, input.TileId)
                ?? throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.GraphTileReadFailed,
                    $"Graph tile {input.TileId} could not be opened.");
        }
        catch (HistoricalSpeedDatasetBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidDataException
                or ArgumentException
                or InvalidOperationException)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.GraphTileReadFailed,
                $"Graph tile {input.TileId} could not be opened.",
                exception);
        }

        uint edgeCount = tile.Header().Directededgecount();
        foreach (HistoricalSpeedRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.EdgeId.Id() >= edgeCount)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.EdgeNotFound,
                    $"Historical-speed edge {record.EdgeId} does not exist in its graph tile.");
            }
        }

        var builder = new GraphTileBuilder(tile);
        int predictedCount = records.Count(item => item.Coefficients is not null);
        int freeFlowCount = 0;
        int constrainedFlowCount = 0;
        foreach (HistoricalSpeedRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int edgeIndex = checked((int)record.EdgeId.Id());
            DirectedEdge edge = builder.DirectedEdges[edgeIndex];
            if (record.FreeFlowSpeed > 0)
            {
                edge.SetFreeFlowSpeed(record.FreeFlowSpeed);
                freeFlowCount++;
            }

            if (record.ConstrainedFlowSpeed > 0)
            {
                edge.SetConstrainedFlowSpeed(record.ConstrainedFlowSpeed);
                constrainedFlowCount++;
            }

            if (record.Coefficients is not null)
            {
                builder.AddPredictedSpeed(
                    record.EdgeId.Id(),
                    record.Coefficients,
                    predictedCount);
                edge.SetHasPredictedSpeed(true);
            }

            builder.DirectedEdges[edgeIndex] = edge;
        }

        byte[] output = builder.StoreTileData();
        if (output.LongLength > scratchDiskBudgetBytes)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.ScratchDiskBudgetExceeded,
                $"Graph tile {input.TileId} exceeds the configured scratch-disk budget.");
        }

        string temporaryPath =
            graphPath + ".historical-speeds-" + Guid.NewGuid().ToString("N") + ".tmp";
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(output, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, graphPath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            DeleteTemporaryFile(temporaryPath);
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.GraphTileWriteFailed,
                $"Graph tile {input.TileId} could not be atomically replaced.",
                exception);
        }
        finally
        {
            writeGate.Release();
        }

        return new TileTrafficBuildResult(
            records.Count,
            predictedCount,
            freeFlowCount,
            constrainedFlowCount,
            checked(input.InputBytes + graphInputBytes),
            output.LongLength,
            output.LongLength);
    }

    private static async ValueTask<IReadOnlyList<HistoricalSpeedRecord>> ParseRecordsAsync(
        TileTrafficInput input,
        CancellationToken cancellationToken)
    {
        var records = new List<HistoricalSpeedRecord>();
        var identities = new HashSet<ulong>();
        foreach (string path in input.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                   is string line)
            {
                lineNumber++;
                cancellationToken.ThrowIfCancellationRequested();
                HistoricalSpeedRecord record = ParseRecord(
                    input.TileId,
                    line,
                    lineNumber);
                if (!identities.Add(record.EdgeId.Value))
                {
                    throw new HistoricalSpeedDatasetBuildException(
                        HistoricalSpeedDatasetFailureCode.DuplicateGraphId,
                        $"Historical-speed input contains duplicate edge {record.EdgeId}.");
                }

                records.Add(record);
            }
        }

        return records;
    }

    private static HistoricalSpeedRecord ParseRecord(
        GraphId tileId,
        string line,
        int lineNumber)
    {
        string[] fields = line.Split(',', StringSplitOptions.None);
        if (fields.Length != 4)
        {
            throw InvalidRecord(tileId, lineNumber);
        }

        try
        {
            var edgeId = new GraphId(fields[0]);
            if (edgeId.TileBase() != tileId)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.TileIdentityMismatch,
                    $"Historical-speed record line {lineNumber} does not match tile {tileId}.");
            }

            if (!byte.TryParse(
                    fields[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out byte freeFlowSpeed)
                || !byte.TryParse(
                    fields[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out byte constrainedFlowSpeed))
            {
                throw InvalidRecord(tileId, lineNumber);
            }

            short[]? coefficients = fields[3].Length == 0
                ? null
                : PredictedSpeedCompression.DecodeCompressedSpeeds(fields[3]);
            return new HistoricalSpeedRecord(
                edgeId,
                freeFlowSpeed,
                constrainedFlowSpeed,
                coefficients);
        }
        catch (HistoricalSpeedDatasetBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FormatException
                or InvalidOperationException
                or OverflowException
                or ArgumentException)
        {
            throw InvalidRecord(tileId, lineNumber, exception);
        }
    }

    private static HistoricalSpeedDatasetBuildException InvalidRecord(
        GraphId tileId,
        int lineNumber,
        Exception? innerException = null) =>
        new(
            HistoricalSpeedDatasetFailureCode.InvalidTrafficRecord,
            $"Historical-speed input for tile {tileId} has an invalid record at line {lineNumber}.",
            innerException);

    private static GraphId ParseTileId(
        string inputDirectory,
        string path)
    {
        try
        {
            string relative = Path.GetRelativePath(inputDirectory, path);
            string fileName = Path.GetFileName(relative);
            int extensionIndex = fileName.IndexOf('.');
            if (extensionIndex <= 0)
            {
                throw new InvalidOperationException();
            }

            string stem = fileName[..extensionIndex];
            string? relativeDirectory = Path.GetDirectoryName(relative);
            string identityPath = string.IsNullOrEmpty(relativeDirectory)
                ? stem
                : Path.Combine(relativeDirectory, stem);
            return GraphTile.GetTileId(identityPath).TileBase();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or FormatException
                or OverflowException)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                "A historical-speed file name does not identify a Valhalla graph tile.",
                exception);
        }
    }

    private static void ValidateRequest(HistoricalSpeedDatasetBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GraphTileDirectory)
            || string.IsNullOrWhiteSpace(request.HistoricalSpeedDirectory)
            || request.MaxDegreeOfParallelism <= 0
            || request.MemoryBudgetBytes <= 0
            || request.ScratchDiskBudgetBytes <= 0)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                "Historical-speed generation requires valid directories and positive resource limits.");
        }
    }

    private static void EnsureDirectoryExists(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                $"The {description} directory does not exist.");
        }
    }

    private static IReadOnlyList<string> EnumerateFilesSafely(
        string rootDirectory,
        string searchPattern,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = pending.Pop();
                RejectReparsePoint(current);
                foreach (string file in Directory
                             .EnumerateFiles(current, searchPattern, SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(file);
                    files.Add(file);
                }

                string[] directories = Directory
                    .EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly)
                    .OrderDescending(StringComparer.Ordinal)
                    .ToArray();
                foreach (string directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(directory);
                    pending.Push(directory);
                }
            }
        }
        catch (HistoricalSpeedDatasetBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                "Historical-speed generation could not enumerate a configured directory.",
                exception);
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void ValidateGraphTileResources(
        IReadOnlyList<string> graphTilePaths,
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes)
    {
        foreach (string path in graphTilePaths)
        {
            long length = new FileInfo(path).Length;
            if (length > scratchDiskBudgetBytes)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.ScratchDiskBudgetExceeded,
                    "A graph tile exceeds the configured scratch-disk budget.");
            }

            long memoryEstimate;
            try
            {
                memoryEstimate = checked(length * 2);
            }
            catch (OverflowException exception)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded,
                    "A graph tile exceeds the supported memory envelope.",
                    exception);
            }

            if (memoryEstimate > memoryBudgetBytes)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded,
                    "A graph tile exceeds the configured memory budget.");
            }
        }
    }

    private static long SumFileLengths(IReadOnlyList<string> paths)
    {
        try
        {
            return paths.Sum(path => new FileInfo(path).Length);
        }
        catch (OverflowException exception)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                "Configured historical-speed files exceed supported size.",
                exception);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new HistoricalSpeedDatasetBuildException(
                    HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                    "Historical-speed generation does not follow reparse points.");
            }
        }
        catch (HistoricalSpeedDatasetBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            throw new HistoricalSpeedDatasetBuildException(
                HistoricalSpeedDatasetFailureCode.InvalidConfiguration,
                "Historical-speed generation could not validate an input path.",
                exception);
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record TileTrafficInput(
        GraphId TileId,
        IReadOnlyList<string> Paths,
        long InputBytes,
        long EstimatedMemoryBytes);

    private sealed record HistoricalSpeedRecord(
        GraphId EdgeId,
        byte FreeFlowSpeed,
        byte ConstrainedFlowSpeed,
        short[]? Coefficients);

    private sealed record TileTrafficBuildResult(
        int UpdatedEdgeCount,
        int PredictedProfileCount,
        int FreeFlowSpeedCount,
        int ConstrainedFlowSpeedCount,
        long BytesRead,
        long BytesWritten,
        long ScratchDiskHighWaterBytes);
}
