// Faithful C# port of Valhalla's gtest suite test/graphtile.cc.
//
// Covers: FileSuffix, IdFromString (GetTileId), Bin (GetBin), GraphTileIntegrity (size guards),
// and the ComplexRestrictionView cases.
//
// PORT-NOTES:
//   - TEST(GraphTileVersion, VersionChecksum) is NOT ported: it loads a real on-disk tile fixture
//     (VALHALLA_BUILD_DIR "test/data/utrecht_tiles") and checks the build version/checksum. That
//     fixture is not available in this repo and version/checksum reading is already covered by
//     GraphTileHeaderTests. Documented here instead of asserting against a missing file.
//   - The ComplexRestrictionView tests in C++ build records with mjolnir::ComplexRestrictionBuilder
//     (the mjolnir tile builder is an excluded module). The builder's operator<< simply writes the
//     fixed 24-byte ComplexRestriction struct followed by via_count GraphIds (8 bytes each). The
//     RestrictionBuilder helper below reproduces that on-disk byte layout exactly by packing the
//     three 64-bit words directly, so the view is exercised over byte-identical input.
//   - The C++ FileSuffix / GetTileId path separators are the platform separator (is_file_path=true).
//     The expected strings here are normalized to Path.DirectorySeparatorChar so the test is
//     faithful on both Windows ('\\') and POSIX ('/'). The is_file_path=false cases keep '/'.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class GraphTileTests
{
    // Normalize a '/'-separated expected suffix to the platform separator (matches the C++
    // is_file_path=true behavior which uses std::filesystem::path::preferred_separator).
    private static string Sep(string s) => s.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void FileSuffix()
    {
        Assert.Equal(Sep("2/000/000/002.gph"), GraphTile.FileSuffix(new GraphId(2, 2, 0)));
        Assert.Equal(Sep("2/000/000/004.gph"), GraphTile.FileSuffix(new GraphId(4, 2, 0)));
        Assert.Equal(Sep("1/064/799.gph"), GraphTile.FileSuffix(new GraphId(64799, 1, 0)));
        Assert.Equal(Sep("0/000/049.gph"), GraphTile.FileSuffix(new GraphId(49, 0, 0)));
        Assert.Equal(Sep("3/001/000/000.gph"), GraphTile.FileSuffix(new GraphId(1000000, 3, 1)));

        Assert.Throws<InvalidOperationException>(() => GraphTile.FileSuffix(new GraphId(64800, 1, 0)));
        Assert.Throws<InvalidOperationException>(() => GraphTile.FileSuffix(new GraphId(1337, 6, 0)));
        Assert.Throws<InvalidOperationException>(() => GraphTile.FileSuffix(new GraphId(1036800, 2, 0)));
        Assert.Throws<InvalidOperationException>(() => GraphTile.FileSuffix(new GraphId(4050, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => GraphTile.FileSuffix(new GraphId(1036800, 3, 0)));

        // TileLevel{7, Secondary, "half_degree_is_a_multiple_of_3", Tiles{{{-180,-90},{180,90}}, .5, 1}}
        var level = new TileLevel(
            7,
            RoadClass.Secondary,
            "half_degree_is_a_multiple_of_3",
            new Tiles<PointLL, double>(
                new Aabb2T<double>(new PointXY<double>(-180, -90), new PointXY<double>(180, 90)),
                0.5f,
                1));

        // is_file_path = false => '/' separators regardless of platform.
        Assert.Equal("7/001/234.qux", GraphTile.FileSuffix(new GraphId(1234, 7, 0), ".qux", false, level));
        Assert.Equal("7/123/456.qux", GraphTile.FileSuffix(new GraphId(123456, 7, 0), ".qux", false, level));
    }

    [Fact]
    public void IdFromString()
    {
        Assert.Equal(new GraphId(2, 1, 0), GraphTile.GetTileId(Sep("foo/bar/baz/qux/corge/1/000/002.gph")));
        Assert.Equal(
            new GraphId(2, 1, 0),
            GraphTile.GetTileId(Sep("foo2/8675309/bar/1baz2/qux42corge/1/000/002.gph")));
        Assert.Equal(
            new GraphId(1000002, 2, 0),
            GraphTile.GetTileId(Sep("foo2/8675309/bar/1baz2/qux42corge/2/001/000/002.gph")));
        Assert.Equal(
            new GraphId(1000002, 3, 0),
            GraphTile.GetTileId(Sep("foo2/8675309/bar/1baz2/qux42corge/3/001/000/002.gph")));
        Assert.Equal(
            new GraphId(1000002, 3, 0),
            GraphTile.GetTileId(Sep("foo2/8675309/bar/1baz2/qux42corge/3/001/000/002")));
        Assert.Equal(new GraphId(791317, 2, 0), GraphTile.GetTileId(Sep("2/000/791/317.gph.gz")));

        Assert.Throws<InvalidOperationException>(
            () => GraphTile.GetTileId(Sep("foo2/8675309/bar/1baz2/qux42corge/1/000/002/.gph")));
        Assert.Throws<InvalidOperationException>(
            () => GraphTile.GetTileId(Sep("foo2/8675309/bar/1baz2/qux42corge/0/004/050.gph")));
        Assert.Throws<InvalidOperationException>(() => GraphTile.GetTileId(Sep("foo/bar/0/004/0-1.gph")));
        Assert.Throws<InvalidOperationException>(() => GraphTile.GetTileId(Sep("foo/bar/0/004//001.gph")));
        Assert.Throws<InvalidOperationException>(() => GraphTile.GetTileId(Sep("foo/bar/1/000/004/001.gph")));
        Assert.Throws<InvalidOperationException>(() => GraphTile.GetTileId(Sep("00/002.gph")));
    }

    [Fact]
    public void Bin()
    {
        // Same setup as the C++ TEST(Graphtile, Bin): build per-bin counts, accumulate to offsets,
        // and fill the edge_bins list with sequential GraphIds.
        uint[] offsets =
        {
            1, 2, 3, 0, 1, 2, 3, 1, 1, 2, 3, 2, 1,
            2, 3, 3, 1, 2, 3, 4, 1, 2, 3, 5, 1,
        };

        var offs = new List<uint> { 0 };
        var bins = new List<GraphId>();
        uint offset = 0;
        uint j = 0;
        for (int i = 0; i < GraphTileHeader.BinCount; ++i)
        {
            offset += offsets[i];
            offs.Add(offset);
            offsets[i] = offset;
            for (uint k = 0; k < offsets[i]; ++k)
            {
                bins.Add(new GraphId(j++));
            }
        }

        GraphTile t = BuildBinTile(offsets, bins);

        for (int i = 0; i < GraphTileHeader.BinCount; ++i)
        {
            // Expected span: bins[offs[i] .. offs[i+1]).
            int expectedCount = (int)(offs[i + 1] - offs[i]);

            IReadOnlyList<GraphId> idxItr = t.GetBin(i);
            IReadOnlyList<GraphId> rcItr = t.GetBin(i % GraphTileHeader.BinsDim, i / GraphTileHeader.BinsDim);

            Assert.Equal(expectedCount, idxItr.Count);
            Assert.Equal(expectedCount, rcItr.Count);

            for (int n = 0; n < expectedCount; ++n)
            {
                GraphId expected = bins[(int)offs[i] + n];
                Assert.Equal(expected, idxItr[n]);
                Assert.Equal(expected, rcItr[n]);
            }
        }
    }

    [Fact]
    public void SizeZero()
    {
        // A zero-length tile must be rejected (smaller than the header).
        Assert.Throws<InvalidOperationException>(
            () => GraphTile.Create(GraphId.Invalid, new byte[0]));
    }

    [Fact]
    public void SizeLessThanHeader()
    {
        int tileSize = GraphTileHeader.HeaderSize - 1;
        Assert.Throws<InvalidOperationException>(
            () => GraphTile.Create(GraphId.Invalid, new byte[tileSize]));
    }

    [Fact]
    public void SizeLessThanPayload()
    {
        // The header's end_offset must equal the actual data size, else the tile is corrupt.
        const int tileSize = 10000;
        var header = new GraphTileHeader();
        header.SetEndOffset(tileSize - 1); // not equal to the data size

        var tileData = new byte[tileSize];
        header.ToBytes().CopyTo(tileData, 0);

        Assert.Throws<InvalidOperationException>(
            () => GraphTile.Create(GraphId.Invalid, tileData));
    }

    // PORT-NOTE: TEST(GraphTileVersion, VersionChecksum) requires the utrecht_tiles on-disk fixture
    // (VALHALLA_BUILD_DIR "test/data/utrecht_tiles") which is not present in this repo. Version and
    // checksum reading are covered by GraphTileHeaderTests; not re-asserted here.

    // ------------------------------------------------------------------
    // ComplexRestrictionView tests (port of test/graphtile.cc lines 149-324)
    // ------------------------------------------------------------------

    [Fact]
    public void ComplexRestrictionView_EmptyView()
    {
        var view = new ComplexRestrictionView(
            Array.Empty<byte>(), 0, 0, new GraphId(100, 0, 0), 0xFF, true);

        Assert.True(view.Empty());

        int count = 0;
        foreach (ComplexRestriction _ in view)
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public void ComplexRestrictionView_NoMatchingRestrictions()
    {
        var builder = new RestrictionBuilder();
        builder.AddRestriction(new GraphId(1, 0, 0), new GraphId(2, 0, 0), 0x1);
        builder.AddRestriction(new GraphId(3, 0, 0), new GraphId(4, 0, 0), 0x2);
        builder.AddRestriction(new GraphId(5, 0, 0), new GraphId(6, 0, 0), 0x4);

        var view = new ComplexRestrictionView(
            builder.Data, 0, builder.Data.Length, new GraphId(100, 0, 0), 0xFF, true);

        Assert.True(view.Empty());
    }

    [Fact]
    public void ComplexRestrictionView_ForwardRestrictions()
    {
        var builder = new RestrictionBuilder();
        builder.AddRestriction(new GraphId(1, 0, 0), new GraphId(100, 0, 0), 0x1); // matches
        builder.AddRestriction(new GraphId(2, 0, 0), new GraphId(200, 0, 0), 0x2); // wrong id
        builder.AddRestriction(new GraphId(3, 0, 0), new GraphId(100, 0, 0), 0x4); // matches
        builder.AddRestriction(new GraphId(4, 0, 0), new GraphId(100, 0, 0), 0x8); // wrong modes

        // to_graphid = 100, modes = 0x5 (matches modes 0x1 and 0x4).
        var view = new ComplexRestrictionView(
            builder.Data, 0, builder.Data.Length, new GraphId(100, 0, 0), 0x5, true);

        Assert.False(view.Empty());

        var results = new List<ComplexRestriction>();
        foreach (ComplexRestriction cr in view)
        {
            results.Add(cr);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal(new GraphId(100, 0, 0), results[0].ToGraphId());
        Assert.Equal(new GraphId(1, 0, 0), results[0].FromGraphId());
        Assert.Equal(new GraphId(100, 0, 0), results[1].ToGraphId());
        Assert.Equal(new GraphId(3, 0, 0), results[1].FromGraphId());
    }

    [Fact]
    public void ComplexRestrictionView_ReverseRestrictions()
    {
        var builder = new RestrictionBuilder();
        builder.AddRestriction(new GraphId(100, 0, 0), new GraphId(1, 0, 0), 0x1); // matches
        builder.AddRestriction(new GraphId(200, 0, 0), new GraphId(2, 0, 0), 0x2); // wrong id
        builder.AddRestriction(new GraphId(100, 0, 0), new GraphId(3, 0, 0), 0x4); // matches

        // from_graphid = 100 (reverse), modes = 0xFF.
        var view = new ComplexRestrictionView(
            builder.Data, 0, builder.Data.Length, new GraphId(100, 0, 0), 0xFF, false);

        Assert.False(view.Empty());

        var results = new List<ComplexRestriction>();
        foreach (ComplexRestriction cr in view)
        {
            results.Add(cr);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal(new GraphId(100, 0, 0), results[0].FromGraphId());
        Assert.Equal(new GraphId(100, 0, 0), results[1].FromGraphId());
    }

    [Fact]
    public void ComplexRestrictionView_ViewInterfaceMethods()
    {
        var builder = new RestrictionBuilder();
        builder.AddRestriction(new GraphId(1, 0, 0), new GraphId(100, 0, 0), 0x1);
        builder.AddRestriction(new GraphId(2, 0, 0), new GraphId(100, 0, 0), 0x2);

        var view = new ComplexRestrictionView(
            builder.Data, 0, builder.Data.Length, new GraphId(100, 0, 0), 0xFF, true);

        Assert.False(view.Empty());
        ComplexRestriction first = view.Front();
        Assert.Equal(new GraphId(1, 0, 0), first.FromGraphId());
    }

    [Fact]
    public void ComplexRestrictionView_IteratorIncrement()
    {
        var builder = new RestrictionBuilder();
        builder.AddRestriction(new GraphId(1, 0, 0), new GraphId(100, 0, 0), 0x1);
        builder.AddRestriction(new GraphId(2, 0, 0), new GraphId(100, 0, 0), 0x2);
        builder.AddRestriction(new GraphId(3, 0, 0), new GraphId(100, 0, 0), 0x4);

        var view = new ComplexRestrictionView(
            builder.Data, 0, builder.Data.Length, new GraphId(100, 0, 0), 0xFF, true);

        ComplexRestrictionView.Enumerator it = view.GetEnumerator();
        Assert.True(it.MoveNext());
        Assert.Equal(new GraphId(1, 0, 0), it.Current.FromGraphId());

        Assert.True(it.MoveNext());
        Assert.Equal(new GraphId(2, 0, 0), it.Current.FromGraphId());

        Assert.True(it.MoveNext());
        Assert.Equal(new GraphId(3, 0, 0), it.Current.FromGraphId());

        Assert.False(it.MoveNext());
    }

    [Theory]
    [InlineData(0x1ul, 2)] // modes 0x1 matches 0x1 and 0x1F
    [InlineData(0x2ul, 2)] // modes 0x2 matches 0x2 and 0x1F
    [InlineData(0x3ul, 3)] // modes 0x3 matches 0x1, 0x2, and 0x1F
    public void ComplexRestrictionView_ModeFiltering(ulong queryModes, int expected)
    {
        var builder = new RestrictionBuilder();
        builder.AddRestriction(new GraphId(1, 0, 0), new GraphId(100, 0, 0), 0x1);
        builder.AddRestriction(new GraphId(2, 0, 0), new GraphId(100, 0, 0), 0x2);
        builder.AddRestriction(new GraphId(3, 0, 0), new GraphId(100, 0, 0), 0x4);
        builder.AddRestriction(new GraphId(4, 0, 0), new GraphId(100, 0, 0), 0x8);
        builder.AddRestriction(new GraphId(5, 0, 0), new GraphId(100, 0, 0), 0x1F);

        var view = new ComplexRestrictionView(
            builder.Data, 0, builder.Data.Length, new GraphId(100, 0, 0), queryModes, true);

        int count = 0;
        foreach (ComplexRestriction _ in view)
        {
            count++;
        }

        Assert.Equal(expected, count);
    }

    // ------------------------------------------------------------------
    // Test helpers
    // ------------------------------------------------------------------

    // Builds the smallest valid tile (header + edge bins only) so GetBin can be exercised, mirroring
    // the C++ testable_graphtile which sets header_->set_edge_bin_offsets(offsets) and edge_bins_.
    // With all record counts zero, the edge_bins section starts immediately after the 272-byte
    // header, exactly where GraphTile.Initialize places _edgeBinsOffset.
    private static GraphTile BuildBinTile(uint[] binOffsets, List<GraphId> bins)
    {
        var header = new GraphTileHeader();
        header.SetEdgeBinOffsets(binOffsets);

        int edgeBinsBytes = bins.Count * 8;
        int tileSize = GraphTileHeader.HeaderSize + edgeBinsBytes;
        header.SetEndOffset((uint)tileSize);

        var tile = new byte[tileSize];
        header.ToBytes().CopyTo(tile, 0);

        for (int i = 0; i < bins.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                tile.AsSpan(GraphTileHeader.HeaderSize + (i * 8), 8), bins[i].Value);
        }

        return GraphTile.Create(GraphId.Invalid, tile);
    }

    // Reproduces mjolnir::ComplexRestrictionBuilder's operator<< on-disk byte layout: the fixed
    // 24-byte ComplexRestriction struct (3 little-endian 64-bit words) followed by via_count
    // GraphIds (8 bytes each). The three words are packed using the exact ComplexRestriction bit
    // layout (see complexrestriction.h):
    //   word0: from_graphid_:46 | has_dt_:1 | begin_day_dow_:5 | begin_month_:4 | begin_week_:3 | begin_hrs_:5
    //   word1: to_graphid_:46   | dt_type_:1 | end_day_dow_:5 | end_month_:4 | end_week_:3 | end_hrs_:5
    //   word2: type_:4 | modes_:12 | via_count_:5 | dow_:7 | begin_mins_:6 | end_mins_:6 | probability_:7 | spare_:17
    private sealed class RestrictionBuilder
    {
        private readonly List<byte> _data = new();

        public byte[] Data => _data.ToArray();

        public void AddRestriction(GraphId fromId, GraphId toId, ushort modes, GraphId[]? vias = null)
        {
            vias ??= Array.Empty<GraphId>();

            ulong word0 = fromId.Value & 0x3FFFFFFFFFFFUL; // from_graphid_:46 (rest of word0 = 0)
            ulong word1 = toId.Value & 0x3FFFFFFFFFFFUL;   // to_graphid_:46  (rest of word1 = 0)
            ulong word2 = ((ulong)modes & 0xFFFUL) << 4    // modes_:12 at bit 4
                          | ((ulong)vias.Length & 0x1FUL) << 16; // via_count_:5 at bit 16

            Span<byte> w = stackalloc byte[24];
            BinaryPrimitives.WriteUInt64LittleEndian(w.Slice(0, 8), word0);
            BinaryPrimitives.WriteUInt64LittleEndian(w.Slice(8, 8), word1);
            BinaryPrimitives.WriteUInt64LittleEndian(w.Slice(16, 8), word2);
            _data.AddRange(w.ToArray());

            foreach (GraphId via in vias)
            {
                Span<byte> v = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(v, via.Value);
                _data.AddRange(v.ToArray());
            }
        }
    }
}
