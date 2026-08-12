// Faithful hand-rolled OSM PBF reader (no external OSM/protobuf library).
// Source schema: OSMPBF fileformat.proto + osmformat.proto (the canonical OSM PBF spec
// used by libosmium in valhalla/third_party/libosmium). Decoding constants (blob header
// sizes, lonlat resolution) mirror valhalla/third_party/libosmium include/osmium/io/detail/pbf.hpp.
//
// An .osm.pbf file is a sequence of fileblocks, each: a 4-byte big-endian length, then a
// BlobHeader (type + datasize), then a Blob. The Blob holds either raw or zlib-compressed
// bytes. "OSMHeader" blobs carry a HeaderBlock; "OSMData" blobs carry a PrimitiveBlock.
//
// A PrimitiveBlock has a string table, a granularity (default 100 nano-degrees), lat/lon
// offsets, and PrimitiveGroups. Each group holds dense nodes (delta-decoded ids/lat/lon and
// a packed keys_vals stream), regular nodes, ways (delta-decoded refs), and relations
// (delta-decoded member ids with member types/roles). This reader decodes all of those and
// drives an IOsmPbfVisitor with resolved tag dictionaries and lat/lon in degrees.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>Member entity type within an OSM relation (matches osmformat.proto Relation.MemberType).</summary>
public enum OsmMemberType : byte
{
    Node = 0,
    Way = 1,
    Relation = 2,
}

/// <summary>A single relation member: the referenced id, its type, and its role string.</summary>
public readonly struct OsmRelationMember
{
    public OsmRelationMember(ulong id, OsmMemberType type, string role)
    {
        Id = id;
        Type = type;
        Role = role;
    }

    /// <summary>OSM id of the referenced entity.</summary>
    public ulong Id { get; }

    /// <summary>Entity type of the member.</summary>
    public OsmMemberType Type { get; }

    /// <summary>Member role string (e.g. "from", "via", "to").</summary>
    public string Role { get; }
}

/// <summary>
/// Visitor invoked by <see cref="OsmPbfReader"/> as it decodes the PBF. Tag dictionaries are
/// fully resolved (string-table indices already looked up). Lat/lon are in degrees. This is
/// the callback API the way/node tag transforms plug into.
/// </summary>
public interface IOsmPbfVisitor
{
    /// <summary>Called once per OSMHeader block with the bounding box (if present) and required features.</summary>
    void Header(double? minLat, double? minLon, double? maxLat, double? maxLon, IReadOnlyList<string> requiredFeatures);

    /// <summary>Called for each OSM node with its id, position (degrees), and resolved tags.</summary>
    void Node(ulong id, double lat, double lon, IReadOnlyDictionary<string, string> tags);

    /// <summary>Called for each OSM way with its id, ordered node-ref list, and resolved tags.</summary>
    void Way(ulong id, IReadOnlyList<ulong> nodeRefs, IReadOnlyDictionary<string, string> tags);

    /// <summary>Called for each OSM relation with its id, members, and resolved tags.</summary>
    void Relation(ulong id, IReadOnlyList<OsmRelationMember> members, IReadOnlyDictionary<string, string> tags);
}

/// <summary>
/// Optional high-throughput visitor contract for callback-scoped way references. Implementations
/// must consume <paramref name="nodeRefs"/> synchronously and must not retain the span.
/// </summary>
public interface IOsmPbfSpanVisitor : IOsmPbfVisitor
{
    /// <summary>Called for an OSM way without materializing its ordered node references.</summary>
    void Way(
        ulong id,
        ReadOnlySpan<ulong> nodeRefs,
        IReadOnlyDictionary<string, string> tags);
}

/// <summary>
/// Faithful OSM PBF reader. Streams an <c>.osm.pbf</c> file, inflates each blob, parses the
/// HeaderBlock / PrimitiveBlock messages, performs string-table + delta decoding, and drives
/// an <see cref="IOsmPbfVisitor"/>. No external OSM or protobuf dependency.
/// </summary>
public sealed class OsmPbfReader
{
    // Mirrors libosmium pbf.hpp constants.
    private const int MaxBlobHeaderSize = 64 * 1024;
    private const long MaxUncompressedBlobSize = 32L * 1024L * 1024L;

