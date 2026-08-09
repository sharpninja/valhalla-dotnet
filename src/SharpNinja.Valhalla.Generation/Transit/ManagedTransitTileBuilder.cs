using System.Security.Cryptography;
using System.Text;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Transit;

/// <summary>Deterministic managed GTFS-to-Valhalla transit graph builder.</summary>
public sealed class ManagedTransitTileBuilder : ITransitTileBuilder
{
    private const uint TransitAccess =
        GraphConstants.PedestrianAccess |
        GraphConstants.BicycleAccess |
        GraphConstants.WheelchairAccess;
    private const uint PlatformAccess =
        GraphConstants.PedestrianAccess |
        GraphConstants.WheelchairAccess;
    private const int TransitLevel = 3;

    private readonly GtfsFeedReader _reader;

    public ManagedTransitTileBuilder()
        : this(new GtfsFeedReader())
    {
    }

    internal ManagedTransitTileBuilder(GtfsFeedReader reader)
    {
        _reader = reader;
    }

    public async ValueTask<TransitTileBuildResult> BuildAsync(
        TransitTileBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedRequest validated = Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        var feeds = new List<ParsedGtfsFeed>(validated.FeedPaths.Length);
        foreach (string feedPath in validated.FeedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GtfsFeedData raw = await _reader.ReadAsync(
                feedPath,
                validated.Options.MemoryBudgetBytes,
                cancellationToken).ConfigureAwait(false);
            feeds.Add(GtfsModelParser.Parse(raw, validated.Options.BuildDate));
        }

        using TransitTimeZoneResolver timeZoneResolver =
            TransitTimeZoneResolver.Open(validated.TimeZoneDatabasePath);
        IReadOnlyDictionary<uint, TileContext> tiles = BuildContexts(
            feeds,
            timeZoneResolver,
            cancellationToken);
        string stagingDirectory = Path.Combine(
            validated.WorkingDirectory,
            "transit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            TileContext[] orderedTiles = tiles.Values
                .OrderBy(tile => tile.TileId)
                .ToArray();
            var artifacts = new TileBuildArtifact?[orderedTiles.Length];
            long bytesWritten = 0;
            int activeWorkers = 0;
            int peakConcurrency = 0;
            const long MinimumWorkerBudgetBytes = 8 * 1024 * 1024;
            int memoryBoundedDegree = checked((int)Math.Max(
                1,
                validated.Options.MemoryBudgetBytes / MinimumWorkerBudgetBytes));
            int degree = Math.Min(
                validated.Options.MaxDegreeOfParallelism,
                memoryBoundedDegree);
            int workerCount = Math.Min(degree, orderedTiles.Length);
            int nextTileIndex = -1;
            var startGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var workers = new Task[workerCount];
            for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                workers[workerIndex] = RunWorkerAsync();
            }

            startGate.SetResult();
            await Task.WhenAll(workers).ConfigureAwait(false);

            async Task RunWorkerAsync()
            {
                int active = Interlocked.Increment(ref activeWorkers);
                UpdatePeak(ref peakConcurrency, active);
                try
                {
                    await startGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    while (true)
                    {
                        int index = Interlocked.Increment(ref nextTileIndex);
                        if (index >= orderedTiles.Length)
                        {
                            return;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        TileContext context = orderedTiles[index];
                        byte[] bytes = BuildTile(context, validated.Options, cancellationToken);
                        GraphTile.Create(context.GraphId, bytes);
                        long totalBytes = Interlocked.Add(ref bytesWritten, bytes.Length);
                        if (totalBytes > validated.Options.ScratchDiskBudgetBytes)
                        {
                            throw new TransitTileBuildException(
                                TransitTileBuildFailureCode.ResourceExhausted,
                                $"Transit output exceeds scratch-disk budget of {validated.Options.ScratchDiskBudgetBytes} bytes");
                        }

                        string relativePath = GraphTile.FileSuffix(context.GraphId);
                        string stagingPath = Path.Combine(stagingDirectory, relativePath);
                        string? stagingParent = Path.GetDirectoryName(stagingPath);
                        if (!string.IsNullOrEmpty(stagingParent))
                        {
                            Directory.CreateDirectory(stagingParent);
                        }

                        await File.WriteAllBytesAsync(
                            stagingPath,
                            bytes,
                            cancellationToken).ConfigureAwait(false);
                        artifacts[index] = new TileBuildArtifact(
                            relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                            Convert.ToHexString(SHA256.HashData(bytes)),
                            context.Nodes.Count,
                            context.Nodes.Sum(node => context.GetEdges(node).Count),
                            context.Routes.Count,
                            context.Departures.Count,
                            context.Schedules.Count,
                            context.Transfers.Count);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            }

            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            int nodes = 0;
            int edges = 0;
            int stops = 0;
            int routes = 0;
            int departures = 0;
            int schedules = 0;
            int transfers = 0;
            foreach (TileBuildArtifact? nullableArtifact in artifacts)
            {
                TileBuildArtifact artifact = nullableArtifact
                    ?? throw new TransitTileBuildException(
                        TransitTileBuildFailureCode.OutputValidationFailed,
                        "A transit tile worker completed without an artifact receipt.");
                hashes.Add(artifact.RelativePath, artifact.Sha256);
                nodes += artifact.NodeCount;
                edges += artifact.DirectedEdgeCount;
                stops += artifact.NodeCount;
                routes += artifact.RouteCount;
                departures += artifact.DepartureCount;
                schedules += artifact.ScheduleCount;
                transfers += artifact.TransferCount;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(validated.OutputDirectory) &&
                Directory.EnumerateFileSystemEntries(validated.OutputDirectory).Any())
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.InvalidConfiguration,
                    "Transit output directory must be absent or empty");
            }

            Directory.CreateDirectory(validated.OutputDirectory);
            foreach (string stagingPath in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(stagingDirectory, stagingPath);
                string outputPath = Path.Combine(validated.OutputDirectory, relativePath);
                string? outputParent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputParent))
                {
                    Directory.CreateDirectory(outputParent);
                }

                string temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.Move(stagingPath, temporaryPath);
                File.Move(temporaryPath, outputPath);
            }

