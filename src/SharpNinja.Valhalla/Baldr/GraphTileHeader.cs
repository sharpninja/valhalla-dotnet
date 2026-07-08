// Faithful C# port of Valhalla baldr graphtileheader.h + src/baldr/graphtileheader.cc
// (valhalla @ 3.7.0).
// Sources:
//   F:/github/valhalla/valhalla/baldr/graphtileheader.h
//   F:/github/valhalla/src/baldr/graphtileheader.cc
//
// CRITICAL FIDELITY: GraphTileHeader is read directly from the on-disk (gzip-decompressed)
// tile blob, so the byte/bit layout MUST match the C++ struct exactly. The C++ struct is
// asserted to be exactly 272 bytes (static_assert(sizeof(GraphTileHeader) == 272)).
//
// This port stores the header as a fixed 272-byte buffer and reads/writes every field
// with the exact bit positions and byte offsets the Itanium C++ ABI (x86-64 GCC/Clang)
// produces, which is the layout Valhalla uses to build tiles. The bit positions below were
// derived from the ABI allocation rules and verified to place the first trailing fixed
// member (spareword0_) at byte offset 80 and the whole struct at 272 bytes.
//
// ---------------------------------------------------------------------------------------
// Byte layout (little-endian, all multi-byte fields LE; bitfields packed LSB-first):
//   [0  ..  7]  word A (uint64): graphid_:46 | density_:4 | name_quality_:4 |
//                                speed_quality_:4 | exit_quality_:4 |
//                                has_elevation_:1 | has_ext_directededge_:1
//   [8  .. 15]  base_ll_      : float[2]  (lng, lat)
//   [16 .. 31]  version_      : char[16]  (NUL-padded ASCII)
//   [32 .. 39]  dataset_id_   : uint64
//   [40 .. 47]  word B (uint64): nodecount_:21 | directededgecount_:21 |
//                                predictedspeeds_count_:21 | spare1_:1
//   [48 .. 51]  uint32: transitioncount_:22 | spare3_:10
//   [52 .. 55]  uint32: turnlane_count_:21  | spare4_:11
//   [56 .. 63]  word E (uint64): transfercount_:16 | spare2_:7 | departurecount_:24 |
//                                stopcount_:16 | spare5_:1
//   [64 .. 71]  word F (uint64): routecount_:12 | schedulecount_:12 | signcount_:24 |
//                                spare6_:16
//   [72 .. 79]  word G (uint64): access_restriction_count_:24 | admincount_:16 | spare7_:24
//   [80 .. 87]  spareword0_   : uint64
//   [88 .. 95]  checksum_     : uint64
//   [96 .. 99]  complex_restriction_forward_offset_ : uint32
//   [100..103]  complex_restriction_reverse_offset_ : uint32
//   [104..107]  edgeinfo_offset_                    : uint32
//   [108..111]  textlist_offset_                    : uint32
//   [112..115]  date_created_                       : uint32
//   [116..215]  bin_offsets_  : uint32[25]
//   [216..219]  lane_connectivity_offset_           : uint32
//   [220..223]  predictedspeeds_offset_             : uint32
//   [224..227]  tile_size_                          : uint32
//   [228..271]  empty_slots_  : uint32[11]
// Total = 272 bytes.
// ---------------------------------------------------------------------------------------
//
// PORT-NOTE: the C++ default constructor sets the version via VALHALLA_PRINT_VERSION
// (a build-time macro). C# cannot read that macro; the default version is left empty
// (matching a freshly zeroed buffer). Tests/builders should call SetVersion explicitly,
// exactly as the C++ engine overwrites it when constructing/serializing tiles.

using System;
using System.Buffers.Binary;
using System.Text;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Summary information about a graph tile: version information, the tile's GraphId, quality
/// metrics, record counts, and offsets to the various variable-size data sections. Backed by a
/// fixed 272-byte buffer whose bit/byte layout is byte-for-byte identical to the C++
/// <c>class GraphTileHeader</c> so a tile blob parses identically.
/// </summary>
public sealed class GraphTileHeader
{
    // ------------------------------ size / layout constants ------------------------------

    /// <summary>Total size in bytes of the on-disk header. C++ <c>static_assert(sizeof == 272)</c>.</summary>
    public const int HeaderSize = 272;

