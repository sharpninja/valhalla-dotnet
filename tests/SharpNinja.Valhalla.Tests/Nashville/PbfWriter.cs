// THROWAWAY engine-validation helper. Minimal OSM PBF writer: emits a valid .osm.pbf that the
// project's own OsmPbfReader (and libosmium) decode. Uses regular (non-dense) Nodes, Ways, Relations
// in PrimitiveBlocks, granularity 100 (1e-7 deg), lat/lon offset 0, zlib-compressed OSMData blobs and
// a single OSMHeader blob carrying the bbox. Entities are chunked into multiple PrimitiveBlocks so no
// uncompressed blob exceeds the reader's 32 MiB limit.
//
// Protobuf wire encoding is hand-rolled (varint / length-delimited / sint64 zigzag) per the
// fileformat.proto + osmformat.proto schema. No external protobuf dependency.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Nashville;

internal static class PbfWriter
{
    internal readonly struct NodeRec
    {
        public NodeRec(ulong id, double lat, double lon, List<KeyValuePair<string, string>> tags)
        {
            Id = id;
            Lat = lat;
            Lon = lon;
            Tags = tags;
        }

        public ulong Id { get; }
        public double Lat { get; }
        public double Lon { get; }
        public List<KeyValuePair<string, string>> Tags { get; }
    }

    internal readonly struct WayRec
    {
        public WayRec(ulong id, List<ulong> nodeRefs, List<KeyValuePair<string, string>> tags)
        {
            Id = id;
            NodeRefs = nodeRefs;
            Tags = tags;
        }

        public ulong Id { get; }
        public List<ulong> NodeRefs { get; }
        public List<KeyValuePair<string, string>> Tags { get; }
    }

    internal readonly struct RelationRec
    {
        public RelationRec(ulong id, List<OsmRelationMember> members, List<KeyValuePair<string, string>> tags)
        {
            Id = id;
            Members = members;
            Tags = tags;
        }

        public ulong Id { get; }
        public List<OsmRelationMember> Members { get; }
        public List<KeyValuePair<string, string>> Tags { get; }
    }

    private const int Granularity = 100; // 1e-7 degrees per unit (100 nano-degrees)

    // Keep each primitive block well under the reader's 32 MiB uncompressed cap.
    private const int NodesPerBlock = 8000;
    private const int WaysPerBlock = 8000;
    private const int RelationsPerBlock = 8000;

    public static void Write(
        Stream output,
        double minLon, double minLat, double maxLon, double maxLat,
        List<NodeRec> nodes, List<WayRec> ways, List<RelationRec> relations)
    {
        // ---- OSMHeader ----
        byte[] header = BuildHeaderBlock(minLon, minLat, maxLon, maxLat);
        WriteBlob(output, "OSMHeader", header);

        // ---- OSMData blocks ----
        for (int i = 0; i < nodes.Count; i += NodesPerBlock)
        {
            int count = Math.Min(NodesPerBlock, nodes.Count - i);
            byte[] block = BuildNodeBlock(nodes, i, count);
            WriteBlob(output, "OSMData", block);
        }

        for (int i = 0; i < ways.Count; i += WaysPerBlock)
        {
            int count = Math.Min(WaysPerBlock, ways.Count - i);
            byte[] block = BuildWayBlock(ways, i, count);
            WriteBlob(output, "OSMData", block);
        }

        for (int i = 0; i < relations.Count; i += RelationsPerBlock)
        {
            int count = Math.Min(RelationsPerBlock, relations.Count - i);
            byte[] block = BuildRelationBlock(relations, i, count);
            WriteBlob(output, "OSMData", block);
        }
    }

    // ---- block builders -------------------------------------------------------

    // HeaderBlock { optional HeaderBBox bbox = 1; repeated string required_features = 4; }
    // HeaderBBox { required sint64 left=1; right=2; top=3; bottom=4; } in nano-degrees.
    private static byte[] BuildHeaderBlock(double minLon, double minLat, double maxLon, double maxLat)
    {
        var bbox = new MemoryStream();
        WriteTag(bbox, 1, WireSint); WriteSint64(bbox, (long)Math.Round(minLon * 1e9)); // left
        WriteTag(bbox, 2, WireSint); WriteSint64(bbox, (long)Math.Round(maxLon * 1e9)); // right
        WriteTag(bbox, 3, WireSint); WriteSint64(bbox, (long)Math.Round(maxLat * 1e9)); // top
        WriteTag(bbox, 4, WireSint); WriteSint64(bbox, (long)Math.Round(minLat * 1e9)); // bottom

        var hb = new MemoryStream();
        WriteTag(hb, 1, WireBytes); WriteBytes(hb, bbox.ToArray());
        // required_features: "OsmSchema-V0.6"
        WriteTag(hb, 4, WireBytes); WriteString(hb, "OsmSchema-V0.6");
        return hb.ToArray();
    }