            return new TransitTileBuildResult(
                validated.OutputDirectory,
                feeds.Count,
                tiles.Count,
                peakConcurrency,
                nodes,
                edges,
                stops,
                routes,
                departures,
                schedules,
                transfers,
                bytesWritten,
                hashes,
                Array.Empty<string>());
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static IReadOnlyDictionary<uint, TileContext> BuildContexts(
        IReadOnlyList<ParsedGtfsFeed> feeds,
        TransitTimeZoneResolver timeZoneResolver,
        CancellationToken cancellationToken)
    {
        var tiles = new SortedDictionary<uint, TileContext>();
        var nodeLookup = new Dictionary<string, TransitNodeSpec>(StringComparer.Ordinal);

        foreach (ParsedGtfsFeed feed in feeds.OrderBy(item => item.Prefix, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (GtfsStop station in feed.Stops.Values
                         .Where(stop => stop.LocationType == 1)
                         .OrderBy(stop => stop.Id, StringComparer.Ordinal))
            {
                string stationIdentity = Identity(feed.Prefix, station.Id);
                TransitNodeSpec egress = AddNode(
                    tiles,
                    nodeLookup,
                    new TransitNodeSpec(
                        stationIdentity + "_transit_egress",
                        station.Name,
                        station.Coordinate,
                        timeZoneResolver.Resolve(station.TimeZone, station.Coordinate),
                        NodeType.TransitEgress,
                        TransitAccess,
                        generated: true,
                        Traversability.Both,
                        modeChange: false));
                TransitNodeSpec stationNode = AddNode(
                    tiles,
                    nodeLookup,
                    new TransitNodeSpec(
                        stationIdentity,
                        station.Name,
                        station.Coordinate,
                        timeZoneResolver.Resolve(station.TimeZone, station.Coordinate),
                        NodeType.TransitStation,
                        TransitAccess,
                        generated: false,
                        Traversability.None,
                        modeChange: false));
                AddConnection(egress, stationNode, Use.EgressConnection);

                foreach (GtfsStop platform in feed.Stops.Values
                             .Where(stop => string.Equals(stop.ParentStation, station.Id, StringComparison.Ordinal))
                             .OrderBy(stop => stop.Id, StringComparer.Ordinal))
                {
                    uint access = platform.WheelchairBoarding == 2
                        ? GraphConstants.PedestrianAccess
                        : PlatformAccess;
                    TransitNodeSpec platformNode = AddNode(
                        tiles,
                        nodeLookup,
                        new TransitNodeSpec(
                            Identity(feed.Prefix, platform.Id),
                            platform.Name,
                            platform.Coordinate,
                            timeZoneResolver.Resolve(platform.TimeZone, platform.Coordinate),
                            NodeType.MultiUseTransitPlatform,
                            access,
                            generated: false,
                            Traversability.None,
                            modeChange: true));
                    AddConnection(stationNode, platformNode, Use.PlatformConnection);
                }
            }
        }

        AssignNodeIndexes(tiles);

        uint tripOrdinal = 0;
        var blockOrdinals = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (ParsedGtfsFeed feed in feeds.OrderBy(item => item.Prefix, StringComparer.Ordinal))
        {
            foreach (GtfsTrip trip in feed.Trips.Values.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                tripOrdinal++;
                GtfsRoute route = feed.Routes[trip.RouteId];
                GtfsAgency agency = feed.Agencies[route.AgencyId];
                GtfsService service = feed.Services[trip.ServiceId];
                IReadOnlyList<GtfsStopTime> stopTimes = feed.StopTimes[trip.Id];
                uint blockId = GetBlockOrdinal(feed.Prefix, trip.BlockId, blockOrdinals);

                for (int segmentIndex = 0; segmentIndex < stopTimes.Count - 1; segmentIndex++)
                {
                    GtfsStopTime originTime = stopTimes[segmentIndex];
                    GtfsStopTime destinationTime = stopTimes[segmentIndex + 1];
                    TransitNodeSpec origin = nodeLookup[Identity(feed.Prefix, originTime.StopId)];
                    TransitNodeSpec destination = nodeLookup[Identity(feed.Prefix, destinationTime.StopId)];
                    TileContext context = tiles[origin.TileId];
                    uint routeIndex = context.GetOrAddRoute(feed.Prefix, route, agency);
                    uint scheduleIndex = context.GetOrAddSchedule(feed.Prefix, service);
                    uint lineId = context.NextLineId();
                    int elapsed = destinationTime.ArrivalTime - originTime.DepartureTime;
                    if (elapsed < 0)
                    {
                        throw new TransitTileBuildException(
                            TransitTileBuildFailureCode.InvalidValue,
                            $"Trip {trip.Id} has negative elapsed time");
                    }

                    TransitDeparture departure;
                    if (feed.Frequencies.TryGetValue(trip.Id, out GtfsFrequency? frequency))
                    {
                        departure = new TransitDeparture(
                            lineId,
                            tripOrdinal,
                            routeIndex,
                            blockId,
                            0,
                            (uint)frequency.StartTime,
                            (uint)frequency.EndTime,
                            (uint)frequency.HeadwaySeconds,
                            (uint)elapsed,
                            scheduleIndex,
                            trip.WheelchairAccessible,
                            trip.BicycleAccessible);
                    }
                    else
                    {
                        departure = new TransitDeparture(
                            lineId,
                            tripOrdinal,
                            routeIndex,
                            blockId,
                            0,
                            (uint)originTime.DepartureTime,
                            (uint)elapsed,
                            scheduleIndex,
                            trip.WheelchairAccessible,
                            trip.BicycleAccessible);
                    }

                    int departureIndex = context.Departures.Count;
                    context.Departures.Add(new DepartureSpec(departure, trip.Headsign));
                    IReadOnlyList<PointLL> shape = ResolveShape(
                        feed,
                        trip,
                        originTime,
                        destinationTime,
                        origin,
                        destination);
                    context.AddEdge(
                        new TransitEdgeSpec(
                            origin,
                            destination,
                            route.Type == TransitType.Bus ? Use.Bus : Use.Rail,
                            lineId,
                            shape,
                            route.ShortName,
                            route.LongName,
                            StableId(feed.Prefix + "|shape|" + trip.ShapeId),
                            departureIndex));
                }
            }
        }

        return tiles;
    }

    private static TransitNodeSpec AddNode(
        IDictionary<uint, TileContext> tiles,
        IDictionary<string, TransitNodeSpec> nodeLookup,
        TransitNodeSpec node)
    {
        if (!nodeLookup.TryAdd(node.Identity, node))
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.ReferentialIntegrity,
                $"Duplicate transit node identity {node.Identity}");
        }

