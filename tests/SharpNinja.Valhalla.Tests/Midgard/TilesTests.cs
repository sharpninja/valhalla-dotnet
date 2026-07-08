// Faithful C# port of Valhalla's gtest suite test/tiles.cc.
// Each [Fact] mirrors a TEST(Tiles, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_LT/LE -> Assert.True with the corresponding comparison;
// EXPECT_DOUBLE_EQ -> Assert.Equal on the exact double.
//
// Ported cases (core row/col/tileid/bbox + neighbors + tile-list + bbox-intersect):
//   TestMaxId, TestBase, TestRowCol, TestTileBounds, TestNeighbors, TileList,
//   float_roundoff_issue, test_intersect_bbox_world, test_intersect_bbox_single,
//   test_intersect_bbox_rounding.
//
// SKIPPED cases (depend on midgard modules not part of this port):
//   - test_intersect_linestring : Tiles.Intersect(linestring) needs Polyline2,
//     DistanceApproximator-based resampling, and the bresenham rasterizer (Polyline2 not ported).
//   - test_random_linestring    : same dependency on Tiles.Intersect(linestring).
//   - test_closest_first        : Tiles.ClosestFirst needs the closest_first_generator_t
//     priority-queue functor (not ported).

using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Midgard;

using Aabb2ll = SharpNinja.Valhalla.Midgard.Aabb2T<double>;
using PointLL = SharpNinja.Valhalla.Midgard.PointLL;
using TilesLL = SharpNinja.Valhalla.Midgard.Tiles<
    SharpNinja.Valhalla.Midgard.PointLL, double>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class TilesTests
{
    // Helper to build the world-spanning bounds [-180,-90,180,90]. The C++ test uses
    // AABB2<PointLL>(PointLL(...), PointLL(...)); the C# Aabb2T<double> stores the same scalar
    // extents, so we build it from the scalar (minx, miny, maxx, maxy) corners.
    private static Aabb2ll WorldBounds() => new(-180.0, -90.0, 180.0, 90.0);

    [Fact]
    public void TestMaxId()
    {
        Assert.Equal(1036799u, TilesLL.MaxTileId(WorldBounds(), .25f));
        Assert.Equal(64799u, TilesLL.MaxTileId(WorldBounds(), 1f));
        Assert.Equal(4049u, TilesLL.MaxTileId(WorldBounds(), 4f));
        Assert.Equal(595685u, TilesLL.MaxTileId(WorldBounds(), .33f));
    }

    [Fact]
    public void TestBase()
    {
        var tiles = new TilesLL(WorldBounds(), 1f);

        // left bottom
        PointLL ll = tiles.Base(0);
        Assert.Equal(-180, ll.Lng);
        Assert.Equal(-90, ll.Lat);

        ll = tiles.Base(1);
        Assert.Equal(-179, ll.Lng);
        Assert.Equal(-90, ll.Lat);

        // right bottom
        ll = tiles.Base(359);
        Assert.Equal(179, ll.Lng);
        Assert.Equal(-90, ll.Lat);

        ll = tiles.Base(360);
        Assert.Equal(-180, ll.Lng);
        Assert.Equal(-89, ll.Lat);

        // right top
        ll = tiles.Base((360 * 180) - 1);
        Assert.Equal(179, ll.Lng);
        Assert.Equal(89, ll.Lat);
    }

    [Fact]
    public void TestRowCol()
    {
        var tiles = new TilesLL(WorldBounds(), 1f);

        int tileid1 = tiles.TileId(-76.5f, 40.5f);
        (int Row, int Col) rc = tiles.GetRowColumn(tileid1);
        int tileid2 = tiles.TileId(rc.Col, rc.Row);
        Assert.Equal(tileid1, tileid2); // TileId does not match using row,col

        // https://github.com/valhalla/valhalla/issues/5360
        double[] xs = { 179.9999986, 180.0, 180.001, -179.9999986, -180.0, -180.001 };
        foreach (double x in xs)
        {
            Assert.True(tiles.Col(x) < tiles.Ncolumns());
        }
    }

    [Fact]
    public void TestTileBounds()
    {
        var tiles = new TilesLL(WorldBounds(), 1f);
        int nTiles = tiles.Ncolumns() * tiles.Nrows();
        Assert.Equal(360 * 180, nTiles); // Number of tiles not correct

        int[] ids =
        {
            0,
            1,
            tiles.Ncolumns() - 1,
            tiles.Ncolumns(),
            (nTiles / 2) - 1,
            nTiles / 2,
            (nTiles / 2) + 1,
            nTiles - tiles.Ncolumns() - 1,
            nTiles - tiles.Ncolumns(),
            nTiles - 2,
            nTiles - 1,
        };

        foreach (int id in ids)
        {
            Aabb2ll bounds1 = tiles.TileBounds(id);
            (int Row, int Col) rc = tiles.GetRowColumn(id);
            Aabb2ll bounds2 = tiles.TileBounds(rc.Col, rc.Row);
            Assert.Equal(bounds1.Minx, bounds2.Minx); // Bounds of tile not equal
            Assert.Equal(bounds1.Maxx, bounds2.Maxx); // Bounds of tile not equal
            Assert.Equal(bounds1.Miny, bounds2.Miny); // Bounds of tile not equal
            Assert.Equal(bounds1.Maxy, bounds2.Maxy); // Bounds of tile not equal
        }
    }

    [Fact]
    public void TestNeighbors()
    {
        var tiles = new TilesLL(WorldBounds(), 1f);

        // Get a tile
        int tileid1 = tiles.TileId(-76.5f, 40.5f);
        (int Row, int Col) rc1 = tiles.GetRowColumn(tileid1);

        // Test left neighbor
        int tileid2 = tiles.LeftNeighbor(tileid1);
        (int Row, int Col) rc2 = tiles.GetRowColumn(tileid2);
        Assert.True(tiles.AreNeighbors((uint)tileid1, (uint)tileid2)); // Left neighbor
        Assert.Equal(rc1.Row, rc2.Row); // Left neighbor row,col not correct
        Assert.Equal(rc1.Col - 1, rc2.Col); // Left neighbor row,col not correct

        // Test right neighbor
        tileid2 = tiles.RightNeighbor(tileid1);
        rc2 = tiles.GetRowColumn(tileid2);
        Assert.True(tiles.AreNeighbors((uint)tileid1, (uint)tileid2)); // Right neighbor
        Assert.Equal(rc1.Row, rc2.Row); // Right neighbor row,col not correct
        Assert.Equal(rc1.Col + 1, rc2.Col); // Right neighbor row,col not correct

        // Top neighbor
        tileid2 = tiles.TopNeighbor(tileid1);
        rc2 = tiles.GetRowColumn(tileid2);
        Assert.True(tiles.AreNeighbors((uint)tileid1, (uint)tileid2)); // Top neighbor
        Assert.Equal(rc1.Row + 1, rc2.Row); // Top neighbor row,col not correct
        Assert.Equal(rc1.Col, rc2.Col); // Top neighbor row,col not correct

        // Bottom neighbor
        tileid2 = tiles.BottomNeighbor(tileid1);
        rc2 = tiles.GetRowColumn(tileid2);
        Assert.True(tiles.AreNeighbors((uint)tileid1, (uint)tileid2)); // Bottom neighbor
        Assert.Equal(rc1.Row - 1, rc2.Row); // Bottom neighbor row,col not correct
        Assert.Equal(rc1.Col, rc2.Col); // Bottom neighbor row,col not correct
    }

    [Fact]
    public void TileList()
    {
        var tiles = new TilesLL(WorldBounds(), 1f);

        // Float literals are promoted to double exactly as the C++ test (which constructs the
        // PointLL bounds from float arguments) does.
        var bbox = new Aabb2ll(-99.5f, 30.5f, -90.5f, 39.5f);
        List<int> tilelist = tiles.TileList(bbox);
        Assert.Equal(100, tilelist.Count); // Wrong number of tiles in TileList

        // Test crossing -180
        var bbox2 = new Aabb2ll(-183.5f, 30.5f, -176.5f, 34.5f);
        tilelist = tiles.TileList(bbox2);
        Assert.Equal(40, tilelist.Count); // Wrong number crossing -180

        // Test crossing 180
        var bbox3 = new Aabb2ll(176.5f, 30.5f, 183.5f, 34.5f);
        tilelist = tiles.TileList(bbox3);
        Assert.Equal(40, tilelist.Count); // Wrong number crossing 180

        var bbox4 = new Aabb2ll(-76.489998f, 40.509998f, -76.480003f, 40.520000f);
        tilelist = tiles.TileList(bbox4);
        Assert.Single(tilelist); // Wrong number of tiles found in TileList
    }

    [Fact]
    public void FloatRoundoffIssue()
    {
        Aabb2ll worldBox = WorldBounds();
        var t = new TilesLL(worldBox, 0.25f, 5);

        var ll = new PointLL(179.999978, -16.805363);
        int tileId = t.TileId(ll);
        Assert.Equal(421919, tileId);

        PointLL baseLl = t.Base(tileId);
        Assert.Equal(-17.0, baseLl.Lat);
        Assert.Equal(179.75, baseLl.Lng);
    }

    [Fact]
    public void TestIntersectBboxWorld()
    {
        Aabb2ll worldBox = WorldBounds();
        var t = new TilesLL(worldBox, 90f, 2);

        Dictionary<int, HashSet<ushort>> intersection = t.Intersect(worldBox);
        Assert.Equal(t.TileCount(), (uint)intersection.Count); // world-spanning intersection

        int nbins = t.Nsubdivisions() * t.Nsubdivisions();
        foreach (KeyValuePair<int, HashSet<ushort>> i in intersection)
        {
            Assert.Equal(nbins, i.Value.Count); // For tile i.Key
        }
    }

    [Fact]
    public void TestIntersectBboxSingle()
    {
        Aabb2ll worldBox = WorldBounds();
        var t = new TilesLL(worldBox, 90f, 2);

        var singleBox = new Aabb2ll(1.0, 1.0, 2.0, 2.0);
        Dictionary<int, HashSet<ushort>> intersection = t.Intersect(singleBox);
        Assert.Single(intersection); // one tile from intersection

        int tileId = intersection.Keys.First();
        HashSet<ushort> bins = intersection.Values.First();

        // expect tile id to be 6 because the point just up and right from the origin should be in
        // the 3rd column, 2nd row, so thats (ncols(=4) * row(=1)) + col(=2).
        Assert.Equal(6, tileId);

        // there should be a single result bin, which should be in the lower left and therefore bin 0.
        Assert.Single(bins);
        Assert.Equal((ushort)0, bins.First());
    }

    [Fact]
    public void TestIntersectBboxRounding()
    {
        Aabb2ll worldBox = WorldBounds();
        var t = new TilesLL(worldBox, 0.25f, 5);

        var singleBox = new Aabb2ll(0.5, 0.5, 0.501, 0.501);
        Dictionary<int, HashSet<ushort>> intersection = t.Intersect(singleBox);
        Assert.Single(intersection); // one tile from intersection

        HashSet<ushort> bins = intersection.Values.First();

        // expect only the lower left bin, 0
        Assert.Single(bins);
        Assert.Equal((ushort)0, bins.First());
    }
}