    private readonly IOsmPbfVisitor _visitor;

    /// <summary>Creates a reader that drives the given visitor.</summary>
    public OsmPbfReader(IOsmPbfVisitor visitor)
    {
        _visitor = visitor ?? throw new ArgumentNullException(nameof(visitor));
    }

    /// <summary>Parses the PBF file at <paramref name="path"/>, invoking the visitor for each entity.</summary>
    public void Parse(string path)
    {
        using FileStream fs = File.OpenRead(path);
        Parse(fs);
    }

    /// <summary>Parses a PBF stream, invoking the visitor for each entity.</summary>
    public void Parse(Stream stream)
    {
        while (true)
        {
            // Each fileblock starts with a 4-byte big-endian length of the BlobHeader.
            byte[] lenBuf = new byte[4];
            int read = ReadFull(stream, lenBuf, 0, 4);
            if (read == 0)
            {
                break; // clean EOF
            }

            if (read != 4)
            {
                throw new InvalidDataException("PBF error: truncated blob header length");
            }

            int headerLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (headerLen <= 0 || headerLen > MaxBlobHeaderSize)
            {
                throw new InvalidDataException("PBF error: invalid blob header size");
            }

            byte[] headerBytes = new byte[headerLen];
            if (ReadFull(stream, headerBytes, 0, headerLen) != headerLen)
            {
                throw new InvalidDataException("PBF error: truncated blob header");
            }

            (string blobType, int blobSize) = ParseBlobHeader(headerBytes);

            byte[] blobBytes = new byte[blobSize];
            if (ReadFull(stream, blobBytes, 0, blobSize) != blobSize)
            {
                throw new InvalidDataException("PBF error: truncated blob");
            }

            byte[] data = DecodeBlob(blobBytes);

            switch (blobType)
            {
                case "OSMHeader":
                    ParseHeaderBlock(data);
                    break;
                case "OSMData":
                    ParsePrimitiveBlock(data);
                    break;
                default:
                    // Unknown blob types are skipped (forward-compatible, like libosmium).
                    break;
            }
        }
    }

    // ---- BlobHeader (fileformat.proto) ----------------------------------------
    // message BlobHeader { required string type = 1; optional bytes indexdata = 2;
    //                      required int32 datasize = 3; }
    private static (string type, int datasize) ParseBlobHeader(byte[] bytes)
    {
        var r = new ProtoReader(bytes);
        string type = string.Empty;
        int datasize = 0;
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.LengthDelimited:
                    type = r.ReadString();
                    break;
                case 3 when wt == WireType.Varint:
                    datasize = (int)r.ReadVarint();
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        return (type, datasize);
    }

    // ---- Blob (fileformat.proto) ----------------------------------------------
    // message Blob { optional bytes raw = 1; optional int32 raw_size = 2;
    //                optional bytes zlib_data = 3; optional bytes lzma_data = 4; ... }
    private static byte[] DecodeBlob(byte[] bytes)
    {
        var r = new ProtoReader(bytes);
        byte[]? raw = null;
        byte[]? zlib = null;
        int rawSize = 0;
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.LengthDelimited:
                    raw = r.ReadBytes();
                    break;
                case 2 when wt == WireType.Varint:
                    rawSize = (int)r.ReadVarint();
                    break;
                case 3 when wt == WireType.LengthDelimited:
                    zlib = r.ReadBytes();
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        if (raw != null)
        {
            return raw;
        }

        if (zlib != null)
        {
            int outSize = rawSize > 0 ? rawSize : 0;
            return Inflate(zlib, outSize);
        }

        throw new InvalidDataException("PBF error: blob has no raw or zlib data (unsupported compression)");
    }