    /// <summary>Number of expansion slots remaining in this tile. Mirrors C++ <c>kEmptySlots</c>.</summary>
    public const int EmptySlots = 11;

    /// <summary>Maximum size of the version string. Mirrors C++ <c>kMaxVersionSize</c>.</summary>
    public const int MaxVersionSize = 16;

    /// <summary>Maximum value used for quality metrics. Mirrors C++ <c>kMaxQualityMeasure</c>.</summary>
    public const uint MaxQualityMeasure = 15;

    /// <summary>Maximum number of node transitions per tile (22 bits). Mirrors C++ <c>kMaxNodeTransitions</c>.</summary>
    public const uint MaxNodeTransitions = 4194303;

    /// <summary>Maximum number of signs (24 bits). Mirrors C++ <c>kMaxSigns</c>.</summary>
    public const uint MaxSigns = 16777215;

    /// <summary>Dimension of the binned-edge grid. Mirrors C++ <c>kBinsDim</c>.</summary>
    public const int BinsDim = 5;

    /// <summary>Total number of bins in the tile (5x5). Mirrors C++ <c>kBinCount</c>.</summary>
    public const int BinCount = BinsDim * BinsDim;

    // Fixed byte offsets within the 272-byte buffer (see layout map above).
    private const int OffWordA = 0;
    private const int OffBaseLl = 8;
    private const int OffVersion = 16;
    private const int OffDatasetId = 32;
    private const int OffWordB = 40;
    private const int OffTransitionWord = 48; // uint32
    private const int OffTurnlaneWord = 52;   // uint32
    private const int OffWordE = 56;          // uint64
    private const int OffWordF = 64;          // uint64
    private const int OffWordG = 72;          // uint64
    private const int OffSpareword0 = 80;
    private const int OffChecksum = 88;
    private const int OffComplexFwd = 96;
    private const int OffComplexRev = 100;
    private const int OffEdgeInfo = 104;
    private const int OffTextList = 108;
    private const int OffDateCreated = 112;
    private const int OffBinOffsets = 116; // uint32[25]
    private const int OffLaneConnectivity = 216;
    private const int OffPredictedSpeeds = 220;
    private const int OffTileSize = 224;
    private const int OffEmptySlots = 228; // uint32[11]

    // The 272-byte on-disk image. All accessors read/write directly through this buffer.
    private readonly byte[] _buf = new byte[HeaderSize];

    /// <summary>Default constructor. Produces a fully zeroed 272-byte header.</summary>
    /// <remarks>
    /// PORT-NOTE: C++ <c>GraphTileHeader()</c> also calls <c>set_version(VALHALLA_PRINT_VERSION)</c>.
    /// That build-time macro is unavailable here, so the version is left empty until
    /// <see cref="SetVersion"/> is called. All bitfield members default to 0, matching the C++ ctor.
    /// </remarks>
    public GraphTileHeader()
    {
    }

    /// <summary>
    /// Parses a header from a tile blob beginning at <paramref name="offset"/> within
    /// <paramref name="source"/>. Copies the first 272 bytes verbatim.
    /// </summary>
    public static GraphTileHeader FromBytes(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new ArgumentException(
                $"GraphTileHeader requires at least {HeaderSize} bytes, got {source.Length}.",
                nameof(source));
        }

