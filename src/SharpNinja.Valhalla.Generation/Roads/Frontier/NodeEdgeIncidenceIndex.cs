using System.Runtime.CompilerServices;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record NodeEdgeIncidenceIndexOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed class NodeEdgeIncidenceIndex : IDisposable
{
    private readonly IntermediateSequenceStore<NodeEdgeIncidenceRecord> input;
    private readonly ExternalSequenceSortResult<NodeEdgeIncidenceRecord> sorted;
    private readonly IntermediateSequenceStore<GenerationGraphNodeRecord> graphNodes;
    private bool disposed;

    private NodeEdgeIncidenceIndex(
        IntermediateSequenceStore<NodeEdgeIncidenceRecord> input,
        ExternalSequenceSortResult<NodeEdgeIncidenceRecord> sorted,
        IntermediateSequenceStore<GenerationGraphNodeRecord> graphNodes,
        IntermediateSequenceManifest graphNodeManifest)
    {
        this.input = input;
        this.sorted = sorted;
        this.graphNodes = graphNodes;
        GraphNodeManifest = graphNodeManifest;
    }

    internal long IncidenceCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sorted.Output.State.RecordCount;
        }
    }

    internal long GraphNodeCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return graphNodes.State.RecordCount;
        }
    }

    internal IntermediateSequenceManifest IncidenceManifest
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sorted.Receipt.OutputManifest;
        }
    }

    internal IntermediateSequenceManifest GraphNodeManifest { get; }

    internal ExternalSequenceSortReceipt SortReceipt
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sorted.Receipt;
        }
    }

    internal NodeEdgeIncidenceRecord ReadIncidence(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return sorted.Output.Read(ordinal);
    }

    internal GenerationGraphNodeRecord ReadGraphNode(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return graphNodes.Read(ordinal);
    }

    internal bool TryGetGraphNode(
        GraphId nodeId,
        out GenerationGraphNodeRecord graphNode)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = graphNodes.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            GenerationGraphNodeRecord candidate = graphNodes.Read(middle);
            if (candidate.NodeId < nodeId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < graphNodes.State.RecordCount)
        {
            GenerationGraphNodeRecord candidate = graphNodes.Read(low);
            if (candidate.NodeId == nodeId)
            {
                graphNode = candidate;
                return true;
            }
        }

        graphNode = default;
        return false;
    }

    internal static async ValueTask<NodeEdgeIncidenceIndex> BuildAsync(
        IFrontierEdgeSource edges,
        NodeEdgeIncidenceIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (edges.EdgeCount < 0)
        {
            throw new InvalidDataException("The frontier edge count cannot be negative.");
        }

        int incidenceSize = Unsafe.SizeOf<NodeEdgeIncidenceRecord>();
        int graphNodeSize = Unsafe.SizeOf<GenerationGraphNodeRecord>();
        BudgetPartition memory = BudgetPartition.Create(
            options.MemoryBudgetBytes,
            incidenceSize,
            incidenceSize * 2L,
            incidenceSize,
            graphNodeSize);
        BudgetPartition scratch = BudgetPartition.Create(
            options.ScratchDiskBudgetBytes,
            incidenceSize,
            incidenceSize * 8L,
            incidenceSize,
            graphNodeSize);

        string indexDirectory = Path.Combine(
            options.WorkingDirectory,
            "node-edge-incidence-index");
        Directory.CreateDirectory(indexDirectory);

        IntermediateSequenceStore<NodeEdgeIncidenceRecord>? input = null;
        ExternalSequenceSortResult<NodeEdgeIncidenceRecord>? sorted = null;
        IntermediateSequenceStore<GenerationGraphNodeRecord>? graphNodes = null;
        try
        {
            input = new IntermediateSequenceStore<NodeEdgeIncidenceRecord>(
                new IntermediateSequenceStoreOptions(
                    indexDirectory,
                    "raw-incidences",
                    options.StorageMode,
                    memory.First,
                    scratch.First,
                    options.SegmentSizeBytes));
            EmitEndpointIncidences(edges, input, cancellationToken);
            await input.CompleteAsync(cancellationToken).ConfigureAwait(false);

            sorted = await ExternalSequenceSorter.SortAsync(
                    input,
                    new IntermediateSequenceStoreOptions(
                        indexDirectory,
                        "ordered-incidences",
                        options.StorageMode,
                        memory.Third,
                        scratch.Third,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        indexDirectory,
                        "node-edge-incidence-sort",
                        memory.Second,
                        scratch.Second),
                    NodeEdgeIncidenceOrdering.Compare,
                    cancellationToken)
                .ConfigureAwait(false);

            graphNodes = new IntermediateSequenceStore<GenerationGraphNodeRecord>(
                new IntermediateSequenceStoreOptions(
                    indexDirectory,
                    "graph-nodes",
                    options.StorageMode,
                    memory.Fourth,
                    scratch.Fourth,
                    options.SegmentSizeBytes));
            BuildGraphNodeRanges(sorted.Output, graphNodes, cancellationToken);
            IntermediateSequenceManifest graphNodeManifest =
                await graphNodes.CompleteAsync(cancellationToken).ConfigureAwait(false);

            var result = new NodeEdgeIncidenceIndex(
                input,
                sorted,
                graphNodes,
                graphNodeManifest);
            input = null;
            sorted = null;
            graphNodes = null;
            return result;
        }
        catch
        {
            graphNodes?.Dispose();
            sorted?.Dispose();
            input?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        graphNodes.Dispose();
        sorted.Dispose();
        input.Dispose();
        disposed = true;
    }

    private static void EmitEndpointIncidences(
        IFrontierEdgeSource edges,
        IIntermediateSequenceStore<NodeEdgeIncidenceRecord> destination,
        CancellationToken cancellationToken)
    {
        for (long edgeOrdinal = 0; edgeOrdinal < edges.EdgeCount; edgeOrdinal++)
        {
            if ((edgeOrdinal & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            GenerationEdgeRecord edge = edges.ReadEdge(edgeOrdinal);
            if (!edge.SourceNode.IsValid() || !edge.TargetNode.IsValid())
            {
                throw new InvalidDataException(
                    $"Frontier edge {edge.EdgeRecordId} has an invalid endpoint.");
            }

            long firstCanonicalOrdinal = checked(edge.CanonicalOrdinal * 2);
            destination.Append(CreateIncidence(
                edge,
                EdgeEndpointRole.Source,
                firstCanonicalOrdinal));
            destination.Append(CreateIncidence(
                edge,
                EdgeEndpointRole.Target,
                checked(firstCanonicalOrdinal + 1)));
        }
    }

    private static NodeEdgeIncidenceRecord CreateIncidence(
        GenerationEdgeRecord edge,
        EdgeEndpointRole role,
        long canonicalOrdinal)
    {
        GraphId nodeId = role == EdgeEndpointRole.Source
            ? edge.SourceNode
            : edge.TargetNode;
        uint access = role == EdgeEndpointRole.Source
            ? edge.ForwardAccess
            : edge.ReverseAccess;
        return new NodeEdgeIncidenceRecord(
            nodeId,
            edge.EdgeRecordId,
            role,
            DriveForward: (access & GraphConstants.AutoAccess) != 0,
            edge.Importance,
            edge.HasNames,
            edge.Shape.Offset,
            edge.SourceNode,
            edge.TargetNode,
            canonicalOrdinal);
    }

    private static void BuildGraphNodeRanges(
        IIntermediateSequenceStore<NodeEdgeIncidenceRecord> incidences,
        IIntermediateSequenceStore<GenerationGraphNodeRecord> graphNodes,
        CancellationToken cancellationToken)
    {
        long offset = 0;
        while (offset < incidences.State.RecordCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId nodeId = incidences.Read(offset).NodeId;
            long end = offset + 1;
            while (end < incidences.State.RecordCount)
            {
                if ((end & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (incidences.Read(end).NodeId != nodeId)
                {
                    break;
                }

                end++;
            }

            graphNodes.Append(new GenerationGraphNodeRecord(
                nodeId,
                offset,
                checked((int)(end - offset))));
            offset = end;
        }
    }

    private static void ValidateOptions(NodeEdgeIncidenceIndexOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);
    }

    private readonly record struct BudgetPartition(
        long First,
        long Second,
        long Third,
        long Fourth)
    {
        internal static BudgetPartition Create(
            long total,
            long firstMinimum,
            long secondMinimum,
            long thirdMinimum,
            long fourthMinimum)
        {
            long minimum = checked(
                firstMinimum +
                secondMinimum +
                thirdMinimum +
                fourthMinimum);
            if (total < minimum)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"The node-edge incidence budget of {total} bytes cannot fit " +
                    $"the required {minimum} bytes.");
            }

            long remainder = total - minimum;
            long firstExtra = remainder / 5;
            long secondExtra = remainder * 2 / 5;
            long thirdExtra = remainder / 5;
            long fourthExtra = remainder - firstExtra - secondExtra - thirdExtra;
            return new BudgetPartition(
                checked(firstMinimum + firstExtra),
                checked(secondMinimum + secondExtra),
                checked(thirdMinimum + thirdExtra),
                checked(fourthMinimum + fourthExtra));
        }
    }
}

internal static class NodeEdgeIncidenceOrdering
{
    internal static int Compare(
        NodeEdgeIncidenceRecord x,
        NodeEdgeIncidenceRecord y)
    {
        int comparison = CompareGraphIds(x.NodeId, y.NodeId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = y.DriveForward.CompareTo(x.DriveForward);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.Importance.CompareTo(y.Importance);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = y.HasNames.CompareTo(x.HasNames);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.ShapeOffset.CompareTo(y.ShapeOffset);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareGraphIds(x.SourceNode, y.SourceNode);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareGraphIds(x.TargetNode, y.TargetNode);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.EdgeRecordId.CompareTo(y.EdgeRecordId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.Role.CompareTo(y.Role);
        return comparison != 0
            ? comparison
            : x.CanonicalOrdinal.CompareTo(y.CanonicalOrdinal);
    }

    internal static int CompareGraphIds(GraphId x, GraphId y)
    {
        int comparison = x.Level().CompareTo(y.Level());
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.Tileid().CompareTo(y.Tileid());
        return comparison != 0
            ? comparison
            : x.Id().CompareTo(y.Id());
    }
}