    // zlib (RFC 1950) inflate: 2-byte header + deflate stream + adler32. .NET's
    // ZLibStream handles the zlib wrapper directly.
    private static byte[] Inflate(byte[] compressed, int expectedSize)
    {
        if (expectedSize > MaxUncompressedBlobSize)
        {
            throw new InvalidDataException("PBF error: uncompressed blob too large");
        }

        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = expectedSize > 0 ? new MemoryStream(expectedSize) : new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    // ---- HeaderBlock (osmformat.proto) ----------------------------------------
    // message HeaderBlock { optional HeaderBBox bbox = 1; repeated string required_features = 4; ... }
    // message HeaderBBox { required sint64 left=1; right=2; top=3; bottom=4; } (in nano-degrees)
    private void ParseHeaderBlock(byte[] data)
    {
        var r = new ProtoReader(data);
        double? minLat = null, minLon = null, maxLat = null, maxLon = null;
        var requiredFeatures = new List<string>();
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.LengthDelimited:
                    byte[] bboxBytes = r.ReadBytes();
                    (minLon, maxLon, maxLat, minLat) = ParseHeaderBBox(bboxBytes);
                    break;
                case 4 when wt == WireType.LengthDelimited:
                    requiredFeatures.Add(r.ReadString());
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        _visitor.Header(minLat, minLon, maxLat, maxLon, requiredFeatures);
    }

    private static (double left, double right, double top, double bottom) ParseHeaderBBox(byte[] bytes)
    {
        var r = new ProtoReader(bytes);
        const double Nano = 1e-9;
        double left = 0, right = 0, top = 0, bottom = 0;
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.Varint:
                    left = r.ReadSInt64() * Nano;
                    break;
                case 2 when wt == WireType.Varint:
                    right = r.ReadSInt64() * Nano;
                    break;
                case 3 when wt == WireType.Varint:
                    top = r.ReadSInt64() * Nano;
                    break;
                case 4 when wt == WireType.Varint:
                    bottom = r.ReadSInt64() * Nano;
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        return (left, right, top, bottom);
    }

    // ---- PrimitiveBlock (osmformat.proto) -------------------------------------
    // message PrimitiveBlock {
    //   required StringTable stringtable = 1;
    //   repeated PrimitiveGroup primitivegroup = 2;
    //   optional int32 granularity = 17 [default = 100];
    //   optional int64 lat_offset = 19 [default = 0];
    //   optional int64 lon_offset = 20 [default = 0];
    //   optional int32 date_granularity = 18 [default = 1000];
    // }
    private void ParsePrimitiveBlock(byte[] data)
    {
        var r = new ProtoReader(data);
        var stringTable = new List<byte[]>();
        var groups = new List<byte[]>();
        int granularity = 100;
        long latOffset = 0;
        long lonOffset = 0;

        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.LengthDelimited:
                    ParseStringTable(r.ReadBytes(), stringTable);
                    break;
                case 2 when wt == WireType.LengthDelimited:
                    groups.Add(r.ReadBytes());
                    break;
                case 17 when wt == WireType.Varint:
                    granularity = (int)r.ReadVarint();
                    break;
                case 19 when wt == WireType.Varint:
                    latOffset = r.ReadVarintSignedAsInt64();
                    break;
                case 20 when wt == WireType.Varint:
                    lonOffset = r.ReadVarintSignedAsInt64();
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        // String 0 is always "" by spec; decode the rest as UTF-8 lazily on lookup.
        string[] strings = new string[stringTable.Count];
        for (int i = 0; i < stringTable.Count; i++)
        {
            strings[i] = Encoding.UTF8.GetString(stringTable[i]);
        }

        foreach (byte[] group in groups)
        {
            ParsePrimitiveGroup(group, strings, granularity, latOffset, lonOffset);
        }
    }

    // message StringTable { repeated bytes s = 1; }
    private static void ParseStringTable(byte[] bytes, List<byte[]> outTable)
    {
        var r = new ProtoReader(bytes);
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            if (field == 1 && wt == WireType.LengthDelimited)
            {
                outTable.Add(r.ReadBytes());
            }
            else
            {
                r.SkipField(wt);
            }
        }
    }

