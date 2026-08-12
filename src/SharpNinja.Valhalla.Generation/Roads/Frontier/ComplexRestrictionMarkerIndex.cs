using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record ComplexRestrictionMarkerIndexOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal readonly record struct RestrictionWayEndpointRecord(
    long WayId,
    GraphId NodeId,
    GraphId OtherNodeId,
    long EdgeRecordId,
    EdgeEndpointRole Role,
    long CanonicalOrdinal);

internal readonly record struct ComplexRestrictionMarkerRecord(
    GraphId StartNode,
    long EdgeRecordId,
    bool Forward,
    uint StartModes,
    uint EndModes,
    bool PartOfComplexRestriction,
    long CanonicalOrdinal);

internal readonly record struct ComplexRestrictionEdgeMarker(
    uint StartModes,
    uint EndModes,
    bool PartOfComplexRestriction);

internal sealed class ComplexRestrictionMarkerIndex : IDisposable
{
    private readonly ExternalSequenceSortResult<ComplexRestrictionMarkerRecord> sorted;
    private bool disposed;

    private ComplexRestrictionMarkerIndex(
        ExternalSequenceSortResult<ComplexRestrictionMarkerRecord> sorted)
    {
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

    internal bool TryGetMarker(
        GraphId startNode,
        long edgeRecordId,
        bool forward,
        out ComplexRestrictionEdgeMarker marker)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = sorted.Output.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            ComplexRestrictionMarkerRecord candidate = sorted.Output.Read(middle);
            if (CompareKey(candidate, startNode, edgeRecordId, forward) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        uint startModes = 0;
        uint endModes = 0;
        bool partOf = false;
        bool found = false;
        while (low < sorted.Output.State.RecordCount)
        {
            ComplexRestrictionMarkerRecord candidate = sorted.Output.Read(low);
            if (CompareKey(candidate, startNode, edgeRecordId, forward) != 0)
            {
                break;
            }

            startModes |= candidate.StartModes;
            endModes |= candidate.EndModes;
            partOf |= candidate.PartOfComplexRestriction;
            found = true;
            low++;
        }

        marker = new ComplexRestrictionEdgeMarker(
            startModes,
            endModes,
            partOf);
        return found;
    }

    internal static async ValueTask<ComplexRestrictionMarkerIndex> BuildAsync(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuildResult graph,
        ComplexRestrictionMarkerIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        string root = Path.GetFullPath(options.WorkingDirectory);
        Directory.CreateDirectory(root);
        long partitionMemory = options.MemoryBudgetBytes / 4;
        long sortMemory = checked(options.MemoryBudgetBytes - (partitionMemory * 2));
        long partitionScratch = options.ScratchDiskBudgetBytes / 4;
        long sortScratch = checked(options.ScratchDiskBudgetBytes - (partitionScratch * 2));

        ExternalSequenceSortResult<RestrictionWayEndpointRecord>? endpoints = null;
        IntermediateSequenceStore<ComplexRestrictionMarkerRecord>? markerInput = null;
        ExternalSequenceSortResult<ComplexRestrictionMarkerRecord>? markers = null;
        try
        {
            using (var endpointInput =
                   new IntermediateSequenceStore<RestrictionWayEndpointRecord>(
                       new IntermediateSequenceStoreOptions(
                           root,
                           "restriction-way-endpoints-input",
                           options.StorageMode,
                           partitionMemory,
                           partitionScratch,
                           options.SegmentSizeBytes)))
            {
                EmitWayEndpoints(graph, endpointInput, cancellationToken);
                await endpointInput.CompleteAsync(cancellationToken).ConfigureAwait(false);
                endpoints = await ExternalSequenceSorter.SortAsync(
                        endpointInput,
                        new IntermediateSequenceStoreOptions(
                            root,
                            "restriction-way-endpoints-index",
                            options.StorageMode,
                            partitionMemory,
                            partitionScratch,
                            options.SegmentSizeBytes),
                        new ExternalSequenceSortOptions(
                            root,
                            "restriction-way-endpoints-sort",
                            sortMemory,
                            sortScratch),
                        CompareEndpoints,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            markerInput =
                new IntermediateSequenceStore<ComplexRestrictionMarkerRecord>(
                    new IntermediateSequenceStoreOptions(
                        root,
                        "complex-restriction-markers-input",
                        options.StorageMode,
                        partitionMemory,
                        partitionScratch,
                        options.SegmentSizeBytes));
            EmitMarkers(
                semanticStore,
                endpoints.Output,
                markerInput,
                cancellationToken);
            await markerInput.CompleteAsync(cancellationToken).ConfigureAwait(false);
            endpoints.Dispose();
            endpoints = null;

            markers = await ExternalSequenceSorter.SortAsync(
                    markerInput,
                    new IntermediateSequenceStoreOptions(
                        root,
                        "complex-restriction-markers-index",
                        options.StorageMode,
                        partitionMemory,
                        partitionScratch,
                        options.SegmentSizeBytes),
                    new ExternalSequenceSortOptions(
                        root,
                        "complex-restriction-markers-sort",
                        sortMemory,
                        sortScratch),
                    CompareMarkers,
                    cancellationToken)
                .ConfigureAwait(false);
            markerInput.Dispose();
            markerInput = null;

            var result = new ComplexRestrictionMarkerIndex(markers);
            markers = null;
            return result;
        }
        catch
        {
            markers?.Dispose();
            markerInput?.Dispose();
            endpoints?.Dispose();
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Preserve the causal build failure when best-effort cleanup is blocked.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the causal build failure when best-effort cleanup is blocked.
            }

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
        disposed = true;
    }

    private static void EmitWayEndpoints(
        PooledRoadEdgeBuildResult graph,
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> destination,
        CancellationToken cancellationToken)
    {
        long canonicalOrdinal = 0;
        for (long edgeOrdinal = 0; edgeOrdinal < graph.EdgeCount; edgeOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GenerationEdgeRecord edge = graph.ReadEdge(edgeOrdinal);
            destination.Append(
                new RestrictionWayEndpointRecord(
                    edge.WayId,
                    edge.SourceNode,
                    edge.TargetNode,
                    edge.EdgeRecordId,
                    EdgeEndpointRole.Source,
                    canonicalOrdinal++));
            destination.Append(
                new RestrictionWayEndpointRecord(
                    edge.WayId,
                    edge.TargetNode,
                    edge.SourceNode,
                    edge.EdgeRecordId,
                    EdgeEndpointRole.Target,
                    canonicalOrdinal++));
        }
    }

    private static void EmitMarkers(
        CompactOsmSemanticStore semanticStore,
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        IIntermediateSequenceStore<ComplexRestrictionMarkerRecord> destination,
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
            if (!ComplexRestrictionSemantics.TryProject(
                    semanticStore,
                    restriction,
                    out ComplexRestrictionSemanticProjection projection))
            {
                continue;
            }

            if (projection.IncludeFromWay)
            {
                EmitPartOfMarkers(
                    endpoints,
                    restriction.FromWayId,
                    destination,
                    ref canonicalOrdinal,
                    cancellationToken);
            }

            EmitPartOfMarkers(
                endpoints,
                restriction.ToWayId,
                destination,
                ref canonicalOrdinal,
                cancellationToken);
            EmitWayModeMarkers(
                endpoints,
                restriction.FromWayId,
                projection.Modes,
                startRestriction: true,
                destination,
                ref canonicalOrdinal,
                cancellationToken);
            EmitWayModeMarkers(
                endpoints,
                restriction.ToWayId,
                projection.Modes,
                startRestriction: false,
                destination,
                ref canonicalOrdinal,
                cancellationToken);

            if (!projection.ViaWay)
            {
                continue;
            }

            for (long viaOrdinal = restriction.ViaOffset;
                 viaOrdinal < restriction.ViaOffset + restriction.ViaCount;
                 viaOrdinal++)
            {
                GenerationRestrictionViaRecord via =
                    semanticStore.ReadRestrictionVia(viaOrdinal);
                if (via.MemberType == OsmMemberType.Way)
                {
                    EmitPartOfMarkers(
                        endpoints,
                        via.MemberId,
                        destination,
                        ref canonicalOrdinal,
                        cancellationToken);
                }
            }
        }
    }

    private static void EmitWayModeMarkers(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long wayId,
        uint modes,
        bool startRestriction,
        IIntermediateSequenceStore<ComplexRestrictionMarkerRecord> destination,
        ref long canonicalOrdinal,
        CancellationToken cancellationToken)
    {
        (long start, long end) = GetWayRange(endpoints, wayId);
        for (long ordinal = start; ordinal < end; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestrictionWayEndpointRecord endpoint = endpoints.Read(ordinal);
            if (endpoint.Role != EdgeEndpointRole.Source)
            {
                continue;
            }

            destination.Append(
                new ComplexRestrictionMarkerRecord(
                    endpoint.NodeId,
                    endpoint.EdgeRecordId,
                    Forward: true,
                    StartModes: startRestriction ? modes : 0,
                    EndModes: startRestriction ? 0 : modes,
                    PartOfComplexRestriction: false,
                    canonicalOrdinal++));
            destination.Append(
                new ComplexRestrictionMarkerRecord(
                    endpoint.OtherNodeId,
                    endpoint.EdgeRecordId,
                    Forward: false,
                    StartModes: startRestriction ? modes : 0,
                    EndModes: startRestriction ? 0 : modes,
                    PartOfComplexRestriction: false,
                    canonicalOrdinal++));
        }
    }

    private static void EmitPartOfMarkers(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long viaWayId,
        IIntermediateSequenceStore<ComplexRestrictionMarkerRecord> destination,
        ref long canonicalOrdinal,
        CancellationToken cancellationToken)
    {
        (long start, long end) = GetWayRange(endpoints, viaWayId);
        for (long ordinal = start; ordinal < end; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestrictionWayEndpointRecord endpoint = endpoints.Read(ordinal);
            if (endpoint.Role != EdgeEndpointRole.Source)
            {
                continue;
            }

            destination.Append(
                new ComplexRestrictionMarkerRecord(
                    endpoint.NodeId,
                    endpoint.EdgeRecordId,
                    Forward: true,
                    StartModes: 0,
                    EndModes: 0,
                    PartOfComplexRestriction: true,
                    canonicalOrdinal++));
            destination.Append(
                new ComplexRestrictionMarkerRecord(
                    endpoint.OtherNodeId,
                    endpoint.EdgeRecordId,
                    Forward: false,
                    StartModes: 0,
                    EndModes: 0,
                    PartOfComplexRestriction: true,
                    canonicalOrdinal++));
        }
    }

    private static (long Start, long End) GetWayRange(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long wayId)
    {
        long start = LowerBoundWay(endpoints, wayId);
        long low = start;
        long high = endpoints.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            if (endpoints.Read(middle).WayId <= wayId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return (start, low);
    }

    private static long LowerBoundWay(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long wayId)
    {
        long low = 0;
        long high = endpoints.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            if (endpoints.Read(middle).WayId < wayId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static int CompareEndpoints(
        RestrictionWayEndpointRecord left,
        RestrictionWayEndpointRecord right)
    {
        int comparison = left.WayId.CompareTo(right.WayId);
        if (comparison == 0)
        {
            comparison = left.NodeId.CompareTo(right.NodeId);
        }

        if (comparison == 0)
        {
            comparison = left.EdgeRecordId.CompareTo(right.EdgeRecordId);
        }

        if (comparison == 0)
        {
            comparison = left.Role.CompareTo(right.Role);
        }

        return comparison == 0
            ? left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal)
            : comparison;
    }

    private static int CompareMarkers(
        ComplexRestrictionMarkerRecord left,
        ComplexRestrictionMarkerRecord right)
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

        return comparison == 0
            ? left.CanonicalOrdinal.CompareTo(right.CanonicalOrdinal)
            : comparison;
    }

    private static int CompareKey(
        ComplexRestrictionMarkerRecord candidate,
        GraphId startNode,
        long edgeRecordId,
        bool forward)
    {
        int comparison = candidate.StartNode.CompareTo(startNode);
        if (comparison == 0)
        {
            comparison = candidate.EdgeRecordId.CompareTo(edgeRecordId);
        }

        return comparison == 0
            ? candidate.Forward.CompareTo(forward)
            : comparison;
    }

    private static void ValidateOptions(
        ComplexRestrictionMarkerIndexOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);
        int minimumPartitionBytes = Math.Max(
            Unsafe.SizeOf<RestrictionWayEndpointRecord>(),
            Unsafe.SizeOf<ComplexRestrictionMarkerRecord>());
        if (options.MemoryBudgetBytes / 4 < minimumPartitionBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The complex-restriction memory budget cannot fit one record per partition.");
        }

        if (options.ScratchDiskBudgetBytes / 4 < minimumPartitionBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The complex-restriction scratch budget cannot fit one record per partition.");
        }
    }
}
