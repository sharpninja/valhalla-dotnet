using System.Collections.Concurrent;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Differential;
using SharpNinja.Valhalla.Mjolnir;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Elevation;

public sealed class ManagedElevationDatasetBuilder : IElevationDatasetBuilder
{
    public async ValueTask<ElevationDatasetBuildResult> BuildAsync(
        ElevationDatasetBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string tileDirectory = Path.GetFullPath(request.GraphTileDirectory);
        string elevationDirectory = Path.GetFullPath(request.ElevationDirectory);
        GraphReader catalogReader = new(
            new GraphReader.Config { TileDir = tileDirectory });
        GraphId[] tileIds = catalogReader.GetTileSet()
            .OrderBy(id => id.Level())
            .ThenBy(id => id.Tileid())
            .ToArray();
        if (tileIds.Length == 0)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                "The graph tile directory does not contain any graph tiles");
        }

        long bytesWritten = 0;
        long scratchHighWater = 0;
        int nodeCount = 0;
        int uniqueEdgeInfoCount = 0;
        int encodedElevationCount = 0;
        int activeWorkers = 0;
        int peakConcurrency = 0;
        var diagnostics = new ConcurrentQueue<ElevationDatasetDiagnostic>();
        using var writeGate = new SemaphoreSlim(1, 1);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism,
        };

        await System.Threading.Tasks.Parallel.ForEachAsync(
            tileIds,
            parallelOptions,
            async (tileId, token) =>
            {
                int active = Interlocked.Increment(ref activeWorkers);
                UpdateMaximum(ref peakConcurrency, active);
                try
                {
                    TileElevationBuildResult result = await ProcessTileAsync(
                        tileDirectory,
                        elevationDirectory,
                        tileId,
                        request.ScratchDiskBudgetBytes,
                        writeGate,
                        diagnostics,
                        token);
                    Interlocked.Add(ref bytesWritten, result.BytesWritten);
                    Interlocked.Add(ref nodeCount, result.NodeCount);
                    Interlocked.Add(
                        ref uniqueEdgeInfoCount,
                        result.UniqueEdgeInfoCount);
                    Interlocked.Add(
                        ref encodedElevationCount,
                        result.EncodedElevationCount);
                    UpdateMaximum(
                        ref scratchHighWater,
                        result.ScratchDiskHighWaterBytes);
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            });

        string treeHash = await new GenerationOutputTreeHasher().ComputeSha256Async(
            tileDirectory,
            cancellationToken);
        return new ElevationDatasetBuildResult(
            tileDirectory,
            tileIds.Length,
            nodeCount,
            uniqueEdgeInfoCount,
            encodedElevationCount,
            bytesWritten,
            scratchHighWater,
            peakConcurrency,
            treeHash,
            diagnostics
                .OrderBy(item => item.TileId)
                .ThenBy(item => item.Code)
                .ThenBy(item => item.SourcePath, StringComparer.Ordinal)
                .ToArray());
    }

    private static async ValueTask<TileElevationBuildResult> ProcessTileAsync(
        string tileDirectory,
        string elevationDirectory,
        GraphId tileId,
        long scratchDiskBudgetBytes,
        SemaphoreSlim writeGate,
        ConcurrentQueue<ElevationDatasetDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphReader reader = new(new GraphReader.Config { TileDir = tileDirectory });
        GraphTile tile = reader.GetGraphTile(tileId)
            ?? throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidGraphTile,
                $"Graph tile {tileId} could not be opened");
        var builder = new GraphTileBuilder(tile);
        using IElevationSampleSource source = new HgtElevationSource(elevationDirectory);

        PointLL baseCoordinate = builder.Header().BaseLl();
        for (int nodeIndex = 0; nodeIndex < builder.Nodes.Count; nodeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NodeInfo node = builder.NodeBuilder(nodeIndex);
            double elevation = source.Sample(node.LatLng(baseCoordinate));
            node.SetElevation((float)elevation);
            builder.SetNodeBuilder(nodeIndex, node);
        }

        var computations = new Dictionary<uint, EdgeElevationComputation>();
        var updates = new Dictionary<uint, EdgeInfoElevationData>();
        for (int edgeIndex = 0; edgeIndex < builder.DirectedEdges.Count; edgeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectedEdge edge = builder.DirectedEdgeBuilder(edgeIndex);
            uint originalOffset = checked((uint)edge.EdgeInfoOffset);
            if (!computations.TryGetValue(
                    originalOffset,
                    out EdgeElevationComputation computation))
            {
                EdgeInfo edgeInfo = builder.EdgeInfoFor(edge);
                computation = ValhallaElevationAlgorithms.Compute(
                    source,
                    edgeInfo.Shape(),
                    edge.Length,
                    edge.Bridge || edge.Tunnel || edge.Use == Use.Ferry);
                computations.Add(originalOffset, computation);
                updates.Add(
                    originalOffset,
                    new EdgeInfoElevationData(
                        computation.MeanElevation,
                        computation.EncodedElevation));
                if (computation.EncodingClamped)
                {
                    diagnostics.Enqueue(new ElevationDatasetDiagnostic(
                        ElevationDatasetDiagnosticCode.ExcessiveElevationDifference,
                        "An encoded elevation delta exceeded one-byte precision and was clamped",
                        TileId: tileId.Value));
                }
            }

            uint weightedGrade = edge.Forward
                ? computation.ForwardWeightedGrade
                : computation.ReverseWeightedGrade;
            float maximumUp = edge.Forward
                ? computation.ForwardMaximumUp
                : computation.ReverseMaximumUp;
            float maximumDown = edge.Forward
                ? computation.ForwardMaximumDown
                : computation.ReverseMaximumDown;
            if (edge.Bridge || edge.Tunnel)
            {
                weightedGrade = Math.Clamp(weightedGrade, 4u, 8u);
                maximumUp = Math.Min(3.0f, maximumUp);
                maximumDown = Math.Max(-3.0f, maximumDown);
            }

            edge.SetWeightedGrade(weightedGrade);
            edge.SetMaxUpSlope(maximumUp);
            edge.SetMaxDownSlope(maximumDown);
            builder.SetDirectedEdgeBuilder(edgeIndex, edge);
        }

        int applied = builder.ApplyElevationData(updates);
        if (applied != updates.Count)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidGraphTile,
                $"Only {applied} of {updates.Count} edge-info elevations were applied");
        }

        byte[] output = builder.StoreTileData();
        if (output.LongLength > scratchDiskBudgetBytes)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.ScratchDiskBudgetExceeded,
                $"Graph tile {tileId} requires {output.LongLength} scratch bytes; " +
                $"the configured limit is {scratchDiskBudgetBytes}");
        }

        string relativePath = GraphTile.FileSuffix(tileId.TileBase());
        string outputPath = Path.Combine(tileDirectory, relativePath);
        string temporaryPath = outputPath + $".elevation-{Guid.NewGuid():N}.tmp";
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(
                temporaryPath,
                output,
                cancellationToken);
            File.Move(temporaryPath, outputPath, overwrite: true);
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
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.GraphTileWriteFailed,
                $"Graph tile {tileId} could not be atomically replaced",
                exception);
        }
        finally
        {
            writeGate.Release();
        }

        return new TileElevationBuildResult(
            builder.Nodes.Count,
            computations.Count,
            computations.Values.Count(value => value.EncodedElevation.Count > 0),
            output.LongLength,
            output.LongLength);
    }

    private static void ValidateRequest(ElevationDatasetBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GraphTileDirectory) ||
            !Directory.Exists(request.GraphTileDirectory))
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                "GraphTileDirectory must identify an existing directory");
        }

        if (string.IsNullOrWhiteSpace(request.ElevationDirectory) ||
            !Directory.Exists(request.ElevationDirectory))
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                "ElevationDirectory must identify an existing directory");
        }

        if (request.MaxDegreeOfParallelism <= 0)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                "MaxDegreeOfParallelism must be positive");
        }

        if (request.ScratchDiskBudgetBytes <= 0)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                "ScratchDiskBudgetBytes must be positive");
        }

        if (!request.DeterministicOutput)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                "The qualified 3.8.3 writer requires deterministic output");
        }

        RejectReparsePoint(request.GraphTileDirectory, nameof(request.GraphTileDirectory));
        RejectReparsePoint(request.ElevationDirectory, nameof(request.ElevationDirectory));
    }

    private static void RejectReparsePoint(string path, string parameterName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidConfiguration,
                $"{parameterName} cannot be a symbolic link or reparse point");
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            int prior = Interlocked.CompareExchange(ref target, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            long prior = Interlocked.CompareExchange(ref target, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }

    private sealed record TileElevationBuildResult(
        int NodeCount,
        int UniqueEdgeInfoCount,
        int EncodedElevationCount,
        long BytesWritten,
        long ScratchDiskHighWaterBytes);
}
