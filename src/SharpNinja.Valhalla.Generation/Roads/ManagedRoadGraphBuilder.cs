using System.Diagnostics;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
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

    public string? TimeZoneDatabasePath { get; init; }
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

    public ManagedRoadGraphResourceMetrics? ResourceMetrics { get; init; }
}

public sealed record ManagedRoadGraphResourceMetrics(
    long IngestionMemoryPeakBytes,
    long SemanticPhaseMemoryPeakBytes,
    long GraphAndTilePhaseMemoryPeakBytes,
    long RestrictionPhaseMemoryPeakBytes,
    long IngestionScratchPeakBytes,
    long SemanticPhaseScratchPeakBytes,
    long GraphAndTilePhaseScratchPeakBytes,
    long RestrictionPhaseScratchPeakBytes)
{
    public int SelectedDop { get; init; }

    public long PerWorkerMemoryReservationBytes { get; init; }

    public long PerWorkerScratchReservationBytes { get; init; }

    public long MemoryHighWaterMarkBytes =>
        Math.Max(
            Math.Max(
                IngestionMemoryPeakBytes,
                SemanticPhaseMemoryPeakBytes),
            Math.Max(
                GraphAndTilePhaseMemoryPeakBytes,
                RestrictionPhaseMemoryPeakBytes));

    public long ScratchHighWaterMarkBytes =>
        Math.Max(
            Math.Max(
                IngestionScratchPeakBytes,
                SemanticPhaseScratchPeakBytes),
            Math.Max(
                GraphAndTilePhaseScratchPeakBytes,
                RestrictionPhaseScratchPeakBytes));
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
        if (request.Pipeline == ManagedRoadGraphPipeline.PooledFrontier)
        {
            return await BuildPooledFrontierAsync(
                    request,
                    tileBuilderConfig,
                    intermediateDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
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

    private static async ValueTask<ManagedRoadGraphBuildResult>
        BuildPooledFrontierAsync(
            ManagedRoadGraphBuildRequest request,
            TileBuilderConfig tileBuilderConfig,
            string intermediateDirectory,
            CancellationToken cancellationToken)
    {
        long stageMemoryBudget = request.MemoryBudgetBytes / 3;
        long stageScratchBudget = request.ScratchDiskBudgetBytes / 3;
        long finalStageScratchBudget = request.ScratchDiskBudgetBytes -
            (stageScratchBudget * 2);
        if (stageMemoryBudget <= 0 || stageScratchBudget <= 0)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The pooled road-graph pipeline cannot partition the configured " +
                "resource budget across semantic, graph, and active-stage state.");
        }

        var durations = new Dictionary<string, TimeSpan>(
            StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        StoredOsmPbfEntitySource? source = null;
        CompactOsmSemanticStore? semanticStore = null;
        StreamingOsmPbfReadMetrics pbfMetrics;
        long sourcePeakMemory;
        long sourceScratchHighWater;
        long semanticPhasePeakMemory;
        long semanticPhasePeakScratch;
        TimeSpan pbfDuration;
        TimeSpan semanticDuration;
        try
        {
            source = await StoredOsmPbfEntitySource.CreateAsync(
                    request.OsmPbfPaths,
                    intermediateDirectory,
                    request.StorageMode,
                    stageMemoryBudget,
                    stageScratchBudget,
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            pbfDuration = stopwatch.Elapsed;
            durations["pooled.ingest"] = pbfDuration;
            pbfMetrics = source.ReadResult.Metrics;
            sourcePeakMemory = source.PeakIntermediateMemoryBytes;
            sourceScratchHighWater = source.ScratchHighWaterMarkBytes;

            stopwatch.Restart();
            semanticStore = await CompactOsmSemanticStore.BuildAsync(
                    source,
                    new CompactOsmSemanticStoreOptions(
                        Path.Combine(
                            request.WorkingDirectory,
                            "pooled-semantic"),
                        request.StorageMode,
                        stageMemoryBudget,
                        stageScratchBudget),
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            semanticDuration = stopwatch.Elapsed;
            durations["pooled.semantic"] = semanticDuration;
            semanticPhasePeakMemory = checked(
                source.CurrentIntermediateMemoryBytes +
                semanticStore.PeakMemoryBytes);
            semanticPhasePeakScratch = checked(
                source.CurrentScratchBytes +
                semanticStore.ScratchHighWaterMarkBytes);
        }
        finally
        {
            source?.Dispose();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Restart();
            using PooledRoadEdgeBuildResult graph =
                await PooledRoadEdgeBuilder.BuildAsync(
                        semanticStore,
                        new PooledRoadEdgeBuilderOptions(
                            Path.Combine(
                                request.WorkingDirectory,
                                "pooled-edges"),
                            request.StorageMode,
                            stageMemoryBudget,
                            stageScratchBudget,
                            tileBuilderConfig.GridDivisions),
                        cancellationToken)
                    .ConfigureAwait(false);
            stopwatch.Stop();
            TimeSpan edgeDuration = stopwatch.Elapsed;
            durations["pooled.edges"] = edgeDuration;

            string unrestrictedTileDirectory = Path.Combine(
                request.WorkingDirectory,
                "pooled-road-tiles");
            // Per-worker reservation estimates (slab + shape/edge/tile buffers).
            const long perWorkerMemoryBytes = 8L * 1024 * 1024;
            const long perWorkerScratchBytes = 16L * 1024 * 1024;
            int selectedDop = AdaptiveGenerationParallelism.FitWorkerCount(
                stageMemoryBudget,
                stageScratchBudget,
                perWorkerMemoryBytes,
                perWorkerScratchBytes,
                tileBuilderConfig.MaxDegreeOfParallelism);
            if (selectedDop <= 0)
            {
                throw new ValhallaGenerationResourceLimitException(
                    "The pooled road-graph pipeline cannot fit a single tile worker " +
                    "within the remaining stage resource budget.");
            }

            stopwatch.Restart();
            BoundedRoadTileWriteReceipt tileReceipt =
                await BoundedRoadTileWriter.WriteAsync(
                        semanticStore,
                        graph,
                        new BoundedRoadTileWriterOptions(
                            unrestrictedTileDirectory,
                            stageMemoryBudget,
                            selectedDop)
                        {
                            TimeZoneDatabasePath = request.TimeZoneDatabasePath,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            stopwatch.Stop();
            TimeSpan tileDuration = stopwatch.Elapsed;
            durations["pooled.tiles"] = tileDuration;
            ValhallaGenerationFrontierMetrics frontierMetrics =
                graph.FrontierMetrics;

            // Restriction consumes writer tiles (checksum-valid). Enhance runs after
            // restrictions on the published tile tree so Stage G never mutates
            // restriction-stage source checksums in place.
            stopwatch.Restart();
            PooledRoadRestrictionStageReceipt restrictionReceipt =
                await PooledRoadRestrictionStage.ApplyAsync(
                        unrestrictedTileDirectory,
                        request.OutputDirectory,
                        semanticStore,
                        new PooledRoadRestrictionStageOptions(
                            Path.Combine(
                                request.WorkingDirectory,
                                "pooled-restrictions"),
                            request.StorageMode,
                            stageMemoryBudget,
                            finalStageScratchBudget),
                        cancellationToken)
                    .ConfigureAwait(false);
            stopwatch.Stop();
            TimeSpan restrictionDuration = stopwatch.Elapsed;
            durations["pooled.restrictions"] = restrictionDuration;

            string enhancedStagingDirectory = Path.Combine(
                request.WorkingDirectory,
                "pooled-road-tiles-enhanced");
            stopwatch.Restart();
            PooledRoadEnhanceStageReceipt enhanceReceipt =
                await PooledRoadEnhanceStage.ApplyAsync(
                        request.OutputDirectory,
                        enhancedStagingDirectory,
                        new PooledRoadEnhanceStageOptions(
                            stageMemoryBudget,
                            selectedDop),
                        cancellationToken)
                    .ConfigureAwait(false);
            // Publish enhanced tiles back to the requested output directory.
            if (Directory.Exists(request.OutputDirectory))
            {
                Directory.Delete(request.OutputDirectory, recursive: true);
            }

            Directory.Move(enhancedStagingDirectory, request.OutputDirectory);
            stopwatch.Stop();
            TimeSpan enhanceDuration = stopwatch.Elapsed;
            durations["pooled.enhance"] = enhanceDuration;
            _ = enhanceReceipt;

            var tileResult = new TileBuilderResult
            {
                Success = true,
                TileDir =
                    Path.TrimEndingDirectorySeparator(
                        Path.GetFullPath(request.OutputDirectory)) +
                    Path.DirectorySeparatorChar,
                WayCount = checked((int)semanticStore.WayCount),
                WayNodeCount =
                    checked((int)semanticStore.WayNodeReferenceCount),
                TileCount = tileReceipt.TileCount,
            };
            foreach (KeyValuePair<string, TimeSpan> duration in durations)
            {
                tileResult.RecordStageDuration(
                    duration.Key,
                    duration.Value);
            }

            long semanticCurrentScratch =
                semanticStore.CurrentScratchBytes;
            long graphCurrentScratch = graph.CurrentScratchBytes;
            long graphAndTilePhaseScratch = checked(
                semanticCurrentScratch +
                frontierMetrics.MappedStorageHighWaterMarkBytes +
                tileReceipt.OutputScratchBytes);
            long restrictionPhaseScratch = checked(
                semanticCurrentScratch +
                graphCurrentScratch +
                tileReceipt.OutputScratchBytes +
                restrictionReceipt.PeakAggregateStageScratchBytes);
            long edgeBuildPhaseMemory = checked(
                semanticStore.CurrentMemoryBytes +
                graph.PeakAggregateMemoryBytes);
            long tileWritePhaseMemory = checked(
                semanticStore.CurrentMemoryBytes +
                graph.CurrentMemoryBytes +
                tileReceipt.PeakWorkerMemoryBytes);
            long graphAndTilePhaseMemory = Math.Max(
                edgeBuildPhaseMemory,
                tileWritePhaseMemory);
            long restrictionPhaseMemory = checked(
                semanticStore.CurrentMemoryBytes +
                graph.CurrentMemoryBytes +
                restrictionReceipt.PeakAggregateStageMemoryBytes);
            var resourceMetrics = new ManagedRoadGraphResourceMetrics(
                sourcePeakMemory,
                semanticPhasePeakMemory,
                graphAndTilePhaseMemory,
                restrictionPhaseMemory,
                sourceScratchHighWater,
                semanticPhasePeakScratch,
                graphAndTilePhaseScratch,
                restrictionPhaseScratch)
            {
                SelectedDop = selectedDop,
                PerWorkerMemoryReservationBytes = perWorkerMemoryBytes,
                PerWorkerScratchReservationBytes = perWorkerScratchBytes,
            };
            return new ManagedRoadGraphBuildResult(
                tileResult,
                pbfMetrics,
                resourceMetrics.MemoryHighWaterMarkBytes,
                resourceMetrics.ScratchHighWaterMarkBytes,
                pbfDuration,
                semanticDuration,
                edgeDuration + tileDuration + enhanceDuration + restrictionDuration,
                durations)
            {
                FrontierMetrics = frontierMetrics,
                ResourceMetrics = resourceMetrics,
            };
        }
        finally
        {
            semanticStore.Dispose();
        }
    }
}