        uint tileId = checked((uint)TileHierarchy.GetTransitLevel().Tiles.TileId(node.Coordinate));
        node.TileId = tileId;
        if (!tiles.TryGetValue(tileId, out TileContext? context))
        {
            context = new TileContext(tileId);
            tiles.Add(tileId, context);
        }

        context.Nodes.Add(node);
        return node;
    }

    private static void AssignNodeIndexes(IEnumerable<KeyValuePair<uint, TileContext>> tiles)
    {
        foreach ((_, TileContext context) in tiles)
        {
            context.Nodes.Sort((left, right) => string.CompareOrdinal(left.Identity, right.Identity));
            for (int index = 0; index < context.Nodes.Count; index++)
            {
                context.Nodes[index].NodeIndex = checked((uint)index);
            }
        }
    }

    private static void AddConnection(
        TransitNodeSpec first,
        TransitNodeSpec second,
        Use use)
    {
        ulong stableId = StableId(first.Identity + "|" + second.Identity + "|" + use);
        IReadOnlyList<PointLL> forward = [first.Coordinate, second.Coordinate];
        IReadOnlyList<PointLL> reverse = [second.Coordinate, first.Coordinate];
        first.PendingConnections.Add(
            new TransitEdgeSpec(first, second, use, 0, forward, string.Empty, string.Empty, stableId, null));
        second.PendingConnections.Add(
            new TransitEdgeSpec(second, first, use, 0, reverse, string.Empty, string.Empty, stableId, null));
    }

    private static IReadOnlyList<PointLL> ResolveShape(
        ParsedGtfsFeed feed,
        GtfsTrip trip,
        GtfsStopTime originTime,
        GtfsStopTime destinationTime,
        TransitNodeSpec origin,
        TransitNodeSpec destination)
    {
        if (string.IsNullOrEmpty(trip.ShapeId))
        {
            return [origin.Coordinate, destination.Coordinate];
        }

        IReadOnlyList<PointLL> source = feed.Shapes[trip.ShapeId];
        double[] distances = BuildCumulativeDistances(source);
        double originDistance = ResolveShapeDistance(
            originTime.ShapeDistance,
            origin.Coordinate,
            source,
            distances);
        double destinationDistance = ResolveShapeDistance(
            destinationTime.ShapeDistance,
            destination.Coordinate,
            source,
            distances);
        if (originDistance >= destinationDistance)
        {
            return [origin.Coordinate, destination.Coordinate];
        }

        return SliceShape(
            source,
            distances,
            originDistance,
            destinationDistance,
            origin.Coordinate,
            destination.Coordinate);
    }

    private static double[] BuildCumulativeDistances(IReadOnlyList<PointLL> source)
    {
        var distances = new double[source.Count];
        for (int index = 1; index < source.Count; index++)
        {
            distances[index] = distances[index - 1] + source[index - 1].Distance(source[index]);
        }

        return distances;
    }

    private static double ResolveShapeDistance(
        double? suppliedDistance,
        PointLL stop,
        IReadOnlyList<PointLL> source,
        IReadOnlyList<double> distances)
    {
        if (suppliedDistance is < 0)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidValue,
                "GTFS shape_dist_traveled cannot be negative.");
        }

        if (suppliedDistance is > 0)
        {
            return Math.Min(suppliedDistance.Value, distances[^1]);
        }

        double nearestSquared = double.PositiveInfinity;
        double resolved = 0;
        for (int index = 0; index < source.Count - 1; index++)
        {
            PointLL first = source[index];
            PointLL second = source[index + 1];
            double longitudeDelta = second.Lng - first.Lng;
            double latitudeDelta = second.Lat - first.Lat;
            double denominator =
                (longitudeDelta * longitudeDelta)
                + (latitudeDelta * latitudeDelta);
            double fraction = denominator == 0
                ? 0
                : Math.Clamp(
                    (((stop.Lng - first.Lng) * longitudeDelta)
                        + ((stop.Lat - first.Lat) * latitudeDelta))
                    / denominator,
                    0,
                    1);
            double projectedLongitude = first.Lng + (longitudeDelta * fraction);
            double projectedLatitude = first.Lat + (latitudeDelta * fraction);
            double longitudeError = stop.Lng - projectedLongitude;
            double latitudeError = stop.Lat - projectedLatitude;
            double squared =
                (longitudeError * longitudeError)
                + (latitudeError * latitudeError);
            if (squared < nearestSquared)
            {
                nearestSquared = squared;
                resolved = fraction > 0.5 ? distances[index + 1] : distances[index];
            }
        }

        return resolved;
    }

    private static IReadOnlyList<PointLL> SliceShape(
        IReadOnlyList<PointLL> source,
        IReadOnlyList<double> distances,
        double originDistance,
        double destinationDistance,
        PointLL origin,
        PointLL destination)
    {
        var result = new List<PointLL>
        {
            InterpolateShapePoint(source, distances, originDistance),
        };
        for (int index = 1; index < source.Count - 1; index++)
        {
            if (distances[index] > originDistance && distances[index] < destinationDistance)
            {
                result.Add(source[index]);
            }
        }

        PointLL final = InterpolateShapePoint(source, distances, destinationDistance);
        if (!result[^1].Equals(final))
        {
            result.Add(final);
        }

        result[0] = origin;
        result[^1] = destination;
        return result;
    }

    private static PointLL InterpolateShapePoint(
        IReadOnlyList<PointLL> source,
        IReadOnlyList<double> distances,
        double distance)
    {
        double clamped = Math.Clamp(distance, 0, distances[^1]);
        int index = 0;
        while (index < distances.Count - 2 && distances[index + 1] < clamped)
        {
            index++;
        }

        double segmentLength = distances[index + 1] - distances[index];
        if (segmentLength <= 0)
        {
            return source[index];
        }

        double fraction = (clamped - distances[index]) / segmentLength;
        return new PointLL(
            source[index].Lng + ((source[index + 1].Lng - source[index].Lng) * fraction),
            source[index].Lat + ((source[index + 1].Lat - source[index].Lat) * fraction));
    }

    private static byte[] BuildTile(
        TileContext context,
        TransitTileBuildOptions options,
        CancellationToken cancellationToken)
    {
        var builder = new GraphTileBuilder(context.GraphId);
        PointLL tileBase = TileHierarchy.GetTransitLevel().Tiles.Base((int)context.TileId);
        builder.HeaderBuilder.SetBaseLl(tileBase);
        builder.HeaderBuilder.SetDatasetId(options.DatasetId);
        if (options.BuildId > ushort.MaxValue)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidConfiguration,
                "BuildId exceeds the Valhalla 3.8 header range");
        }

        builder.HeaderBuilder.SetRawChecksum(options.BuildId << GraphTileHeader.TileHashBits);
        uint dateCreated = checked((uint)(
            options.BuildDate.DayNumber -
            new DateOnly(2014, 1, 1).DayNumber));
        builder.AddTileCreationDate(dateCreated);

        foreach (RouteSpec routeSpec in context.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.TransitRoutes.Add(new TransitRoute(
                routeSpec.Route.Type,
                builder.AddName(Identity(routeSpec.FeedPrefix, routeSpec.Route.Id)),
                builder.AddName(Identity(routeSpec.FeedPrefix, routeSpec.Agency.Id)),
                builder.AddName(routeSpec.Agency.Name),
                builder.AddName(routeSpec.Agency.Website),
                routeSpec.Route.Color,
                routeSpec.Route.TextColor,
                builder.AddName(routeSpec.Route.ShortName),
                builder.AddName(routeSpec.Route.LongName),
                builder.AddName(routeSpec.Route.Description)));
        }

        foreach (ScheduleSpec scheduleSpec in context.Schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.TransitSchedules.Add(new TransitSchedule(
                scheduleSpec.Service.Days,
                scheduleSpec.Service.DaysOfWeek,
                scheduleSpec.Service.EndDay));
        }

        foreach (DepartureSpec departureSpec in context.Departures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransitDeparture departure = WithHeadsignOffset(departureSpec.Departure,
                builder.AddName(departureSpec.Headsign));
            builder.Departures.Add(departure);
        }

        foreach (TransitNodeSpec node in context.Nodes)
        {
            builder.TransitStops.Add(new TransitStop(
                builder.AddName(node.Identity),
                builder.AddName(node.Name),
                node.Generated,
                node.Traversability));
        }

        foreach (TransitNodeSpec node in context.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<TransitEdgeSpec> outgoing = context.GetEdges(node);
            var nodeInfo = new NodeInfo(
                tileBase,
                node.Coordinate,
                node.Access,
                node.Type,
                trafficSignal: false,
                taggedAccess: false,
                privateAccess: false,
                cashOnlyToll: false);
            nodeInfo.SetEdgeIndex(checked((uint)builder.DirectedEdges.Count));
            nodeInfo.SetEdgeCount(checked((uint)outgoing.Count));
            nodeInfo.SetLocalEdgeCount(checked((uint)outgoing.Count));
            nodeInfo.SetStopIndex(node.NodeIndex);
            nodeInfo.SetTimezone(node.TimeZone);
            nodeInfo.SetModeChange(node.ModeChange);

            for (int localIndex = 0; localIndex < outgoing.Count; localIndex++)
            {
                TransitEdgeSpec edgeSpec = outgoing[localIndex];
                nodeInfo.SetHeading(
                    (uint)localIndex,
                    Heading(edgeSpec.Shape[0], edgeSpec.Shape.Count > 1 ? edgeSpec.Shape[1] : edgeSpec.Shape[0]));
                var edge = DirectedEdge.Create();
                edge.SetEndNode(new GraphId(
                    edgeSpec.Destination.TileId,
                    TransitLevel,
                    edgeSpec.Destination.NodeIndex));
                edge.SetUse(edgeSpec.Use);
                edge.SetLineId(edgeSpec.LineId);
                edge.SetLength(LengthMeters(edgeSpec.Shape));
                edge.SetForwardAccess(TransitAccess);
                edge.SetReverseAccess(TransitAccess);
                edge.SetLocalEdgeIdx((uint)localIndex);
                uint logicalEdgeIndex = checked((uint)(edgeSpec.StableId & uint.MaxValue));
                uint edgeInfoOffset = builder.AddEdgeInfo(
                    logicalEdgeIndex,
                    new GraphId(node.TileId, TransitLevel, node.NodeIndex),
                    new GraphId(
                        edgeSpec.Destination.TileId,
                        TransitLevel,
                        edgeSpec.Destination.NodeIndex),
                    edgeSpec.StableId,
                    0,
                    0,
                    0,
                    edgeSpec.Shape,
                    string.IsNullOrEmpty(edgeSpec.ShortName)
                        ? Array.Empty<string>()
                        : [edgeSpec.ShortName, edgeSpec.LongName],
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    0,
                    out bool added);
                edge.SetEdgeInfoOffset(edgeInfoOffset);
                edge.SetForward(added);
                builder.DirectedEdges.Add(edge);
            }

            builder.Nodes.Add(nodeInfo);
        }

        return builder.StoreTileData();
    }

    private static TransitDeparture WithHeadsignOffset(
        TransitDeparture departure,
        uint headsignOffset)
        => departure.Type == TransitDeparture.FrequencySchedule
            ? new TransitDeparture(
                departure.LineId,
                departure.TripId,
                departure.RouteIndex,
                departure.BlockId,
                headsignOffset,
                departure.DepartureTime,
                departure.EndTime,
                departure.Frequency,
                departure.ElapsedTime,
                departure.ScheduleIndex,
                departure.WheelchairAccessible,
                departure.BicycleAccessible)
            : new TransitDeparture(
                departure.LineId,
                departure.TripId,
                departure.RouteIndex,
                departure.BlockId,
                headsignOffset,
                departure.DepartureTime,
                departure.ElapsedTime,
                departure.ScheduleIndex,
                departure.WheelchairAccessible,
                departure.BicycleAccessible);

    private static uint GetBlockOrdinal(
        string prefix,
        string blockId,
        IDictionary<string, uint> ordinals)
    {
        if (string.IsNullOrEmpty(blockId))
        {
            return 0;
        }

        string identity = prefix + "|" + blockId;
        if (!ordinals.TryGetValue(identity, out uint ordinal))
        {
            ordinal = checked((uint)ordinals.Count + 1);
            ordinals.Add(identity, ordinal);
        }

        return ordinal;
    }

    private static uint LengthMeters(IReadOnlyList<PointLL> points)
    {
        double length = 0;
        for (int index = 1; index < points.Count; index++)
        {
            length += points[index - 1].Distance(points[index]);
        }

        return Math.Max(1, checked((uint)length));
    }

    private static uint Heading(PointLL origin, PointLL destination)
    {
        if (origin.Equals(destination))
        {
            return 0;
        }

        double radians = Math.Atan2(
            destination.Lng - origin.Lng,
            destination.Lat - origin.Lat);
        double degrees = (radians * 180 / Math.PI + 360) % 360;
        return checked((uint)Math.Round(degrees, MidpointRounding.AwayFromZero)) % 360;
    }

    private static string Identity(string prefix, string localId)
        => prefix + "_" + localId;

    private static ulong StableId(string value)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offsetBasis;
        foreach (byte valueByte in Encoding.UTF8.GetBytes(value))
        {
            hash ^= valueByte;
            hash *= prime;
        }

        return hash & 0x0000FFFFFFFFFFFFul;
    }
    private static void UpdatePeak(ref int peak, int candidate)
    {
        int observed = Volatile.Read(ref peak);
        while (candidate > observed)
        {
            int prior = Interlocked.CompareExchange(ref peak, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }


    private static ValidatedRequest Validate(TransitTileBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Options);
        if (request.FeedPaths is null || request.FeedPaths.Count == 0)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidConfiguration,
                "At least one GTFS feed is required");
        }

        if (request.Options.MaxDegreeOfParallelism < 1 ||
            request.Options.MemoryBudgetBytes < 1024 * 1024 ||
            request.Options.ScratchDiskBudgetBytes < 1024 * 1024)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidConfiguration,
                "Parallelism and resource budgets must be positive and bounded");
        }

        string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        if (string.Equals(workingDirectory, outputDirectory, StringComparison.OrdinalIgnoreCase) ||
            outputDirectory.StartsWith(
                workingDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.UnsafePath,
                "Working and output directories must be separate");
        }

        Directory.CreateDirectory(workingDirectory);
        RejectReparsePoint(workingDirectory, nameof(request.WorkingDirectory));
        if (Directory.Exists(outputDirectory))
        {
            RejectReparsePoint(outputDirectory, nameof(request.OutputDirectory));
        }

        string[] feedPaths = request.FeedPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (feedPaths.Length != request.FeedPaths.Count)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidConfiguration,
                "GTFS feed paths must be unique");
        }
        string? timeZoneDatabasePath = null;
        if (!string.IsNullOrWhiteSpace(request.TimeZoneDatabasePath))
        {
            timeZoneDatabasePath = Path.GetFullPath(request.TimeZoneDatabasePath);
            if (!File.Exists(timeZoneDatabasePath))
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.InvalidConfiguration,
                    $"The transit timezone database does not exist: {timeZoneDatabasePath}");
            }

            RejectReparsePoint(timeZoneDatabasePath, nameof(request.TimeZoneDatabasePath));
        }

        return new ValidatedRequest(
            feedPaths,
            workingDirectory,
            outputDirectory,
            timeZoneDatabasePath,
            request.Options);
    }

    private static void RejectReparsePoint(string path, string parameterName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.UnsafePath,
                $"{parameterName} cannot be a symbolic link or reparse point");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ValidatedRequest(
        string[] FeedPaths,
        string WorkingDirectory,
        string OutputDirectory,
        string? TimeZoneDatabasePath,
        TransitTileBuildOptions Options);

    private sealed class TileContext
    {
        private readonly Dictionary<string, uint> _routeIndexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _scheduleIndexes = new(StringComparer.Ordinal);
        private uint _lineId;

        public TileContext(uint tileId)
        {
            TileId = tileId;
            GraphId = new GraphId(tileId, TransitLevel, 0);
        }

        public uint TileId { get; }

        public GraphId GraphId { get; }

        public List<TransitNodeSpec> Nodes { get; } = [];

        public Dictionary<TransitNodeSpec, List<TransitEdgeSpec>> OutgoingEdges { get; } = [];

        public List<RouteSpec> Routes { get; } = [];

        public List<ScheduleSpec> Schedules { get; } = [];

        public List<DepartureSpec> Departures { get; } = [];

        public List<TransitTransfer> Transfers { get; } = [];

        public uint GetOrAddRoute(string feedPrefix, GtfsRoute route, GtfsAgency agency)
        {
            string key = feedPrefix + "|" + route.Id;
            if (_routeIndexes.TryGetValue(key, out uint index))
            {
                return index;
            }

            index = checked((uint)Routes.Count);
            Routes.Add(new RouteSpec(feedPrefix, route, agency));
            _routeIndexes.Add(key, index);
            return index;
        }

        public uint GetOrAddSchedule(string feedPrefix, GtfsService service)
        {
            string key = feedPrefix + "|" + service.Id;
            if (_scheduleIndexes.TryGetValue(key, out uint index))
            {
                return index;
            }

            index = checked((uint)Schedules.Count);
            Schedules.Add(new ScheduleSpec(feedPrefix, service));
            _scheduleIndexes.Add(key, index);
            return index;
        }

        public uint NextLineId()
        {
            _lineId++;
            if (_lineId > GraphConstants.MaxTransitLineId)
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.ResourceExhausted,
                    "Transit line count exceeds Valhalla tile capacity");
            }

            return _lineId;
        }

        public void AddEdge(TransitEdgeSpec edge)
        {
            if (!OutgoingEdges.TryGetValue(edge.Origin, out List<TransitEdgeSpec>? edges))
            {
                edges = [];
                OutgoingEdges.Add(edge.Origin, edges);
            }

            edges.Add(edge);
        }

        public List<TransitEdgeSpec> GetEdges(TransitNodeSpec node)
        {
            var result = new List<TransitEdgeSpec>(node.PendingConnections);
            if (OutgoingEdges.TryGetValue(node, out List<TransitEdgeSpec>? transit))
            {
                result.AddRange(transit);
            }

            result.Sort((left, right) =>
            {
                int leftKind = left.Use is Use.EgressConnection or Use.PlatformConnection ? 0 : 1;
                int rightKind = right.Use is Use.EgressConnection or Use.PlatformConnection ? 0 : 1;
                int compare = leftKind.CompareTo(rightKind);
                return compare != 0
                    ? compare
                    : string.CompareOrdinal(left.Destination.Identity, right.Destination.Identity);
            });
            return result;
        }
    }

    private sealed class TransitNodeSpec
    {
        public TransitNodeSpec(
            string identity,
            string name,
            PointLL coordinate,
            uint timeZone,
            NodeType type,
            uint access,
            bool generated,
            Traversability traversability,
            bool modeChange)
        {
            Identity = identity;
            Name = name;
            Coordinate = coordinate;
            TimeZone = timeZone;
            Type = type;
            Access = access;
            Generated = generated;
            Traversability = traversability;
            ModeChange = modeChange;
        }

        public string Identity { get; }

        public string Name { get; }

        public PointLL Coordinate { get; }
        public uint TimeZone { get; }


        public NodeType Type { get; }

        public uint Access { get; }

        public bool Generated { get; }

        public Traversability Traversability { get; }

        public bool ModeChange { get; }

        public uint TileId { get; set; }

        public uint NodeIndex { get; set; }

        public List<TransitEdgeSpec> PendingConnections { get; } = [];
    }

    private sealed record TransitEdgeSpec(
        TransitNodeSpec Origin,
        TransitNodeSpec Destination,
        Use Use,
        uint LineId,
        IReadOnlyList<PointLL> Shape,
        string ShortName,
        string LongName,
        ulong StableId,
        int? DepartureIndex);
    private sealed record TileBuildArtifact(
        string RelativePath,
        string Sha256,
        int NodeCount,
        int DirectedEdgeCount,
        int RouteCount,
        int DepartureCount,
        int ScheduleCount,
        int TransferCount);


    private sealed record RouteSpec(
        string FeedPrefix,
        GtfsRoute Route,
        GtfsAgency Agency);

    private sealed record ScheduleSpec(
        string FeedPrefix,
        GtfsService Service);

    private sealed record DepartureSpec(
        TransitDeparture Departure,
        string Headsign);
}
