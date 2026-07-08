// Faithful C# port of Valhalla's gtest test/edgestatus.cc (TEST(EdgeStatus, TestStatus)).
// Source: F:/github/valhalla/test/edgestatus.cc
//
// The C++ test builds a dummy GraphTile via a `test_tile` friend that sets the header's
// directededgecount to 200000, then Sets several edge statuses across multiple levels and asserts
// Get() returns them, and that clear() resets everything to kUnreachedOrReset. The C# port uses the
// internal GraphTile.CreateForTest(graphid, endOffset, directedEdgeCount) factory (the analogue of
// the C++ test_tile friend) to produce a header-only tile with directededgecount == 200000.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Thor;

public class EdgeStatusTests
{
    private static void TryGet(EdgeStatus edgestatus, GraphId edgeid, EdgeSet expected)
    {
        EdgeStatusInfo r = edgestatus.Get(edgeid);
        Assert.Equal(expected, r.Set());
    }

    [Fact]
    public void TestStatus()
    {
        var edgestatus = new EdgeStatus();

        // Dummy tile header with directededgecount == 200000 (the C++ test_tile friend).
        GraphTile tile = GraphTile.CreateForTest(new GraphId(555, 0, 0), GraphTileHeader.HeaderSize, 200000);

        // Add some edges
        edgestatus.Set(new GraphId(555, 1, 100100), EdgeSet.Permanent, 1, tile);
        edgestatus.Set(new GraphId(555, 2, 100100), EdgeSet.Permanent, 2, tile);
        edgestatus.Set(new GraphId(555, 3, 100100), EdgeSet.Permanent, 3, tile);
        edgestatus.Set(new GraphId(555, 1, 55555), EdgeSet.Temporary, 4, tile);
        edgestatus.Set(new GraphId(555, 2, 55555), EdgeSet.Temporary, 5, tile);
        edgestatus.Set(new GraphId(555, 3, 55555), EdgeSet.Temporary, 6, tile);
        edgestatus.Set(new GraphId(555, 1, 1), EdgeSet.Permanent, 7, tile);
        edgestatus.Set(new GraphId(555, 2, 1), EdgeSet.Permanent, 8, tile);
        edgestatus.Set(new GraphId(555, 3, 1), EdgeSet.Permanent, 9, tile);

        // Test various get
        TryGet(edgestatus, new GraphId(555, 1, 100100), EdgeSet.Permanent);
        TryGet(edgestatus, new GraphId(555, 2, 100100), EdgeSet.Permanent);
        TryGet(edgestatus, new GraphId(555, 3, 100100), EdgeSet.Permanent);
        TryGet(edgestatus, new GraphId(555, 1, 55555), EdgeSet.Temporary);
        TryGet(edgestatus, new GraphId(555, 2, 55555), EdgeSet.Temporary);
        TryGet(edgestatus, new GraphId(555, 3, 55555), EdgeSet.Temporary);
        TryGet(edgestatus, new GraphId(555, 1, 1), EdgeSet.Permanent);
        TryGet(edgestatus, new GraphId(555, 2, 1), EdgeSet.Permanent);
        TryGet(edgestatus, new GraphId(555, 3, 1), EdgeSet.Permanent);

        // Clear and make sure all status are kUnreachedOrReset
        edgestatus.Clear();
        TryGet(edgestatus, new GraphId(555, 1, 100100), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 2, 100100), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 3, 100100), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 1, 55555), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 2, 55555), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 3, 55555), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 1, 1), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 2, 1), EdgeSet.UnreachedOrReset);
        TryGet(edgestatus, new GraphId(555, 3, 1), EdgeSet.UnreachedOrReset);
    }

    [Fact]
    public void Index_And_Set_RoundTrip_Through_28_4_Packing()
    {
        // EdgeStatusInfo packs index_:28 | set_:4. Verify a near-max index survives alongside a set.
        var info = new EdgeStatusInfo(EdgeSet.Skipped, 0x0FFFFFFFu);
        Assert.Equal(0x0FFFFFFFu, info.Index());
        Assert.Equal(EdgeSet.Skipped, info.Set());
    }

    [Fact]
    public void Update_Changes_Set_But_Preserves_Index()
    {
        var edgestatus = new EdgeStatus();
        GraphTile tile = GraphTile.CreateForTest(new GraphId(7, 0, 0), GraphTileHeader.HeaderSize, 100);

        var id = new GraphId(7, 0, 42);
        edgestatus.Set(id, EdgeSet.Temporary, 17, tile);
        Assert.Equal(EdgeSet.Temporary, edgestatus.Get(id).Set());
        Assert.Equal(17u, edgestatus.Get(id).Index());

        edgestatus.Update(id, EdgeSet.Permanent);
        Assert.Equal(EdgeSet.Permanent, edgestatus.Get(id).Set());
        Assert.Equal(17u, edgestatus.Get(id).Index());
    }

    [Fact]
    public void GetPtr_Allows_In_Place_Mutation()
    {
        var edgestatus = new EdgeStatus();
        GraphTile tile = GraphTile.CreateForTest(new GraphId(7, 0, 0), GraphTileHeader.HeaderSize, 100);

        var id = new GraphId(7, 0, 5);
        (EdgeStatusInfo[] arr, int idx) = edgestatus.GetPtr(id, tile);
        arr[idx] = new EdgeStatusInfo(EdgeSet.Permanent, 3);

        Assert.Equal(EdgeSet.Permanent, edgestatus.Get(id).Set());
        Assert.Equal(3u, edgestatus.Get(id).Index());
    }
}
