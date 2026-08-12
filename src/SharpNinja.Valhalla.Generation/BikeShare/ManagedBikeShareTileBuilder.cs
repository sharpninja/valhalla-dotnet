using System.Security.Cryptography;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.BikeShare;

/// <summary>
/// Adds OSM bicycle-rental stations to an existing local-level Valhalla graph.
/// The implementation follows Valhalla 3.8.3 bssbuilder semantics while using a
/// discovery barrier and atomic directory publication.
/// </summary>
public sealed class ManagedBikeShareTileBuilder : IBikeShareTileBuilder
{
    private const double ProjectionDistanceEpsilon = 0.000001;
    private static readonly HashSet<Use> ValidEdgeUses =
    [
        Use.Road,
        Use.LivingStreet,
        Use.Cycleway,
        Use.Sidewalk,
        Use.Footway,
        Use.Path,
        Use.Pedestrian,
        Use.Alley,
        Use.ServiceRoad,
    ];

    /// <inheritdoc />
    public async ValueTask<BikeShareTileBuildResult> BuildAsync(
        BikeShareTileBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedRequest validated = ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(validated.WorkingDirectory);
        string stagingDirectory = Path.Combine(validated.WorkingDirectory, "staging");
        Directory.CreateDirectory(stagingDirectory);

        long graphBytes = await CopyGraphAsync(
            validated.GraphTileDirectory,
            stagingDirectory,
            validated.Options.ScratchDiskBudgetBytes,
            cancellationToken).ConfigureAwait(false);

        (IReadOnlyList<BikeShareStationSource> stations, long pbfBytesRead) =
            await new BikeShareStationReader().ReadAsync(
                validated.OsmPbfPaths,
                validated.Options.MemoryBudgetBytes,
                cancellationToken).ConfigureAwait(false);

        if (stations.Count == 0)
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.NoStations,
                "No bike-share stations were found in the configured OSM PBF inputs.");
        }

        IReadOnlyList<StationPlan> stationPlans = DiscoverStationPlans(
            stagingDirectory,
            stations,
            cancellationToken);
        IReadOnlyDictionary<GraphId, IReadOnlyList<ConnectionPlan>> inboundByTile =
            IndexInboundConnections(stagingDirectory, stationPlans, cancellationToken);

        SortedSet<GraphId> affectedTiles = new(GraphIdValueComparer.Instance);
        foreach (StationPlan stationPlan in stationPlans)
        {
            affectedTiles.Add(stationPlan.StationNodeId.TileBase());
        }

        foreach (GraphId tileId in inboundByTile.Keys)
        {
            affectedTiles.Add(tileId.TileBase());
        }

        foreach (GraphId tileId in affectedTiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RewriteTile(
                stagingDirectory,
                tileId,
                stationPlans
                    .Where(plan => plan.StationNodeId.TileBase() == tileId)
                    .OrderBy(plan => plan.StationNodeId.Id())
                    .ToArray(),
                inboundByTile.TryGetValue(tileId, out IReadOnlyList<ConnectionPlan>? inbound)
                    ? inbound
                    : [],
                cancellationToken);
        }

        long stagedBytes = GetGraphBytes(stagingDirectory, cancellationToken);
        if (stagedBytes > validated.Options.ScratchDiskBudgetBytes)
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.ResourceExhausted,
                "The generated graph exceeds the configured scratch-disk budget.");
        }

        IReadOnlyDictionary<string, string> hashes =
            await ValidateAndHashAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.Move(stagingDirectory, validated.OutputDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.PublicationFailed,
                "The validated bike-share generation could not be atomically published.",
                exception);
        }

        return new BikeShareTileBuildResult(
            validated.OutputDirectory,
            hashes.Count,
            stationPlans.Count,
            stationPlans.Count,
            checked(stationPlans.Count * 8),
            checked(graphBytes + pbfBytesRead),
            stagedBytes,
            MaximumConcurrency: 1,
            hashes);
    }

    private static ValidatedRequest ValidateRequest(BikeShareTileBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);

        if (string.IsNullOrWhiteSpace(request.GraphTileDirectory)
            || request.OsmPbfPaths is null
            || request.OsmPbfPaths.Count == 0
            || string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || string.IsNullOrWhiteSpace(request.OutputDirectory)
            || request.Options.MaxDegreeOfParallelism <= 0
            || request.Options.MemoryBudgetBytes <= 0
            || request.Options.ScratchDiskBudgetBytes <= 0)
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.InvalidConfiguration,
                "Bike-share generation requires graph tiles, PBF inputs, distinct work/output directories, and positive resource limits.");
        }

        string graphDirectory = Path.GetFullPath(request.GraphTileDirectory);
        string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        string[] pbfPaths = request.OsmPbfPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!Directory.Exists(graphDirectory)
            || !Directory.EnumerateFiles(graphDirectory, "*.gph", SearchOption.AllDirectories).Any())
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.GraphTileNotFound,
                "The configured graph-tile directory does not contain graph tiles.");
        }

        if (pbfPaths.Any(path => !File.Exists(path)))
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.MissingInput,
                "One or more configured bike-share OSM PBF inputs do not exist.");
        }

        if (PathEquals(graphDirectory, workingDirectory)
            || PathEquals(graphDirectory, outputDirectory)
            || PathEquals(workingDirectory, outputDirectory)
            || IsWithin(workingDirectory, graphDirectory)
            || IsWithin(outputDirectory, graphDirectory))
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.InvalidConfiguration,
                "Graph input, working, and output directories must be distinct and output paths cannot be inside the graph input.");
        }

        if (Directory.Exists(workingDirectory)
            && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.InvalidConfiguration,
                "The bike-share working directory must be absent or empty.");
        }

        if (Directory.Exists(outputDirectory)
            || File.Exists(outputDirectory))
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.InvalidConfiguration,
                "The bike-share output path must not already exist.");
        }

        if (!string.Equals(
                Path.GetPathRoot(workingDirectory),
                Path.GetPathRoot(outputDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.InvalidConfiguration,
                "The working and output directories must share a volume for atomic publication.");
        }

        foreach (string path in pbfPaths.Append(graphDirectory))
        {
            RejectReparsePoints(path);
        }

        RejectReparsePoints(Path.GetDirectoryName(workingDirectory)!);
        RejectReparsePoints(Path.GetDirectoryName(outputDirectory)!);

        return new ValidatedRequest(
            graphDirectory,
            pbfPaths,
            workingDirectory,
            outputDirectory,
            request.Options);
    }

    private static async Task<long> CopyGraphAsync(
        string sourceDirectory,
        string stagingDirectory,
        long scratchDiskBudgetBytes,
        CancellationToken cancellationToken)
    {
        long bytesCopied = 0;
        foreach (string sourcePath in Directory
                     .EnumerateFiles(sourceDirectory, "*.gph", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = new FileInfo(sourcePath).Length;
            bytesCopied = checked(bytesCopied + length);
            if (bytesCopied > scratchDiskBudgetBytes)
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.ResourceExhausted,
                    "Copying the base graph would exceed the configured scratch-disk budget.");
            }

            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(stagingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            await using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream destination = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 128 * 1024, cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return bytesCopied;
    }

    private static IReadOnlyList<StationPlan> DiscoverStationPlans(
        string graphDirectory,
        IReadOnlyList<BikeShareStationSource> stations,
        CancellationToken cancellationToken)
    {
        byte localLevel = checked((byte)TileHierarchy.Levels()[^1].Level);
        var plans = new List<StationPlan>(stations.Count);

        foreach (IGrouping<GraphId, BikeShareStationSource> group in stations
                     .GroupBy(
                         station => TileHierarchy.GetGraphId(
                             new PointLL(station.Longitude, station.Latitude),
                             localLevel).TileBase(),
                         GraphIdValueComparer.Instance)
                     .OrderBy(group => group.Key, GraphIdValueComparer.Instance))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphTile tile = LoadTile(graphDirectory, group.Key);
            uint nextNodeId = tile.Header().Nodecount();

            foreach (BikeShareStationSource station in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PointLL stationPoint = new(station.Longitude, station.Latitude);
                Projection pedestrian = FindProjection(
                    tile,
                    stationPoint,
                    GraphConstants.PedestrianAccess,
                    station.OsmId);
                Projection bicycle = FindProjection(
                    tile,
                    stationPoint,
                    GraphConstants.BicycleAccess,
                    station.OsmId);
                GraphId stationNodeId = new(
                    group.Key.Tileid(),
                    group.Key.Level(),
                    nextNodeId++);

                ConnectionPlan[] connections =
                [
                    CreateConnection(station, stationNodeId, pedestrian, useStart: true, ordinal: 0),
                    CreateConnection(station, stationNodeId, pedestrian, useStart: false, ordinal: 1),
                    CreateConnection(station, stationNodeId, bicycle, useStart: true, ordinal: 2),
                    CreateConnection(station, stationNodeId, bicycle, useStart: false, ordinal: 3),
                ];
                plans.Add(new StationPlan(station, stationNodeId, connections));
            }
        }

        return plans;
    }

    private static Projection FindProjection(
        GraphTile tile,
        PointLL station,
        uint requiredAccess,
        ulong stationOsmId)
    {
        Projection? best = null;
        double minimumDistance = double.MaxValue;
        uint nodeCount = tile.Header().Nodecount();

        for (uint nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            NodeInfo node = tile.Node(checked((int)nodeIndex));
            for (uint localIndex = 0; localIndex < node.EdgeCount; localIndex++)
            {
                uint edgeIndex = checked(node.EdgeIndex + localIndex);
                DirectedEdge edge = tile.DirectedEdge(checked((int)edgeIndex));
                if (edge.IsShortcut
                    || !ValidEdgeUses.Contains(edge.Use)
                    || (edge.ForwardAccess & requiredAccess) == 0)
                {
                    continue;
                }

                EdgeInfo info = tile.EdgeInfo(edge);
                List<PointLL> shape = info.Shape().ToList();
                if (!edge.Forward)
                {
                    shape.Reverse();
                }

                (PointLL closest, double distance, int index) = station.Project(shape);
                if (distance >= minimumDistance - ProjectionDistanceEpsilon)
                {
                    continue;
                }

                minimumDistance = distance;
                best = new Projection(
                    new GraphId(tile.Id().Tileid(), tile.Id().Level(), nodeIndex),
                    edge.EndNode,
                    edge,
                    info,
                    shape,
                    closest,
                    index);
            }
        }

        return best ?? throw new BikeShareTileBuildException(
            BikeShareTileBuildFailureCode.ProjectionFailed,
            $"Bike-share station {stationOsmId} cannot be projected to a pedestrian/bicycle graph edge.");
    }

    private static ConnectionPlan CreateConnection(
        BikeShareStationSource station,
        GraphId stationNodeId,
        Projection projection,
        bool useStart,
        int ordinal)
    {
        var shape = new List<PointLL>(projection.Shape.Count + 2);
        if (useStart)
        {
            for (int index = 0; index <= projection.ClosestIndex; index++)
            {
                shape.Add(projection.Shape[index]);
            }

            shape.Add(projection.ClosestPoint);
            shape.Add(new PointLL(station.Longitude, station.Latitude));
        }
        else
        {
            shape.Add(new PointLL(station.Longitude, station.Latitude));
            shape.Add(projection.ClosestPoint);
            for (int index = projection.ClosestIndex + 1; index < projection.Shape.Count; index++)
            {
                shape.Add(projection.Shape[index]);
            }
        }

        List<string> taggedValues = projection.EdgeInfo.GetTaggedValues();
        taggedValues.Add(station.EncodedTaggedValue);

        return new ConnectionPlan(
            stationNodeId,
            useStart ? projection.StartNodeId : projection.EndNodeId,
            ordinal,
            IsForwardFromWayNode: useStart,
            projection.EdgeInfo.WayId,
            projection.EdgeInfo.GetNames(),
            taggedValues,
            projection.EdgeInfo.GetLinguisticTaggedValues(),
            shape,
            projection.Edge.Speed,
            projection.Edge.Surface,
            projection.Edge.CycleLane,
            projection.Edge.Classification,
            projection.Edge.Use,
            projection.Edge.ForwardAccess,
            projection.Edge.ReverseAccess);
    }

    private static IReadOnlyDictionary<GraphId, IReadOnlyList<ConnectionPlan>> IndexInboundConnections(
        string graphDirectory,
        IReadOnlyList<StationPlan> stationPlans,
        CancellationToken cancellationToken)
    {
        ConnectionPlan[] connections = stationPlans
            .SelectMany(plan => plan.Connections)
            .OrderBy(connection => connection.WayNodeId.Tileid())
            .ThenBy(connection => connection.WayNodeId.Level())
            .ThenBy(connection => connection.WayNodeId.Id())
            .ThenBy(connection => connection.StationNodeId.Value)
            .ThenBy(connection => connection.Ordinal)
            .ToArray();

        foreach (IGrouping<GraphId, ConnectionPlan> nodeGroup in connections.GroupBy(
                     connection => connection.WayNodeId,
                     GraphIdValueComparer.Instance))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphTile tile = LoadTile(graphDirectory, nodeGroup.Key.TileBase());
            if (nodeGroup.Key.Id() >= tile.Header().Nodecount())
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.CorruptGraph,
                    "A projected bike-share connection references a missing graph node.");
            }

            uint originalEdgeCount = tile.Node(checked((int)nodeGroup.Key.Id())).EdgeCount;
            uint offset = 0;
            foreach (ConnectionPlan connection in nodeGroup)
            {
                connection.TargetInboundLocalIndex = checked(originalEdgeCount + offset++);
            }
        }

        return connections
            .GroupBy(connection => connection.WayNodeId.TileBase(), GraphIdValueComparer.Instance)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ConnectionPlan>)group.ToArray(),
                GraphIdValueComparer.Instance);
    }

    private static void RewriteTile(
        string graphDirectory,
        GraphId tileId,
        IReadOnlyList<StationPlan> stationPlans,
        IReadOnlyList<ConnectionPlan> inboundConnections,
        CancellationToken cancellationToken)
    {
        GraphTile tile = LoadTile(graphDirectory, tileId);
        var builder = new GraphTileBuilder(tile);
        NodeInfo[] originalNodes = builder.Nodes.ToArray();
        DirectedEdge[] originalEdges = builder.DirectedEdges.ToArray();
        var oldToNewEdgeIndex = new Dictionary<uint, uint>(originalEdges.Length);
        var sourceEdgeIndexes = new List<int>(
            checked(originalEdges.Length + inboundConnections.Count + (stationPlans.Count * 4)));

        ILookup<uint, ConnectionPlan> inboundByNode = inboundConnections
            .OrderBy(connection => connection.WayNodeId.Id())
            .ThenBy(connection => connection.StationNodeId.Value)
            .ThenBy(connection => connection.Ordinal)
            .ToLookup(connection => connection.WayNodeId.Id());

        builder.Nodes.Clear();
        builder.DirectedEdges.Clear();

        for (uint nodeIndex = 0; nodeIndex < originalNodes.Length; nodeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NodeInfo node = originalNodes[nodeIndex];
            uint newEdgeIndex = checked((uint)builder.DirectedEdges.Count);

            for (uint localIndex = 0; localIndex < node.EdgeCount; localIndex++)
            {
                uint oldEdgeIndex = checked(node.EdgeIndex + localIndex);
                oldToNewEdgeIndex.Add(oldEdgeIndex, checked((uint)builder.DirectedEdges.Count));
                builder.DirectedEdges.Add(originalEdges[oldEdgeIndex]);
                sourceEdgeIndexes.Add(checked((int)oldEdgeIndex));
            }

            foreach (ConnectionPlan connection in inboundByNode[nodeIndex])
            {
                DirectedEdge inbound = MakeDirectedEdge(
                    connection.StationNodeId,
                    connection,
                    connection.IsForwardFromWayNode,
                    checked((uint)connection.Ordinal));
                uint edgeInfoOffset = AddConnectionEdgeInfo(
                    builder,
                    checked((uint)builder.DirectedEdges.Count),
                    connection.WayNodeId,
                    connection.StationNodeId,
                    connection);
                inbound.SetEdgeInfoOffset(edgeInfoOffset);
                builder.DirectedEdges.Add(inbound);
                sourceEdgeIndexes.Add(-1);
            }

            node.SetEdgeIndex(newEdgeIndex);
            node.SetEdgeCount(checked((uint)builder.DirectedEdges.Count - newEdgeIndex));
            builder.Nodes.Add(node);
        }

        foreach (StationPlan stationPlan in stationPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint edgeIndex = checked((uint)builder.DirectedEdges.Count);
            var node = new NodeInfo(
                tile.BaseLl(),
                new PointLL(
                    stationPlan.Station.Longitude,
                    stationPlan.Station.Latitude),
                GraphConstants.PedestrianAccess | GraphConstants.BicycleAccess,
                NodeType.BikeShare,
                trafficSignal: false,
                taggedAccess: true,
                privateAccess: false,
                cashOnlyToll: false);
            node.SetModeChange(true);
            node.SetEdgeIndex(edgeIndex);
            node.SetEdgeCount(4);
            builder.Nodes.Add(node);

            foreach (ConnectionPlan connection in stationPlan.Connections.OrderBy(item => item.Ordinal))
            {
                DirectedEdge outbound = MakeDirectedEdge(
                    connection.WayNodeId,
                    connection,
                    !connection.IsForwardFromWayNode,
                    connection.TargetInboundLocalIndex);
                uint edgeInfoOffset = AddConnectionEdgeInfo(
                    builder,
                    checked((uint)builder.DirectedEdges.Count),
                    stationPlan.StationNodeId,
                    connection.WayNodeId,
                    connection);
                outbound.SetEdgeInfoOffset(edgeInfoOffset);
                builder.DirectedEdges.Add(outbound);
                sourceEdgeIndexes.Add(-1);
            }
        }

        RemapSigns(builder, oldToNewEdgeIndex);
        RemapAccessRestrictions(builder, oldToNewEdgeIndex);
        builder.RemapPredictedSpeedOffsets(sourceEdgeIndexes);
        builder.StoreTileData(graphDirectory);
    }

    private static DirectedEdge MakeDirectedEdge(
        GraphId endNode,
        ConnectionPlan connection,
        bool isForward,
        uint localEdgeIndex)
    {
        var edge = new DirectedEdge();
        edge.SetEndNode(endNode);
        edge.SetLength(checked((uint)Math.Round(
            PointLlPolyline2.Length(connection.Shape),
            MidpointRounding.AwayFromZero)));
        edge.SetUse(connection.Use);
        edge.SetSpeed(connection.Speed);
        edge.SetSurface(connection.Surface);
        edge.SetCycleLane(connection.CycleLane);
        edge.SetClassification(connection.RoadClass);
        edge.SetLocalEdgeIdx(localEdgeIndex);
        edge.SetForwardAccess(isForward ? connection.ForwardAccess : connection.ReverseAccess);
        edge.SetReverseAccess(isForward ? connection.ReverseAccess : connection.ForwardAccess);
        edge.SetNamed(connection.Names.Count > 0 || connection.TaggedValues.Count > 0);
        edge.SetForward(isForward);
        edge.SetBssConnection(true);
        return edge;
    }

    private static uint AddConnectionEdgeInfo(
        GraphTileBuilder builder,
        uint edgeIndex,
        GraphId nodeA,
        GraphId nodeB,
        ConnectionPlan connection)
    {
        uint offset = builder.AddEdgeInfo(
            edgeIndex,
            nodeA,
            nodeB,
            connection.WayId,
            elev: 0,
            bn: 0,
            spd: 0,
            connection.Shape,
            connection.Names,
            connection.TaggedValues,
            connection.Linguistics,
            types: 0,
            out _);
        return offset;
    }

    private static void RemapSigns(
        GraphTileBuilder builder,
        IReadOnlyDictionary<uint, uint> oldToNewEdgeIndex)
    {
        for (int index = 0; index < builder.Signs.Count; index++)
        {
            Sign sign = builder.Signs[index];
            if (!oldToNewEdgeIndex.TryGetValue(sign.Index, out uint mapped))
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.CorruptGraph,
                    "A graph sign references a missing directed edge.");
            }

            sign.Index = mapped;
            builder.SetSignBuilder(index, sign);
        }
    }

    private static void RemapAccessRestrictions(
        GraphTileBuilder builder,
        IReadOnlyDictionary<uint, uint> oldToNewEdgeIndex)
    {
        for (int index = 0; index < builder.AccessRestrictions.Count; index++)
        {
            AccessRestriction restriction = builder.AccessRestrictions[index];
            if (!oldToNewEdgeIndex.TryGetValue(restriction.EdgeIndex(), out uint mapped))
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.CorruptGraph,
                    "A graph access restriction references a missing directed edge.");
            }

            restriction.SetEdgeIndex(mapped);
            builder.SetAccessRestrictionBuilder(index, restriction);
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ValidateAndHashAsync(
        string graphDirectory,
        CancellationToken cancellationToken)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory
                     .EnumerateFiles(graphDirectory, "*.gph", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId graphId = GraphTile.GetTileId(path);
            try
            {
                _ = GraphTile.Create(
                    graphId,
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.CorruptGraph,
                    "A generated bike-share graph tile failed validation.",
                    exception);
            }

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            hashes.Add(
                Path.GetRelativePath(graphDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
                Convert.ToHexString(hash));
        }

        return hashes;
    }

    private static GraphTile LoadTile(string graphDirectory, GraphId tileId)
        => GraphTile.Create(graphDirectory, tileId)
           ?? throw new BikeShareTileBuildException(
               BikeShareTileBuildFailureCode.GraphTileNotFound,
               $"Graph tile {tileId.Tileid()}/{tileId.Level()} was not found.");

    private static long GetGraphBytes(
        string graphDirectory,
        CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(
                     graphDirectory,
                     "*.gph",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + new FileInfo(path).Length);
        }

        return total;
    }

    private static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.UnsafePath,
                    "Bike-share generation paths cannot traverse reparse points.");
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string candidate, string parent)
    {
        string relative = Path.GetRelativePath(parent, candidate);
        return relative != "."
               && !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private sealed record ValidatedRequest(
        string GraphTileDirectory,
        IReadOnlyList<string> OsmPbfPaths,
        string WorkingDirectory,
        string OutputDirectory,
        BikeShareTileBuildOptions Options);

    private sealed record Projection(
        GraphId StartNodeId,
        GraphId EndNodeId,
        DirectedEdge Edge,
        EdgeInfo EdgeInfo,
        IReadOnlyList<PointLL> Shape,
        PointLL ClosestPoint,
        int ClosestIndex);

    private sealed record StationPlan(
        BikeShareStationSource Station,
        GraphId StationNodeId,
        IReadOnlyList<ConnectionPlan> Connections);

    private sealed record ConnectionPlan(
        GraphId StationNodeId,
        GraphId WayNodeId,
        int Ordinal,
        bool IsForwardFromWayNode,
        ulong WayId,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> TaggedValues,
        IReadOnlyList<string> Linguistics,
        IReadOnlyList<PointLL> Shape,
        uint Speed,
        Surface Surface,
        CycleLane CycleLane,
        RoadClass RoadClass,
        Use Use,
        uint ForwardAccess,
        uint ReverseAccess)
    {
        public uint TargetInboundLocalIndex { get; set; }
    }

    private sealed class GraphIdValueComparer :
        IComparer<GraphId>,
        IEqualityComparer<GraphId>
    {
        public static GraphIdValueComparer Instance { get; } = new();

        public int Compare(GraphId x, GraphId y) => x.Value.CompareTo(y.Value);

        public bool Equals(GraphId x, GraphId y) => x.Value == y.Value;

        public int GetHashCode(GraphId obj) => obj.GetHashCode();
    }
}
