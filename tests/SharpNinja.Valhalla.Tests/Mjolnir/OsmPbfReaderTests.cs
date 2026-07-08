// Tests for the faithful OSM PBF reader (OsmPbfReader + ProtoReader).
// Source schema: OSMPBF fileformat.proto + osmformat.proto.
//
// Since the upstream mjolnir gtests rely on large binary .osm.pbf fixtures (liechtenstein,
// rome, baltimore, ...) that are not reproducible here, these tests build small but fully
// valid PBF blobs in-process with a minimal protobuf encoder (including the zlib blob path
// and delta/zig-zag/string-table encodings), then assert the reader decodes them faithfully:
// header bbox, dense nodes, a regular node, a way with delta-coded refs + resolved tags, and
// a relation with delta-coded member ids/types/roles. This exercises every decode path used
// for real OSM data (blob length framing, BlobHeader, Blob zlib inflate, PrimitiveBlock string
// table + granularity/offset, dense keys_vals walk, way refs, relation members).

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class OsmPbfReaderTests
{
    private sealed class CollectingVisitor : IOsmPbfVisitor
    {
        public (double? minLat, double? minLon, double? maxLat, double? maxLon, IReadOnlyList<string> features)? HeaderInfo;

        public readonly List<(ulong id, double lat, double lon, Dictionary<string, string> tags)> Nodes = new();

        public readonly List<(ulong id, List<ulong> refs, Dictionary<string, string> tags)> Ways = new();

        public readonly List<(ulong id, List<OsmRelationMember> members, Dictionary<string, string> tags)> Relations = new();

        public void Header(double? minLat, double? minLon, double? maxLat, double? maxLon, IReadOnlyList<string> requiredFeatures) =>
            HeaderInfo = (minLat, minLon, maxLat, maxLon, requiredFeatures);

        public void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags) =>
            Nodes.Add((id, lat, lon, new Dictionary<string, string>(tags)));

        public void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags) =>
            Ways.Add((id, new List<ulong>(nodeRefs), new Dictionary<string, string>(tags)));

        public void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags) =>
            Relations.Add((id, new List<OsmRelationMember>(members), new Dictionary<string, string>(tags)));
    }

    [Fact]
    public void ReadsHeaderDenseNodesNodeWayAndRelation()
    {
        byte[] pbf = BuildSamplePbf();
        var visitor = new CollectingVisitor();
        var reader = new OsmPbfReader(visitor);

        using (var ms = new MemoryStream(pbf))
        {
            reader.Parse(ms);
        }

        // Header bbox.
        Assert.NotNull(visitor.HeaderInfo);
        var hi = visitor.HeaderInfo!.Value;
        Assert.Equal(41.0, hi.minLat!.Value, 6);
        Assert.Equal(12.0, hi.minLon!.Value, 6);
        Assert.Equal(42.0, hi.maxLat!.Value, 6);
        Assert.Equal(13.0, hi.maxLon!.Value, 6);
        Assert.Contains("OsmSchema-V0.6", hi.features);

        // Two dense nodes (delta-decoded) + one regular node.
        Assert.Equal(3, visitor.Nodes.Count);

        var n1 = visitor.Nodes[0];
        Assert.Equal(100UL, n1.id);
        Assert.Equal(41.5, n1.lat, 6);
        Assert.Equal(12.5, n1.lon, 6);
        Assert.Equal("crossing", n1.tags["highway"]);

        var n2 = visitor.Nodes[1];
        Assert.Equal(101UL, n2.id); // delta +1
        Assert.Equal(41.6, n2.lat, 6);
        Assert.Equal(12.6, n2.lon, 6);
        Assert.Empty(n2.tags);

        var n3 = visitor.Nodes[2];
        Assert.Equal(200UL, n3.id);
        Assert.Equal(41.7, n3.lat, 6);
        Assert.Equal(12.7, n3.lon, 6);
        Assert.Equal("yes", n3.tags["traffic_signal"]);

        // One way with delta-coded refs.
        Assert.Single(visitor.Ways);
        var w = visitor.Ways[0];
        Assert.Equal(500UL, w.id);
        Assert.Equal(new List<ulong> { 100, 101, 200 }, w.refs);
        Assert.Equal("residential", w.tags["highway"]);
        Assert.Equal("Main Street", w.tags["name"]);

        // One restriction relation with from/via/to members.
        Assert.Single(visitor.Relations);
        var r = visitor.Relations[0];
        Assert.Equal(900UL, r.id);
        Assert.Equal("restriction", r.tags["type"]);
        Assert.Equal("no_left_turn", r.tags["restriction"]);
        Assert.Equal(3, r.members.Count);
        Assert.Equal(500UL, r.members[0].Id);
        Assert.Equal(OsmMemberType.Way, r.members[0].Type);
        Assert.Equal("from", r.members[0].Role);
        Assert.Equal(100UL, r.members[1].Id); // 500 + (-400) delta
        Assert.Equal(OsmMemberType.Node, r.members[1].Type);
        Assert.Equal("via", r.members[1].Role);
        Assert.Equal(501UL, r.members[2].Id); // 100 + 401 delta
        Assert.Equal(OsmMemberType.Way, r.members[2].Type);
        Assert.Equal("to", r.members[2].Role);
    }

    // ---- Minimal PBF encoder (test fixture) -----------------------------------

    private static byte[] BuildSamplePbf()
    {
        using var output = new MemoryStream();

        // OSMHeader fileblock.
        byte[] header = BuildHeaderBlock();
        WriteFileBlock(output, "OSMHeader", header);

        // OSMData fileblock.
        byte[] primitive = BuildPrimitiveBlock();
        WriteFileBlock(output, "OSMData", primitive);

        return output.ToArray();
    }

    private static void WriteFileBlock(Stream output, string type, byte[] blockData)
    {
        // Compress the block with zlib to exercise the inflate path.
        byte[] zlib = Deflate(blockData);

        // Blob: raw_size = uncompressed length (field 2), zlib_data (field 3).
        var blob = new ProtoWriter();
        blob.WriteVarintField(2, (ulong)blockData.Length);
        blob.WriteBytesField(3, zlib);
        byte[] blobBytes = blob.ToArray();

        // BlobHeader: type (field 1), datasize (field 3).
        var blobHeader = new ProtoWriter();
        blobHeader.WriteStringField(1, type);
        blobHeader.WriteVarintField(3, (ulong)blobBytes.Length);
        byte[] blobHeaderBytes = blobHeader.ToArray();

        // 4-byte big-endian BlobHeader length.
        int len = blobHeaderBytes.Length;
        output.WriteByte((byte)(len >> 24));
        output.WriteByte((byte)(len >> 16));
        output.WriteByte((byte)(len >> 8));
        output.WriteByte((byte)len);

        output.Write(blobHeaderBytes, 0, blobHeaderBytes.Length);
        output.Write(blobBytes, 0, blobBytes.Length);
    }

    private static byte[] BuildHeaderBlock()
    {
        const double Nano = 1e9;

        // HeaderBBox: left=12 (field1), right=13 (field2), top=42 (field3), bottom=41 (field4),
        // all sint64 nano-degrees.
        var bbox = new ProtoWriter();
        bbox.WriteSInt64Field(1, (long)(12.0 * Nano));
        bbox.WriteSInt64Field(2, (long)(13.0 * Nano));
        bbox.WriteSInt64Field(3, (long)(42.0 * Nano));
        bbox.WriteSInt64Field(4, (long)(41.0 * Nano));

        var header = new ProtoWriter();
        header.WriteBytesField(1, bbox.ToArray());          // bbox
        header.WriteStringField(4, "OsmSchema-V0.6");        // required_features
        return header.ToArray();
    }

    private static byte[] BuildPrimitiveBlock()
    {
        // String table (index 0 must be ""):
        // 0:"" 1:"highway" 2:"crossing" 3:"traffic_signal" 4:"yes" 5:"name" 6:"Main Street"
        // 7:"residential" 8:"type" 9:"restriction" 10:"no_left_turn" 11:"from" 12:"via" 13:"to"
        string[] strings =
        {
            "", "highway", "crossing", "traffic_signal", "yes", "name", "Main Street",
            "residential", "type", "restriction", "no_left_turn", "from", "via", "to",
        };

        var stringTable = new ProtoWriter();
        foreach (string s in strings)
        {
            stringTable.WriteBytesField(1, Encoding.UTF8.GetBytes(s));
        }

        // granularity = 100 (default), lat/lon offset 0. Lat/lon are stored as
        // value = degrees * 1e9 / granularity.
        // For 41.5 deg: 41.5e9 / 100 = 415000000.
        static long Coord(double deg) => (long)(deg * 1e9 / 100.0);

        // DenseNodes: ids delta [100, +1], lats delta, lons delta, keys_vals.
        var dense = new ProtoWriter();
        dense.WritePackedSInt64Field(1, new long[] { 100, 1 });                         // id deltas
        dense.WritePackedSInt64Field(8, new[] { Coord(41.5), Coord(41.6) - Coord(41.5) }); // lat deltas
        dense.WritePackedSInt64Field(9, new[] { Coord(12.5), Coord(12.6) - Coord(12.5) }); // lon deltas
        // keys_vals: node0 -> highway=crossing (1,2) then 0; node1 -> no tags (just 0).
        dense.WritePackedVarintsField(10, new ulong[] { 1, 2, 0, 0 });

        // Regular Node id=200 (sint64), lat/lon, tag traffic_signal=yes (3,4).
        var node = new ProtoWriter();
        node.WriteSInt64Field(1, 200);
        node.WritePackedVarintsField(2, new ulong[] { 3 }); // keys
        node.WritePackedVarintsField(3, new ulong[] { 4 }); // vals
        node.WriteSInt64Field(8, Coord(41.7)); // lat
        node.WriteSInt64Field(9, Coord(12.7)); // lon

        // Way id=500 (int64), keys/vals highway=residential (1,7) name=Main Street (5,6),
        // refs delta-coded [100, +1, +99] -> 100,101,200.
        var way = new ProtoWriter();
        way.WriteVarintField(1, 500);
        way.WritePackedVarintsField(2, new ulong[] { 1, 5 });
        way.WritePackedVarintsField(3, new ulong[] { 7, 6 });
        way.WritePackedSInt64Field(8, new long[] { 100, 1, 99 });

        // Relation id=900, type=restriction (8,9) restriction=no_left_turn (1,10),
        // roles_sid [from=11, via=12, to=13], memids delta [500, -400, 401] -> 500,100,501,
        // types [way=1, node=0, way=1].
        var relation = new ProtoWriter();
        relation.WriteVarintField(1, 900);
        relation.WritePackedVarintsField(2, new ulong[] { 8, 9 });   // keys: type(8), restriction(9)
        relation.WritePackedVarintsField(3, new ulong[] { 9, 10 });  // vals: restriction(9), no_left_turn(10)
        relation.WritePackedVarintsField(8, new ulong[] { 11, 12, 13 }); // roles_sid
        relation.WritePackedSInt64Field(9, new long[] { 500, -400, 401 }); // memids delta
        relation.WritePackedVarintsField(10, new ulong[] { 1, 0, 1 });    // types

        // PrimitiveGroup containing dense (2), node (1), way (3), relation (4).
        var group = new ProtoWriter();
        group.WriteBytesField(2, dense.ToArray());
        group.WriteBytesField(1, node.ToArray());
        group.WriteBytesField(3, way.ToArray());
        group.WriteBytesField(4, relation.ToArray());

        // PrimitiveBlock: stringtable (1), primitivegroup (2), granularity (17)=100.
        var block = new ProtoWriter();
        block.WriteBytesField(1, stringTable.ToArray());
        block.WriteBytesField(2, group.ToArray());
        block.WriteVarintField(17, 100);
        return block.ToArray();
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

    /// <summary>Minimal protobuf writer matching the wire format the reader decodes.</summary>
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
