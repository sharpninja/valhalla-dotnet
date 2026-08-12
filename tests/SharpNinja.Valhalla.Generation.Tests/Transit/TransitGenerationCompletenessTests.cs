using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.TimeZones;
using SharpNinja.Valhalla.Generation.Transit;
using SharpNinja.Valhalla.Midgard;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Transit;

public sealed class TransitGenerationCompletenessTests
{
    [Fact]
    public async Task LongTripShape_IsSlicedPerStopPair()
    {
        string root = ManagedTransitGenerationTests.NewScratch();
        try
        {
            string feed = Path.Combine(root, "feed");
            await WriteThreeStopFeedAsync(feed, "America/Jamaica", includeStopTimeDistances: false);
            TransitTileBuildResult result = await BuildAsync(
                feed,
                Path.Combine(root, "shape-build"),
                timeZoneDatabasePath: null,
                maxDegreeOfParallelism: 1);

            GraphTile tile = await ReadOnlyTileAsync(result);
            IReadOnlyDictionary<string, IReadOnlyList<PointLL>> shapes = TransitLineShapesByOrigin(tile);

            Assert.Equal(2, shapes.Count);
            Assert.True(shapes["feed_platform_a"][^1].Distance(new PointLL(-77.295, 18.10)) < 2);
            Assert.True(shapes["feed_platform_b"][0].Distance(new PointLL(-77.295, 18.10)) < 2);
            Assert.True(shapes["feed_platform_b"][^1].Distance(new PointLL(-77.29, 18.10)) < 2);
            Assert.DoesNotContain(
                shapes["feed_platform_a"],
                point => point.Distance(new PointLL(-77.29, 18.10)) < 2);
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(root);
        }
    }

    [Fact]
    public async Task TimeZoneDatabase_AttributesTransitNodesWithOfficialIndex()
    {
        string root = ManagedTransitGenerationTests.NewScratch();
        try
        {
            string timeZoneDatabase = await BuildJamaicaTimeZoneDatabaseAsync(root);
            string feed = Path.Combine(root, "feed");
            await WriteThreeStopFeedAsync(feed, string.Empty, includeStopTimeDistances: true);
            TransitTileBuildResult result = await BuildAsync(
                feed,
                Path.Combine(root, "timezone-build"),
                timeZoneDatabase,
                maxDegreeOfParallelism: 1);

            GraphTile tile = await ReadOnlyTileAsync(result);
            Assert.All(
                Enumerable.Range(0, checked((int)tile.Header().Nodecount())),
                index => Assert.Equal(88u, tile.Node(index).Timezone()));
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(root);
        }
    }

    [Fact]
    public async Task ParallelTileConstruction_RespectsDegreeAndProducesDeterministicOutput()
    {
        string root = ManagedTransitGenerationTests.NewScratch();
        try
        {
            string feed = Path.Combine(root, "parallel-feed");
            await WriteMultiRegionFeedAsync(feed);
            TransitTileBuildResult serial = await BuildAsync(
                feed,
                Path.Combine(root, "serial"),
                timeZoneDatabasePath: null,
                maxDegreeOfParallelism: 1);
            TransitTileBuildResult parallel = await BuildAsync(
                feed,
                Path.Combine(root, "parallel"),
                timeZoneDatabasePath: null,
                maxDegreeOfParallelism: 4);

            Assert.True(serial.TileCount >= 4);
            Assert.Equal(1, serial.PeakConcurrency);
            Assert.InRange(parallel.PeakConcurrency, 2, 4);
            Assert.Equal(serial.OutputSha256, parallel.OutputSha256);
        }
        finally
        {
            ManagedTransitGenerationTests.DeleteScratch(root);
        }
    }

    private static async Task<TransitTileBuildResult> BuildAsync(
        string feed,
        string root,
        string? timeZoneDatabasePath,
        int maxDegreeOfParallelism)
    {
        var builder = new ManagedTransitTileBuilder();
        return await builder.BuildAsync(
            new TransitTileBuildRequest(
                [feed],
                Path.Combine(root, "work"),
                Path.Combine(root, "output"),
                timeZoneDatabasePath,
                new TransitTileBuildOptions(
                    maxDegreeOfParallelism,
                    128 * 1024 * 1024,
                    512 * 1024 * 1024,
                    new DateOnly(2026, 8, 8),
                    DatasetId: 42,
                    BuildId: 7,
                    DeterministicOutput: true)),
            TestContext.Current.CancellationToken);
    }

