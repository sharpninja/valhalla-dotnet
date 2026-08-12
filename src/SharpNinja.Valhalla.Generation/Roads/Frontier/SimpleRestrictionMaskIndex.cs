using System.Globalization;
using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record SimpleRestrictionMaskIndexOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal readonly record struct SimpleRestrictionMaskRecord(
    GraphId StartNode,
    long EdgeRecordId,
    bool Forward,
    uint Mask,
    long CanonicalOrdinal);

internal sealed class SimpleRestrictionMaskIndex : IDisposable
{
    private readonly IntermediateSequenceStore<SimpleRestrictionMaskRecord> input;
    private readonly ExternalSequenceSortResult<SimpleRestrictionMaskRecord> sorted;
    private bool disposed;

    private SimpleRestrictionMaskIndex(
        IntermediateSequenceStore<SimpleRestrictionMaskRecord> input,
        ExternalSequenceSortResult<SimpleRestrictionMaskRecord> sorted)
    {
        this.input = input;
        this.sorted = sorted;
    }

    internal long Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return sorted.Output.State.RecordCount;
        }
    }

    internal bool TryGetMask(
        GraphId startNode,
        long edgeRecordId,
        bool forward,
        out uint mask)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = sorted.Output.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            SimpleRestrictionMaskRecord candidate = sorted.Output.Read(middle);
            if (CompareKey(candidate, startNode, edgeRecordId, forward) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        mask = 0;
        while (low < sorted.Output.State.RecordCount)
        {
            SimpleRestrictionMaskRecord candidate = sorted.Output.Read(low);
            if (CompareKey(candidate, startNode, edgeRecordId, forward) != 0)
            {
                break;
            }

            mask |= candidate.Mask;
            low++;
        }

        return mask != 0;
    }

    internal static async ValueTask<SimpleRestrictionMaskIndex> BuildAsync(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuildResult graph,
        SimpleRestrictionMaskIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        string root = Path.GetFullPath(options.WorkingDirectory);
        Directory.CreateDirectory(root);
        long inputMemory = options.MemoryBudgetBytes / 4;
        long outputMemory = options.MemoryBudgetBytes / 4;
        long sortMemory = checked(options.MemoryBudgetBytes - inputMemory - outputMemory);
        long inputScratch = options.ScratchDiskBudgetBytes / 4;
        long outputScratch = options.ScratchDiskBudgetBytes / 4;
        long sortScratch = checked(options.ScratchDiskBudgetBytes - inputScratch - outputScratch);

        IntermediateSequenceStore<SimpleRestrictionMaskRecord>? input = null;
        ExternalSequenceSortResult<SimpleRestrictionMaskRecord>? sorted = null;
        try
        {
            input = new IntermediateSequenceStore<SimpleRestrictionMaskRecord>(
                new IntermediateSequenceStoreOptions(
                    root,
                    "simple-restriction-mask-input",
                    options.StorageMode,
                    inputMemory,
                    inputScratch,
                    options.SegmentSizeBytes));
            EmitMasks(semanticStore, graph, input, cancellationToken);
            await input.CompleteAsync(cancellationToken).ConfigureAwait(false);

            sorted = await ExternalSequenceSorter.SortAsync(
                    input,
                    new IntermediateSequenceStoreOptions(
                        root,
                        "simple-restriction-mask-index",
                        options.StorageMode,
                        outputMemory,
                        outputScratch,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        root,
                        "simple-restriction-mask-sort",
                        sortMemory,
                        sortScratch),
                    Compare,
                    cancellationToken)
                .ConfigureAwait(false);

            var result = new SimpleRestrictionMaskIndex(input, sorted);
            input = null;
            sorted = null;
            return result;
        }
        catch
        {
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

        sorted.Dispose();
        input.Dispose();
        disposed = true;
    }

    private static void EmitMasks(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuildResult graph,
        IIntermediateSequenceStore<SimpleRestrictionMaskRecord> destination,
        CancellationToken cancellationToken)
    {
        long canonicalOrdinal = 0;
        for (long restrictionOrdinal = 0;
             restrictionOrdinal < semanticStore.RestrictionCount;
             restrictionOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GenerationRestrictionRecord restriction =
                semanticStore.ReadRestriction(restrictionOrdinal);
            if (restriction.ViaCount != 1 ||
                !TryGetSimpleRestrictionType(
                    semanticStore.ReadTags(restriction.TagReference),
                    out RestrictionType type))
            {
                continue;
            }

            GenerationRestrictionViaRecord via =
                semanticStore.ReadRestrictionVia(restriction.ViaOffset);
            if (via.MemberType != OsmMemberType.Node ||
                !graph.TryGetGraphId(via.MemberId, out GraphId viaNodeId) ||
                !graph.TryGetGraphNode(viaNodeId, out GenerationGraphNodeRecord graphNode))
            {
                continue;
            }

            uint mask = CreateMask(
                restriction,
                type,
                graph,
                graphNode,
                cancellationToken);
            if (mask == 0)
            {
                continue;
            }

            for (int localIndex = 0; localIndex < graphNode.IncidentEdgeCount; localIndex++)
            {
                NodeEdgeIncidenceRecord incidence = graph.ReadIncidence(
                    checked(graphNode.IncidentEdgeOffset + localIndex));
                GenerationEdgeRecord edge =
                    ReadEdge(graph, incidence.EdgeRecordId);
                if (edge.WayId != restriction.FromWayId)
                {
                    continue;
                }

                bool forward = incidence.Role == EdgeEndpointRole.Target;
                GraphId startNode = forward ? edge.SourceNode : edge.TargetNode;
                destination.Append(
                    new SimpleRestrictionMaskRecord(
                        startNode,
                        edge.EdgeRecordId,
                        forward,
                        mask,
                        canonicalOrdinal++));
            }
        }
    }

    private static uint CreateMask(
        GenerationRestrictionRecord restriction,
        RestrictionType type,
        PooledRoadEdgeBuildResult graph,
        GenerationGraphNodeRecord graphNode,
        CancellationToken cancellationToken)
    {
        bool onlyRestriction = type is
            RestrictionType.OnlyRightTurn or
            RestrictionType.OnlyLeftTurn or
            RestrictionType.OnlyStraightOn;
        uint mask = 0;
        for (int localIndex = 0; localIndex < graphNode.IncidentEdgeCount; localIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NodeEdgeIncidenceRecord incidence = graph.ReadIncidence(
                checked(graphNode.IncidentEdgeOffset + localIndex));
            GenerationEdgeRecord edge =
                ReadEdge(graph, incidence.EdgeRecordId);
            bool matchesToWay = edge.WayId == restriction.ToWayId;
            bool restricted = onlyRestriction ? !matchesToWay : matchesToWay;
            if (!restricted ||
                localIndex >= checked((int)GraphConstants.MaxTurnRestrictionEdges))
            {
                continue;
            }

            mask |= 1U << localIndex;
            if (!onlyRestriction)
            {
                break;
            }
        }

        return mask;
    }

    private static bool TryGetSimpleRestrictionType(
        IReadOnlyDictionary<string, string> tags,
        out RestrictionType type)
    {
        type = default;
        if (tags.ContainsKey("except"))
        {
            return false;
        }

        foreach (string key in tags.Keys)
        {
            if (key.StartsWith("restriction:", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!tags.TryGetValue("restriction", out string? value) ||
            !byte.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out byte encoded))
        {
            return false;
        }

        type = (RestrictionType)encoded;
        return type is
            RestrictionType.NoLeftTurn or
            RestrictionType.NoRightTurn or
            RestrictionType.NoStraightOn or
            RestrictionType.NoUTurn or
            RestrictionType.NoEntry or
            RestrictionType.NoExit or
            RestrictionType.NoTurn or
            RestrictionType.OnlyRightTurn or
            RestrictionType.OnlyLeftTurn or
            RestrictionType.OnlyStraightOn;
    }

    private static GenerationEdgeRecord ReadEdge(
        PooledRoadEdgeBuildResult graph,
        long edgeRecordId) =>
        graph.TryReadEdgeByRecordId(edgeRecordId, out GenerationEdgeRecord edge)
            ? edge
            : throw new InvalidDataException(
                $"Durable edge record {edgeRecordId} was not found.");

    private static int Compare(
        SimpleRestrictionMaskRecord left,
        SimpleRestrictionMaskRecord right)
    {
        int comparison = left.StartNode.CompareTo(right.StartNode);
        if (comparison == 0)
        {
            comparison = left.EdgeRecordId.CompareTo(right.EdgeRecordId);
        }

        if (comparison == 0)
        {
            comparison = left.Forward.CompareTo(right.Forward);
        }

        if (comparison == 0)
        {
            comparison = left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal);
        }

        return comparison;
    }

    private static int CompareKey(
        SimpleRestrictionMaskRecord candidate,
        GraphId startNode,
        long edgeRecordId,
        bool forward)
    {
        int comparison = candidate.StartNode.CompareTo(startNode);
        if (comparison == 0)
        {
            comparison = candidate.EdgeRecordId.CompareTo(edgeRecordId);
        }

        if (comparison == 0)
        {
            comparison = candidate.Forward.CompareTo(forward);
        }

        return comparison;
    }

    private static void ValidateOptions(SimpleRestrictionMaskIndexOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);
        int minimumPartitionBytes = Unsafe.SizeOf<SimpleRestrictionMaskRecord>();
        if (options.MemoryBudgetBytes / 4 < minimumPartitionBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The simple-restriction memory budget cannot fit one record per partition.");
        }

        if (options.ScratchDiskBudgetBytes / 4 < minimumPartitionBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The simple-restriction scratch budget cannot fit one record per partition.");
        }
    }
}
