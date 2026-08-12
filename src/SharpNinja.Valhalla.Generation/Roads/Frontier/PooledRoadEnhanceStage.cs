using System.Runtime.ExceptionServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record PooledRoadEnhanceStageOptions(
    long MemoryBudgetBytes,
    int MaxDegreeOfParallelism = 1)
{
    internal long MinimumPerTileBudgetBytes { get; init; } = 256 * 1024;
}

internal sealed record PooledRoadEnhanceStageReceipt(
    int TileCount,
    int EnhancedTileCount,
    int SelectedDop,
    long PeakSingleTileBytes)
{
}

/// <summary>
/// Bounded Stage G enhance: processes one source tile at a time into a staging
/// directory and atomically publishes to the destination. Never retains two full
/// tile dictionaries (source + enhanced) in managed memory.
/// </summary>
internal static class PooledRoadEnhanceStage
{
    internal static async ValueTask<PooledRoadEnhanceStageReceipt> ApplyAsync(
        string sourceTileDirectory,
        string destinationTileDirectory,
        PooledRoadEnhanceStageOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTileDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDegreeOfParallelism);
        cancellationToken.ThrowIfCancellationRequested();

        string fullSource = Path.GetFullPath(sourceTileDirectory);
        string fullDest = Path.GetFullPath(destinationTileDirectory);
        if (!Directory.Exists(fullSource))
        {
            throw new DirectoryNotFoundException(
                $"Enhance source tile directory was not found: {fullSource}");
        }

        string[] sourceFiles = Directory.GetFiles(
            fullSource,
            "*" + GraphTile.SuffixNonCompressed,
            SearchOption.AllDirectories);
        Array.Sort(sourceFiles, StringComparer.OrdinalIgnoreCase);

        long peakTileBytes = 0;
        foreach (string path in sourceFiles)
        {
            long len = new FileInfo(path).Length;
            if (len > peakTileBytes)
            {
                peakTileBytes = len;
            }
        }

        long perTileBudget = Math.Max(
            options.MinimumPerTileBudgetBytes,
            peakTileBytes * 2 + 64 * 1024);
        int selectedDop = AdaptiveGenerationParallelism.FitWorkerCount(
            options.MemoryBudgetBytes,
            options.MemoryBudgetBytes,
            perTileBudget,
            perTileBudget,
            options.MaxDegreeOfParallelism);
        if (selectedDop <= 0)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The enhance stage memory budget cannot fit a single tile working set.");
        }

        string stagingRoot = Path.Combine(
            Path.GetDirectoryName(fullDest) ?? fullDest,
            $".enhance-stage-{Guid.NewGuid():N}");
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;
        int enhancedCount = 0;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            var enhancer = new GraphEnhancer();
            GraphEnhancer.EnhancerStats stats = await Task.Run(
                    () => enhancer.EnhanceTileDirectory(
                        fullSource,
                        stagingRoot,
                        selectedDop,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            enhancedCount = stats.EnhancedTileWriteCount;

            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(fullDest))
            {
                Directory.Delete(fullDest, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullDest)!);
            // Atomic-ish replace: stage is complete before dest is swapped.
            Directory.Move(stagingRoot, fullDest);
            stagingRoot = string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            operationFailure = ex;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(stagingRoot) && Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }

        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(operationFailure, cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        return new PooledRoadEnhanceStageReceipt(
            sourceFiles.Length,
            enhancedCount,
            selectedDop,
            peakTileBytes);
    }
}