    // message PrimitiveGroup {
    //   repeated Node     nodes = 1;
    //   optional DenseNodes dense = 2;
    //   repeated Way      ways = 3;
    //   repeated Relation relations = 4;
    //   repeated ChangeSet changesets = 5;
    // }
    private void ParsePrimitiveGroup(byte[] bytes, string[] strings, int granularity, long latOffset, long lonOffset)
    {
        var r = new ProtoReader(bytes);
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.LengthDelimited:
                    ParseNode(r.ReadBytes(), strings, granularity, latOffset, lonOffset);
                    break;
                case 2 when wt == WireType.LengthDelimited:
                    ParseDenseNodes(r.ReadBytes(), strings, granularity, latOffset, lonOffset);
                    break;
                case 3 when wt == WireType.LengthDelimited:
                    ParseWay(r.ReadBytes(), strings);
                    break;
                case 4 when wt == WireType.LengthDelimited:
                    ParseRelation(r.ReadBytes(), strings);
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }
    }

    // message Node { required sint64 id = 1; repeated uint32 keys = 2 [packed]; repeated uint32 vals = 3 [packed];
    //                optional Info info = 4; required sint64 lat = 8; required sint64 lon = 9; }
    private void ParseNode(byte[] bytes, string[] strings, int granularity, long latOffset, long lonOffset)
    {
        var r = new ProtoReader(bytes);
        long id = 0;
        long lat = 0;
        long lon = 0;
        var keys = new List<uint>();
        var vals = new List<uint>();
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.Varint:
                    id = r.ReadSInt64();
                    break;
                case 2 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsUInt32(keys);
                    break;
                case 2 when wt == WireType.Varint:
                    keys.Add((uint)r.ReadVarint());
                    break;
                case 3 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsUInt32(vals);
                    break;
                case 3 when wt == WireType.Varint:
                    vals.Add((uint)r.ReadVarint());
                    break;
                case 8 when wt == WireType.Varint:
                    lat = r.ReadSInt64();
                    break;
                case 9 when wt == WireType.Varint:
                    lon = r.ReadSInt64();
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        double latDeg = 1e-9 * (latOffset + granularity * lat);
        double lonDeg = 1e-9 * (lonOffset + granularity * lon);

        var tags = new OsmPbfTransientTagDictionary(keys.Count);
        for (int i = 0; i < keys.Count && i < vals.Count; i++)
        {
            tags[strings[keys[i]]] = strings[vals[i]];
        }

        _visitor.Node((ulong)id, latDeg, lonDeg, tags);
    }

    // message DenseNodes { repeated sint64 id = 1 [packed]; // delta coded
    //                      repeated sint64 lat = 8 [packed]; repeated sint64 lon = 9 [packed]; // delta coded
    //                      repeated int32 keys_vals = 10 [packed]; }
    private void ParseDenseNodes(byte[] bytes, string[] strings, int granularity, long latOffset, long lonOffset)
    {
        var r = new ProtoReader(bytes);
        var ids = new List<long>();
        var lats = new List<long>();
        var lons = new List<long>();
        var keysVals = new List<int>();

        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.LengthDelimited:
                    r.ReadPackedSInt64(ids);
                    break;
                case 8 when wt == WireType.LengthDelimited:
                    r.ReadPackedSInt64(lats);
                    break;
                case 9 when wt == WireType.LengthDelimited:
                    r.ReadPackedSInt64(lons);
                    break;
                case 10 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsInt32(keysVals);
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        // Delta-decode ids/lat/lon and walk the keys_vals stream (0-terminated per node).
        long id = 0;
        long lat = 0;
        long lon = 0;
        int kvIndex = 0;
        for (int n = 0; n < ids.Count; n++)
        {
            id += ids[n];
            lat += lats[n];
            lon += lons[n];

            var tags = new OsmPbfTransientTagDictionary();
            if (keysVals.Count > 0)
            {
                // keys_vals: ... keyIdx, valIdx, keyIdx, valIdx, 0, keyIdx, valIdx, 0, ...
                // A single 0 separates one node's tags from the next; a node with no tags
                // is just a single 0.
                while (kvIndex < keysVals.Count && keysVals[kvIndex] != 0)
                {
                    int keyId = keysVals[kvIndex++];
                    int valId = keysVals[kvIndex++];
                    tags[strings[keyId]] = strings[valId];
                }

                kvIndex++; // skip the 0 terminator
            }

            double latDeg = 1e-9 * (latOffset + granularity * lat);
            double lonDeg = 1e-9 * (lonOffset + granularity * lon);
            _visitor.Node((ulong)id, latDeg, lonDeg, tags);
        }
    }

    // message Way { required int64 id = 1; repeated uint32 keys = 2 [packed]; repeated uint32 vals = 3 [packed];
    //               optional Info info = 4; repeated sint64 refs = 8 [packed]; } // delta coded
    private void ParseWay(byte[] bytes, string[] strings)
    {
        var r = new ProtoReader(bytes);
        long id = 0;
        var keys = new List<uint>();
        var vals = new List<uint>();
        var deltaRefs = new List<long>();
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.Varint:
                    id = r.ReadVarintSignedAsInt64();
                    break;
                case 2 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsUInt32(keys);
                    break;
                case 3 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsUInt32(vals);
                    break;
                case 8 when wt == WireType.LengthDelimited:
                    r.ReadPackedSInt64(deltaRefs);
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        var refs = new List<ulong>(deltaRefs.Count);
        long refId = 0;
        foreach (long d in deltaRefs)
        {
            refId += d;
            refs.Add((ulong)refId);
        }

        var tags = new OsmPbfTransientTagDictionary(keys.Count);
        for (int i = 0; i < keys.Count && i < vals.Count; i++)
        {
            tags[strings[keys[i]]] = strings[vals[i]];
        }

        _visitor.Way((ulong)id, refs, tags);
    }

    // message Relation { required int64 id = 1; repeated uint32 keys = 2 [packed]; repeated uint32 vals = 3 [packed];
    //                    optional Info info = 4; repeated int32 roles_sid = 8 [packed];
    //                    repeated sint64 memids = 9 [packed]; // delta coded
    //                    repeated MemberType types = 10 [packed]; }
    private void ParseRelation(byte[] bytes, string[] strings)
    {
        var r = new ProtoReader(bytes);
        long id = 0;
        var keys = new List<uint>();
        var vals = new List<uint>();
        var rolesSid = new List<int>();
        var memids = new List<long>();
        var types = new List<int>();
        while (!r.Eof)
        {
            (int field, WireType wt) = r.ReadTag();
            switch (field)
            {
                case 1 when wt == WireType.Varint:
                    id = r.ReadVarintSignedAsInt64();
                    break;
                case 2 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsUInt32(keys);
                    break;
                case 3 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsUInt32(vals);
                    break;
                case 8 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsInt32(rolesSid);
                    break;
                case 9 when wt == WireType.LengthDelimited:
                    r.ReadPackedSInt64(memids);
                    break;
                case 10 when wt == WireType.LengthDelimited:
                    r.ReadPackedVarintsInt32(types);
                    break;
                default:
                    r.SkipField(wt);
                    break;
            }
        }

        var members = new List<OsmRelationMember>(memids.Count);
        long memId = 0;
        for (int i = 0; i < memids.Count; i++)
        {
            memId += memids[i]; // delta coded
            var type = (OsmMemberType)(i < types.Count ? types[i] : 0);
            string role = i < rolesSid.Count ? strings[rolesSid[i]] : string.Empty;
            members.Add(new OsmRelationMember((ulong)memId, type, role));
        }

        var tags = new OsmPbfTransientTagDictionary(keys.Count);
        for (int i = 0; i < keys.Count && i < vals.Count; i++)
        {
            tags[strings[keys[i]]] = strings[vals[i]];
        }

        _visitor.Relation((ulong)id, members, tags);
    }

    private static int ReadFull(Stream s, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buffer, offset + total, count - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
