using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads;

public sealed record ManagedRoadGraphBuildRequest(
    IReadOnlyList<string> OsmPbfPaths,
    string WorkingDirectory,
    string OutputDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    TileBuilderConfig? TileBuilderConfig = null);

public sealed record ManagedRoadGraphBuildResult(
    TileBuilderResult TileBuilderResult,
    StreamingOsmPbfReadMetrics PbfMetrics,
    long PeakIntermediateMemoryBytes,
    long ScratchDiskHighWaterMarkBytes);

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

        using (var source = await StoredOsmPbfEntitySource.CreateAsync(
                   request.OsmPbfPaths,
                   intermediateDirectory,
                   request.StorageMode,
                   request.MemoryBudgetBytes,
                   request.ScratchDiskBudgetBytes,
                   cancellationToken)
               .ConfigureAwait(false))
        {
            osmdata = await Task.Run(
                    () => parser.Parse(source, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            pbfMetrics = source.ReadResult.Metrics;
            peakIntermediateMemoryBytes = source.PeakIntermediateMemoryBytes;
            scratchDiskHighWaterMarkBytes = source.ScratchHighWaterMarkBytes;
        }

        cancellationToken.ThrowIfCancellationRequested();
        TileBuilderResult tileResult = await Task.Run(
                () => TileBuilder.BuildParsedTileSet(
                    parser,
                    osmdata,
                    request.OutputDirectory,
                    tileBuilderConfig,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        return new ManagedRoadGraphBuildResult(
            tileResult,
            pbfMetrics,
            peakIntermediateMemoryBytes,
            scratchDiskHighWaterMarkBytes);
    }
}
