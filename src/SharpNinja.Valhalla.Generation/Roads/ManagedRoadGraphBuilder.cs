using System.Diagnostics;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads;

public enum ManagedRoadGraphPipeline
{
    Legacy = 0,
    PooledFrontier = 1,
}

public static class ManagedRoadGraphPipelineSelector
{
    public static ManagedRoadGraphPipeline Resolve(
        ValhallaGenerationProfile profile,
        ManagedRoadGraphPipeline requestedPipeline) =>
        profile == ValhallaGenerationProfile.LegacyEmbedded
            ? ManagedRoadGraphPipeline.Legacy
            : requestedPipeline;
}

public sealed record ManagedRoadGraphBuildRequest(
    IReadOnlyList<string> OsmPbfPaths,
    string WorkingDirectory,
    string OutputDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    TileBuilderConfig? TileBuilderConfig = null)
{
    public ManagedRoadGraphPipeline Pipeline { get; init; } =
        ManagedRoadGraphPipeline.Legacy;
}

public sealed record ManagedRoadGraphBuildResult(
    TileBuilderResult TileBuilderResult,
    StreamingOsmPbfReadMetrics PbfMetrics,
    long PeakIntermediateMemoryBytes,
    long ScratchDiskHighWaterMarkBytes,
    TimeSpan PbfIngestionDuration,
    TimeSpan SemanticParsingDuration,
    TimeSpan TileConstructionDuration,
    IReadOnlyDictionary<string, TimeSpan> SemanticStageDurations)
{
    public ValhallaGenerationFrontierMetrics? FrontierMetrics { get; init; }
}

/// <summary>
/// Production road-graph composition that decodes physical PBF blocks once and supplies the core
/// semantic graph pipeline from bounded replayable stores.
/// </summary>
public sealed class ManagedRoadGraphBuilder
{
    public async ValueTask<ManagedRoadGraphBuildResult> BuildAsync(
        ManagedRoadGraphBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OsmPbfPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        if (request.OsmPbfPaths.Count == 0)
        {
            throw new ArgumentException(
                "At least one OSM PBF input is required.",
                nameof(request));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            request.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            request.ScratchDiskBudgetBytes);
        cancellationToken.ThrowIfCancellationRequested();

        string intermediateDirectory = Path.Combine(
            request.WorkingDirectory,
            "osm-intermediate");
        Directory.CreateDirectory(request.WorkingDirectory);

        TileBuilderConfig tileBuilderConfig =
            request.TileBuilderConfig ?? new TileBuilderConfig();
        var parser = new PbfGraphParser(tileBuilderConfig.ParserOptions);
        OSMData osmdata;
        StreamingOsmPbfReadMetrics pbfMetrics;
        long peakIntermediateMemoryBytes;
        long scratchDiskHighWaterMarkBytes;
        TimeSpan pbfIngestionDuration;
        TimeSpan semanticParsingDuration;

        var stageStopwatch = Stopwatch.StartNew();
        using (var source = await StoredOsmPbfEntitySource.CreateAsync(
                   request.OsmPbfPaths,
                   intermediateDirectory,
                   request.StorageMode,
                   request.MemoryBudgetBytes,
                   request.ScratchDiskBudgetBytes,
                   cancellationToken)
               .ConfigureAwait(false))
        {
            stageStopwatch.Stop();
            pbfIngestionDuration = stageStopwatch.Elapsed;

            stageStopwatch.Restart();
            osmdata = await Task.Run(
                    () => parser.Parse(source, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            stageStopwatch.Stop();
            semanticParsingDuration = stageStopwatch.Elapsed;
            pbfMetrics = source.ReadResult.Metrics;
            peakIntermediateMemoryBytes = source.PeakIntermediateMemoryBytes;
            scratchDiskHighWaterMarkBytes = source.ScratchHighWaterMarkBytes;
        }

        cancellationToken.ThrowIfCancellationRequested();
        stageStopwatch.Restart();
        TileBuilderResult tileResult = await Task.Run(
                () => TileBuilder.BuildParsedTileSet(
                    parser,
                    osmdata,
                    request.OutputDirectory,
                    tileBuilderConfig,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        stageStopwatch.Stop();

        return new ManagedRoadGraphBuildResult(
            tileResult,
            pbfMetrics,
            peakIntermediateMemoryBytes,
            scratchDiskHighWaterMarkBytes,
            pbfIngestionDuration,
            semanticParsingDuration,
            stageStopwatch.Elapsed,
            new Dictionary<string, TimeSpan>(
                parser.LastParseStageDurations,
                StringComparer.Ordinal));
    }
}
