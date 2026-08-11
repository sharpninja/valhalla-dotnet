using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record BoundedRoadTileWriterOptions(
    string OutputDirectory,
    long MemoryBudgetBytes,
    int MaxDegreeOfParallelism);

internal sealed record BoundedRoadTileWriteReceipt(
    int TileCount,
    int PeakActiveTileBuilders,
    long PeakWorkerMemoryBytes);

internal static class BoundedRoadTileWriter
{
    private const uint DefaultSpeedKph = 50;

    internal static async ValueTask<BoundedRoadTileWriteReceipt> WriteAsync(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuildResult graph,
        BoundedRoadTileWriterOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticStore);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        string fullOutputDirectory = Path.GetFullPath(options.OutputDirectory);
        string parentDirectory = Path.GetDirectoryName(fullOutputDirectory) ??
            throw new InvalidOperationException(
                "The tile output directory must have a parent directory.");
        string restrictionWorkDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullOutputDirectory)}-restriction-index-{Guid.NewGuid():N}");
        long tileMemoryBudgetBytes = options.MemoryBudgetBytes;
        SimpleRestrictionMaskIndex? restrictionIndex = null;
        try
        {
            if (semanticStore.RestrictionCount > 0)
            {
                if (options.MemoryBudgetBytes < 8)
                {
                    throw new ValhallaGenerationResourceLimitException(
                        "The tile writer memory budget cannot fit a bounded restriction index.");
                }

                long indexMemoryBudgetBytes = Math.Max(
                    4,
                    options.MemoryBudgetBytes / 4);
                tileMemoryBudgetBytes = checked(
                    options.MemoryBudgetBytes - indexMemoryBudgetBytes);
                long indexScratchBudgetBytes =
                    indexMemoryBudgetBytes > long.MaxValue / 4
                        ? long.MaxValue
                        : indexMemoryBudgetBytes * 4;
                restrictionIndex = await SimpleRestrictionMaskIndex.BuildAsync(
                        semanticStore,
                        graph,
                        new SimpleRestrictionMaskIndexOptions(
                            restrictionWorkDirectory,
                            IntermediateStorageMode.Auto,
                            indexMemoryBudgetBytes,
                            indexScratchBudgetBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            Directory.CreateDirectory(fullOutputDirectory);
            int tileCount = 0;
            long peakWorkerMemoryBytes = 0;
            long identityOrdinal = 0;
            while (identityOrdinal < graph.IdentityCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StableGraphNodeIdentity first = graph.ReadIdentity(identityOrdinal);
                GraphId tileBase = first.GraphId.TileBase();
                long tileEnd = identityOrdinal + 1;
                while (tileEnd < graph.IdentityCount &&
                       graph.ReadIdentity(tileEnd).GraphId.TileBase() == tileBase)
                {
                    tileEnd++;
                }

                long estimatedBytes = EstimateTileWorkingSet(
                    semanticStore,
                    graph,
                    identityOrdinal,
                    tileEnd,
                    cancellationToken);
                if (estimatedBytes > tileMemoryBudgetBytes / 2)
                {
                    throw new ValhallaGenerationResourceLimitException(
                        $"Tile {tileBase} requires an estimated {estimatedBytes} bytes " +
                        "before its final serialization buffer, which cannot fit within " +
                        $"the {tileMemoryBudgetBytes}-byte worker budget.");
                }

                long tilePeak = WriteTile(
                    semanticStore,
                    graph,
                    restrictionIndex,
                    identityOrdinal,
                    tileEnd,
                    tileBase,
                    fullOutputDirectory,
                    estimatedBytes,
                    tileMemoryBudgetBytes,
                    cancellationToken);
                peakWorkerMemoryBytes = Math.Max(peakWorkerMemoryBytes, tilePeak);
                tileCount++;
                identityOrdinal = tileEnd;
            }

            return new BoundedRoadTileWriteReceipt(
                tileCount,
                tileCount == 0 ? 0 : 1,
                peakWorkerMemoryBytes);
        }
        finally
        {
            restrictionIndex?.Dispose();
            if (Directory.Exists(restrictionWorkDirectory))
            {
                Directory.Delete(restrictionWorkDirectory, recursive: true);
            }
        }
    }

    private static long WriteTile(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuildResult graph,
        SimpleRestrictionMaskIndex? restrictionIndex,
        long startIdentityOrdinal,
        long endIdentityOrdinal,
        GraphId tileBase,
        string outputDirectory,
        long estimatedBytes,
        long memoryBudgetBytes,
        CancellationToken cancellationToken)
    {
        var builder = new GraphTileBuilder(tileBase);
        PointLL tileCorner =
            TileHierarchy.Levels()[(int)tileBase.Level()].Tiles.Base(
                checked((int)tileBase.Tileid()));

        for (long identityOrdinal = startIdentityOrdinal;
             identityOrdinal < endIdentityOrdinal;
             identityOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StableGraphNodeIdentity identity = graph.ReadIdentity(identityOrdinal);
            if (identity.GraphId.Id() != builder.Nodes.Count)
            {
                throw new InvalidDataException(
                    $"Graph node {identity.GraphId} is not contiguous in tile {tileBase}.");
            }

            if (!graph.TryGetCanonicalNode(identity.OsmNodeId, out GenerationNodeRecord node))
            {
                throw new InvalidDataException(
                    $"Graph node {identity.GraphId} has no canonical OSM node.");
            }

            if (!graph.TryGetGraphNode(identity.GraphId, out GenerationGraphNodeRecord graphNode))
            {
                throw new InvalidDataException(
                    $"Graph node {identity.GraphId} has no incident-edge range.");
            }

            uint nodeAccess = 0;
            uint firstDirectedEdgeIndex = checked((uint)builder.DirectedEdges.Count);
            for (int localEdgeIndex = 0;
                 localEdgeIndex < graphNode.IncidentEdgeCount;
                 localEdgeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NodeEdgeIncidenceRecord incidence = graph.ReadIncidence(
                    checked(graphNode.IncidentEdgeOffset + localEdgeIndex));
                if (incidence.NodeId != identity.GraphId)
                {
                    throw new InvalidDataException(
                        $"Incident edge {incidence.EdgeRecordId} is assigned to the wrong graph node.");
                }

                GenerationEdgeRecord edge =
                    ReadEdge(graph, incidence.EdgeRecordId);
                bool forward = incidence.Role == EdgeEndpointRole.Source;
                uint forwardAccess = forward ? edge.ForwardAccess : edge.ReverseAccess;
                uint reverseAccess = forward ? edge.ReverseAccess : edge.ForwardAccess;
                nodeAccess |= forwardAccess;

                GenerationNodeRecord[] storedShape = graph.ReadShape(edge.Shape);
                var shape = new PointLL[storedShape.Length];
                for (int shapeIndex = 0; shapeIndex < storedShape.Length; shapeIndex++)
                {
                    shape[shapeIndex] = ToPoint(storedShape[shapeIndex]);
                }

                if (!forward)
                {
                    Array.Reverse(shape);
                }

                IReadOnlyDictionary<string, string> tags =
                    semanticStore.ReadTags(edge.AttributeReference);
                (IReadOnlyList<string> names, ushort types) = GetNames(tags);
                uint edgeInfoOffset = builder.AddEdgeInfo(
                    checked((uint)builder.DirectedEdges.Count),
                    edge.SourceNode,
                    edge.TargetNode,
                    checked((ulong)edge.WayId),
                    GraphConstants.NoElevationData,
                    0,
                    0,
                    shape,
                    names,
                    [],
                    [],
                    types,
                    out _);

                DirectedEdge directedEdge = DirectedEdge.Create();
                GraphId endNodeId = forward ? edge.TargetNode : edge.SourceNode;
                if (!graph.TryGetCanonicalNode(
                        endNodeId,
                        out GenerationNodeRecord endNode))
                {
                    throw new InvalidDataException(
                        $"Directed edge {edge.EdgeRecordId} has no canonical end node.");
                }

                directedEdge.SetEndNode(endNodeId);
                directedEdge.SetForward(forward);
                directedEdge.SetLeavesTile(
                    directedEdge.EndNode.TileBase() != tileBase);
                directedEdge.SetEdgeInfoOffset(edgeInfoOffset);
                directedEdge.SetSpeed(DefaultSpeedKph);
                directedEdge.SetLength(
                    checked((uint)Math.Max(1, PointLlPolyline2.Length(shape) + 0.5)),
                    shouldError: true);
                directedEdge.SetUse(GetUse(edge.Flags));
                directedEdge.SetClassification((RoadClass)edge.Importance);
                directedEdge.SetForwardAccess(forwardAccess);
                directedEdge.SetReverseAccess(reverseAccess);
                directedEdge.SetLocalEdgeIdx(checked((uint)localEdgeIndex));
                directedEdge.SetLink(
                    (edge.Flags & EdgeSemanticFlags.Link) != 0);
                directedEdge.SetRoundabout(
                    (edge.Flags & EdgeSemanticFlags.Roundabout) != 0);
                directedEdge.SetDestOnly(
                    (edge.Flags & EdgeSemanticFlags.DestinationOnly) != 0);
                directedEdge.SetDestOnlyHgv(
                    (edge.Flags & EdgeSemanticFlags.DestinationOnlyHgv) != 0);
                directedEdge.SetNotThru(
                    (edge.Flags & EdgeSemanticFlags.NoThruTraffic) != 0);
                directedEdge.SetTrafficSignal(
                    (endNode.Flags & NodeSemanticFlags.TrafficSignal) != 0);
                directedEdge.SetStopSign(
                    (endNode.Flags & NodeSemanticFlags.StopSign) != 0);
                directedEdge.SetYieldSign(
                    (endNode.Flags & NodeSemanticFlags.YieldSign) != 0);
                if (restrictionIndex is not null &&
                    restrictionIndex.TryGetMask(
                        identity.GraphId,
                        edge.EdgeRecordId,
                        forward,
                        out uint restrictionMask))
                {
                    directedEdge.SetRestrictions(restrictionMask);
                }

                builder.DirectedEdges.Add(directedEdge);
            }

            NodeSemanticFlags flags = node.Flags;
            var nodeInfo = new NodeInfo(
                tileCorner,
                ToPoint(node),
                nodeAccess,
                (flags & NodeSemanticFlags.Gate) != 0
                    ? NodeType.Gate
                    : NodeType.StreetIntersection,
                (flags & NodeSemanticFlags.TrafficSignal) != 0,
                taggedAccess: false,
                privateAccess: false,
                cashOnlyToll: false);
            nodeInfo.SetEdgeIndex(firstDirectedEdgeIndex);
            nodeInfo.SetEdgeCount(checked((uint)graphNode.IncidentEdgeCount));
            nodeInfo.SetLocalEdgeCount(checked((uint)graphNode.IncidentEdgeCount));
            nodeInfo.SetDriveOnRight(true);
            builder.Nodes.Add(nodeInfo);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        string parentDirectory = Path.GetDirectoryName(fullOutputDirectory) ??
            throw new InvalidOperationException(
                "The tile output directory must have a parent directory.");
        string stagingDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullOutputDirectory)}-tile-work",
            $"{tileBase.TileValue():X8}-{Guid.NewGuid():N}");
        try
        {
            builder.StoreTileData(stagingDirectory);
            string stagedTilePath = Path.Combine(
                stagingDirectory,
                GraphTile.FileSuffix(tileBase));
            long serializedBytes = new FileInfo(stagedTilePath).Length;
            long peakBytes = checked(estimatedBytes + serializedBytes);
            if (peakBytes > memoryBudgetBytes)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"Tile {tileBase} reached {peakBytes} bytes while constructing " +
                    $"and serializing within a {memoryBudgetBytes}-byte worker budget.");
            }

            GraphTile? validation = GraphTile.Create(stagingDirectory, tileBase);
            if (validation is null)
            {
                throw new InvalidDataException(
                    $"Written tile {tileBase} could not be reopened.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            string publishedTilePath = Path.Combine(
                fullOutputDirectory,
                GraphTile.FileSuffix(tileBase));
            Directory.CreateDirectory(
                Path.GetDirectoryName(publishedTilePath) ??
                fullOutputDirectory);
            File.Move(stagedTilePath, publishedTilePath, overwrite: true);
            return peakBytes;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static long EstimateTileWorkingSet(
        CompactOsmSemanticStore semanticStore,
        PooledRoadEdgeBuildResult graph,
        long startIdentityOrdinal,
        long endIdentityOrdinal,
        CancellationToken cancellationToken)
    {
        long bytes = 0;
        for (long identityOrdinal = startIdentityOrdinal;
             identityOrdinal < endIdentityOrdinal;
             identityOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StableGraphNodeIdentity identity = graph.ReadIdentity(identityOrdinal);
            if (!graph.TryGetGraphNode(identity.GraphId, out GenerationGraphNodeRecord graphNode))
            {
                throw new InvalidDataException(
                    $"Graph node {identity.GraphId} has no incident-edge range.");
            }

            bytes = checked(bytes + Unsafe.SizeOf<NodeInfo>());
            for (int localEdgeIndex = 0;
                 localEdgeIndex < graphNode.IncidentEdgeCount;
                 localEdgeIndex++)
            {
                NodeEdgeIncidenceRecord incidence = graph.ReadIncidence(
                    checked(graphNode.IncidentEdgeOffset + localEdgeIndex));
                GenerationEdgeRecord edge =
                    ReadEdge(graph, incidence.EdgeRecordId);
                bytes = checked(
                    bytes +
                    Unsafe.SizeOf<DirectedEdge>() +
                    edge.Shape.ByteLength +
                    edge.Shape.ByteLength);
                IReadOnlyDictionary<string, string> tags =
                    semanticStore.ReadTags(edge.AttributeReference);
                foreach ((string key, string value) in tags)
                {
                    bytes = checked(bytes + (key.Length * 2L) + (value.Length * 2L));
                }
            }
        }

        return bytes;
    }

    private static GenerationEdgeRecord ReadEdge(
        PooledRoadEdgeBuildResult graph,
        long edgeRecordId)
    {
        if (!graph.TryReadEdgeByRecordId(edgeRecordId, out GenerationEdgeRecord edge))
        {
            throw new InvalidDataException(
                $"Durable edge {edgeRecordId} was not found.");
        }

        return edge;
    }

    private static (IReadOnlyList<string> Names, ushort Types) GetNames(
        IReadOnlyDictionary<string, string> tags)
    {
        var names = new List<string>(2);
        ushort types = 0;
        if (tags.TryGetValue("name", out string? name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            names.Add(name);
        }

        if (tags.TryGetValue("ref", out string? reference) &&
            !string.IsNullOrWhiteSpace(reference))
        {
            if (names.Count < 16)
            {
                types |= checked((ushort)(1 << names.Count));
            }

            names.Add(reference);
        }

        return (names, types);
    }

    private static Use GetUse(EdgeSemanticFlags flags)
    {
        if ((flags & EdgeSemanticFlags.Rail) != 0)
        {
            return Use.RailFerry;
        }

        if ((flags & EdgeSemanticFlags.Ferry) != 0)
        {
            return Use.Ferry;
        }

        if ((flags & EdgeSemanticFlags.Link) != 0)
        {
            return Use.Ramp;
        }

        return Use.Road;
    }

    private static PointLL ToPoint(in GenerationNodeRecord node) =>
        PointLL.Create(
            node.LongitudeE7 / 10_000_000d,
            node.LatitudeE7 / 10_000_000d);

    private static void ValidateOptions(BoundedRoadTileWriterOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaxDegreeOfParallelism);
    }
}