    private static async Task<GraphTile> ReadOnlyTileAsync(TransitTileBuildResult result)
    {
        KeyValuePair<string, string> item = Assert.Single(result.OutputSha256);
        string path = Path.Combine(
            result.OutputDirectory,
            item.Key.Replace('/', Path.DirectorySeparatorChar));
        string[] parts = item.Key.Split('/');
        uint tileId = checked(
            (uint.Parse(parts[^3], System.Globalization.CultureInfo.InvariantCulture) * 1_000_000)
            + (uint.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture) * 1_000)
            + uint.Parse(
                Path.GetFileNameWithoutExtension(parts[^1]),
                System.Globalization.CultureInfo.InvariantCulture));
        return GraphTile.Create(
            new GraphId(tileId, 3, 0),
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PointLL>> TransitLineShapesByOrigin(
        GraphTile tile)
    {
        var result = new Dictionary<string, IReadOnlyList<PointLL>>(StringComparer.Ordinal);
        for (int nodeIndex = 0; nodeIndex < tile.Header().Nodecount(); nodeIndex++)
        {
            NodeInfo node = tile.Node(nodeIndex);
            TransitStop stop = tile.TransitStop(checked((int)node.StopIndex));
            string identity = tile.GetName(stop.OneStopOffset);
            foreach (DirectedEdge edge in tile.GetDirectedEdges(node).Where(edge => edge.IsTransitLine))
            {
                result.Add(identity, tile.EdgeInfo(edge).Shape());
            }
        }

        return result;
    }

    private static async Task<string> BuildJamaicaTimeZoneDatabaseAsync(string root)
    {
        string source = FindRepositoryArtifact(
            "tests",
            "SharpNinja.Valhalla.Generation.Tests",
            "Fixtures",
            "Timezone",
            "2026c-jamaica",
            "timezone-2026c-jamaica.shp");
        string database = Path.Combine(root, "tz_world.sqlite");
        var builder = new ManagedTimeZoneDatabaseBuilder();
        await builder.BuildAsync(
            new TimeZoneDatabaseBuildRequest(
                source,
                "2026c",
                Path.Combine(root, "timezone-work"),
                database,
                64 * 1024 * 1024),
            TestContext.Current.CancellationToken);
        return database;
    }

    private static async Task WriteThreeStopFeedAsync(
        string feed,
        string explicitStopTimeZone,
        bool includeStopTimeDistances)
    {
        Directory.CreateDirectory(feed);
        string timeZoneColumn = string.IsNullOrEmpty(explicitStopTimeZone)
            ? string.Empty
            : explicitStopTimeZone;
        await WriteAsync(
            feed,
            "agency.txt",
            "agency_id,agency_name,agency_url,agency_timezone\n"
            + "agency,Test Transit,https://example.test,America/Jamaica\n");
        await WriteAsync(
            feed,
            "stops.txt",
            "stop_id,stop_name,stop_lat,stop_lon,location_type,parent_station,"
            + "wheelchair_boarding,stop_timezone\n"
            + $"station_a,Station A,18.10,-77.30,1,,1,{timeZoneColumn}\n"
            + $"platform_a,Platform A,18.10,-77.30,0,station_a,1,{timeZoneColumn}\n"
            + $"station_b,Station B,18.10,-77.295,1,,1,{timeZoneColumn}\n"
            + $"platform_b,Platform B,18.10,-77.295,0,station_b,1,{timeZoneColumn}\n"
            + $"station_c,Station C,18.10,-77.29,1,,1,{timeZoneColumn}\n"
            + $"platform_c,Platform C,18.10,-77.29,0,station_c,1,{timeZoneColumn}\n");
        await WriteAsync(
            feed,
            "routes.txt",
            "route_id,agency_id,route_short_name,route_long_name,route_type\n"
            + "route,agency,T1,Test Route,3\n");
        await WriteAsync(
            feed,
            "trips.txt",
            "route_id,service_id,trip_id,trip_headsign,shape_id\n"
            + "route,service,trip,Station C,shape\n");
        string distanceA = includeStopTimeDistances ? ",0" : string.Empty;
        string distanceB = includeStopTimeDistances ? ",535" : string.Empty;
        string distanceC = includeStopTimeDistances ? ",1070" : string.Empty;
        string distanceHeader = includeStopTimeDistances ? ",shape_dist_traveled" : string.Empty;
        await WriteAsync(
            feed,
            "stop_times.txt",
            "trip_id,arrival_time,departure_time,stop_id,stop_sequence"
            + distanceHeader
            + "\n"
            + $"trip,08:00:00,08:00:00,platform_a,1{distanceA}\n"
            + $"trip,08:05:00,08:05:00,platform_b,2{distanceB}\n"
            + $"trip,08:10:00,08:10:00,platform_c,3{distanceC}\n");
        await WriteAsync(
            feed,
            "calendar.txt",
            "service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,"
            + "start_date,end_date\n"
            + "service,1,1,1,1,1,1,1,20260101,20261231\n");
        await WriteAsync(
            feed,
            "shapes.txt",
            "shape_id,shape_pt_lat,shape_pt_lon,shape_pt_sequence\n"
            + "shape,18.10,-77.30,1\n"
            + "shape,18.10,-77.2975,2\n"
            + "shape,18.10,-77.295,3\n"
            + "shape,18.10,-77.2925,4\n"
            + "shape,18.10,-77.29,5\n");
    }

    private static async Task WriteMultiRegionFeedAsync(string feed)
    {
        Directory.CreateDirectory(feed);
        (string Id, double Latitude, double Longitude)[] regions =
        [
            ("jamaica", 18.10, -77.30),
            ("nashville", 36.16, -86.78),
            ("memphis", 35.15, -90.05),
            ("atlanta", 33.75, -84.39),
        ];

        var stops = new System.Text.StringBuilder(
            "stop_id,stop_name,stop_lat,stop_lon,location_type,parent_station,wheelchair_boarding\n");
        var trips = new System.Text.StringBuilder(
            "route_id,service_id,trip_id,trip_headsign\n");
        var stopTimes = new System.Text.StringBuilder(
            "trip_id,arrival_time,departure_time,stop_id,stop_sequence\n");

        foreach ((string id, double latitude, double longitude) in regions)
        {
            stops.AppendLine(
                FormattableString.Invariant(
                    $"station_{id},Station {id},{latitude},{longitude},1,,1"));
            stops.AppendLine(
                FormattableString.Invariant(
                    $"platform_{id},Platform {id},{latitude},{longitude},0,station_{id},1"));
            stops.AppendLine(
                FormattableString.Invariant(
                    $"station_{id}_2,Station {id} 2,{latitude},{longitude + 0.002},1,,1"));
            stops.AppendLine(
                FormattableString.Invariant(
                    $"platform_{id}_2,Platform {id} 2,{latitude},{longitude + 0.002},0,station_{id}_2,1"));
            trips.AppendLine($"route,service,trip_{id},Station {id} 2");
            stopTimes.AppendLine($"trip_{id},08:00:00,08:00:00,platform_{id},1");
            stopTimes.AppendLine($"trip_{id},08:05:00,08:05:00,platform_{id}_2,2");
        }

        await WriteAsync(
            feed,
            "agency.txt",
            "agency_id,agency_name,agency_url,agency_timezone\n"
            + "agency,Test Transit,https://example.test,America/Chicago\n");
        await WriteAsync(feed, "stops.txt", stops.ToString());
        await WriteAsync(
            feed,
            "routes.txt",
            "route_id,agency_id,route_short_name,route_long_name,route_type\n"
            + "route,agency,T1,Test Route,3\n");
        await WriteAsync(feed, "trips.txt", trips.ToString());
        await WriteAsync(feed, "stop_times.txt", stopTimes.ToString());
        await WriteAsync(
            feed,
            "calendar.txt",
            "service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,"
            + "start_date,end_date\n"
            + "service,1,1,1,1,1,1,1,20260101,20261231\n");
    }

    private static Task WriteAsync(string root, string name, string content)
        => File.WriteAllTextAsync(
            Path.Combine(root, name),
            content.Replace("\n", Environment.NewLine, StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }
}