        var hdr = new GraphTileHeader();
        source.Slice(0, HeaderSize).CopyTo(hdr._buf);
        return hdr;
    }

    /// <summary>
    /// Copies the raw 272-byte image from another header into this one. Mirrors the C++
    /// <c>header_builder_ = *header_</c> assignment in the GraphTileBuilder deserialize path.
    /// </summary>
    public void CopyFrom(GraphTileHeader other)
    {
        ArgumentNullException.ThrowIfNull(other);
        other._buf.AsSpan(0, HeaderSize).CopyTo(_buf);
    }

    /// <summary>Returns a read-only view of the raw 272-byte on-disk image.</summary>
    public ReadOnlySpan<byte> AsSpan() => _buf;

    /// <summary>Copies the raw 272-byte image into a new array.</summary>
    public byte[] ToBytes() => (byte[])_buf.Clone();

    // ------------------------------ low-level bit helpers ------------------------------

    private ulong ReadWord(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(_buf.AsSpan(offset, 8));

    private void WriteWord(int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(offset, 8), value);

    private uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(offset, 4));

    private void WriteU32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(offset, 4), value);

    private static ulong GetBits(ulong word, int shift, int width)
    {
        ulong mask = width == 64 ? ulong.MaxValue : ((1UL << width) - 1UL);
        return (word >> shift) & mask;
    }

    private static ulong SetBits(ulong word, int shift, int width, ulong value)
    {
        ulong mask = width == 64 ? ulong.MaxValue : ((1UL << width) - 1UL);
        word &= ~(mask << shift);
        word |= (value & mask) << shift;
        return word;
    }

    private ulong GetWordField(int offset, int shift, int width) => GetBits(ReadWord(offset), shift, width);

    private void SetWordField(int offset, int shift, int width, ulong value) =>
        WriteWord(offset, SetBits(ReadWord(offset), shift, width, value));

    private uint GetU32Field(int offset, int shift, int width) =>
        (uint)GetBits(ReadU32(offset), shift, width);

    private void SetU32Field(int offset, int shift, int width, uint value) =>
        WriteU32(offset, (uint)SetBits(ReadU32(offset), shift, width, value));

    // ------------------------------ word A: graphid + quality ------------------------------

    /// <summary>Get the GraphId (tileid and level) of this tile.</summary>
    public GraphId Graphid() => new GraphId(GetWordField(OffWordA, 0, 46));

    /// <summary>Set the graph Id of this tile.</summary>
    public void SetGraphid(GraphId graphid) => SetWordField(OffWordA, 0, 46, graphid.Value);

    /// <summary>Get the relative road density within this tile (0-15).</summary>
    public uint Density() => (uint)GetWordField(OffWordA, 46, 4);

    /// <summary>Set the relative road density within this tile (clamped to kMaxDensity).</summary>
    public void SetDensity(uint density) =>
        SetWordField(OffWordA, 46, 4, density <= GraphConstants.MaxDensity ? density : GraphConstants.MaxDensity);

    /// <summary>Get the relative quality of name assignment for this tile (0-15).</summary>
    public uint NameQuality() => (uint)GetWordField(OffWordA, 50, 4);

    /// <summary>Set the relative quality of name assignment for this tile (clamped to kMaxQualityMeasure).</summary>
    public void SetNameQuality(uint nameQuality) =>
        SetWordField(OffWordA, 50, 4, nameQuality <= MaxQualityMeasure ? nameQuality : MaxQualityMeasure);

    /// <summary>Get the relative quality of speed assignment for this tile (0-15).</summary>
    public uint SpeedQuality() => (uint)GetWordField(OffWordA, 54, 4);

    /// <summary>Set the relative quality of speed assignment for this tile (clamped to kMaxQualityMeasure).</summary>
    public void SetSpeedQuality(uint speedQuality) =>
        SetWordField(OffWordA, 54, 4, speedQuality <= MaxQualityMeasure ? speedQuality : MaxQualityMeasure);

    /// <summary>Get the relative quality of exit signs for this tile (0-15).</summary>
    public uint ExitQuality() => (uint)GetWordField(OffWordA, 58, 4);

    /// <summary>Set the relative quality of exit signs for this tile (clamped to kMaxQualityMeasure).</summary>
    public void SetExitQuality(uint exitQuality) =>
        SetWordField(OffWordA, 58, 4, exitQuality <= MaxQualityMeasure ? exitQuality : MaxQualityMeasure);

    /// <summary>Gets the flag indicating whether this tile includes edge elevation data.</summary>
    public bool HasElevation() => GetWordField(OffWordA, 62, 1) != 0;

    /// <summary>Sets the flag indicating whether this tile includes edge elevation data.</summary>
    public void SetHasElevation(bool elev) => SetWordField(OffWordA, 62, 1, elev ? 1UL : 0UL);

    /// <summary>Gets the flag indicating whether this tile includes extended directed edge attributes.</summary>
    public bool HasExtDirectededge() => GetWordField(OffWordA, 63, 1) != 0;

    /// <summary>Sets the flag indicating whether this tile includes extended directed edge attributes.</summary>
    public void SetHasExtDirectededge(bool ext) => SetWordField(OffWordA, 63, 1, ext ? 1UL : 0UL);

    // ------------------------------ base lat/lon ------------------------------

    /// <summary>
    /// Get the base (SW corner) lng,lat of the tile (degrees). Mirrors C++ returning
    /// <c>{base_ll_[0], base_ll_[1]}</c> from the stored <c>float[2]</c>.
    /// </summary>
    public PointLL BaseLl()
    {
        float lng = BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(OffBaseLl)));
        float lat = BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(OffBaseLl + 4)));
        return new PointLL(lng, lat);
    }

    /// <summary>
    /// Sets the base (SW corner) lat,lon of the tile. The C++ stores the lng/lat as
    /// <c>float</c> (<c>base_ll_[0] = ll.lng(); base_ll_[1] = ll.lat();</c>), so the double
    /// inputs are narrowed to float here exactly as in the engine.
    /// </summary>
    public void SetBaseLl(PointLL ll)
    {
        WriteU32(OffBaseLl, unchecked((uint)BitConverter.SingleToInt32Bits((float)ll.Lng)));
        WriteU32(OffBaseLl + 4, unchecked((uint)BitConverter.SingleToInt32Bits((float)ll.Lat)));
    }

    // ------------------------------ version ------------------------------

    /// <summary>Gets the version of this tile (NUL-terminated ASCII, up to 16 chars).</summary>
    public string Version()
    {
        var span = _buf.AsSpan(OffVersion, MaxVersionSize);
        int len = span.IndexOf((byte)0);
        if (len < 0)
        {
            len = MaxVersionSize;
        }

        return Encoding.ASCII.GetString(span.Slice(0, len));
    }

    /// <summary>
    /// Set the version string. Faithful port of <c>set_version</c>: the array is reset to
    /// zero, at most <see cref="MaxVersionSize"/> bytes are copied, and the last byte is forced
    /// to NUL.
    /// </summary>
    public void SetVersion(string version)
    {
        var span = _buf.AsSpan(OffVersion, MaxVersionSize);
        span.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(version ?? string.Empty);
        int count = Math.Min(MaxVersionSize, bytes.Length);
        bytes.AsSpan(0, count).CopyTo(span);
        // version_[kMaxVersionSize - 1] = 0;
        span[MaxVersionSize - 1] = 0;
    }

    // ------------------------------ dataset id ------------------------------

    /// <summary>Returns the data set Id (latest OSM changeset Id).</summary>
    public ulong DatasetId() => ReadWord(OffDatasetId);

    /// <summary>Set the data set Id (latest OSM changeset Id).</summary>
    public void SetDatasetId(ulong id) => WriteWord(OffDatasetId, id);

    // ------------------------------ word B: node / edge / predicted-speed counts ------------------------------

    /// <summary>Gets the number of nodes in this tile.</summary>
    public uint Nodecount() => (uint)GetWordField(OffWordB, 0, 21);

    /// <summary>Sets the number of nodes in this tile. (C++ logs an error but still stores when too large.)</summary>
    public void SetNodecount(uint count) => SetWordField(OffWordB, 0, 21, count);

    /// <summary>Gets the number of directed edges in this tile.</summary>
    public uint Directededgecount() => (uint)GetWordField(OffWordB, 21, 21);

    /// <summary>Sets the number of directed edges in this tile.</summary>
    public void SetDirectededgecount(uint count) => SetWordField(OffWordB, 21, 21, count);

    /// <summary>Gets the count of predicted speed records.</summary>
    public uint PredictedspeedsCount() => (uint)GetWordField(OffWordB, 42, 21);

    /// <summary>Sets the count of predicted speed records within the tile.</summary>
    public void SetPredictedspeedsCount(uint count) => SetWordField(OffWordB, 42, 21, count);

    // ------------------------------ transition / turnlane (uint32 words) ------------------------------

    /// <summary>Gets the number of node transitions in this tile.</summary>
    public uint Transitioncount() => GetU32Field(OffTransitionWord, 0, 22);

    /// <summary>Sets the number of node transitions in this tile.</summary>
    public void SetTransitioncount(uint count) => SetU32Field(OffTransitionWord, 0, 22, count);

    /// <summary>Gets the number of turn lane records in this tile.</summary>
    public uint TurnlaneCount() => GetU32Field(OffTurnlaneWord, 0, 21);

    /// <summary>Sets the number of turn lane records within this tile.</summary>
    public void SetTurnlaneCount(uint count) => SetU32Field(OffTurnlaneWord, 0, 21, count);

    // ------------------------------ word E: transfer / departure / stop ------------------------------

    /// <summary>Gets the number of transit transfers in this tile.</summary>
    public uint Transfercount() => (uint)GetWordField(OffWordE, 0, 16);

    /// <summary>Sets the number of transit transfers in this tile. Throws if &gt; kMaxTransfers.</summary>
    public void SetTransfercount(uint transfers)
    {
        if (transfers > GraphConstants.MaxTransfers)
        {
            throw new InvalidOperationException(
                "Exceeding maximum number of transit transfer entries per tile");
        }

        SetWordField(OffWordE, 0, 16, transfers);
    }

    /// <summary>Gets the number of transit departures in this tile.</summary>
    public uint Departurecount() => (uint)GetWordField(OffWordE, 23, 24);

    /// <summary>Sets the number of transit departures in this tile. Throws if &gt; kMaxTransitDepartures.</summary>
    public void SetDeparturecount(uint departures)
    {
        if (departures > GraphConstants.MaxTransitDepartures)
        {
            throw new InvalidOperationException(
                "Exceeding maximum number of transit departures per tile");
        }

        SetWordField(OffWordE, 23, 24, departures);
    }

    /// <summary>Gets the number of transit stops in this tile.</summary>
    public uint Stopcount() => (uint)GetWordField(OffWordE, 47, 16);

    /// <summary>Sets the number of transit stops in this tile. Throws if &gt; kMaxTransitStops.</summary>
    public void SetStopcount(uint stops)
    {
        if (stops > GraphConstants.MaxTransitStops)
        {
            throw new InvalidOperationException(
                "Exceeding maximum number of transit stops per tile");
        }

        SetWordField(OffWordE, 47, 16, stops);
    }

    // ------------------------------ word F: route / schedule / sign ------------------------------

    /// <summary>Gets the number of transit routes in this tile.</summary>
    public uint Routecount() => (uint)GetWordField(OffWordF, 0, 12);

    /// <summary>Sets the number of transit routes in this tile. Throws if &gt; kMaxTransitRoutes.</summary>
    public void SetRoutecount(uint routes)
    {
        if (routes > GraphConstants.MaxTransitRoutes)
        {
            throw new InvalidOperationException(
                "Exceeding maximum number of transit routes per tile");
        }

        SetWordField(OffWordF, 0, 12, routes);
    }

    /// <summary>Gets the number of transit schedules in this tile.</summary>
    public uint Schedulecount() => (uint)GetWordField(OffWordF, 12, 12);

    /// <summary>Sets the number of transit schedules in this tile. Throws if &gt; kMaxTransitSchedules.</summary>
    public void SetSchedulecount(uint schedules)
    {
        if (schedules > GraphConstants.MaxTransitSchedules)
        {
            throw new InvalidOperationException(
                "Exceeding maximum number of transit schedule entries per tile");
        }

        SetWordField(OffWordF, 12, 12, schedules);
    }

    /// <summary>Gets the number of signs in this tile.</summary>
    public uint Signcount() => (uint)GetWordField(OffWordF, 24, 24);

    /// <summary>Sets the number of signs within this tile. (C++ logs an error but still stores when too large.)</summary>
    public void SetSigncount(uint count) => SetWordField(OffWordF, 24, 24, count);

    // ------------------------------ word G: access restriction / admin ------------------------------

    /// <summary>Gets the number of access restrictions in this tile.</summary>
    public uint AccessRestrictionCount() => (uint)GetWordField(OffWordG, 0, 24);

    /// <summary>Sets the number of access restrictions in this tile.</summary>
    public void SetAccessRestrictionCount(uint n) => SetWordField(OffWordG, 0, 24, n);

    /// <summary>Gets the number of admin records in this tile.</summary>
    public uint Admincount() => (uint)GetWordField(OffWordG, 24, 16);

    /// <summary>Sets the number of admin records within this tile.</summary>
    public void SetAdmincount(uint count) => SetWordField(OffWordG, 24, 16, count);

    // ------------------------------ checksum (formerly spareword1_) ------------------------------

    /// <summary>Get the 64-bit checksum hash of the tile's input.</summary>
    public ulong Checksum() => ReadWord(OffChecksum);

    /// <summary>Sets the 64-bit checksum hash of the tile's input.</summary>
    public void SetChecksum(ulong checksum) => WriteWord(OffChecksum, checksum);

    // ------------------------------ variable-section offsets ------------------------------

    /// <summary>Get the offset (bytes) to the complex restriction list in the forward direction.</summary>
    public uint ComplexRestrictionForwardOffset() => ReadU32(OffComplexFwd);

    /// <summary>Sets the offset to the complex restriction list in the forward direction.</summary>
    public void SetComplexRestrictionForwardOffset(uint offset) => WriteU32(OffComplexFwd, offset);

    /// <summary>Get the offset (bytes) to the complex restriction list in the reverse direction.</summary>
    public uint ComplexRestrictionReverseOffset() => ReadU32(OffComplexRev);

    /// <summary>Sets the offset to the complex restriction list in the reverse direction.</summary>
    public void SetComplexRestrictionReverseOffset(uint offset) => WriteU32(OffComplexRev, offset);

    /// <summary>Gets the offset (bytes) to the edge info.</summary>
    public uint EdgeinfoOffset() => ReadU32(OffEdgeInfo);

    /// <summary>Sets the offset to the edge info.</summary>
    public void SetEdgeinfoOffset(uint offset) => WriteU32(OffEdgeInfo, offset);

    /// <summary>Gets the offset (bytes) to the text list.</summary>
    public uint TextlistOffset() => ReadU32(OffTextList);

    /// <summary>Sets the offset to the text list.</summary>
    public void SetTextlistOffset(uint offset) => WriteU32(OffTextList, offset);

    /// <summary>Gets the date when this tile was created. Days since pivot date.</summary>
    public uint DateCreated() => ReadU32(OffDateCreated);

    /// <summary>Set the date created (days since pivot date).</summary>
    public void SetDateCreated(uint date) => WriteU32(OffDateCreated, date);

    /// <summary>Gets the offset (bytes) to the lane connectivity data.</summary>
    public uint LaneConnectivityOffset() => ReadU32(OffLaneConnectivity);

    /// <summary>Sets the offset to the lane connectivity data.</summary>
    public void SetLaneConnectivityOffset(uint offset) => WriteU32(OffLaneConnectivity, offset);

    /// <summary>Gets the offset (bytes) to predicted speeds.</summary>
    public uint PredictedspeedsOffset() => ReadU32(OffPredictedSpeeds);

    /// <summary>Sets the offset to predicted speed data within the tile.</summary>
    public void SetPredictedspeedsOffset(uint offset) => WriteU32(OffPredictedSpeeds, offset);

    /// <summary>Get the offset (bytes) to the end of the tile (the tile size).</summary>
    public uint EndOffset() => ReadU32(OffTileSize);

    /// <summary>Sets the offset (bytes) to the end of the tile.</summary>
    public void SetEndOffset(uint offset) => WriteU32(OffTileSize, offset);

    // ------------------------------ edge bins ------------------------------

    /// <summary>
    /// Get the begin/end offset (in the list of edge ids) for the bin at (column, row) in the 5x5 grid.
    /// </summary>
    public (uint Begin, uint End) BinOffset(int column, int row) => BinOffset(row * BinsDim + column);

    /// <summary>
    /// Get the begin/end offset (in the list of edge ids) for the bin at the given row-major index.
    /// </summary>
    public (uint Begin, uint End) BinOffset(int index)
    {
        if (index >= 0 && index < BinCount)
        {
            uint begin = index == 0 ? 0u : ReadU32(OffBinOffsets + ((index - 1) * 4));
            uint end = ReadU32(OffBinOffsets + (index * 4));
            return (begin, end);
        }

        throw new InvalidOperationException("Bin out of bounds");
    }

    /// <summary>
    /// Sets the edge bin offsets. Faithful port of <c>set_edge_bin_offsets</c>: copies the supplied
    /// <see cref="BinCount"/>-length array into the header's bin offset table.
    /// </summary>
    public void SetEdgeBinOffsets(ReadOnlySpan<uint> offsets)
    {
        if (offsets.Length != BinCount)
        {
            throw new ArgumentException(
                $"Edge bin offsets must contain exactly {BinCount} entries.", nameof(offsets));
        }

        for (int i = 0; i < BinCount; i++)
        {
            WriteU32(OffBinOffsets + (i * 4), offsets[i]);
        }
    }
}
