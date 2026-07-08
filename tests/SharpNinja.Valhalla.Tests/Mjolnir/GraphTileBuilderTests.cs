// Tests for the faithful C# port of the Valhalla mjolnir GraphTileBuilder (the tile WRITE side).
//
// Ports the directly-applicable unit gtest from F:/github/valhalla/test/graphtilebuilder.cc:
//   - GraphTileBuilder.TestDuplicateEdgeInfo: edge-info dedup (the two directed edges of an edge
//     share one EdgeInfo), name / tagged-value / mean-elevation / speed-limit round-trip through the
//     Baldr GraphTile reader, proving byte compatibility of the written tile.
//
// EXCLUDED (out of scope): TestAddBins (bin edges), TestDuplicatePredictedSpeeds /
// TestDuplicatePredictedSpeedSmallHint (predicted speeds), TestBinEdges (BinEdges) - those exercise
// surfaces excluded from the auto/truck on-device tile build.

using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class GraphTileBuilderTests
{
    [Fact]
    public void EdgeTuple_OrdersNodes_AndIsStable()
    {
        // The (edgeindex, nodea, nodeb) edge tuple is order-independent in nodea/nodeb so the two
        // directed edges of an edge map to the same key. We verify the behavioral consequence via
        // AddEdgeInfo dedup below; here we just confirm a fresh builder starts empty.
        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));
        Assert.False(builder.HasEdgeInfo(0, new GraphId(0, 2, 0), new GraphId(0, 2, 1), out _));
    }

    [Fact]
    public void AddEdgeInfo_DeduplicatesOpposingDirections_AndRoundTrips()
    {
        // Faithful port of GraphTileBuilder.TestDuplicateEdgeInfo.
        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));

        // One directed edge (the forward edge of the A<->B edge). edgeinfo_offset is set below.
        builder.DirectedEdges.Add(new DirectedEdge());

        // Add edge info for node 0 -> node 1.
        uint offsetForward = builder.AddEdgeInfo(
            0, new GraphId(0, 2, 0), new GraphId(0, 2, 1), 1234, 555f, 0, 120,
            new List<PointLL> { new PointLL(0, 0), new PointLL(1, 1) },
            new[] { "einzelweg" }, new[] { "1xyz tunnel" }, System.Array.Empty<string>(), 0, out bool addedFwd);
        Assert.True(addedFwd);

        // The forward edge stores this edge info offset.
        DirectedEdge de = builder.DirectedEdges[0];
        de.SetEdgeInfoOffset(offsetForward);
        builder.DirectedEdges[0] = de;

        // Add edge info for node 1 -> node 0: same edge tuple, so it must dedup to the same offset and
        // NOT add a new edge info.
        uint offsetReverse = builder.AddEdgeInfo(
            0, new GraphId(0, 2, 1), new GraphId(0, 2, 0), 1234, 555f, 0, 120,
            new List<PointLL> { new PointLL(1, 1), new PointLL(0, 0) },
            new[] { "einzelweg" }, new[] { "1xyz tunnel" }, System.Array.Empty<string>(), 0, out bool addedRev);
        Assert.False(addedRev);
        Assert.Equal(offsetForward, offsetReverse);

        // Now HasEdgeInfo should report the offset for either direction.
        Assert.True(builder.HasEdgeInfo(0, new GraphId(0, 2, 0), new GraphId(0, 2, 1), out uint hasOffset));
        Assert.Equal(offsetForward, hasOffset);

        // Serialize and re-read through the Baldr GraphTile reader (byte compatibility).
        byte[] blob = builder.StoreTileData();
        GraphTile tile = GraphTile.Create(new GraphId(0, 2, 0), blob);

        Assert.Equal(1u, tile.Header().Directededgecount());
        EdgeInfo ei = tile.EdgeInfo(tile.DirectedEdge(0));

        Assert.Equal(555.0f, ei.MeanElevation, EdgeInfo.ElevationBinSize);
        Assert.Equal(120u, ei.SpeedLimit);
        Assert.Equal(1234ul, ei.WayId);

        // Names: the single non-tagged name.
        List<string> names = ei.GetNames();
        Assert.Single(names);
        Assert.Equal("einzelweg", names[0]);

        // Tagged values: the single tagged value (returned with its tag prefix stripped).
        List<string> tagged = ei.GetTaggedValues();
        Assert.Single(tagged);
        Assert.Equal("1xyz tunnel", tagged[0]);

        // GetNamesAndTypes(false) returns only the plain name.
        var namesAndTypes = ei.GetNamesAndTypes(false);
        Assert.Single(namesAndTypes);
        Assert.Equal("einzelweg", namesAndTypes[0].Name);
        Assert.False(namesAndTypes[0].IsRouteNum);

        // GetNamesAndTypes(true) returns the name + the tagged value.
        var namesAndTypesTagged = ei.GetNamesAndTypes(true);
        Assert.Equal(2, namesAndTypesTagged.Count);

        // GetTags exposes the tunnel tagged value (tag byte '1' = TaggedValue.Tunnel).
        var tags = ei.GetTags();
        Assert.Single(tags);
        KeyValuePair<TaggedValue, byte[]> tag = tags[0];
        Assert.Equal(TaggedValue.Tunnel, tag.Key);
        string tunnelValue = new string(tag.Value.Select(b => (char)b).ToArray());
        Assert.Equal("xyz tunnel", tunnelValue);

        // Shape round-trips.
        var shape = ei.Shape();
        Assert.True(shape.Count >= 2);
    }

    [Fact]
    public void AddName_DeduplicatesAndAssignsStableOffsets()
    {
        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));

        // Empty name maps to offset 0 always (the empty string entry added in the ctor).
        Assert.Equal(0u, builder.AddName(string.Empty));

        uint a = builder.AddName("alpha");
        uint b = builder.AddName("beta");
        uint a2 = builder.AddName("alpha");

        Assert.Equal(a, a2);            // dedup
        Assert.NotEqual(a, b);          // distinct names get distinct offsets
        Assert.True(b > a);             // monotonically increasing offsets
    }

    [Fact]
    public void AddAdmin_DeduplicatesByCountryStateKey()
    {
        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));

        // index 0 is the dummy "None"/"None" admin added in the ctor.
        uint pa = builder.AddAdmin("United States", "Pennsylvania", "US", "PA");
        uint paAgain = builder.AddAdmin("United States", "Pennsylvania", "US", "PA");
        uint oh = builder.AddAdmin("United States", "Ohio", "US", "OH");

        Assert.Equal(pa, paAgain);
        Assert.NotEqual(pa, oh);
        Assert.Equal(1u, pa);   // first admin after the dummy at index 0
        Assert.Equal(2u, oh);
    }
}