    // PrimitiveBlock { StringTable stringtable=1; repeated PrimitiveGroup primitivegroup=2;
    //                  int32 granularity=17; int64 lat_offset=19; int64 lon_offset=20; }
    private static byte[] BuildNodeBlock(List<NodeRec> nodes, int start, int count)
    {
        var st = new StringTableBuilder();
        // Pre-intern tag strings.
        var group = new MemoryStream();
        for (int n = 0; n < count; n++)
        {
            NodeRec node = nodes[start + n];
            byte[] nodeMsg = BuildNode(node, st);
            WriteTag(group, 1, WireBytes); // PrimitiveGroup.nodes = 1
            WriteBytes(group, nodeMsg);
        }

        return AssemblePrimitiveBlock(st, group.ToArray());
    }

    private static byte[] BuildWayBlock(List<WayRec> ways, int start, int count)
    {
        var st = new StringTableBuilder();
        var group = new MemoryStream();
        for (int w = 0; w < count; w++)
        {
            WayRec way = ways[start + w];
            byte[] wayMsg = BuildWay(way, st);
            WriteTag(group, 3, WireBytes); // PrimitiveGroup.ways = 3
            WriteBytes(group, wayMsg);
        }

        return AssemblePrimitiveBlock(st, group.ToArray());
    }

    private static byte[] BuildRelationBlock(List<RelationRec> relations, int start, int count)
    {
        var st = new StringTableBuilder();
        var group = new MemoryStream();
        for (int r = 0; r < count; r++)
        {
            RelationRec rel = relations[start + r];
            byte[] relMsg = BuildRelation(rel, st);
            WriteTag(group, 4, WireBytes); // PrimitiveGroup.relations = 4
            WriteBytes(group, relMsg);
        }

        return AssemblePrimitiveBlock(st, group.ToArray());
    }

    private static byte[] AssemblePrimitiveBlock(StringTableBuilder st, byte[] groupAndContents)
    {
        var pb = new MemoryStream();

        // field 1: StringTable
        WriteTag(pb, 1, WireBytes);
        WriteBytes(pb, st.Build());

        // field 2: one PrimitiveGroup carrying all the entities of this block.
        // NOTE: BuildNodeBlock/etc already wrote per-entity tags into `groupAndContents` as the BODY
        // of a single PrimitiveGroup. Wrap it as field 2.
        WriteTag(pb, 2, WireBytes);
        WriteBytes(pb, groupAndContents);

        // field 17: granularity
        WriteTag(pb, 17, WireVarint);
        WriteVarint(pb, (ulong)Granularity);
        // lat_offset(19)/lon_offset(20) default 0 - omit (proto default).
        return pb.ToArray();
    }

    // Node { sint64 id=1; repeated uint32 keys=2 [packed]; repeated uint32 vals=3 [packed];
    //        Info info=4; sint64 lat=8; sint64 lon=9; }
    private static byte[] BuildNode(NodeRec node, StringTableBuilder st)
    {
        var m = new MemoryStream();
        WriteTag(m, 1, WireSint); WriteSint64(m, (long)node.Id);

        WriteTagsPacked(m, node.Tags, st);

        long lat = (long)Math.Round(node.Lat * 1e9 / Granularity);
        long lon = (long)Math.Round(node.Lon * 1e9 / Granularity);
        WriteTag(m, 8, WireSint); WriteSint64(m, lat);
        WriteTag(m, 9, WireSint); WriteSint64(m, lon);
        return m.ToArray();
    }

    // Way { int64 id=1; repeated uint32 keys=2 [packed]; repeated uint32 vals=3 [packed];
    //       repeated sint64 refs=8 [packed]; }
    private static byte[] BuildWay(WayRec way, StringTableBuilder st)
    {
        var m = new MemoryStream();
        WriteTag(m, 1, WireVarint); WriteVarint(m, way.Id);

        WriteTagsPacked(m, way.Tags, st);

        // packed delta-encoded sint64 refs
        var refs = new MemoryStream();
        long prev = 0;
        for (int i = 0; i < way.NodeRefs.Count; i++)
        {
            long cur = (long)way.NodeRefs[i];
            WriteSint64(refs, cur - prev);
            prev = cur;
        }

        WriteTag(m, 8, WireBytes); WriteBytes(m, refs.ToArray());
        return m.ToArray();
    }

