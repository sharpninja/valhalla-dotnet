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
                graph,
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
        PooledRoadEdgeBuildResult graph,
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
            if (!TryGetComplexModes(
                    semanticStore,
                    restriction,
                    out uint modes,
                    out bool viaWay,
                    out bool includeFromWay))
            {
                continue;
            }

            GenerationRestrictionViaRecord firstVia =
                semanticStore.ReadRestrictionVia(restriction.ViaOffset);
            GenerationRestrictionViaRecord lastVia =
                semanticStore.ReadRestrictionVia(
                    checked(restriction.ViaOffset + restriction.ViaCount - 1));
            if (includeFromWay)
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

            if (viaWay)
            {
                EmitBoundaryMarkers(
                    endpoints,
                    restriction.FromWayId,
                    firstVia.MemberId,
                    modes,
                    startBoundary: true,
                    destination,
                    ref canonicalOrdinal,
                    cancellationToken);
                EmitBoundaryMarkers(
                    endpoints,
                    restriction.ToWayId,
                    lastVia.MemberId,
                    modes,
                    startBoundary: false,
                    destination,
                    ref canonicalOrdinal,
                    cancellationToken);
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
            else if (firstVia.MemberType == OsmMemberType.Node &&
                     graph.TryGetGraphId(firstVia.MemberId, out GraphId viaNode))
            {
                EmitNodeBoundaryMarkers(
                    endpoints,
                    restriction.FromWayId,
                    viaNode,
                    modes,
                    startBoundary: true,
                    destination,
                    ref canonicalOrdinal,
                    cancellationToken);
                EmitNodeBoundaryMarkers(
                    endpoints,
                    restriction.ToWayId,
                    viaNode,
                    modes,
                    startBoundary: false,
                    destination,
                    ref canonicalOrdinal,
                    cancellationToken);
            }
        }
    }

    private static void EmitBoundaryMarkers(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long boundaryWayId,
        long viaWayId,
        uint modes,
        bool startBoundary,
        IIntermediateSequenceStore<ComplexRestrictionMarkerRecord> destination,
        ref long canonicalOrdinal,
        CancellationToken cancellationToken)
    {
        (long start, long end) = GetWayRange(endpoints, boundaryWayId);
        for (long ordinal = start; ordinal < end; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestrictionWayEndpointRecord endpoint = endpoints.Read(ordinal);
            if (ContainsWayNode(endpoints, viaWayId, endpoint.NodeId))
            {
                EmitBoundaryMarker(
                    endpoint,
                    modes,
                    startBoundary,
                    destination,
                    ref canonicalOrdinal);
            }
        }
    }

    private static void EmitNodeBoundaryMarkers(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long boundaryWayId,
        GraphId viaNode,
        uint modes,
        bool startBoundary,
        IIntermediateSequenceStore<ComplexRestrictionMarkerRecord> destination,
        ref long canonicalOrdinal,
        CancellationToken cancellationToken)
    {
        (long start, long end) = GetWayRange(endpoints, boundaryWayId);
        for (long ordinal = start; ordinal < end; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestrictionWayEndpointRecord endpoint = endpoints.Read(ordinal);
            if (endpoint.NodeId == viaNode)
            {
                EmitBoundaryMarker(
                    endpoint,
                    modes,
                    startBoundary,
                    destination,
                    ref canonicalOrdinal);
            }
        }
    }

    private static void EmitBoundaryMarker(
        RestrictionWayEndpointRecord endpoint,
        uint modes,
        bool startBoundary,
        IIntermediateSequenceStore<ComplexRestrictionMarkerRecord> destination,
        ref long canonicalOrdinal)
    {
        GraphId startNode = startBoundary
            ? endpoint.OtherNodeId
            : endpoint.NodeId;
        bool forward = startBoundary
            ? endpoint.Role == EdgeEndpointRole.Target
            : endpoint.Role == EdgeEndpointRole.Source;
        destination.Append(
            new ComplexRestrictionMarkerRecord(
                startNode,
                endpoint.EdgeRecordId,
                forward,
                StartModes: startBoundary ? modes : 0,
                EndModes: startBoundary ? 0 : modes,
                PartOfComplexRestriction: false,
                canonicalOrdinal++));
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

    private static bool TryGetComplexModes(
        CompactOsmSemanticStore semanticStore,
        GenerationRestrictionRecord restriction,
        out uint modes,
        out bool viaWay,
        out bool includeFromWay)
    {
        IReadOnlyDictionary<string, string> tags =
            semanticStore.ReadTags(restriction.TagReference);
        viaWay = false;
        for (long viaOrdinal = restriction.ViaOffset;
             viaOrdinal < restriction.ViaOffset + restriction.ViaCount;
             viaOrdinal++)
        {
            viaWay |= semanticStore.ReadRestrictionVia(viaOrdinal).MemberType ==
                      OsmMemberType.Way;
        }

        includeFromWay =
            viaWay || !tags.ContainsKey("restriction:conditional");

        uint specificModes = 0;
        specificModes |= GetSpecificMode(tags, "restriction:motorcar",
            (uint)(GraphConstants.AutoAccess | GraphConstants.MopedAccess));
        specificModes |= GetSpecificMode(tags, "restriction:motorcycle",
            GraphConstants.MotorcycleAccess);
        specificModes |= GetSpecificMode(tags, "restriction:taxi",
            GraphConstants.TaxiAccess);
        specificModes |= GetSpecificMode(tags, "restriction:bus",
            GraphConstants.BusAccess);
        specificModes |= GetSpecificMode(tags, "restriction:bicycle",
            GraphConstants.BicycleAccess);
        specificModes |= GetSpecificMode(tags, "restriction:hgv",
            GraphConstants.TruckAccess);
        specificModes |= GetSpecificMode(tags, "restriction:hazmat",
            GraphConstants.TruckAccess);
        specificModes |= GetSpecificMode(tags, "restriction:emergency",
            GraphConstants.EmergencyAccess);
        specificModes |= GetSpecificMode(tags, "restriction:foot",
            (uint)(GraphConstants.PedestrianAccess |
                   GraphConstants.WheelchairAccess));

        bool qualified =
            tags.ContainsKey("restriction:conditional") ||
            tags.ContainsKey("restriction:probable");
        bool excepted =
            tags.TryGetValue("except", out string? except) &&
            !string.IsNullOrWhiteSpace(except);
        if (!viaWay && specificModes == 0 && !qualified && !excepted)
        {
            modes = 0;
            return false;
        }

        if (specificModes != 0)
        {
            modes = specificModes;
            return true;
        }

        modes = (uint)(GraphConstants.AutoAccess |
                       GraphConstants.MopedAccess |
                       GraphConstants.TaxiAccess |
                       GraphConstants.BusAccess |
                       GraphConstants.BicycleAccess |
                       GraphConstants.TruckAccess |
                       GraphConstants.EmergencyAccess |
                       GraphConstants.MotorcycleAccess);
        if (!excepted)
        {
            return true;
        }

        foreach (string token in except!.Split(';'))
        {
            modes = token.Trim() switch
            {
                "motorcar" => modes & ~(uint)(
                    GraphConstants.AutoAccess | GraphConstants.MopedAccess),
                "motorcycle" => modes & ~(uint)GraphConstants.MotorcycleAccess,
                "psv" => modes & ~(uint)(
                    GraphConstants.TaxiAccess | GraphConstants.BusAccess),
                "taxi" => modes & ~(uint)GraphConstants.TaxiAccess,
                "bus" => modes & ~(uint)GraphConstants.BusAccess,
                "bicycle" => modes & ~(uint)GraphConstants.BicycleAccess,
                "hgv" => modes & ~(uint)GraphConstants.TruckAccess,
                "emergency" => modes & ~(uint)GraphConstants.EmergencyAccess,
                "foot" => modes & ~(uint)(
                    GraphConstants.PedestrianAccess |
                    GraphConstants.WheelchairAccess),
                _ => modes,
            };
        }

        return modes != 0;
    }

    private static uint GetSpecificMode(
        IReadOnlyDictionary<string, string> tags,
        string key,
        uint mode) =>
        tags.TryGetValue(key, out string? value) &&
        byte.TryParse(value, out _)
            ? mode
            : 0;

    private static bool ContainsWayNode(
        IIntermediateSequenceStore<RestrictionWayEndpointRecord> endpoints,
        long wayId,
        GraphId nodeId)
    {
        (long start, long end) = GetWayRange(endpoints, wayId);
        long low = start;
        long high = end;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            RestrictionWayEndpointRecord candidate = endpoints.Read(middle);
            if (candidate.NodeId.CompareTo(nodeId) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < end && endpoints.Read(low).NodeId == nodeId;
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
