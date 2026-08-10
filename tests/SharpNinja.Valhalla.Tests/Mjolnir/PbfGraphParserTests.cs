// Tests for the faithful C# port of the Valhalla mjolnir PBFGraphParser orchestration.
// Source: valhalla/src/mjolnir/pbfgraphparser.cc + test/graphparser.cc @ 3.7.0
//
// The upstream gtests (GraphParser.TestBollardsGatesAndAccess / TestBicycleTrafficSignals /
// TestBus / TestBike / TestExits / ...) drive PBFGraphParser over large binary .osm.pbf
// fixtures (liechtenstein / rome / harrisburg / baltimore) that are not reproducible here.
// These ports build small synthetic .osm.pbf streams in-process (the same minimal protobuf
// encoder used by OsmPbfReaderTests) that exercise the same behaviors the upstream cases
// assert, then run the full Parse() three-pass pipeline and check the resulting OSMData /
// OSMWay / OSMWayNode:
//   - bus-only ways (psv access)               -> TestBus
//   - residential way, all modes both ways     -> way() defaults
//   - oneway blocks the backward auto direction
//   - footway = pedestrian only
//   - construction shut-off
//   - gate / bollard / border_control node access masks + intersection (TestBollardsGatesAndAccess)
//   - traffic-signal / stop / give_way control-node flags (TestBicycleTrafficSignals)
//   - motorway_junction node ref + exit_to (TestExits)
//   - bicycle route relation -> bike_relations (TestBike)
//   - simple turn restriction relation -> osmdata.restrictions
//   - cul-de-sac inference on a low-class loop road

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class PbfGraphParserTests
{
    // ---- helpers --------------------------------------------------------------

    private static OSMWay GetWay(PbfGraphParser parser, ulong wayId) =>
        parser.Ways.First(w => w.WayId() == wayId);

    private static OSMNode GetNode(PbfGraphParser parser, ulong nodeId) =>
        parser.WayNodes.First(wn => wn.Node.Osmid == nodeId).Node;

    internal static (PbfGraphParser parser, OSMData data) Run(PbfBuilder builder, PbfGraphParserOptions? options = null)
    {
        byte[] pbf = builder.Build();
        string path = Path.Combine(Path.GetTempPath(), $"tm_pbf_{System.Guid.NewGuid():N}.osm.pbf");
        File.WriteAllBytes(path, pbf);
        try
        {
            var parser = new PbfGraphParser(options ?? new PbfGraphParserOptions());
            OSMData data = parser.Parse(new[] { path });
            return (parser, data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildParsedTileSet_ReleasesConsumedParserBuildSequences()
    {
        var builder = new PbfBuilder();
        builder.AddNode(1, 36.1200, -86.6800);
        builder.AddNode(2, 36.1210, -86.6790);
        builder.AddNode(3, 36.1220, -86.6780);
        builder.AddWay(
            100,
            new ulong[] { 1, 2, 3 },
            new()
            {
                ["highway"] = "residential",
                ["access"] = "private",
            });

        (PbfGraphParser parser, OSMData data) = Run(builder);
        Assert.NotEmpty(parser.Ways);
        Assert.NotEmpty(parser.WayNodes);
        Assert.NotEmpty(parser.Access);

        string tileDirectory = Path.Combine(
            Path.GetTempPath(),
            "valhalla-parser-release-" + Guid.NewGuid().ToString("N"));

        try
        {
            TileBuilderResult result = TileBuilder.BuildParsedTileSet(
                parser,
                data,
                tileDirectory,
                new TileBuilderConfig
                {
                    Hierarchy = false,
                    Shortcuts = false,
                    MaxDegreeOfParallelism = 1,
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.Empty(parser.Ways);
            Assert.Empty(parser.WayNodes);
            Assert.Empty(parser.Access);
        }
        finally
        {
            if (Directory.Exists(tileDirectory))
            {
                Directory.Delete(tileDirectory, recursive: true);
            }
        }
    }

    // ---- way-pass behaviors ---------------------------------------------------

    [Fact]
    public void Residential_AllModesBothDirections()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(100, new ulong[] { 1, 2 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 100);
        Assert.True(way.AutoForward());
        Assert.True(way.AutoBackward());
        Assert.True(way.BikeForward());
        Assert.True(way.BikeBackward());
        Assert.True(way.PedestrianForward());
        Assert.True(way.PedestrianBackward());
        Assert.Equal(RoadClass.Residential, way.RoadClassValue());
        Assert.Equal(Use.Road, way.UseValue());
    }

    [Fact]
    public void Oneway_BlocksAutoBackward()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(101, new ulong[] { 1, 2 }, new() { ["highway"] = "residential", ["oneway"] = "yes" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 101);
        Assert.True(way.Oneway());
        Assert.True(way.AutoForward());
        Assert.False(way.AutoBackward());
    }

    [Fact]
    public void Footway_PedestrianOnly()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(102, new ulong[] { 1, 2 }, new() { ["highway"] = "footway" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 102);
        Assert.True(way.PedestrianForward());
        Assert.False(way.AutoForward());
        Assert.False(way.AutoBackward());
        Assert.Equal(Use.Footway, way.UseValue());
    }

    [Fact]
    public void Construction_ShutsOffAllAccess()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        // include_construction so the way is kept; access is still shut off.
        b.AddWay(103, new ulong[] { 1, 2 }, new() { ["highway"] = "construction", ["construction"] = "residential" });

        (PbfGraphParser parser, _) = Run(b, new PbfGraphParserOptions { IncludeConstruction = true });

        OSMWay way = GetWay(parser, 103);
        Assert.Equal(Use.Construction, way.UseValue());
        Assert.False(way.AutoForward());
        Assert.False(way.AutoBackward());
        Assert.False(way.BikeForward());
        Assert.False(way.PedestrianForward());
    }

    [Fact]
    public void BusOnly_Access()
    {
        // access=psv => bus + taxi only (mirrors TestBus's psv-access ways).
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(104, new ulong[] { 1, 2 }, new() { ["highway"] = "service", ["access"] = "psv" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 104);
        Assert.True(way.BusForward());
        Assert.True(way.TaxiForward());
        Assert.False(way.AutoForward());
    }

    [Fact]
    public void Surface_DefaultedFromRoadClass()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(105, new ulong[] { 1, 2 }, new() { ["highway"] = "primary" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 105);
        // No surface tag -> paved_smooth for high road classes.
        Assert.Equal(Surface.PavedSmooth, way.SurfaceValue());
    }

    [Fact]
    public void DegenerateWay_LessThanTwoNodes_Dropped()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddWay(106, new ulong[] { 1 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        Assert.DoesNotContain(parser.Ways, w => w.WayId() == 106);
    }

    [Fact]
    public void ClosedBuilding_Dropped()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.0);
        b.AddNode(3, 41.001, 12.001);
        // closed ring with building tag -> discarded as an area.
        b.AddWay(107, new ulong[] { 1, 2, 3, 1 }, new() { ["highway"] = "residential", ["building"] = "yes" });

        (PbfGraphParser parser, _) = Run(b);

        Assert.DoesNotContain(parser.Ways, w => w.WayId() == 107);
    }

    [Fact]
    public void MaxSpeed_TaggedAndLimit()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(108, new ulong[] { 1, 2 }, new() { ["highway"] = "primary", ["maxspeed"] = "80" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 108);
        Assert.True(way.TaggedSpeed());
        Assert.Equal((byte)80, way.SpeedLimit());
        Assert.Equal((byte)80, way.Speed());
    }

    // ---- node-pass behaviors --------------------------------------------------

    [Fact]
    public void EndNodes_AreIntersections()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(110, new ulong[] { 1, 2, 3 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        Assert.True(GetNode(parser, 1).Intersection());
        Assert.True(GetNode(parser, 3).Intersection());
    }

    [Fact]
    public void Gate_MarkedIntersectionWithFullAccess()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001, new() { ["barrier"] = "gate" });
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(111, new ulong[] { 1, 2, 3 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        OSMNode gate = GetNode(parser, 2);
        Assert.True(gate.Intersection());
        Assert.Equal(NodeType.Gate, gate.Type());
        // Default gate (no access tag) lets everything through.
        uint expected = GraphConstants.AutoAccess | GraphConstants.HovAccess | GraphConstants.TaxiAccess |
                        GraphConstants.TruckAccess | GraphConstants.BusAccess | GraphConstants.EmergencyAccess |
                        GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess |
                        GraphConstants.BicycleAccess | GraphConstants.MopedAccess | GraphConstants.MotorcycleAccess;
        Assert.Equal(expected, gate.Access());
    }

    [Fact]
    public void Bollard_BlocksMotorVehicles()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001, new() { ["barrier"] = "bollard" });
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(112, new ulong[] { 1, 2, 3 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        OSMNode bollard = GetNode(parser, 2);
        Assert.True(bollard.Intersection());
        Assert.Equal(NodeType.Bollard, bollard.Type());
        // Bollard with no explicit access: foot + wheelchair + bike only.
        uint expected = GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess | GraphConstants.BicycleAccess;
        Assert.Equal(expected, bollard.Access());
    }

    [Fact]
    public void BorderControl_FullAccess()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001, new() { ["barrier"] = "border_control" });
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(113, new ulong[] { 1, 2, 3 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        OSMNode node = GetNode(parser, 2);
        Assert.True(node.Intersection());
        Assert.Equal(NodeType.BorderControl, node.Type());
        uint expected = GraphConstants.AutoAccess | GraphConstants.HovAccess | GraphConstants.TaxiAccess |
                        GraphConstants.TruckAccess | GraphConstants.BusAccess | GraphConstants.EmergencyAccess |
                        GraphConstants.PedestrianAccess | GraphConstants.WheelchairAccess |
                        GraphConstants.BicycleAccess | GraphConstants.MopedAccess | GraphConstants.MotorcycleAccess;
        Assert.Equal(expected, node.Access());
    }

    [Fact]
    public void TrafficSignal_StopAndGiveWayDirection()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001, new() { ["highway"] = "traffic_signals" });
        b.AddNode(3, 41.002, 12.002, new() { ["highway"] = "stop", ["direction"] = "forward" });
        b.AddNode(4, 41.003, 12.003, new() { ["highway"] = "give_way", ["direction"] = "both" });
        b.AddNode(5, 41.004, 12.004);
        b.AddWay(114, new ulong[] { 1, 2, 3, 4, 5 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        Assert.True(GetNode(parser, 2).TrafficSignal());

        OSMNode stop = GetNode(parser, 3);
        Assert.True(stop.StopSign());
        Assert.True(stop.ForwardStop());
        Assert.False(stop.BackwardStop());
        Assert.True(stop.Direction());

        OSMNode yield = GetNode(parser, 4);
        Assert.True(yield.YieldSign());
        Assert.True(yield.ForwardYield());
        Assert.True(yield.BackwardYield());
    }

    [Fact]
    public void MotorwayJunction_RefAndExitTo()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001, new()
        {
            ["highway"] = "motorway_junction",
            ["ref"] = "51A-B",
            ["exit_to"] = "PA441",
        });
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(115, new ulong[] { 1, 2, 3 }, new() { ["highway"] = "motorway" });

        (PbfGraphParser parser, OSMData data) = Run(b);

        OSMNode node = GetNode(parser, 2);
        Assert.Equal(NodeType.MotorWayJunction, node.Type());
        Assert.True(node.HasRef());
        Assert.Equal("51A-B", data.NodeNames.Name(node.RefIndex()));
        Assert.Equal("PA441", data.NodeNames.Name(node.ExitToIndex()));
    }

    // ---- relation-pass behaviors ----------------------------------------------

    [Fact]
    public void SimpleTurnRestriction_Recorded()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddNode(3, 41.002, 12.002);
        b.AddNode(4, 41.003, 12.003);
        b.AddWay(200, new ulong[] { 1, 2 }, new() { ["highway"] = "residential" }); // from
        b.AddWay(201, new ulong[] { 3, 4 }, new() { ["highway"] = "residential" }); // to
        // restriction relation: from way 200, via node 2, to way 201, no_left_turn (type 0).
        b.AddRelation(900, new()
        {
            ["type"] = "restriction",
            ["restriction"] = "0",
        }, new[]
        {
            (200UL, OsmMemberType.Way, "from"),
            (2UL, OsmMemberType.Node, "via"),
            (201UL, OsmMemberType.Way, "to"),
        });

        (_, OSMData data) = Run(b);

        IReadOnlyList<OSMRestriction> restrictions = data.RestrictionsFor(200);
        Assert.Single(restrictions);
        OSMRestriction r = restrictions[0];
        Assert.Equal(RestrictionType.NoLeftTurn, r.TypeValue());
        Assert.Equal(2UL, r.Via());
        Assert.Equal(201UL, r.To());
    }

    [Fact]
    public void BicycleRouteRelation_RecordedAsBikeNetwork()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddWay(300, new ulong[] { 1, 2 }, new() { ["highway"] = "residential" });
        // bicycle route relation with rcn network mask (2) on member way 300.
        b.AddRelation(901, new()
        {
            ["type"] = "route",
            ["route"] = "bicycle",
            ["network"] = "rcn",
            ["bike_network_mask"] = "2",
            ["ref"] = "5",
            ["name"] = "Test Route",
        }, new[]
        {
            (300UL, OsmMemberType.Way, string.Empty),
        });

        (_, OSMData data) = Run(b);

        IReadOnlyList<OSMBike> bikes = data.BikeRelationsFor(300);
        Assert.Single(bikes);
        Assert.Equal(2, bikes[0].BikeNetwork); // rcn
    }

    [Fact]
    public void LaneConnectivityRelation_Recorded()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(400, new ulong[] { 1, 2 }, new() { ["highway"] = "primary" });
        b.AddWay(401, new ulong[] { 2, 3 }, new() { ["highway"] = "primary" });
        b.AddRelation(902, new()
        {
            ["type"] = "connectivity",
            ["to:lanes"] = "left|through",
            ["from:lanes"] = "left|through",
        }, new[]
        {
            (400UL, OsmMemberType.Way, "from"),
            (401UL, OsmMemberType.Way, "to"),
        });

        (_, OSMData data) = Run(b);

        Assert.True(data.LaneConnectivityMap.ContainsKey(401));
        Assert.Single(data.LaneConnectivityMap[401]);
        Assert.Equal(401u, data.LaneConnectivityMap[401][0].ToWayId);
        Assert.Equal(400u, data.LaneConnectivityMap[401][0].FromWayId);
    }

    // ---- cul-de-sac inference -------------------------------------------------

    [Fact]
    public void Loop_LowClassRoad_MarkedCuldesac()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.0);
        b.AddNode(3, 41.001, 12.001);
        b.AddNode(4, 41.0, 12.001);
        // A closed loop (1->2->3->4->1) residential road that is not a roundabout; no other way
        // touches its nodes -> a cul-de-sac. residential => use=Road, road_class=Residential
        // (> tertiary), which is the candidate condition in way().
        b.AddWay(500, new ulong[] { 1, 2, 3, 4, 1 }, new() { ["highway"] = "residential" });

        (PbfGraphParser parser, _) = Run(b);

        OSMWay way = GetWay(parser, 500);
        Assert.Equal(Use.Culdesac, way.UseValue());
    }

    // ---- OSMData counts -------------------------------------------------------

    [Fact]
    public void Counts_WayAndNodeCountsPopulated()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 41.0, 12.0);
        b.AddNode(2, 41.001, 12.001);
        b.AddNode(3, 41.002, 12.002);
        b.AddWay(600, new ulong[] { 1, 2, 3 }, new() { ["highway"] = "residential" });

        (_, OSMData data) = Run(b);

        Assert.Equal(1UL, data.OsmWayCount);
        Assert.Equal(3UL, data.OsmWayNodeCount);
        Assert.True(data.Initialized);
    }

    // ---- Valhalla 3.8.3 road behavior ----------------------------------------

    [Fact]
    public void PedestrianArea_DefaultPolicyDropsWay()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 36.10, -86.80);
        b.AddNode(2, 36.10, -86.79);
        b.AddNode(3, 36.11, -86.79);
        b.AddWay(
            700,
            new ulong[] { 1, 2, 3, 1 },
            new() { ["highway"] = "pedestrian", ["area"] = "yes" });

        (PbfGraphParser parser, _) = Run(b);

        Assert.DoesNotContain(parser.Ways, way => way.WayId() == 700);
    }

    [Fact]
    public void PedestrianArea_EnabledRetainsAreaButGraphBuilderSkipsRing()
    {
        var b = new PbfBuilder();
        b.AddNode(1, 36.10, -86.80);
        b.AddNode(2, 36.10, -86.79);
        b.AddNode(3, 36.11, -86.79);
        b.AddWay(
            701,
            new ulong[] { 1, 2, 3, 1 },
            new() { ["highway"] = "pedestrian", ["area"] = "yes" });

        (PbfGraphParser parser, _) = Run(b, new PbfGraphParserOptions { PedestrianAreas = true });

        OSMWay area = GetWay(parser, 701);
        Assert.True(area.Area());
        Assert.Empty(GraphBuilder.BuildEdges(parser.Ways, parser.WayNodes).Edges);
    }

    [Theory]
    [InlineData("clay")]
    [InlineData("laterite")]
    public void Surface_Valhalla383DirtValues_AreClassifiedAsDirt(string surface)
    {
        var b = new PbfBuilder();
        b.AddNode(1, 36.10, -86.80);
        b.AddNode(2, 36.11, -86.79);
        b.AddWay(
            702,
            new ulong[] { 1, 2 },
            new() { ["highway"] = "track", ["surface"] = surface });

        (PbfGraphParser parser, _) = Run(b);

        Assert.Equal(Surface.Dirt, GetWay(parser, 702).SurfaceValue());
    }

    // =========================================================================
    // Minimal .osm.pbf builder (a flexible version of the OsmPbfReaderTests fixture).
    // Emits a single OSMHeader + a single OSMData PrimitiveBlock with one group holding
    // all regular nodes, ways, and relations. Strings are deduped into a string table.
    // =========================================================================

    [Fact]
    public void WayLanguageAndPronunciationTags_AreRetainedForGraphGeneration()
    {
        var builder = new PbfBuilder();
        builder.AddNode(1, 41.0, 12.0);
        builder.AddNode(2, 41.001, 12.001);
        builder.AddWay(
            700,
            new ulong[] { 1, 2 },
            new()
            {
                ["highway"] = "residential",
                ["name"] = "Murfreesboro Road",
                ["name:es"] = "Camino Murfreesboro",
                ["name:pronunciation"] = "mur frees burrow",
                ["name:es:pronunciation:nt-sampa"] = "kah mee noh",
                ["ref"] = "US 41",
                ["ref:en:pronunciation"] = "you ess forty one",
            });

        (PbfGraphParser parser, OSMData data) = Run(builder);
        OSMWay way = GetWay(parser, 700);

        Assert.Equal("Murfreesboro Road", data.NameOffsetMap.Name(way.NameIndex));
        Assert.Equal("US 41", data.NameOffsetMap.Name(way.RefIndex));
        Assert.Equal("es", data.NameOffsetMap.Name(way.NameLangIndex));

        OSMLinguisticName spanishName = Assert.Single(way.LinguisticNames);
        Assert.Equal(OSMLinguisticType.Name, spanishName.Type);
        Assert.Equal(Language.Es, spanishName.Language);
        Assert.Equal("Camino Murfreesboro", spanishName.Text);

        Assert.Collection(
            way.Pronunciations.OrderBy(value => value.Type).ThenBy(value => value.Alphabet),
            value =>
            {
                Assert.Equal(OSMLinguisticType.Name, value.Type);
                Assert.Equal(Language.None, value.Language);
                Assert.Equal(PronunciationAlphabet.Ipa, value.Alphabet);
                Assert.Equal("mur frees burrow", value.Text);
            },
            value =>
            {
                Assert.Equal(OSMLinguisticType.Name, value.Type);
                Assert.Equal(Language.Es, value.Language);
                Assert.Equal(PronunciationAlphabet.NtSampa, value.Alphabet);
                Assert.Equal("kah mee noh", value.Text);
            },
            value =>
            {
                Assert.Equal(OSMLinguisticType.Ref, value.Type);
                Assert.Equal(Language.En, value.Language);
                Assert.Equal(PronunciationAlphabet.Ipa, value.Alphabet);
                Assert.Equal("you ess forty one", value.Text);
            });
        Assert.True(way.HasPronunciationTags());
    }

    internal sealed class PbfBuilder
    {
        private readonly List<(ulong id, double lat, double lon, Dictionary<string, string> tags)> _nodes = new();
        private readonly List<(ulong id, ulong[] refs, Dictionary<string, string> tags)> _ways = new();
        private readonly List<(ulong id, Dictionary<string, string> tags, (ulong id, OsmMemberType type, string role)[] members)> _relations = new();

        public void AddNode(ulong id, double lat, double lon, Dictionary<string, string>? tags = null) =>
            _nodes.Add((id, lat, lon, tags ?? new Dictionary<string, string>()));

        public void AddWay(ulong id, ulong[] refs, Dictionary<string, string> tags) =>
            _ways.Add((id, refs, tags));

        public void AddRelation(ulong id, Dictionary<string, string> tags, (ulong id, OsmMemberType type, string role)[] members) =>
            _relations.Add((id, tags, members));

        public byte[] Build()
        {
            using var output = new MemoryStream();
            WriteFileBlock(output, "OSMHeader", BuildHeaderBlock());
            WriteFileBlock(output, "OSMData", BuildPrimitiveBlock());
            return output.ToArray();
        }

        // -- string table --
        private readonly List<string> _strings = new() { string.Empty };
        private readonly Dictionary<string, int> _stringIndex = new() { [string.Empty] = 0 };

        private uint Intern(string s)
        {
            if (_stringIndex.TryGetValue(s, out int idx))
            {
                return (uint)idx;
            }

            idx = _strings.Count;
            _strings.Add(s);
            _stringIndex[s] = idx;
            return (uint)idx;
        }

        private static byte[] BuildHeaderBlock()
        {
            const double Nano = 1e9;
            var bbox = new ProtoWriter();
            bbox.WriteSInt64Field(1, (long)(12.0 * Nano)); // left
            bbox.WriteSInt64Field(2, (long)(13.0 * Nano)); // right
            bbox.WriteSInt64Field(3, (long)(42.0 * Nano)); // top
            bbox.WriteSInt64Field(4, (long)(41.0 * Nano)); // bottom

            var header = new ProtoWriter();
            header.WriteBytesField(1, bbox.ToArray());
            header.WriteStringField(4, "OsmSchema-V0.6");
            return header.ToArray();
        }

        private byte[] BuildPrimitiveBlock()
        {
            static long Coord(double deg) => (long)System.Math.Round(deg * 1e9 / 100.0);

            var group = new ProtoWriter();

            // Regular nodes (field 1). Sorted by id (the parser requires sorted input).
            foreach ((ulong id, double lat, double lon, Dictionary<string, string> tags) in _nodes.OrderBy(n => n.id))
            {
                var node = new ProtoWriter();
                node.WriteSInt64Field(1, (long)id);
                if (tags.Count > 0)
                {
                    var keys = new List<ulong>();
                    var vals = new List<ulong>();
                    foreach (KeyValuePair<string, string> t in tags)
                    {
                        keys.Add(Intern(t.Key));
                        vals.Add(Intern(t.Value));
                    }

                    node.WritePackedVarintsField(2, keys);
                    node.WritePackedVarintsField(3, vals);
                }

                node.WriteSInt64Field(8, Coord(lat));
                node.WriteSInt64Field(9, Coord(lon));
                group.WriteBytesField(1, node.ToArray());
            }

            // Ways (field 3). Sorted by id.
            foreach ((ulong id, ulong[] refs, Dictionary<string, string> tags) in _ways.OrderBy(w => w.id))
            {
                var way = new ProtoWriter();
                way.WriteVarintField(1, id);
                if (tags.Count > 0)
                {
                    var keys = new List<ulong>();
                    var vals = new List<ulong>();
                    foreach (KeyValuePair<string, string> t in tags)
                    {
                        keys.Add(Intern(t.Key));
                        vals.Add(Intern(t.Value));
                    }

                    way.WritePackedVarintsField(2, keys);
                    way.WritePackedVarintsField(3, vals);
                }

                // Delta-encode refs.
                var deltas = new List<long>();
                long prev = 0;
                foreach (ulong r in refs)
                {
                    deltas.Add((long)r - prev);
                    prev = (long)r;
                }

                way.WritePackedSInt64Field(8, deltas);
                group.WriteBytesField(3, way.ToArray());
            }

            // Relations (field 4). Sorted by id.
            foreach ((ulong id, Dictionary<string, string> tags, (ulong id, OsmMemberType type, string role)[] members)
                     in _relations.OrderBy(r => r.id))
            {
                var rel = new ProtoWriter();
                rel.WriteVarintField(1, id);
                if (tags.Count > 0)
                {
                    var keys = new List<ulong>();
                    var vals = new List<ulong>();
                    foreach (KeyValuePair<string, string> t in tags)
                    {
                        keys.Add(Intern(t.Key));
                        vals.Add(Intern(t.Value));
                    }

                    rel.WritePackedVarintsField(2, keys);
                    rel.WritePackedVarintsField(3, vals);
                }

                var rolesSid = new List<ulong>();
                var memids = new List<long>();
                var types = new List<ulong>();
                long prevMem = 0;
                foreach ((ulong mid, OsmMemberType mtype, string role) in members)
                {
                    rolesSid.Add(Intern(role));
                    memids.Add((long)mid - prevMem);
                    prevMem = (long)mid;
                    types.Add((ulong)mtype);
                }

                rel.WritePackedVarintsField(8, rolesSid);
                rel.WritePackedSInt64Field(9, memids);
                rel.WritePackedVarintsField(10, types);
                group.WriteBytesField(4, rel.ToArray());
            }

            // String table (field 1).
            var stringTable = new ProtoWriter();
            foreach (string s in _strings)
            {
                stringTable.WriteBytesField(1, Encoding.UTF8.GetBytes(s));
            }

            var block = new ProtoWriter();
            block.WriteBytesField(1, stringTable.ToArray());
            block.WriteBytesField(2, group.ToArray());
            block.WriteVarintField(17, 100); // granularity
            return block.ToArray();
        }

        private static void WriteFileBlock(Stream output, string type, byte[] blockData)
        {
            byte[] zlib = Deflate(blockData);

            var blob = new ProtoWriter();
            blob.WriteVarintField(2, (ulong)blockData.Length);
            blob.WriteBytesField(3, zlib);
            byte[] blobBytes = blob.ToArray();

            var blobHeader = new ProtoWriter();
            blobHeader.WriteStringField(1, type);
            blobHeader.WriteVarintField(3, (ulong)blobBytes.Length);
            byte[] blobHeaderBytes = blobHeader.ToArray();

            int len = blobHeaderBytes.Length;
            output.WriteByte((byte)(len >> 24));
            output.WriteByte((byte)(len >> 16));
            output.WriteByte((byte)(len >> 8));
            output.WriteByte((byte)len);

            output.Write(blobHeaderBytes, 0, blobHeaderBytes.Length);
            output.Write(blobBytes, 0, blobBytes.Length);
        }

        private static byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(data, 0, data.Length);
            }

            return ms.ToArray();
        }
    }

    private sealed class ProtoWriter
    {
        private readonly MemoryStream _ms = new();

        public byte[] ToArray() => _ms.ToArray();

        public void WriteVarintField(int field, ulong value)
        {
            WriteTag(field, 0);
            WriteVarint(value);
        }

        public void WriteSInt64Field(int field, long value)
        {
            WriteTag(field, 0);
            WriteVarint(ZigZag(value));
        }

        public void WriteStringField(int field, string value) =>
            WriteBytesField(field, Encoding.UTF8.GetBytes(value));

        public void WriteBytesField(int field, byte[] value)
        {
            WriteTag(field, 2);
            WriteVarint((ulong)value.Length);
            _ms.Write(value, 0, value.Length);
        }

        public void WritePackedVarintsField(int field, IEnumerable<ulong> values)
        {
            using var tmp = new MemoryStream();
            foreach (ulong v in values)
            {
                WriteVarintTo(tmp, v);
            }

            WriteBytesField(field, tmp.ToArray());
        }

        public void WritePackedSInt64Field(int field, IEnumerable<long> values)
        {
            using var tmp = new MemoryStream();
            foreach (long v in values)
            {
                WriteVarintTo(tmp, ZigZag(v));
            }

            WriteBytesField(field, tmp.ToArray());
        }

        private void WriteTag(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

        private void WriteVarint(ulong value) => WriteVarintTo(_ms, value);

        private static void WriteVarintTo(Stream s, ulong value)
        {
            while (value >= 0x80)
            {
                s.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            s.WriteByte((byte)value);
        }

        private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    }
}