    // Relation { int64 id=1; repeated uint32 keys=2; repeated uint32 vals=3;
    //            repeated int32 roles_sid=8 [packed]; repeated sint64 memids=9 [packed];
    //            repeated MemberType types=10 [packed]; }
    private static byte[] BuildRelation(RelationRec rel, StringTableBuilder st)
    {
        var m = new MemoryStream();
        WriteTag(m, 1, WireVarint); WriteVarint(m, rel.Id);

        WriteTagsPacked(m, rel.Tags, st);

        var rolesSid = new MemoryStream();
        var memids = new MemoryStream();
        var types = new MemoryStream();
        long prevMem = 0;
        foreach (OsmRelationMember mem in rel.Members)
        {
            WriteVarint(rolesSid, (ulong)st.Intern(mem.Role));
            long cur = (long)mem.Id;
            WriteSint64(memids, cur - prevMem);
            prevMem = cur;
            WriteVarint(types, (ulong)(int)mem.Type); // Node=0, Way=1, Relation=2
        }

        WriteTag(m, 8, WireBytes); WriteBytes(m, rolesSid.ToArray());
        WriteTag(m, 9, WireBytes); WriteBytes(m, memids.ToArray());
        WriteTag(m, 10, WireBytes); WriteBytes(m, types.ToArray());
        return m.ToArray();
    }

    private static void WriteTagsPacked(MemoryStream m, List<KeyValuePair<string, string>> tags, StringTableBuilder st)
    {
        if (tags.Count == 0)
        {
            return;
        }

        var keys = new MemoryStream();
        var vals = new MemoryStream();
        foreach (KeyValuePair<string, string> kv in tags)
        {
            WriteVarint(keys, (ulong)st.Intern(kv.Key));
            WriteVarint(vals, (ulong)st.Intern(kv.Value));
        }

        WriteTag(m, 2, WireBytes); WriteBytes(m, keys.ToArray());
        WriteTag(m, 3, WireBytes); WriteBytes(m, vals.ToArray());
    }

    // ---- string table ---------------------------------------------------------

    private sealed class StringTableBuilder
    {
        private readonly List<string> _strings = new() { string.Empty }; // index 0 = "" per spec
        private readonly Dictionary<string, int> _index = new() { [string.Empty] = 0 };

        public int Intern(string s)
        {
            if (_index.TryGetValue(s, out int idx))
            {
                return idx;
            }

            idx = _strings.Count;
            _strings.Add(s);
            _index[s] = idx;
            return idx;
        }

        // StringTable { repeated bytes s = 1; }
        public byte[] Build()
        {
            var m = new MemoryStream();
            foreach (string s in _strings)
            {
                WriteTag(m, 1, WireBytes);
                WriteString(m, s);
            }

            return m.ToArray();
        }
    }

    // ---- blob framing ---------------------------------------------------------

    private static void WriteBlob(Stream output, string type, byte[] content)
    {
        byte[] zlib = ZlibCompress(content);

        // Blob { optional bytes raw=1; optional int32 raw_size=2; optional bytes zlib_data=3; }
        var blob = new MemoryStream();
        WriteTag(blob, 2, WireVarint); WriteVarint(blob, (ulong)content.Length); // raw_size
        WriteTag(blob, 3, WireBytes); WriteBytes(blob, zlib);                     // zlib_data
        byte[] blobBytes = blob.ToArray();

        // BlobHeader { required string type=1; required int32 datasize=3; }
        var bh = new MemoryStream();
        WriteTag(bh, 1, WireBytes); WriteString(bh, type);
        WriteTag(bh, 3, WireVarint); WriteVarint(bh, (ulong)blobBytes.Length);
        byte[] bhBytes = bh.ToArray();

        // 4-byte big-endian BlobHeader length.
        Span<byte> len = stackalloc byte[4];
        len[0] = (byte)((bhBytes.Length >> 24) & 0xFF);
        len[1] = (byte)((bhBytes.Length >> 16) & 0xFF);
        len[2] = (byte)((bhBytes.Length >> 8) & 0xFF);
        len[3] = (byte)(bhBytes.Length & 0xFF);
        output.Write(len);
        output.Write(bhBytes, 0, bhBytes.Length);
        output.Write(blobBytes, 0, blobBytes.Length);
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }

    // ---- protobuf primitives --------------------------------------------------

    private const int WireVarint = 0;
    private const int WireBytes = 2;
    private const int WireSint = 0; // sint uses varint wire type with zigzag payload

    private static void WriteTag(Stream s, int field, int wireType)
        => WriteVarint(s, (ulong)((field << 3) | wireType));

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        s.WriteByte((byte)v);
    }

    private static void WriteSint64(Stream s, long v)
    {
        ulong zig = (ulong)((v << 1) ^ (v >> 63));
        WriteVarint(s, zig);
    }

    private static void WriteBytes(Stream s, byte[] b)
    {
        WriteVarint(s, (ulong)b.Length);
        s.Write(b, 0, b.Length);
    }

    private static void WriteString(Stream s, string str)
    {
        byte[] b = Encoding.UTF8.GetBytes(str);
        WriteBytes(s, b);
    }
}
