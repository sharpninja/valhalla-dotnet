using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Transit;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Transit;

public sealed class TransitGenerationParityTests
{
    [Fact]
    public async Task ManagedTransitGraph_MatchesOfficialFixture()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transit");
        string feedPath = Path.Combine(fixtureRoot, "MonacoBus");
        string officialPath = Path.Combine(
            fixtureRoot,
            "OfficialValhalla383MonacoBus",
            "3",
            "000",
            "769",
            "709.gph");
        string scratch = Path.Combine(Path.GetTempPath(), "valhalla-transit-" + Guid.NewGuid().ToString("N"));
        string work = Path.Combine(scratch, "work");
        string output = Path.Combine(scratch, "output");

        try
        {
            ITransitTileBuilder builder = new ManagedTransitTileBuilder();
            TransitTileBuildResult result = await builder.BuildAsync(
                new TransitTileBuildRequest(
                    [feedPath],
                    work,
                    output,
                    TimeZoneDatabasePath: null,
                    new TransitTileBuildOptions(
                        MaxDegreeOfParallelism: 1,
                        MemoryBudgetBytes: 64 * 1024 * 1024,
                        ScratchDiskBudgetBytes: 256 * 1024 * 1024,
                        BuildDate: new DateOnly(2026, 8, 8),
                        DatasetId: 0,
                        BuildId: 0,
                        DeterministicOutput: true)),
                TestContext.Current.CancellationToken);

            string managedPath = Path.Combine(output, GraphTile.FileSuffix(new GraphId(769709, 3, 0)));
            GraphTile official = GraphTile.Create(
                new GraphId(769709, 3, 0),
                await File.ReadAllBytesAsync(officialPath, TestContext.Current.CancellationToken));
            GraphTile managed = GraphTile.Create(
                new GraphId(769709, 3, 0),
                await File.ReadAllBytesAsync(managedPath, TestContext.Current.CancellationToken));

            Assert.Equal(1, result.TileCount);
            Assert.Equal(6, result.NodeCount);
            Assert.Equal(10, result.DirectedEdgeCount);
            Assert.Equal(6, result.StopCount);
            Assert.Equal(1, result.RouteCount);
            Assert.Equal(2, result.DepartureCount);
            Assert.Equal(1, result.ScheduleCount);
            Assert.Equal(0, result.TransferCount);
            TransitSemanticSnapshot expected = Snapshot(official);
            TransitSemanticSnapshot actual = Snapshot(managed);
            Assert.Equal(expected.Nodes, actual.Nodes);
            Assert.Equal(expected.Edges, actual.Edges);
            Assert.Equal(expected.Routes, actual.Routes);
            Assert.Equal(expected.Departures, actual.Departures);
            Assert.Equal(expected.Schedules, actual.Schedules);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static TransitSemanticSnapshot Snapshot(GraphTile tile)
    {
        GraphTileHeader header = tile.Header();
        var nodeNames = new string[header.Nodecount()];
        var nodes = new List<string>((int)header.Nodecount());

        for (int index = 0; index < header.Nodecount(); index++)
        {
            NodeInfo node = tile.Node(index);
            TransitStop stop = tile.TransitStop((int)node.StopIndex);
            string identity = tile.GetName(stop.OneStopOffset);
            nodeNames[index] = identity;
            var ll = node.LatLng(tile.BaseLl());
            nodes.Add(FormattableString.Invariant(
                $"{identity}|{node.Type}|{tile.GetName(stop.NameOffset)}|{ll.Lat:F5}|{ll.Lng:F5}|{node.Access}|{node.EdgeCount}|{node.ModeChange}|{stop.Generated}|{stop.Traversability}"));
        }

        var departuresByLine = Enumerable.Range(0, (int)header.Departurecount())
            .Select(tile.TransitDeparture)
            .ToDictionary(departure => departure.LineId);

        var edges = new List<string>((int)header.Directededgecount());
        for (int nodeIndex = 0; nodeIndex < header.Nodecount(); nodeIndex++)
        {
            NodeInfo node = tile.Node(nodeIndex);
            for (uint localIndex = 0; localIndex < node.EdgeCount; localIndex++)
            {
                DirectedEdge edge = tile.DirectedEdge((int)(node.EdgeIndex + localIndex));
                string transit = string.Empty;
                if (edge.IsTransitLine)
                {
                    TransitDeparture departure = departuresByLine[edge.LineId];
                    transit = FormattableString.Invariant(
                        $"|{departure.Type}|{tile.GetName(departure.HeadsignOffset)}|{departure.DepartureTime}|{departure.ElapsedTime}|{(departure.Type == TransitDeparture.FrequencySchedule ? departure.EndTime : 0)}|{(departure.Type == TransitDeparture.FrequencySchedule ? departure.Frequency : 0)}");
                }

                edges.Add(FormattableString.Invariant(
                    $"{nodeNames[nodeIndex]}->{nodeNames[edge.EndNode.Id()]}|{edge.Use}|{edge.Length}|{edge.ForwardAccess}|{edge.ReverseAccess}{transit}"));
            }
        }

        var routes = Enumerable.Range(0, (int)header.Routecount())
            .Select(index => tile.TransitRoute(index))
            .Select(route => FormattableString.Invariant(
                $"{route.RouteType}|{tile.GetName(route.OneStopOffset)}|{tile.GetName(route.OperatedByNameOffset)}|{tile.GetName(route.OperatedByWebsiteOffset)}|{tile.GetName(route.ShortNameOffset)}|{tile.GetName(route.LongNameOffset)}|{tile.GetName(route.DescriptionOffset)}|{route.RouteColor:X8}|{route.RouteTextColor:X8}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var departures = Enumerable.Range(0, (int)header.Departurecount())
            .Select(index => tile.TransitDeparture(index))
            .Select(departure => FormattableString.Invariant(
                $"{departure.Type}|{tile.GetName(departure.HeadsignOffset)}|{departure.DepartureTime}|{departure.ElapsedTime}|{(departure.Type == TransitDeparture.FrequencySchedule ? departure.EndTime : 0)}|{(departure.Type == TransitDeparture.FrequencySchedule ? departure.Frequency : 0)}|{departure.WheelchairAccessible}|{departure.BicycleAccessible}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var schedules = Enumerable.Range(0, (int)header.Schedulecount())
            .Select(index => tile.TransitSchedule(index))
            .Select(schedule => FormattableString.Invariant(
                $"{schedule.Days:X16}|{schedule.DaysOfWeek}|{schedule.EndDay}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new TransitSemanticSnapshot(
            nodes.Order(StringComparer.Ordinal).ToArray(),
            edges.Order(StringComparer.Ordinal).ToArray(),
            routes,
            departures,
            schedules);
    }

    private sealed record TransitSemanticSnapshot(
        IReadOnlyList<string> Nodes,
        IReadOnlyList<string> Edges,
        IReadOnlyList<string> Routes,
        IReadOnlyList<string> Departures,
        IReadOnlyList<string> Schedules);
}
